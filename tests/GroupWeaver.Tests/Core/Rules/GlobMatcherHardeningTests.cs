using System.Text.RegularExpressions;

using GroupWeaver.Core.Rules;

using Xunit;

namespace GroupWeaver.Tests.Core.Rules;

/// <summary>
/// Defense-in-depth hardening for <see cref="GlobMatcher"/> (issue #52 d/e),
/// kept separate from the glob-dialect pins in <c>GlobMatcherTests</c>:
///
/// <para><b>(d) Bounded cache.</b> <c>GlobMatcher.Cache</c> memoizes one compiled
/// <see cref="Regex"/> per distinct glob. Imported community rulesets feed it
/// untrusted, arbitrarily-many distinct glob strings, so an UNBOUNDED static
/// dictionary is a slow process-lifetime memory-growth vector. The cache must be
/// bounded by a documented cap with simple eviction; the OBSERVABLE invariant this
/// pins is that compiling far more than the cap of distinct globs never lets the
/// cache grow without bound (<c>CacheCount &lt;= CacheCapacity</c>), while memoization
/// of a hot glob still holds within the working set.</para>
///
/// <para><b>(e) Finite MatchTimeout.</b> The compiled regex is
/// <see cref="RegexOptions.NonBacktracking"/> (linear-time today), so a
/// <see cref="Regex.MatchTimeout"/> is moot in practice — but a future pattern path
/// that forgets the flag would have no second line of defense. The compiled regex
/// must therefore carry a finite, positive timeout, never
/// <see cref="Regex.InfiniteMatchTimeout"/>.</para>
///
/// <para>Both invariants are pinned against the implementer-supplied internal surface
/// (<c>InternalsVisibleTo("GroupWeaver.Tests")</c> already exists for Core):
/// <c>GlobMatcher.CacheCapacity</c> (the cap) and <c>GlobMatcher.CacheCount</c> (the
/// live entry count). RED until the bound and the timeout exist.</para>
/// </summary>
public class GlobMatcherHardeningTests
{
    // --- (e) finite MatchTimeout (defense-in-depth under NonBacktracking) --------

    [Fact]
    public void Compile_CarriesFiniteMatchTimeout_NotInfinite()
    {
        var regex = GlobMatcher.Compile("*,CN=Builtin,*");

        // Never Regex.InfiniteMatchTimeout: a future non-NonBacktracking path must
        // still abort a runaway match rather than hang the scan (issue #52 e).
        Assert.NotEqual(Regex.InfiniteMatchTimeout, regex.MatchTimeout);
        Assert.True(regex.MatchTimeout > TimeSpan.Zero, "MatchTimeout must be a finite, positive span.");
    }

    [Fact]
    public void Compile_NonBacktrackingStillSet_TimeoutIsBeltAndSuspenders()
    {
        // The timeout is ADDED to, never a REPLACEMENT for, the linear-time engine:
        // both invariants must hold on the same compiled instance.
        var regex = GlobMatcher.Compile("GG_*");

        Assert.True(regex.Options.HasFlag(RegexOptions.NonBacktracking));
        Assert.NotEqual(Regex.InfiniteMatchTimeout, regex.MatchTimeout);
    }

    // --- (d) the cache is bounded ------------------------------------------------

    [Fact]
    public void CacheCapacity_IsAPositiveDocumentedBound()
    {
        // The cap is a finite positive number; an unbounded cache would have no cap
        // to read at all (this property would not exist) — its mere presence plus a
        // sane value is the contract.
        Assert.True(GlobMatcher.CacheCapacity > 0, "CacheCapacity must be a positive bound.");
    }

    [Fact]
    public void Compile_ManyDistinctGlobs_DoesNotGrowCacheWithoutBound()
    {
        var cap = GlobMatcher.CacheCapacity;

        // Compile well past the cap with all-distinct globs (each is a unique key, so
        // an unbounded cache would retain every one). After eviction settles, the
        // live count must never exceed the cap.
        for (var i = 0; i < (cap * 3) + 50; i++)
        {
            GlobMatcher.Compile($"GG_Distinct_{i}_*");
        }

        Assert.True(
            GlobMatcher.CacheCount <= GlobMatcher.CacheCapacity,
            $"Cache grew to {GlobMatcher.CacheCount}, exceeding cap {GlobMatcher.CacheCapacity}.");
    }

    [Fact]
    public void Compile_HotGlobWithinWorkingSet_StaysMemoized()
    {
        // Memoization must still pay off within the cap: a glob compiled and
        // immediately re-compiled (well inside any working set) returns the SAME
        // instance — the bound must not degrade into compile-every-time.
        var first = GlobMatcher.Compile("HOT_GLOB_*");
        var second = GlobMatcher.Compile("HOT_GLOB_*");

        Assert.Same(first, second);
    }

    [Fact]
    public void CacheCount_NeverExceedsCapacity_AcrossInterleavedCompiles()
    {
        // Interleave repeats of a small hot set with a flood of one-shot globs: the
        // count is checked continuously, so no transient burst may breach the cap.
        for (var i = 0; i < GlobMatcher.CacheCapacity * 2; i++)
        {
            GlobMatcher.Compile("STABLE_A*");
            GlobMatcher.Compile("STABLE_B*");
            GlobMatcher.Compile($"FLOOD_{i}_*");

            Assert.True(
                GlobMatcher.CacheCount <= GlobMatcher.CacheCapacity,
                $"Cache breached cap mid-flood at i={i}: {GlobMatcher.CacheCount} > {GlobMatcher.CacheCapacity}.");
        }
    }
}
