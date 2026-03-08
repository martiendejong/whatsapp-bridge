using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using WhatsAppBridge.API.Data;
using WhatsAppBridge.API.Services;

namespace WhatsAppBridge.API.Authentication;

public class ApiKeyAuthenticationOptions : AuthenticationSchemeOptions
{
    public const string DefaultScheme = "ApiKey";
    public string Scheme => DefaultScheme;
    public string ApiKeyHeaderName { get; set; } = "X-Api-Token";
    public string EmailHeaderName { get; set; } = "X-Email";
}

public class ApiKeyAuthenticationHandler : AuthenticationHandler<ApiKeyAuthenticationOptions>
{
    private readonly AppDbContext _context;
    private readonly EncryptionService _encryptionService;

    public ApiKeyAuthenticationHandler(
        IOptionsMonitor<ApiKeyAuthenticationOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder,
        AppDbContext context,
        EncryptionService encryptionService)
        : base(options, logger, encoder)
    {
        _context = context;
        _encryptionService = encryptionService;
    }

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        // Check for API token header
        if (!Request.Headers.TryGetValue(Options.ApiKeyHeaderName, out var apiTokenValues))
        {
            return AuthenticateResult.Fail("Missing API token header");
        }

        // Check for email header
        if (!Request.Headers.TryGetValue(Options.EmailHeaderName, out var emailValues))
        {
            return AuthenticateResult.Fail("Missing email header");
        }

        var providedToken = apiTokenValues.FirstOrDefault();
        var providedEmail = emailValues.FirstOrDefault();

        if (string.IsNullOrWhiteSpace(providedToken) || string.IsNullOrWhiteSpace(providedEmail))
        {
            return AuthenticateResult.Fail("Invalid API token or email");
        }

        // Find user by email
        var user = await _context.Users.FirstOrDefaultAsync(u => u.Email == providedEmail);
        if (user == null)
        {
            return AuthenticateResult.Fail("User not found");
        }

        // Find API connection for this user with matching token
        var apiConnection = await _context.ApiConnections
            .Where(c => c.UserId == user.Id && c.IsActive)
            .ToListAsync();

        var matchingConnection = apiConnection.FirstOrDefault(c =>
            _encryptionService.Decrypt(c.Token) == providedToken);

        if (matchingConnection == null)
        {
            return AuthenticateResult.Fail("Invalid API token for this user");
        }

        // Create claims identity
        var claims = new[]
        {
            new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new Claim(ClaimTypes.Email, user.Email),
            new Claim("ApiConnectionId", matchingConnection.Id.ToString()),
            new Claim("ApiConnectionName", matchingConnection.Name)
        };

        var identity = new ClaimsIdentity(claims, Scheme.Name);
        var principal = new ClaimsPrincipal(identity);
        var ticket = new AuthenticationTicket(principal, Scheme.Name);

        return AuthenticateResult.Success(ticket);
    }
}
