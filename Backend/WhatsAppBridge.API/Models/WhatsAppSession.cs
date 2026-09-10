using System.ComponentModel.DataAnnotations;

namespace WhatsAppBridge.API.Models;

public class WhatsAppSession
{
    public int Id { get; set; }

    public int UserId { get; set; }

    [Required]
    public string SessionId { get; set; } = string.Empty; // Unique identifier for whatsapp-web.js

    public string PhoneNumber { get; set; } = string.Empty; // Encrypted if encryption enabled

    public string Status { get; set; } = "disconnected"; // disconnected, qr_pending, connected

    public string? QrCode { get; set; } // Base64 QR code when status is qr_pending

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime? ConnectedAt { get; set; }

    public DateTime? LastSeenAt { get; set; }

    // Which engine ("dawa"/"baileys") this session last (re)connected with. Informational —
    // the active engine choice is the global AppSettings value; column self-healed in Program.cs.
    public string? Engine { get; set; }

    // Navigation properties
    public User User { get; set; } = null!;
}
