namespace WhatsAppBridge.API.Models;

/// <summary>
/// One monitored thing — a production domain, usually — and the bridge's current belief about
/// it. This is the state behind POST /api/wa/monitor (2026-09-10, Martien): monitors FEED the
/// bridge status reports, and the bridge decides what is worth a WhatsApp message. That
/// decision used to live in every monitoring script separately, which is how the same outage
/// once produced three alerts and a different outage produced none.
///
/// The rules the state expresses:
///   - a subject must be down PAST ITS THRESHOLD before anyone is messaged (kritiek 5 min,
///     normaal 30 min) — a blip that recovers in between was never worth waking anyone for;
///   - one alert per outage, however long it lasts and however often the monitor repeats
///     "down";
///   - recovery is only news if the outage was announced ("weer online na 40 min");
///   - a monitor that goes SILENT is itself an alert, because "no news" from a dead watchdog
///     looks identical to "all fine" from a live one — that difference has cost real outage
///     visibility before (the Bugatti watchdog incident, task 856).
///
/// Subjects self-register on their first report, as "normaal". Unknown subjects are therefore
/// not rejected — Martien's rule of 2026-09-10: unknown means non-production, which means the
/// slow tier, not the door.
/// </summary>
public class MonitorSubject
{
    public long Id { get; set; }

    /// <summary>Lowercased, trimmed identity — "portofgiethoorn.com". Unique.</summary>
    public string Subject { get; set; } = string.Empty;

    /// <summary>
    /// Kritiek gets the 5-minute threshold, everything else the 30-minute one. The four
    /// production domains are seeded kritiek from configuration; the tier is editable at
    /// runtime via the API, so promoting a new production domain needs no deploy.
    /// </summary>
    public bool Critical { get; set; }

    /// <summary>"up" or "down" — the last reported state.</summary>
    public string Status { get; set; } = "up";

    /// <summary>Last reported detail ("HTTP 502"), shown in the alert.</summary>
    public string? LastDetail { get; set; }

    /// <summary>When the current outage began. Null while up.</summary>
    public DateTime? FirstDownAtUtc { get; set; }

    /// <summary>
    /// When the alert for the current outage went out. Null = not (yet) alerted. This is what
    /// makes it one alert per outage, and what decides whether recovery is worth a message.
    /// </summary>
    public DateTime? DownAlertAtUtc { get; set; }

    /// <summary>
    /// When the "your monitor has gone quiet" alert went out. Null = the monitor is reporting
    /// (or its silence has not yet crossed the line). Reset when reports resume.
    /// </summary>
    public DateTime? SilenceAlertAtUtc { get; set; }

    public DateTime LastReportAtUtc { get; set; }

    public DateTime CreatedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
}
