namespace WhatsAppBridge.API.Services;

/// <summary>
/// The single definition of "which number is this, really".
///
/// This used to exist four times — in <see cref="OutboundGuardrailService"/>, in
/// <see cref="OutboundRoutingService"/>, in the audit middleware and inline in the routing
/// controller — and the copies had already drifted apart. Three skipped leading non-digits so
/// that "+31633984381" normalised to "31633984381"; the guardrail's copy did not, and produced
/// the empty string for exactly that input. The consequences were quiet rather than loud: a
/// send to "+3163..." was logged against recipient "" (so the per-recipient volume cap counted
/// every such send as the same recipient) and a routing redirect could never match its own
/// dedupe row. Two behaviours that only differ on input nobody tests with are worse than one
/// behaviour that is occasionally wrong, so there is now one.
///
/// The rule: cut the string at the first "@" or ":", then keep the digits of what is left.
///
/// The cut comes first and does the real work. A JID carries a routing tail that must not become
/// part of the number — "31633984381@c.us", or device-suffixed "254715438010:78@s.whatsapp.net",
/// where naively keeping every digit would yield "25471543801078" and match nothing. An earlier
/// <c>Where(char.IsLetterOrDigit)</c> made that class of mistake with letters instead of digits
/// and produced "31633984381cus", which silently blocked vault-approval messages to Martien in
/// production for a day (task 897, 2026-08-30).
///
/// Keeping the digits of the remainder, rather than only the leading run, is what admits a number
/// as a human writes it: "+31 6 33 98 43 81" is the form people paste out of a contact card, and
/// a leading-run rule stopped at the first space and returned "31" — six digits short of usable,
/// so the routing form rejected a number that was perfectly valid.
/// </summary>
public static class PhoneNumber
{
    /// <summary>
    /// Returns the digits of the number, or the empty string when there are none. An empty
    /// result means "not a number" and callers must treat it as such rather than letting it fall
    /// through as a recipient — see <see cref="IsUsable"/>.
    /// </summary>
    public static string Normalize(string? s)
    {
        if (string.IsNullOrEmpty(s)) return string.Empty;

        var head = s.AsSpan();

        // The domain tail is always a tail: everything from "@" on is routing, never number.
        var at = head.IndexOf('@');
        if (at >= 0) head = head[..at];

        // A colon is a device suffix only when a number already precedes it
        // ("254715438010:78"). In "whatsapp:+31633984381" the same character is a scheme
        // prefix and cutting there would discard the entire number, so it is left alone.
        var colon = head.IndexOf(':');
        if (colon >= 0 && ContainsDigit(head[..colon])) head = head[..colon];

        Span<char> digits = head.Length <= 64 ? stackalloc char[head.Length] : new char[head.Length];
        var n = 0;
        foreach (var c in head)
            if (char.IsDigit(c)) digits[n++] = c;

        return new string(digits[..n]);
    }

    private static bool ContainsDigit(ReadOnlySpan<char> s)
    {
        foreach (var c in s)
            if (char.IsDigit(c)) return true;
        return false;
    }

    /// <summary>
    /// True when normalisation produced something that could plausibly be dialled. Guards the
    /// paths where an empty normalisation would otherwise become a wildcard: volume-cap
    /// accounting keyed on the recipient, and dedupe lookups that would all collide on "".
    /// </summary>
    public static bool IsUsable(string? s) => Normalize(s).Length >= 6;
}
