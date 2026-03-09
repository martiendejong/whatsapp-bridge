using Dawa;
using Dawa.Messages;

namespace WhatsAppBridge.Services;

/// <summary>
/// Hosted service that owns the WhatsApp connection lifecycle.
/// Starts on app startup, handles QR code events, reconnects automatically.
/// </summary>
public sealed class WhatsAppConnectionService : BackgroundService
{
    private readonly WhatsAppClient _client;
    private readonly QRCodeStore _qrStore;
    private readonly ILogger<WhatsAppConnectionService> _logger;

    public WhatsAppConnectionService(
        WhatsAppClient client,
        QRCodeStore qrStore,
        ILogger<WhatsAppConnectionService> logger)
    {
        _client = client;
        _qrStore = qrStore;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _client.QRCodeReceived += (_, qr) =>
        {
            _qrStore.Set(qr);
            _logger.LogInformation("QR code ready — GET /api/whatsapp/qr to retrieve it.");
        };

        _client.Connected += (_, _) =>
        {
            _qrStore.Clear();
            _logger.LogInformation("WhatsApp connected and ready.");
        };

        _client.Disconnected += (_, _) =>
            _logger.LogWarning("WhatsApp disconnected.");

        _client.MessageReceived += (_, msg) =>
            _logger.LogInformation("Received message from {From}: {Text}", msg.From, msg.Text);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                _logger.LogInformation("Connecting to WhatsApp…");
                await _client.ConnectAsync(stoppingToken);
                await Task.Delay(Timeout.Infinite, stoppingToken);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _logger.LogError(ex, "WhatsApp connection error. Retrying in 10s…");
                await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
            }
        }

        await _client.DisconnectAsync();
    }
}
