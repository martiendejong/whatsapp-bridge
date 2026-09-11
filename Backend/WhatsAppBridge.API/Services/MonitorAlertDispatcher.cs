using Microsoft.EntityFrameworkCore;
using WhatsAppBridge.API.Data;

namespace WhatsAppBridge.API.Services;

/// <summary>
/// Takes the alerts <see cref="ServerMonitorService"/> decides on and puts them on WhatsApp —
/// through the outbound guardrail, never around it. Category "serverdown", so the routing
/// policy (once armed) decides who is woken, and the volume caps hold even if the monitor
/// itself runs away: a bug that produced an alert a minute would be throttled like any other
/// runaway sender, which is precisely the treatment a misbehaving alerter deserves.
///
/// A failed dispatch is logged and dropped, not retried: the state machine has already marked
/// the episode as alerted, and the silence/recovery messages that follow carry enough context
/// that a single lost alert does not orphan the story. Retrying into a down session would only
/// queue up stale alarms to fire the moment the session reconnects.
/// </summary>
public sealed class MonitorAlertDispatcher
{
    private readonly AppDbContext _context;
    private readonly OutboundGuardrailService _guardrail;
    private readonly WhatsAppBridgeService _whatsapp;
    private readonly ILogger<MonitorAlertDispatcher> _logger;
    private readonly string _recipient;

    public MonitorAlertDispatcher(AppDbContext context, OutboundGuardrailService guardrail,
        WhatsAppBridgeService whatsapp, IConfiguration configuration,
        ILogger<MonitorAlertDispatcher> logger)
    {
        _context = context;
        _guardrail = guardrail;
        _whatsapp = whatsapp;
        _logger = logger;
        _recipient = configuration.GetValue("Monitor:Recipient", "31633984381")!;
    }

    public async Task DispatchAsync(ServerMonitorService.MonitorAlert alert)
    {
        try
        {
            var guard = await _guardrail.CheckAsync("monitorAlert", _recipient, alert.Text,
                userId: null, category: "serverdown");
            if (guard.Suppressed)
            {
                _logger.LogInformation("Monitor alert for {Subject} suppressed: {Reason}",
                    alert.Subject, guard.Reason);
                return;
            }
            if (!guard.Allowed)
            {
                _logger.LogWarning("Monitor alert for {Subject} blocked by guardrail: {Reason}",
                    alert.Subject, guard.Reason);
                return;
            }

            // Any connected session will do — the bridge runs one number. Ordered so that if
            // several exist, the one most recently seen alive gets the send.
            var sessionId = await _context.WhatsAppSessions
                .Where(s => s.Status == "connected")
                .OrderByDescending(s => s.LastSeenAt)
                .Select(s => s.SessionId)
                .FirstOrDefaultAsync();
            if (sessionId == null)
            {
                _logger.LogWarning("Monitor alert for {Subject} not sent: no connected WhatsApp session. " +
                                   "Text was: {Text}", alert.Subject, alert.Text);
                return;
            }

            await _whatsapp.SendMessageAsync(sessionId, guard.Recipient, alert.Text);
            await _guardrail.ConfirmDeliveredAsync(guard);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Monitor alert for {Subject} failed to send: {Text}", alert.Subject, alert.Text);
        }
    }
}
