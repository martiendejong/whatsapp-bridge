using System.ComponentModel.DataAnnotations;

namespace WhatsAppBridge.API.Models;

public class ApiConnection
{
    public int Id { get; set; }

    public int UserId { get; set; }

    [Required]
    public string Name { get; set; } = string.Empty;

    [Required]
    public string Token { get; set; } = string.Empty; // Encrypted if encryption enabled

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime? LastUsedAt { get; set; }

    public bool IsActive { get; set; } = true;

    // Navigation properties
    public User User { get; set; } = null!;
}
