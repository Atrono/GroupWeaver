using GroupWeaver.Core.Model;

namespace GroupWeaver.Core.Diff;

/// <summary>
/// THE gap diff (ADR-015 Slice 1, #66): compares the live "Ist"
/// <see cref="DirectorySnapshot"/> against the proposed "Plan" projection
/// (<c>PlanProjection.ToSnapshot</c>), classifying every node and edge as
/// <see cref="DiffStatus.Common"/>, <see cref="DiffStatus.Added"/>,
/// <see cref="DiffStatus.Removed"/>, or <see cref="DiffStatus.Unchecked"/>.
///
/// <para>Mirrors <see cref="Rules.RuleEngine.Evaluate"/>'s discipline: <see cref="Compute"/>
/// is static, deterministic, total, UI-free, never throws on directory CONTENT, calls no
/// provider, and mutates neither input. <see cref="NodeStatus"/> is keyed by
/// <see cref="Dn.Comparer"/>; <see cref="EdgeStatus"/> is keyed by <see cref="MembershipEdge"/>
/// (which already hashes via <see cref="Dn.Comparer"/>). The honest load-state tri-state
/// (ADR-005, ADR-015 D5) drives <see cref="DiffStatus.Unchecked"/>: an edge under a KNOWN Ist
/// parent whose members were never loaded is Unchecked — never falsely Added/Removed — and that
/// parent surfaces in <see cref="UncheckedParents"/>; a PLAN-ONLY parent (not an Ist object) is
/// genuinely new, so its edges are Added and it never enters <see cref="UncheckedParents"/>.</para>
/// </summary>
/// <param name="NodeStatus">Diff status per DN, over the union of Ist/Plan objects.</param>
/// <param name="EdgeStatus">Diff status per edge, over the union of Ist/Plan edges.</param>
/// <param name="UncheckedParents">Distinct KNOWN-but-unloaded Ist parents that host a union
/// edge, sorted OrdinalIgnoreCase — the literal "unexpanded areas are unchecked."</param>
public sealed record SnapshotDiff(
    IReadOnlyDictionary<string, DiffStatus> NodeStatus,
    IReadOnlyDictionary<MembershipEdge, DiffStatus> EdgeStatus,
    IReadOnlyList<string> UncheckedParents)
{
    /// <summary>Diffs <paramref name="ist"/> (live) against <paramref name="plan"/>
    /// (proposed). Two calls on content-equal inputs yield projection-equal results
    /// (deterministic; the result's dictionaries do not override <c>Equals</c>, so
    /// consumers compare CONTENTS, never record/dictionary identity).</summary>
    public static SnapshotDiff Compute(DirectorySnapshot ist, DirectorySnapshot plan)
    {
        // THE single read of each snapshot's Edges per Compute (ADR-015 D1): the
        // property recomputes O(E) on every access (.claude/rules/data-model.md).
        // Nothing else may touch the property — both locals are read once below.
        var istEdges = ist.Edges;
        var planEdges = plan.Edges;

        // NodeStatus over the union of Ist/Plan object DNs: in both -> Common,
        // plan-only -> Added, ist-only -> Removed. Dn.Comparer-keyed, so a
        // case-variant DN present on both sides collapses to one Common entry.
        var nodeStatus = new Dictionary<string, DiffStatus>(Dn.Comparer);
        foreach (var obj in ist.Objects)
        {
            nodeStatus[obj.Dn] = DiffStatus.Removed;
        }

        foreach (var obj in plan.Objects)
        {
            nodeStatus[obj.Dn] = nodeStatus.ContainsKey(obj.Dn) ? DiffStatus.Common : DiffStatus.Added;
        }

        // EdgeStatus over the union of Ist/Plan edges. For each edge's parent P:
        // a KNOWN Ist object whose members were never loaded -> Unchecked (the Ist
        // side is unknowable) and P goes to UncheckedParents; otherwise normal
        // set-diff (in both -> Common, plan-only -> Added, ist-only -> Removed) —
        // a loaded-empty [] Ist parent AND a plan-only parent both take this arm.
        // An Ist edge always means its parent IS loaded (it has members), so an Ist
        // edge never hits the Unchecked arm; only a PLAN edge can.
        var istEdgeSet = new HashSet<MembershipEdge>(istEdges);

        var edgeStatus = new Dictionary<MembershipEdge, DiffStatus>();
        var uncheckedParents = new HashSet<string>(Dn.Comparer);

        foreach (var edge in istEdges)
        {
            // Provisionally Removed; promoted to Common below if the plan also has it.
            edgeStatus[edge] = DiffStatus.Removed;
        }

        foreach (var edge in planEdges)
        {
            // Unchecked iff the parent is a KNOWN Ist object whose members were
            // never loaded — gated on KNOWN-Ist-object, NOT on load-state alone, so
            // a genuinely new plan-only parent stays Added (ADR-015 D5 keystone).
            if (ist.TryGetObject(edge.ParentDn, out _) && !ist.IsLoaded(edge.ParentDn))
            {
                edgeStatus[edge] = DiffStatus.Unchecked;
                uncheckedParents.Add(edge.ParentDn);
                continue;
            }

            // Else normal: Common if the Ist side also has this exact edge, else Added.
            edgeStatus[edge] = istEdgeSet.Contains(edge) ? DiffStatus.Common : DiffStatus.Added;
        }

        var sortedUncheckedParents = uncheckedParents
            .OrderBy(dn => dn, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new SnapshotDiff(nodeStatus, edgeStatus, sortedUncheckedParents);
    }
}
