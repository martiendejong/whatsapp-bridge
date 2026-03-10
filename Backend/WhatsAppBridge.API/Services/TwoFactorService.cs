using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using WhatsAppBridge.API.Data;
using WhatsAppBridge.API.Models;

namespace WhatsAppBridge.API.Services;

public class TwoFactorService
{
    private readonly AppDbContext _context;
    private readonly WhatsAppBridgeService _whatsappService;
    private readonly ILogger<TwoFactorService> _logger;
    private readonly IConfiguration _configuration;

    public TwoFactorService(
        AppDbContext context,
        WhatsAppBridgeService whatsappService,
        ILogger<TwoFactorService> logger,
        IConfiguration configuration)
    {
        _context = context;
        _whatsappService = whatsappService;
        _logger = logger;
        _configuration = configuration;
    }

    /// <summary>
    /// Generate a 6-digit 2FA code
    /// </summary>
    public string GenerateToken()
    {
        return RandomNumberGenerator.GetInt32(100000, 999999).ToString();
    }

    /// <summary>
    /// Send 2FA token via WhatsApp
    /// </summary>
    public async Task<bool> SendWhatsAppTokenAsync(int userId, string token)
    {
        try
        {
            var user = await _context.Users
                .Include(u => u.WhatsAppSessions)
                .FirstOrDefaultAsync(u => u.Id == userId);

            if (user == null)
                return false;

            // Get phone number (user's phone or first WhatsApp session)
            var phoneNumber = user.PhoneNumber;
            if (string.IsNullOrEmpty(phoneNumber))
            {
                var session = user.WhatsAppSessions
                    .OrderByDescending(s => s.ConnectedAt)
                    .FirstOrDefault();

                if (session != null && !string.IsNullOrEmpty(session.PhoneNumber))
                    phoneNumber = session.PhoneNumber;
            }

            if (string.IsNullOrEmpty(phoneNumber))
            {
                _logger.LogWarning("No phone number found for user {UserId}", userId);
                return false;
            }

            // Get first active session to send from
            var sendingSession = await _context.WhatsAppSessions
                .Where(s => s.Status == "connected")
                .OrderByDescending(s => s.ConnectedAt)
                .FirstOrDefaultAsync();

            if (sendingSession == null)
            {
                _logger.LogWarning("No active WhatsApp session available to send 2FA token");
                return false;
            }

            // Generate verification link
            var baseUrl = _configuration["BaseUrl"] ?? "http://localhost:5173";
            var verificationLink = $"{baseUrl}/verify-2fa?token={token}";

            // Send WhatsApp message
            var message = $"Your WhatsApp Bridge login code is: *{token}*\n\n" +
                         $"Or click here to verify: {verificationLink}\n\n" +
                         $"This code expires in 10 minutes.";

            await _whatsappService.SendMessageAsync(sendingSession.SessionId, phoneNumber, message);

            _logger.LogInformation("2FA token sent via WhatsApp to user {UserId}", userId);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error sending WhatsApp 2FA token to user {UserId}", userId);
            return false;
        }
    }

    /// <summary>
    /// Create and store a 2FA token
    /// </summary>
    public async Task<TwoFactorToken> CreateTokenAsync(int userId, string method)
    {
        var token = GenerateToken();

        var twoFactorToken = new TwoFactorToken
        {
            UserId = userId,
            Token = token,
            Method = method,
            ExpiresAt = DateTime.UtcNow.AddMinutes(10)
        };

        _context.TwoFactorTokens.Add(twoFactorToken);
        await _context.SaveChangesAsync();

        return twoFactorToken;
    }

    /// <summary>
    /// Validate a 2FA token
    /// </summary>
    public async Task<(bool success, User? user)> ValidateTokenAsync(string token)
    {
        var twoFactorToken = await _context.TwoFactorTokens
            .Include(t => t.User)
            .Where(t => t.Token == token && !t.IsUsed && t.ExpiresAt > DateTime.UtcNow)
            .OrderByDescending(t => t.CreatedAt)
            .FirstOrDefaultAsync();

        if (twoFactorToken == null)
            return (false, null);

        // Mark token as used
        twoFactorToken.IsUsed = true;
        twoFactorToken.UsedAt = DateTime.UtcNow;
        await _context.SaveChangesAsync();

        return (true, twoFactorToken.User);
    }

    /// <summary>
    /// Clean up expired tokens
    /// </summary>
    public async Task CleanupExpiredTokensAsync()
    {
        var expiredTokens = await _context.TwoFactorTokens
            .Where(t => t.ExpiresAt < DateTime.UtcNow.AddHours(-1))
            .ToListAsync();

        _context.TwoFactorTokens.RemoveRange(expiredTokens);
        await _context.SaveChangesAsync();

        _logger.LogInformation("Cleaned up {Count} expired 2FA tokens", expiredTokens.Count);
    }
}
