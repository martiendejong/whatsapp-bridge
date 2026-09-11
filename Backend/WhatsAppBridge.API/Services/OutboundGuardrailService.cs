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
/// Task 2026-09-10: the allow-list answers only "may this number be messaged at all", which
/// was sufficient while Martien was the only recipient. The real policy also has a WHAT and a
/// WHEN — Sjoerd hears about a Valsuani deploy during his waking hours and about nothing else,
/// ever. That lives in <see cref="OutboundRoutingService"/> and runs FIRST here, because a
/// redirect changes which number the volume caps below should be weighed against. Routing is
/// skipped entirely while the OutboundContacts table is empty, so an un-migrated deployment
/// behaves exactly as it did before.
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

    /// <summary>
    /// The direct /api/wa/sendReply route. Exempt from ROUTING for the same reason the CoachOS
    /// reply is — routing answers "may we start a conversation with this person, about this,
    /// now", and a reply starts nothing — with the added practical point that a redirect would
    /// be incoherent here: a reply carries a quoted message from one chat, and delivering it to
    /// somebody else's chat produces a message quoting a conversation the recipient cannot see.
    ///
    /// NOT exempt from the allow-list or the volume caps. The reply-window exception further
    /// down deliberately stays CoachOS-only, so this route can still only reach numbers that are
    /// allow-listed. It previously reached any number at all, with no cap and no record.
    /// </summary>
    public const string DirectReplyEndpoint = "sendReply";

    private readonly OutboundGuardrailOptions _options;
    private readonly AppDbContext _context;
    private readonly OutboundRoutingService _routing;
    private readonly ILogger<OutboundGuardrailService> _logger;

    public OutboundGuardrailService(IConfiguration configuration, AppDbContext context,
        OutboundRoutingService routing, ILogger<OutboundGuardrailService> logger)
    {
        _context = context;
        _routing = routing;
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
    public async Task<GuardrailResult> CheckAsync(string endpoint, string to, string body, int? userId,
        string? category = null)
    {
        if (!_options.Enabled)
            return GuardrailResult.Allow(to, null);

        // WHO first, HOW OFTEN second. Routing may hand back a different number than the caller
        // asked for (Sjoerd asleep -> Martien), and the caps below must then be weighed against
        // the number that actually receives the message, not the one that was aimed at.
        // A reply to someone who just messaged us is deliberately exempt from routing. Routing
        // answers "may we start a conversation with this person, about this, now" — and a reply
        // starts nothing. The reply-window check further down is what governs this path, and it
        // is strictly narrower: it demands a genuine inbound message within the last N hours.
        // Without this exemption, replying to anyone outside the contact table would break the
        // one thing Martien explicitly wants to keep working: "als ze zelf iets vragen".
        var isReply = endpoint is CoachOsReplyEndpoint or DirectReplyEndpoint;

        var routedTo = to;
        string? routingNote = null;
        var routingCleared = false;
        if (!isReply && await _routing.IsActiveAsync())
        {
            var decision = await _routing.RouteAsync(to, category, body);

            // A suppressed duplicate is not a block and must not be recorded as one: the message
            // already arrived, and writing it to BlockedOutboundMessages would report a working
            // policy as a failure.
            if (decision.Outcome == OutboundRoutingService.RoutingOutcome.Suppressed)
            {
                _logger.LogInformation("Outbound SUPPRESSED via {Endpoint} to {To}: {Reason}",
                    endpoint, to, decision.Reason);
                return GuardrailResult.Suppress(decision.Reason);
            }

            if (!decision.ShouldSend)
            {
                await RecordBlockAsync(endpoint, to, body, userId, decision.Reason);
                return GuardrailResult.Block(decision.Reason);
            }

            routedTo = decision.Recipient!;
            routingCleared = true;
            if (decision.Outcome == OutboundRoutingService.RoutingOutcome.Redirected)
                routingNote = decision.Reason;
        }

        to = routedTo;
        var normalizedTo = Normalize(to);

        // Once routing is configured, the contact table IS the allow-list — it answers the same
        // "may this number be messaged" question, plus the two the static list cannot express.
        // Consulting both would mean maintaining two lists that must agree, and the day they
        // disagree the failure is silent: Sjoerd would clear his deploy window and then be
        // dropped for not appearing in appsettings. The volume caps below still apply to both.
        var isAllowListed = routingCleared || _options.AllowList.Any(a => Normalize(a) == normalizedTo);

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
                    return GuardrailResult.Block(noWindowReason);
                }

                // Recent genuine inbound confirmed — fall through to the same rate-limit checks
                // below that an allow-listed recipient is subject to. No early "allowed" return.
            }
            else
            {
                var reason = $"Blocked: '{to}' is not on the outbound allow-list. Team-communication " +
                             "requests should route through ClickUp for approval instead of a direct WhatsApp send.";
                await RecordBlockAsync(endpoint, to, body, userId, reason);
                return GuardrailResult.Block(reason);
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
            return GuardrailResult.Block(reason);
        }

        var globalCount = await _context.OutboundSendLogs
            .Where(l => l.SentAtUtc >= nowUtc.AddHours(-1))
            .CountAsync();
        if (globalCount >= _options.MaxGlobalPerHour)
        {
            var reason = $"Blocked: global outbound volume cap reached ({globalCount}/{_options.MaxGlobalPerHour} " +
                         "sends in the last hour, any recipient).";
            await RecordBlockAsync(endpoint, to, body, userId, reason);
            return GuardrailResult.Block(reason);
        }

        // Written BEFORE the caller sends, as an attempt (Delivered=false). The caps above count
        // attempts, so this ordering is what makes them unskippable: a caller cannot burn through
        // sends and only be counted for the ones that worked. Delivery is a separate fact the
        // caller confirms afterwards via ConfirmDeliveredAsync — the dedupe reads only confirmed
        // rows, so a send that failed downstream can never suppress the retry or redirect that
        // would actually deliver it.
        var log = new OutboundSendLog
        {
            Recipient = normalizedTo,
            SentAtUtc = nowUtc,
            Delivered = false,
        };
        OutboundRoutingService.Stamp(log, category, body);
        _context.OutboundSendLogs.Add(log);
        await _context.SaveChangesAsync();

        return GuardrailResult.Allow(to, routingNote) with { SendLog = log };
    }

    /// <summary>
    /// Marks the send this check accounted for as actually delivered. Call after the WhatsApp
    /// send returns without error — and only then. Skipping the call is safe in the direction
    /// that matters: an unconfirmed row still counts against the volume caps but never feeds the
    /// duplicate suppression, so the failure mode of a forgotten call-site is a possible
    /// duplicate, not a silently discarded message. No-op when the check produced no row
    /// (guardrail disabled).
    /// </summary>
    public async Task ConfirmDeliveredAsync(GuardrailResult result)
    {
        if (result.SendLog == null) return;
        result.SendLog.Delivered = true;
        await _context.SaveChangesAsync();
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

    /// <summary>See <see cref="PhoneNumber.Normalize"/> — one definition, shared by every caller.</summary>
    private static string Normalize(string s) => PhoneNumber.Normalize(s);
}

/// <summary>
/// Outcome of a guardrail check. <see cref="Recipient"/> is the number the caller must
/// actually send to — normally the one it asked for, but a routing redirect can change it
/// (message aimed at Sjoerd at 03:00 goes to Martien). Sending to the originally requested
/// number after an Allowed result would defeat the routing policy, so callers must use this.
/// </summary>
public sealed record GuardrailResult(bool Allowed, string? Reason, string Recipient, string? RoutingNote,
    bool Suppressed = false)
{
    /// <summary>
    /// The volume-cap row this check wrote, carried so the caller can confirm delivery through
    /// <see cref="OutboundGuardrailService.ConfirmDeliveredAsync"/> once the send succeeds. Null
    /// when nothing was recorded (blocked, suppressed, or guardrail disabled).
    /// </summary>
    public Models.OutboundSendLog? SendLog { get; init; }

    public static GuardrailResult Allow(string recipient, string? routingNote) =>
        new(true, null, recipient, routingNote);

    public static GuardrailResult Block(string reason) =>
        new(false, reason, string.Empty, null);

    /// <summary>
    /// Nothing was sent, and that is the correct outcome rather than a refusal: the identical
    /// message already reached this recipient moments ago, so a redirect would have delivered it
    /// twice.
    ///
    /// Distinct from <see cref="Block"/> on purpose. A suppressed send used to come back as a
    /// 403 with blocked=true, which was wrong in three directions at once: the caller read it as
    /// a failure and retried, producing the very duplicate the suppression prevented; it was
    /// written to BlockedOutboundMessages, so the "blocked sends" list filled up with sends that
    /// were working exactly as designed; and the audit page counted it under blocked. Callers
    /// should treat this as success with nothing to do.
    /// </summary>
    public static GuardrailResult Suppress(string reason) =>
        new(false, reason, string.Empty, null, Suppressed: true);

    /// <summary>
    /// Shorthand for callers that only need the verdict — <c>var (allowed, reason) = ...</c>.
    /// Anything that then performs a send must use <see cref="Recipient"/> instead, since a
    /// redirect makes the requested and actual recipients differ.
    /// </summary>
    public void Deconstruct(out bool allowed, out string? reason)
    {
        allowed = Allowed;
        reason = Reason;
    }
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
