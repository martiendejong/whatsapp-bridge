namespace WhatsAppBridge.API.Models;

/// <summary>
/// Durable copy of every message that passes through the StoreMessage funnel (incoming,
/// outgoing mirror, and history replays). The in-memory message store is capped per chat
/// and starts empty after an app-pool restart or session re-pair; this table is the
/// append-only record that survives both, so a reply that was decrypted once can never
/// be lost again (task 869ecbkv7).
/// </summary>
public class StoredMessage
{
    public long Id { get; set; }

    /// <summary>Bridge session GUID the message arrived on.</summary>
    public string SessionId { get; set; } = string.Empty;

    /// <summary>Conversation JID as Dawa resolved it (e.g. 31633984381@s.whatsapp.net).</summary>
    public string ChatJid { get; set; } = string.Empty;

    /// <summary>WhatsApp message id — unique per session; dedupe key with SessionId.</summary>
    public string MessageId { get; set; } = string.Empty;

    public bool FromMe { get; set; }

    /// <summary>Sender JID for incoming messages; "me" for our own.</summary>
    public string Sender { get; set; } = string.Empty;

    public string Body { get; set; } = string.Empty;

    /// <summary>Message type as lowercase string (text, image, ...).</summary>
    public string Type { get; set; } = "text";

    public string? MediaUrl { get; set; }

    /// <summary>Original WhatsApp timestamp (unix seconds).</summary>
    public long Timestamp { get; set; }

    /// <summary>When the bridge stored the message (UTC).</summary>
    public DateTime ReceivedAt { get; set; }

    /// <summary>True when this row came from a history-sync replay rather than live delivery.</summary>
    public bool IsHistory { get; set; }
}
