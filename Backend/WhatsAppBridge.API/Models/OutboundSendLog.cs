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

    /// <summary>
    /// Whether the WhatsApp send this row accounts for actually went out. The guardrail writes
    /// the row BEFORE the caller performs the send (that ordering is what makes the volume caps
    /// unskippable), so on its own the row only proves an attempt. The caller confirms after the
    /// send succeeds.
    ///
    /// The split exists because the two consumers of this table need opposite readings. The
    /// volume caps count attempts — a runaway retry loop must exhaust its budget whether or not
    /// its sends work. The duplicate suppression counts deliveries: it once counted attempts,
    /// which meant a send that failed (session down, exception) still registered as "the
    /// recipient already has this", and the redirect that would have rescued the message was
    /// suppressed. The alert vanished with all indicators green.
    ///
    /// Rows written before this column existed default to true: they were overwhelmingly real
    /// deliveries, and reading them as attempts would disable dedupe for the first ten minutes
    /// after deploy.
    /// </summary>
    public bool Delivered { get; set; }
}
