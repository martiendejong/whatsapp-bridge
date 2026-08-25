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
    private readonly TaskIntakeForwarder _taskIntake;

    // One Dawa client per sessionId
    private readonly ConcurrentDictionary<string, WhatsAppClient> _clients = new();

    // In-memory message store: key = "{sessionId}:{remoteJid}", value = ordered messages (newest last)
    private readonly ConcurrentDictionary<string, List<WhatsAppMessage>> _messageStore = new();
    private const int MaxMessagesPerChat = 200;

    public WhatsAppBridgeService(
        IServiceScopeFactory scopeFactory,
        IConfiguration configuration,
        ILoggerFactory loggerFactory,
        ILogger<WhatsAppBridgeService> logger,
        TaskIntakeForwarder taskIntake)
    {
        _scopeFactory = scopeFactory;
        _configuration = configuration;
        _loggerFactory = loggerFactory;
        _logger = logger;
        _taskIntake = taskIntake;
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

        client.MessageReceived        += (_, msg) => StoreMessage(sessionId, msg);
        client.HistoryMessageReceived += (_, msg) => StoreMessage(sessionId, msg, isHistory: true);
        client.HistorySyncCompleted   += (_, n)   => { _logger.LogInformation("HistorySync: persisting {N} msgs for {Sid}", n, sessionId); PersistMessages(sessionId); };
        client.MessageStatusUpdated   += (_, evt) => UpdateMessageStatus(sessionId, evt.Jid, evt.MessageId, evt.Status);

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

        client.MessageReceived        += (_, msg) => StoreMessage(sessionId, msg);
        client.HistoryMessageReceived += (_, msg) => StoreMessage(sessionId, msg, isHistory: true);
        client.HistorySyncCompleted   += (_, n)   => { _logger.LogInformation("HistorySync: persisting {N} msgs for {Sid}", n, sessionId); PersistMessages(sessionId); };
        client.MessageStatusUpdated   += (_, evt) => UpdateMessageStatus(sessionId, evt.Jid, evt.MessageId, evt.Status);

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
        (string messageId, string jid) sent;
        try
        {
            sent = await client.SendMessageAsync(to, body, CancellationToken.None);
        }
        catch (Exception ex) when (ex is not WhatsAppServiceException)
        {
            _logger.LogError(ex, "SendMessageAsync failed for session {SessionId} to {To}", sessionId, to);
            throw new WhatsAppServiceException(WhatsAppError.MessageFailed(ex.Message), ex);
        }

        var key = $"{sessionId}:{sent.jid}";
        var storedMessage = new WhatsAppMessage(
            Id: sent.messageId,
            From: "me",
            To: sent.jid,
            Body: body,
            Timestamp: DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            Status: Dawa.Messages.MessageStatus.Sent.ToString());
        var list = _messageStore.GetOrAdd(key, _ => new List<WhatsAppMessage>());
        lock (list)
        {
            list.Add(storedMessage);
            if (list.Count > MaxMessagesPerChat) list.RemoveAt(0);
        }

        // The send path bypasses the StoreMessage funnel, so mirror the outgoing message into
        // the durable Messages table here as well (task 869ecbkv7).
        PersistMessageToDatabase(sessionId, sent.jid, sent.messageId, fromMe: true,
            sender: "me", body: body, type: "text", mediaUrl: null, mediaKey: null, mimeType: null,
            timestamp: storedMessage.Timestamp, isHistory: false);

        return new { success = true, messageId = sent.messageId };
    }

    /// <summary>Applies a delivery/read/played status update to a previously-sent message in the store.</summary>
    private void UpdateMessageStatus(string sessionId, string jid, string messageId, Dawa.Messages.MessageStatus status)
    {
        var key = $"{sessionId}:{jid}";
        if (!_messageStore.TryGetValue(key, out var msgs)) return;
        lock (msgs)
        {
            var idx = msgs.FindIndex(m => m.Id == messageId);
            if (idx >= 0) msgs[idx] = msgs[idx] with { Status = status.ToString() };
        }
    }

    public async Task<object?> SendReplyAsync(string sessionId, string to, string body, string quotedMsgId, string quotedFromJid)
    {
        var client = GetConnectedClient(sessionId);
        (string messageId, string jid) sent;
        try
        {
            sent = await client.SendReplyAsync(to, body, quotedMsgId, quotedFromJid, CancellationToken.None);
        }
        catch (Exception ex) when (ex is not WhatsAppServiceException)
        {
            _logger.LogError(ex, "SendReplyAsync failed for session {SessionId} to {To}", sessionId, to);
            throw new WhatsAppServiceException(WhatsAppError.MessageFailed(ex.Message), ex);
        }

        var key = $"{sessionId}:{sent.jid}";
        var storedMessage = new WhatsAppMessage(
            Id: sent.messageId,
            From: "me",
            To: sent.jid,
            Body: body,
            Timestamp: DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            Status: Dawa.Messages.MessageStatus.Sent.ToString());
        var list = _messageStore.GetOrAdd(key, _ => new List<WhatsAppMessage>());
        lock (list)
        {
            list.Add(storedMessage);
            if (list.Count > MaxMessagesPerChat) list.RemoveAt(0);
        }

        // Mirror SendMessageAsync (task 869ecbkv7): the send path bypasses the StoreMessage
        // funnel, so persist the outgoing reply into the durable Messages table too.
        PersistMessageToDatabase(sessionId, sent.jid, sent.messageId, fromMe: true,
            sender: "me", body: body, type: "text", mediaUrl: null, mediaKey: null, mimeType: null,
            timestamp: storedMessage.Timestamp, isHistory: false);

        return new { success = true, messageId = sent.messageId };
    }

    public async Task<byte[]?> DownloadMediaAsync(string sessionId, string mediaUrl, string mediaKeyBase64, string mimeType)
    {
        try { return await WhatsAppClient.DownloadMediaAsync(mediaUrl, mediaKeyBase64, mimeType); }
        catch (Exception ex) { _logger.LogWarning(ex, "DownloadMedia failed for session {Sid}", sessionId); return null; }
    }

    public async Task<object?> RevokeMessageAsync(string sessionId, string jid, string messageId, bool fromMe, long timestamp)
    {
        var client = GetConnectedClient(sessionId);
        await client.RevokeMessageAsync(jid, messageId, fromMe, timestamp, CancellationToken.None);
        // Mark as revoked in store
        var key = $"{sessionId}:{jid}";
        if (_messageStore.TryGetValue(key, out var msgs))
        {
            lock (msgs)
            {
                var idx = msgs.FindIndex(m => m.Id == messageId);
                if (idx >= 0) msgs[idx] = msgs[idx] with { IsRevoked = true, Body = "" };
            }
        }
        return new { success = true };
    }

    public async Task<object?> ForwardMessageAsync(string sessionId, string toJid, string text)
    {
        var client = GetConnectedClient(sessionId);
        await client.ForwardMessageAsync(toJid, text, CancellationToken.None);
        return new { success = true };
    }

    public async Task<object?> SendTypingAsync(string sessionId, string jid, bool isTyping)
    {
        var client = GetConnectedClient(sessionId);
        await client.SendTypingAsync(jid, isTyping, CancellationToken.None);
        return new { success = true };
    }

    public async Task<object?> SendPresenceAsync(string sessionId, bool isOnline)
    {
        var client = GetConnectedClient(sessionId);
        await client.SendUserPresenceAsync(isOnline, CancellationToken.None);
        return new { success = true };
    }

    public async Task<object?> CreateGroupAsync(string sessionId, string subject, IEnumerable<string> jids)
    {
        var client = GetConnectedClient(sessionId);
        var groupJid = await client.CreateGroupAsync(subject, jids, CancellationToken.None);
        return new { success = groupJid != null, groupJid };
    }

    public async Task<object?> LeaveGroupAsync(string sessionId, string groupJid)
    {
        var client = GetConnectedClient(sessionId);
        await client.LeaveGroupAsync(groupJid, CancellationToken.None);
        return new { success = true };
    }

    public async Task<object?> AddGroupParticipantsAsync(string sessionId, string groupJid, IEnumerable<string> jids)
    {
        var client = GetConnectedClient(sessionId);
        var results = await client.AddGroupParticipantsAsync(groupJid, jids, CancellationToken.None);
        return new { success = true, results };
    }

    public async Task<object?> RemoveGroupParticipantsAsync(string sessionId, string groupJid, IEnumerable<string> jids)
    {
        var client = GetConnectedClient(sessionId);
        var results = await client.RemoveGroupParticipantsAsync(groupJid, jids, CancellationToken.None);
        return new { success = true, results };
    }

    public async Task<object?> GetGroupInviteLinkAsync(string sessionId, string groupJid)
    {
        var client = GetConnectedClient(sessionId);
        var link = await client.GetGroupInviteLinkAsync(groupJid, CancellationToken.None);
        return new { inviteLink = link };
    }

    public async Task<object?> UpdateGroupSubjectAsync(string sessionId, string groupJid, string subject)
    {
        var client = GetConnectedClient(sessionId);
        await client.UpdateGroupSubjectAsync(groupJid, subject, CancellationToken.None);
        return new { success = true };
    }

    public async Task<object?> SendReactionAsync(string sessionId, string to, string messageId, bool fromMe, string emoji)
    {
        var client = GetConnectedClient(sessionId);
        try
        {
            await client.SendReactionAsync(to, messageId, fromMe, emoji, CancellationToken.None);
        }
        catch (Exception ex) when (ex is not WhatsAppServiceException)
        {
            _logger.LogError(ex, "SendReactionAsync failed for session {SessionId} to {To}", sessionId, to);
            throw new WhatsAppServiceException(WhatsAppError.MessageFailed(ex.Message), ex);
        }
        return new { success = true };
    }

    public async Task<object?> SendMediaAsync(string sessionId, string to, string mediaType,
        string mimeType, byte[] fileBytes, string caption, string fileName)
    {
        var client = GetConnectedClient(sessionId);
        try
        {
            await client.SendMediaAsync(to, fileBytes, mediaType, mimeType, caption, fileName);
        }
        catch (Exception ex) when (ex is not WhatsAppServiceException)
        {
            _logger.LogError(ex, "SendMediaAsync failed for session {SessionId} to {To}", sessionId, to);
            throw new WhatsAppServiceException(WhatsAppError.MessageFailed(ex.Message), ex);
        }
        return new { success = true };
    }

    // ─── Read operations (not yet implemented in Dawa) ────────────────────────

    public async Task<List<WhatsAppMessage>> FetchAndStoreChatHistoryAsync(string sessionId, string chatId, int count)
    {
        var jid = chatId.Contains('@') ? chatId : $"{chatId}@s.whatsapp.net";
        if (!_clients.TryGetValue(sessionId, out var client) || !client.IsConnected)
            return new List<WhatsAppMessage>();

        // Fire the peerDataOperationRequestMessage (ON_DEMAND history sync request).
        // The phone will push a HISTORY_SYNC_NOTIFICATION asynchronously — could be seconds or minutes.
        // We don't block on it here; our session-level HistorySyncCompleted handler persists it when it arrives.
        _ = Task.Run(async () =>
        {
            try { await client.RequestOnDemandHistorySyncAsync(jid, count, CancellationToken.None); }
            catch (Exception ex) { _logger.LogWarning(ex, "OnDemand history sync fire-and-forget error for {Jid}", jid); }
        });

        // Return whatever we already have in the store (may be empty if this is the first request)
        var key = $"{sessionId}:{jid}";
        if (_messageStore.TryGetValue(key, out var stored))
        {
            lock (stored) { return stored.ToList(); }
        }
        return new List<WhatsAppMessage>();
    }

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

    public async Task RequestOnDemandHistoryAsync(string sessionId, string chatJid, int count, bool noAnchor = false)
    {
        var client = GetConnectedClient(sessionId);

        // Normalize JID
        var jid = chatJid.Contains('@') ? chatJid : $"{chatJid}@s.whatsapp.net";
        var key = $"{sessionId}:{jid}";

        // Find the oldest stored message for this chat so the phone knows where to continue from
        // ("give me messages older than this one"). Without this anchor the request behaves like
        // a fresh sync of the newest `count` messages instead of a backfill — the bug behind
        // "requestHistory returned success but delivered nothing" (task 869ecy6kp).
        string? oldestMsgId     = null;
        bool    oldestFromMe    = false;
        long    oldestTimestamp = 0;

        if (!noAnchor)
        {
            // Prefer the durable SQLite store (survives restarts/re-pairs and spans every
            // SessionId the user has ever paired) over the in-memory cache, which is empty
            // right after an app-pool restart even though history already exists in SQLite.
            var dbOldest = await FindOldestStoredMessageAsync(sessionId, jid);
            if (dbOldest != null)
            {
                oldestMsgId     = dbOldest.Value.MessageId;
                oldestFromMe    = dbOldest.Value.FromMe;
                oldestTimestamp = dbOldest.Value.Timestamp * 1000L; // convert seconds → milliseconds
            }
            else if (_messageStore.TryGetValue(key, out var msgs))
            {
                lock (msgs)
                {
                    var oldest = msgs.MinBy(m => m.Timestamp);
                    if (oldest != null)
                    {
                        oldestMsgId     = oldest.Id;
                        oldestFromMe    = oldest.From == "me";
                        oldestTimestamp = oldest.Timestamp * 1000L;
                    }
                }
            }
        }

        _logger.LogInformation(
            "RequestOnDemandHistoryAsync: chat={Chat} count={Count} anchor={Anchor}",
            jid, count, oldestMsgId ?? "(none)");

        await client.RequestOnDemandHistorySyncAsync(jid, count, CancellationToken.None,
            oldestMsgId, oldestFromMe, oldestTimestamp);
    }

    /// <summary>
    /// Finds the oldest durably-stored message for a chat, across every SessionId belonging to
    /// the same user as <paramref name="sessionId"/> — a re-pair gets a new SessionId, and the
    /// backfill anchor must not reset just because the user re-scanned the QR code.
    /// </summary>
    private async Task<(string MessageId, bool FromMe, long Timestamp)?> FindOldestStoredMessageAsync(string sessionId, string chatJid)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            var userId = await db.WhatsAppSessions
                .Where(s => s.SessionId == sessionId)
                .Select(s => (int?)s.UserId)
                .FirstOrDefaultAsync();
            if (userId == null) return null;

            var sessionIds = await db.WhatsAppSessions
                .Where(s => s.UserId == userId.Value)
                .Select(s => s.SessionId)
                .ToListAsync();

            var oldest = await db.Messages.AsNoTracking()
                .Where(m => sessionIds.Contains(m.SessionId) && m.ChatJid == chatJid)
                .OrderBy(m => m.Timestamp)
                .FirstOrDefaultAsync();

            return oldest == null ? null : (oldest.MessageId, oldest.FromMe, oldest.Timestamp);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "FindOldestStoredMessageAsync failed for chat {Chat} — falling back to in-memory anchor", chatJid);
            return null;
        }
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

    public Dawa.Messages.MessageStatus? GetMessageStatus(string sessionId, string messageId)
    {
        if (_clients.TryGetValue(sessionId, out var client))
        {
            var live = client.GetMessageStatus(messageId);
            if (live != null) return live;
        }

        // Fall back to the persisted message store — covers messages whose status
        // was recorded by a prior client instance (e.g. after a reconnect).
        var prefix = $"{sessionId}:";
        foreach (var kv in _messageStore)
        {
            if (!kv.Key.StartsWith(prefix, StringComparison.Ordinal)) continue;
            WhatsAppMessage? found;
            lock (kv.Value) { found = kv.Value.FirstOrDefault(m => m.Id == messageId); }
            if (found?.Status != null && Enum.TryParse<Dawa.Messages.MessageStatus>(found.Status, out var parsed))
                return parsed;
        }
        return null;
    }

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

    private void StoreMessage(string sessionId, Dawa.Messages.IncomingMessage msg, bool isHistory = false)
    {
        var key = $"{sessionId}:{msg.RemoteJid}";
        var stored = new WhatsAppMessage(
            Id: msg.Id,
            From: msg.FromMe ? "me" : msg.From,
            To: msg.RemoteJid,
            Body: msg.Text ?? "",
            Timestamp: msg.Timestamp,
            Type: msg.Type.ToString().ToLowerInvariant(),
            MediaUrl: msg.MediaUrl,
            MimeType: msg.MimeType,
            FileName: msg.FileName,
            FileSize: msg.FileSize,
            Duration: msg.Duration,
            Width: msg.Width,
            Height: msg.Height,
            MediaKey: msg.MediaKey,
            MediaSha256Enc: msg.MediaSha256Enc,
            ReactionEmoji: msg.ReactionEmoji,
            ReactionTargetId: msg.ReactionTargetId,
            IsRevoked: msg.IsRevoked,
            QuotedMessageId: msg.QuotedMessageId,
            QuotedFrom: msg.QuotedFrom,
            QuotedText: msg.QuotedText,
            QuotedType: msg.QuotedType == Dawa.Messages.MessageType.Unknown ? null : msg.QuotedType.ToString().ToLowerInvariant());

        var list = _messageStore.GetOrAdd(key, _ => new List<WhatsAppMessage>());
        lock (list)
        {
            // Deduplicate by message ID
            if (list.Any(m => m.Id == stored.Id)) return;

            // Task-intake forwarding: only genuine, newly-received inbound messages
            // (not history replays, not our own outgoing messages). Fire-and-forget so
            // a slow/failed intake call can never block or crash the inbound pipeline.
            if (!isHistory && !msg.FromMe && _taskIntake.IsEnabled)
                DispatchTaskIntake(sessionId, msg);

            if (isHistory)
            {
                // Insert in chronological order (history messages may arrive out of order)
                var idx = list.BinarySearch(stored, Comparer<WhatsAppMessage>.Create((a, b) => a.Timestamp.CompareTo(b.Timestamp)));
                list.Insert(idx < 0 ? ~idx : idx, stored);
            }
            else
            {
                list.Add(stored);
            }

            // Cap total messages per chat (keep most recent)
            if (list.Count > MaxMessagesPerChat)
                list.RemoveAt(0);
        }

        if (isHistory)
            _logger.LogDebug("History message [{Id}] from {From} in chat {Chat} @ {Ts}", msg.Id, msg.From, msg.RemoteJid, msg.Timestamp);
        else
            _logger.LogInformation("Stored message [{Id}] from {From} in chat {Chat}", msg.Id, msg.From, msg.RemoteJid);

        // Persist to disk (throttle for history: only persist in batch via caller)
        if (!isHistory) PersistMessages(sessionId);

        // Durable copy in SQLite (task 869ecbkv7): the in-memory store is capped and starts
        // empty after a restart or re-pair, so everything that decrypts is also appended to
        // the Messages table. Fire-and-forget with a full guard — a slow or failing DB write
        // must never block or crash the inbound pipeline.
        PersistMessageToDatabase(sessionId, msg.RemoteJid, msg.Id, msg.FromMe,
            msg.FromMe ? "me" : msg.From, msg.Text ?? "", msg.Type.ToString().ToLowerInvariant(),
            msg.MediaUrl, msg.MediaKey, msg.MimeType, msg.Timestamp, isHistory);
    }

    /// <summary>
    /// Appends one message to the durable SQLite Messages table. INSERT OR IGNORE against the
    /// unique (SessionId, MessageId) index makes replays and restarts idempotent. Never throws.
    /// MediaKey/MimeType (task 869ecw8du) let the store/messages/media endpoint decrypt the
    /// CDN blob later — WhatsApp media URLs are useless without the per-message key.
    /// </summary>
    private void PersistMessageToDatabase(string sessionId, string chatJid, string messageId,
        bool fromMe, string sender, string body, string type, string? mediaUrl, string? mediaKey,
        string? mimeType, long timestamp, bool isHistory)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                // Own the row by USER, not just by session GUID: a QR re-pair REPLACES the
                // WhatsAppSessions row (new SessionId), which orphaned every pre-re-pair
                // message for session-scoped queries (2026-08-03). UserId is stable across
                // re-pairs, so readers filter on it instead.
                var userId = await db.WhatsAppSessions
                    .Where(s => s.SessionId == sessionId)
                    .Select(s => (int?)s.UserId)
                    .FirstOrDefaultAsync();
                var receivedAt = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss.fffffff");
                await db.Database.ExecuteSqlInterpolatedAsync($@"
                    INSERT OR IGNORE INTO Messages
                        (SessionId, UserId, ChatJid, MessageId, FromMe, Sender, Body, Type, MediaUrl, MediaKey, MimeType, Timestamp, ReceivedAt, IsHistory)
                    VALUES
                        ({sessionId}, {userId}, {chatJid}, {messageId}, {(fromMe ? 1 : 0)}, {sender}, {body}, {type},
                         {mediaUrl}, {mediaKey}, {mimeType}, {timestamp}, {receivedAt}, {(isHistory ? 1 : 0)})");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Durable message persist failed for [{MessageId}] in {Chat} (message is still in the in-memory store)", messageId, chatJid);
            }
        });
    }

    /// <summary>
    /// Fire-and-forget dispatch of an inbound message to the task-intake forwarder.
    /// Reply is routed back through the existing SendMessageAsync send path (to the sender's
    /// chat). Any error is swallowed here — this must never affect the inbound pipeline.
    /// </summary>
    private void DispatchTaskIntake(string sessionId, Dawa.Messages.IncomingMessage msg)
    {
        // Reply target: the conversation JID the message arrived on.
        var replyTo = msg.RemoteJid;

        Func<string, string, Task> reply = async (to, body) =>
        {
            try { await SendMessageAsync(sessionId, to, body); }
            catch (Exception ex) { _logger.LogWarning(ex, "TaskIntake reply send failed for session {Sid}", sessionId); }
        };

        _ = Task.Run(async () =>
        {
            try
            {
                await _taskIntake.HandleInboundAsync(msg.From, msg.Text, msg.Id, (_, body) => reply(replyTo, body));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "TaskIntake dispatch error for session {Sid}", sessionId);
            }
        });
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

    public bool TryGetClient(string sessionId, out WhatsAppClient? client)
        => _clients.TryGetValue(sessionId, out client);

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
