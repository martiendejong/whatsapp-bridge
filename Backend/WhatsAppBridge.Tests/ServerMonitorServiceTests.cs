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

    // ─── Silence × outage: the cells where two stories collide ──────────────────────────────

    /// <summary>
    /// The monitor resumes WITH bad news. One message, and the outage wins the slot — it is
    /// the actionable one. Two messages here would be the cry-wolf mode for a paging system.
    /// </summary>
    [Fact]
    public async Task A_monitor_resuming_with_a_threshold_crossed_outage_sends_one_alert_the_outage()
    {
        using var db = NewContext();
        var svc = NewService(db);

        await svc.ReportAsync(Pog, "down", null, T0);                    // clock starts
        await svc.SweepAsync(T0.AddMinutes(6));                          // outage announced by sweep
        await svc.SweepAsync(T0.AddMinutes(70));                         // silence announced
        var resumed = await svc.ReportAsync(Pog, "up", null, T0.AddMinutes(80));

        Assert.NotNull(resumed.Alert);
        Assert.Equal("recovered", resumed.Alert!.Kind);                  // not "monitor-resumed"
    }

    /// <summary>Resume with still-down status before the threshold: the resume message carries the status.</summary>
    [Fact]
    public async Task A_monitor_resuming_while_down_but_unannounced_reports_the_resume_with_status()
    {
        using var db = NewContext();
        var svc = NewService(db);

        await svc.ReportAsync(Pog, "up", null, T0);
        await svc.SweepAsync(T0.AddMinutes(61));                         // silence announced
        var resumed = await svc.ReportAsync(Pog, "down", "HTTP 502", T0.AddMinutes(70));

        Assert.NotNull(resumed.Alert);
        Assert.Equal("monitor-resumed", resumed.Alert!.Kind);
        Assert.Contains("down", resumed.Alert.Text);
    }

    // ─── Claim release: transient dispatch failure must not eat the alert ────────────────────

    /// <summary>
    /// The WhatsApp session is down at the threshold minute — strongly correlated with real
    /// incidents. The dispatcher reports a transient failure, the claim is released, and the
    /// next sweep decides (and fires) again. Without the release, the state machine believed
    /// the alert was announced and the outage stayed invisible until the recovery message
    /// arrived for a story nobody had heard.
    /// </summary>
    [Fact]
    public async Task A_released_claim_makes_the_next_sweep_fire_the_alert_again()
    {
        using var db = NewContext();
        var svc = NewService(db);

        await svc.ReportAsync(Pog, "down", null, T0);
        var first = Assert.Single(await svc.SweepAsync(T0.AddMinutes(6)));
        Assert.Equal("down", first.Kind);

        await svc.ReleaseAlertClaimAsync(first);                         // dispatch failed transiently

        var retry = Assert.Single(await svc.SweepAsync(T0.AddMinutes(7)));
        Assert.Equal("down", retry.Kind);

        // And once it sticks (no release), it stays one-per-outage.
        Assert.Empty(await svc.SweepAsync(T0.AddMinutes(8)));
    }

    [Fact]
    public async Task A_released_silence_claim_is_retried_too()
    {
        using var db = NewContext();
        var svc = NewService(db);

        await svc.ReportAsync(Pog, "up", null, T0);
        var first = Assert.Single(await svc.SweepAsync(T0.AddMinutes(61)));
        Assert.Equal("monitor-silent", first.Kind);

        await svc.ReleaseAlertClaimAsync(first);

        Assert.Single(await svc.SweepAsync(T0.AddMinutes(62)));
    }

    // ─── Dedupe safety: texts must be unique per episode ─────────────────────────────────────

    /// <summary>
    /// The guardrail suppresses identical bodies within ten minutes. A server that flaps twice
    /// inside that window must therefore produce DIFFERENT alert texts, or the second outage's
    /// announcement is swallowed as a duplicate of the first.
    /// </summary>
    [Fact]
    public async Task Two_outages_of_the_same_subject_produce_distinct_alert_texts()
    {
        using var db = NewContext();
        var svc = NewService(db);

        await svc.ReportAsync(Pog, "down", "HTTP 502", T0);
        var first = await svc.ReportAsync(Pog, "down", "HTTP 502", T0.AddMinutes(5));
        await svc.ReportAsync(Pog, "up", null, T0.AddMinutes(6));
        await svc.ReportAsync(Pog, "down", "HTTP 502", T0.AddMinutes(7));
        var second = await svc.ReportAsync(Pog, "down", "HTTP 502", T0.AddMinutes(12));

        Assert.NotNull(first.Alert);
        Assert.NotNull(second.Alert);
        Assert.NotEqual(first.Alert!.Text, second.Alert!.Text);
    }

    // ─── Subject identity ────────────────────────────────────────────────────────────────────

    /// <summary>
    /// A monitor reporting its full probe URL must land on the same subject as one reporting
    /// the bare hostname — a run id in the query string used to mint a fresh subject every
    /// five minutes, each earning its own silence alert an hour later.
    /// </summary>
    [Theory]
    [InlineData("https://portofgiethoorn.com/health?run=1234")]
    [InlineData("http://PortOfGiethoorn.com/status")]
    [InlineData("  portofgiethoorn.com  ")]
    public void A_probe_url_normalizes_to_its_hostname(string reported)
    {
        Assert.Equal("portofgiethoorn.com", ServerMonitorService.Normalize(reported));
    }

    [Theory]
    [InlineData("portofgiethoorn.com", true)]
    [InlineData("app.bugattiinsights.com", true)]
    [InlineData("vps1", false)]           // no dot: not a hostname
    [InlineData("x.", false)]             // has a dot but too short to be real
    [InlineData("", false)]
    public void Subject_validation_requires_a_plausible_hostname(string subject, bool expected)
    {
        Assert.Equal(expected, ServerMonitorService.IsValidSubject(ServerMonitorService.Normalize(subject)));
    }

    // ─── The version race ────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Report and sweep run in separate scopes and both act at exactly the threshold minute —
    /// both read "not yet alerted", both decide to alert, and without the concurrency token
    /// both saves succeeded and Martien was paged twice. The token makes the stale save lose:
    /// exactly one alert survives and the loser reports "conflict".
    ///
    /// The stale read is arranged through the identity map: the report context has the row
    /// tracked from before the sweep's save, so its ReportAsync acts on pre-sweep state —
    /// which is precisely what an in-flight request scope holds during the real race.
    /// </summary>
    [Fact]
    public async Task When_report_and_sweep_race_at_the_threshold_exactly_one_alert_survives()
    {
        var storeName = Guid.NewGuid().ToString();
        AppDbContext Ctx() => new(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(storeName).Options);

        using (var setup = Ctx())
            await NewService(setup).ReportAsync(Pog, "down", null, T0);

        using var reportCtx = Ctx();
        using var sweepCtx = Ctx();
        var reportSvc = NewService(reportCtx);

        // The report scope reads the subject first (still unalerted)...
        _ = await reportCtx.MonitorSubjects.FirstAsync(s => s.Subject == Pog);
        // ...then the sweep claims the alert and saves...
        var sweepAlerts = await NewService(sweepCtx).SweepAsync(T0.AddMinutes(6));
        // ...and the report, still holding pre-sweep state, tries to claim it too.
        var report = await reportSvc.ReportAsync(Pog, "down", null, T0.AddMinutes(6));

        Assert.Single(sweepAlerts);
        Assert.Null(report.Alert);
        Assert.Equal("conflict", report.Disposition);
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
