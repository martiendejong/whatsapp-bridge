using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;
using WhatsAppBridge.API.Data;
using WhatsAppBridge.API.Services;

namespace WhatsAppBridge.API.Controllers;

/// <summary>
/// Admin-only bridge configuration. Role "Admin" comes from the JWT (Users.IsAdmin,
/// see AuthService) — regular users get 403 here.
/// </summary>
[ApiController]
[Route("api/admin")]
[Authorize(Roles = "Admin")]
public class AdminController : ControllerBase
{
    private readonly EngineSettingsService _engineSettings;
    private readonly WhatsAppBridgeService _whatsappService;
    private readonly IConfiguration _configuration;
    private readonly ILogger<AdminController> _logger;
    private readonly AppDbContext _context;

    public AdminController(EngineSettingsService engineSettings, WhatsAppBridgeService whatsappService,
        IConfiguration configuration, ILogger<AdminController> logger, AppDbContext context)
    {
        _engineSettings = engineSettings;
        _whatsappService = whatsappService;
        _configuration = configuration;
        _logger = logger;
        _context = context;
    }

    private int GetUserId()
    {
        var claim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        return int.Parse(claim!);
    }

    // ── User management ──────────────────────────────────────────────────────

    [HttpGet("users")]
    public async Task<IActionResult> GetUsers()
    {
        var users = await _context.Users
            .OrderBy(u => u.CreatedAt)
            .Select(u => new
            {
                u.Id,
                u.Email,
                u.IsAdmin,
                u.IsActive,
                u.CreatedAt,
                u.LastLoginAt,
            })
            .ToListAsync();

        return Ok(users);
    }

    [HttpPut("users/{id}/toggle-admin")]
    public async Task<IActionResult> ToggleAdmin(int id)
    {
        var user = await _context.Users.FindAsync(id);
        if (user == null) return NotFound();

        // Prevent an admin from removing their own admin status
        if (user.Id == GetUserId() && user.IsAdmin)
            return BadRequest(new { error = "You cannot remove your own admin status." });

        user.IsAdmin = !user.IsAdmin;
        await _context.SaveChangesAsync();

        _logger.LogInformation("Admin {Actor} set user {Target} IsAdmin={IsAdmin}",
            User.Identity?.Name, user.Email, user.IsAdmin);

        return Ok(new { user.Id, user.Email, user.IsAdmin, user.IsActive });
    }

    [HttpPut("users/{id}/toggle-active")]
    public async Task<IActionResult> ToggleActive(int id)
    {
        var user = await _context.Users.FindAsync(id);
        if (user == null) return NotFound();

        if (user.Id == GetUserId())
            return BadRequest(new { error = "You cannot deactivate your own account." });

        user.IsActive = !user.IsActive;
        await _context.SaveChangesAsync();

        _logger.LogInformation("Admin {Actor} set user {Target} IsActive={IsActive}",
            User.Identity?.Name, user.Email, user.IsActive);

        return Ok(new { user.Id, user.Email, user.IsAdmin, user.IsActive });
    }

    /// <summary>Current WhatsApp engine setting plus per-session live engine state.</summary>
    [HttpGet("engine")]
    public async Task<IActionResult> GetEngine()
    {
        var engine = await _engineSettings.GetEngineAsync();
        return Ok(new
        {
            engine,
            availableEngines = EngineSettingsService.AvailableEngines,
            activeSessions = _whatsappService.GetActiveSessionEngines(),
        });
    }

    public record SetEngineRequest(string Engine, bool RestartSessions = false);

    /// <summary>
    /// Switches the WhatsApp engine ("dawa", "baileys" or "waweb"). The new engine applies to
    /// every session (re)connect from now on; with RestartSessions=true, all active sessions
    /// are reconnected immediately. Engine credentials are not interchangeable — a session's
    /// first connect on another engine requires scanning a new QR code (which links an extra
    /// device; avoid rapid back-and-forth switching, that is a ban footprint).
    /// </summary>
    [HttpPut("engine")]
    public async Task<IActionResult> SetEngine([FromBody] SetEngineRequest request)
    {
        if (!EngineSettingsService.IsValidEngine(request.Engine))
            return BadRequest(new
            {
                error = $"Unknown engine '{request.Engine}'",
                availableEngines = EngineSettingsService.AvailableEngines,
            });

        var previous = await _engineSettings.GetEngineAsync();
        await _engineSettings.SetEngineAsync(request.Engine);
        var engine = await _engineSettings.GetEngineAsync();
        _logger.LogInformation("Admin {User} switched WhatsApp engine {Previous} → {Engine} (restartSessions={Restart})",
            User.Identity?.Name ?? "?", previous, engine, request.RestartSessions);

        int? restarted = null;
        if (request.RestartSessions)
            restarted = await _whatsappService.RestartAllSessionsAsync();

        return Ok(new
        {
            engine,
            previous,
            restartedSessions = restarted,
            note = engine == previous
                ? "Engine unchanged."
                : (request.RestartSessions
                    ? "Sessions reconnected on the new engine. Sessions without saved credentials for this engine stay disconnected until their QR is re-scanned."
                    : "New engine applies on the next (re)connect of each session. Use restartSessions=true to apply immediately."),
        });
    }
}
