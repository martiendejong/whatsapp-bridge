using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Security.Claims;
using WhatsAppBridge.API.Data;
using WhatsAppBridge.API.Services;

namespace WhatsAppBridge.API.Controllers;

[ApiController]
[Route("api/[controller]")]
public class AuthController : ControllerBase
{
    private readonly AuthService _authService;
    private readonly TwoFactorService _twoFactorService;
    private readonly EmailService _emailService;
    private readonly AppDbContext _context;

    public AuthController(AuthService authService, TwoFactorService twoFactorService, EmailService emailService, AppDbContext context)
    {
        _authService = authService;
        _twoFactorService = twoFactorService;
        _emailService = emailService;
        _context = context;
    }

    private int GetUserId()
    {
        var userIdClaim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        return int.Parse(userIdClaim!);
    }

    [HttpPost("register")]
    public async Task<IActionResult> Register([FromBody] RegisterRequest request)
    {
        var user = await _authService.RegisterAsync(request.Email, request.Password);

        if (user == null)
            return BadRequest(new { message = "User already exists" });

        var token = _authService.GenerateJwtToken(user);

        return Ok(new
        {
            user = new { user.Id, user.Email, user.CreatedAt, user.IsAdmin },
            token
        });
    }

    [HttpPost("login")]
    public async Task<IActionResult> Login([FromBody] LoginRequest request)
    {
        var (user, token) = await _authService.LoginAsync(request.Email, request.Password);

        if (user == null || token == null)
            return Unauthorized(new { message = "Invalid credentials" });

        // Check if 2FA is enabled
        if (user.TwoFactorEnabled)
        {
            // Create 2FA token
            var twoFactorToken = await _twoFactorService.CreateTokenAsync(user.Id, user.TwoFactorMethod);

            // Send token based on method
            if (user.TwoFactorMethod == "whatsapp")
            {
                var sent = await _twoFactorService.SendWhatsAppTokenAsync(user.Id, twoFactorToken.Token);
                if (!sent)
                    return BadRequest(new { message = "Failed to send WhatsApp 2FA code. Please ensure you have a phone number linked." });
            }
            else if (user.TwoFactorMethod == "email")
            {
                var sent = await _twoFactorService.SendEmailTokenAsync(user.Email, twoFactorToken.Token);
                if (!sent)
                    return BadRequest(new { message = "Failed to send email 2FA code. Please check your email configuration." });
            }

            // Return pending 2FA status (no JWT token yet)
            return Ok(new
            {
                requiresTwoFactor = true,
                twoFactorMethod = user.TwoFactorMethod,
                userId = user.Id,
                message = user.TwoFactorMethod == "whatsapp"
                    ? "A verification code has been sent to your WhatsApp."
                    : "A verification code has been sent to your email."
            });
        }

        // No 2FA - return token immediately
        return Ok(new
        {
            user = new { user.Id, user.Email, user.LastLoginAt, user.CreatedAt, user.IsActive },
            token,
            requiresTwoFactor = false
        });
    }

    [HttpPost("verify-2fa")]
    public async Task<IActionResult> Verify2FA([FromBody] Verify2FARequest request)
    {
        var (success, user) = await _twoFactorService.ValidateTokenAsync(request.Token);

        if (!success || user == null)
            return BadRequest(new { message = "Invalid or expired verification code" });

        // Generate JWT token after successful 2FA
        var token = _authService.GenerateJwtToken(user);

        user.LastLoginAt = DateTime.UtcNow;
        await _authService.UpdateLastLoginAsync(user.Id);

        return Ok(new
        {
            user = new { user.Id, user.Email, user.LastLoginAt, user.CreatedAt, user.IsActive, user.IsAdmin },
            token
        });
    }

    [Authorize]
    [HttpGet("me")]
    public async Task<IActionResult> Me()
    {
        var userId = GetUserId();
        var user = await _authService.GetUserByIdAsync(userId);

        if (user == null)
            return NotFound();

        return Ok(new
        {
            user = new { user.Id, user.Email, user.LastLoginAt, user.CreatedAt, user.IsActive, user.IsAdmin }
        });
    }

    [Authorize]
    [HttpPut("update-email")]
    public async Task<IActionResult> UpdateEmail([FromBody] UpdateEmailRequest request)
    {
        var userId = GetUserId();
        var result = await _authService.UpdateEmailAsync(userId, request.Email);

        if (!result)
            return BadRequest(new { error = "Email already in use or invalid" });

        return Ok(new { message = "Email updated successfully" });
    }

    [Authorize]
    [HttpPut("update-password")]
    public async Task<IActionResult> UpdatePassword([FromBody] UpdatePasswordRequest request)
    {
        var userId = GetUserId();
        var result = await _authService.UpdatePasswordAsync(userId, request.CurrentPassword, request.NewPassword);

        if (!result)
            return BadRequest(new { error = "Current password is incorrect" });

        return Ok(new { message = "Password updated successfully" });
    }

    [Authorize]
    [HttpGet("2fa-status")]
    public async Task<IActionResult> GetTwoFactorStatus()
    {
        var userId = GetUserId();
        var user = await _authService.GetUserByIdAsync(userId);
        if (user == null) return NotFound();

        return Ok(new
        {
            twoFactorEnabled = user.TwoFactorEnabled,
            twoFactorMethod = user.TwoFactorMethod,
            hasPhoneNumber = !string.IsNullOrEmpty(user.PhoneNumber)
        });
    }

    [Authorize]
    [HttpPut("enable-2fa")]
    public async Task<IActionResult> EnableTwoFactor([FromBody] EnableTwoFactorRequest request)
    {
        var userId = GetUserId();
        var success = await _twoFactorService.EnableTwoFactorAsync(userId, request.Method, request.PhoneNumber);

        if (!success)
            return BadRequest(new { message = "Failed to enable 2FA. For WhatsApp method, ensure you have a connected WhatsApp session or provide a phone number." });

        return Ok(new { message = "Two-factor authentication enabled successfully", method = request.Method });
    }

    [Authorize]
    [HttpPut("disable-2fa")]
    public async Task<IActionResult> DisableTwoFactor()
    {
        var userId = GetUserId();
        await _twoFactorService.DisableTwoFactorAsync(userId);
        return Ok(new { message = "Two-factor authentication disabled" });
    }
}

public record RegisterRequest(string Email, string Password);
public record LoginRequest(string Email, string Password);
public record UpdateEmailRequest(string Email);
public record UpdatePasswordRequest(string CurrentPassword, string NewPassword);
public record Verify2FARequest(string Token);
public record EnableTwoFactorRequest(string Method, string? PhoneNumber);
