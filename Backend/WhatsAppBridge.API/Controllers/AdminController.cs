using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
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

    public AdminController(EngineSettingsService engineSettings, WhatsAppBridgeService whatsappService,
        IConfiguration configuration, ILogger<AdminController> logger)
    {
        _engineSettings = engineSettings;
        _whatsappService = whatsappService;
        _configuration = configuration;
        _logger = logger;
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
    /// Switches the WhatsApp engine ("dawa" or "baileys"). The new engine applies to every
    /// session (re)connect from now on; with RestartSessions=true, all active sessions are
    /// reconnected immediately. Dawa and Baileys credentials are not interchangeable — a
    /// session's first connect on the other engine requires scanning a new QR code (which
    /// links an extra device; avoid rapid back-and-forth switching, that is a ban footprint).
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
