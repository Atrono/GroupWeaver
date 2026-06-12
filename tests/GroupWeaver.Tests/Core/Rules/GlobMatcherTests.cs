using System.Text.RegularExpressions;

using GroupWeaver.Core.Rules;

using Xunit;

namespace GroupWeaver.Tests.Core.Rules;

/// <summary>
/// Pins the glob dialect for ignore/exception entries (ADR-008): everything
/// except <c>*</c>/<c>?</c> is a <c>Regex.Escape</c>'d literal, <c>*</c> = any
/// run of characters (commas included, empty run allowed), <c>?</c> = exactly
/// one character, full-string anchoring (never a substring match),
/// case-insensitive matching, and the compiled regex is linear-time
/// (<see cref="RegexOptions.NonBacktracking"/>) and memoized per glob.
/// </summary>
public class GlobMatcherTests
{
    // --- Literals: regex metachars are escaped -------------------------------

    [Fact]
    public void IsMatch_ParensInLiteral_MatchLiterally()
    {
        Assert.True(GlobMatcher.IsMatch(
            "CN=Anna Acker (u001),*",
            "CN=Anna Acker (u001),OU=Users,OU=AGDLP-Lab,DC=agdlp,DC=lab"));
    }

    [Fact]
    public void Compile_UnbalancedParenInGlob_CompilesAndMatchesLiterally()
    {
        // Without Regex.Escape an unbalanced '(' is an invalid pattern and
        // Regex construction throws; escaped, it is just a character.
        var regex = GlobMatcher.Compile("CN=Group (legacy*");

        Assert.Matches(regex, "CN=Group (legacy),OU=Groups,DC=agdlp,DC=lab");
    }

    [Theory]
    [InlineData("a.c", "a.c", true)]   // dot is literal ...
    [InlineData("a.c", "abc", false)]  // ... not "any single char"
    [InlineData("a+b", "a+b", true)]   // plus is literal ...
    [InlineData("a+b", "aab", false)]  // ... not "one or more"
    [InlineData("[x]", "[x]", true)]   // brackets are literal ...
    [InlineData("[x]", "x", false)]    // ... not a character class
    [InlineData("a|b", "a|b", true)]   // pipe is literal ...
    [InlineData("a|b", "a", false)]    // ... not alternation
    [InlineData("^a$", "^a$", true)]   // anchors are literal characters ...
    [InlineData("^a$", "a", false)]    // ... anchoring comes from the matcher itself
    public void IsMatch_RegexMetacharsInGlob_AreLiteral(string glob, string input, bool expected)
    {
        Assert.Equal(expected, GlobMatcher.IsMatch(glob, input));
    }

    // --- '*' = any run of characters, commas included -------------------------

    [Fact]
    public void IsMatch_StarCrossesCommas_MatchesFullDn()
    {
        Assert.True(GlobMatcher.IsMatch(
            "*,CN=Builtin,*",
            "CN=Print Operators,CN=Builtin,DC=weavedemo,DC=example"));
    }

    [Fact]
    public void IsMatch_Star_MatchesEmptyRun()
    {
        Assert.True(GlobMatcher.IsMatch("GG_X*", "GG_X"));
    }

    // --- '?' = exactly one character -------------------------------------------

    [Theory]
    [InlineData("GG_?", "GG_X", true)]
    [InlineData("GG_?", "GG_", false)]   // zero characters: no match
    [InlineData("GG_?", "GG_XY", false)] // two characters: no match
    [InlineData("u??", "u01", true)]
    [InlineData("u??", "u001", false)]
    public void IsMatch_QuestionMark_MatchesExactlyOneCharacter(string glob, string input, bool expected)
    {
        Assert.Equal(expected, GlobMatcher.IsMatch(glob, input));
    }

    // --- Case-insensitive matching ----------------------------------------------

    [Theory]
    [InlineData("*,cn=builtin,*", "CN=Print Operators,CN=Builtin,DC=weavedemo,DC=example")]
    [InlineData("CN=DOMAIN ADMINS,CN=USERS,*", "cn=domain admins,cn=users,dc=weavedemo,dc=example")]
    public void IsMatch_CaseDiffersOnly_StillMatches(string glob, string input)
    {
        Assert.True(GlobMatcher.IsMatch(glob, input));
    }

    // --- Full anchoring: never a substring match ---------------------------------

    [Fact]
    public void IsMatch_GlobWithoutWildcards_DoesNotMatchSubstring()
    {
        Assert.False(GlobMatcher.IsMatch(
            "CN=Builtin",
            "CN=Print Operators,CN=Builtin,DC=weavedemo,DC=example"));
    }

    [Fact]
    public void IsMatch_StartAnchorHolds_GlobDoesNotFloatToMidString()
    {
        Assert.False(GlobMatcher.IsMatch(
            "CN=Users,*",
            "CN=Domain Admins,CN=Users,DC=weavedemo,DC=example"));
    }

    [Fact]
    public void IsMatch_EndAnchorHolds_TrailingTextDefeatsMatch()
    {
        Assert.False(GlobMatcher.IsMatch("GG_Sales_Staff", "GG_Sales_Staff_Old"));
    }

    [Fact]
    public void IsMatch_ExactLiteral_MatchesItself()
    {
        Assert.True(GlobMatcher.IsMatch("GG_Sales_Staff", "GG_Sales_Staff"));
    }

    // --- Compiled regex options ----------------------------------------------------

    [Fact]
    public void Compile_CarriesNonBacktrackingCultureInvariantIgnoreCase()
    {
        var regex = GlobMatcher.Compile("*,CN=Builtin,*");

        // NonBacktracking = linear-time matching on untrusted community
        // ruleset files (no ReDoS, no backtracking fallback) - ADR-008.
        Assert.True(regex.Options.HasFlag(RegexOptions.NonBacktracking));
        Assert.True(regex.Options.HasFlag(RegexOptions.CultureInvariant));
        Assert.True(regex.Options.HasFlag(RegexOptions.IgnoreCase));
    }

    // --- Memoization -----------------------------------------------------------------

    [Fact]
    public void Compile_SameGlob_ReturnsSameInstance()
    {
        Assert.Same(
            GlobMatcher.Compile("*,CN=Builtin,*"),
            GlobMatcher.Compile("*,CN=Builtin,*"));
    }

    [Fact]
    public void Compile_DifferentGlobs_ReturnDifferentInstances()
    {
        Assert.NotSame(GlobMatcher.Compile("a*"), GlobMatcher.Compile("b*"));
    }

    // --- IsMatch is Compile + IsMatch --------------------------------------------------

    [Fact]
    public void IsMatch_AgreesWithCompiledRegex()
    {
        const string glob = "CN=Domain Admins,CN=Users,*";
        const string dn = "CN=Domain Admins,CN=Users,DC=weavedemo,DC=example";

        Assert.True(GlobMatcher.IsMatch(glob, dn));
        Assert.Equal(GlobMatcher.Compile(glob).IsMatch(dn), GlobMatcher.IsMatch(glob, dn));
    }
}
