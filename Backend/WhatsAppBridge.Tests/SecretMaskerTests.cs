using WhatsAppBridge.API.Middleware;
using Xunit;

namespace WhatsAppBridge.Tests;

/// <summary>
/// Audit rows are kept indefinitely by decision, so the two things this bridge sends most often —
/// vault approve/reject links and one-time login codes — would otherwise stay usable for as long
/// as the database exists. The row must keep saying what was sent while ceasing to be a working
/// credential.
///
/// The second half of this suite is the more important half. A masker that eats real content
/// makes the log lie about what was sent, which is worse than the problem it solves, so the
/// not-masked cases are tested at least as hard as the masked ones.
/// </summary>
public class SecretMaskerTests
{
    // ─── Masked ──────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("Goedkeuren: https://vault.jengo.nl/approve/9f2c8ab4d1e7", "9f2c8ab4d1e7")]
    [InlineData("https://vault.jengo.nl/reject/aa11bb22cc33dd44", "aa11bb22cc33dd44")]
    [InlineData("Klik https://x.nl/verify/abcdefgh12345678 om te bevestigen", "abcdefgh12345678")]
    public void An_approval_link_loses_the_part_that_makes_it_work(string input, string secret)
    {
        var masked = SecretMasker.Apply(input);

        Assert.DoesNotContain(secret, masked);
        Assert.Contains("***", masked);
    }

    /// <summary>The link must stay recognisable as a link, or the row stops being evidence.</summary>
    [Fact]
    public void The_rest_of_the_link_survives_so_the_row_still_says_what_was_sent()
    {
        var masked = SecretMasker.Apply("Goedkeuren: https://vault.jengo.nl/approve/9f2c8ab4d1e7");

        Assert.Equal("Goedkeuren: https://vault.jengo.nl/approve/***", masked);
    }

    [Theory]
    [InlineData("https://x.nl/a?token=abc123def456")]
    [InlineData("https://x.nl/a?access_token=abc123def456")]
    [InlineData("https://x.nl/a?foo=1&secret=abc123def456")]
    [InlineData("https://x.nl/a?api_key=abc123def456")]
    public void A_secret_in_the_query_string_is_blanked(string input)
    {
        var masked = SecretMasker.Apply(input);

        Assert.DoesNotContain("abc123def456", masked);
    }

    /// <summary>Only the secret parameter, so the rest of the URL stays readable.</summary>
    [Fact]
    public void Other_query_parameters_are_left_alone()
    {
        var masked = SecretMasker.Apply("https://x.nl/a?user=martien&token=abc123def456&page=2");

        Assert.Contains("user=martien", masked);
        Assert.Contains("page=2", masked);
        Assert.DoesNotContain("abc123def456", masked);
    }

    /// <summary>
    /// OAuth names every credential it mints "something_token", and an exact-match list knew
    /// none of them: refresh_token, id_token and client_secret all passed through unmasked —
    /// into a table that is kept forever.
    /// </summary>
    [Theory]
    [InlineData("https://x.nl/cb?refresh_token=abc123def456")]
    [InlineData("https://x.nl/cb?id_token=abc123def456")]
    [InlineData("https://x.nl/cb?client_secret=abc123def456")]
    [InlineData("https://x.nl/cb?api-key=abc123def456")]
    [InlineData("https://x.nl/cb?auth_token=abc123def456")]
    [InlineData("https://x.nl/login?pwd=abc123def456")]
    public void A_suffixed_or_prefixed_secret_parameter_is_blanked(string input)
    {
        Assert.DoesNotContain("abc123def456", SecretMasker.Apply(input));
    }

    /// <summary>
    /// The wildcard must not swallow words that merely END in a trigger: "monkey" is not a key
    /// and "postcode" is not a code. Masking those would corrupt exactly the kind of ordinary
    /// content the log exists to preserve.
    /// </summary>
    [Theory]
    [InlineData("https://x.nl/zoo?monkey=aap123456789")]
    [InlineData("https://x.nl/adres?postcode=8355AB&plaats=Giethoorn")]
    [InlineData("https://x.nl/nl?countrycode=NL31633984381")]
    [InlineData("https://x.nl/artikel?author=jdoe12345678")]
    public void A_word_that_merely_ends_in_a_trigger_is_left_alone(string input)
    {
        Assert.Equal(input, SecretMasker.Apply(input));
    }

    [Theory]
    [InlineData("Je verificatiecode is 483920")]
    [InlineData("code: 483920")]
    [InlineData("Pincode 483920")]
    [InlineData("Your code is 483920")]
    [InlineData("otp = 483920")]
    public void A_one_time_code_introduced_by_a_keyword_is_blanked(string input)
    {
        Assert.DoesNotContain("483920", SecretMasker.Apply(input));
    }

    /// <summary>
    /// Grouped digits are how SMS and WhatsApp actually present codes — "483 920" is the
    /// common case, not the corner case. A contiguity requirement let the most common
    /// presentation of the most common secret through unmasked, and "4839-2011" came back as
    /// "***-2011": visibly masked while leaving half the code standing.
    /// </summary>
    [Theory]
    [InlineData("je code is 483 920", "483")]
    [InlineData("jouw code: 4839-2011", "2011")]
    [InlineData("code 48 39 20 11", "48")]
    public void A_grouped_one_time_code_is_blanked_whole(string input, string fragment)
    {
        var masked = SecretMasker.Apply(input);

        Assert.DoesNotContain(fragment, masked);
        Assert.Contains("***", masked);
    }

    /// <summary>
    /// The suffix rule must not absorb a phone number: eleven digits after "code" match
    /// nothing, grouped or not, because every candidate ends adjacent to another digit.
    /// </summary>
    [Theory]
    [InlineData("Bel code-team op 31633984381")]
    [InlineData("code 31633984381")]
    public void A_phone_number_after_the_keyword_is_not_a_code(string input)
    {
        Assert.Equal(input, SecretMasker.Apply(input));
    }

    [Fact]
    public void A_bearer_token_pasted_into_a_message_is_blanked()
    {
        var masked = SecretMasker.Apply("Gebruik header: Bearer eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9");

        Assert.DoesNotContain("eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9", masked);
        Assert.Contains("Bearer ***", masked);
    }

    // ─── Not masked: content must survive intact ─────────────────────────────────────────────

    /// <summary>
    /// The reason every pattern requires a nearby keyword. Amounts, house numbers, order
    /// references, years and phone numbers are all bare digit runs, and a log that quietly
    /// replaced them with *** would misreport what was sent — the exact failure an audit trail
    /// exists to prevent.
    /// </summary>
    [Theory]
    [InlineData("Factuur 202615 staat open, bedrag 10043 euro")]
    [InlineData("Ik ben er om 1400 uur, huisnummer 128")]
    [InlineData("Bel me op 31633984381")]
    [InlineData("De server draait sinds 2026 zonder herstart")]
    [InlineData("Order 8842910 is verzonden")]
    public void An_ordinary_message_with_numbers_is_stored_verbatim(string input)
    {
        Assert.Equal(input, SecretMasker.Apply(input));
    }

    [Theory]
    [InlineData("Deploy naar https://app.bugattiinsights.com/register is klaar")]
    [InlineData("Zie https://portofgiethoorn.com/vaarkaart?zoom=12&lat=52")]
    public void An_ordinary_link_is_stored_verbatim(string input)
    {
        Assert.Equal(input, SecretMasker.Apply(input));
    }

    /// <summary>The word without a code after it is just a word.</summary>
    [Fact]
    public void The_keyword_alone_is_not_enough_to_trigger_masking()
    {
        const string input = "Wat is de code voor de voordeur? Ik ben hem kwijt.";

        Assert.Equal(input, SecretMasker.Apply(input));
    }

    // ─── Degenerate input ────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Null_and_empty_pass_through(string? input)
    {
        Assert.Equal(input, SecretMasker.Apply(input));
    }

    /// <summary>
    /// Several secrets in one body must all go. An early version returned after the first
    /// replacement and left the rest readable.
    /// </summary>
    [Fact]
    public void Every_secret_in_a_body_is_masked_not_just_the_first()
    {
        var masked = SecretMasker.Apply(
            "Goedkeuren https://v.nl/approve/9f2c8ab4d1e7 of https://v.nl/reject/1a2b3c4d5e6f — code 483920");

        Assert.DoesNotContain("9f2c8ab4d1e7", masked);
        Assert.DoesNotContain("1a2b3c4d5e6f", masked);
        Assert.DoesNotContain("483920", masked);
    }
}
