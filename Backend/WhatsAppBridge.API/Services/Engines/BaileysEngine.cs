namespace WhatsAppBridge.API.Services.Engines;

/// <summary>
/// Engine backed by the real Baileys library (@whiskeysockets/baileys) running as a
/// Node.js sidecar process — one process per session. The full sidecar protocol client
/// lives in <see cref="SidecarEngine"/>; this class only binds the Baileys specifics.
///
/// Credentials live in {sessionDir}/baileys-auth/ (Baileys multi-file auth state) and are
/// NOT interchangeable with Dawa's creds.json — first connect on this engine needs a QR re-pair.
/// </summary>
public sealed class BaileysEngine : SidecarEngine
{
    public BaileysEngine(string sessionDirectory, IConfiguration configuration, ILoggerFactory loggerFactory)
        : base(sessionDirectory,
            loggerFactory.CreateLogger<BaileysEngine>(),
            engineName: "baileys",
            sidecarFolderName: "baileys-sidecar",
            nodePath: configuration["WhatsApp:Baileys:NodePath"] ?? "node",
            configuredScriptPath: configuration["WhatsApp:Baileys:SidecarScript"])
    {
    }

    public override bool HasSavedSession
        => File.Exists(Path.Combine(SessionDirectory, "baileys-auth", "creds.json"));
}
