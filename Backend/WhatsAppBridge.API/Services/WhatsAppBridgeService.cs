using System.Text;
using System.Text.Json;

namespace WhatsAppBridge.API.Services;

/// <summary>
/// Service that communicates with the Node.js WhatsApp service
/// </summary>
public class WhatsAppBridgeService
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<WhatsAppBridgeService> _logger;
    private readonly string _whatsappServiceUrl;

    public WhatsAppBridgeService(IHttpClientFactory httpClientFactory, IConfiguration configuration, ILogger<WhatsAppBridgeService> logger)
    {
        _httpClient = httpClientFactory.CreateClient();
        _logger = logger;
        _whatsappServiceUrl = configuration["WhatsAppService:Url"] ?? "http://localhost:3000";
    }

    public async Task<string?> InitializeSessionAsync(string sessionId)
    {
        try
        {
            var response = await _httpClient.PostAsJsonAsync(
                $"{_whatsappServiceUrl}/session/create",
                new { sessionId });

            if (response.IsSuccessStatusCode)
            {
                var result = await response.Content.ReadFromJsonAsync<Dictionary<string, string>>();
                return result?["qrCode"];
            }

            _logger.LogError("Failed to initialize WhatsApp session: {StatusCode}", response.StatusCode);
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error initializing WhatsApp session");
            return null;
        }
    }

    public async Task<bool> DisconnectSessionAsync(string sessionId)
    {
        try
        {
            var response = await _httpClient.DeleteAsync($"{_whatsappServiceUrl}/session/{sessionId}");
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error disconnecting WhatsApp session");
            return false;
        }
    }

    public async Task<object?> SendMessageAsync(string sessionId, string to, string body)
    {
        try
        {
            var response = await _httpClient.PostAsJsonAsync(
                $"{_whatsappServiceUrl}/message/send",
                new { sessionId, to, body });

            if (response.IsSuccessStatusCode)
            {
                return await response.Content.ReadFromJsonAsync<object>();
            }

            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error sending message");
            return null;
        }
    }

    public async Task<object?> SendMediaAsync(string sessionId, string to, string mediaUrl, string? caption)
    {
        try
        {
            var response = await _httpClient.PostAsJsonAsync(
                $"{_whatsappServiceUrl}/message/sendMedia",
                new { sessionId, to, mediaUrl, caption });

            if (response.IsSuccessStatusCode)
            {
                return await response.Content.ReadFromJsonAsync<object>();
            }

            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error sending media");
            return null;
        }
    }

    public async Task<List<Controllers.WhatsAppMessage>?> GetMessagesAsync(string sessionId, string chatId, int limit)
    {
        try
        {
            var response = await _httpClient.GetAsync(
                $"{_whatsappServiceUrl}/messages?sessionId={sessionId}&chatId={chatId}&limit={limit}");

            if (response.IsSuccessStatusCode)
            {
                return await response.Content.ReadFromJsonAsync<List<Controllers.WhatsAppMessage>>();
            }

            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting messages");
            return null;
        }
    }

    public async Task<List<object>?> GetChatsAsync(string sessionId)
    {
        try
        {
            var response = await _httpClient.GetAsync($"{_whatsappServiceUrl}/chats?sessionId={sessionId}");

            if (response.IsSuccessStatusCode)
            {
                return await response.Content.ReadFromJsonAsync<List<object>>();
            }

            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting chats");
            return null;
        }
    }

    public async Task<List<Controllers.WhatsAppContact>?> GetContactsAsync(string sessionId)
    {
        try
        {
            var response = await _httpClient.GetAsync($"{_whatsappServiceUrl}/contacts?sessionId={sessionId}");

            if (response.IsSuccessStatusCode)
            {
                return await response.Content.ReadFromJsonAsync<List<Controllers.WhatsAppContact>>();
            }

            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting contacts");
            return null;
        }
    }

    public async Task<object?> CheckNumberStatusAsync(string sessionId, string number)
    {
        try
        {
            var response = await _httpClient.GetAsync(
                $"{_whatsappServiceUrl}/number/check?sessionId={sessionId}&number={number}");

            if (response.IsSuccessStatusCode)
            {
                return await response.Content.ReadFromJsonAsync<object>();
            }

            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error checking number status");
            return null;
        }
    }
}
