using Microsoft.EntityFrameworkCore;
using WhatsAppBridge.API.Data;
using WhatsAppBridge.API.Models;

namespace WhatsAppBridge.API.Services;

/// <summary>
/// The decision engine behind POST /api/wa/monitor. Monitors report status; this class holds
/// the per-subject state machine and answers one question: does THIS report (or THIS moment,
/// for the sweep) warrant a WhatsApp message?
///
/// Deliberately does not send anything. It returns <see cref="MonitorAlert"/>s and the caller
/// dispatches them through the outbound guardrail — the same chokepoint every other sender
/// uses, so monitor alerts get the routing policy, the volume caps and the audit trail for
/// free rather than a private bypass. Keeping the machine pure also makes every timing rule
/// testable with an injected clock; the previous generation of monitoring logic lived inside
/// PowerShell watchdogs and was tested by waiting for the next outage.
///
/// The state machine, per subject (thresholds: kritiek 5 min, normaal 30 min):
///
///   report down, was up        -> record only, start the outage clock
///   report down, past threshold-> ONE alert, then silence however often "down" repeats
///   report up, alert was sent  -> recovery message ("weer online na 40 min")
///   report up, no alert yet    -> a blip; nothing, clock reset
///   no reports at all too long -> "monitor is stil" alert (the sweep), once per silence;
///                                 reports resuming produces the matching all-clear
///
/// The sweep also fires the threshold alert when the REPORTER died mid-outage: first "down"
/// arrives, then nothing — on-report evaluation alone would never alert, which is exactly the
/// wrong moment to be blind.
/// </summary>
public sealed class ServerMonitorService
{
    private readonly AppDbContext _context;
    private readonly ILogger<ServerMonitorService> _logger;
    private readonly ServerMonitorOptions _options;

    public ServerMonitorService(AppDbContext context, IConfiguration configuration,
        ILogger<ServerMonitorService> logger)
    {
        _context = context;
        _logger = logger;
        _options = new ServerMonitorOptions();
        configuration.GetSection("Monitor").Bind(_options);
    }

    /// <summary>What a state transition decided should be said, and about which subject.</summary>
    public sealed record MonitorAlert(string Subject, string Kind, string Text);

    public sealed record ReportOutcome(string Subject, string Status, bool Critical,
        int ThresholdMinutes, string Disposition, MonitorAlert? Alert);

    /// <summary>
    /// Processes one status report. Persists the state change; returns the alert to dispatch,
    /// if this report produced one.
    /// </summary>
    public async Task<ReportOutcome> ReportAsync(string subject, string status, string? detail,
        DateTime nowUtc)
    {
        var key = Normalize(subject);
        var isDown = string.Equals(status, "down", StringComparison.OrdinalIgnoreCase);

        var row = await _context.MonitorSubjects.FirstOrDefaultAsync(s => s.Subject == key);
        if (row == null)
        {
            // Self-registration, deliberately as "normaal": unknown subjects are non-production
            // by Martien's definition, so they get the slow tier rather than a rejection. The
            // row is visible in the admin list the moment it first reports, where it can be
            // promoted to kritiek without a deploy.
            row = new MonitorSubject
            {
                Subject = key,
                Critical = _options.CriticalSubjects.Any(c => Normalize(c) == key),
                Status = "up",
                CreatedAtUtc = nowUtc,
            };
            _context.MonitorSubjects.Add(row);
        }

        var resumed = row.SilenceAlertAtUtc != null;
        var silentFor = resumed ? Math.Max(1, (int)Math.Round((nowUtc - row.LastReportAtUtc).TotalMinutes)) : 0;
        row.SilenceAlertAtUtc = null;
        row.LastReportAtUtc = nowUtc;
        Touch(row, nowUtc);
        // Truncated at the source: this string is interpolated into the outgoing WhatsApp
        // message, and a monitor that POSTs a 50 KB stack trace as "detail" should not produce
        // a 50 KB WhatsApp message.
        if (!string.IsNullOrWhiteSpace(detail))
        {
            var trimmed = detail.Trim();
            row.LastDetail = trimmed.Length > 200 ? trimmed[..200] : trimmed;
        }

        MonitorAlert? alert = null;
        string disposition;

        if (isDown)
        {
            if (row.Status != "down")
            {
                // First "down" of an episode: start the clock, say nothing. Most of these
                // resolve before the threshold, and a message for each would be the boy who
                // cried wolf by Tuesday.
                row.Status = "down";
                row.FirstDownAtUtc = nowUtc;
                row.DownAlertAtUtc = null;
                disposition = "recorded";
            }
            else
            {
                alert = EvaluateOutage(row, nowUtc);
                disposition = alert != null ? "alerted" : row.DownAlertAtUtc != null ? "already-alerted" : "waiting";
            }
        }
        else
        {
            if (row.Status == "down")
            {
                if (row.DownAlertAtUtc != null && row.FirstDownAtUtc != null)
                {
                    // Recovery is only news when the outage was news.
                    var minutes = Math.Max(1, (int)Math.Round((nowUtc - row.FirstDownAtUtc.Value).TotalMinutes));
                    alert = new MonitorAlert(key, "recovered",
                        $"{key} is weer online na {minutes} min offline (sinds {row.FirstDownAtUtc:HH:mm} UTC).");
                    disposition = "recovered";
                }
                else
                {
                    disposition = "blip";
                }
                row.Status = "up";
                row.FirstDownAtUtc = null;
                row.DownAlertAtUtc = null;
            }
            else
            {
                disposition = "ok";
            }
        }

        // A monitor that resumes after a silence alert closes that loop explicitly — otherwise
        // the operator is left holding an open "is it still broken?" question the log answers
        // but nobody re-reads. When the resume carries bad news, the outage/recovery message
        // wins the single slot: it is the more actionable of the two.
        if (resumed && alert == null)
        {
            alert = new MonitorAlert(key, "monitor-resumed",
                $"De monitor voor {key} meldt zich weer na {silentFor} min stilte. Status: {(isDown ? "down" : "up")}.");
        }

        try
        {
            await _context.SaveChangesAsync();
        }
        catch (DbUpdateConcurrencyException)
        {
            // The sweep got to this subject in the same instant and won the version race. Its
            // scope handles whatever alert this transition warranted; this report stands down
            // rather than double-page. The next report (~5 min) re-syncs the row's bookkeeping.
            _logger.LogDebug("Monitor report for {Subject} lost the version race to the sweep; standing down.", key);
            return new ReportOutcome(key, isDown ? "down" : "up", row.Critical,
                ThresholdMinutes(row), "conflict", null);
        }

        if (alert != null)
            _logger.LogInformation("Monitor alert ({Kind}) for {Subject}: {Text}", alert.Kind, key, alert.Text);

        return new ReportOutcome(key, isDown ? "down" : "up", row.Critical,
            ThresholdMinutes(row), disposition, alert);
    }

    /// <summary>
    /// Undoes an alert claim after a TRANSIENT dispatch failure (no connected session, send
    /// exception), so the next sweep tick re-decides and re-fires — once a minute until it
    /// lands. Without this, the state machine believed an alert was announced the moment it
    /// decided to announce it, and a WhatsApp session that happened to be down at the threshold
    /// minute swallowed the outage alert forever while the recovery message later arrived for
    /// an outage nobody was told about.
    ///
    /// Deliberately NOT called for policy refusals (guardrail block, dedupe suppress): those
    /// are decisions, and retrying a decision once a minute is how a blocked-messages list
    /// fills up overnight. Only "down" and "monitor-silent" carry a claim; "recovered" and
    /// "monitor-resumed" are informational and their loss leaves no lie in the state.
    /// </summary>
    public async Task ReleaseAlertClaimAsync(MonitorAlert alert)
    {
        var row = await _context.MonitorSubjects.FirstOrDefaultAsync(s => s.Subject == alert.Subject);
        if (row == null) return;

        switch (alert.Kind)
        {
            case "down":
                row.DownAlertAtUtc = null;
                break;
            case "monitor-silent":
                row.SilenceAlertAtUtc = null;
                break;
            default:
                return;
        }

        Touch(row, DateTime.UtcNow);
        try
        {
            await _context.SaveChangesAsync();
        }
        catch (DbUpdateConcurrencyException)
        {
            // Someone else changed the subject's state in the meantime (a report arrived).
            // Their view of the world is newer; leave it be.
        }
    }

    /// <summary>
    /// The periodic pass (called every minute by the background worker). Catches the two cases
    /// no report will ever trigger: an outage whose reporter died before the threshold, and a
    /// monitor that has simply stopped reporting.
    /// </summary>
    public async Task<List<MonitorAlert>> SweepAsync(DateTime nowUtc)
    {
        var alerts = new List<MonitorAlert>();
        var subjects = await _context.MonitorSubjects.ToListAsync();

        foreach (var row in subjects)
        {
            // Pre-registered by an admin, monitor not built yet: nothing to measure silence
            // against. It shows as "nog nooit gemeld" in the list, which is the right nag.
            if (row.LastReportAtUtc == default) continue;

            var changed = false;
            var outage = EvaluateOutage(row, nowUtc);
            if (outage != null)
            {
                alerts.Add(outage);
                changed = true;
            }

            var silentMinutes = (nowUtc - row.LastReportAtUtc).TotalMinutes;
            if (row.SilenceAlertAtUtc == null && silentMinutes >= _options.SilenceAfterMinutes)
            {
                row.SilenceAlertAtUtc = nowUtc;
                alerts.Add(new MonitorAlert(row.Subject, "monitor-silent",
                    $"De monitor voor {row.Subject} heeft sinds {row.LastReportAtUtc:HH:mm} UTC " +
                    $"({(int)silentMinutes} min) niets gemeld; laatste status: {row.Status}. " +
                    "Een stille monitor ziet er hetzelfde uit als een gezonde server."));
                changed = true;
            }

            if (changed) Touch(row, nowUtc);
        }

        if (alerts.Count > 0)
        {
            try
            {
                await _context.SaveChangesAsync();
            }
            catch (DbUpdateConcurrencyException)
            {
                // A report raced us on one of these subjects — its scope has fresher state and
                // handles its own alert. Nothing was persisted here, so every still-valid alert
                // simply regenerates on the next tick; dropping this batch cannot lose an
                // outage, only delay its announcement by a minute.
                _logger.LogDebug("Monitor sweep lost a version race; retrying next tick.");
                return new List<MonitorAlert>();
            }
            foreach (var a in alerts)
                _logger.LogInformation("Monitor sweep alert ({Kind}) for {Subject}: {Text}", a.Kind, a.Subject, a.Text);
        }

        return alerts;
    }

    /// <summary>
    /// One alert per outage, at the moment the threshold is crossed — shared by the on-report
    /// path and the sweep so the two can never disagree about when that moment is.
    /// </summary>
    private MonitorAlert? EvaluateOutage(MonitorSubject row, DateTime nowUtc)
    {
        if (row.Status != "down" || row.DownAlertAtUtc != null || row.FirstDownAtUtc == null)
            return null;

        var downMinutes = (nowUtc - row.FirstDownAtUtc.Value).TotalMinutes;
        var threshold = ThresholdMinutes(row);
        if (downMinutes < threshold) return null;

        row.DownAlertAtUtc = nowUtc;
        var detail = string.IsNullOrWhiteSpace(row.LastDetail) ? "" : $" ({row.LastDetail})";
        // The "sinds HH:mm" stamp is not decoration: it makes the text unique per OUTAGE. The
        // guardrail dedupes identical bodies within ten minutes, and without the stamp a server
        // that flaps twice inside that window produces byte-identical alerts — the second
        // outage's announcement would be suppressed as a duplicate of the first.
        return new MonitorAlert(row.Subject, "down",
            $"{row.Subject} is al {(int)downMinutes} min offline{detail}, sinds " +
            $"{row.FirstDownAtUtc:HH:mm} UTC. Niveau: {(row.Critical ? "kritiek" : "normaal")}.");
    }

    public int ThresholdMinutes(MonitorSubject row) =>
        row.Critical ? _options.CriticalThresholdMinutes : _options.NormalThresholdMinutes;

    private static void Touch(MonitorSubject row, DateTime nowUtc)
    {
        row.UpdatedAtUtc = nowUtc;
        row.Version++;
    }

    /// <summary>
    /// Subjects are hostnames. A monitor that reports its full probe URL — scheme, path, query
    /// and all — used to register a NEW subject per unique URL, and a probe with a run id in
    /// the query string would mint a fresh subject every five minutes, each earning its own
    /// silence alert an hour later. So identity is the host part only: scheme stripped, cut at
    /// the first slash, lowercased.
    /// </summary>
    public static string Normalize(string subject)
    {
        var s = (subject ?? string.Empty).Trim().ToLowerInvariant();
        var schemeEnd = s.IndexOf("://", StringComparison.Ordinal);
        if (schemeEnd >= 0) s = s[(schemeEnd + 3)..];
        var slash = s.IndexOf('/');
        if (slash >= 0) s = s[..slash];
        return s;
    }

    /// <summary>
    /// Same bar for every writer — the report endpoint and the admin form alike. Length-capped
    /// because SQLite does not enforce the EF max length, and dot-required because every real
    /// subject is a hostname; without this, junk subjects self-registered without limit.
    /// </summary>
    public static bool IsValidSubject(string normalized) =>
        normalized.Length >= 3 && normalized.Length <= 200 && normalized.Contains('.');
}

/// <summary>Bound from configuration section "Monitor".</summary>
public sealed class ServerMonitorOptions
{
    /// <summary>Down-time before a kritiek subject's outage is announced.</summary>
    public int CriticalThresholdMinutes { get; set; } = 5;

    /// <summary>Down-time before everything else's outage is announced.</summary>
    public int NormalThresholdMinutes { get; set; } = 30;

    /// <summary>
    /// How long a subject may go unreported before the monitor itself is the problem worth
    /// announcing. Reporters are expected to POST every ~5 minutes regardless of status; the
    /// default gives them an hour of slack for restarts and deploys.
    /// </summary>
    public int SilenceAfterMinutes { get; set; } = 60;

    /// <summary>
    /// Domains seeded as kritiek. Only ever CREATES missing rows — a tier changed at runtime
    /// via the API is never overwritten by a restart.
    /// </summary>
    public List<string> CriticalSubjects { get; set; } = new();

    /// <summary>Who the alerts go to. Routed through the guardrail under category "serverdown".</summary>
    public string Recipient { get; set; } = "31633984381";
}
