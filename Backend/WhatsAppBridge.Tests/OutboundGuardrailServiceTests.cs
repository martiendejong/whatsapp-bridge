using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using WhatsAppBridge.API.Data;
using WhatsAppBridge.API.Services;
using Xunit;

namespace WhatsAppBridge.Tests;

/// <summary>
/// Task 1067: unit tests for the CoachOS service-route reply-window exception added to
/// OutboundGuardrailService, plus regression coverage for the pre-existing allow-list/rate-limit
/// behavior it must NOT weaken. Uses EF Core's InMemory provider (one fresh database per test via
/// a unique database name) instead of the repo's usual SQLite file, since this is the first test
/// project in the repo and no test-DB convention exists yet.
/// </summary>
public class OutboundGuardrailServiceTests
{
    private static AppDbContext NewContext()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new AppDbContext(options);
    }

    private static IConfiguration BuildConfig(Dictionary<string, string?> values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values).Build();

    private static OutboundGuardrailService NewService(
        AppDbContext db,
        bool enabled = true,
        string[]? allowList = null,
        int maxPerRecipientPer24h = 20,
        int maxGlobalPerHour = 10,
        bool replyRouteEnabled = false,
        int replyWindowHours = 24)
    {
        var values = new Dictionary<string, string?>
        {
            ["OutboundGuardrail:Enabled"] = enabled.ToString(),
            ["OutboundGuardrail:MaxPerRecipientPer24h"] = maxPerRecipientPer24h.ToString(),
            ["OutboundGuardrail:MaxGlobalPerHour"] = maxGlobalPerHour.ToString(),
            ["OutboundGuardrail:ReplyRouteEnabled"] = replyRouteEnabled.ToString(),
            ["OutboundGuardrail:ReplyWindowHours"] = replyWindowHours.ToString(),
        };
        var list = allowList ?? new[] { "31633984381" };
        for (var i = 0; i < list.Length; i++)
            values[$"OutboundGuardrail:AllowList:{i}"] = list[i];

        var config = BuildConfig(values);
        return new OutboundGuardrailService(config, db, NullLogger<OutboundGuardrailService>.Instance);
    }

    // ─── Regression: pre-existing allow-list / rate-limit behavior must be unchanged ──────────

    [Fact]
    public async Task AllowListedRecipient_IsAllowed()
    {
        using var db = NewContext();
        var svc = NewService(db, allowList: new[] { "31633984381" });

        var (allowed, reason) = await svc.CheckAsync("sendMessage", "31633984381", "hi", userId: 1);

        Assert.True(allowed);
        Assert.Null(reason);
    }

    [Fact]
    public async Task NonAllowListedRecipient_OnNormalEndpoint_IsBlocked_EvenWithReplyRouteEnabled()
    {
        using var db = NewContext();
        // Reply-route enabled, and this recipient even has a fresh inbound row — but the call
        // goes through the ordinary "sendMessage" endpoint, not CoachOsReplyEndpoint, so the
        // exception must not apply.
        var svc = NewService(db, replyRouteEnabled: true);
        await svc.RecordInboundContactAsync("31699999999");

        var (allowed, reason) = await svc.CheckAsync("sendMessage", "31699999999", "hi", userId: null);

        Assert.False(allowed);
        Assert.Contains("not on the outbound allow-list", reason);
    }

    [Fact]
    public async Task AllowListMatches_AcrossJidSuffixFormats()
    {
        using var db = NewContext();
        var svc = NewService(db, allowList: new[] { "31633984381" });

        Assert.True(svc.IsAllowListed("31633984381@c.us"));
        Assert.True(svc.IsAllowListed("31633984381:20@s.whatsapp.net"));
        Assert.True(svc.IsAllowListed("31633984381"));
        Assert.False(svc.IsAllowListed("31699999999@c.us"));
    }

    [Fact]
    public async Task RecipientVolumeCap_StillAppliesToAllowListedSender()
    {
        using var db = NewContext();
        var svc = NewService(db, maxPerRecipientPer24h: 2);

        Assert.True((await svc.CheckAsync("sendMessage", "31633984381", "1", null)).Allowed);
        Assert.True((await svc.CheckAsync("sendMessage", "31633984381", "2", null)).Allowed);
        var (allowed, reason) = await svc.CheckAsync("sendMessage", "31633984381", "3", null);

        Assert.False(allowed);
        Assert.Contains("volume cap reached", reason);
    }

    [Fact]
    public async Task GlobalVolumeCap_StillApplies()
    {
        using var db = NewContext();
        var svc = NewService(db, allowList: new[] { "111", "222" }, maxGlobalPerHour: 2, maxPerRecipientPer24h: 99);

        Assert.True((await svc.CheckAsync("sendMessage", "111", "1", null)).Allowed);
        Assert.True((await svc.CheckAsync("sendMessage", "222", "2", null)).Allowed);
        var (allowed, reason) = await svc.CheckAsync("sendMessage", "111", "3", null);

        Assert.False(allowed);
        Assert.Contains("global outbound volume cap reached", reason);
    }

    [Fact]
    public async Task GuardrailDisabled_AllowsEverything()
    {
        using var db = NewContext();
        var svc = NewService(db, enabled: false, allowList: Array.Empty<string>());

        var (allowed, _) = await svc.CheckAsync("sendMessage", "31699999999", "hi", null);

        Assert.True(allowed);
    }

    // ─── New: CoachOS reply-window exception ──────────────────────────────────────────────────

    [Fact]
    public async Task ReplyRoute_WithoutAnyInbound_IsBlocked()
    {
        using var db = NewContext();
        var svc = NewService(db, replyRouteEnabled: true);

        var (allowed, reason) = await svc.CheckAsync(
            OutboundGuardrailService.CoachOsReplyEndpoint, "31699999999", "AI reply", userId: null);

        Assert.False(allowed);
        Assert.Contains("reply window", reason);
    }

    [Fact]
    public async Task ReplyRoute_WithRecentInbound_IsAllowed()
    {
        using var db = NewContext();
        var svc = NewService(db, replyRouteEnabled: true, replyWindowHours: 24);
        await svc.RecordInboundContactAsync("31699999999@c.us"); // JID form, as it arrives from Dawa

        var (allowed, reason) = await svc.CheckAsync(
            OutboundGuardrailService.CoachOsReplyEndpoint, "31699999999", "AI reply", userId: null);

        Assert.True(allowed);
        Assert.Null(reason);
    }

    [Fact]
    public async Task ReplyRoute_WithInboundOlderThanWindow_IsBlocked()
    {
        using var db = NewContext();
        var svc = NewService(db, replyRouteEnabled: true, replyWindowHours: 24);
        // Simulate a stale inbound row directly (older than the 24h window).
        db.InboundContacts.Add(new WhatsAppBridge.API.Models.InboundContact
        {
            Sender = "31699999999",
            LastInboundAtUtc = DateTime.UtcNow.AddHours(-25),
        });
        await db.SaveChangesAsync();

        var (allowed, reason) = await svc.CheckAsync(
            OutboundGuardrailService.CoachOsReplyEndpoint, "31699999999", "AI reply", userId: null);

        Assert.False(allowed);
        Assert.Contains("reply window", reason);
    }

    [Fact]
    public async Task ReplyRoute_Disabled_BlocksEvenWithRecentInbound()
    {
        // ReplyRouteEnabled defaults to false — the guardrail's own independent opt-in must be
        // explicitly set even if a caller passes the CoachOsReplyEndpoint tag and a real inbound
        // row exists (e.g. CoachOsIntakeForwarder's own flag is on but the guardrail's isn't).
        using var db = NewContext();
        var svc = NewService(db, replyRouteEnabled: false);
        await svc.RecordInboundContactAsync("31699999999");

        var (allowed, reason) = await svc.CheckAsync(
            OutboundGuardrailService.CoachOsReplyEndpoint, "31699999999", "AI reply", userId: null);

        Assert.False(allowed);
        Assert.Contains("not on the outbound allow-list", reason);
    }

    [Fact]
    public async Task ReplyRoute_StillSubjectToPerRecipientVolumeCap()
    {
        using var db = NewContext();
        var svc = NewService(db, replyRouteEnabled: true, maxPerRecipientPer24h: 1);
        await svc.RecordInboundContactAsync("31699999999");

        var first = await svc.CheckAsync(OutboundGuardrailService.CoachOsReplyEndpoint, "31699999999", "1", null);
        var second = await svc.CheckAsync(OutboundGuardrailService.CoachOsReplyEndpoint, "31699999999", "2", null);

        Assert.True(first.Allowed);
        Assert.False(second.Allowed);
        Assert.Contains("volume cap reached", second.Reason);
    }

    [Fact]
    public async Task RecordInboundContact_Upserts_DoesNotDuplicateRows()
    {
        using var db = NewContext();
        var svc = NewService(db);

        await svc.RecordInboundContactAsync("31699999999@c.us");
        await svc.RecordInboundContactAsync("31699999999@s.whatsapp.net"); // same number, different JID suffix

        var count = await db.InboundContacts.CountAsync(c => c.Sender == "31699999999");
        Assert.Equal(1, count);
    }
}
