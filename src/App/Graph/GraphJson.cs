using System.Text.Json;
using System.Text.Json.Serialization;

using GroupWeaver.Core.Graph;

namespace GroupWeaver.App.Graph;

/// <summary>
/// App-side wire mapper (ADR-004 D4): <see cref="GraphModel"/> → camelCase JSON.
/// Node: <c>{"id":DN verbatim,"label","kind":enum name verbatim,"x","y"}</c> plus
/// <c>"root":true</c> on the root node only (absent otherwise); edge:
/// <c>{"id":"m0"/"c0"…,"s","t","rel":"member"|"contains"}</c> with the membership
/// orientation flipped HERE (s := member/ChildDn, t := group/ParentDn — Core keeps
/// the semantic direction). Doubles are invariant-'.' always.
/// Contract pinned by <c>tests/GroupWeaver.App.Tests/Graph/GraphJsonTests.cs</c>.
/// </summary>
public static class GraphJson
{
    /// <summary>Reflection serializer on purpose (ADR-004 D4: source-gen STJ only
    /// if trimming ever lands); the camelCase policy renames PROPERTIES only —
    /// kind values stay enum names verbatim because they are mapped to strings.</summary>
    private static readonly JsonSerializerOptions WireOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    /// <summary>
    /// Serializes the whole model as one flat <c>{"nodes":[…],"edges":[…]}</c>
    /// document — the <c>--dump-graph</c> file format (ADR-004 D7).
    /// </summary>
    public static string SerializeFlat(GraphModel model) =>
        Serialize(new FlatDto(MapNodes(model.Nodes), MapEdges(model.Edges)));

    /// <summary>Serializes a wire DTO with the shared options — every wire string
    /// (flat and chunked) goes through here.</summary>
    internal static string Serialize<TValue>(TValue dto) =>
        JsonSerializer.Serialize(dto, WireOptions);

    /// <summary>The ONE node mapping code path, shared with <see cref="GraphChunker"/>
    /// so chunked and flat output can never drift.</summary>
    internal static List<NodeDto> MapNodes(IReadOnlyList<GraphNode> nodes) =>
        [.. nodes.Select(n => new NodeDto(
            n.Dn, n.Label, n.Kind.ToString(), n.X, n.Y, Root: n.IsRoot ? true : null))];

    /// <summary>The ONE edge mapping code path, shared with <see cref="GraphChunker"/>:
    /// per-kind <c>m</c>/<c>c</c> id counters in model edge order, membership flipped
    /// (s := member, t := group), containment as-is (s := container).</summary>
    internal static List<EdgeDto> MapEdges(IReadOnlyList<GraphEdge> edges)
    {
        var dtos = new List<EdgeDto>(edges.Count);
        var membershipCount = 0;
        var containmentCount = 0;
        foreach (var edge in edges)
        {
            dtos.Add(edge.Kind == GraphEdgeKind.Membership
                ? new EdgeDto($"m{membershipCount++}", S: edge.ChildDn, T: edge.ParentDn, Rel: "member")
                : new EdgeDto($"c{containmentCount++}", S: edge.ParentDn, T: edge.ChildDn, Rel: "contains"));
        }

        return dtos;
    }

    /// <summary>Wire node; <paramref name="Root"/> is <c>true</c> or omitted —
    /// never emitted as <c>false</c>.</summary>
    internal sealed record NodeDto(
        string Id,
        string Label,
        string Kind,
        double X,
        double Y,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? Root);

    /// <summary>Wire edge in drawn orientation.</summary>
    internal sealed record EdgeDto(string Id, string S, string T, string Rel);

    /// <summary>The flat <c>--dump-graph</c> document shape.</summary>
    private sealed record FlatDto(IReadOnlyList<NodeDto> Nodes, IReadOnlyList<EdgeDto> Edges);
}
