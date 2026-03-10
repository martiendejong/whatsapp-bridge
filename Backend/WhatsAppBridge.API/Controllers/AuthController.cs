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

    public AuthController(AuthService authService)
    {
        _authService = authService;
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

        return Ok(new
        {
            user = new { user.Id, user.Email, user.LastLoginAt, user.CreatedAt, user.IsActive, user.IsAdmin },
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
