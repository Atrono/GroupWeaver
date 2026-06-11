using System.Globalization;

namespace GroupWeaver.Providers;

/// <summary>
/// Parses and builds LDAP ranged-retrieval attribute names for <c>member</c>
/// (AD caps a single read at 1500 values and returns the rest via
/// <c>member;range=&lt;low&gt;-&lt;high&gt;</c> follow-up reads).
/// </summary>
internal static class MemberRangeParser
{
    private const string Prefix = "member;range=";

    /// <summary>
    /// Parses a ranged <c>member</c> attribute name of the exact shape
    /// <c>member;range=&lt;low&gt;-&lt;high&gt;</c> or
    /// <c>member;range=&lt;low&gt;-*</c>.
    /// <para>Semantics (binding):</para>
    /// <list type="bullet">
    /// <item>The <c>member;range=</c> prefix is matched case-insensitively
    /// (<c>MEMBER;Range=0-10</c> parses).</item>
    /// <item>On success, <paramref name="start"/> and <paramref name="end"/> are
    /// the INCLUSIVE zero-based value indices exactly as returned by AD
    /// (<c>member;range=0-1499</c> → start 0, end 1499);
    /// <paramref name="end"/> is <c>null</c> when the high bound is <c>*</c>
    /// (the final range, containing all remaining values).</item>
    /// <item>Bounds must be plain decimal digits (invariant, no sign, no
    /// whitespace, no decimals); <c>*</c> is the only non-numeric high bound.</item>
    /// <item>Returns <c>false</c> — with <paramref name="start"/> 0 and
    /// <paramref name="end"/> <c>null</c> — for everything else: a plain
    /// <c>member</c>, any other attribute, a missing <c>=</c> or <c>-</c>,
    /// non-numeric or negative bounds (<c>member;range=x-y</c>), an empty bound,
    /// extra options after the range, integer overflow, or
    /// <c>high &lt; low</c>.</item>
    /// </list>
    /// </summary>
    /// <param name="attributeName">The attribute name as returned by the directory.</param>
    /// <param name="start">Inclusive index of the first value covered by the range.</param>
    /// <param name="end">Inclusive index of the last value, or <c>null</c> for <c>*</c>.</param>
    public static bool TryParse(string attributeName, out int start, out int? end)
    {
        start = 0;
        end = null;
        if (!attributeName.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        ReadOnlySpan<char> range = attributeName.AsSpan(Prefix.Length);
        int dash = range.IndexOf('-');
        if (dash < 0 ||
            !int.TryParse(range[..dash], NumberStyles.None, CultureInfo.InvariantCulture, out int low))
        {
            return false;
        }

        ReadOnlySpan<char> highPart = range[(dash + 1)..];
        if (highPart is "*")
        {
            start = low;
            return true;
        }

        if (!int.TryParse(highPart, NumberStyles.None, CultureInfo.InvariantCulture, out int high) ||
            high < low)
        {
            return false;
        }

        start = low;
        end = high;
        return true;
    }

    /// <summary>
    /// The attribute name requesting the next range: <c>member;range={nextStart}-*</c>
    /// (invariant). <paramref name="nextStart"/> must be non-negative.
    /// </summary>
    public static string NextRangeAttribute(int nextStart)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(nextStart);
        return string.Create(CultureInfo.InvariantCulture, $"member;range={nextStart}-*");
    }
}
