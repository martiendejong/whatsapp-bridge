namespace WhatsAppBridge.API.Models;

/// <summary>
/// One row per HTTP request that reaches the bridge — the complete "what actually happened"
/// record Martien asked for: which API key called, which number it concerned, what was sent,
/// and whether it went out or was blocked.
///
/// Why a middleware-written table and not per-controller logging: the outbound guardrail is
/// only invoked by 3 of the 20+ endpoints (sendMessage, sendMedia, forwardMessage) — every
/// other route, including WhatsAppController's own session send endpoints, bypasses it
/// entirely. Any audit trail wired in per-controller would inherit exactly that gap, and the
/// gap would silently re-open every time someone adds an endpoint. Middleware cannot be
/// forgotten, so "elk request wordt gelogd" stays true by construction.
///
/// Distinct from the two existing tables, which stay as they are:
///   - BlockedOutboundMessages: only blocked sends, kept because the guardrail owns the
///     authoritative block reason and GET /api/wa/blockedOutbound already serves it.
///   - OutboundSendLogs: recipient+timestamp only, deliberately narrow because it is the
///     guardrail's own volume-cap accounting and is queried on every send.
/// This table is the human-facing history; those two are machinery.
/// </summary>
public class ApiAuditLog
{
    public int Id { get; set; }

    public DateTime AtUtc { get; set; }

    /// <summary>Authenticated user, when the request carried credentials at all.</summary>
    public int? UserId { get; set; }

    /// <summary>
    /// WHICH API key made the call. The raw token is deliberately never stored — the
    /// ApiConnection row id plus its human-readable name is enough to answer "wat is er via
    /// deze sleutel verstuurd" without putting a live credential in a queryable table.
    /// </summary>
    public int? ApiConnectionId { get; set; }
    public string? ApiConnectionName { get; set; }

    /// <summary>ApiKey, Jwt, or Anonymous — how the caller authenticated.</summary>
    public string AuthScheme { get; set; } = "Anonymous";

    public string Method { get; set; } = string.Empty;
    public string Path { get; set; } = string.Empty;

    /// <summary>
    /// Coarse action name derived from the route's last segment (sendMessage, getChats,
    /// send-media, ...). This is the "event type" filter dimension.
    /// </summary>
    public string EventType { get; set; } = string.Empty;

    /// <summary>
    /// Normalized leading digit-run of whichever number the request concerned — the `to` of a
    /// send, the `chatId` of a read. Same normalization as OutboundGuardrailService so a JID
    /// ("31633984381@c.us") and a bare number filter identically. Null for requests that
    /// concern no specific number. This is the "phone number" filter dimension.
    /// </summary>
    public string? Phone { get; set; }

    /// <summary>
    /// The outbound message text, in full, for send-type endpoints. This is the point of the
    /// table: "exact kunnen zien wat verstuurd is". Null for reads and for any route on the
    /// redaction list (auth/IAM), where the body is credentials rather than content.
    /// </summary>
    public string? Body { get; set; }

    /// <summary>Truncated response body, so a block reason is visible next to the attempt.</summary>
    public string? ResponsePreview { get; set; }

    public int StatusCode { get; set; }

    /// <summary>ok | blocked | error — derived from the status code.</summary>
    public string Outcome { get; set; } = "ok";

    public int DurationMs { get; set; }

    public string? ClientIp { get; set; }
}
