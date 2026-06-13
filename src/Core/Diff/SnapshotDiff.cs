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

    /// <summary>
    /// Builds a FRESH <see cref="DirectorySnapshot"/> that overlays both sides for RENDERING
    /// the gap graph (ADR-015 D3): every Removed Ist node AND every Added Plan node materializes
    /// so <c>GraphBuilder.Build(union, RootDn)</c> — unchanged — can paint them side by side.
    /// Mutates NEITHER input (the union is a local; nothing is written to AD or to the borrowed
    /// snapshots — ADR-005 append-only holds by construction).
    ///
    /// <para><b>Ist-wins on Common DNs:</b> <c>AddObject</c> is latest-wins, so every PLAN object
    /// is added FIRST, then every IST object — the real loaded Ist object (carrying its
    /// whitelist-filtered <c>Attributes</c>) overwrites the plan object on a shared DN. The order
    /// is load-bearing.</para>
    ///
    /// <para><b>Per-parent member union:</b> every parent that EITHER side has LOADED
    /// (<c>ist.IsLoaded(P) || plan.IsLoaded(P)</c>) is <c>SetMembers</c>'d to the distinct
    /// (<see cref="Dn.Comparer"/>) UNION of the two sides' members — a side that has NOT loaded P
    /// contributes none — so a loaded-EMPTY <c>[]</c> parent stays loaded-and-empty (never regresses
    /// to "never loaded") and an Ist-unloaded-but-plan-loaded parent renders the plan's edges. Reads
    /// only <c>Objects</c>/<c>IsLoaded</c>/<c>GetMembers</c> — never <c>.Edges</c> (no O(E)-recompute).
    /// Edge diff STATUS (e.g. Unchecked) is decided by <see cref="Compute"/>, not here.</para>
    /// </summary>
    public static DirectorySnapshot BuildUnion(DirectorySnapshot ist, DirectorySnapshot plan)
    {
        var union = new DirectorySnapshot();

        // Ist-wins on Common DNs: plan objects first, then Ist objects overwrite (latest wins).
        foreach (var obj in plan.Objects)
        {
            union.AddObject(obj);
        }

        foreach (var obj in ist.Objects)
        {
            union.AddObject(obj);
        }

        // Candidate parents = the union of both sides' object DNs (Dn.Comparer-deduped); every
        // loaded parent in either snapshot is an object, so this set covers them all. (The one
        // exception — a vanished-frontier DN the lazy-expand arm SetMembers'd to [] without an
        // object — is loaded-EMPTY, hence edge-free, so omitting it leaves the rendered topology
        // identical.) Each parent LOADED on either side is SetMembers'd to the distinct union of
        // the loaded sides' members.
        var candidateParents = new HashSet<string>(Dn.Comparer);
        foreach (var obj in plan.Objects)
        {
            candidateParents.Add(obj.Dn);
        }

        foreach (var obj in ist.Objects)
        {
            candidateParents.Add(obj.Dn);
        }

        foreach (var parent in candidateParents)
        {
            var istLoaded = ist.IsLoaded(parent);
            var planLoaded = plan.IsLoaded(parent);
            if (!istLoaded && !planLoaded)
            {
                continue; // neither side loaded P -> leave it "never loaded" (null members).
            }

            var members = new List<string>();
            if (istLoaded)
            {
                members.AddRange(ist.GetMembers(parent)!);
            }

            if (planLoaded)
            {
                members.AddRange(plan.GetMembers(parent)!);
            }

            // SetMembers de-dups case-insensitively; passing a clean union keeps it tidy and
            // preserves the loaded-empty [] tri-state when both contributions are empty.
            union.SetMembers(parent, members);
        }

        return union;
    }
}
