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
