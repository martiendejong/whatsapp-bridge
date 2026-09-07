using Dawa.Messages;
using Dawa.Noise;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace WhatsAppBridge.API.Services.Engines;

/// <summary>
/// Engine backed by the real Baileys library (@whiskeysockets/baileys) running as a
/// Node.js sidecar process — one process per session. Commands and events travel as
/// newline-delimited JSON over stdin/stdout: no ports, no auth surface, and the sidecar
/// dies with its parent handle when this engine is disposed.
///
/// Credentials live in {sessionDir}/baileys-auth/ (Baileys multi-file auth state) and are
/// NOT interchangeable with Dawa's creds.json — first connect on this engine needs a QR re-pair.
/// </summary>
public sealed class BaileysEngine : IWhatsAppEngine
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    private readonly string _sessionDirectory;
    private readonly string _nodePath;
    private readonly string _scriptPath;
    private readonly ILogger _logger;

    private Process? _process;
    private readonly SemaphoreSlim _stdinLock = new(1, 1);
    private readonly ConcurrentDictionary<long, TaskCompletionSource<JsonElement>> _pending = new();
    private readonly ConcurrentDictionary<string, PresenceInfo> _presence = new();
    private readonly ConcurrentDictionary<string, MessageStatus> _messageStatuses = new();
    private long _nextRequestId;
    private volatile bool _connected;
    private volatile bool _disposed;
    private string? _myJid;

    public BaileysEngine(string sessionDirectory, IConfiguration configuration, ILoggerFactory loggerFactory)
    {
        _sessionDirectory = sessionDirectory;
        _logger = loggerFactory.CreateLogger<BaileysEngine>();
        _nodePath = configuration["WhatsApp:Baileys:NodePath"] ?? "node";
        _scriptPath = configuration["WhatsApp:Baileys:SidecarScript"]
            ?? FindDefaultScriptPath();
    }

    private static string FindDefaultScriptPath()
    {
        // Published layout: baileys-sidecar/ sits next to the API DLL (csproj copies it).
        var published = Path.Combine(AppContext.BaseDirectory, "baileys-sidecar", "index.js");
        if (File.Exists(published)) return published;

        // Dev layout: walk up from bin/... to the repo root.
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var candidate = Path.Combine(dir.FullName, "baileys-sidecar", "index.js");
            if (File.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        return published; // will fail with a clear "script not found" on connect
    }

    public string EngineName => "baileys";
    public bool IsConnected => _connected;
    public string? MyJid => _myJid;
    public bool HasSavedSession => File.Exists(Path.Combine(_sessionDirectory, "baileys-auth", "creds.json"));

    public event EventHandler<string>? QRCodeReceived;
    public event EventHandler? Connected;
    public event EventHandler? Disconnected;
    public event EventHandler<IncomingMessage>? MessageReceived;
    public event EventHandler<IncomingMessage>? HistoryMessageReceived;
    public event EventHandler<int>? HistorySyncCompleted;
    public event EventHandler<(string MessageId, string Jid, MessageStatus Status)>? MessageStatusUpdated;

    // ─── Process lifecycle ────────────────────────────────────────────────────

    public Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        if (_process is { HasExited: false })
            return Task.CompletedTask;

        if (!File.Exists(_scriptPath))
            throw new InvalidOperationException(
                $"Baileys sidecar script not found at '{_scriptPath}'. " +
                "Deploy baileys-sidecar/ next to the API and run 'npm install' in it, " +
                "or set WhatsApp:Baileys:SidecarScript.");

        Directory.CreateDirectory(_sessionDirectory);

        var psi = new ProcessStartInfo
        {
            FileName = _nodePath,
            WorkingDirectory = Path.GetDirectoryName(_scriptPath)!,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        psi.ArgumentList.Add(_scriptPath);
        psi.ArgumentList.Add("--session-dir");
        psi.ArgumentList.Add(_sessionDirectory);

        var process = Process.Start(psi)
            ?? throw new InvalidOperationException($"Failed to start Baileys sidecar ({_nodePath})");
        _process = process;
        _logger.LogInformation("Baileys sidecar started (pid {Pid}) for {Dir}", process.Id, _sessionDirectory);

        _ = Task.Run(() => ReadLoopAsync(process));
        _ = Task.Run(() => ReadStderrAsync(process));

        process.EnableRaisingEvents = true;
        process.Exited += (_, _) =>
        {
            _logger.LogWarning("Baileys sidecar exited (code {Code}) for {Dir}", SafeExitCode(process), _sessionDirectory);
            OnProcessGone();
        };

        return Task.CompletedTask;
    }

    private static int? SafeExitCode(Process p)
    {
        try { return p.ExitCode; } catch { return null; }
    }

    private void OnProcessGone()
    {
        var wasConnected = _connected;
        _connected = false;
        foreach (var kv in _pending)
            if (_pending.TryRemove(kv.Key, out var tcs))
                tcs.TrySetException(new InvalidOperationException("Baileys sidecar process exited"));
        if (wasConnected || !_disposed)
            Disconnected?.Invoke(this, EventArgs.Empty);
    }

    private async Task ReadLoopAsync(Process process)
    {
        try
        {
            string? line;
            while ((line = await process.StandardOutput.ReadLineAsync()) != null)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                try { HandleLine(line); }
                catch (Exception ex) { _logger.LogWarning(ex, "Bad sidecar line: {Line}", Truncate(line, 300)); }
            }
        }
        catch (Exception ex)
        {
            if (!_disposed) _logger.LogWarning(ex, "Baileys sidecar stdout loop ended");
        }
    }

    private async Task ReadStderrAsync(Process process)
    {
        try
        {
            string? line;
            while ((line = await process.StandardError.ReadLineAsync()) != null)
                if (!string.IsNullOrWhiteSpace(line))
                    _logger.LogInformation("[baileys-sidecar] {Line}", Truncate(line, 500));
        }
        catch { /* process died — Exited handler covers it */ }
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";

    private void HandleLine(string line)
    {
        using var doc = JsonDocument.Parse(line);
        var root = doc.RootElement;

        if (root.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.Number)
        {
            // Command response
            var id = idEl.GetInt64();
            if (!_pending.TryRemove(id, out var tcs)) return;
            if (root.TryGetProperty("ok", out var okEl) && okEl.GetBoolean())
            {
                var result = root.TryGetProperty("result", out var r) ? r.Clone() : default;
                tcs.TrySetResult(result);
            }
            else
            {
                var error = root.TryGetProperty("error", out var e) ? e.GetString() : "unknown sidecar error";
                tcs.TrySetException(new InvalidOperationException($"Baileys sidecar: {error}"));
            }
            return;
        }

        if (!root.TryGetProperty("event", out var evtEl)) return;
        var evt = evtEl.GetString();
        var data = root.TryGetProperty("data", out var d) ? d : default;

        switch (evt)
        {
            case "qr":
                QRCodeReceived?.Invoke(this, data.GetProperty("qr").GetString() ?? "");
                break;

            case "connected":
                _myJid = data.TryGetProperty("jid", out var jidEl) ? jidEl.GetString() : null;
                _connected = true;
                Connected?.Invoke(this, EventArgs.Empty);
                break;

            case "disconnected":
                _connected = false;
                Disconnected?.Invoke(this, EventArgs.Empty);
                break;

            case "message":
                MessageReceived?.Invoke(this, ParseIncomingMessage(data));
                break;

            case "history_message":
                HistoryMessageReceived?.Invoke(this, ParseIncomingMessage(data));
                break;

            case "history_sync_completed":
                HistorySyncCompleted?.Invoke(this, data.TryGetProperty("count", out var c) ? c.GetInt32() : 0);
                break;

            case "message_status":
            {
                var msgId = data.GetProperty("messageId").GetString() ?? "";
                var jid = data.GetProperty("jid").GetString() ?? "";
                if (Enum.TryParse<MessageStatus>(data.GetProperty("status").GetString(), ignoreCase: true, out var status))
                {
                    _messageStatuses[msgId] = status;
                    MessageStatusUpdated?.Invoke(this, (msgId, jid, status));
                }
                break;
            }

            case "presence":
            {
                var jid = data.GetProperty("jid").GetString() ?? "";
                var status = data.TryGetProperty("status", out var st) ? st.GetString() ?? "unknown" : "unknown";
                _presence[jid] = new PresenceInfo(jid, status, DateTime.UtcNow);
                break;
            }

            case "log":
                _logger.LogInformation("[baileys-sidecar] {Msg}", data.GetString() ?? "");
                break;
        }
    }

    private static IncomingMessage ParseIncomingMessage(JsonElement d)
    {
        string? Str(string name) => d.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        long Lng(string name) => d.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt64() : 0;
        bool Bool(string name) => d.TryGetProperty(name, out var v) && v.ValueKind is JsonValueKind.True;
        uint? UInt(string name) => d.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetUInt32() : null;
        long? LngN(string name) => d.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt64() : null;

        var type = Enum.TryParse<MessageType>(Str("type"), ignoreCase: true, out var t) ? t : MessageType.Unknown;
        var quotedType = Enum.TryParse<MessageType>(Str("quotedType"), ignoreCase: true, out var qt) ? qt : MessageType.Unknown;

        return new IncomingMessage
        {
            Id = Str("id") ?? "",
            From = Str("from") ?? "",
            RemoteJid = Str("remoteJid") ?? "",
            Participant = Str("participant"),
            Type = type,
            Text = Str("text"),
            FromMe = Bool("fromMe"),
            Timestamp = Lng("timestamp"),
            PushName = Str("pushName"),
            IsRevoked = Bool("isRevoked"),
            MediaUrl = Str("mediaUrl"),
            MimeType = Str("mimeType"),
            FileName = Str("fileName"),
            FileSize = LngN("fileSize"),
            Duration = UInt("duration"),
            Width = UInt("width"),
            Height = UInt("height"),
            MediaKey = Str("mediaKey"),
            MediaSha256Enc = Str("mediaSha256Enc"),
            ReactionEmoji = Str("reactionEmoji"),
            ReactionTargetId = Str("reactionTargetId"),
            QuotedMessageId = Str("quotedMessageId"),
            QuotedFrom = Str("quotedFrom"),
            QuotedText = Str("quotedText"),
            QuotedType = quotedType,
        };
    }

    // ─── Command plumbing ─────────────────────────────────────────────────────

    private async Task<JsonElement> SendCommandAsync(string cmd, object? args = null, TimeSpan? timeout = null, CancellationToken ct = default)
    {
        var process = _process;
        if (process == null || process.HasExited)
            throw new InvalidOperationException("Baileys sidecar is not running.");

        var id = Interlocked.Increment(ref _nextRequestId);
        var tcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;

        var payload = JsonSerializer.Serialize(new { id, cmd, args }, JsonOpts);
        await _stdinLock.WaitAsync(ct);
        try
        {
            await process.StandardInput.WriteLineAsync(payload);
            await process.StandardInput.FlushAsync();
        }
        finally { _stdinLock.Release(); }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout ?? TimeSpan.FromSeconds(60));
        await using var reg = cts.Token.Register(() =>
        {
            if (_pending.TryRemove(id, out var pending))
                pending.TrySetException(new TimeoutException($"Baileys sidecar command '{cmd}' timed out"));
        });
        return await tcs.Task;
    }

    // ─── Messaging ────────────────────────────────────────────────────────────

    public async Task<(string MessageId, string Jid)> SendMessageAsync(string to, string text, CancellationToken ct = default)
    {
        var r = await SendCommandAsync("sendMessage", new { to, text }, ct: ct);
        return (r.GetProperty("messageId").GetString() ?? "", r.GetProperty("jid").GetString() ?? "");
    }

    public async Task<(string MessageId, string Jid)> SendReplyAsync(string to, string text, string quotedMsgId, string quotedFromJid, CancellationToken ct = default)
    {
        var r = await SendCommandAsync("sendReply", new { to, text, quotedMsgId, quotedFromJid }, ct: ct);
        return (r.GetProperty("messageId").GetString() ?? "", r.GetProperty("jid").GetString() ?? "");
    }

    public Task SendMediaAsync(string to, byte[] fileBytes, string mediaType, string mimeType, string caption, string fileName)
        => SendCommandAsync("sendMedia", new
        {
            to,
            mediaType,
            mimeType,
            caption,
            fileName,
            dataBase64 = Convert.ToBase64String(fileBytes),
        }, timeout: TimeSpan.FromMinutes(3));

    public Task SendReactionAsync(string targetJid, string targetMessageId, bool targetFromMe, string emoji, CancellationToken ct)
        => SendCommandAsync("sendReaction", new { jid = targetJid, messageId = targetMessageId, fromMe = targetFromMe, emoji }, ct: ct);

    public Task SendTypingAsync(string jid, bool isTyping, CancellationToken ct)
        => SendCommandAsync("sendTyping", new { jid, isTyping }, ct: ct);

    public Task SendUserPresenceAsync(bool isOnline, CancellationToken ct)
        => SendCommandAsync("sendUserPresence", new { isOnline }, ct: ct);

    public Task RevokeMessageAsync(string jid, string messageId, bool fromMe, long timestamp, CancellationToken ct)
        => SendCommandAsync("revokeMessage", new { jid, messageId, fromMe }, ct: ct);

    public Task ForwardMessageAsync(string toJid, string text, CancellationToken ct)
        => SendCommandAsync("sendMessage", new { to = toJid, text }, ct: ct);

    public Task SendReadReceiptAsync(string jid, string messageId, long timestamp, CancellationToken ct)
        => SendCommandAsync("sendReadReceipt", new { jid, messageId }, ct: ct);

    public Task SendManualRetryReceiptAsync(string senderJid, string msgId, long timestamp, CancellationToken ct)
        => throw new NotSupportedException("Manual retry receipts are Dawa-specific; Baileys handles retries internally.");

    // ─── Reads ────────────────────────────────────────────────────────────────

    public async Task<List<(string Jid, string Name)>> GetContactsAsync(CancellationToken ct)
    {
        var r = await SendCommandAsync("getContacts", ct: ct);
        return r.EnumerateArray()
            .Select(c => (c.GetProperty("jid").GetString() ?? "", c.GetProperty("name").GetString() ?? ""))
            .ToList();
    }

    public async Task<List<(string Jid, string Name, bool Archived, bool Pinned)>> GetChatsAsync(CancellationToken ct)
    {
        var r = await SendCommandAsync("getChats", ct: ct);
        return r.EnumerateArray()
            .Select(c => (
                c.GetProperty("jid").GetString() ?? "",
                c.GetProperty("name").GetString() ?? "",
                c.TryGetProperty("archived", out var a) && a.ValueKind == JsonValueKind.True,
                c.TryGetProperty("pinned", out var p) && p.ValueKind == JsonValueKind.True))
            .ToList();
    }

    public async Task<string?> GetProfilePictureAsync(string jid, CancellationToken ct)
    {
        var r = await SendCommandAsync("getProfilePicture", new { jid }, ct: ct);
        return r.ValueKind == JsonValueKind.String ? r.GetString() : null;
    }

    public Task SubscribePresenceAsync(string jid, CancellationToken ct)
        => SendCommandAsync("subscribePresence", new { jid }, ct: ct);

    public PresenceInfo? GetPresence(string jid) => _presence.TryGetValue(jid, out var p) ? p : null;

    public async Task<string?> ResolveLidAsync(string lidJid, CancellationToken ct)
    {
        var r = await SendCommandAsync("resolveLid", new { lidJid }, ct: ct);
        return r.ValueKind == JsonValueKind.String ? r.GetString() : null;
    }

    public async Task<List<IncomingMessage>> FetchMessageHistoryAsync(string jid, int count, CancellationToken ct)
    {
        var r = await SendCommandAsync("fetchMessageHistory", new { jid, count }, ct: ct);
        return r.ValueKind == JsonValueKind.Array ? r.EnumerateArray().Select(ParseIncomingMessage).ToList() : new();
    }

    public async Task<List<IncomingMessage>> RequestOnDemandHistorySyncAsync(string jid, int count, CancellationToken ct,
        string? oldestMsgId = null, bool oldestFromMe = false, long oldestTimestampMs = 0)
    {
        // Baileys' fetchMessageHistory is the equivalent on-demand sync; results arrive
        // asynchronously via the history_message event stream, same as with Dawa.
        await SendCommandAsync("requestHistorySync", new { jid, count, oldestMsgId, oldestFromMe, oldestTimestampMs }, ct: ct);
        return new List<IncomingMessage>();
    }

    // ─── Groups ───────────────────────────────────────────────────────────────

    public List<string> GetGroupJids()
    {
        try
        {
            var r = SendCommandAsync("getGroupJids").GetAwaiter().GetResult();
            return r.ValueKind == JsonValueKind.Array
                ? r.EnumerateArray().Select(x => x.GetString() ?? "").Where(x => x.Length > 0).ToList()
                : new();
        }
        catch { return new List<string>(); }
    }

    public async Task<GroupMetadata?> FetchGroupMetadataAsync(string groupJid, CancellationToken ct)
    {
        var r = await SendCommandAsync("getGroupMetadata", new { jid = groupJid }, ct: ct);
        if (r.ValueKind != JsonValueKind.Object) return null;
        var participants = r.TryGetProperty("participants", out var parts) && parts.ValueKind == JsonValueKind.Array
            ? parts.EnumerateArray().Select(p => new GroupParticipant(
                p.GetProperty("jid").GetString() ?? "",
                p.TryGetProperty("lidJid", out var l) ? l.GetString() ?? "" : "",
                p.TryGetProperty("type", out var ty) ? ty.GetString() ?? "member" : "member")).ToList()
            : new List<GroupParticipant>();
        return new GroupMetadata(
            r.GetProperty("jid").GetString() ?? groupJid,
            r.TryGetProperty("subject", out var s) ? s.GetString() ?? "" : "",
            r.TryGetProperty("creator", out var cr) ? cr.GetString() ?? "" : "",
            r.TryGetProperty("creationTimestamp", out var cts) && cts.ValueKind == JsonValueKind.Number ? cts.GetInt64() : 0,
            participants);
    }

    public async Task<string?> CreateGroupAsync(string subject, IEnumerable<string> participantJids, CancellationToken ct)
    {
        var r = await SendCommandAsync("createGroup", new { subject, participants = participantJids.ToArray() }, ct: ct);
        return r.ValueKind == JsonValueKind.String ? r.GetString() : null;
    }

    public Task LeaveGroupAsync(string groupJid, CancellationToken ct)
        => SendCommandAsync("leaveGroup", new { jid = groupJid }, ct: ct);

    public Task<Dictionary<string, string>> AddGroupParticipantsAsync(string groupJid, IEnumerable<string> jids, CancellationToken ct)
        => GroupParticipantsUpdateAsync(groupJid, jids, "add", ct);

    public Task<Dictionary<string, string>> RemoveGroupParticipantsAsync(string groupJid, IEnumerable<string> jids, CancellationToken ct)
        => GroupParticipantsUpdateAsync(groupJid, jids, "remove", ct);

    private async Task<Dictionary<string, string>> GroupParticipantsUpdateAsync(string groupJid, IEnumerable<string> jids, string action, CancellationToken ct)
    {
        var r = await SendCommandAsync("groupParticipantsUpdate", new { jid = groupJid, participants = jids.ToArray(), action }, ct: ct);
        var result = new Dictionary<string, string>();
        if (r.ValueKind == JsonValueKind.Object)
            foreach (var prop in r.EnumerateObject())
                result[prop.Name] = prop.Value.GetString() ?? "";
        return result;
    }

    public async Task<string?> GetGroupInviteLinkAsync(string groupJid, CancellationToken ct)
    {
        var r = await SendCommandAsync("getGroupInviteLink", new { jid = groupJid }, ct: ct);
        return r.ValueKind == JsonValueKind.String ? r.GetString() : null;
    }

    public Task UpdateGroupSubjectAsync(string groupJid, string newSubject, CancellationToken ct)
        => SendCommandAsync("updateGroupSubject", new { jid = groupJid, subject = newSubject }, ct: ct);

    // ─── Misc ─────────────────────────────────────────────────────────────────

    public MessageStatus? GetMessageStatus(string messageId)
        => _messageStatuses.TryGetValue(messageId, out var s) ? s : null;

    public object GetDebugInfo() => new
    {
        engine = EngineName,
        sidecarPid = _process is { HasExited: false } p ? p.Id : (int?)null,
        sidecarRunning = _process is { HasExited: false },
        scriptPath = _scriptPath,
        pendingCommands = _pending.Count,
    };

    public async ValueTask DisposeAsync()
    {
        _disposed = true;
        var process = _process;
        _process = null;
        if (process == null) return;

        try
        {
            if (!process.HasExited)
            {
                // Polite shutdown first so Baileys can flush creds, then hard kill.
                try
                {
                    await _stdinLock.WaitAsync(TimeSpan.FromSeconds(2));
                    try
                    {
                        await process.StandardInput.WriteLineAsync(
                            JsonSerializer.Serialize(new { id = 0, cmd = "shutdown" }, JsonOpts));
                        await process.StandardInput.FlushAsync();
                    }
                    finally { _stdinLock.Release(); }
                }
                catch { /* stdin may already be closed */ }

                if (!process.WaitForExit(3000))
                    process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception) { /* already gone */ }
        finally { process.Dispose(); }
    }
}
