namespace GroupWeaver.Core.Plan;

/// <summary>
/// THE single definition of which characters are unsafe in a plan token (an object
/// Name or SamAccountName). Author-time validation (<see cref="PlanModel"/>) and
/// export-time validation (<see cref="PlanScriptExporter"/>) both route through this
/// predicate so they can never drift — issue #77 existed precisely because they had.
///
/// Unsafe = any <see cref="char.IsControl(char)"/> (supersedes the old "c &lt; ' '"
/// test; also catches U+0085 NEL and the C1 range), the line/paragraph separators
/// U+2028/U+2029 (neither is IsControl), and the curly-quote block U+2018..U+201F,
/// which PowerShell's tokenizer honours as string delimiters (the 0.2 audit's
/// single-quote breakout). The ASCII apostrophe U+0027 is NOT unsafe — it is the safe
/// doubled case in <see cref="PlanScriptExporter"/>'s single-quoted literal.
/// </summary>
// Public so the App-layer audit remediation snippet (RemediationSnippet.Clean, WP5f)
// shares this ONE predicate too: both PowerShell-emitting paths weave directory/user
// strings into text, and #77's lesson is that every such site must route through a
// single definition, never a divergent local copy (the guard-predicate-drift class).
public static class PlanText
{
    /// <summary>True if <paramref name="c"/> is unsafe in a plan token.</summary>
    public static bool IsUnsafe(char c) =>
        char.IsControl(c)
        || c == '\u2028'
        || c == '\u2029'
        || (c >= '\u2018' && c <= '\u201F');

    /// <summary>True if <paramref name="value"/> contains any unsafe character.</summary>
    public static bool ContainsUnsafe(string value)
    {
        foreach (var c in value)
        {
            if (IsUnsafe(c))
            {
                return true;
            }
        }

        return false;
    }
}
