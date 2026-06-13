using System.Text.RegularExpressions;

namespace GroupWeaver.App.Settings;

/// <summary>
/// Pure, stateless live preview for a naming rule's regex (AP 3.3 / ADR-011 §4):
/// answers "would this pattern flag this sample?" for the settings editor without
/// touching the live <c>RuleEngine</c> or its caches.
///
/// <para>The pattern is compiled into a THROWAWAY <see cref="Regex"/> with the
/// engine's EXACT options — <see cref="RegexOptions.NonBacktracking"/> |
/// <see cref="RegexOptions.CultureInvariant"/> (no <c>IgnoreCase</c>: that is a
/// glob-only flag) — so the verdict equals what <c>RuleEngine.Evaluate</c> would
/// produce (case-sensitive, inline <c>(?i)</c> honored, the evaluated string is the
/// sample verbatim). <see cref="RegexOptions.NonBacktracking"/> makes matching
/// linear-time (no ReDoS on untrusted community patterns). The regex is never
/// interned into <c>GlobMatcher.Cache</c> or any static memo — ADR-009 forbids a
/// process-memory leak of untrusted patterns; it is GC-collected after the call.</para>
///
/// <para>A pattern that cannot be compiled under <c>NonBacktracking</c> —
/// lookaround/backreferences throw <see cref="NotSupportedException"/> at
/// construction, malformed patterns throw <see cref="ArgumentException"/> — yields
/// <see cref="NamingPreviewResult.PatternInvalid"/> carrying the compiler's
/// plain-text message (rendered verbatim, #45).</para>
/// </summary>
public static class NamingPreview
{
    /// <summary>
    /// Compiles <paramref name="pattern"/> as a throwaway
    /// <c>NonBacktracking | CultureInvariant</c> regex and reports whether
    /// <paramref name="sample"/> matches it: <see cref="NamingPreviewResult.Ok"/> on
    /// a full match, <see cref="NamingPreviewResult.Violation"/> when it does not
    /// match, or <see cref="NamingPreviewResult.PatternInvalid"/> when the pattern
    /// itself will not compile.
    /// </summary>
    public static NamingPreviewResult Evaluate(string pattern, string sample)
    {
        Regex rx;
        try
        {
            rx = new Regex(pattern, RegexOptions.NonBacktracking | RegexOptions.CultureInvariant);
        }
        catch (NotSupportedException ex)
        {
            return NamingPreviewResult.PatternInvalid(ex.Message); // lookaround / backreference
        }
        catch (ArgumentException ex)
        {
            return NamingPreviewResult.PatternInvalid(ex.Message); // malformed pattern
        }

        return rx.IsMatch(sample) ? NamingPreviewResult.Ok : NamingPreviewResult.Violation;
    }
}
