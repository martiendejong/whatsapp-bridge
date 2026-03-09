namespace WhatsAppBridge.Services;

/// <summary>
/// In-memory store for the current QR code string.
/// Allows the /qr endpoint to return the latest QR to the caller.
/// </summary>
public sealed class QRCodeStore
{
    private string? _currentQR;
    private readonly object _lock = new();

    public void Set(string qr)
    {
        lock (_lock) _currentQR = qr;
    }

    public void Clear()
    {
        lock (_lock) _currentQR = null;
    }

    public string? Get()
    {
        lock (_lock) return _currentQR;
    }
}
