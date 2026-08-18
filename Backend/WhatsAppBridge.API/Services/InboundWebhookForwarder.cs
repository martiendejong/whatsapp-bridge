using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Security.Authentication;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace WhatsAppBridge.API.Services;

/// <summary>
/// Additive, default-OFF feature: pushes every genuine inbound message (live, not history,
/// not our own outgoing) to a configured webhook endpoint — in practice jengo-agi, which
/// decides whether the sender is allow-listed and spawns a reply agent immediately. This
/// replaces waiting for jengo-agi's 5-minute poll and keeps working even when the bridge's
/// own getMessages store lags, because the full message body travels in the push.
///
/// Same safety contract as TaskIntakeForwarder: invoked fire-and-forget from
/// WhatsAppBridgeService.StoreMessage; a failed forward NEVER throws back into the inbound
/// pipeline. Registered as a Singleton and bound from configuration section "InboundWebhook".
/// </summary>
public sealed class InboundWebhookForwarder
{
    private readonly InboundWebhookOptions _options;
    private readonly ILogger<InboundWebhookForwarder> _logger;
    private readonly HttpClient _http;

    // Dedupe: a reconnect can re-deliver a message we already pushed. Keep the last
    // few hundred forwarded message ids and skip repeats (bounded, FIFO eviction).
    private readonly ConcurrentQueue<string> _forwardedOrder = new();
    private readonly ConcurrentDictionary<string, byte> _forwardedIds = new();
    private const int MaxRememberedIds = 500;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public InboundWebhookForwarder(IConfiguration configuration, ILogger<InboundWebhookForwarder> logger)
    {
        _logger = logger;
        _options = new InboundWebhookOptions();
        // Bind the "InboundWebhook" section (works with env vars like INBOUNDWEBHOOK__APIKEY too).
        configuration.GetSection("InboundWebhook").Bind(_options);

        var handler = new HttpClientHandler
        {
            SslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
        };
        _http = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(10),
        };
    }

    /// <summary>True only when the feature is switched on and minimally configured.</summary>
    public bool IsEnabled => _options.Enabled && !string.IsNullOrWhiteSpace(_options.Endpoint);

    /// <summary>
    /// Fire-and-forget push of an inbound message. Never throws.
    /// </summary>
    public void Forward(string sessionId, Dawa.Messages.IncomingMessage msg)
    {
        if (!IsEnabled) return;

        // Forward text, voice, and media messages. Skip receipts/reactions/revocations
        // that carry neither text nor a downloadable media payload.
        var hasContent = !string.IsNullOrWhiteSpace(msg.Text) || !string.IsNullOrWhiteSpace(msg.MediaUrl);
        if (!hasContent) return;

        if (!string.IsNullOrEmpty(msg.Id))
        {
            if (!_forwardedIds.TryAdd(msg.Id, 0)) return; // already pushed
            _forwardedOrder.Enqueue(msg.Id);
            while (_forwardedIds.Count > MaxRememberedIds && _forwardedOrder.TryDequeue(out var old))
                _forwardedIds.TryRemove(old, out _);
        }

        _ = Task.Run(() => ForwardAsync(sessionId, msg));
    }

    private async Task ForwardAsync(string sessionId, Dawa.Messages.IncomingMessage msg)
    {
        try
        {
            var payload = new InboundWebhookPayload
            {
                SessionId = sessionId,
                From = ExtractNumber(msg.From),
                FromJid = msg.From,
                RemoteJid = msg.RemoteJid,
                MessageId = msg.Id,
                Body = msg.Text ?? "",
                Type = msg.Type.ToString().ToLowerInvariant(),
                Timestamp = msg.Timestamp,
                PushName = string.IsNullOrEmpty(msg.PushName) ? null : msg.PushName,
                MediaUrl = string.IsNullOrEmpty(msg.MediaUrl) ? null : msg.MediaUrl,
                MediaKey = string.IsNullOrEmpty(msg.MediaKey) ? null : msg.MediaKey,
                MimeType = string.IsNullOrEmpty(msg.MimeType) ? null : msg.MimeType,
                FileName = string.IsNullOrEmpty(msg.FileName) ? null : msg.FileName,
                Duration = msg.Duration,
            };

            using var request = new HttpRequestMessage(HttpMethod.Post, _options.Endpoint);
            if (!string.IsNullOrEmpty(_options.ApiKey))
            {
                request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _options.ApiKey);
                request.Headers.Add("X-Api-Key", _options.ApiKey);
            }
            request.Content = JsonContent.Create(payload, options: JsonOptions);

            using var response = await _http.SendAsync(request);
            if (response.IsSuccessStatusCode)
            {
                _logger.LogInformation("InboundWebhook: pushed message {MessageId} from {From} ({Status})",
                    msg.Id, payload.From, (int)response.StatusCode);
            }
            else
            {
                _logger.LogWarning("InboundWebhook: endpoint returned {Status} for message {MessageId} from {From}",
                    (int)response.StatusCode, msg.Id, payload.From);
            }
        }
        catch (Exception ex)
        {
            // Never rethrow — keep the inbound pipeline alive. Do NOT log the ApiKey.
            _logger.LogWarning(ex, "InboundWebhook: push failed for message {MessageId}", msg.Id);
        }
    }

    /// <summary>Extract the bare number from a JID: "3161234567:20@s.whatsapp.net" → "3161234567".</summary>
    private static string ExtractNumber(string jidOrNumber)
    {
        if (string.IsNullOrEmpty(jidOrNumber)) return "";
        var beforeAt = jidOrNumber.Split('@')[0];
        return beforeAt.Split(':')[0];
    }

    public sealed class InboundWebhookOptions
    {
        public bool Enabled { get; set; } = false;
        public string? Endpoint { get; set; }
        public string? ApiKey { get; set; }
    }

    private sealed class InboundWebhookPayload
    {
        [JsonPropertyName("sessionId")] public string SessionId { get; set; } = "";
        [JsonPropertyName("from")] public string From { get; set; } = "";
        [JsonPropertyName("fromJid")] public string FromJid { get; set; } = "";
        [JsonPropertyName("remoteJid")] public string RemoteJid { get; set; } = "";
        [JsonPropertyName("messageId")] public string MessageId { get; set; } = "";
        [JsonPropertyName("body")] public string Body { get; set; } = "";
        [JsonPropertyName("type")] public string Type { get; set; } = "text";
        [JsonPropertyName("timestamp")] public long Timestamp { get; set; }
        [JsonPropertyName("pushName")] public string? PushName { get; set; }
        [JsonPropertyName("mediaUrl")] public string? MediaUrl { get; set; }
        [JsonPropertyName("mediaKey")] public string? MediaKey { get; set; }
        [JsonPropertyName("mimeType")] public string? MimeType { get; set; }
        [JsonPropertyName("fileName")] public string? FileName { get; set; }
        [JsonPropertyName("duration")] public uint? Duration { get; set; }
    }
}
