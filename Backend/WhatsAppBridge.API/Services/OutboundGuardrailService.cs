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
/// Task 1067 (2026-08-31, default OFF): a narrow, explicit exception to "unknown recipients are
/// never auto-messaged" for exactly ONE caller — the CoachOS service route, which lets a
/// non-allow-listed sender get an AI reply via the coaching app instead of being silently
/// dropped. This is intentionally NOT a general allow-list bypass:
///   - Only requests tagged with <see cref="CoachOsReplyEndpoint"/> are eligible at all.
///   - <see cref="OutboundGuardrailOptions.ReplyRouteEnabled"/> is its own independent flag
///     (default false) — the CoachOS forwarder having its own "Enabled" flag on is not enough;
///     the guardrail must separately opt in too (defense in depth, same principle as the rest
///     of this class).
///   - Even then, the recipient must have a row in InboundContacts (written ONLY by
///     RecordInboundContactAsync, called ONLY from a genuine, live, non-history inbound message)
///     with a timestamp inside the last <see cref="OutboundGuardrailOptions.ReplyWindowHours"/>
///     hours — i.e. this exact person actually messaged the bridge recently. No inbound row, no
///     exception: outbound to a truly unknown number is still unconditionally blocked.
///   - The existing MaxPerRecipientPer24h / MaxGlobalPerHour rate limits are NOT bypassed —
///     a reply that clears the reply-window check still has to clear both caps below.
///
/// Registered as Scoped (writes to AppDbContext) and bound from configuration section
/// "OutboundGuardrail".
/// </summary>
public sealed class OutboundGuardrailService
{
    /// <summary>
    /// The only "endpoint" value CheckAsync will ever consider for the reply-window exception.
    /// Only WhatsAppBridgeService's CoachOS dispatch path passes this — the normal
    /// sendMessage/sendMedia/forwardMessage endpoints in WhatsAppApiController pass their own
    /// literal endpoint names, so this exception can never apply to a direct API caller.
    /// </summary>
    public const string CoachOsReplyEndpoint = "coachOsReply";

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

    /// <summary>True if <paramref name="to"/> normalizes to an entry on the configured allow-list.</summary>
    public bool IsAllowListed(string to) => _options.AllowList.Any(a => Normalize(a) == Normalize(to));

    /// <summary>
    /// Records that a genuine inbound message just arrived from <paramref name="from"/>. Call
    /// this ONLY for live, non-history, non-self inbound messages — this timestamp is the sole
    /// basis for the CoachOS reply-window exception in CheckAsync, so recording a false or stale
    /// entry would directly weaken the guardrail.
    /// </summary>
    public async Task RecordInboundContactAsync(string from)
    {
        var normalized = Normalize(from);
        if (string.IsNullOrEmpty(normalized)) return;

        var nowUtc = DateTime.UtcNow;
        var existing = await _context.InboundContacts.FirstOrDefaultAsync(c => c.Sender == normalized);
        if (existing == null)
            _context.InboundContacts.Add(new InboundContact { Sender = normalized, LastInboundAtUtc = nowUtc });
        else
            existing.LastInboundAtUtc = nowUtc;
        await _context.SaveChangesAsync();
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
            if (endpoint == CoachOsReplyEndpoint && _options.ReplyRouteEnabled)
            {
                var cutoffUtc = DateTime.UtcNow.AddHours(-Math.Max(0, _options.ReplyWindowHours));
                var hasRecentInbound = await _context.InboundContacts
                    .AnyAsync(c => c.Sender == normalizedTo && c.LastInboundAtUtc >= cutoffUtc);

                if (!hasRecentInbound)
                {
                    var noWindowReason = $"Blocked: no inbound WhatsApp message from '{to}' within the " +
                                          $"{_options.ReplyWindowHours}h reply window — the CoachOS service route " +
                                          "only replies to numbers that genuinely messaged the bridge recently.";
                    await RecordBlockAsync(endpoint, to, body, userId, noWindowReason);
                    return (false, noWindowReason);
                }

                // Recent genuine inbound confirmed — fall through to the same rate-limit checks
                // below that an allow-listed recipient is subject to. No early "allowed" return.
            }
            else
            {
                var reason = $"Blocked: '{to}' is not on the outbound allow-list. Team-communication " +
                             "requests should route through ClickUp for approval instead of a direct WhatsApp send.";
                await RecordBlockAsync(endpoint, to, body, userId, reason);
                return (false, reason);
            }
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

    /// <summary>
    /// Task 1067, default false: independent opt-in for the CoachOS service-route reply
    /// exception (see class doc comment). Must be explicitly true in config in addition to
    /// CoachOsIntake:Enabled — the guardrail does not trust the forwarder's own flag alone.
    /// </summary>
    public bool ReplyRouteEnabled { get; set; } = false;

    /// <summary>How many hours after a genuine inbound message the CoachOS reply exception stays open.</summary>
    public int ReplyWindowHours { get; set; } = 24;
}
