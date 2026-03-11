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
