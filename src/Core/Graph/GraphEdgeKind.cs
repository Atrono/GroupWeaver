namespace GroupWeaver.Core.Graph;

/// <summary>Kind of a drawn graph edge (ADR-004).</summary>
public enum GraphEdgeKind
{
    /// <summary>A group-membership edge from <see cref="Model.DirectorySnapshot.Edges"/>.</summary>
    Membership,

    /// <summary>A DN-containment edge from the nearest in-snapshot ancestor.</summary>
    Containment,
}
