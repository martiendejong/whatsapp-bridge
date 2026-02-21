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

    public ApiConnectionsController(AppDbContext context, AuthService authService, EncryptionService encryptionService)
    {
        _context = context;
        _authService = authService;
        _encryptionService = encryptionService;
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
}

public record CreateConnectionRequest(string Name);
