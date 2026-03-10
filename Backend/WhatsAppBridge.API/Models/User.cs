using System.ComponentModel.DataAnnotations;

namespace WhatsAppBridge.API.Models;

public class User
{
    public int Id { get; set; }

    [Required]
    [EmailAddress]
    public string Email { get; set; } = string.Empty;

    [Required]
    public string PasswordHash { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime? LastLoginAt { get; set; }

    public bool IsActive { get; set; } = true;

    public bool IsAdmin { get; set; } = false;

    // Navigation properties
    public ICollection<ApiConnection> ApiConnections { get; set; } = new List<ApiConnection>();
    public ICollection<WhatsAppSession> WhatsAppSessions { get; set; } = new List<WhatsAppSession>();
}
