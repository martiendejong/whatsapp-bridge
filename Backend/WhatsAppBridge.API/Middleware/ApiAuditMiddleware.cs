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
/// Placed after UseAuthentication so the caller's identity (which API key) is already
/// resolved, and around the rest of the pipeline so the status code and duration are known.
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
        var originalBody = context.Response.Body;
        using var buffer = new MemoryStream();
        context.Response.Body = buffer;

        string? responsePreview = null;
        try
        {
            await _next(context);

            buffer.Position = 0;
            if (buffer.Length > 0)
            {
                using var reader = new StreamReader(buffer, Encoding.UTF8, leaveOpen: true);
                var text = await reader.ReadToEndAsync();
                responsePreview = Truncate(text, MaxResponseChars);
            }

            buffer.Position = 0;
            await buffer.CopyToAsync(originalBody);
        }
        finally
        {
            context.Response.Body = originalBody;
            sw.Stop();

            try
            {
                await WriteAuditAsync(context, path, rawBody, responsePreview, (int)sw.ElapsedMilliseconds);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to write API audit row for {Method} {Path}",
                    context.Request.Method, path);
            }
        }
    }

    private async Task WriteAuditAsync(HttpContext context, string path, string? rawBody,
        string? responsePreview, int durationMs)
    {
        var db = context.RequestServices.GetService<AppDbContext>();
        if (db == null) return;

        var status = context.Response.StatusCode;
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
            Body = ExtractMessageText(rawBody),
            ResponsePreview = responsePreview,
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
    /// Leading digit run, matching OutboundGuardrailService.Normalize so that "31633984381",
    /// "31633984381@c.us" and "254715438010:78@s.whatsapp.net" all filter as one number.
    /// </summary>
    private static string NormalizePhone(string s) =>
        new(s.SkipWhile(c => !char.IsDigit(c)).TakeWhile(char.IsDigit).ToArray());

    private static int? ParseInt(string? s) => int.TryParse(s, out var v) ? v : null;

    private static bool HasBody(HttpRequest request) =>
        request.ContentLength is > 0 ||
        (request.Headers.TryGetValue("Transfer-Encoding", out var te) && te.Count > 0);

    private static async Task<string?> ReadRequestBodyAsync(HttpRequest request)
    {
        // Only JSON is parsed for content; a multipart upload's bytes must not be buffered
        // into the audit row.
        var contentType = request.ContentType ?? string.Empty;
        if (!contentType.Contains("json", StringComparison.OrdinalIgnoreCase)) return null;

        request.EnableBuffering();
        using var reader = new StreamReader(request.Body, Encoding.UTF8, leaveOpen: true);
        var body = await reader.ReadToEndAsync();
        request.Body.Position = 0;
        return body;
    }

    private static string? Truncate(string? s, int max) =>
        s == null ? null : s.Length <= max ? s : s[..max];
}
