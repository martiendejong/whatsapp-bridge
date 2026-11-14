using System.Net;
using WhatsAppBridge.API.Services;
using Xunit;

namespace WhatsAppBridge.Tests;

/// <summary>
/// Task 3299: covers the two review corrections from jengo-agi PR #190 that must not be
/// optimized away — (1) only an exact HTTP 200 is accepted, never IsSuccessStatusCode, so a
/// RequiresApproval credential's 202 approval-pending envelope can never leak into config as
/// if it were the real secret; (2) the provider's own no-op guards (disabled / no bootstrap
/// key / no mappings) never make a network call and never set any config value.
///
/// VaultResponseParser.ExtractPassword is a pure, network-free function specifically so the
/// 200-only acceptance rule can be tested against every status/body combination without
/// mocking HttpClient transport.
/// </summary>
public class VaultConfigurationTests
{
    [Fact]
    public void ExtractPassword_Accepts_Http200_WithValidPassword()
    {
        var value = VaultResponseParser.ExtractPassword(
            HttpStatusCode.OK, """{"id":216,"password":"the-real-secret"}""", out var failureReason);

        Assert.Equal("the-real-secret", value);
        Assert.Null(failureReason);
    }

    [Fact]
    public void ExtractPassword_Rejects_Http202_ApprovalPendingEnvelope_EvenWithNoPasswordField()
    {
        // A RequiresApproval credential answers 202 with a pending-approval envelope.
        var value = VaultResponseParser.ExtractPassword(
            HttpStatusCode.Accepted, """{"status":"pending_approval","approvalId":123}""", out var failureReason);

        Assert.Null(value);
        Assert.Contains("202", failureReason);
    }

    [Fact]
    public void ExtractPassword_Rejects_Http202_EvenWhenBodyHappensToContainAPasswordField()
    {
        // The critical regression case: IsSuccessStatusCode would treat this 202 as success
        // and let a well-formed-looking "password" field through. The status code alone must
        // reject it regardless of body content.
        var value = VaultResponseParser.ExtractPassword(
            HttpStatusCode.Accepted, """{"status":"pending_approval","password":"should-never-be-used"}""", out var failureReason);

        Assert.Null(value);
        Assert.Contains("202", failureReason);
    }

    [Theory]
    [InlineData(HttpStatusCode.Created)]      // 201 — another "successful" 2xx that isn't 200
    [InlineData(HttpStatusCode.NoContent)]    // 204
    public void ExtractPassword_Rejects_Other2xxStatusCodes(HttpStatusCode status)
    {
        var value = VaultResponseParser.ExtractPassword(
            status, """{"password":"whatever"}""", out var failureReason);

        Assert.Null(value);
        Assert.NotNull(failureReason);
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    public void ExtractPassword_Rejects_4xxAnd5xx(HttpStatusCode status)
    {
        var value = VaultResponseParser.ExtractPassword(
            status, """{"password":"whatever"}""", out var failureReason);

        Assert.Null(value);
        Assert.Contains(((int)status).ToString(), failureReason);
    }

    [Fact]
    public void ExtractPassword_Rejects_Http200_MissingPasswordField()
    {
        var value = VaultResponseParser.ExtractPassword(
            HttpStatusCode.OK, """{"id":216,"username":"x-api-key"}""", out var failureReason);

        Assert.Null(value);
        Assert.Contains("password", failureReason);
    }

    [Fact]
    public void ExtractPassword_Rejects_Http200_EmptyPasswordValue()
    {
        var value = VaultResponseParser.ExtractPassword(
            HttpStatusCode.OK, """{"id":216,"password":""}""", out var failureReason);

        Assert.Null(value);
        Assert.Contains("empty", failureReason);
    }

    [Fact]
    public void ExtractPassword_Rejects_Http200_NullPasswordValue()
    {
        var value = VaultResponseParser.ExtractPassword(
            HttpStatusCode.OK, """{"id":216,"password":null}""", out var failureReason);

        Assert.Null(value);
        Assert.NotNull(failureReason);
    }

    [Fact]
    public void ExtractPassword_Rejects_Http200_UnparseableJsonBody()
    {
        var value = VaultResponseParser.ExtractPassword(
            HttpStatusCode.OK, "not json at all {{{", out var failureReason);

        Assert.Null(value);
        Assert.Contains("unparseable", failureReason);
    }

    [Fact]
    public void ExtractPassword_Rejects_Http200_EmptyBody()
    {
        var value = VaultResponseParser.ExtractPassword(
            HttpStatusCode.OK, "", out var failureReason);

        Assert.Null(value);
        Assert.NotNull(failureReason);
    }

    [Fact]
    public void ExtractPassword_Rejects_Http200_NonObjectJsonBody()
    {
        var value = VaultResponseParser.ExtractPassword(
            HttpStatusCode.OK, "[1,2,3]", out var failureReason);

        Assert.Null(value);
        Assert.NotNull(failureReason);
    }

    // ── Provider-level no-op guards: none of these may make a network call. ──────────

    [Fact]
    public void Load_NoOp_WhenDisabled()
    {
        var provider = new VaultConfigurationProvider(new VaultOptions { Enabled = false });

        provider.Load();

        Assert.False(provider.TryGet("InboundWebhook:ApiKey", out _));
    }

    [Fact]
    public void Load_NoOp_WhenBootstrapApiKeyMissing()
    {
        var provider = new VaultConfigurationProvider(new VaultOptions
        {
            Enabled = true,
            ApiKey = "",
            ProjectId = 9,
            Mappings = { new VaultMapping { ConfigKey = "InboundWebhook:ApiKey", CredentialId = 216 } },
        });

        provider.Load();

        Assert.False(provider.TryGet("InboundWebhook:ApiKey", out _));
    }

    [Fact]
    public void Load_NoOp_WhenNoMappingsConfigured()
    {
        var provider = new VaultConfigurationProvider(new VaultOptions
        {
            Enabled = true,
            ApiKey = "pm_test-bootstrap-key",
            ProjectId = 9,
        });

        provider.Load();

        Assert.False(provider.TryGet("InboundWebhook:ApiKey", out _));
    }
}
