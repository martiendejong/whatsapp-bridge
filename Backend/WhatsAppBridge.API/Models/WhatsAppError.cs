namespace WhatsAppBridge.API.Models;

/// <summary>
/// Represents a WhatsApp service error with user-friendly message and technical details
/// </summary>
public class WhatsAppError
{
    public string UserMessage { get; set; } = string.Empty;
    public string TechnicalMessage { get; set; } = string.Empty;
    public string ErrorCode { get; set; } = string.Empty;
    public Dictionary<string, string>? AdditionalInfo { get; set; }

    public static WhatsAppError QrExpired(string sessionId) => new()
    {
        UserMessage = "Your WhatsApp QR code has expired. Please reconnect your WhatsApp account by scanning a new QR code.",
        TechnicalMessage = $"QR code expired for session {sessionId}",
        ErrorCode = "QR_EXPIRED"
    };

    public static WhatsAppError SessionNotFound(string sessionId) => new()
    {
        UserMessage = "WhatsApp session not found or not connected. Please ensure your WhatsApp is connected.",
        TechnicalMessage = $"Session {sessionId} not found or disconnected",
        ErrorCode = "SESSION_NOT_FOUND"
    };

    public static WhatsAppError SessionDisconnected(string sessionId) => new()
    {
        UserMessage = "Your WhatsApp connection was lost. Please reconnect by scanning a new QR code.",
        TechnicalMessage = $"Session {sessionId} disconnected",
        ErrorCode = "SESSION_DISCONNECTED"
    };

    public static WhatsAppError RateLimitExceeded() => new()
    {
        UserMessage = "Too many requests. Please wait a moment before trying again.",
        TechnicalMessage = "WhatsApp rate limit exceeded",
        ErrorCode = "RATE_LIMIT"
    };

    public static WhatsAppError InvalidNumber(string number) => new()
    {
        UserMessage = $"The phone number '{number}' is not registered on WhatsApp or is invalid.",
        TechnicalMessage = $"Invalid or unregistered WhatsApp number: {number}",
        ErrorCode = "INVALID_NUMBER"
    };

    public static WhatsAppError ServiceUnavailable() => new()
    {
        UserMessage = "WhatsApp service is temporarily unavailable. Please try again in a moment.",
        TechnicalMessage = "WhatsApp service connection failed",
        ErrorCode = "SERVICE_UNAVAILABLE"
    };

    public static WhatsAppError MessageFailed(string reason) => new()
    {
        UserMessage = "Failed to send message. Please check your connection and try again.",
        TechnicalMessage = $"Message send failed: {reason}",
        ErrorCode = "MESSAGE_FAILED"
    };

    public static WhatsAppError UnknownError(string details) => new()
    {
        UserMessage = "An unexpected error occurred. Our team has been notified.",
        TechnicalMessage = details,
        ErrorCode = "UNKNOWN_ERROR"
    };
}

/// <summary>
/// Exception thrown by WhatsApp service operations
/// </summary>
public class WhatsAppServiceException : Exception
{
    public WhatsAppError Error { get; }

    public WhatsAppServiceException(WhatsAppError error) : base(error.TechnicalMessage)
    {
        Error = error;
    }

    public WhatsAppServiceException(WhatsAppError error, Exception innerException)
        : base(error.TechnicalMessage, innerException)
    {
        Error = error;
    }
}
