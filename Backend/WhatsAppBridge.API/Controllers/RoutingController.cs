using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using WhatsAppBridge.API.Data;
using WhatsAppBridge.API.Models;
using WhatsAppBridge.API.Services;

namespace WhatsAppBridge.API.Controllers;

/// <summary>
/// Read and edit the outbound routing policy: who may be messaged, about what, and when in
/// their own timezone.
///
/// Deliberately editable at runtime rather than through appsettings. "Sjoerd is on holiday,
/// mute him until Monday" should not require a deploy, and a window that can only be changed
/// by redeploying is a window nobody changes.
/// </summary>
[ApiController]
[Route("api/wa/routing")]
[Authorize(AuthenticationSchemes = "ApiKey,Bearer")]
public class RoutingController : ControllerBase
{
    /// <summary>
    /// Reading and changing the policy are not the same privilege.
    ///
    /// Anyone holding a valid API token may ask the preview endpoint "would a message to this
    /// number land right now" — that is the question a sender needs answered, and refusing it
    /// only encourages callers to find out by sending. Changing who may be messaged, muting
    /// someone, or pointing a fallback somewhere new is an admin action, and it is deliberately
    /// restricted to the Bearer scheme: API keys carry no role claim at all, so an unqualified
    /// [Authorize(Roles = "Admin")] would be satisfiable by no API key and misleadingly written
    /// as though it might be. The rule is "a logged-in admin in the browser, not a token".
    /// </summary>
    private const string AdminOnly = "Bearer";

    private readonly AppDbContext _context;
    private readonly OutboundRoutingService _routing;

    public RoutingController(AppDbContext context, OutboundRoutingService routing)
    {
        _context = context;
        _routing = routing;
    }

    [HttpGet]
    [Authorize(Roles = "Admin", AuthenticationSchemes = AdminOnly)]
    public async Task<IActionResult> List()
    {
        var contacts = await _context.OutboundContacts
            .OrderBy(c => c.Name)
            .ToListAsync();

        return Ok(contacts.Select(c => new
        {
            c.Id, c.Phone, c.Name, c.Alias, c.Enabled, c.TimeZoneId,
            c.WindowStartHour, c.WindowEndHour, c.Categories, c.FallbackPhone,
            c.UpdatedAtUtc,
            // The one thing you actually want to see at a glance: can I reach this person now.
            OpenNow = OutboundRoutingService.IsInsideWindow(c, DateTime.UtcNow, out _),
        }));
    }

    [HttpPost]
    [Authorize(Roles = "Admin", AuthenticationSchemes = AdminOnly)]
    public async Task<IActionResult> Upsert([FromBody] ContactRequest request)
    {
        var phone = PhoneNumber.Normalize(request.Phone);
        if (!PhoneNumber.IsUsable(phone))
            return BadRequest(new { error = "Phone must be a number with at least 6 digits." });

        if (request.WindowStartHour is < 0 or > 23)
            return BadRequest(new { error = "WindowStartHour must be 0-23." });
        if (request.WindowEndHour is < 1 or > 24)
            return BadRequest(new { error = "WindowEndHour must be 1-24." });

        // Reject an unknown timezone here rather than silently falling back to UTC at send
        // time: a typo'd zone would otherwise produce a window that is wrong by hours and
        // gives no sign of it.
        if (!IsKnownTimeZone(request.TimeZoneId))
            return BadRequest(new { error = $"Unknown timezone '{request.TimeZoneId}'. Use an IANA id such as 'Europe/Amsterdam'." });

        // An empty category list used to be silently rewritten to "*" — receives everything. That
        // turned the most likely mistake in this form (leaving a field blank) into the most
        // permissive possible setting, and it disagreed with the engine, which treats an empty
        // value in the database as "receives nothing". Fail closed, and say what is missing.
        if (string.IsNullOrWhiteSpace(request.Categories))
            return BadRequest(new { error = "Categories is required. Use '*' for everything, or a comma-separated list such as 'deploy:valsuani,reply'." });

        if (string.IsNullOrWhiteSpace(request.Name))
            return BadRequest(new { error = "Name is required." });

        // A fallback must be an existing contact. The redirect path tells the guardrail that the
        // contact table has already vouched for the recipient, so a free-text number here would
        // reach WhatsApp without passing either the contact table or the static allow-list.
        string? fallback = null;
        if (!string.IsNullOrWhiteSpace(request.FallbackPhone))
        {
            fallback = PhoneNumber.Normalize(request.FallbackPhone);
            if (fallback == phone)
                return BadRequest(new { error = "FallbackPhone cannot be the contact's own number." });
            if (!await _context.OutboundContacts.AnyAsync(c => c.Phone == fallback))
                return BadRequest(new { error = $"FallbackPhone '{request.FallbackPhone}' is not a routing contact yet. Add that number first, then set it as a fallback." });
        }

        // One alias, one contact. Aliases resolve via FirstOrDefault, so a duplicate would make
        // "sjoerd" deliver to whichever row the provider happens to return first — a policy
        // decided by storage order is not a policy.
        var alias = string.IsNullOrWhiteSpace(request.Alias) ? null : request.Alias.Trim().ToLowerInvariant();
        if (alias != null && await _context.OutboundContacts
                .AnyAsync(c => c.Phone != phone && c.Alias != null && c.Alias.ToLower() == alias))
            return BadRequest(new { error = $"Alias '{alias}' is already used by another contact." });

        var contact = await _context.OutboundContacts.FirstOrDefaultAsync(c => c.Phone == phone);
        if (contact == null)
        {
            contact = new OutboundContact { Phone = phone, CreatedAtUtc = DateTime.UtcNow };
            _context.OutboundContacts.Add(contact);
        }

        contact.Name = request.Name.Trim();
        contact.Alias = alias;
        contact.Enabled = request.Enabled;
        contact.TimeZoneId = request.TimeZoneId;
        contact.WindowStartHour = request.WindowStartHour;
        contact.WindowEndHour = request.WindowEndHour;
        contact.Categories = request.Categories.Trim();
        contact.FallbackPhone = fallback;
        contact.UpdatedAtUtc = DateTime.UtcNow;

        await _context.SaveChangesAsync();
        return Ok(new { contact.Id, contact.Phone });
    }

    [HttpDelete("{id:int}")]
    [Authorize(Roles = "Admin", AuthenticationSchemes = AdminOnly)]
    public async Task<IActionResult> Delete(int id)
    {
        var contact = await _context.OutboundContacts.FindAsync(id);
        if (contact == null) return NotFound();

        // Upsert refuses a fallback that is not a contact; deleting the contact out from under
        // the fallbacks that point at it would break the same invariant through the back door.
        // The engine fails closed on a dangling fallback, but "closed" here means overnight
        // redirects silently stop being delivered — the safety net the admin explicitly set up
        // would be gone, and the only symptom rows in a blocked-list nobody reads at 03:00.
        var dependents = await _context.OutboundContacts
            .Where(c => c.FallbackPhone == contact.Phone)
            .Select(c => c.Name)
            .ToListAsync();
        if (dependents.Count > 0)
            return BadRequest(new
            {
                error = $"'{contact.Name}' is the fallback for: {string.Join(", ", dependents)}. " +
                        "Change or clear those fallbacks first, then delete this contact.",
            });

        _context.OutboundContacts.Remove(contact);
        await _context.SaveChangesAsync();
        return Ok(new { deleted = id });
    }

    /// <summary>
    /// Answers "if I send a <paramref name="category"/> message to <paramref name="to"/> right
    /// now, who gets it?" without sending anything. The policy has enough moving parts —
    /// category matching, wrapped windows, two timezones, fallbacks — that being able to ask it
    /// directly beats inferring the answer from whether a message arrived.
    /// </summary>
    [HttpGet("preview")]
    public async Task<IActionResult> Preview([FromQuery] string to, [FromQuery] string? category, [FromQuery] string? atUtc)
    {
        // AssumeUniversal covers the common "2026-09-11T03:00" with no zone on it; AdjustToUniversal
        // converts the ones that do carry an offset instead of keeping the local wall-clock reading.
        // The previous plain TryParse + SpecifyKind(Utc) did the opposite of both: it took the
        // parser's local-time interpretation and then relabelled it UTC, so a preview for 03:00
        // was silently evaluated as 01:00 in summer — which is the difference between inside and
        // outside a window, on the one endpoint whose whole job is answering that question.
        var when = DateTime.TryParse(atUtc, System.Globalization.CultureInfo.InvariantCulture,
                       System.Globalization.DateTimeStyles.AdjustToUniversal |
                       System.Globalization.DateTimeStyles.AssumeUniversal, out var parsed)
            ? DateTime.SpecifyKind(parsed, DateTimeKind.Utc)
            : DateTime.UtcNow;

        var decision = await _routing.RouteAsync(to, category, string.Empty, when, dryRun: true);

        // Whether any of this is actually enforced right now. Without it the preview happily
        // shows redirects and blocks for a policy that nothing applies, and an admin reading it
        // concludes the table is live when the flag is still off.
        var enforced = await _routing.IsActiveAsync();
        return Ok(new
        {
            outcome = decision.Outcome.ToString(),
            recipient = decision.Recipient,
            reason = decision.Reason,
            evaluatedAtUtc = when,
            enforced,
            note = enforced ? null
                : "OutboundRouting:Enabled is off (or the contact table is empty) — this shows what WOULD happen, nothing is currently enforced.",
        });
    }

    /// <summary>
    /// IANA ids only. On Windows the system list is in Windows form ("W. Europe Standard Time"),
    /// and while .NET 8 accepts both at lookup time, offering the Windows names would let an
    /// admin store a zone that stops resolving the day this moves to a Linux host.
    /// </summary>
    [HttpGet("timezones")]
    public IActionResult TimeZones() =>
        Ok(TimeZoneInfo.GetSystemTimeZones()
            .Select(t => TimeZoneInfo.TryConvertWindowsIdToIanaId(t.Id, out var iana) ? iana : t.Id)
            .Where(id => id.Contains('/'))
            .Distinct()
            .OrderBy(id => id));

    private static bool IsKnownTimeZone(string id)
    {
        try
        {
            TimeZoneInfo.FindSystemTimeZoneById(id);
            return true;
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            return false;
        }
    }

    public record ContactRequest(
        string Phone,
        string Name,
        string? Alias,
        bool Enabled,
        string TimeZoneId,
        int WindowStartHour,
        int WindowEndHour,
        string? Categories,
        string? FallbackPhone);
}
