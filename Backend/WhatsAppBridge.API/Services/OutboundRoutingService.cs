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

    /// <summary>
    /// How long a redirect looks back for an identical message already delivered to the
    /// fallback. Most callers fan out to Sjoerd and Martien in the same breath; without this,
    /// a nightly deploy notice would reach Martien twice — once addressed to him, once
    /// redirected off Sjoerd. Long enough to cover a slow fan-out, short enough that a genuine
    /// repeat alert ten minutes later still lands.
    /// </summary>
    private static readonly TimeSpan RedirectDedupeWindow = TimeSpan.FromMinutes(10);

    public const string DefaultCategory = "other";

    public OutboundRoutingService(AppDbContext context, ILogger<OutboundRoutingService> logger)
    {
        _context = context;
        _logger = logger;
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
    /// False while no contact has been configured. A fresh or un-migrated deployment then keeps
    /// its previous behaviour instead of silently blocking every send: an empty policy table is
    /// far more likely to mean "not set up yet" than "nobody may be messaged".
    /// </summary>
    public Task<bool> IsConfiguredAsync() => _context.OutboundContacts.AnyAsync();

    /// <summary>
    /// Resolves an alias or number to the number a message should actually go to.
    /// <paramref name="category"/> may be null; it is then treated as <see cref="DefaultCategory"/>.
    /// </summary>
    public async Task<RoutingDecision> RouteAsync(string to, string? category, string body, DateTime? nowUtc = null)
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

        if (!contact.Enabled)
            return await FallbackOrBlockAsync(contact, cat, body, utc,
                $"{contact.Name} is muted.");

        if (!AcceptsCategory(contact, cat))
            return new RoutingDecision(RoutingOutcome.Blocked, null,
                $"{contact.Name} does not receive '{cat}' messages (accepts: {contact.Categories}). " +
                "This is the default for team contacts: they are messaged when they ask something, not otherwise.");

        if (!IsInsideWindow(contact, utc, out var localTime))
            return await FallbackOrBlockAsync(contact, cat, body, utc,
                $"{localTime:HH:mm} is outside {contact.Name}'s window " +
                $"({contact.WindowStartHour:00}:00-{contact.WindowEndHour:00}:00 {contact.TimeZoneId}).");

        return new RoutingDecision(RoutingOutcome.Allowed, contact.Phone,
            $"{contact.Name} accepts '{cat}' and it is {localTime:HH:mm} locally.");
    }

    private async Task<RoutingDecision> FallbackOrBlockAsync(
        OutboundContact contact, string category, string body, DateTime utc, string why)
    {
        if (string.IsNullOrWhiteSpace(contact.FallbackPhone))
            return new RoutingDecision(RoutingOutcome.Blocked, null,
                $"{why} No fallback configured, so the message is dropped.");

        var fallback = Normalize(contact.FallbackPhone);

        // Already delivered to the fallback by a caller that addressed them directly? Then this
        // redirect is a duplicate, not a rescue.
        var hash = HashBody(body);
        var since = utc - RedirectDedupeWindow;
        var alreadyDelivered = await _context.OutboundSendLogs.AnyAsync(l =>
            l.Recipient == fallback &&
            l.BodyHash == hash &&
            l.Category == category &&
            l.SentAtUtc >= since);

        if (alreadyDelivered)
            return new RoutingDecision(RoutingOutcome.Suppressed, null,
                $"{why} The fallback already received this exact message within " +
                $"{RedirectDedupeWindow.TotalMinutes:0} minutes, so it is not sent twice.");

        _logger.LogInformation("Outbound REDIRECTED from {Original} to {Fallback}: {Why}",
            contact.Phone, fallback, why);

        return new RoutingDecision(RoutingOutcome.Redirected, fallback, $"{why} Redirected to the fallback.");
    }

    /// <summary>
    /// Records a delivered send so the redirect dedupe above can see it. Called by the guardrail
    /// on the same path that already writes the volume-cap row.
    /// </summary>
    public static void Stamp(OutboundSendLog log, string? category, string body)
    {
        log.Category = string.IsNullOrWhiteSpace(category) ? DefaultCategory : category.Trim().ToLowerInvariant();
        log.BodyHash = HashBody(body);
    }

    private async Task<OutboundContact?> ResolveContactAsync(string to)
    {
        var normalized = Normalize(to);
        if (!string.IsNullOrEmpty(normalized))
            return await _context.OutboundContacts.FirstOrDefaultAsync(c => c.Phone == normalized);

        // No digits at all — the caller used an alias like "martien".
        var alias = to.Trim().ToLowerInvariant();
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

    /// <summary>Leading digit run, matching OutboundGuardrailService.Normalize.</summary>
    private static string Normalize(string s) =>
        new(s.SkipWhile(c => !char.IsDigit(c)).TakeWhile(char.IsDigit).ToArray());
}
