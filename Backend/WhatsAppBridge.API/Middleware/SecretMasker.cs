using System.Text.RegularExpressions;

namespace WhatsAppBridge.API.Middleware;

/// <summary>
/// Blanks out the parts of a stored message that are credentials rather than content.
///
/// Martien's decision on 2026-09-10 was to keep audit bodies indefinitely — being able to read
/// back exactly what was sent is the whole point of the log, and a retention job would eat the
/// oldest evidence first. The cost of that decision is that the two things this bridge sends
/// most often are one-time login codes and vault approve/reject links, and both stay usable for
/// as long as the row exists. Whoever reaches the database reaches every approval link ever
/// issued.
///
/// So the row keeps the message and loses the secret: "Approve: https://vault.../approve/***"
/// still tells you what was sent, to whom, and when, which is what an audit trail is for, while
/// no longer being a working link.
///
/// Deliberately conservative. Each pattern needs a nearby keyword — a bare six-digit number is
/// left alone, because house numbers, amounts and order references look identical and masking
/// real content would make the log lie about what was sent. This trades a missed secret against
/// a corrupted record, in that direction on purpose.
/// </summary>
public static class SecretMasker
{
    private const string Mask = "***";
    private static readonly TimeSpan Timeout = TimeSpan.FromMilliseconds(200);

    /// <summary>
    /// Secret-bearing query parameters: "...?token=abc123" -> "...?token=***". Stops at the next
    /// "&amp;" or whitespace so the rest of the URL, which is the useful part, survives.
    ///
    /// Three shapes of parameter name, because a literal list missed the common real-world ones:
    /// "refresh_token", "id_token" and "client_secret" all sailed through a list that only knew
    /// "token" and "secret" exactly. So: any name ENDING in token/secret/password ("×_token" is
    /// how OAuth names every credential it mints); "key"/"auth" only when they are the whole name
    /// or follow a separator (api_key yes, monkey no); and a short exact list for the rest.
    /// "code" stays exact-match only — suffix-matching it would eat postcode and countrycode.
    /// </summary>
    private static readonly Regex QuerySecret = new(
        @"([?&](?:[a-z0-9_\-]*(?:token|secret|password|passwd|pwd)|(?:[a-z0-9_\-]*[_\-])?(?:key|auth)|code|otp|signature|sig|pw)=)[^\s&""']+",
        RegexOptions.IgnoreCase | RegexOptions.Compiled, Timeout);

    /// <summary>
    /// Opaque token as a path segment, which is how the vault's approve/reject links are built:
    /// "/approve/9f2c...". The verb stays visible; only the segment that makes it work is lost.
    /// </summary>
    private static readonly Regex PathToken = new(
        @"(/(?:approve|reject|confirm|verify|unsubscribe|reset|invite)/)[A-Za-z0-9_\-\.]{8,}",
        RegexOptions.IgnoreCase | RegexOptions.Compiled, Timeout);

    /// <summary>A bearer token pasted into a message body.</summary>
    private static readonly Regex BearerToken = new(
        @"\b(Bearer\s+)[A-Za-z0-9_\-\.]{12,}",
        RegexOptions.IgnoreCase | RegexOptions.Compiled, Timeout);

    /// <summary>
    /// A one-time code introduced by a word that says it is one, in either language, with at most
    /// a short run of separator characters in between ("code: 123456", "je verificatiecode is
    /// 8452"). The keyword requirement is what keeps this off ordinary numbers in ordinary
    /// sentences.
    ///
    /// The digits may be grouped — "483 920", "4839-2011" — because grouped is how SMS and
    /// WhatsApp actually present codes. Requiring a contiguous run meant the single most common
    /// presentation of the single most common secret passed through unmasked (or worse,
    /// half-masked: "4839-2011" became "***-2011", which reads as masked while leaving half the
    /// code standing). Still 4-8 digits total and never a longer run: an 11-digit phone number
    /// after the word "code" does not match, because every attempt ends adjacent to yet another
    /// digit.
    /// </summary>
    private static readonly Regex OneTimeCode = new(
        @"\b((?:verificatie|verificatie-|beveiligings|toegangs)?code|pincode|pin|otp|token|wachtwoord|password)\b(\W{0,4}(?:is|=|:)?\W{0,4})(\d(?:[ \-]?\d){3,7})(?!\d)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled, Timeout);

    /// <summary>
    /// Returns <paramref name="text"/> with any recognised secret replaced. Null and empty pass
    /// through untouched. A regex timeout returns the input unchanged rather than dropping the
    /// row: an unmasked audit entry is worse than a masked one, but no audit entry at all is
    /// worse than both.
    /// </summary>
    public static string? Apply(string? text)
    {
        if (string.IsNullOrEmpty(text)) return text;

        try
        {
            var result = QuerySecret.Replace(text, "$1" + Mask);
            result = PathToken.Replace(result, "$1" + Mask);
            result = BearerToken.Replace(result, "$1" + Mask);
            result = OneTimeCode.Replace(result, "$1$2" + Mask);
            return result;
        }
        catch (RegexMatchTimeoutException)
        {
            return text;
        }
    }
}
