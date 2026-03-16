using Dawa.Auth;
using Dawa.Messages;
using Dawa.Models;
using Dawa.Noise;
using Dawa.Transport;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Dawa;

/// <summary>
/// The main Dawa WhatsApp client. Connects to WhatsApp Web and allows
/// sending and receiving messages without a Node.js dependency.
///
/// Usage:
/// <code>
/// var client = WhatsAppClient.Create("./wa-session");
/// client.QRCodeReceived += (_, qr) => Console.WriteLine("Scan: " + qr);
/// client.MessageReceived += (_, msg) => Console.WriteLine(msg);
/// await client.ConnectAsync();
/// await client.WaitUntilConnectedAsync();
/// await client.SendMessageAsync("+31612345678", "Hello from Dawa!");
/// </code>
/// </summary>
public sealed class WhatsAppClient : IAsyncDisposable
{
    private readonly WhatsAppClientOptions _options;
    private readonly ILogger<WhatsAppClient> _logger;
    private readonly SessionStore _sessionStore;

    private FrameSocket? _frameSocket;
    private NoiseProcessor? _noiseProcessor;
    private ConnectionState _state = ConnectionState.Disconnected;
    private AuthState? _authState;
    private CancellationTokenSource? _cts;
    private TaskCompletionSource<bool>? _connectedTcs;

    // Routing info received from WhatsApp edge_routing — used as X-WA-Routing on next connect
    private byte[]? _pendingRoutingInfo;

    // ─── Events ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Fired when a QR code is ready to be scanned.
    /// The string argument is the raw QR code data (pass to a QR renderer).
    /// </summary>
    public event EventHandler<string>? QRCodeReceived;

    /// <summary>Fired when the connection state changes.</summary>
    public event EventHandler<ConnectionState>? ConnectionStateChanged;

    /// <summary>Fired when a new message is received.</summary>
    public event EventHandler<IncomingMessage>? MessageReceived;

    /// <summary>Fired once the session is fully authenticated.</summary>
    public event EventHandler? Connected;

    /// <summary>Fired when the connection is lost.</summary>
    public event EventHandler? Disconnected;

    /// <summary>Fired when a HistorySync blob is received, decrypted, and processed.</summary>
    public event EventHandler<HistorySyncBatch>? HistorySyncReceived;

    // ─── Properties ─────────────────────────────────────────────────────────

    public ConnectionState State => _state;
    public bool IsConnected => _state == ConnectionState.Connected;
    public string? MyJid => _authState?.Me?.Id;

    // ─── Construction ────────────────────────────────────────────────────────

    public WhatsAppClient(WhatsAppClientOptions options, ILogger<WhatsAppClient>? logger = null)
    {
        _options = options;
        _logger = logger ?? NullLogger<WhatsAppClient>.Instance;
        _sessionStore = new SessionStore(options.SessionDirectory);
    }

    /// <summary>
    /// Creates a new WhatsApp client with default options.
    /// </summary>
    /// <param name="sessionDirectory">Directory to store session credentials.</param>
    public static WhatsAppClient Create(string sessionDirectory = "./whatsapp-session")
        => new(new WhatsAppClientOptions { SessionDirectory = sessionDirectory });

    /// <summary>
    /// Creates a client with a logger factory (e.g. from dependency injection).
    /// </summary>
    public static WhatsAppClient Create(string sessionDirectory, ILoggerFactory loggerFactory)
        => new(new WhatsAppClientOptions { SessionDirectory = sessionDirectory },
               loggerFactory.CreateLogger<WhatsAppClient>());

    // ─── Connection ──────────────────────────────────────────────────────────

    /// <summary>
    /// Connects to WhatsApp and starts the authentication flow.
    /// If a session exists it restores it; otherwise fires <see cref="QRCodeReceived"/>.
    /// </summary>
    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        if (_state != ConnectionState.Disconnected)
            throw new InvalidOperationException($"Client is already in state {_state}.");

        SetState(ConnectionState.Connecting);
        _connectedTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        try
        {
            _authState = await _sessionStore.LoadAsync(_cts.Token);

            _frameSocket = new FrameSocket(_logger);
            SetState(ConnectionState.Handshaking);
            await _frameSocket.ConnectAsync(routingInfo: _pendingRoutingInfo, ct: _cts.Token);

            _noiseProcessor = new NoiseProcessor(_frameSocket, _authState, _options, _logger);
            _noiseProcessor.QRCodeGenerated += (_, qr) =>
            {
                SetState(ConnectionState.Authenticating);
                QRCodeReceived?.Invoke(this, qr);
            };
            _noiseProcessor.Authenticated += async (_, auth) =>
            {
                _authState = auth;
                await _sessionStore.SaveAsync(auth, _cts!.Token);
                SetState(ConnectionState.Connected);
                _connectedTcs?.TrySetResult(true);
                Connected?.Invoke(this, EventArgs.Empty);
            };
            _noiseProcessor.MessageReceived += (_, msg) => MessageReceived?.Invoke(this, msg);
            _noiseProcessor.HistorySyncReceived += (_, batch) => HistorySyncReceived?.Invoke(this, batch);

            await _noiseProcessor.PerformHandshakeAsync(_cts.Token);

            // Start background receive loop
            _ = Task.Run(() => RunReceiveLoopAsync(_cts.Token), _cts.Token);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Connection failed.");
            SetState(ConnectionState.Disconnected);
            _connectedTcs?.TrySetException(ex);
            throw;
        }
    }

    /// <summary>
    /// Waits until the client is authenticated and connected.
    /// Throws if connection fails or the timeout is exceeded.
    /// </summary>
    public async Task WaitUntilConnectedAsync(TimeSpan? timeout = null)
    {
        if (_state == ConnectionState.Connected) return;
        if (_connectedTcs == null) throw new InvalidOperationException("Not connecting.");

        var cts = timeout.HasValue
            ? new CancellationTokenSource(timeout.Value)
            : new CancellationTokenSource(TimeSpan.FromMinutes(3));

        using (cts.Token.Register(() => _connectedTcs.TrySetCanceled()))
            await _connectedTcs.Task;
    }

    // ─── Messaging ───────────────────────────────────────────────────────────

    /// <summary>
    /// Sends a text message to a phone number or JID.
    /// </summary>
    /// <param name="to">Phone number ("31612345678") or full JID ("31612345678@s.whatsapp.net").</param>
    /// <param name="text">Message text.</param>
    public async Task SendMessageAsync(string to, string text, CancellationToken cancellationToken = default)
    {
        if (_noiseProcessor == null || _state != ConnectionState.Connected)
            throw new InvalidOperationException("Client is not connected.");

        // Normalize to JID
        var jid = to.Contains('@') ? to : $"{new string(to.Where(char.IsDigit).ToArray())}@s.whatsapp.net";
        await _noiseProcessor.SendTextMessageAsync(jid, text, cancellationToken);
    }

    public Task<List<(string Jid, string Name)>> GetContactsAsync(CancellationToken ct)
    {
        if (_noiseProcessor == null || _state != ConnectionState.Connected)
            throw new InvalidOperationException("Client is not connected.");
        return _noiseProcessor.FetchContactsAsync(ct);
    }

    public Task<List<(string Jid, string Name, bool Archived, bool Pinned)>> GetChatsAsync(CancellationToken ct)
    {
        if (_noiseProcessor == null || _state != ConnectionState.Connected)
            throw new InvalidOperationException("Client is not connected.");
        return _noiseProcessor.FetchChatsAsync(ct);
    }

    public Task<string?> GetProfilePictureAsync(string jid, CancellationToken ct)
    {
        if (_noiseProcessor == null || _state != ConnectionState.Connected)
            throw new InvalidOperationException("Client is not connected.");
        return _noiseProcessor.FetchProfilePictureAsync(jid, ct);
    }

    /// <summary>Returns internal cache state for debugging — thread metadata, LID map, push names.</summary>
    public object GetCacheDebugInfo()
    {
        if (_noiseProcessor == null) return new { error = "not connected" };
        return _noiseProcessor.GetCacheDebugInfo();
    }

    public Task SubscribePresenceAsync(string jid, CancellationToken ct)
    {
        if (_noiseProcessor == null || _state != ConnectionState.Connected)
            throw new InvalidOperationException("Client is not connected.");
        return _noiseProcessor.SubscribePresenceAsync(jid, ct);
    }

    public Dawa.Noise.PresenceInfo? GetPresence(string jid)
    {
        if (_noiseProcessor == null) return null;
        return _noiseProcessor.GetPresence(jid);
    }

    public Task SendReadReceiptAsync(string jid, string messageId, long timestamp, CancellationToken ct)
    {
        if (_noiseProcessor == null || _state != ConnectionState.Connected)
            throw new InvalidOperationException("Client is not connected.");
        return _noiseProcessor.SendReadReceiptAsync(jid, messageId, timestamp, ct);
    }

    public Task SendManualRetryReceiptAsync(string senderJid, string msgId, long timestamp, CancellationToken ct)
    {
        if (_noiseProcessor == null || _state != ConnectionState.Connected)
            throw new InvalidOperationException("Client is not connected.");
        return _noiseProcessor.SendManualRetryReceiptAsync(senderJid, msgId, timestamp, ct);
    }

    public Task<List<Dawa.Messages.IncomingMessage>> FetchMessageHistoryAsync(string jid, int count, CancellationToken ct)
    {
        if (_noiseProcessor == null || _state != ConnectionState.Connected)
            return Task.FromResult(new List<Dawa.Messages.IncomingMessage>());
        return _noiseProcessor.FetchMessageHistoryAsync(jid, count, ct);
    }

    public Task RequestOnDemandHistoryAsync(string chatJid, string? oldestMsgId, bool oldestMsgFromMe, long oldestMsgTimestampMs, int count, CancellationToken ct)
    {
        if (_noiseProcessor == null || _state != ConnectionState.Connected)
            return Task.CompletedTask;
        return _noiseProcessor.RequestOnDemandHistoryAsync(chatJid, oldestMsgId, oldestMsgFromMe, oldestMsgTimestampMs, count, ct);
    }

    public List<string> GetGroupJids()
    {
        if (_noiseProcessor == null) return new List<string>();
        return _noiseProcessor.GetGroupJids();
    }

    public Task<string?> ResolveLidAsync(string lidJid, CancellationToken ct)
    {
        if (_noiseProcessor == null || _state != ConnectionState.Connected)
            return Task.FromResult<string?>(null);
        return _noiseProcessor.ResolveLidAsync(lidJid, ct);
    }

    public Task<Dawa.Noise.GroupMetadata?> FetchGroupMetadataAsync(string groupJid, CancellationToken ct)
    {
        if (_noiseProcessor == null || _state != ConnectionState.Connected)
            throw new InvalidOperationException("Client is not connected.");
        return _noiseProcessor.FetchGroupMetadataAsync(groupJid, ct);
    }

    // ─── Disconnection ───────────────────────────────────────────────────────

    /// <summary>Disconnects from WhatsApp and cleans up resources.</summary>
    public async Task DisconnectAsync()
    {
        _cts?.Cancel();
        if (_frameSocket != null)
        {
            await _frameSocket.DisposeAsync();
            _frameSocket = null;
        }
        _noiseProcessor = null;
        SetState(ConnectionState.Disconnected);
        Disconnected?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Deletes the saved session (forces QR code re-scan on next connect).</summary>
    public void Logout()
    {
        _sessionStore.Delete();
        _logger.LogInformation("Session deleted. QR scan required on next connection.");
    }

    public bool HasSavedSession => _sessionStore.HasSession;

    // ─── Private helpers ─────────────────────────────────────────────────────

    private async Task RunReceiveLoopAsync(CancellationToken ct)
    {
        var shouldReconnect = false;
        try
        {
            await _noiseProcessor!.ReceiveLoopAsync(ct);
        }
        catch (OperationCanceledException) { return; }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Receive loop crashed.");
        }
        finally
        {
            // Reconnect if we were connected OR authenticating (QR phase):
            // WhatsApp server closes the stream after sending QR refs — client must
            // reconnect to pick up pair-success once the phone scans the code.
            shouldReconnect = _state == ConnectionState.Connected
                           || _state == ConnectionState.Authenticating;
            if (_state != ConnectionState.Disconnected && !ct.IsCancellationRequested)
            {
                SetState(ConnectionState.Disconnected);
                Disconnected?.Invoke(this, EventArgs.Empty);
            }
        }

        if (shouldReconnect && _options.AutoReconnect && !ct.IsCancellationRequested)
        {
            // Capture any routing info for the next connection before clearing the processor
            var routingInfo = _noiseProcessor?.PendingRoutingInfo;
            if (routingInfo != null)
            {
                _pendingRoutingInfo = routingInfo;
                _logger.LogInformation("Reconnecting with edge routing info ({Len} bytes).", routingInfo.Length);
            }
            else
            {
                _logger.LogInformation("Reconnecting in {Delay}…", _options.ReconnectDelay);
            }
            await Task.Delay(_options.ReconnectDelay, CancellationToken.None);
            try { await ConnectAsync(CancellationToken.None); }
            catch (Exception ex) { _logger.LogError(ex, "Reconnect failed."); }
        }
    }

    private void SetState(ConnectionState state)
    {
        if (_state == state) return;
        _state = state;
        _logger.LogDebug("State → {State}", state);
        ConnectionStateChanged?.Invoke(this, state);
    }

    public async ValueTask DisposeAsync()
    {
        await DisconnectAsync();
        _cts?.Dispose();
    }
}
