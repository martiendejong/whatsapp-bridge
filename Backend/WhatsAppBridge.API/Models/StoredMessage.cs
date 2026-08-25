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

    /// <summary>Owning bridge user - stable across QR re-pairs (a re-pair replaces the
    /// session row and its GUID, so SessionId alone cannot be used for ownership).</summary>
    public int? UserId { get; set; }

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

    /// <summary>
    /// Base64 media decryption key captured at ingest time (task 869ecw8du). WhatsApp CDN
    /// links are encrypted; without this key alongside MediaUrl the media can never be
    /// downloaded, even by the bridge. Null for rows persisted before this column existed.
    /// </summary>
    public string? MediaKey { get; set; }

    public string? MimeType { get; set; }

    /// <summary>Original WhatsApp timestamp (unix seconds).</summary>
    public long Timestamp { get; set; }

    /// <summary>When the bridge stored the message (UTC).</summary>
    public DateTime ReceivedAt { get; set; }

    /// <summary>True when this row came from a history-sync replay rather than live delivery.</summary>
    public bool IsHistory { get; set; }

    /// <summary>
    /// Whisper transcript, filled in automatically shortly after an audio message arrives
    /// (task 869ejuycr). Null for non-audio messages or while transcription is still pending
    /// (fire-and-forget — never blocks message ingest).
    /// </summary>
    public string? Transcript { get; set; }

    /// <summary>
    /// Absolute path to the already-decrypted media file on local disk, populated automatically
    /// at ingest time for any message with MediaUrl+MediaKey (task 869ejuycr) — so the plaintext
    /// file itself, not just the encrypted CDN link + key, is available without a further
    /// network round-trip. Null while decryption is pending or if it failed (readers fall back
    /// to on-demand decrypt via MediaUrl/MediaKey in that case).
    /// </summary>
    public string? LocalMediaPath { get; set; }
}
