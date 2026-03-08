using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;
using WhatsAppBridge.API.Data;
using WhatsAppBridge.API.Services;
using WhatsAppBridge.API.Models;

namespace WhatsAppBridge.API.Controllers;

[ApiController]
[Route("api/external")]
[Authorize(AuthenticationSchemes = "ApiKey")]
public class ExternalApiController : ControllerBase
{
    private readonly AppDbContext _context;
    private readonly WhatsAppBridgeService _whatsappService;
    private readonly EncryptionService _encryptionService;

    public ExternalApiController(
        AppDbContext context,
        WhatsAppBridgeService whatsappService,
        EncryptionService encryptionService)
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

    /// <summary>
    /// Get all WhatsApp sessions for the authenticated user
    /// </summary>
    [HttpGet("whatsapp/sessions")]
    public async Task<IActionResult> GetSessions()
    {
        var userId = GetUserId();
        var sessions = await _context.WhatsAppSessions
            .Where(s => s.UserId == userId)
            .Select(s => new
            {
                s.Id,
                s.SessionId,
                s.Status,
                PhoneNumber = s.PhoneNumber != null ? _encryptionService.Decrypt(s.PhoneNumber) : null,
                s.ConnectedAt,
                s.LastSeenAt
            })
            .ToListAsync();

        return Ok(sessions);
    }

    /// <summary>
    /// Create a new WhatsApp session and get QR code
    /// </summary>
    [HttpPost("whatsapp/sessions/create")]
    public async Task<IActionResult> CreateSession()
    {
        var userId = GetUserId();

        // Create session in database
        var session = new WhatsAppSession
        {
            UserId = userId,
            SessionId = Guid.NewGuid().ToString(),
            Status = "qr_pending",
            CreatedAt = DateTime.UtcNow
        };

        _context.WhatsAppSessions.Add(session);
        await _context.SaveChangesAsync();

        // Request QR code from WhatsApp service
        var qrCode = await _whatsappService.CreateSessionAsync(session.SessionId);

        return Ok(new
        {
            sessionId = session.SessionId,
            qrCode,
            status = session.Status,
            message = "Scan the QR code with WhatsApp to connect"
        });
    }

    /// <summary>
    /// Get QR code for an existing session
    /// </summary>
    [HttpGet("whatsapp/sessions/{sessionId}/qr")]
    public async Task<IActionResult> GetQrCode(string sessionId)
    {
        var userId = GetUserId();
        var session = await _context.WhatsAppSessions
            .FirstOrDefaultAsync(s => s.SessionId == sessionId && s.UserId == userId);

        if (session == null)
            return NotFound(new { message = "Session not found" });

        if (session.Status != "qr_pending")
            return BadRequest(new { message = "Session is not in QR pending state" });

        var qrCode = await _whatsappService.CreateSessionAsync(sessionId);
        return Ok(new { sessionId, qrCode });
    }

    /// <summary>
    /// Send a text message via WhatsApp
    /// </summary>
    [HttpPost("whatsapp/messages/send")]
    public async Task<IActionResult> SendMessage([FromBody] SendMessageRequest request)
    {
        var userId = GetUserId();

        // Find active session for user
        var session = await _context.WhatsAppSessions
            .Where(s => s.UserId == userId && s.Status == "connected")
            .OrderByDescending(s => s.ConnectedAt)
            .FirstOrDefaultAsync();

        if (session == null)
            return BadRequest(new { message = "No connected WhatsApp session found. Create a session first." });

        // Send message via WhatsApp service
        try
        {
            var result = await _whatsappService.SendMessageAsync(session.SessionId, request.To, request.Body);
            return Ok(new
            {
                success = true,
                sessionId = session.SessionId,
                to = request.To,
                message = request.Body,
                sentAt = DateTime.UtcNow
            });
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { message = "Failed to send message", error = ex.Message });
        }
    }

    /// <summary>
    /// Send media (image/video/document) via WhatsApp
    /// </summary>
    [HttpPost("whatsapp/messages/send-media")]
    public async Task<IActionResult> SendMedia([FromBody] SendMediaRequest request)
    {
        var userId = GetUserId();

        var session = await _context.WhatsAppSessions
            .Where(s => s.UserId == userId && s.Status == "connected")
            .OrderByDescending(s => s.ConnectedAt)
            .FirstOrDefaultAsync();

        if (session == null)
            return BadRequest(new { message = "No connected WhatsApp session found" });

        try
        {
            var result = await _whatsappService.SendMediaAsync(
                session.SessionId,
                request.To,
                request.MediaUrl,
                request.Caption);

            return Ok(new
            {
                success = true,
                sessionId = session.SessionId,
                to = request.To,
                mediaUrl = request.MediaUrl,
                sentAt = DateTime.UtcNow
            });
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { message = "Failed to send media", error = ex.Message });
        }
    }

    /// <summary>
    /// Delete a WhatsApp session
    /// </summary>
    [HttpDelete("whatsapp/sessions/{sessionId}")]
    public async Task<IActionResult> DeleteSession(string sessionId)
    {
        var userId = GetUserId();
        var session = await _context.WhatsAppSessions
            .FirstOrDefaultAsync(s => s.SessionId == sessionId && s.UserId == userId);

        if (session == null)
            return NotFound(new { message = "Session not found" });

        try
        {
            await _whatsappService.DisconnectSessionAsync(sessionId);
            _context.WhatsAppSessions.Remove(session);
            await _context.SaveChangesAsync();

            return Ok(new { message = "Session deleted successfully" });
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { message = "Failed to delete session", error = ex.Message });
        }
    }

    /// <summary>
    /// Check if a phone number is registered on WhatsApp
    /// </summary>
    [HttpGet("whatsapp/check-number")]
    public async Task<IActionResult> CheckNumber([FromQuery] string phoneNumber)
    {
        var userId = GetUserId();

        var session = await _context.WhatsAppSessions
            .Where(s => s.UserId == userId && s.Status == "connected")
            .OrderByDescending(s => s.ConnectedAt)
            .FirstOrDefaultAsync();

        if (session == null)
            return BadRequest(new { message = "No connected WhatsApp session found" });

        try
        {
            var result = await _whatsappService.CheckNumberStatusAsync(session.SessionId, phoneNumber);
            return Ok(result);
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { message = "Failed to check number", error = ex.Message });
        }
    }

    /// <summary>
    /// Get messages from a specific chat
    /// </summary>
    [HttpGet("whatsapp/messages")]
    public async Task<IActionResult> GetMessages([FromQuery] string chatId, [FromQuery] int limit = 50)
    {
        var userId = GetUserId();

        var session = await _context.WhatsAppSessions
            .Where(s => s.UserId == userId && s.Status == "connected")
            .OrderByDescending(s => s.ConnectedAt)
            .FirstOrDefaultAsync();

        if (session == null)
            return BadRequest(new { message = "No connected WhatsApp session found" });

        try
        {
            var messages = await _whatsappService.GetMessagesAsync(session.SessionId, chatId, limit);
            return Ok(messages ?? new List<WhatsAppMessage>());
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { message = "Failed to get messages", error = ex.Message });
        }
    }

    /// <summary>
    /// Get all chats
    /// </summary>
    [HttpGet("whatsapp/chats")]
    public async Task<IActionResult> GetChats()
    {
        var userId = GetUserId();

        var session = await _context.WhatsAppSessions
            .Where(s => s.UserId == userId && s.Status == "connected")
            .OrderByDescending(s => s.ConnectedAt)
            .FirstOrDefaultAsync();

        if (session == null)
            return BadRequest(new { message = "No connected WhatsApp session found" });

        try
        {
            var chats = await _whatsappService.GetChatsAsync(session.SessionId);
            return Ok(chats ?? new List<object>());
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { message = "Failed to get chats", error = ex.Message });
        }
    }

    /// <summary>
    /// Get all contacts
    /// </summary>
    [HttpGet("whatsapp/contacts")]
    public async Task<IActionResult> GetContacts()
    {
        var userId = GetUserId();

        var session = await _context.WhatsAppSessions
            .Where(s => s.UserId == userId && s.Status == "connected")
            .OrderByDescending(s => s.ConnectedAt)
            .FirstOrDefaultAsync();

        if (session == null)
            return BadRequest(new { message = "No connected WhatsApp session found" });

        try
        {
            var contacts = await _whatsappService.GetContactsAsync(session.SessionId);
            return Ok(contacts ?? new List<WhatsAppContact>());
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { message = "Failed to get contacts", error = ex.Message });
        }
    }
}
