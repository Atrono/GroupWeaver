using System.Text.RegularExpressions;

using GroupWeaver.Core.Graph;
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

    /// <summary>Nesting-matrix check over the local edges copy (ADR-009).
    /// Judged domain: only edges whose PARENT kind is a real group scope —
    /// OU containment, user/computer parents, and loaded parents absent from
    /// <see cref="DirectorySnapshot.Objects"/> (GetKind == External) are out.
    /// A member DN absent from the snapshot is External by definition and is
    /// judged under the External COLUMN — the raw builtin/FSP edge is never
    /// skipped. Missing matrix row OR column falls back to
    /// <see cref="NestingRule.Unlisted"/> (fails closed); a self-edge A→A is a
    /// normal cell lookup (circularity belongs to the circular rule). Finding:
    /// Dns = [ParentDn, ChildDn] in the EDGE's stored spellings.</summary>
    private static void CheckNesting(
        DirectorySnapshot snapshot,
        Ruleset ruleset,
        IReadOnlyCollection<MembershipEdge> edges,
        List<RuleViolation> violations)
    {
        var nesting = ruleset.Nesting;
        if (!nesting.Enabled)
        {
            return;
        }

        foreach (var edge in edges)
        {
            var parentKind = snapshot.GetKind(edge.ParentDn);
            if (!IsGroupKind(parentKind))
            {
                continue;
            }

            var memberKind = snapshot.GetKind(edge.ChildDn);
            var cell = nesting.Cell(parentKind, memberKind);
            if (cell.Allowed)
            {
                continue;
            }

            // Suppression order is global ignore -> per-rule exceptions -> check
            // (ADR-008 §5); evaluating the CELL first and consulting suppression
            // only on non-allow cells is observationally equivalent — suppressing
            // a non-finding is a no-op — and skips all glob work on the
            // allow-dominated bulk of a conformant directory. Global ignore
            // exempts the edge when EITHER endpoint matches (dual channel);
            // exceptions honor endpoint narrowing.
            if (MatchesAny(ruleset.Ignore, snapshot, edge.ParentDn)
                || MatchesAny(ruleset.Ignore, snapshot, edge.ChildDn)
                || IsExceptedEdge(nesting.Exceptions, snapshot, edge))
            {
                continue;
            }

            var parentWord = KindWord(parentKind);
            violations.Add(new RuleViolation
            {
                RuleId = RuleIds.Nesting,
                Severity = cell.SeverityOverride ?? nesting.Severity,
                Dns = [edge.ParentDn, edge.ChildDn],
                // Endpoints are named by Name when they resolve in the snapshot,
                // else by the raw DN verbatim; string-only interpolation is
                // culture-invariant by construction. The parent kind word opens
                // the sentence (always a group word — judged domain).
                Message = $"{char.ToUpperInvariant(parentWord[0])}{parentWord[1..]} '{DisplayName(snapshot, edge.ParentDn)}' contains {KindWord(memberKind)} '{DisplayName(snapshot, edge.ChildDn)}' - denied by the nesting matrix.",
            });
        }
    }

    /// <summary>Naming checks, one block per rule in file order (ADR-009):
    /// subjects are the snapshot objects of <c>rule.Kind</c> (the loader forbids
    /// External targets); the evaluated string is <c>SamAccountName ?? Name</c>.
    /// A disabled rule yields zero findings AND skipped work — its pattern is
    /// never compiled. An unsupported pattern in a hand-built ruleset lets the
    /// Regex constructor exception propagate (programming error, not input
    /// error — the loader validates user files).</summary>
    private static void CheckNaming(DirectorySnapshot snapshot, Ruleset ruleset, List<RuleViolation> violations)
    {
        foreach (var rule in ruleset.Naming)
        {
            if (!rule.Enabled)
            {
                continue;
            }

            // Compiled once per ENABLED rule per Evaluate call, never memoized
            // statically (ADR-009) — AP 3.3's per-keystroke preview patterns
            // must not intern into process memory. NonBacktracking keeps
            // matching linear-time on untrusted files; the pattern is anchored
            // as written and case-SENSITIVE (inline (?i) supported).
            var regex = new Regex(rule.Pattern, RegexOptions.NonBacktracking | RegexOptions.CultureInvariant);

            foreach (var obj in snapshot.Objects)
            {
                if (obj.Kind != rule.Kind)
                {
                    continue;
                }

                var evaluated = obj.SamAccountName ?? obj.Name;
                if (regex.IsMatch(evaluated))
                {
                    continue;
                }

                // Suppression order (ADR-008): global ignore, then per-rule
                // exceptions — object-subject rules use the object channel
                // directly. Testing the pattern first is observationally
                // equivalent (suppressing a non-finding is a no-op).
                if (MatchesAny(ruleset.Ignore, obj) || MatchesAny(rule.Exceptions, obj))
                {
                    continue;
                }

                violations.Add(new RuleViolation
                {
                    RuleId = rule.Id,
                    Severity = rule.Severity,
                    Dns = [obj.Dn],
                    // Names the EVALUATED string (sam when present), never the
                    // un-judged other field; string-only interpolation is
                    // culture-invariant by construction.
                    Message = $"Name '{evaluated}' does not match pattern '{rule.Pattern}'.",
                });
            }
        }
    }

    /// <summary>Circular check plus the frontier sweep — the ONLY transitive
    /// traversal is <see cref="MembershipTraversal.Walk"/> (ADR-006/009).
    /// Starts are the DISTINCT parents of the local edges copy (every cycle
    /// node has an out-edge, so it is a loaded parent — this also roots cycles
    /// among loaded parents absent from <see cref="DirectorySnapshot.Objects"/>),
    /// sorted OrdinalIgnoreCase and walked with ONE cumulative seen set.
    /// Findings are deduped canonical back-edge cycles (Walk's pinned
    /// decomposition), never an exhaustive simple-cycle enumeration; a cycle is
    /// suppressed when ANY of its DNs matches the global ignore or
    /// <c>Circular.Exceptions</c> (dual channel). The sweep ALWAYS runs (it
    /// feeds <see cref="RuleReport.UncheckedDns"/>); only cycle-to-finding
    /// conversion is gated on <c>Circular.Enabled</c>. Returns the unchecked
    /// DNs — every walk's frontier plus the O(V) load-state scan for unloaded
    /// in-snapshot fetchables that no walk reaches (LdapProvider's
    /// vanished-group arm produces exactly these) — NEVER ignore-filtered
    /// (load-state truth, not a judgment); the <see cref="RuleReport"/>
    /// constructor is the dedup/sort choke point. Complexity (ADR-009):
    /// typically O(V+E); worst case O(S·(V+E)) over S distinct loaded parents
    /// (Walk is memoryless across calls — accepted at the v0.1 scale, the fix
    /// would be a Walk API change under ADR review, never a second walk).</summary>
    private static IReadOnlyList<string> SweepCircularAndFrontier(
        DirectorySnapshot snapshot,
        Ruleset ruleset,
        IReadOnlyCollection<MembershipEdge> edges,
        List<RuleViolation> violations)
    {
        var circular = ruleset.Circular;
        var seen = new HashSet<string>(Dn.Comparer);
        var reported = new HashSet<IReadOnlyList<string>>(DnSequenceComparer.Instance);
        var uncheckedDns = new List<string>();

        var starts = edges
            .Select(edge => edge.ParentDn)
            .Distinct(Dn.Comparer)
            .OrderBy(dn => dn, StringComparer.OrdinalIgnoreCase);

        foreach (var start in starts)
        {
            if (seen.Contains(start))
            {
                continue;
            }

            var walk = MembershipTraversal.Walk(snapshot, start);
            seen.UnionWith(walk.Visited);

            // Walk frontiers land BEFORE the load-state scan below, in sweep
            // order — so the first-WALKED spelling deterministically wins the
            // report-side first-occurrence dedup (sorted starts).
            uncheckedDns.AddRange(walk.Frontier);

            if (!circular.Enabled)
            {
                // Disabled gates only the cycle-to-finding conversion (and its
                // canonicalization/suppression work) — never the sweep itself.
                continue;
            }

            foreach (var path in walk.Cycles)
            {
                var cycle = RotateToMinimalDn(path);
                if (!reported.Add(cycle) || IsSuppressedCycle(snapshot, ruleset, cycle))
                {
                    continue;
                }

                violations.Add(new RuleViolation
                {
                    RuleId = RuleIds.Circular,
                    Severity = circular.Severity,
                    Dns = cycle,
                    Message = CircularMessage(snapshot, cycle),
                });
            }
        }

        // Source (b): the O(V) load-state SCAN (not a walk) — in-snapshot
        // fetchables whose members were never loaded and that no walk reaches.
        foreach (var obj in snapshot.Objects)
        {
            if (IsFetchableKind(obj.Kind) && !snapshot.IsLoaded(obj.Dn))
            {
                uncheckedDns.Add(obj.Dn);
            }
        }

        return uncheckedDns;
    }

    /// <summary>Canonical cycle identity (ADR-009, the consumer concern
    /// <see cref="MembershipWalk.Cycles"/> assigns to AP 3.2): the path slice
    /// rotated so the <see cref="Dn.Comparer"/>-minimal DN comes first — unique,
    /// because path DNs are pairwise distinct under the comparer. Rotation ONLY,
    /// never reversal: membership direction is meaningful. Self-membership
    /// <c>[A]</c> canonicalizes to itself; spellings stay first-encountered.</summary>
    private static IReadOnlyList<string> RotateToMinimalDn(IReadOnlyList<string> path)
    {
        var minIndex = 0;
        for (var i = 1; i < path.Count; i++)
        {
            if (Dn.Comparer.Compare(path[i], path[minIndex]) < 0)
            {
                minIndex = i;
            }
        }

        if (minIndex == 0)
        {
            return path;
        }

        var rotated = new string[path.Count];
        for (var i = 0; i < path.Count; i++)
        {
            rotated[i] = path[(minIndex + i) % path.Count];
        }

        return rotated;
    }

    /// <summary>Whether ANY cycle DN matches the global ignore or
    /// <c>Circular.Exceptions</c> — one match takes the WHOLE cycle out.
    /// Suppression order (ADR-008 §5): global ignore first, then the per-rule
    /// exceptions; every DN goes through the dual channel (raw cycle DNs match
    /// via <see cref="MatchEntry.MatchesDn"/> only).</summary>
    private static bool IsSuppressedCycle(DirectorySnapshot snapshot, Ruleset ruleset, IReadOnlyList<string> cycle)
    {
        foreach (var dn in cycle)
        {
            if (MatchesAny(ruleset.Ignore, snapshot, dn))
            {
                return true;
            }
        }

        foreach (var dn in cycle)
        {
            if (MatchesAny(ruleset.Circular.Exceptions, snapshot, dn))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>The circular message template (ADR-009): cycle DNs by
    /// <see cref="DisplayName"/>, anchor repeated at the END in prose only —
    /// the Dns list keeps the no-repeat convention. String-only interpolation
    /// is culture-invariant by construction.</summary>
    private static string CircularMessage(DirectorySnapshot snapshot, IReadOnlyList<string> cycle)
    {
        var names = string.Join(" -> ", cycle.Select(dn => DisplayName(snapshot, dn)));
        return $"Circular nesting: {names} -> {DisplayName(snapshot, cycle[0])}.";
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
        foreach (var entry in entries)
        {
            if (Matches(entry, snapshot, dn))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>One entry through the dual channel (see
    /// <see cref="MatchesAny(IReadOnlyList{MatchEntry}, DirectorySnapshot, string)"/>).</summary>
    private static bool Matches(MatchEntry entry, DirectorySnapshot snapshot, string dn) =>
        snapshot.TryGetObject(dn, out var obj) ? entry.Matches(obj) : entry.MatchesDn(dn);

    /// <summary>Whether any nesting exception suppresses the edge, honoring
    /// endpoint narrowing (ADR-009): <c>Parent</c> tests only ParentDn,
    /// <c>Member</c> only ChildDn, <c>Any</c> either. Each tested endpoint goes
    /// through the dual channel.</summary>
    private static bool IsExceptedEdge(IReadOnlyList<MatchEntry> exceptions, DirectorySnapshot snapshot, MembershipEdge edge)
    {
        foreach (var entry in exceptions)
        {
            var suppresses = entry.Endpoint switch
            {
                MatchEndpoint.Parent => Matches(entry, snapshot, edge.ParentDn),
                MatchEndpoint.Member => Matches(entry, snapshot, edge.ChildDn),
                _ => Matches(entry, snapshot, edge.ParentDn) || Matches(entry, snapshot, edge.ChildDn),
            };

            if (suppresses)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Message-template addressing (ADR-009): the object's Name when
    /// the DN resolves in the snapshot, else the raw DN verbatim.</summary>
    private static string DisplayName(DirectorySnapshot snapshot, string dn) =>
        snapshot.TryGetObject(dn, out var obj) ? obj.Name : dn;

    /// <summary>English kind word for message templates — lowercase; callers
    /// capitalize the sentence-initial (parent) position.</summary>
    private static string KindWord(AdObjectKind kind) => kind switch
    {
        AdObjectKind.User => "user",
        AdObjectKind.GlobalGroup => "global group",
        AdObjectKind.DomainLocalGroup => "domain-local group",
        AdObjectKind.UniversalGroup => "universal group",
        AdObjectKind.OrganizationalUnit => "organizational unit",
        AdObjectKind.Computer => "computer",
        _ => "external object",
    };

    /// <summary>The rule-subject group kinds (empty-group subjects, nesting
    /// judged-domain parents): real group scopes only — External is fetchable
    /// but never a rule subject (ADR-008).</summary>
    private static bool IsGroupKind(AdObjectKind kind) => kind
        is AdObjectKind.GlobalGroup
        or AdObjectKind.DomainLocalGroup
        or AdObjectKind.UniversalGroup;

    /// <summary>ADR-005's fetchable kinds — congruent with "what a double-click
    /// would fetch": the group scopes plus External. The load-state scan's kind
    /// filter; users, computers, and OUs are leaves whose members are never
    /// fetched and therefore never unchecked.</summary>
    private static bool IsFetchableKind(AdObjectKind kind) =>
        IsGroupKind(kind) || kind == AdObjectKind.External;

    /// <summary>Element-wise <see cref="Dn.Comparer"/> equality over canonical
    /// cycle sequences (ADR-009): exact — never a joined-string surrogate (DN
    /// strings are opaque and uncanonicalized). Case-variant first-encountered
    /// spellings of the same cycle collapse; rotation variants already
    /// canonicalized away by <see cref="RotateToMinimalDn"/>.</summary>
    private sealed class DnSequenceComparer : IEqualityComparer<IReadOnlyList<string>>
    {
        public static DnSequenceComparer Instance { get; } = new();

        public bool Equals(IReadOnlyList<string>? x, IReadOnlyList<string>? y)
        {
            if (ReferenceEquals(x, y))
            {
                return true;
            }

            if (x is null || y is null || x.Count != y.Count)
            {
                return false;
            }

            for (var i = 0; i < x.Count; i++)
            {
                if (!Dn.Comparer.Equals(x[i], y[i]))
                {
                    return false;
                }
            }

            return true;
        }

        public int GetHashCode(IReadOnlyList<string> obj)
        {
            var hash = default(HashCode);
            foreach (var dn in obj)
            {
                hash.Add(dn, Dn.Comparer);
            }

            return hash.ToHashCode();
        }
    }
}
