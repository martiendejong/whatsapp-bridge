using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using WhatsAppBridge.API.Data;

namespace WhatsAppBridge.API.Controllers;

/// <summary>
/// Read-only views over <see cref="Models.ApiAuditLog"/>: the full request history, the
/// per-number conversation view, and the distinct values that populate the filter dropdowns.
///
/// Scoping: an admin sees everything; a normal user sees only rows attributed to their own
/// user id. The audit trail exists to make outbound sends accountable, so it must not itself
/// become a way for one tenant to read another's message bodies.
/// </summary>
[ApiController]
[Route("api/wa/audit")]
[Authorize(AuthenticationSchemes = "ApiKey,Bearer")]
public class AuditController : ControllerBase
{
    private readonly AppDbContext _context;

    public AuditController(AppDbContext context)
    {
        _context = context;
    }

    /// <summary>
    /// Filterable request history, newest first.
    /// Every filter is optional and they combine (AND).
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> List(
        [FromQuery] string? phone,
        [FromQuery] string? eventType,
        [FromQuery] string? outcome,
        [FromQuery] int? apiConnectionId,
        [FromQuery] DateTime? fromUtc,
        [FromQuery] DateTime? toUtc,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50)
    {
        var query = Scoped();

        if (!string.IsNullOrWhiteSpace(phone))
        {
            var normalized = NormalizePhone(phone);
            query = query.Where(a => a.Phone == normalized);
        }

        if (!string.IsNullOrWhiteSpace(eventType))
            query = query.Where(a => a.EventType == eventType);

        if (!string.IsNullOrWhiteSpace(outcome))
            query = query.Where(a => a.Outcome == outcome);

        if (apiConnectionId.HasValue)
            query = query.Where(a => a.ApiConnectionId == apiConnectionId.Value);

        if (fromUtc.HasValue)
            query = query.Where(a => a.AtUtc >= fromUtc.Value);

        if (toUtc.HasValue)
            query = query.Where(a => a.AtUtc <= toUtc.Value);

        var total = await query.CountAsync();

        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 500);

        var items = await query
            .OrderByDescending(a => a.AtUtc)
            .ThenByDescending(a => a.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(a => new
            {
                a.Id,
                a.AtUtc,
                a.Method,
                a.Path,
                a.EventType,
                a.Phone,
                a.Body,
                a.ResponsePreview,
                a.StatusCode,
                a.Outcome,
                a.DurationMs,
                a.ApiConnectionId,
                a.ApiConnectionName,
                a.AuthScheme,
                a.ClientIp,
            })
            .ToListAsync();

        return Ok(new { total, page, pageSize, items });
    }

    /// <summary>
    /// One row per number the bridge has interacted with, with counts — the index for
    /// "chats per nummer inzichtelijk". Click a number and call <see cref="List"/> with
    /// ?phone= to get its full history.
    /// </summary>
    [HttpGet("phones")]
    public async Task<IActionResult> Phones()
    {
        var rows = await Scoped()
            .Where(a => a.Phone != null && a.Phone != "")
            .GroupBy(a => a.Phone!)
            .Select(g => new
            {
                phone = g.Key,
                total = g.Count(),
                sent = g.Count(a => a.Outcome == "ok"),
                blocked = g.Count(a => a.Outcome == "blocked"),
                errors = g.Count(a => a.Outcome == "error"),
                lastAtUtc = g.Max(a => a.AtUtc),
            })
            .OrderByDescending(r => r.lastAtUtc)
            .ToListAsync();

        return Ok(rows);
    }

    /// <summary>Distinct event types present in the log, with counts — populates the filter.</summary>
    [HttpGet("event-types")]
    public async Task<IActionResult> EventTypes()
    {
        var rows = await Scoped()
            .GroupBy(a => a.EventType)
            .Select(g => new { eventType = g.Key, count = g.Count() })
            .OrderByDescending(r => r.count)
            .ToListAsync();

        return Ok(rows);
    }

    /// <summary>Full detail for one entry, including the untruncated stored body.</summary>
    [HttpGet("{id:int}")]
    public async Task<IActionResult> Detail(int id)
    {
        var entry = await Scoped().FirstOrDefaultAsync(a => a.Id == id);
        return entry == null ? NotFound() : Ok(entry);
    }

    /// <summary>
    /// Admins see the whole log; everyone else sees only their own rows. Anonymous requests
    /// are recorded with a null UserId and are therefore visible to admins only — which is
    /// correct, since an unauthenticated call is exactly what an operator needs to be able
    /// to review.
    /// </summary>
    private IQueryable<Models.ApiAuditLog> Scoped()
    {
        var query = _context.ApiAuditLogs.AsNoTracking();
        if (IsAdmin()) return query;

        var userId = CurrentUserId();
        return userId == null
            ? query.Where(_ => false)
            : query.Where(a => a.UserId == userId);
    }

    private bool IsAdmin() =>
        User.IsInRole("Admin") ||
        string.Equals(User.FindFirst("IsAdmin")?.Value, "true", StringComparison.OrdinalIgnoreCase);

    private int? CurrentUserId() =>
        int.TryParse(User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value, out var id)
            ? id
            : null;

    private static string NormalizePhone(string s) =>
        new(s.SkipWhile(c => !char.IsDigit(c)).TakeWhile(char.IsDigit).ToArray());
}
