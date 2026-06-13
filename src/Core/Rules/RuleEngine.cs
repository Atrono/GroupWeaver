using System.Text.RegularExpressions;

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
}
