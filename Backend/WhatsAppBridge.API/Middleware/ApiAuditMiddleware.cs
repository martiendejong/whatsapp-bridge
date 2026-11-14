using System.Diagnostics;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using WhatsAppBridge.API.Data;
using WhatsAppBridge.API.Models;

namespace WhatsAppBridge.API.Middleware;

/// <summary>
/// Writes one <see cref="ApiAuditLog"/> row for every request that reaches the API.
///
/// Placed BEFORE the authentication/authorization middlewares — that is what puts framework
/// 401/403 refusals in the log at all — while the row itself is written in the finally, after
/// downstream has run, so the identity and final status code are still known by then.
/// A failure to audit never fails the request — an unwritable log is a monitoring problem,
/// not a reason to drop someone's message — but it is surfaced via ILogger.
/// </summary>
public sealed class ApiAuditMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<ApiAuditMiddleware> _logger;

    /// <summary>
    /// Routes whose request body is credentials, not content. For these the body is never
    /// stored — not truncated, not hashed, simply absent. Matched as a case-insensitive
    /// path prefix.
    /// </summary>
    private static readonly string[] BodyRedactedPrefixes =
    {
        "/api/auth",
        "/api/iamauth",
        "/api/apiconnections",
    };

    /// <summary>
    /// Body fields that carry a message's text, in the order the various send endpoints use
    /// them. WhatsAppApiController uses "body"; WhatsAppController's session routes use
    /// "message"; the CoachOS/task forwarders use "text".
    /// </summary>
    private static readonly string[] BodyTextFields = { "body", "message", "text", "caption" };

    /// <summary>Body/query fields naming the number a request concerns.</summary>
    private static readonly string[] PhoneFields = { "to", "chatid", "phone", "jid", "recipient", "number" };

    private const int MaxBodyChars = 8000;
    private const int MaxResponseChars = 1000;

    public ApiAuditMiddleware(RequestDelegate next, ILogger<ApiAuditMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        // Non-API traffic (static files, the SPA itself) is noise in an audit trail.
        if (!context.Request.Path.StartsWithSegments("/api"))
        {
            await _next(context);
            return;
        }

        var sw = Stopwatch.StartNew();
        var path = context.Request.Path.Value ?? string.Empty;
        var redactBody = BodyRedactedPrefixes.Any(p => path.StartsWith(p, StringComparison.OrdinalIgnoreCase));

        string? rawBody = null;
        if (!redactBody && HasBody(context.Request))
        {
            rawBody = await ReadRequestBodyAsync(context.Request);
        }

        // Capture the response so a block reason lands next to the attempt that caused it.
        //
        // Pass-through rather than buffer-then-copy. The previous version replaced the response
        // stream with a MemoryStream, held the entire response, and copied it out afterwards —
        // which meant every media download through /api was held twice in memory, and, worse,
        // that an exception skipped the copy entirely: the client got an empty body for any
        // request that threw. This wrapper forwards each write to the real stream immediately and
        // keeps only the first kilobyte for the log.
        var originalBody = context.Response.Body;
        await using var capture = new PreviewCaptureStream(originalBody, MaxResponseChars,
            () => IsTextResponse(context.Response.ContentType));
        context.Response.Body = capture;

        var threw = false;
        try
        {
            await _next(context);
        }
        catch
        {
            // Recorded, then rethrown untouched: the exception handler upstream still decides
            // what the client sees. Without this the audit row said 200/"ok" for a request that
            // failed, because the status code is only set to 500 further up the pipeline —
            // after this middleware's finally block has already run.
            threw = true;
            throw;
        }
        finally
        {
            context.Response.Body = originalBody;
            sw.Stop();

            try
            {
                await WriteAuditAsync(context, path, rawBody, capture.GetPreview(),
                    (int)sw.ElapsedMilliseconds, threw);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to write API audit row for {Method} {Path}",
                    context.Request.Method, path);
            }
        }
    }

    /// <summary>
    /// Whether the response is worth previewing. Media contributes nothing readable to an audit
    /// row and would be stored as mangled bytes, so it is skipped by content type.
    ///
    /// Anything else is captured, including a response that declares no content type at all.
    /// Requiring a positive text/json header looked tidier and was wrong: middleware that
    /// short-circuits with a bare 403 writes its reason without setting one, so the very rows
    /// where the preview earns its keep — "why was this send refused" — came back empty.
    /// Guessing text for an untyped body costs at most a kilobyte of noise; guessing binary
    /// costs the evidence.
    /// </summary>
    private static bool IsTextResponse(string? contentType)
    {
        if (string.IsNullOrEmpty(contentType)) return true;
        return !(contentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase) ||
                 contentType.StartsWith("audio/", StringComparison.OrdinalIgnoreCase) ||
                 contentType.StartsWith("video/", StringComparison.OrdinalIgnoreCase) ||
                 contentType.StartsWith("font/", StringComparison.OrdinalIgnoreCase) ||
                 contentType.Contains("octet-stream", StringComparison.OrdinalIgnoreCase) ||
                 contentType.Contains("pdf", StringComparison.OrdinalIgnoreCase) ||
                 contentType.Contains("zip", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Writes straight through to the real response stream while keeping the first
    /// <c>maxChars</c> bytes for the audit row. Nothing is withheld from the client and nothing
    /// larger than the preview is retained.
    /// </summary>
    private sealed class PreviewCaptureStream : Stream
    {
        private readonly Stream _inner;
        private readonly int _max;
        private readonly Func<bool> _shouldCapture;
        private readonly MemoryStream _preview = new();
        private bool? _capturing;

        public PreviewCaptureStream(Stream inner, int max, Func<bool> shouldCapture)
        {
            _inner = inner;
            _max = max;
            _shouldCapture = shouldCapture;
        }

        public string? GetPreview()
        {
            if (_preview.Length == 0) return null;
            return Encoding.UTF8.GetString(_preview.ToArray());
        }

        private void Capture(ReadOnlySpan<byte> data)
        {
            // Decided once, on the first write: by then the handler has set the content type,
            // and re-checking per write would let a late header change split a body in half.
            _capturing ??= _shouldCapture();
            if (_capturing != true || _preview.Length >= _max) return;

            var room = _max - (int)_preview.Length;
            _preview.Write(data[..Math.Min(room, data.Length)]);
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            Capture(buffer.AsSpan(offset, count));
            _inner.Write(buffer, offset, count);
        }

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            Capture(buffer);
            _inner.Write(buffer);
        }

        public override async Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            Capture(buffer.AsSpan(offset, count));
            await _inner.WriteAsync(buffer.AsMemory(offset, count), cancellationToken);
        }

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Capture(buffer.Span);
            await _inner.WriteAsync(buffer, cancellationToken);
        }

        public override void Flush() => _inner.Flush();
        public override Task FlushAsync(CancellationToken cancellationToken) => _inner.FlushAsync(cancellationToken);

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing) _preview.Dispose();
            base.Dispose(disposing);
        }
    }

    private async Task WriteAuditAsync(HttpContext context, string path, string? rawBody,
        string? responsePreview, int durationMs, bool threw)
    {
        // A scope of our own, never the request's. The request-scoped AppDbContext is the one
        // the controller just used, and if the controller's SaveChanges threw — a unique-index
        // race, a poisoned entity — its change tracker still holds the failed entity. Reusing it
        // here re-attempts that same failed write, throws the same exception, and the audit row
        // vanishes for precisely the request that most needs a row. It would also silently flush
        // any tracked-but-unsaved leftovers a controller abandoned on an early error return.
        var scopeFactory = context.RequestServices.GetService<IServiceScopeFactory>();
        if (scopeFactory == null) return;
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetService<AppDbContext>();
        if (db == null) return;
        var encryption = scope.ServiceProvider.GetService<Services.EncryptionService>()
                         ?? new Services.EncryptionService(new ConfigurationBuilder().Build());

        // A request that threw has not reached the exception handler yet, so the response still
        // carries whatever status was set before the throw — usually 200. Recording that would
        // make the log claim success for the exact requests worth investigating.
        var status = threw && context.Response.StatusCode < 400 ? 500 : context.Response.StatusCode;
        var user = context.User;
        var isAuthenticated = user?.Identity?.IsAuthenticated == true;

        var entry = new ApiAuditLog
        {
            AtUtc = DateTime.UtcNow,
            UserId = ParseInt(user?.FindFirst(ClaimTypes.NameIdentifier)?.Value),
            ApiConnectionId = ParseInt(user?.FindFirst("ApiConnectionId")?.Value),
            ApiConnectionName = user?.FindFirst("ApiConnectionName")?.Value,
            AuthScheme = !isAuthenticated
                ? "Anonymous"
                : user!.FindFirst("ApiConnectionId") != null ? "ApiKey" : "Jwt",
            Method = context.Request.Method,
            Path = Truncate(path, 400)!,
            EventType = DeriveEventType(path),
            Phone = ExtractPhone(context.Request, rawBody),
            // Masked on the way in, not on the way out: rows are kept indefinitely by design, so
            // an approve link or login code stored in the clear stays usable for as long as the
            // database exists. See SecretMasker for what is and is not recognised.
            //
            // Then encrypted, under the same key and flag as the Messages table. Without this,
            // an encryption-enabled deployment kept a complete plaintext copy of every outbound
            // message here — the audit log would have quietly undone the at-rest encryption it
            // sat next to. When encryption is off, Encrypt is a pass-through.
            Body = encryption.Encrypt(SecretMasker.Apply(ExtractMessageText(rawBody))!),
            ResponsePreview = encryption.Encrypt(SecretMasker.Apply(responsePreview)!),
            StatusCode = status,
            Outcome = status is >= 200 and < 300 ? "ok" : status == 403 ? "blocked" : "error",
            DurationMs = durationMs,
            ClientIp = context.Connection.RemoteIpAddress?.ToString(),
        };

        db.ApiAuditLogs.Add(entry);
        await db.SaveChangesAsync();
    }

    /// <summary>
    /// Last non-numeric route segment, so "/api/wa/sendMessage" -> "sendMessage" and
    /// "/api/whatsapp/sessions/{id}/send-media" -> "send-media" rather than a per-session
    /// event type that would make the filter dropdown useless.
    /// </summary>
    private static string DeriveEventType(string path)
    {
        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        for (var i = segments.Length - 1; i >= 0; i--)
        {
            var s = segments[i];
            if (s.Length == 0) continue;
            if (s.All(char.IsDigit)) continue;          // numeric id
            if (Guid.TryParse(s, out _)) continue;      // session id
            return Truncate(s, 80)!;
        }
        return "unknown";
    }

    private static string? ExtractPhone(HttpRequest request, string? rawBody)
    {
        foreach (var field in PhoneFields)
        {
            foreach (var (key, value) in request.Query)
            {
                if (string.Equals(key, field, StringComparison.OrdinalIgnoreCase))
                {
                    var normalized = NormalizePhone(value.ToString());
                    if (!string.IsNullOrEmpty(normalized)) return normalized;
                }
            }
        }

        var fromBody = ReadJsonField(rawBody, PhoneFields);
        return string.IsNullOrEmpty(fromBody) ? null : NormalizePhone(fromBody);
    }

    private static string? ExtractMessageText(string? rawBody)
    {
        var text = ReadJsonField(rawBody, BodyTextFields);
        return string.IsNullOrEmpty(text) ? null : Truncate(text, MaxBodyChars);
    }

    /// <summary>
    /// First matching top-level field, case-insensitive. Multipart/form bodies (send-media)
    /// are not JSON, so this returns null for them — the row still records the endpoint,
    /// number and outcome, which is what matters for a media send.
    /// </summary>
    private static string? ReadJsonField(string? rawBody, string[] fields)
    {
        if (string.IsNullOrWhiteSpace(rawBody)) return null;

        try
        {
            using var doc = JsonDocument.Parse(rawBody);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return null;

            foreach (var field in fields)
            {
                foreach (var prop in doc.RootElement.EnumerateObject())
                {
                    if (!string.Equals(prop.Name, field, StringComparison.OrdinalIgnoreCase)) continue;
                    if (prop.Value.ValueKind == JsonValueKind.String) return prop.Value.GetString();
                    if (prop.Value.ValueKind is JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False)
                        return prop.Value.ToString();
                }
            }
        }
        catch (JsonException)
        {
            // Not JSON (form post, binary upload) — nothing to extract, not an error.
        }

        return null;
    }

    /// <summary>
    /// Shared with the guardrail and the routing policy so that "31633984381",
    /// "31633984381@c.us" and "254715438010:78@s.whatsapp.net" all filter as one number.
    /// </summary>
    private static string NormalizePhone(string s) => Services.PhoneNumber.Normalize(s);

    private static int? ParseInt(string? s) => int.TryParse(s, out var v) ? v : null;

    private static bool HasBody(HttpRequest request) =>
        request.ContentLength is > 0 ||
        (request.Headers.TryGetValue("Transfer-Encoding", out var te) && te.Count > 0);

    /// <summary>
    /// How much of a request body this middleware will pull into a string of its own. Far above
    /// any real message (the stored excerpt is 8000 chars) but a hard stop against the abuse
    /// case: an unauthenticated caller POSTing a maximum-size JSON body had it buffered here in
    /// full, per request, before any cap applied. A body over this limit no longer parses as
    /// JSON, so its row loses the extracted text and phone — endpoint, caller and outcome
    /// survive, which is what matters for a body that size.
    /// </summary>
    private const int MaxBodyReadChars = 256 * 1024;

    private static async Task<string?> ReadRequestBodyAsync(HttpRequest request)
    {
        // Only JSON is parsed for content; a multipart upload's bytes must not be buffered
        // into the audit row.
        var contentType = request.ContentType ?? string.Empty;
        if (!contentType.Contains("json", StringComparison.OrdinalIgnoreCase)) return null;

        request.EnableBuffering();
        using var reader = new StreamReader(request.Body, Encoding.UTF8, leaveOpen: true);

        var buffer = new char[8192];
        var sb = new StringBuilder();
        int read;
        while ((read = await reader.ReadAsync(buffer, 0, buffer.Length)) > 0)
        {
            sb.Append(buffer, 0, Math.Min(read, MaxBodyReadChars - sb.Length));
            if (sb.Length >= MaxBodyReadChars) break;
        }
        request.Body.Position = 0;
        return sb.ToString();
    }

    private static string? Truncate(string? s, int max) =>
        s == null ? null : s.Length <= max ? s : s[..max];
}
