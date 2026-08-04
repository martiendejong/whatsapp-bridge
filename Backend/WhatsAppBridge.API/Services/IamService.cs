using System.IdentityModel.Tokens.Jwt;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace WhatsAppBridge.API.Services;

// OAuth2/OIDC authorization-code + PKCE client for the IAM System (maendeleo.martiendejong.nl).
// Mirrors the established pattern used by yinyogasound-coach and jengo-web: a public (no
// client-secret) OpenIddict client, PKCE S256, and the id_token decoded without signature
// verification since it comes straight off the server-to-server /connect/token exchange over
// TLS. See jengo-knowledge-private/knowledge/iam-integration-pattern-oauth2-pkce.md.
public class IamService
{
    private readonly IConfiguration _configuration;
    private readonly IHttpClientFactory _httpClientFactory;

    public IamService(IConfiguration configuration, IHttpClientFactory httpClientFactory)
    {
        _configuration = configuration;
        _httpClientFactory = httpClientFactory;
    }

    private string Authority => (_configuration["Iam:Authority"] ?? "").TrimEnd('/');
    private string ClientId => _configuration["Iam:ClientId"] ?? "";
    private string CallbackUrl => _configuration["Iam:CallbackUrl"] ?? "";
    public string FrontendBase => (_configuration["Iam:FrontendBase"] ?? "").TrimEnd('/');

    public bool IsConfigured => !string.IsNullOrEmpty(ClientId) && !string.IsNullOrEmpty(Authority) && !string.IsNullOrEmpty(CallbackUrl);

    public (string Verifier, string Challenge) CreatePkce()
    {
        var bytes = RandomNumberGenerator.GetBytes(64);
        var verifier = Base64UrlEncode(bytes);
        var hash = SHA256.HashData(Encoding.ASCII.GetBytes(verifier));
        var challenge = Base64UrlEncode(hash);
        return (verifier, challenge);
    }

    public string CreateState() => Base64UrlEncode(RandomNumberGenerator.GetBytes(16));

    public string BuildAuthorizationUrl(string state, string codeChallenge)
    {
        var query = new Dictionary<string, string>
        {
            ["response_type"] = "code",
            ["client_id"] = ClientId,
            ["redirect_uri"] = CallbackUrl,
            ["scope"] = "openid profile email roles",
            ["state"] = state,
            ["code_challenge"] = codeChallenge,
            ["code_challenge_method"] = "S256",
        };
        var qs = string.Join("&", query.Select(kv => $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value)}"));
        return $"{Authority}/connect/authorize?{qs}";
    }

    public async Task<IamTokenResponse?> ExchangeCodeForTokensAsync(string code, string codeVerifier)
    {
        var client = _httpClientFactory.CreateClient();
        var form = new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["client_id"] = ClientId,
            ["code"] = code,
            ["redirect_uri"] = CallbackUrl,
            ["code_verifier"] = codeVerifier,
        };

        var response = await client.PostAsync($"{Authority}/connect/token", new FormUrlEncodedContent(form));
        if (!response.IsSuccessStatusCode)
            return null;

        var json = await response.Content.ReadAsStringAsync();
        return JsonSerializer.Deserialize<IamTokenResponse>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
    }

    // Decodes the id_token claims without signature verification — accepted across every
    // existing IAM-consuming app because the token came directly from a server-to-server TLS
    // call to /connect/token, authenticated by the PKCE code_verifier rather than a client secret.
    public IamUserInfo? ReadUserInfo(string idToken)
    {
        try
        {
            var jwt = new JwtSecurityTokenHandler().ReadJwtToken(idToken);
            var email = jwt.Claims.FirstOrDefault(c => c.Type == "email")?.Value;
            if (string.IsNullOrWhiteSpace(email))
                return null;

            var name = jwt.Claims.FirstOrDefault(c => c.Type == "name")?.Value;
            return new IamUserInfo(email, name);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string Base64UrlEncode(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

public record IamTokenResponse(
    [property: JsonPropertyName("access_token")] string? AccessToken,
    [property: JsonPropertyName("id_token")] string? IdToken,
    [property: JsonPropertyName("token_type")] string? TokenType,
    [property: JsonPropertyName("expires_in")] int? ExpiresIn
);

public record IamUserInfo(string Email, string? Name);
