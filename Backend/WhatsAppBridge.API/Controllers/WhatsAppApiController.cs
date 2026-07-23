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

    public WhatsAppApiController(
        AppDbContext context,
        AuthService authService,
        WhatsAppBridgeService whatsappService,
        EncryptionService encryptionService)
    {
        _context = context;
        _authService = authService;
        _whatsappService = whatsappService;
        _encryptionService = encryptionService;
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

        return Ok(messages);
    }

    /// <summary>
    /// Get all chats
    /// GET /api/wa/getChats
    /// </summary>
    [HttpGet("getChats")]
    public async Task<IActionResult> GetChats([FromQuery] string? sessionId = null)
    {
        var (success, userId, error) = await ValidateApiToken();
        if (!success)
            return Unauthorized(new { error });

        var resolvedSessionId = await GetUserSessionId(userId!.Value, sessionId);
        if (resolvedSessionId == null)
            return BadRequest(new { error = sessionId != null
                ? $"WhatsApp session '{sessionId}' not found or not connected"
                : "No active WhatsApp session" });

        var chats = await _whatsappService.GetChatsAsync(resolvedSessionId);

        return Ok(chats);
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
            var bytes = await _whatsappService.DownloadMediaAsync(sessionId!, request.MediaUrl, request.MediaKey, request.MimeType ?? "application/octet-stream");
            return File(bytes ?? Array.Empty<byte>(), request.MimeType ?? "application/octet-stream");
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
public record RequestHistoryRequest(string ChatId, int Count = 50, bool NoAnchor = false, string? SessionId = null);
public record SendMediaRequest(string To, string MediaUrl, string? Caption = null, string? SessionId = null);
public record DownloadMediaRequest(string MediaUrl, string MediaKey, string? MimeType = null, string? SessionId = null);
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
    string? Status = null);
public record WhatsAppContact(string Id, string Name, string Number);
