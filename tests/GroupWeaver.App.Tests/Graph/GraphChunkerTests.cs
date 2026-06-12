using System.Text.Json.Nodes;

using GroupWeaver.App.Graph;
using GroupWeaver.Core.Graph;
using GroupWeaver.Core.Model;

using Xunit;

namespace GroupWeaver.App.Tests.Graph;

/// <summary>
/// Pins the ADR-004 D4 chunking contract of <see cref="GraphChunker.ToChunkCommands"/>
/// (AP 2.2 S4): per-chunk caps respected (defaults 500 nodes / 1000 edges, greedy
/// fill), order preserved, every element delivered exactly once, a trailing
/// <c>{"type":"graphCommit"}</c> as the LAST command with only <c>graphChunk</c>
/// commands before it, edge ids unique across the whole transfer, and every emitted
/// string parses as JSON with a <c>type</c> field. Nodes and edges MAY share a chunk
/// — tests never pin their separation.
/// </summary>
public sealed class GraphChunkerTests
{
    // --- chunk splitting --------------------------------------------------------------

    [Fact]
    public void ToChunkCommands_With501Nodes_EmitsTwoNodeChunks_500Plus1_InModelOrder()
    {
        var model = NodesOnly(501);

        var chunks = ChunksOf(GraphChunker.ToChunkCommands(model));

        Assert.Equal(new[] { 500, 1 }, NodeChunkSizes(chunks));
        Assert.Equal(model.Nodes.Select(n => n.Dn), NodeIds(chunks));
        Assert.Empty(Edges(chunks));
    }

    [Fact]
    public void ToChunkCommands_With1001Edges_EmitsTwoEdgeChunks_1000Plus1_InModelOrder()
    {
        var model = EdgesOnly(1001);

        var chunks = ChunksOf(GraphChunker.ToChunkCommands(model));
        var edges = Edges(chunks);

        Assert.Equal(new[] { 1000, 1 }, EdgeChunkSizes(chunks));
        Assert.Equal(model.Edges.Select(Wire), edges.Select(e => (e.S, e.T, e.Rel)));
        Assert.Empty(NodeIds(chunks));
        Assert.Distinct(edges.Select(e => e.Id));
    }

    [Fact]
    public void ToChunkCommands_CustomCaps_AreRespectedGreedily()
    {
        var model = new GraphModel(
            [.. Enumerable.Range(0, 5).Select(NodeAt)],
            [.. Enumerable.Range(0, 5).Select(MembershipAt)]);

        var chunks = ChunksOf(GraphChunker.ToChunkCommands(model, maxNodesPerChunk: 2, maxEdgesPerChunk: 2));

        Assert.Equal(new[] { 2, 2, 1 }, NodeChunkSizes(chunks));
        Assert.Equal(new[] { 2, 2, 1 }, EdgeChunkSizes(chunks));
        Assert.Equal(model.Nodes.Select(n => n.Dn), NodeIds(chunks));
        Assert.Equal(model.Edges.Select(Wire), Edges(chunks).Select(e => (e.S, e.T, e.Rel)));
    }

    // --- command shape ------------------------------------------------------------------

    [Fact]
    public void ToChunkCommands_LastCommandIsGraphCommit_AllOthersAreGraphChunk()
    {
        var commands = GraphChunker.ToChunkCommands(SmallModel());

        var parsed = ParseAll(commands);

        Assert.Equal("graphCommit", CommandType(parsed[^1]));
        Assert.All(parsed.SkipLast(1), c => Assert.Equal("graphChunk", CommandType(c)));
        Assert.Single(parsed, c => CommandType(c) == "graphCommit");
    }

    [Fact]
    public void ToChunkCommands_SmallModel_BatchesIntoMinimalCommands()
    {
        var model = SmallModel(); // 3 nodes + 2 edges, far under any cap

        var commands = GraphChunker.ToChunkCommands(model);

        // At most one node-bearing chunk plus one edge-bearing chunk (sharing one
        // chunk is allowed) plus the commit - never one command per element.
        Assert.InRange(commands.Count, 2, 3);
        var chunks = ChunksOf(commands);
        Assert.Equal(model.Nodes.Select(n => n.Dn), NodeIds(chunks));
        Assert.Equal(model.Edges.Select(Wire), Edges(chunks).Select(e => (e.S, e.T, e.Rel)));
    }

    [Fact]
    public void ToChunkCommands_EmptyModel_IsSingleGraphCommit()
    {
        var commands = GraphChunker.ToChunkCommands(new GraphModel([], []));

        var command = Assert.Single(commands);
        Assert.Equal("graphCommit", CommandType(ParseCommand(command)));
    }

    // --- totality over a mixed transfer ---------------------------------------------------

    [Fact]
    public void ToChunkCommands_MixedModel_EveryElementExactlyOnce_CapsHold_EdgeIdsUnique()
    {
        var model = new GraphModel(
            [.. Enumerable.Range(0, 501).Select(NodeAt)],
            [
                .. Enumerable.Range(0, 600).Select(MembershipAt),
                .. Enumerable.Range(0, 401).Select(ContainmentAt),
            ]);

        var chunks = ChunksOf(GraphChunker.ToChunkCommands(model));

        foreach (var chunk in chunks)
        {
            Assert.InRange((chunk["nodes"] as JsonArray)?.Count ?? 0, 0, 500);
            Assert.InRange((chunk["edges"] as JsonArray)?.Count ?? 0, 0, 1000);
        }

        // Exactly once AND in order: sequence equality covers both directions.
        Assert.Equal(model.Nodes.Select(n => n.Dn), NodeIds(chunks));
        var edges = Edges(chunks);
        Assert.Equal(model.Edges.Select(Wire), edges.Select(e => (e.S, e.T, e.Rel)));

        // Edge ids unique across the WHOLE transfer (m/c counters must not collide).
        Assert.Distinct(edges.Select(e => e.Id));
    }

    // --- model factories ------------------------------------------------------------------

    private static GraphNode NodeAt(int i) =>
        new($"CN=N{i:D4},OU=X,DC=x", $"N{i:D4}", AdObjectKind.User, X: i, Y: i + 0.5, Ring: 1, IsRoot: false);

    private static GraphEdge MembershipAt(int i) =>
        new(GraphEdgeKind.Membership, $"CN=G{i:D4},OU=X,DC=x", $"CN=U{i:D4},OU=X,DC=x");

    private static GraphEdge ContainmentAt(int i) =>
        new(GraphEdgeKind.Containment, "OU=X,DC=x", $"CN=C{i:D4},OU=X,DC=x");

    private static GraphModel NodesOnly(int count) =>
        new([.. Enumerable.Range(0, count).Select(NodeAt)], []);

    private static GraphModel EdgesOnly(int count) =>
        new([], [.. Enumerable.Range(0, count).Select(MembershipAt)]);

    private static GraphModel SmallModel() =>
        new(
            [NodeAt(0), NodeAt(1), NodeAt(2)],
            [MembershipAt(0), ContainmentAt(0)]);

    /// <summary>The wire (s, t, rel) triple for a model edge: membership is flipped
    /// (s := member, t := group), containment is not (ADR-004 D4).</summary>
    private static (string S, string T, string Rel) Wire(GraphEdge edge) =>
        edge.Kind == GraphEdgeKind.Membership
            ? (edge.ChildDn, edge.ParentDn, "member")
            : (edge.ParentDn, edge.ChildDn, "contains");

    // --- command parsing helpers -------------------------------------------------------------

    /// <summary>Every emitted command must parse as a JSON object with a string
    /// <c>type</c> field — asserted on EVERY path through these tests.</summary>
    private static JsonObject ParseCommand(string command)
    {
        var parsed = Assert.IsType<JsonObject>(JsonNode.Parse(command));
        Assert.True(
            parsed["type"] is JsonValue value && value.TryGetValue<string>(out _),
            $"command lacks a string 'type' field: {command}");
        return parsed;
    }

    private static List<JsonObject> ParseAll(IReadOnlyList<string> commands) =>
        [.. commands.Select(ParseCommand)];

    private static string CommandType(JsonObject command) => command["type"]!.GetValue<string>();

    /// <summary>Parses all commands, asserts the trailing-commit shape (last command
    /// <c>graphCommit</c>, everything before it <c>graphChunk</c>), and returns the chunks.</summary>
    private static List<JsonObject> ChunksOf(IReadOnlyList<string> commands)
    {
        var parsed = ParseAll(commands);
        Assert.NotEmpty(parsed);
        Assert.Equal("graphCommit", CommandType(parsed[^1]));
        var chunks = parsed.Take(parsed.Count - 1).ToList();
        Assert.All(chunks, c => Assert.Equal("graphChunk", CommandType(c)));
        return chunks;
    }

    private static List<string> NodeIds(IEnumerable<JsonObject> chunks) =>
        [.. chunks
            .SelectMany(c => c["nodes"] as JsonArray ?? new JsonArray())
            .Select(n => n!["id"]!.GetValue<string>())];

    private static List<(string Id, string S, string T, string Rel)> Edges(IEnumerable<JsonObject> chunks) =>
        [.. chunks
            .SelectMany(c => c["edges"] as JsonArray ?? new JsonArray())
            .Select(e => (
                e!["id"]!.GetValue<string>(),
                e!["s"]!.GetValue<string>(),
                e!["t"]!.GetValue<string>(),
                e!["rel"]!.GetValue<string>()))];

    private static List<int> NodeChunkSizes(IEnumerable<JsonObject> chunks) =>
        [.. chunks.Select(c => (c["nodes"] as JsonArray)?.Count ?? 0).Where(n => n > 0)];

    private static List<int> EdgeChunkSizes(IEnumerable<JsonObject> chunks) =>
        [.. chunks.Select(c => (c["edges"] as JsonArray)?.Count ?? 0).Where(n => n > 0)];
}
