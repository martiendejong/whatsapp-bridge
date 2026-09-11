using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Configuration;

namespace WhatsAppBridge.API.Services;

// ─────────────────────────────────────────────────────────────────────────────
// Prospergenics Vault configuration provider (task 3299).
//
// Ported from jengo-agi's Services/VaultConfiguration.cs (see jengo-agi's
// docs/vault-config.md) with two review corrections from jengo-agi PR #190
// that must NOT be optimized away:
//
//   1. Only an exact HTTP 200 is accepted, never `IsSuccessStatusCode`. A
//      credential behind `RequiresApproval` answers 202 with an
//      approval-pending envelope — `IsSuccessStatusCode` treats 202 as
//      success and would let that pending envelope leak into config as if it
//      were the real secret.
//   2. This provider loads ONCE, during configuration build, and never
//      reloads. Rotating a vault credential takes effect only after the next
//      process restart/app-pool recycle — this is documented behavior, not a
//      bug (see README.md "Secrets & Vault Rotation").
//
// ADDITIVE: added LAST (see AddProspergenicsVault below), so a successful
// vault fetch overrides whatever appsettings.json/appsettings.Production.json/
// appsettings.Local.json/env supplied. Fully fail-safe: if the vault is
// disabled, has no bootstrap key, has no mappings, or is unreachable, the app
// still starts and every mapped ConfigKey simply keeps its earlier value
// (fail-safe, NOT fail-closed, on boot).
//
// SECURITY: the ONLY server-side secret this needs is the bootstrap
// Vault:ApiKey (env VAULT__APIKEY or appsettings.Production.json — never
// committed). Fetched secret VALUES are never logged, only ConfigKey names.
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// One credential-to-config mapping. The vault credential identified by
/// <see cref="CredentialId"/> (in <see cref="ProjectId"/>, or the top-level
/// <c>Vault:ProjectId</c> when this is null) is fetched and its
/// <c>password</c> field is written to <see cref="ConfigKey"/>.
/// </summary>
public sealed class VaultMapping
{
    /// <summary>Hierarchical config key, e.g. "InboundWebhook:ApiKey".</summary>
    public string ConfigKey { get; set; } = "";

    /// <summary>Vault credential id to fetch.</summary>
    public int CredentialId { get; set; }

    /// <summary>Optional per-mapping project id override; falls back to the top-level ProjectId.</summary>
    public int? ProjectId { get; set; }
}

/// <summary>Options for the Prospergenics vault configuration source.</summary>
public sealed class VaultOptions
{
    public bool Enabled { get; set; } = false;
    public string BaseUrl { get; set; } = "https://vault.prospergenics.com";

    /// <summary>Bootstrap API key. MUST come from env (VAULT__APIKEY) or appsettings.Production.json — never committed.</summary>
    public string ApiKey { get; set; } = "";
    public int ProjectId { get; set; }
    public List<VaultMapping> Mappings { get; set; } = new();

    /// <summary>Bind a <see cref="VaultOptions"/> from an already-built configuration's "Vault" section.</summary>
    public static VaultOptions FromConfiguration(IConfiguration configuration)
    {
        var options = new VaultOptions();
        configuration.GetSection("Vault").Bind(options);
        return options;
    }
}

/// <summary>
/// Pure, network-free interpretation of a vault credential-fetch response. Kept
/// separate from the HTTP transport in <see cref="VaultConfigurationProvider"/>
/// so the 200-only acceptance rule can be unit tested directly against every
/// status/body combination without mocking HttpClient.
/// </summary>
public static class VaultResponseParser
{
    /// <summary>
    /// Returns the credential's password, or null if the response must be
    /// rejected. <paramref name="failureReason"/> is set (non-null) whenever
    /// null is returned, for logging — never contains the secret value.
    /// </summary>
    public static string? ExtractPassword(HttpStatusCode statusCode, string? body, out string? failureReason)
    {
        // Strict: ONLY HTTP 200 is accepted. A RequiresApproval credential answers
        // 202 with an approval-pending envelope — IsSuccessStatusCode(true for
        // 200-299) would let that slip through as if it were the real secret.
        if (statusCode != HttpStatusCode.OK)
        {
            failureReason = $"HTTP {(int)statusCode}";
            return null;
        }

        if (string.IsNullOrEmpty(body))
        {
            failureReason = "empty response body";
            return null;
        }

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(body);
        }
        catch (JsonException)
        {
            failureReason = "unparseable JSON body";
            return null;
        }

        using (doc)
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Object ||
                !doc.RootElement.TryGetProperty("password", out var passwordElement) ||
                passwordElement.ValueKind != JsonValueKind.String)
            {
                failureReason = "no 'password' string field";
                return null;
            }

            var value = passwordElement.GetString();
            if (string.IsNullOrEmpty(value))
            {
                failureReason = "empty password value";
                return null;
            }

            failureReason = null;
            return value;
        }
    }
}

/// <summary>IConfigurationSource for the Prospergenics vault.</summary>
public sealed class VaultConfigurationSource : IConfigurationSource
{
    private readonly VaultOptions _options;

    public VaultConfigurationSource(VaultOptions options)
    {
        _options = options;
    }

    public IConfigurationProvider Build(IConfigurationBuilder builder)
        => new VaultConfigurationProvider(_options);
}

/// <summary>
/// IConfigurationProvider that pulls each mapped credential's password from the
/// vault at load time. Fully fail-safe: never throws on network/HTTP/parse
/// failure; a failed fetch simply leaves the earlier sources' value in place.
/// Loads exactly once (during configuration build) and never reloads.
/// </summary>
public sealed class VaultConfigurationProvider : ConfigurationProvider
{
    private readonly VaultOptions _options;

    public VaultConfigurationProvider(VaultOptions options)
    {
        _options = options;
    }

    public override void Load()
    {
        // No-op guards — nothing set, earlier sources' values stand.
        if (!_options.Enabled)
        {
            Log("Vault configuration source disabled (Vault:Enabled=false) — using appsettings/Local/Production/env values as-is.");
            return;
        }

        if (string.IsNullOrWhiteSpace(_options.ApiKey))
        {
            Log("Vault configuration source enabled but no bootstrap Vault:ApiKey provided (set VAULT__APIKEY or appsettings.Production.json) — using appsettings/Local/Production/env values as-is.");
            return;
        }

        if (_options.Mappings.Count == 0)
        {
            Log("Vault configuration source enabled but no mappings configured — nothing to fetch.");
            return;
        }

        using var http = new HttpClient
        {
            BaseAddress = new Uri(_options.BaseUrl.TrimEnd('/') + "/"),
            Timeout = TimeSpan.FromSeconds(10)
        };
        http.DefaultRequestHeaders.Add("X-API-Key", _options.ApiKey);

        var sourced = new List<string>();

        foreach (var mapping in _options.Mappings)
        {
            if (string.IsNullOrWhiteSpace(mapping.ConfigKey) || mapping.CredentialId <= 0)
            {
                Log($"[warn] Skipping invalid vault mapping (ConfigKey='{mapping.ConfigKey}', CredentialId={mapping.CredentialId}).");
                continue;
            }

            var projectId = mapping.ProjectId ?? _options.ProjectId;
            var value = TryFetchPassword(http, projectId, mapping.CredentialId, mapping.ConfigKey);
            if (value is null)
            {
                // Warning already logged inside TryFetchPassword; leave earlier value untouched.
                continue;
            }

            Data[mapping.ConfigKey] = value;
            sourced.Add(mapping.ConfigKey);
        }

        if (sourced.Count > 0)
        {
            // Names only — never values. This is the "startup log shows the count of
            // sourced secrets and never a value" requirement.
            Log($"Sourced {sourced.Count} secret(s) from Prospergenics vault: {string.Join(", ", sourced)}.");
        }
        else
        {
            Log("[warn] Vault configuration source ran but sourced 0 secrets — every mapped credential failed or was skipped. Earlier config values remain in effect.");
        }
    }

    /// <summary>
    /// Fetch a single credential's password. Returns null (and logs a WARNING naming
    /// the ConfigKey and failure reason) on any failure — network, timeout, non-200,
    /// or an unparseable/missing/empty password. Never throws, never logs the value.
    /// </summary>
    private string? TryFetchPassword(HttpClient http, int projectId, int credentialId, string configKey)
    {
        try
        {
            var reason = Uri.EscapeDataString($"whatsapp-bridge startup config bootstrap ({configKey})");
            var path = $"api/projects/{projectId}/credentials/{credentialId}?reason={reason}";
            // Synchronous wait is acceptable here: this runs once during config build,
            // before the host/DI is up.
            using var response = http.GetAsync(path).GetAwaiter().GetResult();
            var body = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();

            var value = VaultResponseParser.ExtractPassword(response.StatusCode, body, out var failureReason);
            if (value is null)
            {
                Log($"[warn] Vault fetch for '{configKey}' (project {projectId}, credential {credentialId}) failed: {failureReason} — keeping prior value.");
                return null;
            }

            return value;
        }
        catch (Exception ex)
        {
            // Message may reference host/timeout but never the secret; safe to log the type + message.
            Log($"[warn] Vault fetch for '{configKey}' (credential {credentialId}) failed: {ex.GetType().Name}: {ex.Message} — keeping prior value.");
            return null;
        }
    }

    // Config providers run before the logging pipeline is built, so we cannot use
    // ILogger. Write to stdout/stderr directly. NEVER pass a secret value here.
    private static void Log(string message)
    {
        var line = $"[VaultConfig] {message}";
        if (message.Contains("[warn]"))
            Console.Error.WriteLine(line);
        else
            Console.WriteLine(line);
    }
}

/// <summary>Extension methods for wiring the Prospergenics vault into the configuration pipeline.</summary>
public static class VaultConfigurationExtensions
{
    /// <summary>
    /// Add the Prospergenics vault as a configuration source. Because the provider
    /// needs its own settings (the "Vault" section) before it can run, this builds a
    /// preliminary IConfiguration from the sources already registered on
    /// <paramref name="builder"/>, reads the Vault section from it, then appends the
    /// vault source LAST so its fetched secrets override the earlier sources.
    /// </summary>
    public static IConfigurationBuilder AddProspergenicsVault(this IConfigurationBuilder builder)
    {
        var preliminary = new ConfigurationBuilder()
            .AddConfiguration(builder.Build())
            .Build();

        var options = VaultOptions.FromConfiguration(preliminary);
        return builder.Add(new VaultConfigurationSource(options));
    }

    /// <summary>Overload accepting explicit options (useful for testing / non-standard wiring).</summary>
    public static IConfigurationBuilder AddProspergenicsVault(this IConfigurationBuilder builder, VaultOptions options)
        => builder.Add(new VaultConfigurationSource(options));
}
