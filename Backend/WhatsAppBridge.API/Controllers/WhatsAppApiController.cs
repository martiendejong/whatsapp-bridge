using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using WhatsAppBridge.API.Data;
using WhatsAppBridge.API.Models;
using WhatsAppBridge.API.Services;

namespace WhatsAppBridge.API.Controllers;

/// <summary>
/// WhatsApp API endpoints - accessible via API token
/// Signature mirrors actual WhatsApp Web API
/// </summary>
[ApiController]
[Route("api/wa")]
public class WhatsAppApiController : ControllerBase
{
    private readonly AppDbContext _context;
    private readonly AuthService _authService;
    private readonly WhatsAppBridgeService _whatsappService;
    private readonly EncryptionService _encryptionService;

    public WhatsAppApiController(
        AppDbContext context,
        AuthService authService,
        WhatsAppBridgeService whatsappService,
        EncryptionService encryptionService)
    {
        _context = context;
        _authService = authService;
        _whatsappService = whatsappService;
        _encryptionService = encryptionService;
    }

    private async Task<(bool success, int? userId, string? error)> ValidateApiToken()
    {
        var authHeader = Request.Headers.Authorization.ToString();
        if (string.IsNullOrEmpty(authHeader) || !authHeader.StartsWith("Bearer "))
            return (false, null, "Missing or invalid authorization header");

        var token = authHeader.Substring("Bearer ".Length).Trim();

        // Decrypt token if encryption is enabled
        if (_encryptionService.IsEncryptionEnabled)
        {
            // Try to find connection by encrypted token
            var connection = await _context.ApiConnections
                .FirstOrDefaultAsync(c => c.Token == token && c.IsActive);

            if (connection == null)
            {
                // Try decrypting the provided token and searching
                try
                {
                    var encryptedToken = _encryptionService.Encrypt(token);
                    connection = await _context.ApiConnections
                        .FirstOrDefaultAsync(c => c.Token == encryptedToken && c.IsActive);
                }
                catch
                {
                    // Token is invalid
                }
            }

            if (connection != null)
            {
                connection.LastUsedAt = DateTime.UtcNow;
                await _context.SaveChangesAsync();
                return (true, connection.UserId, null);
            }
        }
        else
        {
            var user = await _authService.ValidateApiTokenAsync(token);
            if (user != null)
                return (true, user.Id, null);
        }

        return (false, null, "Invalid API token");
    }

    private async Task<string?> GetUserSessionId(int userId)
    {
        var session = await _context.WhatsAppSessions
            .Where(s => s.UserId == userId && s.Status == "connected")
            .OrderByDescending(s => s.ConnectedAt)
            .FirstOrDefaultAsync();

        return session?.SessionId;
    }

    /// <summary>
    /// Send a text message
    /// POST /api/wa/sendMessage
    /// </summary>
    [HttpPost("sendMessage")]
    public async Task<IActionResult> SendMessage([FromBody] SendMessageRequest request)
    {
        try
        {
            var (success, userId, error) = await ValidateApiToken();
            if (!success)
                return Unauthorized(new { error });

            var sessionId = await GetUserSessionId(userId!.Value);
            if (sessionId == null)
                return BadRequest(new { error = "No active WhatsApp session" });

            // Encrypt message if encryption enabled
            var messageToSend = _encryptionService.IsEncryptionEnabled
                ? _encryptionService.Encrypt(request.Body)
                : request.Body;

            var result = await _whatsappService.SendMessageAsync(sessionId, request.To, messageToSend);

            return Ok(result);
        }
        catch (WhatsAppServiceException ex)
        {
            // Return user-friendly error message
            return StatusCode(400, new
            {
                error = ex.Error.UserMessage,
                errorCode = ex.Error.ErrorCode,
                details = ex.Error.AdditionalInfo
            });
        }
    }

    /// <summary>
    /// Send media (image, video, document)
    /// POST /api/wa/sendMedia
    /// </summary>
    [HttpPost("sendMedia")]
    public async Task<IActionResult> SendMedia([FromBody] SendMediaRequest request)
    {
        try
        {
            var (success, userId, error) = await ValidateApiToken();
            if (!success)
                return Unauthorized(new { error });

            var sessionId = await GetUserSessionId(userId!.Value);
            if (sessionId == null)
                return BadRequest(new { error = "No active WhatsApp session" });

            var result = await _whatsappService.SendMediaAsync(sessionId, request.To, request.MediaUrl, request.Caption);

            return Ok(result);
        }
        catch (WhatsAppServiceException ex)
        {
            // Return user-friendly error message
            return StatusCode(400, new
            {
                error = ex.Error.UserMessage,
                errorCode = ex.Error.ErrorCode,
                details = ex.Error.AdditionalInfo
            });
        }
    }

    /// <summary>
    /// Get chat messages
    /// GET /api/wa/getMessages?chatId=123456789@c.us&limit=50
    /// </summary>
    [HttpGet("getMessages")]
    public async Task<IActionResult> GetMessages([FromQuery] string chatId, [FromQuery] int limit = 50)
    {
        var (success, userId, error) = await ValidateApiToken();
        if (!success)
            return Unauthorized(new { error });

        var sessionId = await GetUserSessionId(userId!.Value);
        if (sessionId == null)
            return BadRequest(new { error = "No active WhatsApp session" });

        var messages = await _whatsappService.GetMessagesAsync(sessionId, chatId, limit);

        // Decrypt messages if encryption enabled
        if (_encryptionService.IsEncryptionEnabled && messages != null)
        {
            messages = messages.Select(msg =>
                !string.IsNullOrEmpty(msg.Body)
                    ? msg with { Body = _encryptionService.Decrypt(msg.Body) }
                    : msg
            ).ToList();
        }

        return Ok(messages);
    }

    /// <summary>
    /// Get all chats
    /// GET /api/wa/getChats
    /// </summary>
    [HttpGet("getChats")]
    public async Task<IActionResult> GetChats()
    {
        var (success, userId, error) = await ValidateApiToken();
        if (!success)
            return Unauthorized(new { error });

        var sessionId = await GetUserSessionId(userId!.Value);
        if (sessionId == null)
            return BadRequest(new { error = "No active WhatsApp session" });

        var chats = await _whatsappService.GetChatsAsync(sessionId);

        return Ok(chats);
    }

    /// <summary>
    /// Get contacts
    /// GET /api/wa/getContacts
    /// </summary>
    [HttpGet("getContacts")]
    public async Task<IActionResult> GetContacts()
    {
        var (success, userId, error) = await ValidateApiToken();
        if (!success)
            return Unauthorized(new { error });

        var sessionId = await GetUserSessionId(userId!.Value);
        if (sessionId == null)
            return BadRequest(new { error = "No active WhatsApp session" });

        var contacts = await _whatsappService.GetContactsAsync(sessionId);

        // Decrypt phone numbers if encryption enabled
        if (_encryptionService.IsEncryptionEnabled && contacts != null)
        {
            contacts = contacts.Select(contact =>
                !string.IsNullOrEmpty(contact.Number)
                    ? contact with { Number = _encryptionService.Decrypt(contact.Number) }
                    : contact
            ).ToList();
        }

        return Ok(contacts);
    }

    /// <summary>
    /// Check if number is registered on WhatsApp
    /// GET /api/wa/checkNumberStatus?number=1234567890
    /// </summary>
    [HttpGet("checkNumberStatus")]
    public async Task<IActionResult> CheckNumberStatus([FromQuery] string number)
    {
        var (success, userId, error) = await ValidateApiToken();
        if (!success)
            return Unauthorized(new { error });

        var sessionId = await GetUserSessionId(userId!.Value);
        if (sessionId == null)
            return BadRequest(new { error = "No active WhatsApp session" });

        var result = await _whatsappService.CheckNumberStatusAsync(sessionId, number);

        return Ok(result);
    }
}

public record SendMessageRequest(string To, string Body);
public record SendMediaRequest(string To, string MediaUrl, string? Caption);
public record WhatsAppMessage(string Id, string From, string To, string Body, long Timestamp);
public record WhatsAppContact(string Id, string Name, string Number);
