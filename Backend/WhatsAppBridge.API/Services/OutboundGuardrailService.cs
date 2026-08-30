using Microsoft.EntityFrameworkCore;
using WhatsAppBridge.API.Data;
using WhatsAppBridge.API.Models;

namespace WhatsAppBridge.API.Services;

/// <summary>
/// Server-side, config-driven outbound guardrail (task 869edf485, 2026-08-04; tightened by
/// task 897, 2026-08-30): an autonomous jengo-agi session sent Sjoerd an unsolicited 06:01
/// WhatsApp nag about the backlog running dry, violating an explicit no-unsolicited-team-
/// messages agreement. jengo-agi now enforces its own allow-list before calling the bridge,
/// but a valid API token can also be used to call this bridge directly — so the bridge
/// enforces its own guardrail too (defense in depth):
///
///   - Recipients on the allow-list (default: Martien's number) can always be messaged,
///     subject to the volume cap below.
///   - Any other recipient is never auto-messaged, at any time of day. The original
///     "any recipient during quiet hours" exception is gone — it was WHO/WHEN only and had
///     no HOW MANY dimension, which is what actually triggered a WhatsApp ban of the bridge
///     number (task 897): three uncoordinated automated senders (a daily team-briefing
///     script, an autonomous mission's proactive outreach, and a Bugatti uptime watchdog
///     re-alerting the same number) all stayed inside the WHO/WHEN rules while collectively
///     producing exactly the frequent one-way automated-send pattern WhatsApp's anti-spam
///     detection flags on an unofficial client.
///   - Even an allow-listed recipient is capped: at most <see cref="OutboundGuardrailOptions.MaxPerRecipientPer24h"/>
///     sends per rolling 24h, and at most <see cref="OutboundGuardrailOptions.MaxGlobalPerHour"/>
///     sends (any recipient) per rolling hour — defense against a future runaway sender/loop,
///     not just a bad recipient list.
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
    /// proceed with the send. When allowed, records the send for volume-cap accounting.
    /// </summary>
    public async Task<(bool Allowed, string? Reason)> CheckAsync(string endpoint, string to, string body, int? userId)
    {
        if (!_options.Enabled)
            return (true, null);

        var normalizedTo = Normalize(to);
        var isAllowListed = _options.AllowList.Any(a => Normalize(a) == normalizedTo);

        if (!isAllowListed)
        {
            var reason = $"Blocked: '{to}' is not on the outbound allow-list. Team-communication " +
                         "requests should route through ClickUp for approval instead of a direct WhatsApp send.";
            await RecordBlockAsync(endpoint, to, body, userId, reason);
            return (false, reason);
        }

        var nowUtc = DateTime.UtcNow;

        var recipientCount = await _context.OutboundSendLogs
            .Where(l => l.Recipient == normalizedTo && l.SentAtUtc >= nowUtc.AddHours(-24))
            .CountAsync();
        if (recipientCount >= _options.MaxPerRecipientPer24h)
        {
            var reason = $"Blocked: volume cap reached for '{to}' ({recipientCount}/{_options.MaxPerRecipientPer24h} " +
                         "sends in the last 24h).";
            await RecordBlockAsync(endpoint, to, body, userId, reason);
            return (false, reason);
        }

        var globalCount = await _context.OutboundSendLogs
            .Where(l => l.SentAtUtc >= nowUtc.AddHours(-1))
            .CountAsync();
        if (globalCount >= _options.MaxGlobalPerHour)
        {
            var reason = $"Blocked: global outbound volume cap reached ({globalCount}/{_options.MaxGlobalPerHour} " +
                         "sends in the last hour, any recipient).";
            await RecordBlockAsync(endpoint, to, body, userId, reason);
            return (false, reason);
        }

        _context.OutboundSendLogs.Add(new OutboundSendLog
        {
            Recipient = normalizedTo,
            SentAtUtc = nowUtc,
        });
        await _context.SaveChangesAsync();

        return (true, null);
    }

    private async Task RecordBlockAsync(string endpoint, string to, string body, int? userId, string reason)
    {
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
    }

    /// <summary>
    /// Extracts the leading phone-number digit run (task 897, 2026-08-30 — found live via the
    /// Bugatti uptime watchdog fix): every recipient format used against this API puts the
    /// digits first — a bare number ("31633984381"), a contact JID ("31633984381@c.us"), or a
    /// device-suffixed JID ("254715438010:78@s.whatsapp.net") — but the previous
    /// <c>Where(char.IsLetterOrDigit)</c> kept the LETTERS from "@c.us"/"@s.whatsapp.net" too
    /// (producing "31633984381cus"), which never matched a plain-digits allow-list entry.
    /// This silently blocked messages to Martien himself whenever sent as "...@c.us" — the
    /// documented standard format in prod-access/vault-access's own config.example.json —
    /// confirmed live via a real blocked vault-approval-request send in production. Taking
    /// only the leading digit run fixes this for every format above.
    /// </summary>
    private static string Normalize(string s) =>
        new string(s.TakeWhile(char.IsDigit).ToArray());
}

public class OutboundGuardrailOptions
{
    public bool Enabled { get; set; } = true;
    public List<string> AllowList { get; set; } = new();

    /// <summary>Max sends to any single recipient in a rolling 24h window.</summary>
    public int MaxPerRecipientPer24h { get; set; } = 20;

    /// <summary>Max sends total (any recipient) in a rolling 1h window.</summary>
    public int MaxGlobalPerHour { get; set; } = 10;
}
