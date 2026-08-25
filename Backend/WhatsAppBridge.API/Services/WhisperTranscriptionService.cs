using System.Net.Http.Headers;
using System.Text.Json;

namespace WhatsAppBridge.API.Services;

/// <summary>
/// Transcribes inbound WhatsApp voice messages via OpenAI's Whisper API — same "whisper-1"
/// model + multipart-upload contract already proven by jengo-live-meeting's /api/transcribe
/// (C:\projects\jengo-live-meeting\server.js), reused here rather than re-invented (task 869ejuycr).
///
/// The API key is never hardcoded. It is resolved lazily, in priority order:
///   1. OpenAI:ApiKey from config (appsettings.Local.json, gitignored — same convention already
///      used for TaskIntake:ApiKey/Jwt:Key in this repo — or an OPENAI__APIKEY env var).
///   2. The Prospergenics vault (vault.prospergenics.com), using the same HTTP contract
///      jengo-agi's VaultConfigurationProvider already uses. Vault project 9, credential 131
///      ("OpenAI API Key - Jengo") already holds a working key for exactly this purpose. The
///      vault bootstrap key (Vault:ApiKey) is itself sourced only from config, never committed.
/// If neither resolves, transcription is silently disabled (IsEnabledAsync() -> false) — audio
/// messages still ingest normally, just without a transcript, and nothing ever throws back into
/// the inbound message pipeline.
/// </summary>
public sealed class WhisperTranscriptionService
{
    private readonly IConfiguration _configuration;
    private readonly ILogger<WhisperTranscriptionService> _logger;
    private readonly HttpClient _http;

    private string? _apiKey;
    private bool _resolved;
    private readonly SemaphoreSlim _resolveLock = new(1, 1);

    public WhisperTranscriptionService(IConfiguration configuration, ILogger<WhisperTranscriptionService> logger)
    {
        _configuration = configuration;
        _logger = logger;
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
    }

    public async Task<bool> IsEnabledAsync() => !string.IsNullOrWhiteSpace(await ResolveApiKeyAsync());

    /// <summary>
    /// Transcribes raw (already-decrypted) audio bytes. Returns null — never throws — when
    /// transcription is unconfigured, the upload fails, or OpenAI returns an error.
    /// </summary>
    public async Task<string?> TranscribeAsync(byte[] audioBytes, string? mimeType)
    {
        if (audioBytes.Length == 0) return null;

        var apiKey = await ResolveApiKeyAsync();
        if (string.IsNullOrWhiteSpace(apiKey)) return null;

        try
        {
            using var content = new MultipartFormDataContent();
            var audioContent = new ByteArrayContent(audioBytes);
            var contentType = string.IsNullOrWhiteSpace(mimeType) ? "audio/ogg" : mimeType.Split(';')[0].Trim();
            audioContent.Headers.ContentType = new MediaTypeHeaderValue(contentType);
            content.Add(audioContent, "file", $"voice{ExtensionFor(mimeType)}");
            content.Add(new StringContent("whisper-1"), "model");

            using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.openai.com/v1/audio/transcriptions")
            {
                Content = content,
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

            using var response = await _http.SendAsync(request);
            var body = await response.Content.ReadAsStringAsync();
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Whisper transcription failed ({Status}): {Body}", response.StatusCode, Truncate(body));
                return null;
            }

            using var doc = JsonDocument.Parse(body);
            var text = doc.RootElement.TryGetProperty("text", out var t) ? t.GetString() : null;
            return string.IsNullOrWhiteSpace(text) ? null : text.Trim();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Whisper transcription errored");
            return null;
        }
    }

    private async Task<string?> ResolveApiKeyAsync()
    {
        if (_resolved) return _apiKey;
        await _resolveLock.WaitAsync();
        try
        {
            if (_resolved) return _apiKey;

            var direct = _configuration["OpenAI:ApiKey"];
            if (!string.IsNullOrWhiteSpace(direct))
            {
                _apiKey = direct;
                _resolved = true;
                return _apiKey;
            }

            _apiKey = await TryResolveFromVaultAsync();
            _resolved = true;
            return _apiKey;
        }
        finally
        {
            _resolveLock.Release();
        }
    }

    private async Task<string?> TryResolveFromVaultAsync()
    {
        var vaultApiKey = _configuration["Vault:ApiKey"];
        if (string.IsNullOrWhiteSpace(vaultApiKey))
        {
            _logger.LogInformation(
                "Whisper: no OpenAI:ApiKey and no Vault:ApiKey configured — transcription disabled until one is set.");
            return null;
        }

        var baseUrl = (_configuration["Vault:BaseUrl"] ?? "https://vault.prospergenics.com").TrimEnd('/');
        var projectId = _configuration.GetValue<int?>("Vault:ProjectId") ?? 9;
        var credentialId = _configuration.GetValue<int?>("Vault:OpenAiCredentialId") ?? 131;

        try
        {
            using var request = new HttpRequestMessage(
                HttpMethod.Get, $"{baseUrl}/api/projects/{projectId}/credentials/{credentialId}");
            request.Headers.Add("X-API-Key", vaultApiKey);

            using var response = await _http.SendAsync(request);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Whisper: vault fetch for OpenAI key returned HTTP {Status} — transcription disabled.",
                    response.StatusCode);
                return null;
            }

            var body = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind != JsonValueKind.Object ||
                !doc.RootElement.TryGetProperty("password", out var passwordElement) ||
                passwordElement.ValueKind != JsonValueKind.String)
            {
                _logger.LogWarning("Whisper: vault response had no 'password' field — transcription disabled.");
                return null;
            }

            var value = passwordElement.GetString();
            return string.IsNullOrEmpty(value) ? null : value;
        }
        catch (Exception ex)
        {
            // Never logs the key itself — only the exception type/message.
            _logger.LogWarning(ex, "Whisper: vault fetch for OpenAI key failed — transcription disabled.");
            return null;
        }
    }

    private static string ExtensionFor(string? mimeType) => (mimeType ?? "").Split(';')[0].Trim() switch
    {
        "audio/ogg" => ".ogg",
        "audio/opus" => ".opus",
        "audio/mp4" => ".m4a",
        "audio/mpeg" => ".mp3",
        "audio/wav" or "audio/x-wav" => ".wav",
        "audio/webm" => ".webm",
        "audio/amr" => ".amr",
        _ => ".ogg", // WhatsApp voice notes are ogg/opus in the overwhelming majority of cases
    };

    private static string Truncate(string s) => s.Length <= 300 ? s : s[..300] + "...";
}
