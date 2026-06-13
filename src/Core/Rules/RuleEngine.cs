using GroupWeaver.Core.Model;

namespace GroupWeaver.Core.Rules;

/// <summary>
/// THE rule evaluation (ADR-009, AP 3.2): pure, stateless, synchronous, UI-free.
/// Never mutates the snapshot, never calls a provider, and never throws on
/// directory CONTENT — unresolvable DNs are values (External kind / raw-DN
/// suppression channel). Precondition: a loader-validated ruleset — a hand-built
/// ruleset with an unsupported regex lets the Regex constructor exception
/// propagate (programming error, not input error). No scope root and no
/// incrementality: the snapshot IS the scope, kinds are re-resolved every call,
/// and consumers re-run Evaluate per lazy expand (AP 2.3/3.4) and per ruleset
/// edit (AP 3.3 live preview). Complexity (V objects, E edges, S distinct loaded
/// parents, R naming rules): nesting O(E), naming O(V*R) with linear-time
/// regexes, empty-group O(V), circular typically O(V+E) and O(S*(V+E)) worst
/// case — milliseconds at the 10K-edge target.
/// </summary>
public static class RuleEngine
{
    /// <summary>Evaluates <paramref name="ruleset"/> against
    /// <paramref name="snapshot"/>. Two calls on identical inputs yield
    /// projection-identical reports (pinned ordering contract,
    /// <see cref="RuleViolationComparer"/>).</summary>
    public static RuleReport Evaluate(DirectorySnapshot snapshot, Ruleset ruleset)
    {
        // THE single read of snapshot.Edges per Evaluate (ADR-009): the property
        // recomputes O(E) on every access (.claude/rules/data-model.md). The
        // nesting check iterates this local and the circular sweep derives its
        // start set from it — nothing else may touch the property.
        var edges = snapshot.Edges;

        var violations = new List<RuleViolation>();
        CheckNesting(snapshot, ruleset, edges, violations);
        CheckNaming(snapshot, ruleset, violations);
        var uncheckedDns = SweepCircularAndFrontier(snapshot, ruleset, edges, violations);
        CheckEmptyGroups(snapshot, ruleset, violations);

        // Canonical report order (ADR-009): EnumerateRules() block order, then
        // element-wise OrdinalIgnoreCase over Dns — never insertion or dictionary
        // order. OrderBy is a stable sort; ties cannot occur anyway, because every
        // evaluator emits at most one finding per Dn.Comparer-distinct
        // subject/edge/cycle within its block.
        var order = new RuleViolationComparer(ruleset.EnumerateRules().Select(rule => rule.Id));
        return new RuleReport(violations.OrderBy(v => v, order).ToList(), uncheckedDns);
    }

    /// <summary>Nesting-matrix check over the local edges copy.</summary>
    private static void CheckNesting(
        DirectorySnapshot snapshot,
        Ruleset ruleset,
        IReadOnlyCollection<MembershipEdge> edges,
        List<RuleViolation> violations)
    {
        // TODO(AP 3.2 S4): judged domain = edges whose parent kind is GG/DL/UG;
        // member kind via GetKind (absent => External column, never skipped);
        // missing row/column fails closed via NestingRule.Unlisted; severity =
        // Cell.SeverityOverride ?? Nesting.Severity; global ignore on EITHER
        // endpoint via the dual-channel DN helper, then endpoint-narrowed
        // Nesting.Exceptions. Finding: Dns = [ParentDn, ChildDn] as stored.
    }

    /// <summary>Naming checks, one block per rule in file order.</summary>
    private static void CheckNaming(DirectorySnapshot snapshot, Ruleset ruleset, List<RuleViolation> violations)
    {
        // TODO(AP 3.2 S3): per enabled naming rule, compile the pattern once per
        // Evaluate call (NonBacktracking | CultureInvariant — no static memo, so
        // AP 3.3 preview keystrokes never intern into process memory); subjects =
        // snapshot objects of rule.Kind; evaluated string = SamAccountName ?? Name;
        // suppression via the object channel (global ignore, then rule.Exceptions).
    }

    /// <summary>Circular check plus the frontier sweep — the ONLY transitive
    /// traversal is <c>MembershipTraversal.Walk</c>. The sweep ALWAYS runs (it
    /// feeds <see cref="RuleReport.UncheckedDns"/>); only cycle-to-finding
    /// conversion is gated on <c>Circular.Enabled</c>. Returns the unchecked DNs
    /// (never ignore-filtered — load-state truth, not a judgment).</summary>
    private static IReadOnlyList<string> SweepCircularAndFrontier(
        DirectorySnapshot snapshot,
        Ruleset ruleset,
        IReadOnlyCollection<MembershipEdge> edges,
        List<RuleViolation> violations)
    {
        // TODO(AP 3.2 S5): starts = distinct edge parents (Dn.Comparer), sorted
        // OrdinalIgnoreCase; Walk per unseen start with one cumulative seen set;
        // canonical cycle = rotation to the Dn.Comparer-minimal DN (never
        // reversal), deduped element-wise under Dn.Comparer; suppression on ANY
        // cycle DN (dual-channel). UncheckedDns = walk frontiers + the O(V) scan
        // of in-snapshot fetchable kinds with GetMembers == null.
        return Array.Empty<string>();
    }

    /// <summary>Empty-group check (ADR-009): subjects are snapshot OBJECTS of a
    /// real group kind whose members are loaded and genuinely empty. <c>null</c>
    /// members = the tri-state's unchecked arm (surfaces in the frontier, never
    /// as a finding); a loaded-and-empty parent absent from
    /// <see cref="DirectorySnapshot.Objects"/> has unknown kind and is never a
    /// subject. Finding: Dns = [subjectDn] in the object's stored spelling.</summary>
    private static void CheckEmptyGroups(DirectorySnapshot snapshot, Ruleset ruleset, List<RuleViolation> violations)
    {
        if (!ruleset.EmptyGroup.Enabled)
        {
            return;
        }

        foreach (var obj in snapshot.Objects)
        {
            if (!IsGroupKind(obj.Kind) || snapshot.GetMembers(obj.Dn) is not { Count: 0 })
            {
                continue;
            }

            // Suppression order (ADR-008): global ignore, then per-rule
            // exceptions — object-subject rules use the object channel directly.
            if (MatchesAny(ruleset.Ignore, obj) || MatchesAny(ruleset.EmptyGroup.Exceptions, obj))
            {
                continue;
            }

            violations.Add(new RuleViolation
            {
                RuleId = RuleIds.EmptyGroup,
                Severity = ruleset.EmptyGroup.Severity,
                Dns = [obj.Dn],
                // Message templates are deterministic English one-liners (ADR-009);
                // string-only interpolation is culture-invariant by construction.
                Message = $"Group '{obj.Name}' has no members.",
            });
        }
    }

    /// <summary>The object suppression channel: whether any entry matches the
    /// snapshot object (dn entries against its Dn, name entries against its
    /// Name or SamAccountName).</summary>
    private static bool MatchesAny(IReadOnlyList<MatchEntry> entries, AdObject obj)
    {
        foreach (var entry in entries)
        {
            if (entry.Matches(obj))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>The dual-channel DN suppression test (ADR-009) for edge endpoints
    /// and cycle DNs: a DN that resolves in the snapshot is matched as the OBJECT
    /// (dn and name entries both apply); a raw DN absent from the snapshot is
    /// matched via <see cref="MatchEntry.MatchesDn"/> only — name entries never
    /// match raw DNs.</summary>
    private static bool MatchesAny(IReadOnlyList<MatchEntry> entries, DirectorySnapshot snapshot, string dn)
    {
        if (snapshot.TryGetObject(dn, out var obj))
        {
            return MatchesAny(entries, obj);
        }

        foreach (var entry in entries)
        {
            if (entry.MatchesDn(dn))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>The rule-subject group kinds (empty-group subjects, nesting
    /// judged-domain parents): real group scopes only — External is fetchable
    /// but never a rule subject (ADR-008).</summary>
    private static bool IsGroupKind(AdObjectKind kind) => kind
        is AdObjectKind.GlobalGroup
        or AdObjectKind.DomainLocalGroup
        or AdObjectKind.UniversalGroup;
}
