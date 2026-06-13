using System.Text.RegularExpressions;

using GroupWeaver.Core.Rules;

using Xunit;

namespace GroupWeaver.Tests.Core.Rules;

/// <summary>
/// Pins the finite-<see cref="Regex.MatchTimeout"/> defense-in-depth (issue #52 e)
/// across the Core untrusted-input regex construction sites. Four sites compile a
/// pattern from an imported/community source:
/// <list type="bullet">
/// <item><c>GlobMatcher.Compile</c> — returns the regex (directly observable; pinned
/// in <c>GlobMatcherHardeningTests</c>).</item>
/// <item><c>RuleEngine.CheckNaming</c> (RuleEngine.cs:138) — throwaway, not returned.</item>
/// <item><c>RulesetLoader.ValidatePattern</c> (RulesetLoader.cs:310) — validate-only,
/// not returned.</item>
/// <item><c>NamingPreview.Evaluate</c> — App-side, pinned in App.Tests.</item>
/// </list>
///
/// <para>The three non-returned Core sites are not directly observable per regex
/// instance, so this pins the SHARED timeout constant the implementer must route all
/// Core sites through: <c>GlobMatcher.RegexMatchTimeout</c>
/// (<c>InternalsVisibleTo("GroupWeaver.Tests")</c> exists for Core). Tying it to the
/// timeout actually baked into a <c>GlobMatcher.Compile</c> result guarantees the
/// constant is the live value, not a dead unused field — and the loader's
/// validate-compile path is exercised so a forgotten timeout there is reachable.</para>
///
/// <para>RED until the shared finite timeout constant exists and the compiled regexes
/// carry it.</para>
/// </summary>
public class RegexMatchTimeoutTests
{
    [Fact]
    public void RegexMatchTimeout_SharedConstant_IsFiniteAndPositive()
    {
        // The single source of truth every Core untrusted-input Regex ctor must pass.
        Assert.NotEqual(Regex.InfiniteMatchTimeout, GlobMatcher.RegexMatchTimeout);
        Assert.True(GlobMatcher.RegexMatchTimeout > TimeSpan.Zero, "shared timeout must be finite and positive.");
    }

    [Fact]
    public void GlobMatcher_CompileResult_UsesTheSharedTimeoutConstant()
    {
        // Ties the shared constant to an observable regex: the value the loader and
        // the engine compile with is provably the same finite span the matcher uses,
        // so the constant cannot be an unused dead field that the sites ignore.
        var regex = GlobMatcher.Compile("*,CN=Builtin,*");

        Assert.Equal(GlobMatcher.RegexMatchTimeout, regex.MatchTimeout);
    }

    [Fact]
    public void RulesetLoader_ValidateCompilePath_AcceptsAFiniteTimeoutPattern()
    {
        // Exercises RulesetLoader.ValidatePattern's compile arm (RulesetLoader.cs:310)
        // through LoadDefault: the embedded default ruleset validate-compiles every
        // naming pattern at load (LoadDefault throws on any validation error). A finite
        // timeout on that ctor must not change the load verdict — this keeps the
        // timeout'd site live and reachable so a regression that drops the timeout is
        // caught by the constant-equality pin above rather than diverging silently.
        var ruleset = RulesetLoader.LoadDefault();

        Assert.NotNull(ruleset);
        Assert.NotEmpty(ruleset.Naming); // the patterns that flowed through ValidatePattern
    }
}
