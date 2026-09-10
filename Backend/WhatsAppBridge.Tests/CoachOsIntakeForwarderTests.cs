using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using WhatsAppBridge.API.Services;
using Xunit;

namespace WhatsAppBridge.Tests;

/// <summary>
/// Task 1067: covers the "flag default OFF" contract for CoachOsIntakeForwarder — when the
/// feature is off or under-configured, GetAiReplyAsync must return null without making any
/// network call (no HttpClient mocking available in this repo yet, so these tests only exercise
/// the paths that short-circuit before any HTTP call is attempted).
/// </summary>
public class CoachOsIntakeForwarderTests
{
    private static IConfiguration BuildConfig(Dictionary<string, string?> values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values).Build();

    [Fact]
    public void IsEnabled_False_ByDefault()
    {
        var config = BuildConfig(new Dictionary<string, string?>());
        var forwarder = new CoachOsIntakeForwarder(config, NullLogger<CoachOsIntakeForwarder>.Instance);

        Assert.False(forwarder.IsEnabled);
    }

    [Fact]
    public async Task GetAiReplyAsync_ReturnsNull_WhenDisabled_NoException()
    {
        var config = BuildConfig(new Dictionary<string, string?> { ["CoachOsIntake:Enabled"] = "false" });
        var forwarder = new CoachOsIntakeForwarder(config, NullLogger<CoachOsIntakeForwarder>.Instance);

        var reply = await forwarder.GetAiReplyAsync("31699999999@c.us", "Jan", "hallo");

        Assert.Null(reply);
    }

    [Fact]
    public void IsEnabled_False_WhenEnabledButMissingEndpointOrTenant()
    {
        var config = BuildConfig(new Dictionary<string, string?>
        {
            ["CoachOsIntake:Enabled"] = "true",
            // Endpoint and TenantSlug both missing.
        });
        var forwarder = new CoachOsIntakeForwarder(config, NullLogger<CoachOsIntakeForwarder>.Instance);

        Assert.False(forwarder.IsEnabled);
    }

    [Fact]
    public void IsEnabled_True_WhenFullyConfigured()
    {
        var config = BuildConfig(new Dictionary<string, string?>
        {
            ["CoachOsIntake:Enabled"] = "true",
            ["CoachOsIntake:Endpoint"] = "https://example.test/api/whatsapp-intake",
            ["CoachOsIntake:TenantSlug"] = "manon",
        });
        var forwarder = new CoachOsIntakeForwarder(config, NullLogger<CoachOsIntakeForwarder>.Instance);

        Assert.True(forwarder.IsEnabled);
    }

    [Fact]
    public async Task GetAiReplyAsync_ReturnsNull_ForBlankText()
    {
        var config = BuildConfig(new Dictionary<string, string?>
        {
            ["CoachOsIntake:Enabled"] = "true",
            ["CoachOsIntake:Endpoint"] = "https://example.test/api/whatsapp-intake",
            ["CoachOsIntake:TenantSlug"] = "manon",
        });
        var forwarder = new CoachOsIntakeForwarder(config, NullLogger<CoachOsIntakeForwarder>.Instance);

        var reply = await forwarder.GetAiReplyAsync("31699999999@c.us", "Jan", "   ");

        Assert.Null(reply);
    }
}
