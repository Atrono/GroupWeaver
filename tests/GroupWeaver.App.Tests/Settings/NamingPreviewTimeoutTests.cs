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
}
