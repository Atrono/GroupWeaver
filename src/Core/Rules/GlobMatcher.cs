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
    private static readonly ConcurrentDictionary<string, Regex> Cache = new(StringComparer.Ordinal);

    /// <summary>The compiled, anchored regex for <paramref name="glob"/>;
    /// identical globs return the identical cached instance.</summary>
    public static Regex Compile(string glob)
    {
        return Cache.GetOrAdd(glob, static g =>
        {
            var pattern = "^" + Regex.Escape(g).Replace("\\*", ".*").Replace("\\?", ".") + "$";
            return new Regex(
                pattern,
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
        });
    }

    /// <summary>Whether <paramref name="input"/> matches <paramref name="glob"/>
    /// in full (equivalent to <c>Compile(glob).IsMatch(input)</c>).</summary>
    public static bool IsMatch(string glob, string input) => Compile(glob).IsMatch(input);
}
