using Dawa;
using Microsoft.EntityFrameworkCore;
using System.Collections.Concurrent;
using WhatsAppBridge.API.Controllers;
using WhatsAppBridge.API.Data;
using WhatsAppBridge.API.Models;

namespace WhatsAppBridge.API.Services;

/// <summary>
/// Manages per-user WhatsApp sessions using Dawa (C# native client).
/// Replaces the former Node.js/Baileys HTTP bridge.
/// Registered as Singleton — holds long-lived WhatsAppClient instances.
/// </summary>
public class WhatsAppBridgeService : IAsyncDisposable
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IConfiguration _configuration;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<WhatsAppBridgeService> _logger;

    // One Dawa client per sessionId
    private readonly ConcurrentDictionary<string, WhatsAppClient> _clients = new();

    public WhatsAppBridgeService(
        IServiceScopeFactory scopeFactory,
        IConfiguration configuration,
        ILoggerFactory loggerFactory,
        ILogger<WhatsAppBridgeService> logger)
    {
        _scopeFactory = scopeFactory;
        _configuration = configuration;
        _loggerFactory = loggerFactory;
        _logger = logger;
    }

    // ─── Session lifecycle ─────────────────────────────────────────────────────

    /// <summary>
    /// Creates a Dawa client for the session and waits up to 30s for a QR code.
    /// Returns the QR string on success, null if it times out (QR will arrive later via event).
    /// </summary>
    public async Task<string?> InitializeSessionAsync(string sessionId)
    {
        // Clean up any existing client for this session
        if (_clients.TryRemove(sessionId, out var existing))
            await existing.DisposeAsync();

        var sessionsRoot = _configuration["WhatsApp:SessionsDirectory"]
            ?? Path.Combine(AppContext.BaseDirectory, "whatsapp-sessions");
        var sessionDir = Path.Combine(sessionsRoot, sessionId);

        var client = WhatsAppClient.Create(sessionDir, _loggerFactory);
        _clients[sessionId] = client;

        // Wire events — these fire from background threads
        var qrTcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var everConnected = false;

        client.QRCodeReceived += (_, qr) =>
        {
            _logger.LogInformation("QR received for session {SessionId}", sessionId);
            qrTcs.TrySetResult(qr);
            _ = UpdateSessionAsync(sessionId, s => s.QrCode = qr);
        };

        client.Connected += (_, _) =>
        {
            everConnected = true;
            _logger.LogInformation("Session {SessionId} connected as {Jid}", sessionId, client.MyJid);
            _ = UpdateSessionAsync(sessionId, s =>
            {
                s.Status = "connected";
                s.ConnectedAt = DateTime.UtcNow;
                s.QrCode = null; // QR no longer needed
                // Extract phone number from JID: "31633984381:20@s.whatsapp.net" → "31633984381"
                if (client.MyJid != null)
                {
                    var phone = client.MyJid.Split('@')[0].Split(':')[0];
                    s.PhoneNumber = phone;
                }
            });
        };

        client.Disconnected += (_, _) =>
        {
            // If we never reached "connected", the server rejected us (e.g. rate limiting, bad payload).
            // Mark as "failed" so the frontend stops polling instead of hanging on "qr_pending".
            var status = everConnected ? "disconnected" : "failed";
            _logger.LogInformation("Session {SessionId} disconnected (status → {Status})", sessionId, status);
            qrTcs.TrySetException(new Exception($"Connection rejected by server (status: {status})"));
            _ = UpdateSessionAsync(sessionId, s =>
            {
                s.Status = status;
                s.LastSeenAt = DateTime.UtcNow;
            });
        };

        // Start connecting in background
        _ = client.ConnectAsync(CancellationToken.None);

        // If session is already saved, client connects directly (no QR).
        // If not, QR fires within ~2s. If server rejects (rate limit, bad payload),
        // qrTcs completes with exception so we don't hang for 30s.
        var winner = await Task.WhenAny(qrTcs.Task, Task.Delay(TimeSpan.FromSeconds(30)));

        if (winner == qrTcs.Task)
        {
            try { return await qrTcs.Task; }
            catch { return null; } // Server rejected — status already updated to "failed" via Disconnected event
        }
        return null; // Timed out waiting for QR
    }

    /// <summary>
    /// Silently restores a saved session on startup without waiting for QR.
    /// Only works if creds.json exists in the session directory.
    /// </summary>
    public Task RestoreSessionAsync(string sessionId)
    {
        var sessionsRoot = _configuration["WhatsApp:SessionsDirectory"]
            ?? Path.Combine(AppContext.BaseDirectory, "whatsapp-sessions");
        var sessionDir = Path.Combine(sessionsRoot, sessionId);

        if (!Directory.Exists(sessionDir))
            return Task.CompletedTask;

        if (_clients.ContainsKey(sessionId))
            return Task.CompletedTask;

        var client = WhatsAppClient.Create(sessionDir, _loggerFactory);
        _clients[sessionId] = client;

        client.QRCodeReceived += (_, qr) =>
            _ = UpdateSessionAsync(sessionId, s => s.QrCode = qr);

        client.Connected += (_, _) =>
        {
            _logger.LogInformation("Session {SessionId} restored and connected as {Jid}", sessionId, client.MyJid);
            _ = UpdateSessionAsync(sessionId, s =>
            {
                s.Status = "connected";
                s.LastSeenAt = DateTime.UtcNow;
                if (client.MyJid != null && string.IsNullOrEmpty(s.PhoneNumber))
                {
                    s.PhoneNumber = client.MyJid.Split('@')[0].Split(':')[0];
                }
            });
        };

        client.Disconnected += (_, _) =>
            _ = UpdateSessionAsync(sessionId, s =>
            {
                s.Status = "disconnected";
                s.LastSeenAt = DateTime.UtcNow;
            });

        // Fire and forget — reconnects in background
        _ = client.ConnectAsync(CancellationToken.None);
        return Task.CompletedTask;
    }

    public async Task<bool> DisconnectSessionAsync(string sessionId)
    {
        if (_clients.TryRemove(sessionId, out var client))
        {
            await client.DisposeAsync();
            return true;
        }
        return false;
    }

    // ─── Messaging ────────────────────────────────────────────────────────────

    public async Task<object?> SendMessageAsync(string sessionId, string to, string body)
    {
        var client = GetConnectedClient(sessionId);
        await client.SendMessageAsync(to, body, CancellationToken.None);
        return new { success = true };
    }

    public Task<object?> SendMediaAsync(string sessionId, string to, string mediaUrl, string? caption)
    {
        // Media sending not yet implemented in Dawa
        throw new WhatsAppServiceException(WhatsAppError.MessageFailed(
            "Media sending is not yet supported by the Dawa client."));
    }

    // ─── Read operations (not yet implemented in Dawa) ────────────────────────

    public Task<List<WhatsAppMessage>?> GetMessagesAsync(string sessionId, string chatId, int limit)
        => Task.FromResult<List<WhatsAppMessage>?>(new List<WhatsAppMessage>());

    public Task<List<object>?> GetChatsAsync(string sessionId)
        => Task.FromResult<List<object>?>(new List<object>());

    public Task<List<WhatsAppContact>?> GetContactsAsync(string sessionId)
        => Task.FromResult<List<WhatsAppContact>?>(new List<WhatsAppContact>());

    public Task<object?> CheckNumberStatusAsync(string sessionId, string number)
        => Task.FromResult<object?>(new { number, isWhatsApp = true });

    // ─── Helpers ──────────────────────────────────────────────────────────────

    public bool IsSessionConnected(string sessionId)
        => _clients.TryGetValue(sessionId, out var client) && client.IsConnected;

    private WhatsAppClient GetConnectedClient(string sessionId)
    {
        if (!_clients.TryGetValue(sessionId, out var client))
            throw new WhatsAppServiceException(WhatsAppError.SessionNotFound(sessionId));
        if (!client.IsConnected)
            throw new WhatsAppServiceException(WhatsAppError.SessionDisconnected(sessionId));
        return client;
    }

    private async Task UpdateSessionAsync(string sessionId, Action<WhatsAppSession> update)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var session = await db.WhatsAppSessions
                .FirstOrDefaultAsync(s => s.SessionId == sessionId);
            if (session != null)
            {
                update(session);
                await db.SaveChangesAsync();
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to update session {SessionId} in DB", sessionId);
        }
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var client in _clients.Values)
            await client.DisposeAsync();
        _clients.Clear();
    }
}
