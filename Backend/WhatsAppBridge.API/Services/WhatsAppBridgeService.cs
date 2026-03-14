using Dawa;
using Microsoft.EntityFrameworkCore;
using System.Collections.Concurrent;
using System.Text.Json;
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

    // Contact store: key = "{sessionId}:{jid}", value = contact info
    private readonly ConcurrentDictionary<string, ContactEntry> _contactStore = new();

    private record ContactEntry(string Jid, string Name, string PhoneNumber, long LastSeen);

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
        LoadContacts(sessionId);

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

        client.QRCodeReceived += (_, qr) =>
            _ = UpdateSessionAsync(sessionId, s => s.QrCode = qr);

        client.MessageReceived += (_, msg) => StoreMessage(sessionId, msg);
        LoadContacts(sessionId);

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

        // Track the recipient as a known contact
        var jid = to.Contains('@') ? to : $"{to.TrimStart('+')}@s.whatsapp.net";
        var phone = jid.Split('@')[0].Split(':')[0];
        TrackContact(sessionId, jid, phone, phone, DateTimeOffset.UtcNow.ToUnixTimeSeconds());

        return new { success = true };
    }

    public Task<object?> SendMediaAsync(string sessionId, string to, string mediaUrl, string? caption)
    {
        // Media sending not yet implemented in Dawa
        throw new WhatsAppServiceException(WhatsAppError.MessageFailed(
            "Media sending is not yet supported by the Dawa client."));
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
            var slice = msgs.TakeLast(limit).ToList();
            return Task.FromResult<List<WhatsAppMessage>?>(slice);
        }
    }

    public Task<List<object>?> GetChatsAsync(string sessionId)
        => Task.FromResult<List<object>?>(new List<object>());

    public Task<List<WhatsAppContact>?> GetContactsAsync(string sessionId)
    {
        var contacts = _contactStore
            .Where(kv => kv.Key.StartsWith($"{sessionId}:"))
            .Select(kv => kv.Value)
            .Where(c => !c.Jid.EndsWith("@g.us")) // skip groups
            .OrderByDescending(c => c.LastSeen)
            .Select(c => new WhatsAppContact(c.Jid, c.Name, c.PhoneNumber))
            .ToList();
        return Task.FromResult<List<WhatsAppContact>?>(contacts);
    }

    public Task<object?> CheckNumberStatusAsync(string sessionId, string number)
        => Task.FromResult<object?>(new { number, isWhatsApp = true });

    // ─── Helpers ──────────────────────────────────────────────────────────────

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
            list.Add(stored);
            if (list.Count > MaxMessagesPerChat)
                list.RemoveAt(0); // drop oldest
        }

        // Track sender as a known contact (using push name if available)
        if (!msg.FromMe && msg.RemoteJid.EndsWith("@s.whatsapp.net"))
        {
            var phone = msg.RemoteJid.Split('@')[0].Split(':')[0];
            var name = !string.IsNullOrEmpty(msg.PushName) ? msg.PushName : phone;
            TrackContact(sessionId, msg.RemoteJid, name, phone, msg.Timestamp);
        }

        _logger.LogInformation("Stored message [{Id}] from {From} in chat {Chat}", msg.Id, msg.From, msg.RemoteJid);
    }

    private void TrackContact(string sessionId, string jid, string name, string phone, long lastSeen)
    {
        var storeKey = $"{sessionId}:{jid}";
        _contactStore.AddOrUpdate(storeKey,
            _ => new ContactEntry(jid, name, phone, lastSeen),
            (_, existing) => existing with
            {
                Name = !string.IsNullOrEmpty(name) && name != phone ? name : existing.Name,
                LastSeen = Math.Max(existing.LastSeen, lastSeen),
            });
        _ = SaveContactsAsync(sessionId);
    }

    private async Task SaveContactsAsync(string sessionId)
    {
        try
        {
            var sessionsRoot = _configuration["WhatsApp:SessionsDirectory"]
                ?? Path.Combine(AppContext.BaseDirectory, "whatsapp-sessions");
            var path = Path.Combine(sessionsRoot, sessionId, "contacts.json");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);

            var contacts = _contactStore
                .Where(kv => kv.Key.StartsWith($"{sessionId}:"))
                .Select(kv => kv.Value)
                .ToList();

            var json = JsonSerializer.Serialize(contacts, new JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(path, json);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to save contacts for session {SessionId}", sessionId);
        }
    }

    private void LoadContacts(string sessionId)
    {
        try
        {
            var sessionsRoot = _configuration["WhatsApp:SessionsDirectory"]
                ?? Path.Combine(AppContext.BaseDirectory, "whatsapp-sessions");
            var path = Path.Combine(sessionsRoot, sessionId, "contacts.json");
            if (!File.Exists(path)) return;

            var json = File.ReadAllText(path);
            var contacts = JsonSerializer.Deserialize<List<ContactEntry>>(json);
            if (contacts == null) return;

            foreach (var c in contacts)
                _contactStore[$"{sessionId}:{c.Jid}"] = c;

            _logger.LogInformation("Loaded {Count} contacts for session {SessionId}", contacts.Count, sessionId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load contacts for session {SessionId}", sessionId);
        }
    }

    public bool IsSessionConnected(string sessionId)
        => _clients.TryGetValue(sessionId, out var client) && client.IsConnected;

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
