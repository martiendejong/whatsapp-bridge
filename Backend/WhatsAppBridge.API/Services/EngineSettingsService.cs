using Microsoft.EntityFrameworkCore;
using WhatsAppBridge.API.Data;

namespace WhatsAppBridge.API.Services;

/// <summary>
/// Persists the admin-selected WhatsApp engine ("dawa" or "baileys") in the AppSettings
/// key/value table (self-healed in Program.cs). Falls back to WhatsApp:Engine from
/// configuration, then to "dawa". Registered as singleton; the value is cached and
/// only re-read after a write, so the hot path (every session create/restore) is free.
/// </summary>
public class EngineSettingsService
{
    public const string DawaEngine = "dawa";
    public const string BaileysEngine = "baileys";
    public static readonly string[] AvailableEngines = { DawaEngine, BaileysEngine };

    private const string SettingKey = "WhatsAppEngine";

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IConfiguration _configuration;
    private readonly ILogger<EngineSettingsService> _logger;
    private string? _cached;

    public EngineSettingsService(IServiceScopeFactory scopeFactory, IConfiguration configuration,
        ILogger<EngineSettingsService> logger)
    {
        _scopeFactory = scopeFactory;
        _configuration = configuration;
        _logger = logger;
    }

    public static bool IsValidEngine(string? engine)
        => engine != null && AvailableEngines.Contains(engine, StringComparer.OrdinalIgnoreCase);

    public async Task<string> GetEngineAsync()
    {
        if (_cached != null) return _cached;

        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var stored = (await db.Database
                .SqlQueryRaw<string>("SELECT Value FROM AppSettings WHERE Key = 'WhatsAppEngine'")
                .ToListAsync()).FirstOrDefault();
            if (IsValidEngine(stored))
                return _cached = stored!.ToLowerInvariant();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not read WhatsAppEngine setting — falling back to config/default");
        }

        var configured = _configuration["WhatsApp:Engine"];
        return _cached = IsValidEngine(configured) ? configured!.ToLowerInvariant() : DawaEngine;
    }

    public async Task SetEngineAsync(string engine)
    {
        if (!IsValidEngine(engine))
            throw new ArgumentException($"Unknown engine '{engine}'. Valid: {string.Join(", ", AvailableEngines)}");

        engine = engine.ToLowerInvariant();
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"INSERT INTO AppSettings (Key, Value) VALUES ({SettingKey}, {engine}) ON CONFLICT(Key) DO UPDATE SET Value = {engine}");
        _cached = engine;
        _logger.LogInformation("WhatsApp engine setting changed to '{Engine}'", engine);
    }
}
