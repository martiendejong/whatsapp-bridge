using System.Text.Json;
using Dawa.Messages;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using WhatsAppBridge.API.Services;
using WhatsAppBridge.API.Services.Engines;
using Xunit;

namespace WhatsAppBridge.Tests;

/// <summary>
/// Inbound mapping of Meta Cloud API webhook payloads onto the bridge's shared message DTOs,
/// and the webhook router that hands a payload to the engine owning the phone-number-id.
/// </summary>
public class MetaCloudEngineTests
{
    private static MetaCloudEngine NewEngine(MetaWebhookRouter? router = null, string phoneId = "1453342791198748")
    {
        var services = new ServiceCollection().BuildServiceProvider();
        var settings = new MetaCloudSettings(
            services.GetRequiredService<IServiceScopeFactory>(),
            new ConfigurationBuilder().Build(),
            NullLogger<MetaCloudSettings>.Instance);
        return new MetaCloudEngine(phoneId, settings, router ?? new MetaWebhookRouter(), NullLoggerFactory.Instance);
    }

    private static JsonElement Value(string json) => JsonDocument.Parse(json).RootElement;

    [Fact]
    public void Inbound_text_becomes_an_IncomingMessage_with_jid_and_push_name()
    {
        var engine = NewEngine();
        IncomingMessage? got = null;
        engine.MessageReceived += (_, m) => got = m;

        engine.HandleWebhookValue(Value("""
        {"metadata":{"phone_number_id":"1453342791198748"},
         "contacts":[{"wa_id":"31612345678","profile":{"name":"Anna"}}],
         "messages":[{"from":"31612345678","id":"wamid.A","timestamp":"1790000000","type":"text","text":{"body":"hoi"}}]}
        """));

        Assert.NotNull(got);
        Assert.Equal("wamid.A", got!.Id);
        Assert.Equal("31612345678@s.whatsapp.net", got.RemoteJid);
        Assert.Equal("hoi", got.Text);
        Assert.Equal("Anna", got.PushName);
        Assert.Equal(MessageType.Text, got.Type);
        Assert.False(got.FromMe);
        Assert.Equal(1790000000, got.Timestamp);
    }

    [Fact]
    public void Inbound_audio_carries_a_meta_media_reference()
    {
        var engine = NewEngine();
        IncomingMessage? got = null;
        engine.MessageReceived += (_, m) => got = m;

        engine.HandleWebhookValue(Value("""
        {"messages":[{"from":"31612345678","id":"wamid.B","timestamp":"1790000001","type":"audio",
                      "audio":{"id":"MEDIA42","mime_type":"audio/ogg; codecs=opus"}}]}
        """));

        Assert.Equal(MessageType.Audio, got!.Type);
        Assert.Equal("meta-media:MEDIA42", got.MediaUrl);
        Assert.Equal("audio/ogg; codecs=opus", got.MimeType);
    }

    [Fact]
    public void Reply_context_is_mapped_to_the_quoted_message_id()
    {
        var engine = NewEngine();
        IncomingMessage? got = null;
        engine.MessageReceived += (_, m) => got = m;

        engine.HandleWebhookValue(Value("""
        {"messages":[{"from":"31612345678","id":"wamid.C","timestamp":"1790000002","type":"text",
                      "context":{"id":"wamid.PREV"},"text":{"body":"ja"}}]}
        """));

        Assert.Equal("wamid.PREV", got!.QuotedMessageId);
    }

    [Fact]
    public void Status_updates_map_to_delivery_status_and_are_remembered()
    {
        var engine = NewEngine();
        (string Id, string Jid, MessageStatus Status)? got = null;
        engine.MessageStatusUpdated += (_, e) => got = e;

        engine.HandleWebhookValue(Value("""
        {"statuses":[{"id":"wamid.OUT","status":"delivered","recipient_id":"31612345678"}]}
        """));

        Assert.Equal(MessageStatus.Delivered, got!.Value.Status);
        Assert.Equal("31612345678@s.whatsapp.net", got.Value.Jid);
        Assert.Equal(MessageStatus.Delivered, engine.GetMessageStatus("wamid.OUT"));
    }

    [Fact]
    public void Unsupported_message_types_are_ignored_not_thrown()
    {
        var engine = NewEngine();
        var raised = false;
        engine.MessageReceived += (_, _) => raised = true;

        engine.HandleWebhookValue(Value("""
        {"messages":[{"from":"31612345678","id":"wamid.D","timestamp":"1","type":"order"}]}
        """));

        Assert.False(raised);
    }

    [Fact]
    public async Task Router_finds_the_engine_by_phone_number_id_and_forgets_it_on_dispose()
    {
        var router = new MetaWebhookRouter();
        var engine = NewEngine(router, "999");
        await engine.ConnectAsync();

        Assert.Same(engine, router.Find("999"));
        Assert.Null(router.Find("123"));

        await engine.DisposeAsync();
        Assert.Null(router.Find("999"));
    }

    [Fact]
    public async Task Group_operations_fail_loudly_instead_of_pretending()
    {
        var engine = NewEngine();
        await Assert.ThrowsAsync<NotSupportedException>(() =>
            engine.CreateGroupAsync("x", new[] { "31612345678" }, CancellationToken.None));
    }

    [Theory]
    [InlineData("meta", true)]
    [InlineData("dawa", true)]
    [InlineData("baileys", true)]
    [InlineData("waweb", true)]
    [InlineData("nope", false)]
    [InlineData(null, false)]
    public void Per_number_engine_choice_accepts_exactly_the_four_engines(string? engine, bool valid)
    {
        Assert.Equal(valid, EngineSettingsService.IsValidSessionEngine(engine));
    }

    [Fact]
    public void Meta_is_a_per_number_choice_only_never_a_global_default()
    {
        Assert.False(EngineSettingsService.IsValidEngine("meta"));
    }
}
