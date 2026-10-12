using System.Diagnostics;

namespace WhatsAppBridge.API.Services.Engines;

/// <summary>
/// Engine that drives the REAL WhatsApp Web client in a headless Chromium via
/// whatsapp-web.js (Puppeteer) — one Node.js sidecar process (and one browser) per
/// session. Because the traffic comes from an authentic WA Web client, this engine has
/// no protocol-fingerprint ban vector (the failure mode that keeps hitting Dawa/Baileys);
/// remaining risks are behavioral (volume, datacenter IP, frequent re-pairing) and
/// upstream WA Web UI changes breaking whatsapp-web.js.
///
/// The sidecar speaks the exact same stdio protocol as baileys-sidecar; the shared
/// client lives in <see cref="SidecarEngine"/>. Credentials live in
/// {sessionDir}/waweb-auth/ (a persistent Chromium profile, LocalAuth clientId "bridge")
/// and are not interchangeable with Dawa or Baileys creds — first connect needs a QR re-pair.
///
/// Config: WhatsApp:WaWeb:NodePath, WhatsApp:WaWeb:SidecarScript and
/// WhatsApp:WaWeb:ChromePath (forwarded to the sidecar as WAWEB_CHROME_PATH so Puppeteer
/// uses an installed Chrome/Chromium instead of downloading its own).
/// </summary>
public sealed class WaWebEngine : SidecarEngine
{
    private readonly string? _chromePath;

    public WaWebEngine(string sessionDirectory, IConfiguration configuration, ILoggerFactory loggerFactory)
        : base(sessionDirectory,
            loggerFactory.CreateLogger<WaWebEngine>(),
            engineName: "waweb",
            sidecarFolderName: "waweb-sidecar",
            nodePath: configuration["WhatsApp:WaWeb:NodePath"] ?? "node",
            configuredScriptPath: configuration["WhatsApp:WaWeb:SidecarScript"])
    {
        _chromePath = configuration["WhatsApp:WaWeb:ChromePath"];
    }

    /// <summary>
    /// LocalAuth persists the paired browser profile under waweb-auth/session-bridge/.
    /// A non-empty profile directory means a pairing happened on this engine.
    /// </summary>
    public override bool HasSavedSession => HasWaWebProfile(SessionDirectory);

    internal static bool HasWaWebProfile(string sessionDirectory)
    {
        var profileDir = Path.Combine(sessionDirectory, "waweb-auth", "session-bridge");
        try { return Directory.Exists(profileDir) && Directory.EnumerateFileSystemEntries(profileDir).Any(); }
        catch { return false; }
    }

    protected override void ConfigureSidecarProcess(ProcessStartInfo psi)
    {
        if (!string.IsNullOrWhiteSpace(_chromePath))
            psi.Environment["WAWEB_CHROME_PATH"] = _chromePath;
    }
}
