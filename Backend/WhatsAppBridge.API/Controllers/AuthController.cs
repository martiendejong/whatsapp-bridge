using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Security.Claims;
using WhatsAppBridge.API.Services;

namespace WhatsAppBridge.API.Controllers;

[ApiController]
[Route("api/[controller]")]
public class AuthController : ControllerBase
{
    private readonly AuthService _authService;
    private readonly TwoFactorService _twoFactorService;

    public AuthController(AuthService authService, TwoFactorService twoFactorService)
    {
        _authService = authService;
        _twoFactorService = twoFactorService;
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
            user = new { user.Id, user.Email, user.CreatedAt },
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
            user = new { user.Id, user.Email, user.LastLoginAt, user.CreatedAt, user.IsActive },
            token
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
}

public record RegisterRequest(string Email, string Password);
public record LoginRequest(string Email, string Password);
public record UpdateEmailRequest(string Email);
public record UpdatePasswordRequest(string CurrentPassword, string NewPassword);
public record Verify2FARequest(string Token);
