using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using WhatsAppBridge.API.Data;
using WhatsAppBridge.API.Models;

namespace WhatsAppBridge.API.Services;

public class AuthService
{
    private readonly AppDbContext _context;
    private readonly IConfiguration _configuration;

    public AuthService(AppDbContext context, IConfiguration configuration)
    {
        _context = context;
        _configuration = configuration;
    }

    public async Task<User?> RegisterAsync(string email, string password)
    {
        if (await _context.Users.AnyAsync(u => u.Email == email))
            return null;

        var user = new User
        {
            Email = email,
            PasswordHash = BCrypt.Net.BCrypt.HashPassword(password)
        };

        _context.Users.Add(user);
        await _context.SaveChangesAsync();

        return user;
    }

    public async Task<(User? user, string? token)> LoginAsync(string email, string password)
    {
        var user = await _context.Users.FirstOrDefaultAsync(u => u.Email == email);

        if (user == null || !user.IsActive)
            return (null, null);

        if (!BCrypt.Net.BCrypt.Verify(password, user.PasswordHash))
            return (null, null);

        user.LastLoginAt = DateTime.UtcNow;
        await _context.SaveChangesAsync();

        var token = GenerateJwtToken(user);
        return (user, token);
    }

    public string GenerateJwtToken(User user)
    {
        var tokenHandler = new JwtSecurityTokenHandler();
        var key = Encoding.ASCII.GetBytes(_configuration["Jwt:Key"] ?? throw new InvalidOperationException("JWT Key not configured"));

        var tokenDescriptor = new SecurityTokenDescriptor
        {
            Subject = new ClaimsIdentity(new[]
            {
                new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()),
                new Claim(ClaimTypes.Email, user.Email),
                new Claim(ClaimTypes.Role, user.IsAdmin ? "Admin" : "User")
            }),
            Expires = DateTime.UtcNow.AddDays(7),
            SigningCredentials = new SigningCredentials(new SymmetricSecurityKey(key), SecurityAlgorithms.HmacSha256Signature),
            Issuer = _configuration["Jwt:Issuer"],
            Audience = _configuration["Jwt:Audience"]
        };

        var token = tokenHandler.CreateToken(tokenDescriptor);
        return tokenHandler.WriteToken(token);
    }

    public async Task<string> GenerateApiToken(int userId, string connectionName)
    {
        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

        var apiConnection = new ApiConnection
        {
            UserId = userId,
            Name = connectionName,
            Token = token
        };

        _context.ApiConnections.Add(apiConnection);
        await _context.SaveChangesAsync();

        return token;
    }

    public async Task<User?> ValidateApiTokenAsync(string token)
    {
        var connection = await _context.ApiConnections
            .Include(c => c.User)
            .FirstOrDefaultAsync(c => c.Token == token && c.IsActive);

        if (connection == null || !connection.User.IsActive)
            return null;

        connection.LastUsedAt = DateTime.UtcNow;
        await _context.SaveChangesAsync();

        return connection.User;
    }

    public async Task<bool> UpdateEmailAsync(int userId, string newEmail)
    {
        var user = await _context.Users.FindAsync(userId);
        if (user == null)
            return false;

        // Check if email is already in use by another user
        if (await _context.Users.AnyAsync(u => u.Email == newEmail && u.Id != userId))
            return false;

        user.Email = newEmail;
        await _context.SaveChangesAsync();

        return true;
    }

    public async Task<bool> UpdatePasswordAsync(int userId, string currentPassword, string newPassword)
    {
        var user = await _context.Users.FindAsync(userId);
        if (user == null)
            return false;

        // Verify current password
        if (!BCrypt.Net.BCrypt.Verify(currentPassword, user.PasswordHash))
            return false;

        // Update to new password
        user.PasswordHash = BCrypt.Net.BCrypt.HashPassword(newPassword);
        await _context.SaveChangesAsync();

        return true;
    }
}
