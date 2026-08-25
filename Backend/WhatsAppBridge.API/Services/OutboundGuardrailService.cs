using WhatsAppBridge.API.Data;
using WhatsAppBridge.API.Models;

namespace WhatsAppBridge.API.Services;

/// <summary>
/// Server-side, config-driven outbound guardrail (task 869edf485, 2026-08-04): an autonomous
/// jengo-agi session sent Sjoerd an unsolicited 06:01 WhatsApp nag about the backlog running
/// dry, violating an explicit no-unsolicited-team-messages agreement. jengo-agi now enforces
/// its own allow-list before calling the bridge, but a valid API token can also be used to
/// call this bridge directly — so the bridge enforces its own guardrail too (defense in depth):
///
///   - Recipients on the allow-list (default: Martien's number) can always be messaged.
///   - Any other recipient can only be messaged during quiet hours (default 08:00-21:00,
///     bridge server local time) — never outside them, regardless of who calls the API.
///
/// A blocked send is logged (ILogger) AND persisted to BlockedOutboundMessages so it is
/// discoverable via GET /api/wa/blockedOutbound, not silently dropped.
///
/// Registered as Scoped (writes to AppDbContext) and bound from configuration section
/// "OutboundGuardrail".
/// </summary>
public sealed class OutboundGuardrailService
{
    private readonly OutboundGuardrailOptions _options;
    private readonly AppDbContext _context;
    private readonly ILogger<OutboundGuardrailService> _logger;

    public OutboundGuardrailService(IConfiguration configuration, AppDbContext context, ILogger<OutboundGuardrailService> logger)
    {
        _context = context;
        _logger = logger;
        _options = new OutboundGuardrailOptions();
        configuration.GetSection("OutboundGuardrail").Bind(_options);
        if (_options.AllowList.Count == 0)
            _options.AllowList = new List<string> { "31633984381" }; // Martien — safe default even if config binding produced an empty list
    }

    /// <summary>
    /// Checks whether an outbound send to <paramref name="to"/> is permitted right now. When
    /// blocked, records the attempt and returns a human-readable reason; the caller must NOT
    /// proceed with the send.
    /// </summary>
    public async Task<(bool Allowed, string? Reason)> CheckAsync(string endpoint, string to, string body, int? userId)
    {
        if (!_options.Enabled)
            return (true, null);

        var normalizedTo = Normalize(to);
        var isAllowListed = _options.AllowList.Any(a => Normalize(a) == normalizedTo);
        if (isAllowListed)
            return (true, null);

        if (IsWithinQuietHours(DateTime.Now.TimeOfDay))
            return (true, null);

        var reason = $"Blocked: '{to}' is not on the outbound allow-list and the current time is outside " +
                     $"quiet hours ({_options.QuietHoursStart}-{_options.QuietHoursEnd}). Team-communication " +
                     "requests should route through ClickUp for approval instead of a direct WhatsApp send.";

        _logger.LogWarning("Outbound WhatsApp BLOCKED via {Endpoint}: to={To} reason={Reason}", endpoint, to, reason);

        _context.BlockedOutboundMessages.Add(new BlockedOutboundMessage
        {
            UserId = userId,
            Endpoint = endpoint,
            Recipient = to,
            BodyPreview = body.Length > 200 ? body[..200] : body,
            Reason = reason,
            BlockedAtUtc = DateTime.UtcNow,
        });
        await _context.SaveChangesAsync();

        return (false, reason);
    }

    private bool IsWithinQuietHours(TimeSpan now)
    {
        if (!TimeSpan.TryParse(_options.QuietHoursStart, out var start))
            start = new TimeSpan(8, 0, 0);
        if (!TimeSpan.TryParse(_options.QuietHoursEnd, out var end))
            end = new TimeSpan(21, 0, 0);

        return start <= end
            ? now >= start && now <= end
            : now >= start || now <= end; // window wraps past midnight
    }

    private static string Normalize(string s) =>
        new string(s.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
}

public class OutboundGuardrailOptions
{
    public bool Enabled { get; set; } = true;
    public List<string> AllowList { get; set; } = new();
    public string QuietHoursStart { get; set; } = "08:00";
    public string QuietHoursEnd { get; set; } = "21:00";
}
