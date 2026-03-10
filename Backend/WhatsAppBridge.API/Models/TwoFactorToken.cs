using System.ComponentModel.DataAnnotations;

namespace WhatsAppBridge.API.Models;

public class TwoFactorToken
{
    public int Id { get; set; }

    public int UserId { get; set; }

    [Required]
    public string Token { get; set; } = string.Empty;

    [Required]
    public string Method { get; set; } = string.Empty; // email or whatsapp

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime ExpiresAt { get; set; }

    public bool IsUsed { get; set; } = false;

    public DateTime? UsedAt { get; set; }

    // Navigation
    public User User { get; set; } = null!;
}
