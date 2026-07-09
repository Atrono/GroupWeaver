using GroupWeaver.Providers;

using Xunit;

namespace GroupWeaver.Tests.Providers;

/// <summary>
/// Pins <see cref="LdapFilter.Escape"/> (ADR-039 D3) — the RFC 4515 §3
/// filter-value escaper that defends the new batched-member-resolution filter
/// composition surface (<c>LdapProvider.BuildDnFilter</c>). Pure unit tests,
/// no AD needed. Backslash must be escaped FIRST so the two-digit hex escape
/// sequences the escaper itself introduces are never re-escaped — several
/// cases here exist specifically to pin that ordering.
/// </summary>
public sealed class LdapFilterTests
{
    // --- Individual metacharacters -------------------------------------------------

    [Fact]
    public void Escape_Backslash_ProducesHex5c()
    {
        Assert.Equal("\\5c", LdapFilter.Escape("\\"));
    }

    [Fact]
    public void Escape_Asterisk_ProducesHex2a()
    {
        Assert.Equal("\\2a", LdapFilter.Escape("*"));
    }

    [Fact]
    public void Escape_OpenParen_ProducesHex28()
    {
        Assert.Equal("\\28", LdapFilter.Escape("("));
    }

    [Fact]
    public void Escape_CloseParen_ProducesHex29()
    {
        Assert.Equal("\\29", LdapFilter.Escape(")"));
    }

    [Fact]
    public void Escape_Nul_ProducesHex00()
    {
        Assert.Equal("\\00", LdapFilter.Escape("\0"));
    }

    // --- Combined / realistic values -------------------------------------------------

    [Fact]
    public void Escape_ValueWithMultipleMetacharacters_EscapesEveryOccurrence()
    {
        // CN=Weird(Name)*\Path -> every metacharacter is individually escaped,
        // in source order, with no double-escaping of the sequences introduced
        // along the way.
        const string input = "CN=Weird(Name)*\\Path";
        const string expected = "CN=Weird\\28Name\\29\\2a\\5cPath";

        Assert.Equal(expected, LdapFilter.Escape(input));
    }

    [Fact]
    public void Escape_PlainDn_NoMetacharacters_PassesThroughUnchanged()
    {
        const string input = "CN=Anna Acker,OU=Users,OU=AGDLP-Lab,DC=agdlp,DC=lab";

        Assert.Equal(input, LdapFilter.Escape(input));
    }

    [Fact]
    public void Escape_EmptyString_ReturnsEmptyString()
    {
        Assert.Equal(string.Empty, LdapFilter.Escape(string.Empty));
    }

    // --- Order-sensitivity: backslash MUST be escaped first --------------------------

    [Fact]
    public void Escape_LiteralBackslashSequenceThatLooksLikeAnEscape_DoesNotDoubleEscape()
    {
        // Input is the two literal characters '\' and '*' (NOT a pre-escaped
        // "\2a" sequence) immediately followed by a literal '*'. If '*' were
        // escaped before '\', the resulting "\2a" text would then have its
        // backslash escaped too, producing "\5c2a2a" instead of the correct
        // "\5c2a\2a". Escaping '\' first yields "\5c" once, then the '*' (both
        // occurrences) are escaped independently and correctly.
        const string input = "\\**";
        const string expected = "\\5c\\2a\\2a";

        Assert.Equal(expected, LdapFilter.Escape(input));
    }

    [Fact]
    public void Escape_LiteralTextSpellingOutAnEscapeSequence_BackslashIsEscapedNotTheDigits()
    {
        // Input is the literal FOUR characters '\', '2', 'a' i.e. someone's RDN
        // text literally contains the substring "\2a" (not an actual asterisk).
        // Escaping backslash first turns the leading '\' into "\5c", and the
        // literal digits "2a" pass through untouched -> "\5c2a", NOT a
        // filter-meaningful "\2a" escape (which would wrongly represent a
        // literal '*' that was never in the input).
        const string input = "\\2a";
        const string expected = "\\5c2a";

        Assert.Equal(expected, LdapFilter.Escape(input));
    }
}
