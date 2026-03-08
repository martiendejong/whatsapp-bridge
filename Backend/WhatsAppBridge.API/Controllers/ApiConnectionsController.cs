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
public class ApiConnectionsController : ControllerBase
{
    private readonly AppDbContext _context;
    private readonly AuthService _authService;
    private readonly EncryptionService _encryptionService;
    private readonly WhatsAppBridgeService _whatsappService;

    public ApiConnectionsController(
        AppDbContext context,
        AuthService authService,
        EncryptionService encryptionService,
        WhatsAppBridgeService whatsappService)
    {
        _context = context;
        _authService = authService;
        _encryptionService = encryptionService;
        _whatsappService = whatsappService;
    }

    private int GetUserId()
    {
        var userIdClaim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        return int.Parse(userIdClaim!);
    }

    [HttpGet]
    public async Task<IActionResult> GetAll()
    {
        var userId = GetUserId();
        var connections = await _context.ApiConnections
            .Where(c => c.UserId == userId)
            .Select(c => new
            {
                c.Id,
                c.Name,
                Token = _encryptionService.Decrypt(c.Token), // Decrypt for display
                c.CreatedAt,
                c.LastUsedAt,
                c.IsActive
            })
            .ToListAsync();

        return Ok(connections);
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateConnectionRequest request)
    {
        var userId = GetUserId();
        var token = await _authService.GenerateApiToken(userId, request.Name);

        // Encrypt token if encryption is enabled
        var connection = await _context.ApiConnections
            .FirstOrDefaultAsync(c => c.Token == token);

        if (connection != null && _encryptionService.IsEncryptionEnabled)
        {
            connection.Token = _encryptionService.Encrypt(token);
            await _context.SaveChangesAsync();
        }

        return Ok(new
        {
            id = connection?.Id,
            name = request.Name,
            token = token, // Return plain token once (user must save it)
            createdAt = connection?.CreatedAt
        });
    }

    [HttpDelete("{id}")]
    public async Task<IActionResult> Delete(int id)
    {
        var userId = GetUserId();
        var connection = await _context.ApiConnections
            .FirstOrDefaultAsync(c => c.Id == id && c.UserId == userId);

        if (connection == null)
            return NotFound();

        _context.ApiConnections.Remove(connection);
        await _context.SaveChangesAsync();

        return NoContent();
    }

    [HttpPatch("{id}/toggle")]
    public async Task<IActionResult> Toggle(int id)
    {
        var userId = GetUserId();
        var connection = await _context.ApiConnections
            .FirstOrDefaultAsync(c => c.Id == id && c.UserId == userId);

        if (connection == null)
            return NotFound();

        connection.IsActive = !connection.IsActive;
        await _context.SaveChangesAsync();

        return Ok(new { isActive = connection.IsActive });
    }

    [HttpPost("{id}/test")]
    public async Task<IActionResult> TestConnection(int id)
    {
        var userId = GetUserId();

        // Verify the API connection exists and is active
        var connection = await _context.ApiConnections
            .FirstOrDefaultAsync(c => c.Id == id && c.UserId == userId);

        if (connection == null)
            return NotFound(new { success = false, message = "Connection not found" });

        if (!connection.IsActive)
            return Ok(new { success = false, message = "Connection is inactive. Enable it first." });

        // Find the user's WhatsApp session
        var whatsappSession = await _context.WhatsAppSessions
            .Where(s => s.UserId == userId)
            .OrderByDescending(s => s.ConnectedAt ?? s.CreatedAt)
            .FirstOrDefaultAsync();

        if (whatsappSession == null)
        {
            return Ok(new
            {
                success = false,
                message = "No WhatsApp session found. Please scan a QR code first.",
                sessionStatus = "not_found"
            });
        }

        if (whatsappSession.Status != "connected")
        {
            return Ok(new
            {
                success = false,
                message = $"WhatsApp session is {whatsappSession.Status}. Please reconnect by scanning the QR code.",
                sessionStatus = whatsappSession.Status,
                sessionId = whatsappSession.SessionId
            });
        }

        // Test the actual WhatsApp connection by trying to get session info
        try
        {
            // Try to get contacts as a test - if this works, the session is valid
            var contacts = await _whatsappService.GetContactsAsync(whatsappSession.SessionId);

            if (contacts != null)
            {
                // Update last used timestamps
                connection.LastUsedAt = DateTime.UtcNow;
                whatsappSession.LastSeenAt = DateTime.UtcNow;
                await _context.SaveChangesAsync();

                return Ok(new
                {
                    success = true,
                    message = "WhatsApp connection is working! Your QR code scan is still active.",
                    sessionStatus = "connected",
                    sessionId = whatsappSession.SessionId,
                    phoneNumber = whatsappSession.PhoneNumber != null
                        ? _encryptionService.Decrypt(whatsappSession.PhoneNumber)
                        : null,
                    connectedAt = whatsappSession.ConnectedAt,
                    lastSeenAt = whatsappSession.LastSeenAt
                });
            }
            else
            {
                // WhatsApp service returned null - session might be disconnected
                whatsappSession.Status = "disconnected";
                await _context.SaveChangesAsync();

                return Ok(new
                {
                    success = false,
                    message = "WhatsApp session appears to be disconnected. Please scan the QR code again.",
                    sessionStatus = "disconnected",
                    sessionId = whatsappSession.SessionId
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

public record CreateConnectionRequest(string Name);
