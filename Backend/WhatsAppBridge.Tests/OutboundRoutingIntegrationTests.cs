using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using WhatsAppBridge.API.Data;
using WhatsAppBridge.API.Models;
using WhatsAppBridge.API.Services;
using Xunit;

namespace WhatsAppBridge.Tests;

/// <summary>
/// Routing seen through OutboundGuardrailService, which is the only way real traffic reaches it.
/// OutboundRoutingServiceTests proves the policy is right; this proves it is actually wired in
/// — that the guardrail hands back the redirected number, that the contact table supersedes the
/// static allow-list, and that the volume caps still bite afterwards.
/// </summary>
public class OutboundRoutingIntegrationTests
{
    private const string Martien = "31633984381";
    private const string Sjoerd = "31621484793";

    private static AppDbContext NewContext() =>
        new(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    /// <summary>
    /// Note the allow-list: Martien only, exactly as shipped. Sjoerd is deliberately absent, so
    /// any test in which he receives something proves the contact table is what cleared him.
    /// </summary>
    private static OutboundGuardrailService NewService(AppDbContext db, int maxPerRecipientPer24h = 20)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["OutboundGuardrail:Enabled"] = "true",
            ["OutboundGuardrail:AllowList:0"] = Martien,
            ["OutboundGuardrail:MaxPerRecipientPer24h"] = maxPerRecipientPer24h.ToString(),
            ["OutboundGuardrail:MaxGlobalPerHour"] = "100",
            ["OutboundGuardrail:ReplyRouteEnabled"] = "true",
            ["OutboundGuardrail:ReplyWindowHours"] = "24",
        }).Build();

        var routing = new OutboundRoutingService(db, NullLogger<OutboundRoutingService>.Instance);
        return new OutboundGuardrailService(config, db, routing, NullLogger<OutboundGuardrailService>.Instance);
    }

    private static async Task SeedPolicyAsync(AppDbContext db, int sjoerdWindowStart = 10, int sjoerdWindowEnd = 24)
    {
        db.OutboundContacts.AddRange(
            new OutboundContact
            {
                Phone = Martien, Name = "Martien", Alias = "martien",
                TimeZoneId = "Europe/Amsterdam", WindowStartHour = 0, WindowEndHour = 24, Categories = "*",
            },
            new OutboundContact
            {
                Phone = Sjoerd, Name = "Sjoerd", Alias = "sjoerd", TimeZoneId = "Europe/Amsterdam",
                WindowStartHour = sjoerdWindowStart, WindowEndHour = sjoerdWindowEnd,
                Categories = "deploy:valsuani", FallbackPhone = Martien,
            });
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task Nothing_changes_for_a_deployment_that_has_no_contacts_yet()
    {
        using var db = NewContext();
        var svc = NewService(db);

        var toMartien = await svc.CheckAsync("sendMessage", Martien, "hi", userId: 1);
        var toSjoerd = await svc.CheckAsync("sendMessage", Sjoerd, "hi", userId: 1);

        Assert.True(toMartien.Allowed);
        Assert.False(toSjoerd.Allowed);
        Assert.Contains("not on the outbound allow-list", toSjoerd.Reason);
    }

    /// <summary>
    /// Sjoerd is not in appsettings' allow-list. If he gets through, it is because the contact
    /// table replaced that list rather than being ANDed with it — which is the intended design,
    /// since two lists that must agree eventually will not.
    /// </summary>
    [Fact]
    public async Task The_contact_table_supersedes_the_static_allow_list()
    {
        using var db = NewContext();
        await SeedPolicyAsync(db, sjoerdWindowStart: 0, sjoerdWindowEnd: 24);
        var svc = NewService(db);

        var result = await svc.CheckAsync("sendMessage", Sjoerd, "deploy klaar", userId: 1, category: "deploy:valsuani");

        Assert.True(result.Allowed);
        Assert.Equal(Sjoerd, result.Recipient);
    }

    /// <summary>
    /// The whole feature in one assertion: the caller aimed at Sjoerd, the guardrail hands back
    /// Martien. A controller that ignored Recipient and sent to request.To would silently undo
    /// this, which is why every send site was changed to use it.
    /// </summary>
    [Fact]
    public async Task A_closed_window_makes_the_guardrail_hand_back_a_different_recipient()
    {
        using var db = NewContext();
        // A zero-width window: start == end means the window never opens, whatever hour the
        // test happens to run at. The contact stays Enabled, so the redirect can only come
        // from the window arithmetic.
        await SeedPolicyAsync(db, sjoerdWindowStart: 10, sjoerdWindowEnd: 10);
        var svc = NewService(db);

        var result = await svc.CheckAsync("sendMessage", Sjoerd, "deploy klaar", userId: 1, category: "deploy:valsuani");

        Assert.True(result.Allowed);
        Assert.Equal(Martien, result.Recipient);
        Assert.NotNull(result.RoutingNote);
    }

    /// <summary>
    /// Muting a contact must behave like a closed window rather than like a block: the message
    /// still has to arrive somewhere, otherwise switching someone off silently drops his alerts.
    /// </summary>
    [Fact]
    public async Task A_muted_contact_also_falls_back_rather_than_dropping_the_message()
    {
        using var db = NewContext();
        await SeedPolicyAsync(db, sjoerdWindowStart: 0, sjoerdWindowEnd: 24);
        var contact = await db.OutboundContacts.FirstAsync(c => c.Phone == Sjoerd);
        contact.Enabled = false;
        await db.SaveChangesAsync();

        var result = await NewService(db).CheckAsync("sendMessage", Sjoerd, "deploy klaar", userId: 1, category: "deploy:valsuani");

        Assert.True(result.Allowed);
        Assert.Equal(Martien, result.Recipient);
    }

    [Fact]
    public async Task A_category_the_contact_does_not_accept_is_blocked_and_recorded()
    {
        using var db = NewContext();
        await SeedPolicyAsync(db, sjoerdWindowStart: 0, sjoerdWindowEnd: 24);
        var svc = NewService(db);

        var result = await svc.CheckAsync("sendMessage", Sjoerd, "backlog is leeg", userId: 1, category: "other");

        Assert.False(result.Allowed);
        // Blocked attempts stay discoverable via GET /api/wa/blockedOutbound, same as before.
        Assert.Single(await db.BlockedOutboundMessages.ToListAsync());
    }

    /// <summary>
    /// Replying to someone who just messaged must survive routing, or "als ze zelf iets vragen"
    /// stops working the moment the contact table is populated.
    /// </summary>
    [Fact]
    public async Task A_reply_reaches_someone_with_no_contact_row_at_all()
    {
        using var db = NewContext();
        await SeedPolicyAsync(db);
        var svc = NewService(db);
        await svc.RecordInboundContactAsync("31699999999");

        var result = await svc.CheckAsync(
            OutboundGuardrailService.CoachOsReplyEndpoint, "31699999999", "hallo terug",
            userId: null, category: "reply");

        Assert.True(result.Allowed);
        Assert.Equal("31699999999", result.Recipient);
    }

    /// <summary>
    /// Routing decides who; the caps decide how often. A redirect must not become a way around
    /// the limits that exist because this number was once banned for exactly that pattern.
    /// </summary>
    [Fact]
    public async Task Volume_caps_still_apply_after_routing_clears_a_recipient()
    {
        using var db = NewContext();
        await SeedPolicyAsync(db, sjoerdWindowStart: 0, sjoerdWindowEnd: 24);
        var svc = NewService(db, maxPerRecipientPer24h: 2);

        var first = await svc.CheckAsync("sendMessage", Sjoerd, "a", 1, "deploy:valsuani");
        var second = await svc.CheckAsync("sendMessage", Sjoerd, "b", 1, "deploy:valsuani");
        var third = await svc.CheckAsync("sendMessage", Sjoerd, "c", 1, "deploy:valsuani");

        Assert.True(first.Allowed);
        Assert.True(second.Allowed);
        Assert.False(third.Allowed);
        Assert.Contains("volume cap", third.Reason);
    }

    /// <summary>
    /// The category and body hash have to reach OutboundSendLogs, or the redirect dedupe has
    /// nothing to look at and Martien gets every night-time deploy notice twice.
    /// </summary>
    [Fact]
    public async Task An_allowed_send_records_its_category_and_body_hash()
    {
        using var db = NewContext();
        await SeedPolicyAsync(db);
        var svc = NewService(db);

        await svc.CheckAsync("sendMessage", Martien, "deploy klaar", userId: 1, category: "deploy:valsuani");

        var log = Assert.Single(await db.OutboundSendLogs.ToListAsync());
        Assert.Equal("deploy:valsuani", log.Category);
        Assert.False(string.IsNullOrEmpty(log.BodyHash));
    }

    [Fact]
    public async Task A_send_without_a_category_is_recorded_as_other()
    {
        using var db = NewContext();
        await SeedPolicyAsync(db);
        var svc = NewService(db);

        await svc.CheckAsync("sendMessage", Martien, "iets", userId: 1);

        Assert.Equal("other", (await db.OutboundSendLogs.SingleAsync()).Category);
    }
}
