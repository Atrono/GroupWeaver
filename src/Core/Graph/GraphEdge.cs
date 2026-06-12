namespace GroupWeaver.Core.Graph;

/// <summary>
/// A drawn edge in semantic direction: <paramref name="ParentDn"/> is the
/// group/container, <paramref name="ChildDn"/> the member/contained object
/// (the drawn orientation is the App layer's concern, ADR-004).
/// </summary>
public sealed record GraphEdge(GraphEdgeKind Kind, string ParentDn, string ChildDn);
