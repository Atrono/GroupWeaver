namespace GroupWeaver.Core.Graph;

/// <summary>The complete renderable graph produced by <see cref="GraphBuilder"/>.</summary>
public sealed record GraphModel(IReadOnlyList<GraphNode> Nodes, IReadOnlyList<GraphEdge> Edges);
