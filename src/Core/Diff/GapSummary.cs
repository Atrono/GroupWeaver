namespace GroupWeaver.Core.Diff;

/// <summary>
/// Flat per-status counts of a <see cref="SnapshotDiff"/> (ADR-015 D6) — the GapView's summary
/// line and the "N Ist areas unexpanded" honesty banner. Nodes are only ever
/// <see cref="DiffStatus.Common"/>/<see cref="DiffStatus.Added"/>/<see cref="DiffStatus.Removed"/>
/// (node diff is total — never <see cref="DiffStatus.Unchecked"/>); edges add
/// <see cref="DiffStatus.Unchecked"/> (the honest load-state tri-state, ADR-005/ADR-015 D5). The
/// trailing <see cref="UncheckedParents"/> count comes straight from
/// <see cref="SnapshotDiff.UncheckedParents"/> — the distinct known-but-unloaded Ist parents that
/// host a union edge.
/// </summary>
/// <param name="AddedNodes">Nodes present only in the Plan projection.</param>
/// <param name="RemovedNodes">Nodes present only in the live Ist snapshot.</param>
/// <param name="CommonNodes">Nodes present in both sides.</param>
/// <param name="AddedEdges">Edges present only in the Plan projection.</param>
/// <param name="RemovedEdges">Edges present only in the live Ist snapshot.</param>
/// <param name="CommonEdges">Edges present in both sides.</param>
/// <param name="UncheckedEdges">Edges under a known-but-unloaded Ist parent (unknowable side).</param>
/// <param name="UncheckedParents">Distinct known-but-unloaded Ist parents that host a union edge.</param>
public sealed record GapSummary(
    int AddedNodes,
    int RemovedNodes,
    int CommonNodes,
    int AddedEdges,
    int RemovedEdges,
    int CommonEdges,
    int UncheckedEdges,
    int UncheckedParents)
{
    /// <summary>Tallies <paramref name="diff"/>'s <see cref="SnapshotDiff.NodeStatus"/> and
    /// <see cref="SnapshotDiff.EdgeStatus"/> values plus its
    /// <see cref="SnapshotDiff.UncheckedParents"/> count into the flat counts record — so the
    /// summary can never silently drift from the diff it summarizes.</summary>
    public static GapSummary From(SnapshotDiff diff)
    {
        int addedNodes = 0, removedNodes = 0, commonNodes = 0;
        foreach (var status in diff.NodeStatus.Values)
        {
            switch (status)
            {
                case DiffStatus.Added:
                    addedNodes++;
                    break;
                case DiffStatus.Removed:
                    removedNodes++;
                    break;
                case DiffStatus.Common:
                    commonNodes++;
                    break;
            }
        }

        int addedEdges = 0, removedEdges = 0, commonEdges = 0, uncheckedEdges = 0;
        foreach (var status in diff.EdgeStatus.Values)
        {
            switch (status)
            {
                case DiffStatus.Added:
                    addedEdges++;
                    break;
                case DiffStatus.Removed:
                    removedEdges++;
                    break;
                case DiffStatus.Common:
                    commonEdges++;
                    break;
                case DiffStatus.Unchecked:
                    uncheckedEdges++;
                    break;
            }
        }

        return new GapSummary(
            addedNodes,
            removedNodes,
            commonNodes,
            addedEdges,
            removedEdges,
            commonEdges,
            uncheckedEdges,
            diff.UncheckedParents.Count);
    }
}
