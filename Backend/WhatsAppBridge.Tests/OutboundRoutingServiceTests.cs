using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using WhatsAppBridge.API.Data;
using WhatsAppBridge.API.Models;
using WhatsAppBridge.API.Services;
using Xunit;

namespace WhatsAppBridge.Tests;

/// <summary>
/// The routing policy Martien specified on 2026-09-10:
///
///   approval        -> Martien, always
///   deploy:valsuani -> Sjoerd 10:00-24:00 Europe/Amsterdam, outside that window Martien
///   serverdown      -> Martien, always
///   reply           -> whoever asked, exempt from routing entirely
///   everything else -> Martien
///
/// The failure this guards against is concrete: a deploy notice waking Sjoerd at 03:00.
/// Same InMemory-per-test convention as the other suites here.
/// </summary>
public class OutboundRoutingServiceTests
{
    private const string Martien = "31633984381";
    private const string Sjoerd = "31621484793";
    private const string Frank = "254715438010";

    private static AppDbContext NewContext() =>
        new(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    /// <summary>
    /// Routing only governs sends when OutboundRouting:Enabled is true, which it deliberately is
    /// not by default. Every test below is about what happens once it is on, so it is on here.
    /// The off case has its own tests at the bottom.
    /// </summary>
    private static IConfiguration Config(bool enabled = true) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["OutboundRouting:Enabled"] = enabled ? "true" : "false",
            })
            .Build();

    private static OutboundRoutingService NewService(AppDbContext db, bool enabled = true) =>
        new(db, NullLogger<OutboundRoutingService>.Instance, Config(enabled));

    /// <summary>Seeds the real policy, so these tests fail if the shipped defaults drift.</summary>
    private static async Task<AppDbContext> SeededAsync()
    {
        var db = NewContext();
        db.OutboundContacts.AddRange(
            new OutboundContact
            {
                Phone = Martien, Name = "Martien", Alias = "martien",
                TimeZoneId = "Europe/Amsterdam", WindowStartHour = 0, WindowEndHour = 24,
                Categories = "*",
            },
            new OutboundContact
            {
                Phone = Sjoerd, Name = "Sjoerd", Alias = "sjoerd",
                TimeZoneId = "Europe/Amsterdam", WindowStartHour = 10, WindowEndHour = 24,
                Categories = "deploy:valsuani", FallbackPhone = Martien,
            });
        await db.SaveChangesAsync();
        return db;
    }

    /// <summary>13:00 Amsterdam in September (CEST, UTC+2) — squarely inside Sjoerd's window.</summary>
    private static readonly DateTime Midday = new(2026, 9, 10, 11, 0, 0, DateTimeKind.Utc);

    /// <summary>03:00 Amsterdam — Sjoerd is asleep. The whole point of this feature.</summary>
    private static readonly DateTime DeadOfNight = new(2026, 9, 10, 1, 0, 0, DateTimeKind.Utc);

    // ─── Martien ─────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("approval")]
    [InlineData("serverdown")]
    [InlineData("deploy:valsuani")]
    [InlineData("other")]
    [InlineData(null)]
    public async Task Martien_receives_every_category_at_any_hour(string? category)
    {
        using var db = await SeededAsync();

        var day = await NewService(db).RouteAsync(Martien, category, "x", Midday);
        var night = await NewService(db).RouteAsync(Martien, category, "x", DeadOfNight);

        Assert.Equal(OutboundRoutingService.RoutingOutcome.Allowed, day.Outcome);
        Assert.Equal(OutboundRoutingService.RoutingOutcome.Allowed, night.Outcome);
        Assert.Equal(Martien, night.Recipient);
    }

    [Fact]
    public async Task An_alias_resolves_to_the_number()
    {
        using var db = await SeededAsync();

        var decision = await NewService(db).RouteAsync("martien", "approval", "x", Midday);

        Assert.Equal(Martien, decision.Recipient);
    }

    // ─── Sjoerd: the window ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Sjoerd_gets_a_valsuani_deploy_during_the_day()
    {
        using var db = await SeededAsync();

        var decision = await NewService(db).RouteAsync(Sjoerd, "deploy:valsuani", "deploy ok", Midday);

        Assert.Equal(OutboundRoutingService.RoutingOutcome.Allowed, decision.Outcome);
        Assert.Equal(Sjoerd, decision.Recipient);
    }

    [Fact]
    public async Task A_deploy_at_three_in_the_morning_goes_to_Martien_instead()
    {
        using var db = await SeededAsync();

        var decision = await NewService(db).RouteAsync(Sjoerd, "deploy:valsuani", "deploy ok", DeadOfNight);

        Assert.Equal(OutboundRoutingService.RoutingOutcome.Redirected, decision.Outcome);
        Assert.Equal(Martien, decision.Recipient);
        Assert.Contains("outside", decision.Reason, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 09:59 vs 10:00 Amsterdam. Off-by-one on a window boundary is exactly the bug that would
    /// go unnoticed for months and then wake someone once.
    /// </summary>
    [Theory]
    [InlineData(7, 59, OutboundRoutingService.RoutingOutcome.Redirected)]   // 09:59 CEST
    [InlineData(8, 0, OutboundRoutingService.RoutingOutcome.Allowed)]       // 10:00 CEST
    [InlineData(21, 59, OutboundRoutingService.RoutingOutcome.Allowed)]     // 23:59 CEST
    [InlineData(22, 0, OutboundRoutingService.RoutingOutcome.Redirected)]   // 00:00 CEST
    public async Task The_window_boundary_is_exact(int utcHour, int utcMinute,
        OutboundRoutingService.RoutingOutcome expected)
    {
        using var db = await SeededAsync();
        var when = new DateTime(2026, 9, 10, utcHour, utcMinute, 0, DateTimeKind.Utc);

        var decision = await NewService(db).RouteAsync(Sjoerd, "deploy:valsuani", "x", when);

        Assert.Equal(expected, decision.Outcome);
    }

    /// <summary>
    /// The window is Amsterdam time, not server time. In January the same clock hour is a
    /// different UTC hour, and a service that quietly used UTC would be an hour wrong for half
    /// the year without anyone noticing until spring.
    /// </summary>
    [Fact]
    public async Task The_window_follows_daylight_saving_in_the_contacts_own_zone()
    {
        using var db = await SeededAsync();

        // 09:30 UTC = 10:30 Amsterdam in January (CET, UTC+1) — inside the window.
        var winter = await NewService(db).RouteAsync(
            Sjoerd, "deploy:valsuani", "x", new DateTime(2026, 1, 15, 9, 30, 0, DateTimeKind.Utc));
        // Same UTC instant in July = 11:30 Amsterdam (CEST) — also inside, but for a different reason.
        var summer = await NewService(db).RouteAsync(
            Sjoerd, "deploy:valsuani", "x", new DateTime(2026, 7, 15, 9, 30, 0, DateTimeKind.Utc));
        // 08:30 UTC in January = 09:30 Amsterdam — still too early.
        var winterEarly = await NewService(db).RouteAsync(
            Sjoerd, "deploy:valsuani", "x", new DateTime(2026, 1, 15, 8, 30, 0, DateTimeKind.Utc));

        Assert.Equal(OutboundRoutingService.RoutingOutcome.Allowed, winter.Outcome);
        Assert.Equal(OutboundRoutingService.RoutingOutcome.Allowed, summer.Outcome);
        Assert.Equal(OutboundRoutingService.RoutingOutcome.Redirected, winterEarly.Outcome);
    }

    // ─── Sjoerd: the category ────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("approval")]
    [InlineData("serverdown")]
    [InlineData("other")]
    [InlineData("deploy:bugatti")]
    public async Task Sjoerd_gets_nothing_but_valsuani_deploys_even_at_midday(string category)
    {
        using var db = await SeededAsync();

        var decision = await NewService(db).RouteAsync(Sjoerd, category, "x", Midday);

        Assert.Equal(OutboundRoutingService.RoutingOutcome.Blocked, decision.Outcome);
    }

    /// <summary>
    /// A contact listing the parent "deploy" opts into the whole family; one listing the
    /// specific "deploy:valsuani" does not thereby receive sibling deploys.
    /// </summary>
    [Theory]
    [InlineData("deploy", "deploy:valsuani", true)]
    [InlineData("deploy", "deploy", true)]
    [InlineData("deploy:valsuani", "deploy:bugatti", false)]
    [InlineData("deploy:valsuani", "deploy", false)]
    [InlineData("approval,serverdown", "serverdown", true)]
    [InlineData("*", "anything:at:all", true)]
    public void Category_matching_is_hierarchical_on_the_colon(string listed, string requested, bool expected)
    {
        var contact = new OutboundContact { Categories = listed };

        Assert.Equal(expected, OutboundRoutingService.AcceptsCategory(contact, requested));
    }

    // ─── Everyone else ───────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_team_member_without_a_contact_row_is_never_messaged()
    {
        using var db = await SeededAsync();

        var decision = await NewService(db).RouteAsync(Frank, "serverdown", "site down", Midday);

        Assert.Equal(OutboundRoutingService.RoutingOutcome.Blocked, decision.Outcome);
        Assert.Contains("No routing contact", decision.Reason);
    }

    [Fact]
    public async Task A_muted_contact_falls_back_rather_than_vanishing()
    {
        using var db = await SeededAsync();
        var sjoerd = await db.OutboundContacts.FirstAsync(c => c.Phone == Sjoerd);
        sjoerd.Enabled = false;
        await db.SaveChangesAsync();

        var decision = await NewService(db).RouteAsync(Sjoerd, "deploy:valsuani", "x", Midday);

        Assert.Equal(OutboundRoutingService.RoutingOutcome.Redirected, decision.Outcome);
        Assert.Equal(Martien, decision.Recipient);
    }

    // ─── The fallback is a contact, not an escape hatch ──────────────────────────────────────

    /// <summary>
    /// The redirect used to hand the message to whatever number sat in FallbackPhone without
    /// checking it against anything. That made the field a hole straight through the policy:
    /// anyone able to edit a contact could route production alerts to a number that appears in no
    /// routing table, and the feature's one promise — numbers not in this list are never
    /// auto-messaged — would have been false.
    /// </summary>
    [Fact]
    public async Task A_fallback_that_is_not_itself_a_contact_blocks_rather_than_delivering()
    {
        using var db = await SeededAsync();
        var sjoerd = await db.OutboundContacts.FirstAsync(c => c.Phone == Sjoerd);
        sjoerd.FallbackPhone = Frank;                 // Frank has no contact row
        await db.SaveChangesAsync();

        var decision = await NewService(db).RouteAsync(Sjoerd, "deploy:valsuani", "x", DeadOfNight);

        Assert.Equal(OutboundRoutingService.RoutingOutcome.Blocked, decision.Outcome);
        Assert.DoesNotContain(Frank, decision.Recipient ?? string.Empty);
    }

    /// <summary>
    /// A redirect must satisfy the fallback's own policy, not merely exist. Sending Sjoerd's
    /// night-time deploy notice to someone who does not accept deploys would deliver it to a
    /// person who explicitly opted out.
    /// </summary>
    [Fact]
    public async Task A_fallback_that_refuses_this_category_does_not_receive_it_anyway()
    {
        using var db = await SeededAsync();
        var martien = await db.OutboundContacts.FirstAsync(c => c.Phone == Martien);
        martien.Categories = "approval";              // no longer accepts deploys
        await db.SaveChangesAsync();

        var decision = await NewService(db).RouteAsync(Sjoerd, "deploy:valsuani", "x", DeadOfNight);

        Assert.Equal(OutboundRoutingService.RoutingOutcome.Blocked, decision.Outcome);
    }

    /// <summary>The fallback's window is the fallback's own, not an inherited exemption.</summary>
    [Fact]
    public async Task A_fallback_who_is_also_asleep_is_not_woken_by_the_redirect()
    {
        using var db = await SeededAsync();
        var martien = await db.OutboundContacts.FirstAsync(c => c.Phone == Martien);
        martien.WindowStartHour = 9;
        martien.WindowEndHour = 17;
        await db.SaveChangesAsync();

        var decision = await NewService(db).RouteAsync(Sjoerd, "deploy:valsuani", "x", DeadOfNight);

        Assert.Equal(OutboundRoutingService.RoutingOutcome.Blocked, decision.Outcome);
    }

    /// <summary>A muted fallback is muted. It must not be revived by being someone's backstop.</summary>
    [Fact]
    public async Task A_muted_fallback_does_not_receive_the_redirect()
    {
        using var db = await SeededAsync();
        var martien = await db.OutboundContacts.FirstAsync(c => c.Phone == Martien);
        martien.Enabled = false;
        await db.SaveChangesAsync();

        var decision = await NewService(db).RouteAsync(Sjoerd, "deploy:valsuani", "x", DeadOfNight);

        Assert.Equal(OutboundRoutingService.RoutingOutcome.Blocked, decision.Outcome);
    }

    /// <summary>
    /// Two contacts naming each other, or one naming itself, would otherwise recurse until the
    /// stack gave out. The redirect is one hop and then a decision.
    /// </summary>
    [Fact]
    public async Task A_fallback_pointing_back_at_itself_terminates()
    {
        using var db = await SeededAsync();
        var sjoerd = await db.OutboundContacts.FirstAsync(c => c.Phone == Sjoerd);
        sjoerd.FallbackPhone = Sjoerd;
        await db.SaveChangesAsync();

        var decision = await NewService(db).RouteAsync(Sjoerd, "deploy:valsuani", "x", DeadOfNight);

        Assert.Equal(OutboundRoutingService.RoutingOutcome.Blocked, decision.Outcome);
    }

    // ─── Alias resolution ────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Lookup used to branch: if the input contained a digit it was treated as a number and the
    /// alias table was never consulted. Any alias with a digit in it — "sjoerd2", "vps1" — was
    /// therefore unreachable, and resolved to "no routing contact" instead. Number first, then
    /// alias, always both.
    /// </summary>
    [Fact]
    public async Task An_alias_containing_a_digit_still_resolves()
    {
        using var db = await SeededAsync();
        db.OutboundContacts.Add(new OutboundContact
        {
            Phone = Frank, Name = "Frank", Alias = "frank2",
            TimeZoneId = "Europe/Amsterdam", WindowStartHour = 0, WindowEndHour = 24,
            Categories = "*",
        });
        await db.SaveChangesAsync();

        var decision = await NewService(db).RouteAsync("frank2", "approval", "x", Midday);

        Assert.Equal(OutboundRoutingService.RoutingOutcome.Allowed, decision.Outcome);
        Assert.Equal(Frank, decision.Recipient);
    }

    [Fact]
    public async Task Without_a_fallback_an_out_of_window_message_is_dropped_not_rerouted()
    {
        using var db = await SeededAsync();
        var sjoerd = await db.OutboundContacts.FirstAsync(c => c.Phone == Sjoerd);
        sjoerd.FallbackPhone = null;
        await db.SaveChangesAsync();

        var decision = await NewService(db).RouteAsync(Sjoerd, "deploy:valsuani", "x", DeadOfNight);

        Assert.Equal(OutboundRoutingService.RoutingOutcome.Blocked, decision.Outcome);
    }

    // ─── Redirect dedupe ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Callers typically fan out to Sjoerd and Martien in the same breath. At night the Sjoerd
    /// leg redirects to Martien, who already has the message — he must not receive it twice.
    /// </summary>
    [Fact]
    public async Task A_redirect_is_suppressed_when_the_fallback_already_got_this_message()
    {
        using var db = await SeededAsync();
        var body = "Valsuani deploy afgerond, versie 2.1.4";

        var direct = new OutboundSendLog { Recipient = Martien, SentAtUtc = DeadOfNight.AddMinutes(-1) };
        OutboundRoutingService.Stamp(direct, "deploy:valsuani", body);
        db.OutboundSendLogs.Add(direct);
        await db.SaveChangesAsync();

        var decision = await NewService(db).RouteAsync(Sjoerd, "deploy:valsuani", body, DeadOfNight);

        Assert.Equal(OutboundRoutingService.RoutingOutcome.Suppressed, decision.Outcome);
        Assert.False(decision.ShouldSend);
    }

    [Fact]
    public async Task A_genuinely_different_alert_still_gets_through()
    {
        using var db = await SeededAsync();

        var earlier = new OutboundSendLog { Recipient = Martien, SentAtUtc = DeadOfNight.AddMinutes(-1) };
        OutboundRoutingService.Stamp(earlier, "deploy:valsuani", "Valsuani deploy afgerond, versie 2.1.4");
        db.OutboundSendLogs.Add(earlier);
        await db.SaveChangesAsync();

        var decision = await NewService(db).RouteAsync(
            Sjoerd, "deploy:valsuani", "Valsuani deploy MISLUKT, rollback gestart", DeadOfNight);

        Assert.Equal(OutboundRoutingService.RoutingOutcome.Redirected, decision.Outcome);
    }

    /// <summary>A repeat of the same alert an hour later is news again, not a duplicate.</summary>
    [Fact]
    public async Task The_dedupe_window_expires()
    {
        using var db = await SeededAsync();
        var body = "Valsuani deploy afgerond";

        var old = new OutboundSendLog { Recipient = Martien, SentAtUtc = DeadOfNight.AddHours(-1) };
        OutboundRoutingService.Stamp(old, "deploy:valsuani", body);
        db.OutboundSendLogs.Add(old);
        await db.SaveChangesAsync();

        var decision = await NewService(db).RouteAsync(Sjoerd, "deploy:valsuani", body, DeadOfNight);

        Assert.Equal(OutboundRoutingService.RoutingOutcome.Redirected, decision.Outcome);
    }

    // ─── Rollout safety ──────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The safety valve, and the reason this feature can be merged at all. Seeding the contacts
    /// is not the same as arming the policy: without the flag, a deploy that populates the table
    /// would start blocking every caller that has not yet learned to pass a category. Off is the
    /// default and off must mean off, however full the table is.
    /// </summary>
    [Fact]
    public async Task A_seeded_table_enforces_nothing_while_the_flag_is_off()
    {
        using var db = await SeededAsync();

        Assert.False(await NewService(db, enabled: false).IsActiveAsync());
    }

    /// <summary>
    /// The other half: turning the flag on before entering anyone would block every send, so an
    /// empty table also reads as inactive. "I armed it before adding contacts" must not take
    /// WhatsApp down.
    /// </summary>
    [Fact]
    public async Task An_empty_contact_table_is_inactive_even_with_the_flag_on()
    {
        using var db = NewContext();

        Assert.False(await NewService(db).IsActiveAsync());
    }

    [Fact]
    public async Task Routing_is_active_only_with_both_the_flag_and_contacts()
    {
        using var db = await SeededAsync();

        Assert.True(await NewService(db).IsActiveAsync());
    }

    // ─── Timezones ───────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The Kenya team is two hours ahead of Amsterdam. A single server-side window would be
    /// wrong for one of the two groups by construction, which is why the zone lives per contact.
    /// </summary>
    [Fact]
    public async Task Windows_are_evaluated_in_each_contacts_own_zone()
    {
        using var db = NewContext();
        db.OutboundContacts.Add(new OutboundContact
        {
            Phone = Frank, Name = "Frank", TimeZoneId = "Africa/Nairobi",
            WindowStartHour = 9, WindowEndHour = 18, Categories = "*",
        });
        await db.SaveChangesAsync();

        // 07:00 UTC = 10:00 Nairobi (open) but only 09:00 Amsterdam.
        var decision = await NewService(db).RouteAsync(
            Frank, "other", "x", new DateTime(2026, 9, 10, 7, 0, 0, DateTimeKind.Utc));
        // 16:00 UTC = 19:00 Nairobi — closed, though it is still 18:00 in Amsterdam.
        var evening = await NewService(db).RouteAsync(
            Frank, "other", "x", new DateTime(2026, 9, 10, 16, 0, 0, DateTimeKind.Utc));

        Assert.Equal(OutboundRoutingService.RoutingOutcome.Allowed, decision.Outcome);
        Assert.Equal(OutboundRoutingService.RoutingOutcome.Blocked, evening.Outcome);
    }

    /// <summary>A window whose end is before its start wraps midnight rather than being empty.</summary>
    [Theory]
    [InlineData(20, true)]    // 22:00 CEST — inside 22-6
    [InlineData(2, true)]     // 04:00 CEST — inside
    [InlineData(10, false)]   // 12:00 CEST — outside
    public void A_window_that_crosses_midnight_is_read_as_wrapping(int utcHour, bool expected)
    {
        var contact = new OutboundContact
        {
            TimeZoneId = "Europe/Amsterdam", WindowStartHour = 22, WindowEndHour = 6,
        };

        var inside = OutboundRoutingService.IsInsideWindow(
            contact, new DateTime(2026, 9, 10, utcHour, 0, 0, DateTimeKind.Utc), out _);

        Assert.Equal(expected, inside);
    }

    /// <summary>
    /// Start equal to end is the boundary between the two readings above, so it must not fall
    /// into the wrapping branch and become always-open. It is a zero-width window: closed at
    /// every hour, which is how an admin silences a contact without deleting the row.
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(9)]
    [InlineData(23)]
    public void A_window_whose_start_equals_its_end_is_closed_at_every_hour(int utcHour)
    {
        var contact = new OutboundContact
        {
            TimeZoneId = "Europe/Amsterdam", WindowStartHour = 10, WindowEndHour = 10,
        };

        Assert.False(OutboundRoutingService.IsInsideWindow(
            contact, new DateTime(2026, 9, 10, utcHour, 0, 0, DateTimeKind.Utc), out _));
    }
}
