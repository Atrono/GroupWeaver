using System.Text.RegularExpressions;

using GroupWeaver.App.Settings;
using GroupWeaver.Core.Rules;

using Xunit;

namespace GroupWeaver.App.Tests.Settings;

/// <summary>
/// Pins the AP 3.3 / S1 live-preview pure helpers (ADR-011 §4, ADR-009): a
/// throwaway, never-interned <see cref="Regex"/> compiled with the engine's
/// EXACT options (<see cref="RegexOptions.NonBacktracking"/> |
/// <see cref="RegexOptions.CultureInvariant"/>, no <c>IgnoreCase</c> — that is
/// glob-only) so a preview verdict equals what <c>RuleEngine.Evaluate</c> would
/// produce for the same naming pattern.
///
/// <para><see cref="NamingPreview.Evaluate(string,string)"/> returns
/// <c>NamingPreviewResult</c> in one of three states: <c>Ok</c> (sample matches),
/// <c>Violation</c> (sample does not match — would be flagged), or
/// <c>PatternInvalid(message)</c> when the pattern cannot be COMPILED under
/// <c>NonBacktracking</c> (lookaround/backreferences throw
/// <see cref="NotSupportedException"/> at construction; malformed patterns throw
/// <see cref="ArgumentException"/>). The "idle" empty-sample affordance is a chip
/// concern (S4 converter), NOT an <c>Evaluate</c> state — <c>Evaluate</c> simply
/// runs <c>IsMatch</c> against the verbatim sample (a <c>^…$</c> pattern does not
/// match the empty string ⇒ <c>Violation</c>).</para>
///
/// <para>The no-static-cache guard: the preview must NOT intern the regex into
/// <c>GlobMatcher.Cache</c> or any static memo (ADR-009 forbids a process-memory
/// leak of untrusted patterns). <c>GlobMatcher.Cache</c> is <c>private</c>, so we
/// pin behavioral independence: two distinct patterns evaluated back-to-back never
/// share state, and the glob preview is unaffected by naming previews.</para>
///
/// Red until <c>src/App/Settings/NamingPreview.cs</c>,
/// <c>NamingPreviewResult.cs</c>, and <c>GlobPreview.cs</c> exist.
/// </summary>
public sealed class NamingPreviewTests
{
    private const string GgPattern = "^GG_.*_(Lesen|Schreiben)$";

    // --- Ok ---------------------------------------------------------------------

    [Fact]
    public void Evaluate_GgPattern_MatchingSample_IsOk()
    {
        var result = NamingPreview.Evaluate(GgPattern, "GG_Vertrieb_Lesen");

        Assert.Equal(NamingPreviewKind.Ok, result.Kind);
    }

    [Theory]
    [InlineData("GG_Vertrieb_Lesen")]
    [InlineData("GG_HR_Schreiben")]
    [InlineData("GG__Lesen")] // ".*" allows an empty middle run
    public void Evaluate_GgPattern_AnyMatchingSample_IsOk(string sample)
    {
        Assert.Equal(NamingPreviewKind.Ok, NamingPreview.Evaluate(GgPattern, sample).Kind);
    }

    // --- Violation --------------------------------------------------------------

    [Fact]
    public void Evaluate_GgPattern_NonMatchingSample_IsViolation()
    {
        var result = NamingPreview.Evaluate(GgPattern, "DL_x");

        Assert.Equal(NamingPreviewKind.Violation, result.Kind);
    }

    [Theory]
    [InlineData("DL_x")]
    [InlineData("GG_Vertrieb_Loeschen")] // wrong permission verb
    [InlineData("gg_Vertrieb_Lesen")] // case-sensitive: lowercase prefix fails
    [InlineData("xGG_Vertrieb_Lesen")] // anchored at start
    [InlineData("GG_Vertrieb_Lesenx")] // anchored at end
    public void Evaluate_GgPattern_AnyNonMatchingSample_IsViolation(string sample)
    {
        Assert.Equal(NamingPreviewKind.Violation, NamingPreview.Evaluate(GgPattern, sample).Kind);
    }

    // --- PatternInvalid: NotSupportedException (lookaround / backreference) ------

    [Fact]
    public void Evaluate_LookbehindPattern_IsPatternInvalid_NonBacktrackingRejectsIt()
    {
        // Lookbehind is unsupported under NonBacktracking — the Regex ctor throws
        // NotSupportedException at construction time, which Evaluate must catch.
        var result = NamingPreview.Evaluate("(?<=x)", "x");

        Assert.Equal(NamingPreviewKind.PatternInvalid, result.Kind);
        Assert.False(string.IsNullOrWhiteSpace(result.Message));
    }

    [Theory]
    [InlineData("(?<=x)foo")] // lookbehind
    [InlineData("(?=foo)bar")] // lookahead
    [InlineData(@"(\w)\1")] // backreference
    public void Evaluate_UnsupportedConstruct_IsPatternInvalid(string pattern)
    {
        Assert.Equal(NamingPreviewKind.PatternInvalid, NamingPreview.Evaluate(pattern, "anything").Kind);
    }

    // --- PatternInvalid: ArgumentException (malformed pattern) -------------------

    [Fact]
    public void Evaluate_MalformedPattern_IsPatternInvalid_WithLoaderPlainTextMessage()
    {
        // "[" is an unterminated character class — the Regex ctor throws
        // ArgumentException; Evaluate surfaces its plain-text Message (#45: this
        // string is rendered verbatim, never as a format template).
        var result = NamingPreview.Evaluate("[", "anything");

        Assert.Equal(NamingPreviewKind.PatternInvalid, result.Kind);
        Assert.False(string.IsNullOrWhiteSpace(result.Message));
    }

    [Fact]
    public void Evaluate_EmptyPattern_IsNotPatternInvalid()
    {
        // An empty pattern is a legal regex (matches everywhere); the
        // empty-pattern state is not a compile failure. Guards against an
        // over-eager IsNullOrEmpty short-circuit that would mask real verdicts.
        var result = NamingPreview.Evaluate(string.Empty, "GG_x");

        Assert.NotEqual(NamingPreviewKind.PatternInvalid, result.Kind);
    }

    // --- case sensitivity: plain prefix vs inline (?i) --------------------------

    [Fact]
    public void Evaluate_IsCaseSensitiveByDefault_PlainPrefixDoesNotMatchOtherCase()
    {
        // No IgnoreCase option (glob-only) ⇒ plain "GG_" must NOT match "gg_x".
        var result = NamingPreview.Evaluate("GG_", "gg_x");

        Assert.Equal(NamingPreviewKind.Violation, result.Kind);
    }

    [Fact]
    public void Evaluate_PlainPrefix_MatchesSameCase()
    {
        Assert.Equal(NamingPreviewKind.Ok, NamingPreview.Evaluate("GG_", "GG_x").Kind);
    }

    [Fact]
    public void Evaluate_InlineIgnoreCaseFlag_MatchesAcrossCase()
    {
        // Inline "(?i)" is honored by the regex engine itself (not the options),
        // so it must flow through to the preview verdict just like production.
        var result = NamingPreview.Evaluate("(?i)gg_", "GG_x");

        Assert.Equal(NamingPreviewKind.Ok, result.Kind);
    }

    // --- empty sample: a chip-"idle" affordance, an Evaluate Violation ----------

    [Fact]
    public void Evaluate_EmptySample_AgainstAnchoredPattern_IsViolation()
    {
        // Evaluate has no "idle" state — that is the S4 chip's concern. A "^…$"
        // pattern simply does not match the empty string ⇒ Violation.
        var result = NamingPreview.Evaluate(GgPattern, string.Empty);

        Assert.Equal(NamingPreviewKind.Violation, result.Kind);
    }

    [Fact]
    public void Evaluate_EmptySample_AgainstEmptyAnchoredPattern_IsOk()
    {
        // "^$" matches exactly the empty string — proves Evaluate runs IsMatch on
        // the verbatim sample rather than short-circuiting empties to a fixed state.
        var result = NamingPreview.Evaluate("^$", string.Empty);

        Assert.Equal(NamingPreviewKind.Ok, result.Kind);
    }

    // --- no static cache / no shared state (ADR-009) ----------------------------

    [Fact]
    public void Evaluate_TwoDistinctPatterns_DoNotShareState()
    {
        // Evaluating one pattern must not leak into the verdict of another. If the
        // helper interned a static regex keyed loosely (or kept a single mutable
        // field), the second call could reuse the first pattern's regex.
        var first = NamingPreview.Evaluate("^A.*$", "ABC");
        var second = NamingPreview.Evaluate("^B.*$", "ABC");

        Assert.Equal(NamingPreviewKind.Ok, first.Kind);
        Assert.Equal(NamingPreviewKind.Violation, second.Kind);

        // And re-running the first still holds (no cross-contamination either way).
        Assert.Equal(NamingPreviewKind.Ok, NamingPreview.Evaluate("^A.*$", "ABC").Kind);
        Assert.Equal(NamingPreviewKind.Violation, NamingPreview.Evaluate("^B.*$", "ABC").Kind);
    }

    [Fact]
    public void Evaluate_RepeatedSamePattern_IsDeterministic()
    {
        // Idempotent across calls — a throwaway regex each time yields the same
        // verdict (no half-initialized static memo).
        for (var i = 0; i < 5; i++)
        {
            Assert.Equal(NamingPreviewKind.Ok, NamingPreview.Evaluate(GgPattern, "GG_Vertrieb_Lesen").Kind);
            Assert.Equal(NamingPreviewKind.Violation, NamingPreview.Evaluate(GgPattern, "DL_x").Kind);
        }
    }

    [Fact]
    public void Evaluate_DoesNotPopulateGlobMatcherCache_GlobPreviewStaysIndependent()
    {
        // GlobMatcher.Cache is private (no count probe), so pin the behavioral
        // invariant: a naming preview over a pattern that is ALSO a syntactically
        // valid glob string must not change how the glob matcher treats that same
        // string. "*" as a regex means "0+ of the previous atom"; as a glob it
        // means "any run". If Evaluate ever fed the regex into GlobMatcher.Cache,
        // the glob semantics for "*" would be corrupted.
        _ = NamingPreview.Evaluate("*", "anything");

        Assert.True(GlobPreview.IsMatch("*", "anything")); // glob "*" matches any input
        Assert.True(GlobPreview.IsMatch("*", string.Empty)); // empty run allowed
        Assert.True(GlobPreview.IsMatch("GG_*", "GG_Vertrieb")); // run after a literal prefix
        Assert.False(GlobPreview.IsMatch("GG_*", "DL_Vertrieb"));
    }

    // --- GlobPreview: a thin pass-through over GlobMatcher.IsMatch ---------------

    [Theory]
    [InlineData("GG_*", "GG_Vertrieb_Lesen", true)]
    [InlineData("GG_*", "DL_Vertrieb_Lesen", false)]
    [InlineData("*_Lesen", "GG_Vertrieb_Lesen", true)]
    [InlineData("GG_?", "GG_x", true)] // "?" is exactly one char
    [InlineData("GG_?", "GG_xy", false)]
    [InlineData("cn=admin*", "CN=Administrator", true)] // glob is case-insensitive
    public void GlobPreview_IsMatch_MirrorsGlobMatcher(string glob, string input, bool expected)
    {
        Assert.Equal(expected, GlobPreview.IsMatch(glob, input));

        // The thin helper must delegate to the engine's own matcher verbatim — no
        // re-implementation that could drift from the rule semantics.
        Assert.Equal(GlobMatcher.IsMatch(glob, input), GlobPreview.IsMatch(glob, input));
    }
}
