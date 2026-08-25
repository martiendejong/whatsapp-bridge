using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using WhatsAppBridge.API.Data;
using WhatsAppBridge.API.Models;
using WhatsAppBridge.API.Services;

namespace WhatsAppBridge.API.Controllers;

/// <summary>
/// WhatsApp API endpoints - accessible via API token
/// Signature mirrors actual WhatsApp Web API
/// </summary>
[ApiController]
[Route("api/wa")]
public class WhatsAppApiController : ControllerBase
{
    private readonly AppDbContext _context;
    private readonly AuthService _authService;
    private readonly WhatsAppBridgeService _whatsappService;
    private readonly EncryptionService _encryptionService;
    private readonly OutboundGuardrailService _outboundGuardrail;

    public WhatsAppApiController(
        AppDbContext context,
        AuthService authService,
        WhatsAppBridgeService whatsappService,
        EncryptionService encryptionService,
        OutboundGuardrailService outboundGuardrail)
    {
        _context = context;
        _authService = authService;
        _whatsappService = whatsappService;
        _encryptionService = encryptionService;
        _outboundGuardrail = outboundGuardrail;
    }

    private async Task<(bool success, int? userId, string? error)> ValidateApiToken()
    {
        var authHeader = Request.Headers.Authorization.ToString();
        if (string.IsNullOrEmpty(authHeader) || !authHeader.StartsWith("Bearer "))
            return (false, null, "Missing or invalid authorization header");

        var token = authHeader.Substring("Bearer ".Length).Trim();

        // Decrypt token if encryption is enabled
        if (_encryptionService.IsEncryptionEnabled)
        {
            // Try to find connection by encrypted token
            var connection = await _context.ApiConnections
                .FirstOrDefaultAsync(c => c.Token == token && c.IsActive);

            if (connection == null)
            {
                // Try decrypting the provided token and searching
                try
                {
                    var encryptedToken = _encryptionService.Encrypt(token);
                    connection = await _context.ApiConnections
                        .FirstOrDefaultAsync(c => c.Token == encryptedToken && c.IsActive);
                }
                catch
                {
                    // Token is invalid
                }
            }

            if (connection != null)
            {
                connection.LastUsedAt = DateTime.UtcNow;
                await _context.SaveChangesAsync();
                return (true, connection.UserId, null);
            }
        }
        else
        {
            var user = await _authService.ValidateApiTokenAsync(token);
            if (user != null)
                return (true, user.Id, null);
        }

        return (false, null, "Invalid API token");
    }

    private async Task<string?> GetUserSessionId(int userId, string? sessionIdOrPhone = null)
    {
        var query = _context.WhatsAppSessions
            .Where(s => s.UserId == userId && s.Status == "connected");

        // If sessionId or phoneNumber specified, try to find that specific session
        if (!string.IsNullOrEmpty(sessionIdOrPhone))
        {
            // Try as session ID first
            var session = await query
                .FirstOrDefaultAsync(s => s.SessionId == sessionIdOrPhone);

            if (session != null)
                return session.SessionId;

            // Try as phone number (decrypt if needed)
            if (_encryptionService.IsEncryptionEnabled)
            {
                // Find session by encrypted phone number
                session = await query
                    .ToListAsync()
                    .ContinueWith(t => t.Result.FirstOrDefault(s =>
                        !string.IsNullOrEmpty(s.PhoneNumber) &&
                        _encryptionService.Decrypt(s.PhoneNumber) == sessionIdOrPhone
                    ));
            }
            else
            {
                session = await query
                    .FirstOrDefaultAsync(s => s.PhoneNumber == sessionIdOrPhone);
            }

            if (session != null)
                return session.SessionId;

            // Specific session requested but not found
            return null;
        }

        // No specific session - return first active session
        var defaultSession = await query
            .OrderByDescending(s => s.ConnectedAt)
            .FirstOrDefaultAsync();

        return defaultSession?.SessionId;
    }

    /// <summary>
    /// Send a text message
    /// POST /api/wa/sendMessage
    /// </summary>
    [HttpPost("sendMessage")]
    public async Task<IActionResult> SendMessage([FromBody] SendMessageRequest request)
    {
        try
        {
            var (success, userId, error) = await ValidateApiToken();
            if (!success)
                return Unauthorized(new { error });

            var (allowed, blockReason) = await _outboundGuardrail.CheckAsync("sendMessage", request.To, request.Body, userId);
            if (!allowed)
                return StatusCode(403, new { error = blockReason, blocked = true });

            var sessionId = await GetUserSessionId(userId!.Value, request.SessionId);
            if (sessionId == null)
                return BadRequest(new { error = request.SessionId != null
                    ? $"WhatsApp session '{request.SessionId}' not found or not connected"
                    : "No active WhatsApp session" });

            // Encrypt message if encryption enabled
            var messageToSend = _encryptionService.IsEncryptionEnabled
                ? _encryptionService.Encrypt(request.Body)
                : request.Body;

            var result = await _whatsappService.SendMessageAsync(sessionId, request.To, messageToSend);

            return Ok(result);
        }
        catch (WhatsAppServiceException ex)
        {
            // Return user-friendly error message
            return StatusCode(400, new
            {
                error = ex.Error.UserMessage,
                errorCode = ex.Error.ErrorCode,
                details = ex.Error.AdditionalInfo
            });
        }
    }

    /// <summary>
    /// Send a reply to a specific message, attaching quoted-message context so the
    /// recipient's client renders it as a reply.
    /// POST /api/wa/sendReply
    /// </summary>
    [HttpPost("sendReply")]
    public async Task<IActionResult> SendReply([FromBody] SendReplyRequest request)
    {
        try
        {
            var (success, userId, error) = await ValidateApiToken();
            if (!success)
                return Unauthorized(new { error });

            var sessionId = await GetUserSessionId(userId!.Value, request.SessionId);
            if (sessionId == null)
                return BadRequest(new { error = request.SessionId != null
                    ? $"WhatsApp session '{request.SessionId}' not found or not connected"
                    : "No active WhatsApp session" });

            // Encrypt message if encryption enabled
            var messageToSend = _encryptionService.IsEncryptionEnabled
                ? _encryptionService.Encrypt(request.Body)
                : request.Body;

            var result = await _whatsappService.SendReplyAsync(sessionId, request.To, messageToSend, request.QuotedMessageId, request.QuotedFromJid);

            return Ok(result);
        }
        catch (WhatsAppServiceException ex)
        {
            // Return user-friendly error message
            return StatusCode(400, new
            {
                error = ex.Error.UserMessage,
                errorCode = ex.Error.ErrorCode,
                details = ex.Error.AdditionalInfo
            });
        }
    }

    /// <summary>
    /// Request on-demand history sync: ask the phone to push older message history for a
    /// chat. This is WhatsApp Web's own history mechanism (a linked-device request to the
    /// phone) — NOT the Google Drive backup, which is not accessible to third parties.
    /// Fully fail-safe and OPTIONAL: any failure returns success=false and never affects the
    /// live connection, the automatic history sync, the offline queue, or message delivery.
    /// POST /api/wa/requestHistory  body: { "chatId": "2547...@s.whatsapp.net", "count": 50 }
    /// </summary>
    [HttpPost("requestHistory")]
    public async Task<IActionResult> RequestHistory([FromBody] RequestHistoryRequest request)
    {
        try
        {
            var (success, userId, error) = await ValidateApiToken();
            if (!success)
                return Unauthorized(new { error });

            if (request == null || string.IsNullOrWhiteSpace(request.ChatId))
                return BadRequest(new { success = false, error = "chatId is required" });

            var sessionId = await GetUserSessionId(userId!.Value, request.SessionId);
            if (sessionId == null)
                return Ok(new { success = false, error = "No active WhatsApp session — normal history sync/live delivery unaffected." });

            var count = request.Count is > 0 and <= 500 ? request.Count : 50;
            try
            {
                await _whatsappService.RequestOnDemandHistoryAsync(sessionId, request.ChatId, count, request.NoAnchor);
                return Ok(new { success = true, requested = count, chatId = request.ChatId });
            }
            catch (Exception)
            {
                // Best-effort: on-demand request failed, but the connection and every normal
                // delivery path keep working. Never surface this as a hard error.
                return Ok(new { success = false, error = "On-demand history request failed; normal history sync/live delivery unaffected." });
            }
        }
        catch (Exception)
        {
            // Absolute backstop: this optional endpoint must never break anything.
            return Ok(new { success = false, error = "Unexpected error; connection unaffected." });
        }
    }

    /// <summary>
    /// Send media (image, video, document)
    /// POST /api/wa/sendMedia
    /// </summary>
    [HttpPost("sendMedia")]
    public async Task<IActionResult> SendMedia([FromBody] SendMediaRequest request)
    {
        try
        {
            var (success, userId, error) = await ValidateApiToken();
            if (!success)
                return Unauthorized(new { error });

            var (allowed, blockReason) = await _outboundGuardrail.CheckAsync("sendMedia", request.To, request.Caption ?? "", userId);
            if (!allowed)
                return StatusCode(403, new { error = blockReason, blocked = true });

            var sessionId = await GetUserSessionId(userId!.Value, request.SessionId);
            if (sessionId == null)
                return BadRequest(new { error = request.SessionId != null
                    ? $"WhatsApp session '{request.SessionId}' not found or not connected"
                    : "No active WhatsApp session" });

            // Download media from URL
            using var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
            var response = await httpClient.GetAsync(request.MediaUrl);
            response.EnsureSuccessStatusCode();
            var fileBytes = await response.Content.ReadAsByteArrayAsync();
            var mimeType = response.Content.Headers.ContentType?.MediaType ?? "application/octet-stream";
            var mediaType = mimeType.StartsWith("image/") ? "image"
                          : mimeType.StartsWith("audio/") ? "audio"
                          : mimeType.StartsWith("video/") ? "video"
                          : "document";
            var uri = new Uri(request.MediaUrl);
            var fn = Path.GetFileName(uri.LocalPath);
            var result = await _whatsappService.SendMediaAsync(
                sessionId, request.To, mediaType, mimeType, fileBytes,
                request.Caption ?? "", fn);

            return Ok(result);
        }
        catch (WhatsAppServiceException ex)
        {
            // Return user-friendly error message
            return StatusCode(400, new
            {
                error = ex.Error.UserMessage,
                errorCode = ex.Error.ErrorCode,
                details = ex.Error.AdditionalInfo
            });
        }
    }

    /// <summary>
    /// Get chat messages
    /// GET /api/wa/getMessages?chatId=123456789@c.us&limit=50
    /// </summary>
    [HttpGet("getMessages")]
    public async Task<IActionResult> GetMessages([FromQuery] string chatId, [FromQuery] int limit = 50, [FromQuery] string? sessionId = null)
    {
        var (success, userId, error) = await ValidateApiToken();
        if (!success)
            return Unauthorized(new { error });

        var resolvedSessionId = await GetUserSessionId(userId!.Value, sessionId);
        if (resolvedSessionId == null)
            return BadRequest(new { error = sessionId != null
                ? $"WhatsApp session '{sessionId}' not found or not connected"
                : "No active WhatsApp session" });

        var messages = await _whatsappService.GetMessagesAsync(resolvedSessionId, chatId, limit);

        // Decrypt messages if encryption enabled
        if (_encryptionService.IsEncryptionEnabled && messages != null)
        {
            messages = messages.Select(msg =>
                !string.IsNullOrEmpty(msg.Body)
                    ? msg with { Body = _encryptionService.Decrypt(msg.Body) }
                    : msg
            ).ToList();
        }

        // Fall back to SQLite when in-memory store is empty (restart / re-pair / deploy).
        // The Messages table is excluded from deploy cleanup and is the durable record.
        if (messages == null || messages.Count == 0)
        {
            var sessionIds = await _context.WhatsAppSessions
                .Where(s => s.UserId == userId!.Value)
                .Select(s => s.SessionId)
                .ToListAsync();

            var bare = chatId.Split('@')[0].Split(':')[0];
            var stored = await _context.Messages.AsNoTracking()
                .Where(m => (m.UserId == userId!.Value || (m.UserId == null && sessionIds.Contains(m.SessionId)))
                         && (m.ChatJid == chatId || m.ChatJid.StartsWith(bare + "@")))
                .OrderByDescending(m => m.Timestamp).ThenByDescending(m => m.Id)
                .Take(limit)
                .ToListAsync();

            stored.Reverse();
            return Ok(stored.Select(m => new
            {
                id = m.MessageId,
                from = m.FromMe ? "me" : m.Sender,
                to = m.ChatJid,
                body = m.Body,
                timestamp = m.Timestamp,
                type = m.Type,
                mediaUrl = m.MediaUrl,
                mimeType = m.MimeType
            }));
        }

        return Ok(messages);
    }

    /// <summary>
    /// Durable message log (task 869ecbkv7): reads from the SQLite Messages table instead of
    /// the capped in-memory store, so it survives app-pool restarts and session re-pairs and
    /// spans ALL of the caller's sessions (including replaced ones). chatId accepts a full JID
    /// or a bare phone number. since = unix seconds (only messages with a later WhatsApp
    /// timestamp). Results are returned oldest-first.
    /// GET /api/wa/messages?chatId=31612345678&since=1785500000&count=50&fromMe=false
    /// </summary>
    [HttpGet("messages")]
    public async Task<IActionResult> GetDurableMessages(
        [FromQuery] string? chatId = null,
        [FromQuery] long? since = null,
        [FromQuery] int count = 50,
        [FromQuery] bool? fromMe = null)
    {
        var (success, userId, error) = await ValidateApiToken();
        if (!success)
            return Unauthorized(new { error });

        count = Math.Clamp(count, 1, 500);

        // All sessions ever owned by this user — a reply that arrived on a since-replaced
        // session must stay readable after a re-pair.
        var sessionIds = await _context.WhatsAppSessions
            .Where(s => s.UserId == userId!.Value)
            .Select(s => s.SessionId)
            .ToListAsync();

        var query = _context.Messages.AsNoTracking()
            .Where(m => (m.UserId == userId!.Value || (m.UserId == null && sessionIds.Contains(m.SessionId))));

        if (!string.IsNullOrWhiteSpace(chatId))
        {
            var bare = chatId.Split('@')[0].Split(':')[0];
            query = query.Where(m => m.ChatJid == chatId || m.ChatJid.StartsWith(bare + "@"));
        }
        if (since.HasValue)
            query = query.Where(m => m.Timestamp > since.Value);
        if (fromMe.HasValue)
            query = query.Where(m => m.FromMe == fromMe.Value);

        var messages = await query
            .OrderByDescending(m => m.Timestamp).ThenByDescending(m => m.Id)
            .Take(count)
            .ToListAsync();
        messages.Reverse();

        return Ok(messages.Select(m => new
        {
            id = m.MessageId,
            chatJid = m.ChatJid,
            fromMe = m.FromMe,
            sender = m.Sender,
            body = m.Body,
            type = m.Type,
            mediaUrl = m.MediaUrl,
            mediaAvailable = !string.IsNullOrEmpty(m.MediaUrl) && !string.IsNullOrEmpty(m.MediaKey),
            mediaReady = !string.IsNullOrEmpty(m.LocalMediaPath),
            // Whisper transcript for audio messages (task 869ejuycr) — filled in automatically
            // shortly after ingest, no separate call needed.
            transcript = m.Transcript,
            timestamp = m.Timestamp,
            receivedAt = m.ReceivedAt,
            isHistory = m.IsHistory
        }));
    }

    /// <summary>
    /// Get all chats.
    /// Upserts live results into SQLite (Chats table) so the list survives app-pool
    /// restarts and deploys. Falls back to the stored list when Dawa is offline.
    /// GET /api/wa/getChats
    /// </summary>
    [HttpGet("getChats")]
    public async Task<IActionResult> GetChats([FromQuery] string? sessionId = null)
    {
        var (success, userId, error) = await ValidateApiToken();
        if (!success)
            return Unauthorized(new { error });

        var resolvedSessionId = await GetUserSessionId(userId!.Value, sessionId);

        List<object>? liveChats = null;
        if (resolvedSessionId != null)
            liveChats = await _whatsappService.GetChatsAsync(resolvedSessionId);

        if (liveChats != null && liveChats.Count > 0)
        {
            // Upsert into SQLite so this list survives the next restart/deploy
            var now = DateTime.UtcNow;
            foreach (var chat in liveChats)
            {
                // chat is an anonymous object — reflect jid/name/phone out of it
                var type = chat.GetType();
                var jid  = type.GetProperty("jid")?.GetValue(chat)?.ToString() ?? "";
                var name = type.GetProperty("name")?.GetValue(chat)?.ToString() ?? "";
                var phone = type.GetProperty("phone")?.GetValue(chat)?.ToString() ?? "";
                if (string.IsNullOrEmpty(jid)) continue;

                var existing = await _context.Chats
                    .FirstOrDefaultAsync(c => c.UserId == userId!.Value && c.Jid == jid);
                if (existing == null)
                {
                    _context.Chats.Add(new Models.StoredChat
                    {
                        UserId = userId!.Value,
                        Jid = jid,
                        Name = name,
                        Phone = phone,
                        LastSeenAt = now
                    });
                }
                else
                {
                    existing.Name = name;
                    existing.Phone = phone;
                    existing.LastSeenAt = now;
                }
            }
            await _context.SaveChangesAsync();
            return Ok(liveChats);
        }

        // Dawa offline or returned nothing — return stored chat list from SQLite
        var stored = await _context.Chats
            .Where(c => c.UserId == userId!.Value)
            .OrderByDescending(c => c.LastSeenAt)
            .Select(c => (object)new { jid = c.Jid, name = c.Name, phone = c.Phone, archived = false, pinned = false })
            .ToListAsync();

        return Ok(stored);
    }

    /// <summary>
    /// Get contacts
    /// GET /api/wa/getContacts
    /// </summary>
    [HttpGet("getContacts")]
    public async Task<IActionResult> GetContacts([FromQuery] string? sessionId = null)
    {
        var (success, userId, error) = await ValidateApiToken();
        if (!success)
            return Unauthorized(new { error });

        var resolvedSessionId = await GetUserSessionId(userId!.Value, sessionId);
        if (resolvedSessionId == null)
            return BadRequest(new { error = sessionId != null
                ? $"WhatsApp session '{sessionId}' not found or not connected"
                : "No active WhatsApp session" });

        var contacts = await _whatsappService.GetContactsAsync(resolvedSessionId);

        // Decrypt phone numbers if encryption enabled
        if (_encryptionService.IsEncryptionEnabled && contacts != null)
        {
            contacts = contacts.Select(contact =>
                !string.IsNullOrEmpty(contact.Number)
                    ? contact with { Number = _encryptionService.Decrypt(contact.Number) }
                    : contact
            ).ToList();
        }

        return Ok(contacts);
    }

    /// <summary>
    /// Check if number is registered on WhatsApp
    /// GET /api/wa/checkNumberStatus?number=1234567890
    /// </summary>
    [HttpGet("checkNumberStatus")]
    public async Task<IActionResult> CheckNumberStatus([FromQuery] string number, [FromQuery] string? sessionId = null)
    {
        var (success, userId, error) = await ValidateApiToken();
        if (!success)
            return Unauthorized(new { error });

        var resolvedSessionId = await GetUserSessionId(userId!.Value, sessionId);
        if (resolvedSessionId == null)
            return BadRequest(new { error = sessionId != null
                ? $"WhatsApp session '{sessionId}' not found or not connected"
                : "No active WhatsApp session" });

        var result = await _whatsappService.CheckNumberStatusAsync(resolvedSessionId, number);

        return Ok(result);
    }

    /// <summary>
    /// Download and decrypt WhatsApp media
    /// POST /api/wa/downloadMedia
    /// </summary>
    [HttpPost("downloadMedia")]
    public async Task<IActionResult> DownloadMedia([FromBody] DownloadMediaRequest request)
    {
        var (success, userId, error) = await ValidateApiToken();
        if (!success)
            return Unauthorized(new { error });

        var sessionId = await GetUserSessionId(userId!.Value, request.SessionId);
        if (sessionId == null)
            return BadRequest(new { error = "No active WhatsApp session" });

        try
        {
            var mediaUrl = request.MediaUrl;
            var mediaKey = request.MediaKey;
            var mimeType = request.MimeType;

            // Task 869ejuycr: resolve by chatJid+messageId instead of a manually-extracted
            // mediaUrl/mediaKey — serves the already-decrypted local cache directly when
            // available (instant, no re-decrypt), falling back to the message's own stored
            // MediaUrl/MediaKey otherwise. Lets a caller (e.g. an agent reading getMessages)
            // fetch the actual file without ever having to handle the raw key itself.
            if (!string.IsNullOrEmpty(request.ChatJid) && !string.IsNullOrEmpty(request.MessageId))
            {
                var stored = await _context.Messages.AsNoTracking().FirstOrDefaultAsync(m =>
                    m.SessionId == sessionId && m.ChatJid == request.ChatJid && m.MessageId == request.MessageId);
                if (stored == null)
                    return NotFound(new { error = "Message not found" });

                if (!string.IsNullOrEmpty(stored.LocalMediaPath) && System.IO.File.Exists(stored.LocalMediaPath))
                {
                    var cachedBytes = await System.IO.File.ReadAllBytesAsync(stored.LocalMediaPath);
                    return File(cachedBytes, stored.MimeType ?? "application/octet-stream");
                }

                if (string.IsNullOrEmpty(stored.MediaUrl) || string.IsNullOrEmpty(stored.MediaKey))
                    return NotFound(new { error = "Media niet beschikbaar voor dit bericht" });

                mediaUrl = stored.MediaUrl;
                mediaKey = stored.MediaKey;
                mimeType = stored.MimeType ?? mimeType;
            }

            if (string.IsNullOrEmpty(mediaUrl) || string.IsNullOrEmpty(mediaKey))
                return BadRequest(new { error = "mediaUrl/mediaKey or chatJid/messageId required" });

            var bytes = await _whatsappService.DownloadMediaAsync(sessionId!, mediaUrl, mediaKey, mimeType ?? "application/octet-stream");
            return File(bytes ?? Array.Empty<byte>(), mimeType ?? "application/octet-stream");
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { error = ex.Message });
        }
    }

    /// <summary>
    /// Revoke (delete for everyone) a sent message
    /// POST /api/wa/revokeMessage
    /// </summary>
    [HttpPost("revokeMessage")]
    public async Task<IActionResult> RevokeMessage([FromBody] RevokeMessageRequest request)
    {
        var (success, userId, error) = await ValidateApiToken();
        if (!success)
            return Unauthorized(new { error });

        var sessionId = await GetUserSessionId(userId!.Value, request.SessionId);
        if (sessionId == null)
            return BadRequest(new { error = "No active WhatsApp session" });

        try
        {
            await _whatsappService.RevokeMessageAsync(sessionId!, request.ChatJid, request.MessageId, request.FromMe, 0);
            return Ok(new { success = true });
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { error = ex.Message });
        }
    }

    /// <summary>
    /// Forward a message to another chat
    /// POST /api/wa/forwardMessage
    /// </summary>
    [HttpPost("forwardMessage")]
    public async Task<IActionResult> ForwardMessage([FromBody] ForwardMessageRequest request)
    {
        var (success, userId, error) = await ValidateApiToken();
        if (!success)
            return Unauthorized(new { error });

        var (allowed, blockReason) = await _outboundGuardrail.CheckAsync("forwardMessage", request.ToJid, request.Text, userId);
        if (!allowed)
            return StatusCode(403, new { error = blockReason, blocked = true });

        var sessionId = await GetUserSessionId(userId!.Value, request.SessionId);
        if (sessionId == null)
            return BadRequest(new { error = "No active WhatsApp session" });

        try
        {
            var result = await _whatsappService.ForwardMessageAsync(sessionId!, request.ToJid, request.Text);
            return Ok(result);
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { error = ex.Message });
        }
    }

    /// <summary>
    /// Send typing indicator to a chat
    /// POST /api/wa/sendTyping
    /// </summary>
    [HttpPost("sendTyping")]
    public async Task<IActionResult> SendTyping([FromBody] SendTypingRequest request)
    {
        var (success, userId, error) = await ValidateApiToken();
        if (!success)
            return Unauthorized(new { error });

        var sessionId = await GetUserSessionId(userId!.Value, request.SessionId);
        if (sessionId == null)
            return BadRequest(new { error = "No active WhatsApp session" });

        try
        {
            await _whatsappService.SendTypingAsync(sessionId!, request.ChatJid, request.IsTyping);
            return Ok(new { success = true });
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { error = ex.Message });
        }
    }

    /// <summary>
    /// Set user presence (online/offline)
    /// POST /api/wa/setPresence
    /// </summary>
    [HttpPost("setPresence")]
    public async Task<IActionResult> SetPresence([FromBody] SetPresenceRequest request)
    {
        var (success, userId, error) = await ValidateApiToken();
        if (!success)
            return Unauthorized(new { error });

        var sessionId = await GetUserSessionId(userId!.Value, request.SessionId);
        if (sessionId == null)
            return BadRequest(new { error = "No active WhatsApp session" });

        try
        {
            await _whatsappService.SendPresenceAsync(sessionId!, request.Available);
            return Ok(new { success = true });
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { error = ex.Message });
        }
    }

    /// <summary>
    /// Create a new WhatsApp group
    /// POST /api/wa/createGroup
    /// </summary>
    [HttpPost("createGroup")]
    public async Task<IActionResult> CreateGroup([FromBody] CreateGroupRequest request)
    {
        var (success, userId, error) = await ValidateApiToken();
        if (!success)
            return Unauthorized(new { error });

        var sessionId = await GetUserSessionId(userId!.Value, request.SessionId);
        if (sessionId == null)
            return BadRequest(new { error = "No active WhatsApp session" });

        try
        {
            var result = await _whatsappService.CreateGroupAsync(sessionId!, request.Subject, request.Participants);
            return Ok(result);
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { error = ex.Message });
        }
    }

    /// <summary>
    /// Leave a WhatsApp group
    /// POST /api/wa/leaveGroup
    /// </summary>
    [HttpPost("leaveGroup")]
    public async Task<IActionResult> LeaveGroup([FromBody] GroupJidRequest request)
    {
        var (success, userId, error) = await ValidateApiToken();
        if (!success)
            return Unauthorized(new { error });

        var sessionId = await GetUserSessionId(userId!.Value, request.SessionId);
        if (sessionId == null)
            return BadRequest(new { error = "No active WhatsApp session" });

        try
        {
            await _whatsappService.LeaveGroupAsync(sessionId!, request.GroupJid);
            return Ok(new { success = true });
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { error = ex.Message });
        }
    }

    /// <summary>
    /// Add participants to a WhatsApp group
    /// POST /api/wa/addGroupParticipants
    /// </summary>
    [HttpPost("addGroupParticipants")]
    public async Task<IActionResult> AddGroupParticipants([FromBody] GroupParticipantsRequest request)
    {
        var (success, userId, error) = await ValidateApiToken();
        if (!success)
            return Unauthorized(new { error });

        var sessionId = await GetUserSessionId(userId!.Value, request.SessionId);
        if (sessionId == null)
            return BadRequest(new { error = "No active WhatsApp session" });

        try
        {
            await _whatsappService.AddGroupParticipantsAsync(sessionId!, request.GroupJid, request.Participants);
            return Ok(new { success = true });
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { error = ex.Message });
        }
    }

    /// <summary>
    /// Remove participants from a WhatsApp group
    /// POST /api/wa/removeGroupParticipants
    /// </summary>
    [HttpPost("removeGroupParticipants")]
    public async Task<IActionResult> RemoveGroupParticipants([FromBody] GroupParticipantsRequest request)
    {
        var (success, userId, error) = await ValidateApiToken();
        if (!success)
            return Unauthorized(new { error });

        var sessionId = await GetUserSessionId(userId!.Value, request.SessionId);
        if (sessionId == null)
            return BadRequest(new { error = "No active WhatsApp session" });

        try
        {
            await _whatsappService.RemoveGroupParticipantsAsync(sessionId!, request.GroupJid, request.Participants);
            return Ok(new { success = true });
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { error = ex.Message });
        }
    }

    /// <summary>
    /// Get the invite link for a WhatsApp group
    /// POST /api/wa/getGroupInviteLink
    /// </summary>
    [HttpPost("getGroupInviteLink")]
    public async Task<IActionResult> GetGroupInviteLink([FromBody] GroupJidRequest request)
    {
        var (success, userId, error) = await ValidateApiToken();
        if (!success)
            return Unauthorized(new { error });

        var sessionId = await GetUserSessionId(userId!.Value, request.SessionId);
        if (sessionId == null)
            return BadRequest(new { error = "No active WhatsApp session" });

        try
        {
            var result = await _whatsappService.GetGroupInviteLinkAsync(sessionId!, request.GroupJid);
            return Ok(result);
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { error = ex.Message });
        }
    }

    /// <summary>
    /// Update a WhatsApp group subject (name)
    /// POST /api/wa/updateGroupSubject
    /// </summary>
    [HttpPost("updateGroupSubject")]
    public async Task<IActionResult> UpdateGroupSubject([FromBody] UpdateGroupSubjectRequest request)
    {
        var (success, userId, error) = await ValidateApiToken();
        if (!success)
            return Unauthorized(new { error });

        var sessionId = await GetUserSessionId(userId!.Value, request.SessionId);
        if (sessionId == null)
            return BadRequest(new { error = "No active WhatsApp session" });

        try
        {
            await _whatsappService.UpdateGroupSubjectAsync(sessionId!, request.GroupJid, request.Subject);
            return Ok(new { success = true });
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { error = ex.Message });
        }
    }

    /// <summary>
    /// Outbound sends refused by the guardrail (task 869edf485): surfaces blocked attempts
    /// instead of leaving them only in a log line, scoped to the caller's own user.
    /// GET /api/wa/blockedOutbound?count=50
    /// </summary>
    [HttpGet("blockedOutbound")]
    public async Task<IActionResult> GetBlockedOutbound([FromQuery] int count = 50)
    {
        var (success, userId, error) = await ValidateApiToken();
        if (!success)
            return Unauthorized(new { error });

        count = Math.Clamp(count, 1, 500);

        var blocked = await _context.BlockedOutboundMessages
            .AsNoTracking()
            .Where(b => b.UserId == userId!.Value)
            .OrderByDescending(b => b.BlockedAtUtc)
            .Take(count)
            .ToListAsync();

        return Ok(blocked.Select(b => new
        {
            endpoint = b.Endpoint,
            to = b.Recipient,
            bodyPreview = b.BodyPreview,
            reason = b.Reason,
            blockedAtUtc = b.BlockedAtUtc,
        }));
    }

    /// <summary>
    /// Get message delivery/read status
    /// GET /api/wa/messageStatus?messageId=xxx
    /// </summary>
    [HttpGet("messageStatus")]
    public async Task<IActionResult> GetMessageStatus([FromQuery] string messageId, [FromQuery] string? sessionId = null)
    {
        var (success, userId, error) = await ValidateApiToken();
        if (!success)
            return Unauthorized(new { error });

        var resolvedSessionId = await GetUserSessionId(userId!.Value, sessionId);
        if (resolvedSessionId == null)
            return BadRequest(new { error = "No active WhatsApp session" });

        var status = _whatsappService.GetMessageStatus(resolvedSessionId!, messageId);
        return Ok(new { messageId, status = status?.ToString() ?? "unknown" });
    }

    /// <summary>
    /// Get message delivery/read status (path-based resource route)
    /// GET /api/{sessionId}/messages/{msgId}/status
    /// </summary>
    [HttpGet("/api/{sessionId}/messages/{msgId}/status")]
    public async Task<IActionResult> GetMessageStatusByPath(string sessionId, string msgId)
    {
        var (success, userId, error) = await ValidateApiToken();
        if (!success)
            return Unauthorized(new { error });

        var resolvedSessionId = await GetUserSessionId(userId!.Value, sessionId);
        if (resolvedSessionId == null)
            return BadRequest(new { error = $"WhatsApp session '{sessionId}' not found or not connected" });

        var status = _whatsappService.GetMessageStatus(resolvedSessionId!, msgId);
        return Ok(new { messageId = msgId, status = status?.ToString() ?? "unknown" });
    }
}

public record SendMessageRequest(string To, string Body, string? SessionId = null);
public record SendReplyRequest(string To, string Body, string QuotedMessageId, string QuotedFromJid, string? SessionId = null);
public record RequestHistoryRequest(string ChatId, int Count = 50, bool NoAnchor = false, string? SessionId = null);
public record SendMediaRequest(string To, string MediaUrl, string? Caption = null, string? SessionId = null);
public record DownloadMediaRequest(
    string? MediaUrl = null,
    string? MediaKey = null,
    string? MimeType = null,
    string? SessionId = null,
    // Task 869ejuycr: alternative to MediaUrl/MediaKey — fetch by chatJid+messageId to use the
    // already-decrypted local cache without the caller ever handling the raw encryption key.
    string? ChatJid = null,
    string? MessageId = null);
public record RevokeMessageRequest(string ChatJid, string MessageId, bool FromMe = true, string? SessionId = null);
public record ForwardMessageRequest(string ToJid, string Text, string? SessionId = null);
public record SendTypingRequest(string ChatJid, bool IsTyping = true, string? SessionId = null);
public record SetPresenceRequest(bool Available, string? SessionId = null);
public record CreateGroupRequest(string Subject, List<string> Participants, string? SessionId = null);
public record GroupJidRequest(string GroupJid, string? SessionId = null);
public record GroupParticipantsRequest(string GroupJid, List<string> Participants, string? SessionId = null);
public record UpdateGroupSubjectRequest(string GroupJid, string Subject, string? SessionId = null);
public record WhatsAppMessage(
    string Id,
    string From,
    string To,
    string Body,
    long Timestamp,
    string Type = "text",
    string? MediaUrl = null,
    string? MimeType = null,
    string? FileName = null,
    long? FileSize = null,
    uint? Duration = null,
    uint? Width = null,
    uint? Height = null,
    string? MediaKey = null,
    string? MediaSha256Enc = null,
    string? ReactionEmoji = null,
    string? ReactionTargetId = null,
    bool IsRevoked = false,
    string? QuotedMessageId = null,
    string? QuotedFrom = null,
    string? QuotedText = null,
    string? QuotedType = null,
    string? Status = null,
    // Whisper transcript for audio messages, filled in shortly after ingest (task 869ejuycr).
    // Null until transcription completes (or for non-audio messages).
    string? Transcript = null);
public record WhatsAppContact(string Id, string Name, string Number);
