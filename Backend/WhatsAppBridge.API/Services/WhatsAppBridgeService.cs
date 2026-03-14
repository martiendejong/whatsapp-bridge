using Dawa;
using Microsoft.EntityFrameworkCore;
using System.Collections.Concurrent;
using WhatsAppBridge.API.Controllers;
using WhatsAppBridge.API.Data;
using WhatsAppBridge.API.Models;

namespace WhatsAppBridge.API.Services;

/// <summary>
/// Manages per-user WhatsApp sessions using Dawa (C# native client).
/// Replaces the former Node.js/Baileys HTTP bridge.
/// Registered as Singleton — holds long-lived WhatsAppClient instances.
/// </summary>
public class WhatsAppBridgeService : IAsyncDisposable
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IConfiguration _configuration;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<WhatsAppBridgeService> _logger;

    // One Dawa client per sessionId
    private readonly ConcurrentDictionary<string, WhatsAppClient> _clients = new();

    // In-memory message store: key = "{sessionId}:{remoteJid}", value = ordered messages (newest last)
    private readonly ConcurrentDictionary<string, List<WhatsAppMessage>> _messageStore = new();
    private const int MaxMessagesPerChat = 200;

    public WhatsAppBridgeService(
        IServiceScopeFactory scopeFactory,
        IConfiguration configuration,
        ILoggerFactory loggerFactory,
        ILogger<WhatsAppBridgeService> logger)
    {
        _scopeFactory = scopeFactory;
        _configuration = configuration;
        _loggerFactory = loggerFactory;
        _logger = logger;
    }

    // ─── Session lifecycle ─────────────────────────────────────────────────────

    /// <summary>
    /// Creates a Dawa client for the session and waits up to 30s for a QR code.
    /// Returns the QR string on success, null if it times out (QR will arrive later via event).
    /// </summary>
    public async Task<string?> InitializeSessionAsync(string sessionId)
    {
        // Clean up any existing client for this session
        if (_clients.TryRemove(sessionId, out var existing))
            await existing.DisposeAsync();

        var sessionsRoot = _configuration["WhatsApp:SessionsDirectory"]
            ?? Path.Combine(AppContext.BaseDirectory, "whatsapp-sessions");
        var sessionDir = Path.Combine(sessionsRoot, sessionId);

        var client = WhatsAppClient.Create(sessionDir, _loggerFactory);
        _clients[sessionId] = client;

        // Load persisted messages from previous sessions
        LoadPersistedMessages(sessionId);

        // Wire events — these fire from background threads
        var qrTcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var everConnected = false;

        client.QRCodeReceived += (_, qr) =>
        {
            _logger.LogInformation("QR received for session {SessionId}", sessionId);
            qrTcs.TrySetResult(qr);
            _ = UpdateSessionAsync(sessionId, s => s.QrCode = qr);
        };

        client.MessageReceived += (_, msg) => StoreMessage(sessionId, msg);

        client.Connected += (_, _) =>
        {
            everConnected = true;
            _logger.LogInformation("Session {SessionId} connected as {Jid}", sessionId, client.MyJid);
            _ = UpdateSessionAsync(sessionId, s =>
            {
                s.Status = "connected";
                s.ConnectedAt = DateTime.UtcNow;
                s.QrCode = null; // QR no longer needed
                // Extract phone number from JID: "31633984381:20@s.whatsapp.net" → "31633984381"
                if (client.MyJid != null)
                {
                    var phone = client.MyJid.Split('@')[0].Split(':')[0];
                    s.PhoneNumber = phone;
                }
            });
        };

        client.Disconnected += (_, _) =>
        {
            // If we never reached "connected", the server rejected us (e.g. rate limiting, bad payload).
            // Mark as "failed" so the frontend stops polling instead of hanging on "qr_pending".
            var status = everConnected ? "disconnected" : "failed";
            _logger.LogInformation("Session {SessionId} disconnected (status → {Status})", sessionId, status);
            qrTcs.TrySetException(new Exception($"Connection rejected by server (status: {status})"));
            _ = UpdateSessionAsync(sessionId, s =>
            {
                s.Status = status;
                s.LastSeenAt = DateTime.UtcNow;
            });
        };

        // Start connecting in background
        _ = client.ConnectAsync(CancellationToken.None);

        // If session is already saved, client connects directly (no QR).
        // If not, QR fires within ~2s. If server rejects (rate limit, bad payload),
        // qrTcs completes with exception so we don't hang for 30s.
        var winner = await Task.WhenAny(qrTcs.Task, Task.Delay(TimeSpan.FromSeconds(30)));

        if (winner == qrTcs.Task)
        {
            try { return await qrTcs.Task; }
            catch { return null; } // Server rejected — status already updated to "failed" via Disconnected event
        }
        return null; // Timed out waiting for QR
    }

    /// <summary>
    /// Silently restores a saved session on startup without waiting for QR.
    /// Only works if creds.json exists in the session directory.
    /// </summary>
    public Task RestoreSessionAsync(string sessionId)
    {
        var sessionsRoot = _configuration["WhatsApp:SessionsDirectory"]
            ?? Path.Combine(AppContext.BaseDirectory, "whatsapp-sessions");
        var sessionDir = Path.Combine(sessionsRoot, sessionId);

        if (!Directory.Exists(sessionDir))
            return Task.CompletedTask;

        if (_clients.ContainsKey(sessionId))
            return Task.CompletedTask;

        var client = WhatsAppClient.Create(sessionDir, _loggerFactory);
        _clients[sessionId] = client;

        // Load persisted messages from previous sessions
        LoadPersistedMessages(sessionId);

        client.QRCodeReceived += (_, qr) =>
            _ = UpdateSessionAsync(sessionId, s => s.QrCode = qr);

        client.MessageReceived += (_, msg) => StoreMessage(sessionId, msg);

        client.Connected += (_, _) =>
        {
            _logger.LogInformation("Session {SessionId} restored and connected as {Jid}", sessionId, client.MyJid);
            _ = UpdateSessionAsync(sessionId, s =>
            {
                s.Status = "connected";
                s.LastSeenAt = DateTime.UtcNow;
                if (client.MyJid != null && string.IsNullOrEmpty(s.PhoneNumber))
                {
                    s.PhoneNumber = client.MyJid.Split('@')[0].Split(':')[0];
                }
            });
        };

        client.Disconnected += (_, _) =>
            _ = UpdateSessionAsync(sessionId, s =>
            {
                s.Status = "disconnected";
                s.LastSeenAt = DateTime.UtcNow;
            });

        // Fire and forget — reconnects in background
        _ = client.ConnectAsync(CancellationToken.None);
        return Task.CompletedTask;
    }

    public async Task<bool> DisconnectSessionAsync(string sessionId)
    {
        if (_clients.TryRemove(sessionId, out var client))
        {
            await client.DisposeAsync();
            return true;
        }
        return false;
    }

    // ─── Messaging ────────────────────────────────────────────────────────────

    public async Task<object?> SendMessageAsync(string sessionId, string to, string body)
    {
        var client = GetConnectedClient(sessionId);
        await client.SendMessageAsync(to, body, CancellationToken.None);
        return new { success = true };
    }

    public async Task<object?> SendReactionAsync(string sessionId, string to, string messageId, bool fromMe, string emoji)
    {
        var client = GetConnectedClient(sessionId);
        await client.SendReactionAsync(to, messageId, fromMe, emoji, CancellationToken.None);
        return new { success = true };
    }

    public async Task<object?> SendMediaAsync(string sessionId, string to, string mediaType,
        string mimeType, byte[] fileBytes, string caption, string fileName)
    {
        var client = GetConnectedClient(sessionId);
        await client.SendMediaAsync(to, fileBytes, mediaType, mimeType, caption, fileName);
        return new { success = true };
    }

    // ─── Read operations (not yet implemented in Dawa) ────────────────────────

    public Task<List<WhatsAppMessage>?> GetMessagesAsync(string sessionId, string chatId, int limit)
    {
        // Normalize chatId — accept bare number or full JID
        var jid = chatId.Contains('@') ? chatId : $"{chatId}@s.whatsapp.net";
        var key = $"{sessionId}:{jid}";

        if (!_messageStore.TryGetValue(key, out var msgs))
            return Task.FromResult<List<WhatsAppMessage>?>(new List<WhatsAppMessage>());

        lock (msgs)
        {
            return Task.FromResult<List<WhatsAppMessage>?>(msgs.TakeLast(limit).ToList());
        }
    }

    public async Task<List<object>?> GetChatsAsync(string sessionId)
    {
        if (!_clients.TryGetValue(sessionId, out var client) || !client.IsConnected)
            return null;
        var chats = await client.GetChatsAsync(CancellationToken.None);
        return chats.Select(c => (object)new {
            jid      = c.Jid,
            name     = c.Name,
            phone    = c.Jid.Split('@')[0].Split(':')[0],
            archived = c.Archived,
            pinned   = c.Pinned,
        }).ToList();
    }

    public async Task<string?> GetProfilePictureAsync(string sessionId, string jid)
    {
        if (!_clients.TryGetValue(sessionId, out var client) || !client.IsConnected)
            return null;
        return await client.GetProfilePictureAsync(jid, CancellationToken.None);
    }

    public async Task SubscribePresenceAsync(string sessionId, string jid)
    {
        if (_clients.TryGetValue(sessionId, out var client) && client.IsConnected)
            await client.SubscribePresenceAsync(jid, CancellationToken.None);
    }

    public object? GetPresence(string sessionId, string jid)
    {
        if (!_clients.TryGetValue(sessionId, out var client) || !client.IsConnected)
            return null;
        var p = client.GetPresence(jid);
        if (p == null) return new { jid, status = "unknown" };
        return new { jid = p.Jid, status = p.Status, lastSeen = p.LastSeen };
    }

    public async Task SendReadReceiptAsync(string sessionId, string jid, string messageId, long timestamp)
    {
        var client = GetConnectedClient(sessionId);
        await client.SendReadReceiptAsync(jid, messageId, timestamp, CancellationToken.None);
    }

    public async Task<string?> ResolveLidAsync(string sessionId, string lidJid)
    {
        if (!_clients.TryGetValue(sessionId, out var client) || !client.IsConnected)
            return null;
        return await client.ResolveLidAsync(lidJid, CancellationToken.None);
    }

    public async Task<List<object>?> FetchMessageHistoryAsync(string sessionId, string jid, int count)
    {
        if (!_clients.TryGetValue(sessionId, out var client) || !client.IsConnected)
            return null;
        var messages = await client.FetchMessageHistoryAsync(jid, count, CancellationToken.None);
        return messages.Select(m => (object)new
        {
            id        = m.Id,
            from      = m.From,
            remoteJid = m.RemoteJid,
            text      = m.Text,
            fromMe    = m.FromMe,
            timestamp = m.Timestamp,
            pushName  = m.PushName,
        }).ToList();
    }

    public async Task<List<WhatsAppContact>?> GetContactsAsync(string sessionId)
    {
        if (!_clients.TryGetValue(sessionId, out var client) || !client.IsConnected)
            return null;

        var contacts = await client.GetContactsAsync(CancellationToken.None);
        return contacts
            .Where(c => !c.Jid.EndsWith("@g.us"))
            .Select(c =>
            {
                var phone = c.Jid.Split('@')[0].Split(':')[0];
                return new WhatsAppContact(c.Jid, c.Name, phone);
            })
            .ToList();
    }

    public async Task<List<object>?> GetGroupsAsync(string sessionId)
    {
        if (!_clients.TryGetValue(sessionId, out var client) || !client.IsConnected)
            return null;

        var groupJids = client.GetGroupJids();
        var result = new List<object>();

        foreach (var gid in groupJids)
        {
            try
            {
                var meta = await client.FetchGroupMetadataAsync(gid, CancellationToken.None);
                if (meta != null)
                {
                    result.Add(new
                    {
                        jid          = meta.Jid,
                        subject      = meta.Subject,
                        creator      = meta.Creator,
                        createdAt    = meta.CreationTimestamp,
                        memberCount  = meta.Participants.Count,
                    });
                }
                else
                {
                    result.Add(new { jid = gid, subject = (string?)null, error = "metadata_unavailable" });
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "GetGroupsAsync: failed to fetch metadata for {Gid}", gid);
                result.Add(new { jid = gid, subject = (string?)null, error = ex.Message });
            }
        }

        return result;
    }

    public async Task<object?> GetGroupMembersAsync(string sessionId, string groupJid)
    {
        if (!_clients.TryGetValue(sessionId, out var client) || !client.IsConnected)
            return null;

        var meta = await client.FetchGroupMetadataAsync(groupJid, CancellationToken.None);
        if (meta == null) return null;

        return new
        {
            jid         = meta.Jid,
            subject     = meta.Subject,
            creator     = meta.Creator,
            createdAt   = meta.CreationTimestamp,
            memberCount = meta.Participants.Count,
            members     = meta.Participants.Select(p => new
            {
                jid    = p.Jid,
                lidJid = string.IsNullOrEmpty(p.LidJid) ? null : p.LidJid,
                type   = p.Type,
            }).ToList(),
        };
    }

    public Task<object?> CheckNumberStatusAsync(string sessionId, string number)
        => Task.FromResult<object?>(new { number, isWhatsApp = true });

    // ─── Helpers ──────────────────────────────────────────────────────────────

    private string GetMessageStorePath(string sessionId)
    {
        var sessionsRoot = _configuration["WhatsApp:SessionsDirectory"]
            ?? Path.Combine(AppContext.BaseDirectory, "whatsapp-sessions");
        return Path.Combine(sessionsRoot, sessionId, "message-store.json");
    }

    private void LoadPersistedMessages(string sessionId)
    {
        try
        {
            var path = GetMessageStorePath(sessionId);
            if (!File.Exists(path)) return;
            var json = File.ReadAllText(path);
            var dict = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, List<WhatsAppMessage>>>(json);
            if (dict == null) return;
            foreach (var kv in dict)
                _messageStore[$"{sessionId}:{kv.Key}"] = kv.Value;
            _logger.LogInformation("Loaded {Count} chats from message store for session {SessionId}",
                dict.Count, sessionId);
        }
        catch (Exception ex) { _logger.LogWarning(ex, "Failed to load message store for {SessionId}", sessionId); }
    }

    private void PersistMessages(string sessionId)
    {
        try
        {
            var prefix = $"{sessionId}:";
            var dict = new Dictionary<string, List<WhatsAppMessage>>();
            foreach (var kv in _messageStore)
            {
                if (!kv.Key.StartsWith(prefix)) continue;
                var jid = kv.Key.Substring(prefix.Length);
                lock (kv.Value) { dict[jid] = kv.Value.ToList(); }
            }
            var path = GetMessageStorePath(sessionId);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(dict));
        }
        catch (Exception ex) { _logger.LogWarning(ex, "Failed to persist message store for {SessionId}", sessionId); }
    }

    private void StoreMessage(string sessionId, Dawa.Messages.IncomingMessage msg)
    {
        var key = $"{sessionId}:{msg.RemoteJid}";
        var stored = new WhatsAppMessage(
            Id: msg.Id,
            From: msg.FromMe ? "me" : msg.From,
            To: msg.RemoteJid,
            Body: msg.Text ?? "",
            Timestamp: msg.Timestamp);

        var list = _messageStore.GetOrAdd(key, _ => new List<WhatsAppMessage>());
        lock (list)
        {
            // Deduplicate by message ID
            if (list.Any(m => m.Id == stored.Id)) return;
            list.Add(stored);
            if (list.Count > MaxMessagesPerChat)
                list.RemoveAt(0); // drop oldest
        }

        _logger.LogInformation("Stored message [{Id}] from {From} in chat {Chat}", msg.Id, msg.From, msg.RemoteJid);

        // Persist to disk after each new message (bounded store → small file)
        PersistMessages(sessionId);
    }

    public bool IsSessionConnected(string sessionId)
        => _clients.TryGetValue(sessionId, out var client) && client.IsConnected;

    public object GetSessionDebugInfo(string sessionId)
    {
        if (!_clients.TryGetValue(sessionId, out var client))
            return new { error = "session not in _clients" };
        return new
        {
            isConnected = client.IsConnected,
            myJid = client.MyJid,
            cacheDebugInfo = client.GetCacheDebugInfo(),
        };
    }

    private WhatsAppClient GetConnectedClient(string sessionId)
    {
        if (!_clients.TryGetValue(sessionId, out var client))
            throw new WhatsAppServiceException(WhatsAppError.SessionNotFound(sessionId));
        if (!client.IsConnected)
            throw new WhatsAppServiceException(WhatsAppError.SessionDisconnected(sessionId));
        return client;
    }

    private async Task UpdateSessionAsync(string sessionId, Action<WhatsAppSession> update)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var session = await db.WhatsAppSessions
                .FirstOrDefaultAsync(s => s.SessionId == sessionId);
            if (session != null)
            {
                update(session);
                await db.SaveChangesAsync();
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to update session {SessionId} in DB", sessionId);
        }
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var client in _clients.Values)
            await client.DisposeAsync();
        _clients.Clear();
    }
}
