using Dawa;
using Microsoft.AspNetCore.Mvc;
using QRCoder;
using WhatsAppBridge.Models;
using WhatsAppBridge.Services;

namespace WhatsAppBridge.Controllers;

/// <summary>
/// HTTP API for WhatsApp operations.
///
/// Endpoints:
///   GET  /api/whatsapp/status    — connection state + session info
///   GET  /api/whatsapp/qr        — QR code as text (scan with WhatsApp)
///   GET  /api/whatsapp/qr.png    — QR code as PNG image (embed in browser)
///   POST /api/whatsapp/send      — send a text message
///   POST /api/whatsapp/logout    — delete session (forces re-pairing)
/// </summary>
[ApiController]
[Route("api/whatsapp")]
public sealed class WhatsAppController : ControllerBase
{
    private readonly WhatsAppClient _client;
    private readonly QRCodeStore _qrStore;
    private readonly ILogger<WhatsAppController> _logger;

    public WhatsAppController(WhatsAppClient client, QRCodeStore qrStore, ILogger<WhatsAppController> logger)
    {
        _client = client;
        _qrStore = qrStore;
        _logger = logger;
    }

    // ─── Status ───────────────────────────────────────────────────────────────

    /// <summary>Returns current connection status.</summary>
    [HttpGet("status")]
    public ActionResult<StatusResponse> GetStatus()
    {
        return Ok(new StatusResponse
        {
            State = _client.State.ToString(),
            IsConnected = _client.IsConnected,
            HasSession = _client.HasSavedSession,
            QRPending = _qrStore.Get() != null,
        });
    }

    // ─── QR Code ─────────────────────────────────────────────────────────────

    /// <summary>Returns the raw QR code string (for terminal rendering).</summary>
    [HttpGet("qr")]
    public IActionResult GetQR()
    {
        var qr = _qrStore.Get();
        if (qr == null)
        {
            if (_client.IsConnected)
                return Ok(new { message = "Already connected. No QR needed." });
            return Accepted(new { message = "QR not yet available. Connect first and try again in a moment." });
        }
        return Ok(new { qr });
    }

    /// <summary>Returns the QR code as a PNG image (embed with &lt;img src="/api/whatsapp/qr.png"&gt;).</summary>
    [HttpGet("qr.png")]
    public IActionResult GetQRImage()
    {
        var qr = _qrStore.Get();
        if (qr == null)
            return NotFound(new { message = "No QR code available. Already connected or not yet started." });

        using var generator = new QRCodeGenerator();
        var data = generator.CreateQrCode(qr, QRCodeGenerator.ECCLevel.L);
        var code = new PngByteQRCode(data);
        var png = code.GetGraphic(10);

        return File(png, "image/png");
    }

    // ─── Send ─────────────────────────────────────────────────────────────────

    /// <summary>Sends a text message to a phone number or WhatsApp JID.</summary>
    /// <remarks>
    /// Request body:
    /// {
    ///   "to": "+31612345678",
    ///   "text": "Hello from the bridge!"
    /// }
    /// </remarks>
    [HttpPost("send")]
    public async Task<ActionResult<SendResult>> Send(
        [FromBody] SendMessageRequest request,
        CancellationToken cancellationToken)
    {
        if (!_client.IsConnected)
        {
            return StatusCode(503, new SendResult
            {
                Success = false,
                Error = $"WhatsApp client is not connected (state: {_client.State}). " +
                        "Check /api/whatsapp/status and scan the QR at /api/whatsapp/qr.png if needed."
            });
        }

        try
        {
            await _client.SendMessageAsync(request.To, request.Text, cancellationToken);
            _logger.LogInformation("Sent message to {To}", request.To);
            return Ok(new SendResult { Success = true });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send message to {To}", request.To);
            return StatusCode(500, new SendResult { Success = false, Error = ex.Message });
        }
    }

    // ─── Logout ───────────────────────────────────────────────────────────────

    /// <summary>Deletes the saved session. Next connection will require QR re-scan.</summary>
    [HttpPost("logout")]
    public IActionResult Logout()
    {
        _client.Logout();
        return Ok(new { message = "Session deleted. Restart the service to re-pair." });
    }
}
