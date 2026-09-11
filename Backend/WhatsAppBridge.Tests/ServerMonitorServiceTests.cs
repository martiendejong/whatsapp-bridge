using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using WhatsAppBridge.API.Data;
using WhatsAppBridge.API.Services;
using Xunit;

namespace WhatsAppBridge.Tests;

/// <summary>
/// The monitor policy Martien specified on 2026-09-10:
///
///   kritieke servers  -> melding na 5 minuten offline
///   al het andere     -> melding na 30 minuten
///   blip              -> niets
///   één alert per storing, herstelmelding alleen na een verstuurde alert
///   stille monitor    -> zelf een alert
///
/// Every test injects its own clock — the previous generation of this logic lived in
/// PowerShell watchdogs and was tested by waiting for the next outage.
/// </summary>
public class ServerMonitorServiceTests
{
    private const string Pog = "portofgiethoorn.com";        // seeded kritiek via config below
    private const string Blog = "blog.example.nl";           // unknown -> normaal

    private static readonly DateTime T0 = new(2026, 9, 11, 3, 0, 0, DateTimeKind.Utc);

    private static AppDbContext NewContext() =>
        new(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static ServerMonitorService NewService(AppDbContext db)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Monitor:CriticalThresholdMinutes"] = "5",
            ["Monitor:NormalThresholdMinutes"] = "30",
            ["Monitor:SilenceAfterMinutes"] = "60",
            ["Monitor:CriticalSubjects:0"] = Pog,
        }).Build();
        return new ServerMonitorService(db, config, NullLogger<ServerMonitorService>.Instance);
    }

    // ─── The threshold ───────────────────────────────────────────────────────────────────────

    /// <summary>The first "down" is a clock start, never a message.</summary>
    [Fact]
    public async Task The_first_down_report_records_and_says_nothing()
    {
        using var db = NewContext();

        var outcome = await NewService(db).ReportAsync(Pog, "down", "HTTP 502", T0);

        Assert.Null(outcome.Alert);
        Assert.Equal("recorded", outcome.Disposition);
    }

    [Fact]
    public async Task A_critical_subject_alerts_after_five_minutes_down()
    {
        using var db = NewContext();
        var svc = NewService(db);

        await svc.ReportAsync(Pog, "down", "HTTP 502", T0);
        var atFour = await svc.ReportAsync(Pog, "down", "HTTP 502", T0.AddMinutes(4));
        var atFive = await svc.ReportAsync(Pog, "down", "HTTP 502", T0.AddMinutes(5));

        Assert.Null(atFour.Alert);
        Assert.Equal("waiting", atFour.Disposition);
        Assert.NotNull(atFive.Alert);
        Assert.Equal("down", atFive.Alert!.Kind);
        Assert.Contains(Pog, atFive.Alert.Text);
        Assert.Contains("kritiek", atFive.Alert.Text);
        Assert.Contains("HTTP 502", atFive.Alert.Text);
    }

    /// <summary>
    /// Unknown subjects are non-production by definition (Martien, 2026-09-10), so they get
    /// the slow tier rather than a rejection: self-registered, normaal, 30 minutes.
    /// </summary>
    [Fact]
    public async Task An_unknown_subject_self_registers_as_normaal_with_the_slow_threshold()
    {
        using var db = NewContext();
        var svc = NewService(db);

        await svc.ReportAsync(Blog, "down", null, T0);
        var atTen = await svc.ReportAsync(Blog, "down", null, T0.AddMinutes(10));
        var atThirty = await svc.ReportAsync(Blog, "down", null, T0.AddMinutes(30));

        Assert.Null(atTen.Alert);
        Assert.False(atThirty.Critical);
        Assert.NotNull(atThirty.Alert);
        Assert.Contains("normaal", atThirty.Alert!.Text);
    }

    // ─── One alert per outage ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task An_announced_outage_is_announced_exactly_once()
    {
        using var db = NewContext();
        var svc = NewService(db);

        await svc.ReportAsync(Pog, "down", null, T0);
        var first = await svc.ReportAsync(Pog, "down", null, T0.AddMinutes(6));
        var again = await svc.ReportAsync(Pog, "down", null, T0.AddMinutes(11));
        var stillDown = await svc.ReportAsync(Pog, "down", null, T0.AddHours(3));

        Assert.NotNull(first.Alert);
        Assert.Null(again.Alert);
        Assert.Null(stillDown.Alert);
        Assert.Equal("already-alerted", stillDown.Disposition);
    }

    // ─── Recovery and blips ──────────────────────────────────────────────────────────────────

    /// <summary>Recovery is only news when the outage was news.</summary>
    [Fact]
    public async Task Recovery_after_an_announced_outage_reports_the_duration()
    {
        using var db = NewContext();
        var svc = NewService(db);

        await svc.ReportAsync(Pog, "down", null, T0);
        await svc.ReportAsync(Pog, "down", null, T0.AddMinutes(6));      // alert
        var up = await svc.ReportAsync(Pog, "up", null, T0.AddMinutes(40));

        Assert.NotNull(up.Alert);
        Assert.Equal("recovered", up.Alert!.Kind);
        Assert.Contains("40 min", up.Alert.Text);
    }

    /// <summary>
    /// The whole point of the threshold: a server that dips for three minutes at 03:00 and
    /// comes back was never worth waking anyone for — not on the way down, not on the way up.
    /// </summary>
    [Fact]
    public async Task A_blip_that_recovers_before_the_threshold_produces_no_message_at_all()
    {
        using var db = NewContext();
        var svc = NewService(db);

        await svc.ReportAsync(Pog, "down", null, T0);
        var up = await svc.ReportAsync(Pog, "up", null, T0.AddMinutes(3));

        Assert.Null(up.Alert);
        Assert.Equal("blip", up.Disposition);
    }

    /// <summary>A new outage after a recovery is a new story with its own clock and alert.</summary>
    [Fact]
    public async Task A_second_outage_after_recovery_alerts_again()
    {
        using var db = NewContext();
        var svc = NewService(db);

        await svc.ReportAsync(Pog, "down", null, T0);
        await svc.ReportAsync(Pog, "down", null, T0.AddMinutes(6));      // alert #1
        await svc.ReportAsync(Pog, "up", null, T0.AddMinutes(10));       // recovery
        await svc.ReportAsync(Pog, "down", null, T0.AddMinutes(20));     // new clock
        var atFour = await svc.ReportAsync(Pog, "down", null, T0.AddMinutes(24));
        var second = await svc.ReportAsync(Pog, "down", null, T0.AddMinutes(26));

        Assert.Null(atFour.Alert);       // new episode waits its own five minutes
        Assert.NotNull(second.Alert);
    }

    // ─── The sweep: dead reporters and silent monitors ───────────────────────────────────────

    /// <summary>
    /// The reporter died right after the first "down". No further report will ever arrive, so
    /// only the sweep can fire the threshold alert — being blind at exactly that moment is the
    /// failure mode this feature exists to close.
    /// </summary>
    [Fact]
    public async Task The_sweep_fires_the_outage_alert_when_the_reporter_died_mid_outage()
    {
        using var db = NewContext();
        var svc = NewService(db);

        await svc.ReportAsync(Pog, "down", "HTTP 502", T0);

        var atFour = await svc.SweepAsync(T0.AddMinutes(4));
        var atSix = await svc.SweepAsync(T0.AddMinutes(6));
        var atSeven = await svc.SweepAsync(T0.AddMinutes(7));

        Assert.Empty(atFour);
        var alert = Assert.Single(atSix);
        Assert.Equal("down", alert.Kind);
        Assert.Empty(atSeven);           // still one alert per outage, sweep included
    }

    [Fact]
    public async Task A_silent_monitor_is_itself_announced_once()
    {
        using var db = NewContext();
        var svc = NewService(db);

        await svc.ReportAsync(Pog, "up", null, T0);

        var atHalfHour = await svc.SweepAsync(T0.AddMinutes(30));
        var atHour = await svc.SweepAsync(T0.AddMinutes(61));
        var later = await svc.SweepAsync(T0.AddMinutes(90));

        Assert.Empty(atHalfHour);
        var alert = Assert.Single(atHour);
        Assert.Equal("monitor-silent", alert.Kind);
        Assert.Contains(Pog, alert.Text);
        Assert.Empty(later);             // once per silence, not every sweep
    }

    /// <summary>The silence story gets its explicit all-clear when reports resume.</summary>
    [Fact]
    public async Task A_monitor_that_resumes_after_a_silence_alert_closes_the_loop()
    {
        using var db = NewContext();
        var svc = NewService(db);

        await svc.ReportAsync(Pog, "up", null, T0);
        await svc.SweepAsync(T0.AddMinutes(61));                        // silence alert
        var resumed = await svc.ReportAsync(Pog, "up", null, T0.AddMinutes(75));

        Assert.NotNull(resumed.Alert);
        Assert.Equal("monitor-resumed", resumed.Alert!.Kind);

        // And the next silence is a new story.
        var silentAgain = await svc.SweepAsync(T0.AddMinutes(75 + 61));
        Assert.Single(silentAgain);
    }

    /// <summary>
    /// A subject an admin pre-registered whose monitor was never built must not page anyone
    /// about "silence" — there was never a first report to be silent after.
    /// </summary>
    [Fact]
    public async Task A_subject_that_never_reported_is_not_silent_it_is_unborn()
    {
        using var db = NewContext();
        db.MonitorSubjects.Add(new WhatsAppBridge.API.Models.MonitorSubject
        {
            Subject = "nieuw.example.nl", Status = "up", CreatedAtUtc = T0, UpdatedAtUtc = T0,
        });
        await db.SaveChangesAsync();

        var alerts = await NewService(db).SweepAsync(T0.AddDays(7));

        Assert.Empty(alerts);
    }

    // ─── State visibility ────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task The_outcome_reports_tier_and_threshold_for_the_caller()
    {
        using var db = NewContext();
        var svc = NewService(db);

        var pog = await svc.ReportAsync(Pog, "up", null, T0);
        var blog = await svc.ReportAsync(Blog, "up", null, T0);

        Assert.True(pog.Critical);
        Assert.Equal(5, pog.ThresholdMinutes);
        Assert.False(blog.Critical);
        Assert.Equal(30, blog.ThresholdMinutes);
    }
}
