using Microsoft.EntityFrameworkCore;
using WhatsAppBridge.API.Data;

namespace WhatsAppBridge.API.Services;

/// <summary>
/// Meta WhatsApp Cloud API credentials, kept in the AppSettings key/value table (same store as
/// the engine switch) so they can be rotated through the admin API without touching
/// appsettings or restarting. Falls back to configuration keys Meta:AccessToken / Meta:AppSecret /
/// Meta:WebhookVerifyToken / Meta:GraphVersion. Singleton; values are cached until written.
/// </summary>
public class MetaCloudSettings
{
    public const string KeyAccessToken = "MetaAccessToken";
    public const string KeyAppSecret = "MetaAppSecret";
    public const string KeyVerifyToken = "MetaWebhookVerifyToken";

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IConfiguration _configuration;
    private readonly ILogger<MetaCloudSettings> _logger;
    private readonly Dictionary<string, string?> _cache = new();
    private readonly object _lock = new();

    public MetaCloudSettings(IServiceScopeFactory scopeFactory, IConfiguration configuration,
        ILogger<MetaCloudSettings> logger)
    {
        _scopeFactory = scopeFactory;
        _configuration = configuration;
        _logger = logger;
    }

    public string GraphVersion => _configuration["Meta:GraphVersion"] ?? "v21.0";

    public Task<string?> GetAccessTokenAsync() => GetAsync(KeyAccessToken, "Meta:AccessToken");
    public Task<string?> GetAppSecretAsync() => GetAsync(KeyAppSecret, "Meta:AppSecret");
    public Task<string?> GetVerifyTokenAsync() => GetAsync(KeyVerifyToken, "Meta:WebhookVerifyToken");

    private async Task<string?> GetAsync(string key, string configKey)
    {
        lock (_lock)
        {
            if (_cache.TryGetValue(key, out var cached)) return cached;
        }

        string? value = null;
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            value = (await db.Database
                .SqlQuery<string>($"SELECT Value FROM AppSettings WHERE Key = {key}")
                .ToListAsync()).FirstOrDefault();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not read {Key} from AppSettings", key);
        }

        if (string.IsNullOrWhiteSpace(value)) value = _configuration[configKey];
        if (string.IsNullOrWhiteSpace(value)) value = null;

        lock (_lock) { _cache[key] = value; }
        return value;
    }

    public async Task SetAsync(string key, string value)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"INSERT INTO AppSettings (Key, Value) VALUES ({key}, {value}) ON CONFLICT(Key) DO UPDATE SET Value = {value}");
        lock (_lock) { _cache[key] = value; }
        _logger.LogInformation("Meta setting {Key} updated", key);
    }

    /// <summary>Which settings are present, never the values.</summary>
    public async Task<object> DescribeAsync() => new
    {
        graphVersion = GraphVersion,
        hasAccessToken = await GetAccessTokenAsync() != null,
        hasAppSecret = await GetAppSecretAsync() != null,
        hasVerifyToken = await GetVerifyTokenAsync() != null,
    };
}

/// <summary>
/// Routes Meta webhook events to the live Meta engine that owns the phone-number-id.
/// Singleton; engines register on connect and unregister on dispose.
/// </summary>
public class MetaWebhookRouter
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, Engines.MetaCloudEngine> _engines = new();

    public void Register(string phoneNumberId, Engines.MetaCloudEngine engine) => _engines[phoneNumberId] = engine;

    public void Unregister(string phoneNumberId, Engines.MetaCloudEngine engine)
    {
        if (_engines.TryGetValue(phoneNumberId, out var current) && ReferenceEquals(current, engine))
            _engines.TryRemove(phoneNumberId, out _);
    }

    public Engines.MetaCloudEngine? Find(string phoneNumberId) =>
        _engines.TryGetValue(phoneNumberId, out var e) ? e : null;

    public IReadOnlyCollection<string> RegisteredPhoneNumberIds => _engines.Keys.ToList();
}
