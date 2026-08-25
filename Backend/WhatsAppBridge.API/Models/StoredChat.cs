namespace WhatsAppBridge.API.Models;

/// <summary>
/// Persists known WhatsApp chats to SQLite so getChats can return them even after an
/// app-pool restart, a session re-pair, or a deploy that wiped the Dawa session dir.
/// Upserted every time a live getChats call succeeds; read back when Dawa is offline.
/// </summary>
public class StoredChat
{
    public long Id { get; set; }

    /// <summary>Owning bridge user — stable across QR re-pairs.</summary>
    public int UserId { get; set; }

    /// <summary>Full JID as Dawa resolved it (e.g. 31633984381@s.whatsapp.net).</summary>
    public string Jid { get; set; } = string.Empty;

    /// <summary>Display name from WhatsApp.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Bare phone number (JID without suffix and resource, e.g. 31633984381).</summary>
    public string Phone { get; set; } = string.Empty;

    /// <summary>Last time this chat was seen in a live getChats response (UTC).</summary>
    public DateTime LastSeenAt { get; set; }
}
