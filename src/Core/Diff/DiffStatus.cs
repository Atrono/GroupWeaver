namespace GroupWeaver.Core.Diff;

/// <summary>
/// Gap-analysis classification (ADR-015) of a node or edge when the live "Ist"
/// <see cref="Model.DirectorySnapshot"/> is diffed against the proposed "Plan"
/// projection: present in BOTH (<see cref="Common"/>), only in the plan
/// (<see cref="Added"/>), only in the live Ist (<see cref="Removed"/>), or — for
/// an edge under a KNOWN Ist parent whose members were never loaded — unknowable
/// and therefore <see cref="Unchecked"/> (the honest load-state tri-state, never a
/// false Added/Removed). Direction: the plan is the proposed target, so what the
/// plan introduces is Added and what only the live Ist still has is Removed.
/// </summary>
public enum DiffStatus
{
    /// <summary>Present in both the Ist snapshot and the Plan projection.</summary>
    Common,

    /// <summary>Present only in the Plan projection (proposed by the plan).</summary>
    Added,

    /// <summary>Present only in the live Ist snapshot (the plan drops it).</summary>
    Removed,

    /// <summary>An edge under a KNOWN Ist parent whose members were never loaded:
    /// the Ist side is unknowable, so the edge is neither Added nor Removed.</summary>
    Unchecked,
}
