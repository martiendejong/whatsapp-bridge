namespace WhatsAppBridge.API.Models;

/// <summary>
/// One row per outbound send the guardrail allowed through (task 897, 2026-08-30) — the
/// accounting record <see cref="Services.OutboundGuardrailService"/> uses to enforce its
/// per-recipient/24h and global/hour volume caps. Deliberately separate from
/// <see cref="BlockedOutboundMessage"/>, which only records refused attempts; this table
/// records attempts that were let through, regardless of whether the downstream WhatsApp
/// send itself later succeeded.
/// </summary>
public class OutboundSendLog
{
    public long Id { get; set; }

    /// <summary>Normalized recipient (digits only, lowercased) — matches the guardrail's own Normalize().</summary>
    public string Recipient { get; set; } = string.Empty;

    public DateTime SentAtUtc { get; set; }

    /// <summary>
    /// Message category the caller declared ("approval", "deploy:valsuani", ...), or null for
    /// callers predating categories. Null is treated as "other" by the routing policy.
    /// </summary>
    public string? Category { get; set; }

    /// <summary>
    /// Stable hash of the message text. Exists so a redirect can tell "Martien already got
    /// exactly this notice a minute ago because the caller fanned out to both of us" apart
    /// from "this is a genuine second alert", without storing the text twice.
    /// </summary>
    public string? BodyHash { get; set; }
}
