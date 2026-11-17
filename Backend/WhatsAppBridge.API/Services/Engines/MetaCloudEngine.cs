using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Dawa.Messages;
using Dawa.Noise;

namespace WhatsAppBridge.API.Services.Engines;

/// <summary>
/// Engine backed by the official WhatsApp Business Cloud API (Meta Graph API).
/// One engine instance = one Meta phone-number-id. Outbound goes through
/// POST /{phone-number-id}/messages; inbound arrives via the Meta webhook
/// (<see cref="Controllers.MetaWebhookController"/>) which feeds <see cref="HandleWebhookValue"/>.
///
/// Platform limits, surfaced as <see cref="NotSupportedException"/> or empty results rather than
/// faked: no groups, no contacts/chat lists, no history sync, no revoke, no presence. Free-form
/// messages only deliver inside the 24h customer-service window; outside it use
/// <see cref="SendTemplateAsync"/> with an approved template.
/// </summary>
public sealed class MetaCloudEngine : IWhatsAppEngine
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(60) };

    private readonly string _phoneNumberId;
    private readonly MetaCloudSettings _settings;
    private readonly MetaWebhookRouter _router;
    private readonly ILogger _logger;
    private readonly ConcurrentDictionary<string, MessageStatus> _statuses = new();
    private readonly CancellationTokenSource _cts = new();
    private volatile bool _connected;
    private volatile bool _disposed;
    private string? _myJid;
    private string? _displayPhone;

    public MetaCloudEngine(string phoneNumberId, MetaCloudSettings settings, MetaWebhookRouter router,
        ILoggerFactory loggerFactory)
    {
        _phoneNumberId = phoneNumberId;
        _settings = settings;
        _router = router;
        _logger = loggerFactory.CreateLogger<MetaCloudEngine>();
    }

    public string EngineName => "meta";
    public string PhoneNumberId => _phoneNumberId;
    public bool IsConnected => _connected;
    public string? MyJid => _myJid;
    public bool HasSavedSession => true; // Credentials live in AppSettings, not on disk.

    public event EventHandler<string>? QRCodeReceived { add { } remove { } } // Never pairs via QR.
    public event EventHandler? Connected;
    public event EventHandler? Disconnected;
    public event EventHandler<string>? NeedsQr { add { } remove { } }   // Nothing on disk that can expire.
    public event EventHandler<string?>? AuthWarning { add { } remove { } }
    public event EventHandler<IncomingMessage>? MessageReceived;
    public event EventHandler<IncomingMessage>? HistoryMessageReceived { add { } remove { } }
    public event EventHandler<int>? HistorySyncCompleted { add { } remove { } }
    public event EventHandler<(string MessageId, string Jid, MessageStatus Status)>? MessageStatusUpdated;

    // ─── Connection ───────────────────────────────────────────────────────────

    public Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        _router.Register(_phoneNumberId, this);
        _ = Task.Run(() => ConnectLoopAsync(_cts.Token));
        return Task.CompletedTask;
    }

    /// <summary>Verifies the phone number against Graph, then re-checks every 5 minutes.</summary>
    private async Task ConnectLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && !_disposed)
        {
            var ok = false;
            try
            {
                using var doc = await GraphGetAsync(
                    $"{_phoneNumberId}?fields=display_phone_number,status,platform_type,code_verification_status", ct);
                var root = doc.RootElement;
                var status = root.TryGetProperty("status", out var s) ? s.GetString() : null;
                var platform = root.TryGetProperty("platform_type", out var p) ? p.GetString() : null;
                _displayPhone = root.TryGetProperty("display_phone_number", out var d) ? d.GetString() : null;
                ok = string.Equals(status, "CONNECTED", StringComparison.OrdinalIgnoreCase)
                     && string.Equals(platform, "CLOUD_API", StringComparison.OrdinalIgnoreCase);
                if (!ok)
                    _logger.LogWarning("Meta number {Pid} not ready: status={Status} platform={Platform}",
                        _phoneNumberId, status, platform);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                _logger.LogWarning(ex, "Meta number {Pid} health check failed", _phoneNumberId);
            }

            if (ok && !_connected)
            {
                var digits = Digits(_displayPhone);
                _myJid = string.IsNullOrEmpty(digits) ? null : digits + "@s.whatsapp.net";
                _connected = true;
                Connected?.Invoke(this, EventArgs.Empty);
            }
            else if (!ok && _connected)
            {
                _connected = false;
                Disconnected?.Invoke(this, EventArgs.Empty);
            }
            else if (!ok && !_connected)
            {
                Disconnected?.Invoke(this, EventArgs.Empty);
            }

            try { await Task.Delay(ok ? TimeSpan.FromMinutes(5) : TimeSpan.FromMinutes(1), ct); }
            catch (OperationCanceledException) { break; }
        }
    }

    // ─── Sending ──────────────────────────────────────────────────────────────

    public async Task<(string MessageId, string Jid)> SendMessageAsync(string to, string text, CancellationToken ct = default)
    {
        var number = ToNumber(to);
        var body = new Dictionary<string, object?>
        {
            ["messaging_product"] = "whatsapp",
            ["recipient_type"] = "individual",
            ["to"] = number,
            ["type"] = "text",
            ["text"] = new { body = text, preview_url = true },
        };
        var id = await PostMessageAsync(body, ct);
        return (id, number + "@s.whatsapp.net");
    }

    public async Task<(string MessageId, string Jid)> SendReplyAsync(string to, string text, string quotedMsgId,
        string quotedFromJid, CancellationToken ct = default)
    {
        var number = ToNumber(to);
        var body = new Dictionary<string, object?>
        {
            ["messaging_product"] = "whatsapp",
            ["recipient_type"] = "individual",
            ["to"] = number,
            ["type"] = "text",
            ["context"] = new { message_id = quotedMsgId },
            ["text"] = new { body = text, preview_url = true },
        };
        var id = await PostMessageAsync(body, ct);
        return (id, number + "@s.whatsapp.net");
    }

    /// <summary>Business-initiated message via an approved template (works outside the 24h window).</summary>
    public async Task<(string MessageId, string Jid)> SendTemplateAsync(string to, string templateName,
        string languageCode, IEnumerable<string>? bodyParameters = null, CancellationToken ct = default)
    {
        var number = ToNumber(to);
        var template = new Dictionary<string, object?>
        {
            ["name"] = templateName,
            ["language"] = new { code = languageCode },
        };
        var parameters = bodyParameters?.Select(p => new { type = "text", text = p }).ToList();
        if (parameters is { Count: > 0 })
            template["components"] = new[] { new { type = "body", parameters } };

        var body = new Dictionary<string, object?>
        {
            ["messaging_product"] = "whatsapp",
            ["to"] = number,
            ["type"] = "template",
            ["template"] = template,
        };
        var id = await PostMessageAsync(body, ct);
        return (id, number + "@s.whatsapp.net");
    }

    public async Task SendMediaAsync(string to, byte[] fileBytes, string mediaType, string mimeType, string caption, string fileName)
    {
        var number = ToNumber(to);
        var kind = (mediaType ?? "").ToLowerInvariant() switch
        {
            "image" or "photo" => "image",
            "video" => "video",
            "audio" or "voice" or "ptt" => "audio",
            "sticker" => "sticker",
            _ => "document",
        };

        // 1) upload
        using var form = new MultipartFormDataContent();
        form.Add(new StringContent("whatsapp"), "messaging_product");
        form.Add(new StringContent(mimeType), "type");
        var file = new ByteArrayContent(fileBytes);
        file.Headers.ContentType = new MediaTypeHeaderValue(mimeType);
        form.Add(file, "file", string.IsNullOrEmpty(fileName) ? "file" : fileName);

        using var uploadReq = await NewRequestAsync(HttpMethod.Post, $"{_phoneNumberId}/media");
        uploadReq.Content = form;
        using var uploadResp = await Http.SendAsync(uploadReq);
        var uploadJson = await uploadResp.Content.ReadAsStringAsync();
        if (!uploadResp.IsSuccessStatusCode) throw MetaError(uploadResp.StatusCode, uploadJson);
        var mediaId = JsonDocument.Parse(uploadJson).RootElement.GetProperty("id").GetString()!;

        // 2) send
        var media = new Dictionary<string, object?> { ["id"] = mediaId };
        if (!string.IsNullOrEmpty(caption) && kind is "image" or "video" or "document") media["caption"] = caption;
        if (kind == "document" && !string.IsNullOrEmpty(fileName)) media["filename"] = fileName;

        var body = new Dictionary<string, object?>
        {
            ["messaging_product"] = "whatsapp",
            ["to"] = number,
            ["type"] = kind,
            [kind] = media,
        };
        await PostMessageAsync(body, CancellationToken.None);
    }

    public async Task SendReactionAsync(string targetJid, string targetMessageId, bool targetFromMe, string emoji, CancellationToken ct)
    {
        var body = new Dictionary<string, object?>
        {
            ["messaging_product"] = "whatsapp",
            ["to"] = ToNumber(targetJid),
            ["type"] = "reaction",
            ["reaction"] = new { message_id = targetMessageId, emoji },
        };
        await PostMessageAsync(body, ct);
    }

    public async Task SendReadReceiptAsync(string jid, string messageId, long timestamp, CancellationToken ct)
    {
        var body = new Dictionary<string, object?>
        {
            ["messaging_product"] = "whatsapp",
            ["status"] = "read",
            ["message_id"] = messageId,
        };
        await PostJsonAsync($"{_phoneNumberId}/messages", body, ct);
    }

    // Typing/presence have no Cloud API equivalent without a message id; treat as best-effort no-ops.
    public Task SendTypingAsync(string jid, bool isTyping, CancellationToken ct) => Task.CompletedTask;
    public Task SendUserPresenceAsync(bool isOnline, CancellationToken ct) => Task.CompletedTask;
    public Task SubscribePresenceAsync(string jid, CancellationToken ct) => Task.CompletedTask;
    public PresenceInfo? GetPresence(string jid) => null;

    public Task RevokeMessageAsync(string jid, string messageId, bool fromMe, long timestamp, CancellationToken ct)
        => throw new NotSupportedException("The WhatsApp Cloud API cannot delete messages for everyone.");

    public Task ForwardMessageAsync(string toJid, string text, CancellationToken ct)
        => SendMessageAsync(toJid, text, ct);

    public Task SendManualRetryReceiptAsync(string senderJid, string msgId, long timestamp, CancellationToken ct)
        => throw new NotSupportedException("Retry receipts do not apply to the Cloud API.");

    // ─── Lookups: the Cloud API exposes none of these ─────────────────────────

    public Task<List<(string Jid, string Name)>> GetContactsAsync(CancellationToken ct)
        => Task.FromResult(new List<(string Jid, string Name)>());

    public Task<List<(string Jid, string Name, bool Archived, bool Pinned)>> GetChatsAsync(CancellationToken ct)
        => Task.FromResult(new List<(string Jid, string Name, bool Archived, bool Pinned)>());

    public Task<string?> GetProfilePictureAsync(string jid, CancellationToken ct) => Task.FromResult<string?>(null);
    public Task<string?> ResolveLidAsync(string lidJid, CancellationToken ct) => Task.FromResult<string?>(null);

    public Task<List<IncomingMessage>> FetchMessageHistoryAsync(string jid, int count, CancellationToken ct)
        => Task.FromResult(new List<IncomingMessage>());

    public Task<List<IncomingMessage>> RequestOnDemandHistorySyncAsync(string jid, int count, CancellationToken ct,
        string? oldestMsgId = null, bool oldestFromMe = false, long oldestTimestampMs = 0)
        => Task.FromResult(new List<IncomingMessage>());

    // ─── Groups: not available on the Cloud API ───────────────────────────────

    private static NotSupportedException NoGroups() =>
        new("WhatsApp groups are not supported by the Cloud API. Use the Dawa or Baileys engine for group chats.");

    public List<string> GetGroupJids() => new();
    public Task<GroupMetadata?> FetchGroupMetadataAsync(string groupJid, CancellationToken ct) => Task.FromResult<GroupMetadata?>(null);
    public Task<string?> CreateGroupAsync(string subject, IEnumerable<string> participantJids, CancellationToken ct) => throw NoGroups();
    public Task LeaveGroupAsync(string groupJid, CancellationToken ct) => throw NoGroups();
    public Task<Dictionary<string, string>> AddGroupParticipantsAsync(string groupJid, IEnumerable<string> jids, CancellationToken ct) => throw NoGroups();
    public Task<Dictionary<string, string>> RemoveGroupParticipantsAsync(string groupJid, IEnumerable<string> jids, CancellationToken ct) => throw NoGroups();
    public Task<string?> GetGroupInviteLinkAsync(string groupJid, CancellationToken ct) => throw NoGroups();
    public Task UpdateGroupSubjectAsync(string groupJid, string newSubject, CancellationToken ct) => throw NoGroups();

    public MessageStatus? GetMessageStatus(string messageId)
        => _statuses.TryGetValue(messageId, out var s) ? s : null;

    public object GetDebugInfo() => new
    {
        engine = EngineName,
        phoneNumberId = _phoneNumberId,
        displayPhone = _displayPhone,
        connected = _connected,
    };

    // ─── Inbound (called by the webhook controller) ───────────────────────────

    /// <summary>Processes one webhook "value" object (messages and/or statuses).</summary>
    public void HandleWebhookValue(JsonElement value)
    {
        var names = new Dictionary<string, string>();
        if (value.TryGetProperty("contacts", out var contacts) && contacts.ValueKind == JsonValueKind.Array)
        {
            foreach (var c in contacts.EnumerateArray())
            {
                var waId = Str(c, "wa_id");
                var name = c.TryGetProperty("profile", out var prof) ? Str(prof, "name") : null;
                if (waId != null && name != null) names[waId] = name;
            }
        }

        if (value.TryGetProperty("messages", out var messages) && messages.ValueKind == JsonValueKind.Array)
        {
            foreach (var m in messages.EnumerateArray())
            {
                try
                {
                    var msg = ParseInbound(m, names);
                    if (msg != null) MessageReceived?.Invoke(this, msg);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to process inbound Meta message on {Pid}", _phoneNumberId);
                }
            }
        }

        if (value.TryGetProperty("statuses", out var statuses) && statuses.ValueKind == JsonValueKind.Array)
        {
            foreach (var s in statuses.EnumerateArray())
            {
                var id = Str(s, "id");
                var recipient = Str(s, "recipient_id");
                var state = Str(s, "status");
                if (id == null || recipient == null || state == null) continue;
                MessageStatus? mapped = state switch
                {
                    "sent" => MessageStatus.Sent,
                    "delivered" => MessageStatus.Delivered,
                    "read" => MessageStatus.Read,
                    _ => null,
                };
                if (state == "failed")
                    _logger.LogWarning("Meta delivery failed for {Id}: {Detail}", id, s.GetRawText());
                if (mapped == null) continue;
                _statuses[id] = mapped.Value;
                MessageStatusUpdated?.Invoke(this, (id, recipient + "@s.whatsapp.net", mapped.Value));
            }
        }
    }

    private IncomingMessage? ParseInbound(JsonElement m, Dictionary<string, string> names)
    {
        var id = Str(m, "id");
        var from = Str(m, "from");
        if (id == null || from == null) return null;
        var type = Str(m, "type") ?? "unknown";
        long.TryParse(Str(m, "timestamp"), out var ts);
        if (ts == 0) ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        var jid = from + "@s.whatsapp.net";
        var msgType = MessageType.Unknown;
        string? text = null, mediaId = null, mime = null, fileName = null, reaction = null, reactionTarget = null;

        switch (type)
        {
            case "text":
                msgType = MessageType.Text;
                text = m.GetProperty("text").TryGetProperty("body", out var b) ? b.GetString() : null;
                break;
            case "image": case "video": case "audio": case "document": case "sticker":
                msgType = type switch
                {
                    "image" => MessageType.Image, "video" => MessageType.Video, "audio" => MessageType.Audio,
                    "document" => MessageType.Document, _ => MessageType.Sticker,
                };
                var media = m.GetProperty(type);
                mediaId = Str(media, "id");
                mime = Str(media, "mime_type");
                text = Str(media, "caption");
                fileName = Str(media, "filename");
                break;
            case "reaction":
                msgType = MessageType.Reaction;
                var r = m.GetProperty("reaction");
                reaction = Str(r, "emoji") ?? "";
                reactionTarget = Str(r, "message_id");
                break;
            case "location":
                msgType = MessageType.Location;
                var loc = m.GetProperty("location");
                text = $"{Str(loc, "name")} {Str(loc, "address")}".Trim();
                break;
            case "button":
                msgType = MessageType.Text;
                text = m.GetProperty("button").TryGetProperty("text", out var bt) ? bt.GetString() : null;
                break;
            case "interactive":
                msgType = MessageType.Text;
                var inter = m.GetProperty("interactive");
                text = (inter.TryGetProperty("button_reply", out var br) ? Str(br, "title") : null)
                       ?? (inter.TryGetProperty("list_reply", out var lr) ? Str(lr, "title") : null);
                break;
            default:
                _logger.LogInformation("Ignoring inbound Meta message type {Type}", type);
                return null;
        }

        string? quotedId = null;
        if (m.TryGetProperty("context", out var ctx)) quotedId = Str(ctx, "id");

        names.TryGetValue(from, out var pushName);

        return new IncomingMessage
        {
            Id = id,
            From = jid,
            RemoteJid = jid,
            Type = msgType,
            Text = text,
            FromMe = false,
            Timestamp = ts,
            PushName = pushName,
            // Meta media is fetched by id with the access token; the bridge downloads it on demand.
            MediaUrl = mediaId != null ? "meta-media:" + mediaId : null,
            MediaKey = mediaId != null ? "meta" : null,
            MimeType = mime,
            FileName = fileName,
            ReactionEmoji = reaction,
            ReactionTargetId = reactionTarget,
            QuotedMessageId = quotedId,
        };
    }

    /// <summary>Downloads inbound media by Meta media id (two-step: resolve URL, then fetch with the token).</summary>
    public async Task<byte[]?> DownloadMediaAsync(string mediaId, CancellationToken ct = default)
    {
        using var meta = await GraphGetAsync(mediaId, ct);
        var url = meta.RootElement.GetProperty("url").GetString();
        if (string.IsNullOrEmpty(url)) return null;
        using var req = await NewRequestAsync(HttpMethod.Get, null);
        req.RequestUri = new Uri(url);
        using var resp = await Http.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode) return null;
        return await resp.Content.ReadAsByteArrayAsync(ct);
    }

    // ─── Graph helpers ────────────────────────────────────────────────────────

    private async Task<HttpRequestMessage> NewRequestAsync(HttpMethod method, string? path)
    {
        var token = await _settings.GetAccessTokenAsync()
                    ?? throw new InvalidOperationException("Meta access token is not configured (AppSettings MetaAccessToken).");
        var req = new HttpRequestMessage(method,
            path == null ? null : $"https://graph.facebook.com/{_settings.GraphVersion}/{path}");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return req;
    }

    private async Task<JsonDocument> GraphGetAsync(string path, CancellationToken ct)
    {
        using var req = await NewRequestAsync(HttpMethod.Get, path);
        using var resp = await Http.SendAsync(req, ct);
        var json = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode) throw MetaError(resp.StatusCode, json);
        return JsonDocument.Parse(json);
    }

    private async Task<string> PostMessageAsync(Dictionary<string, object?> body, CancellationToken ct)
    {
        using var doc = await PostJsonAsync($"{_phoneNumberId}/messages", body, ct);
        if (doc.RootElement.TryGetProperty("messages", out var msgs) && msgs.GetArrayLength() > 0)
            return msgs[0].GetProperty("id").GetString() ?? Guid.NewGuid().ToString("N");
        return Guid.NewGuid().ToString("N");
    }

    private async Task<JsonDocument> PostJsonAsync(string path, object body, CancellationToken ct)
    {
        using var req = await NewRequestAsync(HttpMethod.Post, path);
        req.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        using var resp = await Http.SendAsync(req, ct);
        var json = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode) throw MetaError(resp.StatusCode, json);
        return JsonDocument.Parse(json);
    }

    private static Exception MetaError(System.Net.HttpStatusCode status, string json)
    {
        try
        {
            var err = JsonDocument.Parse(json).RootElement.GetProperty("error");
            var code = err.TryGetProperty("code", out var c) ? c.GetInt32() : 0;
            var msg = err.TryGetProperty("message", out var m) ? m.GetString() : json;
            var hint = code == 131047 || code == 131026
                ? " (outside the 24h customer-service window: use an approved template)"
                : "";
            return new InvalidOperationException($"Meta API {(int)status} code {code}: {msg}{hint}");
        }
        catch
        {
            return new InvalidOperationException($"Meta API {(int)status}: {json}");
        }
    }

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() : null;

    private static string Digits(string? s) => new((s ?? "").Where(char.IsDigit).ToArray());

    /// <summary>"31612345678@s.whatsapp.net", "+31 6 1234 5678" and "0031..." all become "31612345678".</summary>
    private static string ToNumber(string to)
    {
        var raw = to.Split('@')[0].Split(':')[0];
        var d = Digits(raw);
        if (d.StartsWith("00")) d = d[2..];
        return d;
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        _cts.Cancel();
        _router.Unregister(_phoneNumberId, this);
        _connected = false;
        await Task.CompletedTask;
    }
}
