using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using WhatsAppBridge.API.Data;
using WhatsAppBridge.API.Models;

namespace WhatsAppBridge.API.Services;

/// <summary>
/// Decides WHO actually receives a message, given who the caller aimed it at and what kind of
/// message it is.
///
/// This sits in the bridge rather than in jengo-agi on purpose. Nine different senders reach
/// WhatsApp today — the jengo-agi dashboard, the TaskManager approval notifier, the inbound
/// reply runner, a PowerShell helper, the morning briefing, the vault- and prod-access
/// approval flows, the constitution check — and five of them are Python or PowerShell talking
/// straight to this API with nothing but a bearer token. A policy enforced in jengo-agi would
/// cover four of nine. The bridge is the only chokepoint every sender shares.
///
/// Three outcomes:
///   - Allowed: the intended recipient accepts this category and is inside their window.
///   - Redirected: the intended recipient is outside their window (or muted) and has a
///     fallback. The message goes to the fallback instead of being lost.
///   - Blocked: no route. The caller must not send.
///
/// The volume caps and allow-list in <see cref="OutboundGuardrailService"/> still apply on top
/// of whatever this decides — routing answers "who", the guardrail answers "how often". A
/// redirect does not buy an exemption from either.
/// </summary>
public sealed class OutboundRoutingService
{
    private readonly AppDbContext _context;
    private readonly ILogger<OutboundRoutingService> _logger;
    private readonly OutboundRoutingOptions _options;

    /// <summary>
    /// How long a redirect looks back for an identical message already delivered to the
    /// fallback. Most callers fan out to Sjoerd and Martien in the same breath; without this,
    /// a nightly deploy notice would reach Martien twice — once addressed to him, once
    /// redirected off Sjoerd. Long enough to cover a slow fan-out, short enough that a genuine
    /// repeat alert ten minutes later still lands.
    /// </summary>
    private static readonly TimeSpan RedirectDedupeWindow = TimeSpan.FromMinutes(10);

    public const string DefaultCategory = "other";

    public OutboundRoutingService(AppDbContext context, ILogger<OutboundRoutingService> logger,
        IConfiguration configuration)
    {
        _context = context;
        _logger = logger;
        _options = new OutboundRoutingOptions();
        configuration.GetSection("OutboundRouting").Bind(_options);
    }

    public enum RoutingOutcome { Allowed, Redirected, Blocked, Suppressed }

    public sealed record RoutingDecision(
        RoutingOutcome Outcome,
        string? Recipient,
        string Reason)
    {
        public bool ShouldSend => Outcome is RoutingOutcome.Allowed or RoutingOutcome.Redirected;
    }

    /// <summary>
    /// Whether routing actually governs sends right now. Two independent conditions, both
    /// required.
    ///
    /// The flag is the important one and it defaults to FALSE. An earlier version of this had
    /// only the table check, plus a seed in appsettings that populated the table on first boot —
    /// which meant merging the feature silently switched it on in production, and every caller
    /// that did not yet pass a category started getting its sends blocked as "other". A feature
    /// that arms itself on deploy is not a feature with a safety valve. Now the deploy is inert:
    /// the contacts get seeded and are visible in the admin UI, but nothing is enforced until
    /// someone sets OutboundRouting:Enabled to true, having looked at the table first.
    ///
    /// The table check stays as the second condition: enabling the flag against an empty table
    /// would block every send, and "I turned it on before adding anyone" should not take
    /// WhatsApp down.
    /// </summary>
    public async Task<bool> IsActiveAsync() =>
        _options.Enabled && await _context.OutboundContacts.AnyAsync();

    /// <summary>
    /// Resolves an alias or number to the number a message should actually go to.
    /// <paramref name="category"/> may be null; it is then treated as <see cref="DefaultCategory"/>.
    /// </summary>
    /// <param name="dryRun">
    /// True when the caller is asking, not sending (the Preview endpoint). The decision is
    /// computed identically; what differs is that nothing is logged as if traffic had moved —
    /// a GET that emits "Outbound REDIRECTED" lines fabricates events in the log it shares
    /// with real sends.
    /// </param>
    public async Task<RoutingDecision> RouteAsync(string to, string? category, string body, DateTime? nowUtc = null,
        bool dryRun = false)
    {
        var utc = nowUtc ?? DateTime.UtcNow;
        var cat = string.IsNullOrWhiteSpace(category) ? DefaultCategory : category.Trim().ToLowerInvariant();

        var contact = await ResolveContactAsync(to);
        if (contact == null)
        {
            // Not a policy failure to report loudly: unknown numbers were already unreachable
            // via the guardrail's allow-list. Say so in terms the caller can act on.
            return new RoutingDecision(RoutingOutcome.Blocked, null,
                $"No routing contact for '{to}'. Add the number under Routing in the bridge admin " +
                "to make it reachable; numbers without a contact are never auto-messaged.");
        }

        // Category before Enabled, and the order is load-bearing. A category the contact never
        // accepted must stop dead here — never reach the fallback path. When Enabled was checked
        // first, muting a contact WIDENED delivery: every category that would have been refused
        // outright ("Sjoerd does not receive 'other'") instead fell through to FallbackOrBlock,
        // whose target accepts "*", and Martien started receiving the exact backlog nags the
        // policy existed to stop — triggered by the act of muting someone for their holiday.
        if (!AcceptsCategory(contact, cat))
            return new RoutingDecision(RoutingOutcome.Blocked, null,
                $"{contact.Name} does not receive '{cat}' messages (accepts: {contact.Categories}). " +
                "This is the default for team contacts: they are messaged when they ask something, not otherwise.");

        if (!contact.Enabled)
            return await FallbackOrBlockAsync(contact, cat, body, utc, dryRun,
                $"{contact.Name} is muted.");

        if (!IsInsideWindow(contact, utc, out var localTime))
            return await FallbackOrBlockAsync(contact, cat, body, utc, dryRun,
                $"{localTime:HH:mm} is outside {contact.Name}'s window " +
                $"({contact.WindowStartHour:00}:00-{contact.WindowEndHour:00}:00 {contact.TimeZoneId}).");

        // Dedupe on the direct path too, not only on redirects. With it only on the redirect
        // side, the fan-out order decided whether Martien got a duplicate: Sjoerd-then-Martien
        // was caught (the redirect found the direct row), Martien-after-redirect was not — the
        // direct leg never looked. Same recipient, same body, same category, minutes apart is
        // one message regardless of which leg delivered it first.
        if (await AlreadyDeliveredAsync(contact.Phone, cat, body, utc))
            return new RoutingDecision(RoutingOutcome.Suppressed, null,
                $"{contact.Name} already received this exact message within " +
                $"{RedirectDedupeWindow.TotalMinutes:0} minutes, so it is not sent twice.");

        return new RoutingDecision(RoutingOutcome.Allowed, contact.Phone,
            $"{contact.Name} accepts '{cat}' and it is {localTime:HH:mm} locally.");
    }

    /// <summary>
    /// True when an identical message (same recipient, body hash and category) was actually
    /// DELIVERED within the dedupe window. Two deliberate narrowings:
    ///
    /// Delivered only — the guardrail logs the row before the caller sends, so an unconfirmed
    /// row proves an attempt, not a delivery. Counting attempts here meant a failed send (session
    /// down) marked the message as "already there", and the suppression then discarded the one
    /// copy that would have arrived. Suppression must only ever trade a duplicate for silence,
    /// never a delivery for silence.
    ///
    /// A blank body never dedupes — a hash of "" is a single shared value, not an identity.
    /// Media sends pass their caption as the body, and most media has no caption, so with blank
    /// bodies eligible, any two distinct captionless images to the same recipient within ten
    /// minutes would collide and the second would silently never be delivered to anyone.
    /// </summary>
    private async Task<bool> AlreadyDeliveredAsync(string recipient, string category, string body, DateTime utc)
    {
        if (string.IsNullOrWhiteSpace(body)) return false;

        var hash = HashBody(body);
        var since = utc - RedirectDedupeWindow;
        return await _context.OutboundSendLogs.AnyAsync(l =>
            l.Recipient == recipient &&
            l.Delivered &&
            l.BodyHash == hash &&
            l.Category == category &&
            l.SentAtUtc >= since);
    }

    private async Task<RoutingDecision> FallbackOrBlockAsync(
        OutboundContact contact, string category, string body, DateTime utc, bool dryRun, string why)
    {
        if (string.IsNullOrWhiteSpace(contact.FallbackPhone))
            return new RoutingDecision(RoutingOutcome.Blocked, null,
                $"{why} No fallback configured, so the message is dropped.");

        var fallback = Normalize(contact.FallbackPhone);

        // The fallback must itself be a routing contact. Otherwise this free-text field is an
        // escape hatch rather than a safety net: a redirect tells the guardrail that the contact
        // table has already vouched for the recipient, and the guardrail then skips its static
        // allow-list. An arbitrary number reachable without passing either list is precisely the
        // hole both lists exist to close.
        var target = await _context.OutboundContacts.FirstOrDefaultAsync(c => c.Phone == fallback);
        if (target == null)
            return new RoutingDecision(RoutingOutcome.Blocked, null,
                $"{why} Its fallback '{contact.FallbackPhone}' is not itself a routing contact, " +
                "so there is nowhere to redirect to. Add that number under Routing first.");

        if (target.Id == contact.Id)
            return new RoutingDecision(RoutingOutcome.Blocked, null,
                $"{why} Its fallback points back at itself, which is not a route.");

        // The fallback's own policy is not a formality to skip on the redirect path. Handing a
        // 03:00 deploy notice to someone who is themselves asleep, muted, or who does not accept
        // this category would defeat the exact rule the redirect exists to honour. One hop only:
        // if the fallback cannot take it either the message stops here, rather than walking a
        // chain of fallbacks until it finds someone.
        if (!target.Enabled)
            return new RoutingDecision(RoutingOutcome.Blocked, null,
                $"{why} Its fallback {target.Name} is muted too, so the message is dropped.");

        if (!AcceptsCategory(target, category))
            return new RoutingDecision(RoutingOutcome.Blocked, null,
                $"{why} Its fallback {target.Name} does not receive '{category}' messages " +
                $"(accepts: {target.Categories}), so the message is dropped.");

        if (!IsInsideWindow(target, utc, out var targetLocal))
            return new RoutingDecision(RoutingOutcome.Blocked, null,
                $"{why} Its fallback {target.Name} is outside their own window as well " +
                $"({targetLocal:HH:mm} local), so the message is dropped.");

        // Already delivered to the fallback by a caller that addressed them directly? Then this
        // redirect is a duplicate, not a rescue.
        if (await AlreadyDeliveredAsync(fallback, category, body, utc))
            return new RoutingDecision(RoutingOutcome.Suppressed, null,
                $"{why} The fallback already received this exact message within " +
                $"{RedirectDedupeWindow.TotalMinutes:0} minutes, so it is not sent twice.");

        if (!dryRun)
            _logger.LogInformation("Outbound REDIRECTED from {Original} to {Fallback}: {Why}",
                contact.Phone, fallback, why);

        return new RoutingDecision(RoutingOutcome.Redirected, fallback, $"{why} Redirected to the fallback.");
    }

    /// <summary>
    /// Stamps the identity fields the dedupe matches on. Called by the guardrail on the same
    /// path that writes the volume-cap row; note that the row it stamps is an ATTEMPT until the
    /// caller confirms delivery — see <see cref="Models.OutboundSendLog.Delivered"/>.
    /// </summary>
    public static void Stamp(OutboundSendLog log, string? category, string body)
    {
        log.Category = string.IsNullOrWhiteSpace(category) ? DefaultCategory : category.Trim().ToLowerInvariant();
        log.BodyHash = HashBody(body);
    }

    /// <summary>
    /// Number first, then alias. The alias branch used to be reachable only when the input
    /// contained no digits at all, which quietly made any alias with a digit in it — "sjoerd2",
    /// "vps1" — unresolvable: normalisation pulled out the "2", found no contact with phone "2",
    /// and returned "unknown number" rather than ever trying the alias. Falling through instead
    /// of branching costs one extra query on a miss and removes the trap.
    /// </summary>
    private async Task<OutboundContact?> ResolveContactAsync(string to)
    {
        var normalized = Normalize(to);
        if (!string.IsNullOrEmpty(normalized))
        {
            var byNumber = await _context.OutboundContacts.FirstOrDefaultAsync(c => c.Phone == normalized);
            if (byNumber != null) return byNumber;
        }

        var alias = (to ?? string.Empty).Trim().ToLowerInvariant();
        if (alias.Length == 0) return null;

        return await _context.OutboundContacts
            .FirstOrDefaultAsync(c => c.Alias != null && c.Alias.ToLower() == alias);
    }

    /// <summary>
    /// "*" accepts everything. Otherwise a listed entry matches the requested category exactly,
    /// or as a parent of it: a contact listing "deploy" receives "deploy:valsuani", but one
    /// listing "deploy:valsuani" does not receive "deploy:bugatti".
    /// </summary>
    internal static bool AcceptsCategory(OutboundContact contact, string category)
    {
        var listed = (contact.Categories ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        foreach (var entry in listed)
        {
            if (entry == "*") return true;
            var e = entry.ToLowerInvariant();
            if (e == category) return true;
            if (category.StartsWith(e + ":", StringComparison.Ordinal)) return true;
        }
        return false;
    }

    /// <summary>
    /// Window membership in the contact's own timezone. A window whose end is before its start
    /// wraps midnight, so 22-6 reads as "22:00 until 06:00 the next morning" rather than as an
    /// empty window. Start equal to end is the one case that stays literal: 10-10 is a zero-width
    /// window and nothing is ever sent, which is the fail-closed reading and gives an admin a way
    /// to close someone's window without deleting the contact.
    /// </summary>
    internal static bool IsInsideWindow(OutboundContact contact, DateTime utc, out DateTime localTime)
    {
        localTime = ToLocal(contact.TimeZoneId, utc);

        var start = Math.Clamp(contact.WindowStartHour, 0, 23);
        var end = Math.Clamp(contact.WindowEndHour, 1, 24);
        if (start == 0 && end == 24) return true;
        if (start == end) return false;

        var hour = localTime.Hour;
        return start < end
            ? hour >= start && hour < end
            : hour >= start || hour < end;
    }

    private static DateTime ToLocal(string timeZoneId, DateTime utc)
    {
        try
        {
            var tz = TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
            return TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), tz);
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            // Falling back to UTC rather than to server-local: a wrong-but-known reference beats
            // one that silently follows wherever the bridge happens to be hosted.
            return utc;
        }
    }

    private static string HashBody(string body) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(body ?? string.Empty)))[..16];

    /// <summary>See <see cref="PhoneNumber.Normalize"/> — one definition, shared by every caller.</summary>
    private static string Normalize(string s) => PhoneNumber.Normalize(s);
}

/// <summary>
/// Bound from configuration section "OutboundRouting".
/// </summary>
public sealed class OutboundRoutingOptions
{
    /// <summary>
    /// Default FALSE, deliberately. Routing decides who does and does not receive a message, and
    /// a wrong policy is silent: the caller gets a block reason it usually does not read, and the
    /// person who should have been alerted simply hears nothing. Switching that on as a side
    /// effect of a deploy is not acceptable, so it takes a config change made on purpose, after
    /// looking at the contact table it will start enforcing.
    /// </summary>
    public bool Enabled { get; set; } = false;

    /// <summary>
    /// Contacts written to an empty table on first boot. Seeding is independent of
    /// <see cref="Enabled"/>: the rows appear in the admin UI so the policy can be reviewed and
    /// corrected before it governs anything.
    /// </summary>
    public List<Models.OutboundContact> Seed { get; set; } = new();
}
