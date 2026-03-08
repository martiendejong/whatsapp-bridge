using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;
using WhatsAppBridge.API.Data;
using WhatsAppBridge.API.Services;

namespace WhatsAppBridge.API.Controllers;

[Authorize]
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

    [HttpPost("sessions/{sessionId}/test")]
    public async Task<IActionResult> TestSession(string sessionId)
    {
        var userId = GetUserId();
        var session = await _context.WhatsAppSessions
            .FirstOrDefaultAsync(s => s.SessionId == sessionId && s.UserId == userId);

        if (session == null)
            return NotFound(new { success = false, message = "Session not found" });

        if (session.Status != "connected")
        {
            return Ok(new
            {
                success = false,
                message = $"WhatsApp session is {session.Status}. Please scan the QR code to connect.",
                sessionStatus = session.Status
            });
        }

        // Test the actual WhatsApp connection by trying to get contacts
        try
        {
            var contacts = await _whatsappService.GetContactsAsync(sessionId);

            if (contacts != null)
            {
                // Update last seen timestamp
                session.LastSeenAt = DateTime.UtcNow;
                await _context.SaveChangesAsync();

                return Ok(new
                {
                    success = true,
                    message = "WhatsApp connection is working! Your QR code scan is still active.",
                    sessionStatus = "connected",
                    phoneNumber = session.PhoneNumber != null
                        ? _encryptionService.Decrypt(session.PhoneNumber)
                        : null,
                    connectedAt = session.ConnectedAt,
                    lastSeenAt = session.LastSeenAt
                });
            }
            else
            {
                // WhatsApp service returned null - session might be disconnected
                session.Status = "disconnected";
                await _context.SaveChangesAsync();

                return Ok(new
                {
                    success = false,
                    message = "WhatsApp session appears to be disconnected. Please scan the QR code again.",
                    sessionStatus = "disconnected"
                });
            }
        }
        catch (Exception ex)
        {
            return Ok(new
            {
                success = false,
                message = "Failed to test WhatsApp connection. The session may be disconnected or the WhatsApp service is unavailable.",
                sessionStatus = "error",
                error = ex.Message
            });
        }
    }
}
