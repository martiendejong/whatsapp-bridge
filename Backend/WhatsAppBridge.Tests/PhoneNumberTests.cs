using WhatsAppBridge.API.Services;
using Xunit;

namespace WhatsAppBridge.Tests;

/// <summary>
/// Four copies of this normalizer had drifted apart across the guardrail, the routing service,
/// the audit middleware and the routing controller. They are one function now, and these are the
/// cases where they disagreed.
/// </summary>
public class PhoneNumberTests
{
    /// <summary>
    /// The one that mattered. The guardrail's copy took digits from the start of the string and
    /// stopped at the first non-digit — but "+31633984381" starts with "+", so it stopped
    /// immediately and normalized Martien's number to the empty string. Every comparison against
    /// the allow-list then failed on a number written the way people actually write it.
    /// </summary>
    [Theory]
    [InlineData("+31633984381", "31633984381")]
    [InlineData("31633984381", "31633984381")]
    [InlineData("0031633984381", "0031633984381")]
    [InlineData("31633984381@s.whatsapp.net", "31633984381")]
    [InlineData("31633984381@c.us", "31633984381")]
    public void Leading_punctuation_is_skipped_not_treated_as_a_terminator(string input, string expected)
    {
        Assert.Equal(expected, PhoneNumber.Normalize(input));
    }

    /// <summary>
    /// The form people actually paste. A leading-digit-run rule stopped at the first space and
    /// returned "31", which then failed the six-digit check and had the routing form reject a
    /// valid number with "must be a number with at least 6 digits".
    /// </summary>
    [Theory]
    [InlineData(" +31 6 33 98 43 81 ")]
    [InlineData("+31-6-33-98-43-81")]
    [InlineData("(+31) 6 3398 4381")]
    public void A_number_written_the_way_people_write_it_still_normalizes(string input)
    {
        Assert.Equal("31633984381", PhoneNumber.Normalize(input));
    }

    /// <summary>
    /// The JID tail must be cut, not merely stripped of letters. A device-suffixed JID keeps
    /// digits after the colon, so anything that kept every digit in the string would turn
    /// "254715438010:78@s.whatsapp.net" into "25471543801078" and match no contact anywhere.
    /// </summary>
    [Theory]
    [InlineData("254715438010:78@s.whatsapp.net", "254715438010")]
    [InlineData("31633984381:3@s.whatsapp.net", "31633984381")]
    public void A_device_suffix_is_cut_rather_than_absorbed(string input, string expected)
    {
        Assert.Equal(expected, PhoneNumber.Normalize(input));
    }

    /// <summary>
    /// The same colon means two different things. In a JID it separates the device index; in a
    /// Twilio-style recipient it ends the scheme, and cutting there would throw the whole number
    /// away and silently normalize to "not a number".
    /// </summary>
    [Theory]
    [InlineData("whatsapp:+31633984381", "31633984381")]
    [InlineData("tel:+31633984381", "31633984381")]
    public void A_scheme_prefix_is_not_mistaken_for_a_device_suffix(string input, string expected)
    {
        Assert.Equal(expected, PhoneNumber.Normalize(input));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("sjoerd")]
    [InlineData("++")]
    public void Input_with_no_digits_normalizes_to_empty(string? input)
    {
        Assert.Equal(string.Empty, PhoneNumber.Normalize(input));
    }

    /// <summary>
    /// IsUsable is what stops a typo becoming a routing contact. Six digits is deliberately
    /// permissive — short codes exist — but it rejects the cases that are plainly not a number.
    /// </summary>
    [Theory]
    [InlineData("+31633984381", true)]
    [InlineData("123456", true)]
    [InlineData("12345", false)]
    [InlineData("sjoerd", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsUsable_rejects_what_could_not_be_a_number(string? input, bool expected)
    {
        Assert.Equal(expected, PhoneNumber.IsUsable(input));
    }
}
