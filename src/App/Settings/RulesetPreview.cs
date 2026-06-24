using GroupWeaver.Core.Rules;

namespace GroupWeaver.App.Settings;

/// <summary>
/// The signed direction of a preview delta (WP6b / #164): the editor compares the
/// currently-edited ruleset's demo findings to the DEFAULT ruleset's demo findings.
/// MORE findings than default reads as a <see cref="Caution"/> (the edit relaxed or
/// tightened the audit so the demo dataset trips MORE checks); FEWER or equal reads as
/// <see cref="Neutral"/>. Color is REDUNDANT (WCAG 1.4.1) — the signed number text
/// (<c>+3</c> / <c>-1</c> / <c>0</c>) always carries the meaning; this only drives the
/// emphasis cue.
/// </summary>
public enum PreviewDeltaTone
{
    /// <summary>The count equals or is below the default's — no caution emphasis.</summary>
    Neutral,

    /// <summary>The count is ABOVE the default's — a caution emphasis cue.</summary>
    Caution,
}

/// <summary>
/// One signed delta line in the diff-from-default block (WP6b / #164): a label, the
/// signed count difference (current minus the default baseline), and the redundant
/// <see cref="PreviewDeltaTone"/>. <see cref="DisplayValue"/> is the always-shown
/// signed text (never color-only): <c>+3</c>, <c>-1</c>, or <c>0</c>.
/// </summary>
public sealed record PreviewDelta(string Label, int Value)
{
    /// <summary>More-than-default is the only cautionary direction; equal/fewer is neutral.</summary>
    public PreviewDeltaTone Tone => Value > 0 ? PreviewDeltaTone.Caution : PreviewDeltaTone.Neutral;

    /// <summary>True when this delta reads as a caution (MORE findings than the default) — the
    /// <c>Classes.caution</c> toggle that re-tones the signed text. Color is redundant: the
    /// <see cref="DisplayValue"/> sign always carries the meaning (WCAG 1.4.1).</summary>
    public bool IsCaution => Tone == PreviewDeltaTone.Caution;

    /// <summary>The signed text the UI always shows (color is a redundant cue, never the
    /// sole channel): <c>+n</c> for a rise, <c>-n</c> for a drop, <c>0</c> for no change.</summary>
    public string DisplayValue => Value > 0 ? $"+{Value}" : Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
}

/// <summary>
/// The live finding-count + diff-from-default projection the Settings Advanced tab
/// binds (WP6b / #164): the currently-edited ruleset evaluated over the EMBEDDED DEMO
/// snapshot (<see cref="DemoPreviewSource"/>) via <see cref="RuleEngine.Evaluate"/> +
/// <see cref="AuditSummary.Compute"/>, joined against the DEFAULT ruleset's demo
/// <see cref="AuditSummary"/> (the cached baseline). Pure projection — it neither
/// evaluates rules nor touches a provider; <see cref="Compute"/> just reads two
/// already-computed summaries.
///
/// <para><b>Demo-only, read-only.</b> The preview is ALWAYS over the embedded demo
/// dataset — never the live directory, and it writes nothing. It is a consistent,
/// safe, deterministic baseline for "what would this ruleset flag" in the standalone
/// Settings modal, independent of any live connection.</para>
/// </summary>
public sealed record RulesetPreview(
    int Total,
    int Critical,
    int Warning,
    int Info,
    PreviewDelta TotalDelta,
    PreviewDelta CriticalDelta,
    PreviewDelta WarningDelta,
    PreviewDelta InfoDelta,
    IReadOnlyList<PreviewDelta> RuleClassDeltas)
{
    /// <summary>Joins the edited ruleset's demo <paramref name="current"/> summary against
    /// the default ruleset's demo <paramref name="baseline"/> summary into the bindable
    /// projection. Total = Critical + Warning + Info (the three severity buckets the audit
    /// counts). Per-severity deltas are <c>current - baseline</c>; the per-rule-class deltas
    /// are over the UNION of both summaries' <see cref="AuditSummary.ByRuleClass"/> keys, in
    /// the canonical rule order of <paramref name="ruleset"/> (<see cref="Ruleset.EnumerateRules"/>),
    /// with any class present only in the default appended after. A class whose delta is 0 is
    /// omitted (only changed classes are listed). The default ruleset against itself yields an
    /// all-zero delta set and an empty <see cref="RuleClassDeltas"/>.</summary>
    public static RulesetPreview Compute(AuditSummary current, AuditSummary baseline, Ruleset ruleset)
    {
        var total = current.Critical + current.Warnings + current.Info;
        var baselineTotal = baseline.Critical + baseline.Warnings + baseline.Info;

        var ruleClassDeltas = new List<PreviewDelta>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Canonical rule order first (Nesting -> naming -> circular -> empty-group), then any
        // class that only the default produced (so a rule the edit DISABLED still shows its drop).
        foreach (var summary in ruleset.EnumerateRules())
        {
            AddClassDelta(summary.Id, summary.DisplayName, current, baseline, seen, ruleClassDeltas);
        }

        foreach (var ruleId in baseline.ByRuleClass.Keys)
        {
            AddClassDelta(ruleId, ruleId, current, baseline, seen, ruleClassDeltas);
        }

        return new RulesetPreview(
            Total: total,
            Critical: current.Critical,
            Warning: current.Warnings,
            Info: current.Info,
            TotalDelta: new PreviewDelta("total", total - baselineTotal),
            CriticalDelta: new PreviewDelta("Critical", current.Critical - baseline.Critical),
            WarningDelta: new PreviewDelta("Warning", current.Warnings - baseline.Warnings),
            InfoDelta: new PreviewDelta("Info", current.Info - baseline.Info),
            RuleClassDeltas: ruleClassDeltas);
    }

    private static void AddClassDelta(
        string ruleId,
        string label,
        AuditSummary current,
        AuditSummary baseline,
        HashSet<string> seen,
        List<PreviewDelta> sink)
    {
        if (!seen.Add(ruleId))
        {
            return;
        }

        var currentCount = current.ByRuleClass.TryGetValue(ruleId, out var c) ? c : 0;
        var baselineCount = baseline.ByRuleClass.TryGetValue(ruleId, out var b) ? b : 0;
        var delta = currentCount - baselineCount;
        if (delta != 0)
        {
            sink.Add(new PreviewDelta(label, delta));
        }
    }
}
