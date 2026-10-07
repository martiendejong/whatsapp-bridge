using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using WhatsAppBridge.API.Services;
using WhatsAppBridge.API.Services.Engines;
using Xunit;

namespace WhatsAppBridge.Tests;

/// <summary>
/// Task 4998: a session whose saved credentials WhatsApp refuses must end up as needs_qr (with a QR on the
/// sessions page), not as a silent "disconnected". These cover the sidecar-event to engine-event mapping and
/// the session status mapping; the sidecar side is covered by its own node tests.
/// </summary>
public class BaileysNeedsQrTests
{
    private static BaileysEngine NewEngine() =>
        new(Path.Combine(Path.GetTempPath(), "baileys-needsqr-test"), new ConfigurationBuilder().Build(), LoggerFactory.Create(_ => { }));

    [Fact]
    public void Needs_qr_event_from_the_sidecar_raises_NeedsQr_with_the_reason_and_marks_it_disconnected()
    {
        var engine = NewEngine();
        string? reason = null;
        engine.NeedsQr += (_, r) => reason = r;

        engine.HandleLine("""{"event":"connected","data":{"jid":"31600000000:1@s.whatsapp.net"}}""");
        Assert.True(engine.IsConnected);
        engine.HandleLine("""{"event":"needs_qr","data":{"reason":"logged-out","archivedTo":"x"}}""");

        Assert.Equal("logged-out", reason);
        Assert.False(engine.IsConnected);
    }

    [Fact]
    public void Needs_qr_without_a_reason_still_raises()
    {
        var engine = NewEngine();
        string? reason = null;
        engine.NeedsQr += (_, r) => reason = r;
        engine.HandleLine("""{"event":"needs_qr","data":{}}""");
        Assert.Equal("unknown", reason);
    }

    [Fact]
    public void LoggedOut_disconnect_is_still_a_disconnect_event()
    {
        var engine = NewEngine();
        var disconnects = 0;
        engine.Disconnected += (_, _) => disconnects++;
        engine.HandleLine("""{"event":"disconnected","data":{"code":401,"loggedOut":true}}""");
        Assert.Equal(1, disconnects);
    }

    [Fact]
    public void Auth_write_warning_is_surfaced_and_cleared_when_writes_recover()
    {
        var engine = NewEngine();
        var seen = new List<string?>();
        engine.AuthWarning += (_, t) => seen.Add(t);

        engine.HandleLine("""{"event":"auth_write_warning","data":{"pending":3,"failingForMs":780000,"lastError":"EPERM creds.json"}}""");
        engine.HandleLine("""{"event":"auth_write_ok","data":{}}""");

        Assert.Equal(2, seen.Count);
        Assert.Contains("13 min", seen[0]);
        Assert.Contains("EPERM creds.json", seen[0]);
        Assert.Null(seen[1]);
    }

    [Theory]
    // restoring a saved session that gets disconnected stays "disconnected" (as before)
    [InlineData("connected", false, true, "disconnected")]
    // interactive pairing that never connected is "failed" (as before)
    [InlineData("qr_pending", false, false, "failed")]
    [InlineData("connected", true, false, "disconnected")]
    // needs_qr is sticky: a later disconnect event (e.g. the process exiting) must not hide it
    [InlineData("needs_qr", false, true, "needs_qr")]
    [InlineData("needs_qr", true, false, "needs_qr")]
    public void Status_after_disconnect(string current, bool everConnected, bool restoring, string expected) =>
        Assert.Equal(expected, WhatsAppBridgeService.StatusAfterDisconnect(current, everConnected, restoring));
}
