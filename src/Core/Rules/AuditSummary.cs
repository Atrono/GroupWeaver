using GroupWeaver.Core.Model;

namespace GroupWeaver.Core.Rules;

/// <summary>
/// The dashboard health roll-up (WP5): a single 0-100 score plus the tile counts
/// and per-rule-class breakdown the AP 3.4 summary header binds. Pure, total,
/// UI-free and deterministic, mirroring <see cref="RuleEngine.Evaluate"/>'s
/// discipline: <see cref="Compute"/> never calls a provider, never mutates its
/// inputs, and never throws on directory CONTENT. Derived entirely from the
/// already-computed <see cref="RuleReport"/> plus a load-state read of the
/// snapshot — it does NOT re-evaluate rules and does NOT read
/// <see cref="DirectorySnapshot.Edges"/> (the O(E)-recompute perf contract,
/// .claude/rules/data-model.md, is untouched).
/// </summary>
/// <param name="Score">The clamped, rounded 0-100 health score (see <see cref="Compute"/>).</param>
/// <param name="Band">The qualitative label for <paramref name="Score"/>:
/// Excellent (>=90), Good (>=75), Fair (>=50), Poor (otherwise).</param>
/// <param name="Critical">Count of <see cref="RuleReport.Violations"/> with
/// <see cref="RuleSeverity.Error"/>.</param>
/// <param name="Warnings">Count of <see cref="RuleReport.Violations"/> with
/// <see cref="RuleSeverity.Warning"/>.</param>
/// <param name="Info">Count of <see cref="RuleReport.Violations"/> with
/// <see cref="RuleSeverity.Info"/> (a sub-count, not part of Critical/Warnings).</param>
/// <param name="Passing">Checked subjects with no finding:
/// <see cref="CheckedSubjects"/> minus the distinct primary DNs
/// (<see cref="RuleViolation.PrimaryDn"/>, <see cref="Dn.Comparer"/>-keyed) across
/// all findings. Never negative; an unexpanded subtree is never counted as
/// Passing.</param>
/// <param name="CheckedSubjects">Objects in <see cref="DirectorySnapshot.Objects"/>
/// that were actually in a rule's judged domain (see <see cref="Compute"/> for the
/// exact definition) — the denominator of <see cref="Score"/>.</param>
/// <param name="RuleClasses">Count of ENABLED rule blocks
/// (<see cref="Ruleset.EnumerateRules"/> with <see cref="RuleSummary.Enabled"/>).</param>
/// <param name="UncheckedPresent"><see cref="RuleReport.UncheckedDns"/> is non-empty —
/// the UI shows the "unexpanded areas are unchecked; the score is over checked
/// objects only" caveat. The score never implies a clean bill over unexpanded
/// subtrees.</param>
/// <param name="ByRuleClass">Findings grouped by <see cref="RuleViolation.RuleId"/> to
/// count, <see cref="StringComparer.OrdinalIgnoreCase"/>-keyed (rule ids are unique
/// case-insensitively, loader-enforced) — the categories pane projection.</param>
public sealed record AuditSummary(
    int Score,
    string Band,
    int Critical,
    int Warnings,
    int Info,
    int Passing,
    int CheckedSubjects,
    int RuleClasses,
    bool UncheckedPresent,
    IReadOnlyDictionary<string, int> ByRuleClass)
{
    /// <summary>Score weight for an <see cref="RuleSeverity.Error"/> finding.</summary>
    public const double ErrorWeight = 3.0;

    /// <summary>Score weight for a <see cref="RuleSeverity.Warning"/> finding.</summary>
    public const double WarningWeight = 1.0;

    /// <summary>Score weight for an <see cref="RuleSeverity.Info"/> finding.</summary>
    public const double InfoWeight = 0.25;

    /// <summary>
    /// Rolls <paramref name="report"/> + the <paramref name="snapshot"/> load state +
    /// <paramref name="ruleset"/> into the summary. Static, pure, total, UI-free; two
    /// calls on identical inputs yield an equal record.
    ///
    /// <para><b>CheckedSubjects</b> is the count of objects in
    /// <see cref="DirectorySnapshot.Objects"/> that were in SOME enabled rule's judged
    /// domain — i.e. actually evaluated, honoring the null-vs-empty tri-state. An object
    /// counts when EITHER:
    /// <list type="bullet">
    /// <item>it is a group kind (GG/DL/UG) whose members were loaded
    /// (<see cref="DirectorySnapshot.IsLoaded"/>) AND any of the loaded-group rules
    /// (nesting, circular, empty-group) is enabled — these rules judge loaded group
    /// parents; an unloaded group is the tri-state's unchecked arm and is NOT counted; OR</item>
    /// <item>its <see cref="AdObject.Kind"/> is targeted by some enabled naming rule —
    /// naming judges named objects regardless of load state (a leaf has no members to
    /// load).</item>
    /// </list>
    /// Each object is counted at most once. With every rule disabled, CheckedSubjects is 0
    /// (the engine evaluated nothing). This deliberately never counts a known-but-unloaded
    /// group as Passing.</para>
    ///
    /// <para><b>Score:</b> <c>raw = 100 - (ErrorWeight*Critical + WarningWeight*Warnings +
    /// InfoWeight*Info) / max(1, CheckedSubjects) * 100</c>; the result is rounded
    /// (away-from-zero) and clamped to [0, 100].</para>
    /// </summary>
    public static AuditSummary Compute(RuleReport report, DirectorySnapshot snapshot, Ruleset ruleset)
    {
        var critical = 0;
        var warnings = 0;
        var info = 0;
        var byRuleClass = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var subjectsWithFindings = new HashSet<string>(Dn.Comparer);

        foreach (var violation in report.Violations)
        {
            switch (violation.Severity)
            {
                case RuleSeverity.Error:
                    critical++;
                    break;
                case RuleSeverity.Warning:
                    warnings++;
                    break;
                default:
                    info++;
                    break;
            }

            byRuleClass[violation.RuleId] = byRuleClass.TryGetValue(violation.RuleId, out var c) ? c + 1 : 1;

            // The primary DN (Dns[0]) is the subject anchor the AP 3.4 sidebar jumps
            // to; distinct primary DNs are the subjects WITH a finding, Dn.Comparer-keyed.
            subjectsWithFindings.Add(violation.PrimaryDn);
        }

        // Which judged domains are live this evaluation. The loaded-group rules
        // (nesting/circular/empty-group) share the same subject domain — a loaded
        // group parent — so any one of them enabled makes loaded groups "checked".
        var loadedGroupRulesEnabled =
            ruleset.Nesting.Enabled || ruleset.Circular.Enabled || ruleset.EmptyGroup.Enabled;
        var enabledNamingKinds = new HashSet<AdObjectKind>();
        foreach (var rule in ruleset.Naming)
        {
            if (rule.Enabled)
            {
                enabledNamingKinds.Add(rule.Kind);
            }
        }

        var checkedSubjects = 0;
        foreach (var obj in snapshot.Objects)
        {
            var loadedGroupChecked =
                loadedGroupRulesEnabled && IsGroupKind(obj.Kind) && snapshot.IsLoaded(obj.Dn);
            var namingChecked = enabledNamingKinds.Contains(obj.Kind);
            if (loadedGroupChecked || namingChecked)
            {
                checkedSubjects++;
            }
        }

        var ruleClasses = ruleset.EnumerateRules().Count(rule => rule.Enabled);

        // Subjects WITH a finding are by construction a subset of the checked
        // subjects (a finding's primary DN is always something an enabled rule
        // judged), so Passing is never negative; Max(0, ...) is belt-and-braces.
        var passing = Math.Max(0, checkedSubjects - subjectsWithFindings.Count);

        var penalty = (ErrorWeight * critical) + (WarningWeight * warnings) + (InfoWeight * info);
        var raw = 100.0 - (penalty / Math.Max(1, checkedSubjects) * 100.0);
        var score = Math.Clamp((int)Math.Round(raw, MidpointRounding.AwayFromZero), 0, 100);

        return new AuditSummary(
            Score: score,
            Band: BandFor(score),
            Critical: critical,
            Warnings: warnings,
            Info: info,
            Passing: passing,
            CheckedSubjects: checkedSubjects,
            RuleClasses: ruleClasses,
            UncheckedPresent: report.UncheckedDns.Count > 0,
            ByRuleClass: byRuleClass);
    }

    /// <summary>The qualitative band for a 0-100 score (pinned thresholds).</summary>
    private static string BandFor(int score) => score switch
    {
        >= 90 => "Excellent",
        >= 75 => "Good",
        >= 50 => "Fair",
        _ => "Poor",
    };

    /// <summary>The real group scopes — the loaded-group rules' judged-domain
    /// subjects (mirrors <see cref="RuleEngine"/>'s notion; External is fetchable
    /// but never a rule subject, ADR-008).</summary>
    private static bool IsGroupKind(AdObjectKind kind) => kind
        is AdObjectKind.GlobalGroup
        or AdObjectKind.DomainLocalGroup
        or AdObjectKind.UniversalGroup;
}
