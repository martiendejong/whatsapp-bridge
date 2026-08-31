using System.Net.Http.Json;
using System.Security.Authentication;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace WhatsAppBridge.API.Services;

/// <summary>
/// Additive, default-OFF feature (task 1067): for an inbound WhatsApp message from a sender who
/// is NOT on the outbound guardrail allow-list, asks the coachingplatform (CoachOS) chat intake
/// for an AI reply — find-or-create a participant + conversation there, formulate an answer using
/// that tenant's knowledge base/tools, and hand the reply text back. This class NEVER sends
/// anything itself: it only returns candidate reply text. The caller (WhatsAppBridgeService)
/// still routes the actual send through OutboundGuardrailService.CheckAsync using the dedicated
/// <see cref="OutboundGuardrailService.CoachOsReplyEndpoint"/> endpoint name, so the guardrail's
/// reply-window check and existing rate limits remain the single source of truth for what
/// actually leaves the bridge — this class has no send capability to misuse.
///
/// Same safety contract as TaskIntakeForwarder/InboundWebhookForwarder: invoked fire-and-forget
/// from WhatsAppBridgeService's inbound path; a failed/slow call NEVER throws back into the
/// inbound pipeline. Registered as a Singleton and bound from configuration section
/// "CoachOsIntake".
/// </summary>
public sealed class CoachOsIntakeForwarder
{
    private readonly CoachOsIntakeOptions _options;
    private readonly ILogger<CoachOsIntakeForwarder> _logger;
    private readonly HttpClient _http;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public CoachOsIntakeForwarder(IConfiguration configuration, ILogger<CoachOsIntakeForwarder> logger)
    {
        _logger = logger;
        _options = new CoachOsIntakeOptions();
        // Bind the "CoachOsIntake" section (works with env vars like COACHOSINTAKE__APIKEY too).
        configuration.GetSection("CoachOsIntake").Bind(_options);

        var handler = new HttpClientHandler
        {
            SslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
        };
        _http = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(_options.TimeoutSeconds > 0 ? _options.TimeoutSeconds : 25),
        };
    }

    /// <summary>True only when the feature is switched on and minimally configured.</summary>
    public bool IsEnabled =>
        _options.Enabled &&
        !string.IsNullOrWhiteSpace(_options.Endpoint) &&
        !string.IsNullOrWhiteSpace(_options.TenantSlug);

    /// <summary>
    /// Asks coachingplatform for an AI reply to this inbound message. Returns null on any
    /// failure, timeout, or empty reply — the caller treats null as "do not reply", never as
    /// "retry" or "fall back to a default message". Never throws.
    /// </summary>
    public async Task<string?> GetAiReplyAsync(string senderJid, string? pushName, string text)
    {
        if (!IsEnabled) return null;
        if (string.IsNullOrWhiteSpace(text)) return null;

        var phone = ExtractNumber(senderJid);
        try
        {
            var payload = new CoachOsIntakePayload
            {
                TenantSlug = _options.TenantSlug!,
                Phone = phone,
                PushName = string.IsNullOrWhiteSpace(pushName) ? null : pushName,
                Text = text,
            };

            using var request = new HttpRequestMessage(HttpMethod.Post, _options.Endpoint);
            if (!string.IsNullOrEmpty(_options.ApiKey))
                request.Headers.Add("X-Api-Key", _options.ApiKey);
            request.Content = JsonContent.Create(payload, options: JsonOptions);

            using var response = await _http.SendAsync(request);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("CoachOsIntake: endpoint returned {Status} for {Phone}", (int)response.StatusCode, phone);
                return null;
            }

            var result = await response.Content.ReadFromJsonAsync<CoachOsIntakeResult>(JsonOptions);
            if (string.IsNullOrWhiteSpace(result?.Reply))
            {
                _logger.LogInformation("CoachOsIntake: empty/no reply for {Phone}", phone);
                return null;
            }

            return result.Reply;
        }
        catch (TaskCanceledException)
        {
            _logger.LogWarning("CoachOsIntake: request timed out for {Phone}", phone);
            return null;
        }
        catch (Exception ex)
        {
            // Never rethrow — keep the inbound pipeline alive. Do NOT log the ApiKey.
            _logger.LogError(ex, "CoachOsIntake: request failed for {Phone}", phone);
            return null;
        }
    }

    /// <summary>Extract the bare number from a JID: "3161234567:20@s.whatsapp.net" → "3161234567".</summary>
    private static string ExtractNumber(string jidOrNumber)
    {
        if (string.IsNullOrEmpty(jidOrNumber)) return "";
        var beforeAt = jidOrNumber.Split('@')[0];
        return beforeAt.Split(':')[0];
    }

    public sealed class CoachOsIntakeOptions
    {
        public bool Enabled { get; set; } = false;
        public string? Endpoint { get; set; }
        public string? ApiKey { get; set; }
        public string? TenantSlug { get; set; }
        public int TimeoutSeconds { get; set; } = 25;
    }

    private sealed class CoachOsIntakePayload
    {
        [JsonPropertyName("tenantSlug")] public string TenantSlug { get; set; } = "";
        [JsonPropertyName("phone")] public string Phone { get; set; } = "";
        [JsonPropertyName("pushName")] public string? PushName { get; set; }
        [JsonPropertyName("text")] public string Text { get; set; } = "";
    }

    private sealed class CoachOsIntakeResult
    {
        [JsonPropertyName("reply")] public string? Reply { get; set; }
    }
}
