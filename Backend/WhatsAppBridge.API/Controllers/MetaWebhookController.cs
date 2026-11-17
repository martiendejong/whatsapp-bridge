using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using WhatsAppBridge.API.Services;

namespace WhatsAppBridge.API.Controllers;

/// <summary>
/// Receiver for Meta WhatsApp Cloud API webhooks. Configure in the Meta app
/// (WhatsApp > Configuration) with callback https://{api-host}/api/meta/webhook and the
/// verify token stored under AppSettings MetaWebhookVerifyToken.
/// GET answers Meta's subscribe handshake; POST delivers messages and statuses.
/// When MetaAppSecret is set, the X-Hub-Signature-256 HMAC is enforced.
/// </summary>
[ApiController]
[AllowAnonymous]
[Route("api/meta/webhook")]
public class MetaWebhookController : ControllerBase
{
    private readonly MetaCloudSettings _settings;
    private readonly MetaWebhookRouter _router;
    private readonly ILogger<MetaWebhookController> _logger;

    public MetaWebhookController(MetaCloudSettings settings, MetaWebhookRouter router,
        ILogger<MetaWebhookController> logger)
    {
        _settings = settings;
        _router = router;
        _logger = logger;
    }

    [HttpGet]
    public async Task<IActionResult> Verify(
        [FromQuery(Name = "hub.mode")] string? mode,
        [FromQuery(Name = "hub.verify_token")] string? token,
        [FromQuery(Name = "hub.challenge")] string? challenge)
    {
        var expected = await _settings.GetVerifyTokenAsync();
        if (mode == "subscribe" && !string.IsNullOrEmpty(expected) && FixedEquals(token, expected))
            return Content(challenge ?? "", "text/plain");

        _logger.LogWarning("Meta webhook verification rejected (mode={Mode})", mode);
        return Forbid();
    }

    [HttpPost]
    public async Task<IActionResult> Receive()
    {
        using var ms = new MemoryStream();
        await Request.Body.CopyToAsync(ms);
        var raw = ms.ToArray();

        var secret = await _settings.GetAppSecretAsync();
        if (!string.IsNullOrEmpty(secret))
        {
            var header = Request.Headers["X-Hub-Signature-256"].ToString();
            using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
            var expected = "sha256=" + Convert.ToHexString(hmac.ComputeHash(raw)).ToLowerInvariant();
            if (!FixedEquals(header, expected))
            {
                _logger.LogWarning("Meta webhook signature mismatch");
                return Unauthorized();
            }
        }

        try
        {
            using var doc = JsonDocument.Parse(raw);
            if (doc.RootElement.TryGetProperty("entry", out var entries))
            {
                foreach (var entry in entries.EnumerateArray())
                {
                    if (!entry.TryGetProperty("changes", out var changes)) continue;
                    foreach (var change in changes.EnumerateArray())
                    {
                        if (!change.TryGetProperty("value", out var value)) continue;
                        var pid = value.TryGetProperty("metadata", out var meta)
                                  && meta.TryGetProperty("phone_number_id", out var p)
                            ? p.GetString() : null;
                        var engine = pid != null ? _router.Find(pid) : null;
                        if (engine == null)
                        {
                            _logger.LogWarning("Meta webhook for unknown/unconnected phone_number_id {Pid}", pid);
                            continue;
                        }
                        engine.HandleWebhookValue(value);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            // Always 200: Meta retries failures aggressively and a poison payload must not loop.
            _logger.LogError(ex, "Meta webhook processing failed");
        }

        return Ok();
    }

    private static bool FixedEquals(string? a, string? b)
    {
        if (a == null || b == null) return false;
        var x = Encoding.UTF8.GetBytes(a);
        var y = Encoding.UTF8.GetBytes(b);
        return x.Length == y.Length && CryptographicOperations.FixedTimeEquals(x, y);
    }
}
