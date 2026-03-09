using System.ComponentModel.DataAnnotations;

namespace WhatsAppBridge.Models;

public sealed class SendMessageRequest
{
    /// <summary>Phone number or JID. Examples: "+31612345678", "31612345678@s.whatsapp.net"</summary>
    [Required]
    public string To { get; set; } = "";

    /// <summary>Text message content.</summary>
    [Required]
    [MaxLength(4096)]
    public string Text { get; set; } = "";
}

public sealed class StatusResponse
{
    public string State { get; set; } = "";
    public bool IsConnected { get; set; }
    public bool HasSession { get; set; }
    public bool QRPending { get; set; }
}

public sealed class SendResult
{
    public bool Success { get; set; }
    public string? Error { get; set; }
}
