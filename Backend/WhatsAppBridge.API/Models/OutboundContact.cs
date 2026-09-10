namespace WhatsAppBridge.API.Models;

/// <summary>
/// Who may be messaged, about what, and at what hour in their own timezone.
///
/// The allow-list in OutboundGuardrailOptions answers only "may this number be messaged at
/// all". That was enough when Martien was the sole recipient, but the actual policy has three
/// dimensions: WHO, about WHICH KIND of message, and WHEN in THEIR local time. Sjoerd should
/// hear about a Valsuani deploy at 14:00 Amsterdam time and about nothing else ever; the same
/// message at 03:00 must go to Martien instead, because Sjoerd is asleep.
///
/// A number with no row here is unknown to the policy and is never auto-messaged — the row is
/// the opt-in, not the block. Deleting a row therefore silences someone completely, which is
/// the intended fail-closed direction.
/// </summary>
public class OutboundContact
{
    public int Id { get; set; }

    /// <summary>Bare digits, normalized the same way as everywhere else in this codebase.</summary>
    public string Phone { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Short handle callers may use instead of the number ("martien", "sjoerd"), so a script
    /// does not have to carry phone numbers around. Optional but recommended.
    /// </summary>
    public string? Alias { get; set; }

    /// <summary>
    /// Off means "this person is not messaged, full stop", regardless of category or window.
    /// Kept separate from deleting the row so the configured windows survive a temporary mute.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// IANA id, e.g. "Europe/Amsterdam" or "Africa/Nairobi". Windows are evaluated in THIS
    /// zone, not the server's — the team spans two zones and a server-local window would be
    /// silently wrong for one of them. An unrecognised id is treated as UTC and logged.
    /// </summary>
    public string TimeZoneId { get; set; } = "Europe/Amsterdam";

    /// <summary>
    /// Local hour the window opens (0-23) and closes (1-24). 0 and 24 mean "always". A window
    /// that wraps midnight (start 22, end 6) is supported and read as "22:00 until 06:00".
    /// </summary>
    public int WindowStartHour { get; set; } = 0;

    public int WindowEndHour { get; set; } = 24;

    /// <summary>
    /// Comma-separated category names this contact accepts, or "*" for all. Matching is
    /// case-insensitive and hierarchical on ":" — "deploy" matches "deploy:valsuani", so a
    /// contact can opt into a whole family or one specific member.
    /// </summary>
    public string Categories { get; set; } = "*";

    /// <summary>
    /// Where a message goes when this contact is outside their window. Empty means the message
    /// is simply blocked. Set to Martien for anything that must not be lost overnight.
    /// </summary>
    public string? FallbackPhone { get; set; }

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
}
