using System.Collections.Concurrent;
using System.Text.RegularExpressions;

namespace GroupWeaver.Core.Rules;

/// <summary>
/// Glob dialect for ignore/exception entries (ADR-008): the glob is
/// <see cref="Regex.Escape(string)"/>'d, then <c>*</c> becomes any run of
/// characters (commas included, empty run allowed) and <c>?</c> exactly one
/// character; everything else is literal. Matching is full-string anchored
/// (<c>^…$</c>), case-insensitive, culture-invariant, and linear-time
/// (<see cref="RegexOptions.NonBacktracking"/> — no ReDoS on untrusted
/// community ruleset files). Compiled regexes are memoized per glob so rule
/// records stay pure data while repeated matching stays cheap.
/// </summary>
public static class GlobMatcher
{
    /// <summary>Hard cap on distinct memoized globs (issue #52 d). Imported community
    /// rulesets feed untrusted, arbitrarily-many distinct glob strings; without a bound
    /// the static cache is a slow process-lifetime memory-growth vector. When the cap is
    /// reached the cache is cleared wholesale before the next distinct glob is interned —
    /// dirt-simple eviction that strictly bounds the count and never lets concurrent
    /// readers observe more than the cap, while a hot working set well inside it stays
    /// memoized.</summary>
    internal const int CacheCapacity = 4096;

    /// <summary>Belt-and-suspenders abort for every untrusted-pattern regex (issue #52 e).
    /// Matching is already <see cref="RegexOptions.NonBacktracking"/> (linear-time), so
    /// this never fires today — but a future pattern path that forgets the flag must still
    /// abort a runaway match rather than hang the scan. The single source of truth every
    /// Core untrusted-input <see cref="Regex"/> ctor routes through (RuleEngine, RulesetLoader).</summary>
    internal static readonly TimeSpan RegexMatchTimeout = TimeSpan.FromSeconds(2);

    private static readonly ConcurrentDictionary<string, Regex> Cache = new(StringComparer.Ordinal);

    /// <summary>Live count of memoized globs; never exceeds <see cref="CacheCapacity"/>.</summary>
    internal static int CacheCount => Cache.Count;

    /// <summary>The compiled, anchored regex for <paramref name="glob"/>;
    /// identical globs return the identical cached instance.</summary>
    public static Regex Compile(string glob)
    {
        // Evict-on-cap before interning a new distinct glob so the count is bounded at
        // every observation point. A racing thread may clear after another's check, but
        // the worst case is one extra Clear — the cap is never breached.
        if (Cache.Count >= CacheCapacity && !Cache.ContainsKey(glob))
        {
            Cache.Clear();
        }

        return Cache.GetOrAdd(glob, static g =>
        {
            var pattern = "^" + Regex.Escape(g).Replace("\\*", ".*").Replace("\\?", ".") + "$";
            return new Regex(
                pattern,
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking,
                RegexMatchTimeout);
        });
    }

    /// <summary>Whether <paramref name="input"/> matches <paramref name="glob"/>
    /// in full (equivalent to <c>Compile(glob).IsMatch(input)</c>).</summary>
    public static bool IsMatch(string glob, string input) => Compile(glob).IsMatch(input);
}
