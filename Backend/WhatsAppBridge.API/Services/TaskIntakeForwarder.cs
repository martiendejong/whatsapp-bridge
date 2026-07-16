using System.Net;
using System.Net.Http.Json;
using System.Security.Authentication;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace WhatsAppBridge.API.Services;

/// <summary>
/// Additive, default-OFF feature: turns an inbound WhatsApp message from an allow-listed
/// sender that starts with a command prefix (e.g. "/task ...") into a task instruction and
/// forwards it to the jengo-agi intake API.
///
/// This class does NOT touch any pairing/session/send machinery. It is invoked from the
/// existing inbound message path (WhatsAppBridgeService.StoreMessage) in a fire-and-forget
/// manner. A failed forward NEVER throws back into the inbound pipeline — every path is
/// wrapped so a broken endpoint can never crash the bridge.
///
/// Registered as a Singleton and bound from configuration section "TaskIntake".
/// </summary>
public sealed class TaskIntakeForwarder
{
    private readonly TaskIntakeOptions _options;
    private readonly ILogger<TaskIntakeForwarder> _logger;
    private readonly HttpClient _http;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public TaskIntakeForwarder(IConfiguration configuration, ILogger<TaskIntakeForwarder> logger)
    {
        _logger = logger;
        _options = new TaskIntakeOptions();
        // Bind the "TaskIntake" section (works with env vars like TASKINTAKE__APIKEY too).
        configuration.GetSection("TaskIntake").Bind(_options);

        // Force TLS 1.2 (+1.3) for the outbound call; some hosts still default to older policy.
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
    public bool IsEnabled => _options.Enabled;

    /// <summary>
    /// Inspects an inbound message and, if it is an allow-listed task command, forwards it to
    /// jengo-agi and (optionally) replies. Fire-and-forget safe: never throws.
    /// </summary>
    /// <param name="senderJid">Raw sender JID of the inbound message (msg.From).</param>
    /// <param name="messageText">The message text (msg.Text).</param>
    /// <param name="messageId">The WhatsApp message id (used as dedupe key).</param>
    /// <param name="reply">
    /// Callback used to send a confirmation reply back to the sender via the existing send path.
    /// Signature: (recipient, body) => Task. May be null to disable replies.
    /// </param>
    public async Task HandleInboundAsync(
        string senderJid,
        string? messageText,
        string messageId,
        Func<string, string, Task>? reply)
    {
        try
        {
            // 1. Feature switched off → do nothing (existing behavior unchanged).
            if (!_options.Enabled)
                return;

            if (string.IsNullOrWhiteSpace(messageText))
                return;

            // 2. Sender must be explicitly allow-listed. Match on the raw JID OR the bare number.
            var senderNumber = ExtractNumber(senderJid);
            if (!IsAllowedSender(senderJid, senderNumber))
                return; // ignore silently for task purposes

            // 3. Message must start with a configured command prefix.
            var prefixes = _options.CommandPrefixes is { Length: > 0 }
                ? _options.CommandPrefixes
                : DefaultPrefixes;

            var trimmed = messageText.TrimStart();
            var matchedPrefix = prefixes.FirstOrDefault(p =>
                !string.IsNullOrEmpty(p) &&
                trimmed.StartsWith(p, StringComparison.OrdinalIgnoreCase));

            if (matchedPrefix == null)
                return; // not a task command → untouched

            var instruction = trimmed.Substring(matchedPrefix.Length).Trim();
            if (string.IsNullOrWhiteSpace(instruction))
            {
                _logger.LogInformation("TaskIntake: empty instruction from {Sender} after prefix; ignoring", senderNumber);
                if (_options.ReplyOnSuccess && reply != null)
                    await SafeReply(reply, senderJid, "⚠️ Could not create task: empty instruction.");
                return;
            }

            var title = BuildTitle(instruction);

            await ForwardAsync(senderJid, senderNumber, instruction, title, messageId, reply);
        }
        catch (Exception ex)
        {
            // A failed forward must NEVER crash the bridge or the inbound pipeline.
            _logger.LogError(ex, "TaskIntake: unexpected error while handling inbound message {MessageId}", messageId);
        }
    }

    private async Task ForwardAsync(
        string senderJid,
        string senderNumber,
        string instruction,
        string title,
        string messageId,
        Func<string, string, Task>? reply)
    {
        if (string.IsNullOrWhiteSpace(_options.Endpoint))
        {
            _logger.LogWarning("TaskIntake: enabled but Endpoint is not configured; skipping forward");
            return;
        }

        try
        {
            var payload = new TaskIntakePayload
            {
                Title = title,
                Description = instruction,
                Source = "whatsapp",
                RequestedBy = senderNumber,
                DedupeKey = messageId,
            };

            using var request = new HttpRequestMessage(HttpMethod.Post, _options.Endpoint);
            if (!string.IsNullOrEmpty(_options.ApiKey))
                request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _options.ApiKey);
            request.Content = JsonContent.Create(payload, options: JsonOptions);

            using var response = await _http.SendAsync(request);
            var body = await response.Content.ReadAsStringAsync();

            if (response.StatusCode == HttpStatusCode.Created || response.StatusCode == HttpStatusCode.OK)
            {
                var result = TryParseResult(body);
                _logger.LogInformation(
                    "TaskIntake: created task {TaskId} (status {Status}) for {Sender}",
                    result?.TaskId ?? "?", result?.Status ?? "?", senderNumber);

                if (_options.ReplyOnSuccess && reply != null)
                {
                    var url = result?.Url;
                    var text = string.IsNullOrEmpty(url)
                        ? "✅ Task created."
                        : $"✅ Task created: {url}";
                    await SafeReply(reply, senderJid, text);
                }
            }
            else
            {
                _logger.LogWarning(
                    "TaskIntake: intake returned {StatusCode} for {Sender}",
                    (int)response.StatusCode, senderNumber);

                if (_options.ReplyOnSuccess && reply != null)
                    await SafeReply(reply, senderJid,
                        $"⚠️ Could not create task: intake returned HTTP {(int)response.StatusCode}.");
            }
        }
        catch (TaskCanceledException)
        {
            _logger.LogWarning("TaskIntake: forward timed out for {Sender}", senderNumber);
            if (_options.ReplyOnSuccess && reply != null)
                await SafeReply(reply, senderJid, "⚠️ Could not create task: request timed out.");
        }
        catch (Exception ex)
        {
            // Never rethrow — keep the inbound pipeline alive. Do NOT log the ApiKey.
            _logger.LogError(ex, "TaskIntake: forward failed for {Sender}", senderNumber);
            if (_options.ReplyOnSuccess && reply != null)
                await SafeReply(reply, senderJid, "⚠️ Could not create task: forwarding error.");
        }
    }

    private async Task SafeReply(Func<string, string, Task> reply, string recipient, string text)
    {
        try { await reply(recipient, text); }
        catch (Exception ex) { _logger.LogWarning(ex, "TaskIntake: reply failed"); }
    }

    private bool IsAllowedSender(string senderJid, string senderNumber)
    {
        if (_options.AllowedSenders is not { Length: > 0 })
            return false;

        foreach (var allowed in _options.AllowedSenders)
        {
            if (string.IsNullOrWhiteSpace(allowed)) continue;
            var allowedNumber = ExtractNumber(allowed);
            if (string.Equals(allowed, senderJid, StringComparison.OrdinalIgnoreCase)) return true;
            if (string.Equals(allowedNumber, senderNumber, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    /// <summary>Extract the bare number from a JID: "3161234567:20@s.whatsapp.net" → "3161234567".</summary>
    private static string ExtractNumber(string jidOrNumber)
    {
        if (string.IsNullOrEmpty(jidOrNumber)) return "";
        var beforeAt = jidOrNumber.Split('@')[0];
        return beforeAt.Split(':')[0];
    }

    private static string BuildTitle(string instruction)
    {
        var firstLine = instruction.Split('\n')[0].Trim();
        var candidate = string.IsNullOrEmpty(firstLine) ? instruction.Trim() : firstLine;
        if (candidate.Length <= 80) return candidate;
        return candidate.Substring(0, 80).TrimEnd();
    }

    private TaskIntakeResult? TryParseResult(string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return null;
        try { return JsonSerializer.Deserialize<TaskIntakeResult>(body, JsonOptions); }
        catch { return null; }
    }

    private static readonly string[] DefaultPrefixes = { "/task", "task:" };

    // ─── Nested config/DTO types ───────────────────────────────────────────────

    public sealed class TaskIntakeOptions
    {
        public bool Enabled { get; set; } = false;
        public string? Endpoint { get; set; }
        public string? ApiKey { get; set; }
        public string[]? CommandPrefixes { get; set; }
        public string[]? AllowedSenders { get; set; }
        public bool ReplyOnSuccess { get; set; } = true;
    }

    private sealed class TaskIntakePayload
    {
        [JsonPropertyName("title")] public string Title { get; set; } = "";
        [JsonPropertyName("description")] public string Description { get; set; } = "";
        [JsonPropertyName("source")] public string Source { get; set; } = "whatsapp";
        [JsonPropertyName("requestedBy")] public string RequestedBy { get; set; } = "";
        [JsonPropertyName("dedupeKey")] public string DedupeKey { get; set; } = "";
    }

    private sealed class TaskIntakeResult
    {
        [JsonPropertyName("taskId")] public string? TaskId { get; set; }
        [JsonPropertyName("url")] public string? Url { get; set; }
        [JsonPropertyName("status")] public string? Status { get; set; }
    }
}
