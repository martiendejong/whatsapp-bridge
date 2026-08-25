namespace WhatsAppBridge.API.Models;

/// <summary>
/// Audit record of an outbound send refused by the server-side outbound guardrail
/// (task 869edf485, 2026-08-04): a message to a recipient not on the allow-list, sent
/// outside quiet hours, was blocked rather than silently dropped. Kept so a blocked send
/// is discoverable (GET /api/wa/blockedOutbound) instead of only living in a log line.
/// </summary>
public class BlockedOutboundMessage
{
    public long Id { get; set; }

    /// <summary>Owning bridge user whose API token attempted the send.</summary>
    public int? UserId { get; set; }

    /// <summary>Endpoint that attempted the send (sendMessage, sendMedia, forwardMessage).</summary>
    public string Endpoint { get; set; } = string.Empty;

    /// <summary>Recipient as passed by the caller (phone number or JID).</summary>
    public string Recipient { get; set; } = string.Empty;

    /// <summary>First 200 chars of the message body — enough to audit, not the full content.</summary>
    public string BodyPreview { get; set; } = string.Empty;

    public string Reason { get; set; } = string.Empty;

    public DateTime BlockedAtUtc { get; set; }
}
