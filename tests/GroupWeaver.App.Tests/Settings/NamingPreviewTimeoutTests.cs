using System.Text.RegularExpressions;

using GroupWeaver.App.Settings;

using Xunit;

namespace GroupWeaver.App.Tests.Settings;

/// <summary>
/// Defense-in-depth finite-<see cref="Regex.MatchTimeout"/> for the App-side
/// untrusted-pattern compile site (issue #52 e): <see cref="NamingPreview.Evaluate"/>
/// compiles a community/editor-supplied pattern into a throwaway
/// <c>NonBacktracking | CultureInvariant</c> regex (NamingPreview.cs:41). The regex is
/// never returned, so the per-instance <c>MatchTimeout</c> is not directly observable;
/// this pins the implementer-supplied surface instead — a finite
/// <c>NamingPreview.MatchTimeout</c> constant (requires
/// <c>InternalsVisibleTo("GroupWeaver.App.Tests")</c> on the App project) — and that
/// adding the timeout leaves the three Evaluate verdicts (Ok / Violation /
/// PatternInvalid) unchanged.
///
/// <para>Mirrors the Core pins in <c>GlobMatcherHardeningTests</c> /
/// <c>RegexMatchTimeoutTests</c>. RED until <c>NamingPreview.MatchTimeout</c> exists
/// and the App project exposes its internals to this test assembly.</para>
/// </summary>
public sealed class NamingPreviewTimeoutTests
{
    [Fact]
    public void MatchTimeout_IsFiniteAndPositive_NotInfinite()
    {
        // The throwaway regex must carry a finite abort so a future non-NonBacktracking
        // pattern path cannot hang the per-keystroke preview (issue #52 e).
        Assert.NotEqual(Regex.InfiniteMatchTimeout, NamingPreview.MatchTimeout);
        Assert.True(NamingPreview.MatchTimeout > TimeSpan.Zero, "NamingPreview.MatchTimeout must be finite and positive.");
    }

    [Fact]
    public void Evaluate_WithTimeout_StillProducesTheSameThreeVerdicts()
    {
        // Adding a MatchTimeout is defense-in-depth, NOT a semantic change: the
        // existing verdict contract (pinned in NamingPreviewTests) must be untouched.
        Assert.Equal(NamingPreviewKind.Ok, NamingPreview.Evaluate("^GG_.*$", "GG_Vertrieb").Kind);
        Assert.Equal(NamingPreviewKind.Violation, NamingPreview.Evaluate("^GG_.*$", "DL_x").Kind);
        Assert.Equal(NamingPreviewKind.PatternInvalid, NamingPreview.Evaluate("(?<=x)", "x").Kind);
    }

    // --- Untrusted-pattern DoS: construction-time length cap (issue #112) --------
    //
    // The MatchTimeout above bounds MATCHING only. With RegexOptions.NonBacktracking
    // the `new Regex(...)` builds a DFA whose COST scales with pattern SIZE — the
    // hang is at CONSTRUCTION, before any IsMatch runs, so the timeout cannot help.
    // The Core loader (RulesetLoader.MaxPatternLength = 1000) already rejects
    // over-long patterns BEFORE constructing the Regex (see
    // RulesetLoaderTests.Load_NamingPatternExceedingLengthCap_FailsFastWithValidationError);
    // the App-side live preview lacks the same cap. These tests pin that the preview
    // rejects an over-cap pattern fast (before the seconds-scale DFA construction)
    // with a PatternInvalid result, and that an ordinary in-spec pattern is unchanged.
    //
    // Deliberately asserts against a literal 1000 boundary, NOT
    // GlobMatcher.MaxPatternLength: the App-side guard is pinned independently of the
    // Core constant, so a drift in either surfaces here instead of co-varying silently.

    [Fact]
    public void Evaluate_PatternExceedingLengthCap_IsPatternInvalid_FailsFastBeforeConstruction()
    {
        // A pathological alternation far beyond any real naming regex, comfortably
        // past the 1000-char cap. The preview must reject it on LENGTH without
        // constructing the NonBacktracking Regex (the part that hangs for seconds).
        var huge = "^(?:" + string.Join("|", Enumerable.Range(0, 5000).Select(i => $"GG_{i}")) + ")$";
        Assert.True(huge.Length > 1000, "guard precondition: the pathological pattern must exceed the cap");

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var result = NamingPreview.Evaluate(huge, "GG_Sales");
        sw.Stop();

        // Rejected as an invalid pattern (length), with the loader's wording.
        Assert.Equal(NamingPreviewKind.PatternInvalid, result.Kind);
        Assert.Matches(new Regex("maximum length", RegexOptions.IgnoreCase), result.Message);

        // Fail-fast: rejecting on length must not pay the DFA-construction cost the
        // audit measured in seconds. A generous ceiling keeps this non-flaky.
        Assert.True(
            sw.Elapsed < TimeSpan.FromSeconds(2),
            $"length-capped reject must be fast, took {sw.ElapsedMilliseconds} ms");
    }

    [Fact]
    public void Evaluate_OrdinaryPatternUnderCap_StillEvaluatesNormally()
    {
        // Regression guard against over-rejection: a perfectly ordinary naming
        // pattern (well under the cap) must still produce its real verdict, never
        // a spurious PatternInvalid.
        const string pattern = "^GG_[A-Z][A-Za-z0-9]*$";
        Assert.True(pattern.Length <= 1000, "guard precondition: the ordinary pattern is under the cap");

        Assert.Equal(NamingPreviewKind.Ok, NamingPreview.Evaluate(pattern, "GG_Sales").Kind);
        Assert.Equal(NamingPreviewKind.Violation, NamingPreview.Evaluate(pattern, "DL_Sales").Kind);
    }
}
