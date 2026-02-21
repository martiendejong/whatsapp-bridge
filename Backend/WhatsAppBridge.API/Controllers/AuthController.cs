using Microsoft.AspNetCore.Mvc;
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

        return Ok(new
        {
            user = new { user.Id, user.Email, user.LastLoginAt },
            token
        });
    }
}

public record RegisterRequest(string Email, string Password);
public record LoginRequest(string Email, string Password);
