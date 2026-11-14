using Microsoft.EntityFrameworkCore;
using WhatsAppBridge.API.Data;

namespace WhatsAppBridge.API.Services;

/// <summary>
/// Takes the alerts <see cref="ServerMonitorService"/> decides on and puts them on WhatsApp —
/// through the outbound guardrail, never around it. Category "serverdown", so the routing
/// policy (once armed) decides who is woken, and the volume caps hold even if the monitor
/// itself runs away.
///
/// The outcome matters as much as the attempt. A TRANSIENT failure (no connected session, the
/// send threw) must hand the alert claim back to the state machine so the sweep re-fires it a
/// minute later — the WhatsApp session being down at the exact threshold minute is strongly
/// correlated with the kind of incident being announced, and "marked as told, told no one" was
/// this feature's worst reviewed defect. A POLICY refusal (guardrail block, duplicate
/// suppression) is a decision, not a failure: the claim stands, because retrying a decision
/// once a minute fills the blocked-list overnight. Blocked alerts remain visible under
/// GET /api/wa/blockedOutbound.
///
/// A sweep tick can produce several alerts at once — one dead VPS takes all its subjects down
/// together — so batches go out as ONE combined message. Fifteen separate sends against a
/// global cap of ten per hour meant alerts eleven to fifteen were silently lost, and the
/// combined message is also simply the better page to receive at 03:00.
/// </summary>
public sealed class MonitorAlertDispatcher
{
    public enum DispatchOutcome
    {
        /// <summary>Sent and confirmed.</summary>
        Delivered,

        /// <summary>Guardrail blocked or suppressed it — deliberate; do not release the claim.</summary>
        PolicyRefused,

        /// <summary>No session / send failed — release the claim so the sweep retries.</summary>
        TransientFailure,
    }

    private readonly AppDbContext _context;
    private readonly OutboundGuardrailService _guardrail;
    private readonly WhatsAppBridgeService _whatsapp;
    private readonly ILogger<MonitorAlertDispatcher> _logger;
    private readonly string _recipient;
    private readonly int? _sessionUserId;

    public MonitorAlertDispatcher(AppDbContext context, OutboundGuardrailService guardrail,
        WhatsAppBridgeService whatsapp, IConfiguration configuration,
        ILogger<MonitorAlertDispatcher> logger)
    {
        _context = context;
        _guardrail = guardrail;
        _whatsapp = whatsapp;
        _logger = logger;
        _recipient = configuration.GetValue("Monitor:Recipient", "31633984381")!;
        _sessionUserId = configuration.GetValue<int?>("Monitor:SessionUserId", null);
    }

    public Task<DispatchOutcome> DispatchAsync(ServerMonitorService.MonitorAlert alert) =>
        DispatchBatchAsync(new List<ServerMonitorService.MonitorAlert> { alert });

    public async Task<DispatchOutcome> DispatchBatchAsync(IReadOnlyList<ServerMonitorService.MonitorAlert> alerts)
    {
        if (alerts.Count == 0) return DispatchOutcome.Delivered;

        var text = alerts.Count == 1
            ? alerts[0].Text
            : "Servermonitor, " + alerts.Count + " meldingen:\n" +
              string.Join("\n", alerts.Select(a => "· " + a.Text));
        var subjects = string.Join(", ", alerts.Select(a => a.Subject));

        try
        {
            // Session BEFORE guardrail, and the order is deliberate: CheckAsync writes a
            // volume-cap attempt row the moment it allows a send. Checking first and then
            // discovering there is no session would burn cap budget once a minute for as long
            // as the session stays down — and the cap would then block the real alert the
            // moment the session came back.
            var sessionQuery = _context.WhatsAppSessions.Where(s => s.Status == "connected");
            // Multi-user bridge: Monitor:SessionUserId pins alerts to one user's session so
            // they cannot go out from whoever paired a device most recently. Null (default)
            // means single-tenant: any connected session.
            if (_sessionUserId != null)
                sessionQuery = sessionQuery.Where(s => s.UserId == _sessionUserId.Value);
            var sessionId = await sessionQuery
                .OrderByDescending(s => s.LastSeenAt)
                .Select(s => s.SessionId)
                .FirstOrDefaultAsync();
            if (sessionId == null)
            {
                _logger.LogWarning("Monitor alert(s) for {Subjects} not sent: no connected WhatsApp session. " +
                                   "Claim released; the sweep retries next tick.", subjects);
                return DispatchOutcome.TransientFailure;
            }

            var guard = await _guardrail.CheckAsync("monitorAlert", _recipient, text,
                userId: null, category: "serverdown");
            if (guard.Suppressed)
            {
                _logger.LogInformation("Monitor alert(s) for {Subjects} suppressed: {Reason}", subjects, guard.Reason);
                return DispatchOutcome.PolicyRefused;
            }
            if (!guard.Allowed)
            {
                _logger.LogWarning("Monitor alert(s) for {Subjects} blocked by guardrail: {Reason}", subjects, guard.Reason);
                return DispatchOutcome.PolicyRefused;
            }

            await _whatsapp.SendMessageAsync(sessionId, guard.Recipient, text);
            await _guardrail.ConfirmDeliveredAsync(guard);
            return DispatchOutcome.Delivered;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Monitor alert(s) for {Subjects} failed to send; claim released for retry.", subjects);
            return DispatchOutcome.TransientFailure;
        }
    }
}
