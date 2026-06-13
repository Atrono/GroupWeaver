using GroupWeaver.Core.Model;

namespace GroupWeaver.Core.Rules;

/// <summary>
/// Outcome of one <c>RuleEngine.Evaluate</c> (ADR-009). Immutable; per-DN
/// indexes are built once in the constructor, keyed via <see cref="Dn.Comparer"/>.
/// </summary>
public sealed class RuleReport
{
    private readonly Dictionary<string, List<RuleViolation>> _violationsByDn;

    /// <summary>Convenience for VM initialization (AP 3.4): no findings, empty frontier.</summary>
    public static RuleReport Empty { get; } = new(Array.Empty<RuleViolation>(), Array.Empty<string>());

    /// <summary>Builds the report. <paramref name="violations"/> is stored verbatim —
    /// the engine hands canonical report order (see <see cref="RuleViolationComparer"/>),
    /// the constructor never reshuffles. <paramref name="uncheckedDns"/> is deduped
    /// (<see cref="Dn.Comparer"/>) and sorted OrdinalIgnoreCase here, enforcing the
    /// documented invariant at the single choke point.</summary>
    public RuleReport(IReadOnlyList<RuleViolation> violations, IReadOnlyList<string> uncheckedDns)
    {
        Violations = violations.ToArray();
        UncheckedDns = uncheckedDns
            .Distinct(Dn.Comparer)
            .OrderBy(dn => dn, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        // One pass over all attached DNs builds both per-DN indexes. The first
        // encountered spelling becomes the key; case-variant respellings of the
        // same DN aggregate under it (Dn.Comparer keying).
        var maxSeverityByDn = new Dictionary<string, RuleSeverity>(Dn.Comparer);
        _violationsByDn = new Dictionary<string, List<RuleViolation>>(Dn.Comparer);
        foreach (var violation in Violations)
        {
            foreach (var dn in violation.Dns)
            {
                if (!maxSeverityByDn.TryGetValue(dn, out var severity) || violation.Severity > severity)
                {
                    maxSeverityByDn[dn] = violation.Severity;
                }

                if (!_violationsByDn.TryGetValue(dn, out var attached))
                {
                    attached = new List<RuleViolation>();
                    _violationsByDn[dn] = attached;
                }

                // A finding attaches to a DN once, even when its Dns list names
                // the DN twice (nesting self-edge [A, A]). Violations are
                // processed in order, so a repeat is always the last element.
                if (attached.Count == 0 || !ReferenceEquals(attached[^1], violation))
                {
                    attached.Add(violation);
                }
            }
        }

        MaxSeverityByDn = maxSeverityByDn;
    }

    /// <summary>All findings in canonical report order (see <see cref="RuleViolationComparer"/>).</summary>
    public IReadOnlyList<RuleViolation> Violations { get; }

    /// <summary>"Unexpanded areas are unchecked": every fetchable DN whose members were
    /// never loaded. Deduped (<see cref="Dn.Comparer"/>), sorted OrdinalIgnoreCase.
    /// NEVER filtered by ignore/exceptions — load-state truth, not a judgment.
    /// ALWAYS computed, even with every rule disabled.</summary>
    public IReadOnlyList<string> UncheckedDns { get; }

    /// <summary>AP 3.4 node indicator: max severity per attached DN (every DN occurring
    /// in any <c>Violations[i].Dns</c> — including raw DNs absent from the snapshot, which
    /// GraphBuilder renders as synthetic External nodes). <see cref="Dn.Comparer"/>-keyed.</summary>
    public IReadOnlyDictionary<string, RuleSeverity> MaxSeverityByDn { get; }

    /// <summary>Findings attached to one DN, in report order. Unknown DN → empty list,
    /// never null, never throws. <see cref="Dn.Comparer"/> lookup (case-variant DNs hit).</summary>
    public IReadOnlyList<RuleViolation> ViolationsFor(string dn) =>
        _violationsByDn.TryGetValue(dn, out var attached) ? attached : Array.Empty<RuleViolation>();

    /// <summary>AP 3.4 roll-up primitive ("n below" badge + its max-severity color):
    /// the DISTINCT findings attached to any of <paramref name="dns"/>, in report order
    /// (violations are singleton instances within a report — reference-distinct).</summary>
    public IReadOnlyList<RuleViolation> ViolationsAmong(IEnumerable<string> dns)
    {
        var queried = new HashSet<string>(dns, Dn.Comparer);
        if (queried.Count == 0 || Violations.Count == 0)
        {
            return Array.Empty<RuleViolation>();
        }

        var result = new List<RuleViolation>();
        foreach (var violation in Violations)
        {
            foreach (var dn in violation.Dns)
            {
                if (queried.Contains(dn))
                {
                    result.Add(violation);
                    break;
                }
            }
        }

        return result;
    }
}

/// <summary>
/// THE canonical report order (ADR-009, pinned): blocks in
/// <see cref="Ruleset.EnumerateRules"/> order — nesting, each naming rule in
/// file order, circular, empty-group — then element-wise
/// <see cref="StringComparer.OrdinalIgnoreCase"/> over <see cref="RuleViolation.Dns"/>,
/// shorter prefix first. Never depends on dictionary or insertion order; AP 3.3
/// live preview diffs the list and the AP 3.4 sidebar binds it unshuffled.
/// </summary>
public sealed class RuleViolationComparer : IComparer<RuleViolation>
{
    private readonly Dictionary<string, int> _blockIndexByRuleId;

    /// <summary>Creates the comparer for one ruleset's block order; the engine derives
    /// <paramref name="ruleIdsInReportOrder"/> via <c>ruleset.EnumerateRules()
    /// .Select(r => r.Id)</c>. Ids are unique case-insensitively (loader-enforced);
    /// a duplicate here is a programming error and throws.</summary>
    public RuleViolationComparer(IEnumerable<string> ruleIdsInReportOrder)
    {
        _blockIndexByRuleId = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var ruleId in ruleIdsInReportOrder)
        {
            _blockIndexByRuleId.Add(ruleId, _blockIndexByRuleId.Count);
        }
    }

    /// <summary>Block index, then element-wise OrdinalIgnoreCase over Dns, then
    /// shorter-prefix-first. A rule id absent from the construction list is a
    /// programming error (the engine sorts only findings it produced) and throws.</summary>
    public int Compare(RuleViolation? x, RuleViolation? y)
    {
        if (ReferenceEquals(x, y))
        {
            return 0;
        }

        if (x is null)
        {
            return -1;
        }

        if (y is null)
        {
            return 1;
        }

        var byBlock = _blockIndexByRuleId[x.RuleId].CompareTo(_blockIndexByRuleId[y.RuleId]);
        if (byBlock != 0)
        {
            return byBlock;
        }

        var shared = Math.Min(x.Dns.Count, y.Dns.Count);
        for (var i = 0; i < shared; i++)
        {
            var byElement = StringComparer.OrdinalIgnoreCase.Compare(x.Dns[i], y.Dns[i]);
            if (byElement != 0)
            {
                return byElement;
            }
        }

        return x.Dns.Count.CompareTo(y.Dns.Count);
    }
}
