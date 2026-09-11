using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using WhatsAppBridge.API.Data;
using WhatsAppBridge.API.Models;
using WhatsAppBridge.API.Services;

namespace WhatsAppBridge.API.Controllers;

/// <summary>
/// Admin view over the monitor's subject list: which domains are kritiek (5 min) versus
/// normaal (30 min), editable without a deploy. The reporting endpoint itself lives in
/// WhatsAppApiController (POST /api/wa/monitor) under API-token auth, because that is what the
/// monitoring scripts hold; THIS controller is JWT-admin-only, because whoever can flip a
/// production domain to the slow tier can delay its outage alert by 25 minutes.
///
/// Subjects normally self-register on their first report, so POST here is for the two edits
/// that matter: promoting a domain to kritiek, and pre-registering one before its monitor
/// exists.
/// </summary>
[ApiController]
[Route("api/wa/monitor/subjects")]
[Authorize(Roles = "Admin", AuthenticationSchemes = "Bearer")]
public class MonitorAdminController : ControllerBase
{
    private readonly AppDbContext _context;
    private readonly ServerMonitorService _monitor;

    public MonitorAdminController(AppDbContext context, ServerMonitorService monitor)
    {
        _context = context;
        _monitor = monitor;
    }

    [HttpGet]
    public async Task<IActionResult> List()
    {
        var now = DateTime.UtcNow;
        var subjects = await _context.MonitorSubjects
            .AsNoTracking()
            .OrderByDescending(s => s.Critical)
            .ThenBy(s => s.Subject)
            .ToListAsync();

        return Ok(subjects.Select(s => new
        {
            s.Id,
            s.Subject,
            critical = s.Critical,
            thresholdMinutes = _monitor.ThresholdMinutes(s),
            s.Status,
            s.LastDetail,
            s.FirstDownAtUtc,
            downAlerted = s.DownAlertAtUtc != null,
            monitorSilent = s.SilenceAlertAtUtc != null,
            s.LastReportAtUtc,
            minutesSinceLastReport = s.LastReportAtUtc == default
                ? (int?)null
                : (int)(now - s.LastReportAtUtc).TotalMinutes,
        }));
    }

    public record UpsertRequest(string Subject, bool Critical);

    [HttpPost]
    public async Task<IActionResult> Upsert([FromBody] UpsertRequest request)
    {
        var key = ServerMonitorService.Normalize(request.Subject);
        if (key.Length < 3 || !key.Contains('.'))
            return BadRequest(new { error = "Subject must be a hostname, e.g. \"portofgiethoorn.com\"." });

        var now = DateTime.UtcNow;
        var row = await _context.MonitorSubjects.FirstOrDefaultAsync(s => s.Subject == key);
        if (row == null)
        {
            // Pre-registered by hand: no reports yet, so LastReportAtUtc stays at default and
            // the silence sweep leaves it alone until the first report arrives (a subject whose
            // monitor was never built should show up as "nog nooit gemeld", not page someone
            // about a monitor that does not exist).
            row = new MonitorSubject { Subject = key, CreatedAtUtc = now, Status = "up" };
            _context.MonitorSubjects.Add(row);
        }

        row.Critical = request.Critical;
        row.UpdatedAtUtc = now;
        await _context.SaveChangesAsync();

        return Ok(new { row.Id, row.Subject, critical = row.Critical, thresholdMinutes = _monitor.ThresholdMinutes(row) });
    }

    [HttpDelete("{id:long}")]
    public async Task<IActionResult> Delete(long id)
    {
        var row = await _context.MonitorSubjects.FindAsync(id);
        if (row == null) return NotFound();

        _context.MonitorSubjects.Remove(row);
        await _context.SaveChangesAsync();
        // Note for the caller: if this subject's monitor is still reporting, the row returns on
        // its next report — as "normaal". Deleting is for retired domains, not for silencing.
        return Ok(new { deleted = id });
    }
}
