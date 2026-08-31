namespace WhatsAppBridge.API.Models;

/// <summary>
/// Task 1067: records the most recent genuine inbound-message timestamp per sender, keyed by
/// normalized phone number (same digit-run normalization as OutboundGuardrailService.Normalize).
/// This table is written to ONLY by OutboundGuardrailService.RecordInboundContactAsync and read
/// ONLY by OutboundGuardrailService.CheckAsync's reply-window check — it exists purely so the
/// guardrail can prove "this outbound send is a reply to a real prior inbound message" for the
/// CoachOS service route, instead of trusting the caller's say-so.
/// </summary>
public class InboundContact
{
    public int Id { get; set; }
    public string Sender { get; set; } = "";
    public DateTime LastInboundAtUtc { get; set; }
}
