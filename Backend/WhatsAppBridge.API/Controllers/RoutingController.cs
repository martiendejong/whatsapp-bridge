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
    private readonly AppDbContext _context;
    private readonly OutboundRoutingService _routing;

    public RoutingController(AppDbContext context, OutboundRoutingService routing)
    {
        _context = context;
        _routing = routing;
    }

    [HttpGet]
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
    public async Task<IActionResult> Upsert([FromBody] ContactRequest request)
    {
        var phone = new string(request.Phone.SkipWhile(c => !char.IsDigit(c)).TakeWhile(char.IsDigit).ToArray());
        if (string.IsNullOrEmpty(phone))
            return BadRequest(new { error = "Phone must contain digits." });

        if (request.WindowStartHour is < 0 or > 23)
            return BadRequest(new { error = "WindowStartHour must be 0-23." });
        if (request.WindowEndHour is < 1 or > 24)
            return BadRequest(new { error = "WindowEndHour must be 1-24." });

        // Reject an unknown timezone here rather than silently falling back to UTC at send
        // time: a typo'd zone would otherwise produce a window that is wrong by hours and
        // gives no sign of it.
        if (!IsKnownTimeZone(request.TimeZoneId))
            return BadRequest(new { error = $"Unknown timezone '{request.TimeZoneId}'. Use an IANA id such as 'Europe/Amsterdam'." });

        var contact = await _context.OutboundContacts.FirstOrDefaultAsync(c => c.Phone == phone);
        if (contact == null)
        {
            contact = new OutboundContact { Phone = phone };
            _context.OutboundContacts.Add(contact);
        }

        contact.Name = request.Name;
        contact.Alias = string.IsNullOrWhiteSpace(request.Alias) ? null : request.Alias.Trim().ToLowerInvariant();
        contact.Enabled = request.Enabled;
        contact.TimeZoneId = request.TimeZoneId;
        contact.WindowStartHour = request.WindowStartHour;
        contact.WindowEndHour = request.WindowEndHour;
        contact.Categories = string.IsNullOrWhiteSpace(request.Categories) ? "*" : request.Categories.Trim();
        contact.FallbackPhone = string.IsNullOrWhiteSpace(request.FallbackPhone) ? null : request.FallbackPhone.Trim();
        contact.UpdatedAtUtc = DateTime.UtcNow;

        await _context.SaveChangesAsync();
        return Ok(new { contact.Id, contact.Phone });
    }

    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Delete(int id)
    {
        var contact = await _context.OutboundContacts.FindAsync(id);
        if (contact == null) return NotFound();

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
        var when = DateTime.TryParse(atUtc, out var parsed)
            ? DateTime.SpecifyKind(parsed, DateTimeKind.Utc)
            : DateTime.UtcNow;

        var decision = await _routing.RouteAsync(to, category, string.Empty, when);
        return Ok(new
        {
            outcome = decision.Outcome.ToString(),
            recipient = decision.Recipient,
            reason = decision.Reason,
            evaluatedAtUtc = when,
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
