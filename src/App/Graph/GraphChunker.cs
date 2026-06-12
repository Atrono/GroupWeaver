using GroupWeaver.Core.Graph;

namespace GroupWeaver.App.Graph;

/// <summary>
/// Splits a <see cref="GraphModel"/> into ready-to-dispatch bridge command strings
/// (ADR-004 D4): zero or more <c>{"type":"graphChunk",…}</c> commands respecting the
/// per-chunk caps (nodes and edges may share a chunk), then a trailing
/// <c>{"type":"graphCommit"}</c> as the LAST command — JS is a dumb accumulator.
/// Order is preserved, every element is delivered exactly once, and edge ids are
/// unique across the whole transfer.
/// Contract pinned by <c>tests/GroupWeaver.App.Tests/Graph/GraphChunkerTests.cs</c>.
/// </summary>
public static class GraphChunker
{
    private const string CommitCommand = """{"type":"graphCommit"}""";

    /// <summary>Maps <paramref name="model"/> to the chunked wire commands.</summary>
    public static IReadOnlyList<string> ToChunkCommands(
        GraphModel model,
        int maxNodesPerChunk = 500,
        int maxEdgesPerChunk = 1000)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxNodesPerChunk);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxEdgesPerChunk);

        // GraphJson owns the ONE mapping code path (ids, flip, camelCase) — the
        // chunker only slices its output, so chunked and flat can never drift.
        var nodes = GraphJson.MapNodes(model.Nodes);
        var edges = GraphJson.MapEdges(model.Edges);

        var commands = new List<string>();
        var nodeIndex = 0;
        var edgeIndex = 0;
        while (nodeIndex < nodes.Count || edgeIndex < edges.Count)
        {
            // Greedy fill: each chunk takes as many remaining nodes AND edges as
            // its caps allow — never one command per element.
            var nodeCount = Math.Min(maxNodesPerChunk, nodes.Count - nodeIndex);
            var edgeCount = Math.Min(maxEdgesPerChunk, edges.Count - edgeIndex);
            commands.Add(GraphJson.Serialize(new ChunkDto(
                "graphChunk",
                nodes.GetRange(nodeIndex, nodeCount),
                edges.GetRange(edgeIndex, edgeCount))));
            nodeIndex += nodeCount;
            edgeIndex += edgeCount;
        }

        commands.Add(CommitCommand);
        return commands;
    }

    /// <summary>One <c>graphChunk</c> bridge command.</summary>
    private sealed record ChunkDto(
        string Type,
        IReadOnlyList<GraphJson.NodeDto> Nodes,
        IReadOnlyList<GraphJson.EdgeDto> Edges);
}
