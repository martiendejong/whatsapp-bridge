using Microsoft.AspNetCore.Mvc;
using WhatsAppBridge.API.Services;

namespace WhatsAppBridge.API.Controllers;

// "Login with IAM" — OAuth2/OIDC authorization-code + PKCE flow against the central IAM System.
// Password login (AuthController) stays fully intact as a fallback; this only adds a second way in.
[ApiController]
[Route("api/auth/iam")]
public class IamAuthController : ControllerBase
{
    private readonly IamService _iamService;
    private readonly AuthService _authService;
    private readonly ILogger<IamAuthController> _logger;

    public IamAuthController(IamService iamService, AuthService authService, ILogger<IamAuthController> logger)
    {
        _iamService = iamService;
        _authService = authService;
        _logger = logger;
    }

    private CookieOptions PkceCookieOptions => new()
    {
        HttpOnly = true,
        Secure = true,
        SameSite = SameSiteMode.Lax,
        MaxAge = TimeSpan.FromMinutes(10),
    };

    [HttpGet("status")]
    public IActionResult Status() => Ok(new { enabled = _iamService.IsConfigured });

    [HttpGet("login")]
    public IActionResult Login()
    {
        if (!_iamService.IsConfigured)
            return BadRequest(new { message = "IAM login is not configured" });

        var (verifier, challenge) = _iamService.CreatePkce();
        var state = _iamService.CreateState();

        Response.Cookies.Append("iam_state", state, PkceCookieOptions);
        Response.Cookies.Append("iam_pkce", verifier, PkceCookieOptions);

        return Redirect(_iamService.BuildAuthorizationUrl(state, challenge));
    }

    [HttpGet("callback")]
    public async Task<IActionResult> Callback([FromQuery] string? code, [FromQuery] string? state, [FromQuery] string? error)
    {
        var frontendBase = _iamService.FrontendBase;

        var cookieState = Request.Cookies["iam_state"];
        var codeVerifier = Request.Cookies["iam_pkce"];
        Response.Cookies.Delete("iam_state");
        Response.Cookies.Delete("iam_pkce");

        if (!string.IsNullOrEmpty(error))
        {
            _logger.LogWarning("IAM login returned an error: {Error}", error);
            return Redirect($"{frontendBase}/login?iam_error={Uri.EscapeDataString(error)}");
        }

        if (string.IsNullOrEmpty(code) || string.IsNullOrEmpty(state) || string.IsNullOrEmpty(codeVerifier) || state != cookieState)
        {
            _logger.LogWarning("IAM callback rejected: missing/mismatched state or code");
            return Redirect($"{frontendBase}/login?iam_error=invalid_state");
        }

        var tokens = await _iamService.ExchangeCodeForTokensAsync(code, codeVerifier);
        if (tokens?.IdToken == null)
        {
            _logger.LogWarning("IAM token exchange failed");
            return Redirect($"{frontendBase}/login?iam_error=token_exchange_failed");
        }

        var userInfo = _iamService.ReadUserInfo(tokens.IdToken);
        if (userInfo == null)
        {
            _logger.LogWarning("IAM id_token had no usable email claim");
            return Redirect($"{frontendBase}/login?iam_error=invalid_identity");
        }

        var user = await _authService.FindOrCreateFromIamAsync(userInfo.Email);
        if (!user.IsActive)
            return Redirect($"{frontendBase}/login?iam_error=account_disabled");

        // IAM SSO is a redirect-based flow with no step to collect a second factor, so it
        // cannot satisfy the same 2FA requirement AuthController.Login enforces for password
        // login. Rather than silently bypass that protection, refuse SSO here and point the
        // user back to password login, which still runs the full verify-2fa step.
        if (user.TwoFactorEnabled)
        {
            _logger.LogWarning("IAM callback refused for user {UserId}: two-factor authentication is enabled", user.Id);
            return Redirect($"{frontendBase}/login?iam_error=two_factor_required");
        }

        var jwt = _authService.GenerateJwtToken(user);
        return Redirect($"{frontendBase}/auth/iam/callback?token={Uri.EscapeDataString(jwt)}");
    }
}
