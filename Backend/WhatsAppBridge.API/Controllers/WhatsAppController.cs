using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;
using WhatsAppBridge.API.Data;
using WhatsAppBridge.API.Services;

namespace WhatsAppBridge.API.Controllers;

[Authorize(AuthenticationSchemes = "Bearer,ApiKey")]
[ApiController]
[Route("api/[controller]")]
public class WhatsAppController : ControllerBase
{
    private readonly AppDbContext _context;
    private readonly WhatsAppBridgeService _whatsappService;
    private readonly EncryptionService _encryptionService;

    public WhatsAppController(AppDbContext context, WhatsAppBridgeService whatsappService, EncryptionService encryptionService)
    {
        _context = context;
        _whatsappService = whatsappService;
        _encryptionService = encryptionService;
    }

    private int GetUserId()
    {
        var userIdClaim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        return int.Parse(userIdClaim!);
    }

    [HttpGet("sessions")]
    public async Task<IActionResult> GetSessions()
    {
        var userId = GetUserId();
        var sessions = await _context.WhatsAppSessions
            .Where(s => s.UserId == userId)
            .Select(s => new
            {
                s.Id,
                s.SessionId,
                PhoneNumber = _encryptionService.Decrypt(s.PhoneNumber),
                s.Status,
                s.CreatedAt,
                s.ConnectedAt,
                s.LastSeenAt
            })
            .ToListAsync();

        return Ok(sessions);
    }

    [HttpPost("sessions/create")]
    public async Task<IActionResult> CreateSession()
    {
        var userId = GetUserId();
        var sessionId = Guid.NewGuid().ToString();

        var session = new Models.WhatsAppSession
        {
            UserId = userId,
            SessionId = sessionId,
            Status = "qr_pending"
        };

        _context.WhatsAppSessions.Add(session);
        await _context.SaveChangesAsync();

        // Request QR code from WhatsApp service
        var qrCode = await _whatsappService.InitializeSessionAsync(sessionId);

        if (qrCode != null)
        {
            session.QrCode = qrCode;
            await _context.SaveChangesAsync();
        }

        return Ok(new
        {
            sessionId,
            qrCode,
            status = "qr_pending"
        });
    }

    [HttpGet("sessions/{sessionId}/qr")]
    public async Task<IActionResult> GetQrCode(string sessionId)
    {
        var userId = GetUserId();
        var session = await _context.WhatsAppSessions
            .FirstOrDefaultAsync(s => s.SessionId == sessionId && s.UserId == userId);

        if (session == null)
            return NotFound();

        return Ok(new { qrCode = session.QrCode, status = session.Status });
    }

    [HttpPost("sessions/{sessionId}/test")]
    public async Task<IActionResult> TestSession(string sessionId)
    {
        var userId = GetUserId();
        var session = await _context.WhatsAppSessions
            .FirstOrDefaultAsync(s => s.SessionId == sessionId && s.UserId == userId);

        if (session == null)
            return NotFound(new { success = false, message = "Session not found" });

        try
        {
            var isConnected = _whatsappService.IsSessionConnected(sessionId);
            if (!isConnected)
                return Ok(new { success = false, message = "Session is not connected" });

            // Update last seen
            session.LastSeenAt = DateTime.UtcNow;
            await _context.SaveChangesAsync();

            var phoneNumber = _encryptionService.Decrypt(session.PhoneNumber);
            return Ok(new { success = true, message = "WhatsApp session is active", phoneNumber });
        }
        catch (Exception ex)
        {
            return Ok(new { success = false, message = ex.Message });
        }
    }

    [HttpPost("sessions/{sessionId}/send")]
    public async Task<IActionResult> SendMessage(string sessionId, [FromBody] SendRequest request)
    {
        var userId = GetUserId();
        var session = await _context.WhatsAppSessions
            .FirstOrDefaultAsync(s => s.SessionId == sessionId && s.UserId == userId);

        if (session == null)
            return NotFound(new { error = "Session not found" });

        if (session.Status != "connected")
            return BadRequest(new { error = $"Session is not connected (status: {session.Status})" });

        try
        {
            await _whatsappService.SendMessageAsync(sessionId, request.To, request.Message);
            return Ok(new { success = true, message = "Message sent" });
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { error = ex.Message });
        }
    }

    public record SendRequest(string To, string Message);

    [HttpGet("sessions/{sessionId}/contacts")]
    public async Task<IActionResult> GetContacts(string sessionId)
    {
        var userId = GetUserId();
        var session = await _context.WhatsAppSessions
            .FirstOrDefaultAsync(s => s.SessionId == sessionId && s.UserId == userId);

        if (session == null)
            return NotFound(new { error = "Session not found" });

        var contacts = await _whatsappService.GetContactsAsync(sessionId);
        return Ok(contacts ?? new List<WhatsAppContact>());
    }

    [HttpGet("sessions/{sessionId}/chats")]
    public async Task<IActionResult> GetChats(string sessionId)
    {
        var userId = GetUserId();
        var session = await _context.WhatsAppSessions
            .FirstOrDefaultAsync(s => s.SessionId == sessionId && s.UserId == userId);
        if (session == null) return NotFound();
        var chats = await _whatsappService.GetChatsAsync(sessionId);
        return Ok(chats ?? new List<object>());
    }

    [HttpGet("sessions/{sessionId}/profile-pic/{jid}")]
    public async Task<IActionResult> GetProfilePic(string sessionId, string jid)
    {
        var userId = GetUserId();
        var session = await _context.WhatsAppSessions
            .FirstOrDefaultAsync(s => s.SessionId == sessionId && s.UserId == userId);
        if (session == null) return NotFound();
        var url = await _whatsappService.GetProfilePictureAsync(sessionId, jid);
        if (url == null) return NotFound(new { error = "No profile picture found" });
        return Ok(new { url });
    }

    [HttpPost("sessions/{sessionId}/presence/subscribe")]
    public async Task<IActionResult> SubscribePresence(string sessionId, [FromBody] JidRequest request)
    {
        var userId = GetUserId();
        var session = await _context.WhatsAppSessions
            .FirstOrDefaultAsync(s => s.SessionId == sessionId && s.UserId == userId);
        if (session == null) return NotFound();
        await _whatsappService.SubscribePresenceAsync(sessionId, request.Jid);
        return Ok(new { subscribed = true });
    }

    [HttpGet("sessions/{sessionId}/presence/{jid}")]
    public async Task<IActionResult> GetPresence(string sessionId, string jid)
    {
        var userId = GetUserId();
        var session = await _context.WhatsAppSessions
            .FirstOrDefaultAsync(s => s.SessionId == sessionId && s.UserId == userId);
        if (session == null) return NotFound();
        var presence = _whatsappService.GetPresence(sessionId, jid);
        return Ok(presence);
    }

    [HttpPost("sessions/{sessionId}/read-receipt")]
    public async Task<IActionResult> SendReadReceipt(string sessionId, [FromBody] ReadReceiptRequest request)
    {
        var userId = GetUserId();
        var session = await _context.WhatsAppSessions
            .FirstOrDefaultAsync(s => s.SessionId == sessionId && s.UserId == userId);
        if (session == null) return NotFound();
        if (session.Status != "connected") return BadRequest(new { error = "Session not connected" });
        await _whatsappService.SendReadReceiptAsync(sessionId, request.Jid, request.MessageId, request.Timestamp);
        return Ok(new { success = true });
    }

    public record JidRequest(string Jid);
    public record ReadReceiptRequest(string Jid, string MessageId, long Timestamp);

    /// <summary>
    /// Test endpoint for sending messages without auth (dev only).
    /// </summary>
    [AllowAnonymous]
    [HttpPost("test-send/{sessionId}")]
    public async Task<IActionResult> TestSend(string sessionId, [FromBody] SendRequest request)
    {
        var session = await _context.WhatsAppSessions
            .FirstOrDefaultAsync(s => s.SessionId == sessionId);

        if (session == null)
            return NotFound(new { error = "Session not found" });

        if (session.Status != "connected")
            return BadRequest(new { error = $"Session is not connected (status: {session.Status})" });

        try
        {
            await _whatsappService.SendMessageAsync(sessionId, request.To, request.Message);
            return Ok(new { success = true, message = "Message sent" });
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { error = ex.Message, stack = ex.StackTrace });
        }
    }

    /// <summary>
    /// Re-pair endpoint: wipes old session data, creates fresh auth state, returns QR code.
    /// Dev/test only — remove for production.
    /// </summary>
    [AllowAnonymous]
    [HttpPost("test-repair/{sessionId}")]
    public async Task<IActionResult> TestRepair(string sessionId)
    {
        // 1. Disconnect existing client
        await _whatsappService.DisconnectSessionAsync(sessionId);

        // 2. Wipe session files (creds.json, signals.json) to force fresh pairing
        var sessionDir = Path.Combine(AppContext.BaseDirectory, "whatsapp-sessions", sessionId);
        if (Directory.Exists(sessionDir))
        {
            foreach (var file in Directory.GetFiles(sessionDir))
                System.IO.File.Delete(file);
        }

        // 3. Initialize fresh session — this will produce a QR code
        var qrCode = await _whatsappService.InitializeSessionAsync(sessionId);

        if (qrCode != null)
        {
            return Ok(new { success = true, qrCode, message = "Scan this QR code with WhatsApp. First unlink old device from Linked Devices." });
        }

        return Ok(new { success = false, message = "QR code not received within timeout. Try GET /api/WhatsApp/test-qr/{sessionId} to poll." });
    }

    /// <summary>
    /// Poll for QR code (anonymous, dev only).
    /// </summary>
    [AllowAnonymous]
    [HttpGet("test-qr/{sessionId}")]
    public async Task<IActionResult> TestGetQr(string sessionId)
    {
        var session = await _context.WhatsAppSessions
            .FirstOrDefaultAsync(s => s.SessionId == sessionId);

        if (session == null)
            return NotFound(new { error = "Session not found" });

        return Ok(new { qrCode = session.QrCode, status = session.Status });
    }

    /// <summary>
    /// Check session connection status (anonymous, dev only).
    /// </summary>
    [AllowAnonymous]
    [HttpGet("test-status/{sessionId}")]
    public async Task<IActionResult> TestStatus(string sessionId)
    {
        var session = await _context.WhatsAppSessions
            .FirstOrDefaultAsync(s => s.SessionId == sessionId);

        if (session == null)
            return NotFound(new { error = "Session not found" });

        var connected = _whatsappService.IsSessionConnected(sessionId);
        return Ok(new { sessionId, status = session.Status, connected, phoneNumber = session.PhoneNumber });
    }

    /// <summary>Test contacts fetch (anonymous, dev only).</summary>
    [AllowAnonymous]
    [HttpGet("test-contacts/{sessionId}")]
    public async Task<IActionResult> TestContacts(string sessionId)
    {
        try
        {
            var contacts = await _whatsappService.GetContactsAsync(sessionId);
            return Ok(new { count = contacts?.Count ?? 0, contacts });
        }
        catch (Exception ex)
        {
            return Ok(new { error = ex.Message });
        }
    }

    [HttpGet("sessions/{sessionId}/messages/{chatId}")]
    public async Task<IActionResult> GetMessages(string sessionId, string chatId, [FromQuery] int limit = 50)
    {
        var userId = GetUserId();
        var session = await _context.WhatsAppSessions
            .FirstOrDefaultAsync(s => s.SessionId == sessionId && s.UserId == userId);
        if (session == null) return NotFound();
        var messages = await _whatsappService.GetMessagesAsync(sessionId, chatId, limit);
        return Ok(messages ?? new List<WhatsAppMessage>());
    }

    [HttpGet("sessions/{sessionId}/groups")]
    public async Task<IActionResult> GetGroups(string sessionId)
    {
        var userId = GetUserId();
        var session = await _context.WhatsAppSessions
            .FirstOrDefaultAsync(s => s.SessionId == sessionId && s.UserId == userId);
        if (session == null) return NotFound();
        var groups = await _whatsappService.GetGroupsAsync(sessionId);
        return Ok(groups ?? new List<object>());
    }

    [HttpGet("sessions/{sessionId}/groups/{groupJid}")]
    public async Task<IActionResult> GetGroupMembers(string sessionId, string groupJid)
    {
        var userId = GetUserId();
        var session = await _context.WhatsAppSessions
            .FirstOrDefaultAsync(s => s.SessionId == sessionId && s.UserId == userId);
        if (session == null) return NotFound();
        var meta = await _whatsappService.GetGroupMembersAsync(sessionId, groupJid);
        if (meta == null) return NotFound(new { error = "Group not found or not connected" });
        return Ok(meta);
    }

    /// <summary>Test messages fetch (anonymous, dev only).</summary>
    [AllowAnonymous]
    [HttpGet("test-messages/{sessionId}/{chatId}")]
    public async Task<IActionResult> TestMessages(string sessionId, string chatId, [FromQuery] int limit = 50)
    {
        try
        {
            var messages = await _whatsappService.GetMessagesAsync(sessionId, chatId, limit);
            return Ok(new { count = messages?.Count ?? 0, messages });
        }
        catch (Exception ex)
        {
            return Ok(new { error = ex.Message });
        }
    }

    /// <summary>Test groups fetch (anonymous, dev only).</summary>
    [AllowAnonymous]
    [HttpGet("test-groups/{sessionId}")]
    public async Task<IActionResult> TestGroups(string sessionId)
    {
        try
        {
            var groups = await _whatsappService.GetGroupsAsync(sessionId);
            return Ok(new { count = groups?.Count ?? 0, groups });
        }
        catch (Exception ex)
        {
            return Ok(new { error = ex.Message });
        }
    }

    /// <summary>Test group members fetch (anonymous, dev only).</summary>
    [AllowAnonymous]
    [HttpGet("test-group/{sessionId}/{groupJid}")]
    public async Task<IActionResult> TestGroupMembers(string sessionId, string groupJid)
    {
        try
        {
            var meta = await _whatsappService.GetGroupMembersAsync(sessionId, groupJid);
            if (meta == null) return Ok(new { error = "Group not found or metadata unavailable" });
            return Ok(meta);
        }
        catch (Exception ex)
        {
            return Ok(new { error = ex.Message });
        }
    }

    /// <summary>Test chats fetch (anonymous, dev only).</summary>
    [AllowAnonymous]
    [HttpGet("test-chats/{sessionId}")]
    public async Task<IActionResult> TestChats(string sessionId)
    {
        try
        {
            var chats = await _whatsappService.GetChatsAsync(sessionId);
            return Ok(new { count = chats?.Count ?? 0, chats });
        }
        catch (Exception ex)
        {
            return Ok(new { error = ex.Message });
        }
    }

    /// <summary>Debug: show internal cache state (anonymous, dev only).</summary>
    [AllowAnonymous]
    [HttpGet("test-debug/{sessionId}")]
    public IActionResult TestDebug(string sessionId)
    {
        return Ok(_whatsappService.GetSessionDebugInfo(sessionId));
    }

    /// <summary>
    /// Debug: resolve a LID JID to a phone JID via usync IQ.
    /// Sends a live usync query to WhatsApp and returns the resolved phone JID.
    /// Example: GET /api/WhatsApp/test-resolve-lid/{sessionId}/261542083862683%40lid
    /// </summary>
    [AllowAnonymous]
    [HttpGet("test-resolve-lid/{sessionId}/{lid}")]
    public async Task<IActionResult> TestResolveLid(string sessionId, string lid)
    {
        var resolved = await _whatsappService.ResolveLidAsync(sessionId, lid);
        return Ok(new
        {
            lid,
            resolved,
            success = resolved != null,
        });
    }

    /// <summary>
    /// Debug: return all stored messages for a chat JID from the in-memory message store.
    /// Example: GET /api/WhatsApp/test-stored-messages/{sessionId}/261542083862683%40lid
    /// </summary>
    [AllowAnonymous]
    [HttpGet("test-stored-messages/{sessionId}/{chatId}")]
    public async Task<IActionResult> TestStoredMessages(string sessionId, string chatId)
    {
        var messages = await _whatsappService.GetMessagesAsync(sessionId, chatId, 100);
        return Ok(new
        {
            chatId,
            count = messages?.Count ?? 0,
            messages,
        });
    }

    /// <summary>
    /// Debug: attempt to fetch message history for a JID by sending a w:msg sync IQ.
    /// Also resolves LID JIDs to phone JIDs first via usync before fetching.
    /// Example: POST /api/WhatsApp/test-fetch-history/{sessionId}/261542083862683%40lid
    /// </summary>
    [AllowAnonymous]
    [HttpPost("test-fetch-history/{sessionId}/{chatId}")]
    public async Task<IActionResult> TestFetchHistory(string sessionId, string chatId)
    {
        // If it's a LID, resolve to phone JID first
        var resolvedJid = chatId;
        if (chatId.EndsWith("@lid"))
        {
            var phoneJid = await _whatsappService.ResolveLidAsync(sessionId, chatId);
            if (phoneJid != null)
            {
                resolvedJid = phoneJid;
            }
        }

        var messages = await _whatsappService.FetchMessageHistoryAsync(sessionId, resolvedJid, 50);
        return Ok(new
        {
            requestedJid = chatId,
            resolvedJid,
            count = messages?.Count ?? 0,
            messages,
        });
    }

    [HttpDelete("sessions/{sessionId}")]
    public async Task<IActionResult> DeleteSession(string sessionId)
    {
        var userId = GetUserId();
        var session = await _context.WhatsAppSessions
            .FirstOrDefaultAsync(s => s.SessionId == sessionId && s.UserId == userId);

        if (session == null)
            return NotFound();

        await _whatsappService.DisconnectSessionAsync(sessionId);

        _context.WhatsAppSessions.Remove(session);
        await _context.SaveChangesAsync();

        return NoContent();
    }
}
