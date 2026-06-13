using GroupWeaver.Core.Graph;
using GroupWeaver.Core.Rules;

namespace GroupWeaver.App.Graph;

/// <summary>
/// Splits a <see cref="GraphModel"/> into ready-to-dispatch bridge command strings
/// (ADR-004 D4): zero or more <c>{"type":"graphChunk",…}</c> commands respecting the
/// per-chunk caps (nodes and edges may share a chunk), then a trailing
/// <c>{"type":"graphCommit"}</c> as the LAST command — JS is a dumb accumulator.
/// Order is preserved, every element is delivered exactly once, and edge ids are
/// unique across the whole transfer. Update mode (<see cref="ToUpdateCommands"/>,
/// ADR-005 D1) shares the ONE slicing path and differs ONLY in the trailing commit
/// verb: <c>{"type":"graphUpdate"}</c> (replace-in-place) instead of <c>graphCommit</c>.
/// Contract pinned by <c>tests/GroupWeaver.App.Tests/Graph/GraphChunkerTests.cs</c>.
/// </summary>
public static class GraphChunker
{
    private const string CommitCommand = """{"type":"graphCommit"}""";
    private const string UpdateCommand = """{"type":"graphUpdate"}""";

    /// <summary>Maps <paramref name="model"/> to the chunked wire commands for a full
    /// init (<c>graphCommit</c>: destroy + fit) — no severity (pre-AP-3.4 wire).</summary>
    public static IReadOnlyList<string> ToChunkCommands(
        GraphModel model,
        int maxNodesPerChunk = 500,
        int maxEdgesPerChunk = 1000) =>
        ToChunkCommands(model, RuleReport.Empty, belowMap: null, maxNodesPerChunk, maxEdgesPerChunk);

    /// <summary>Maps <paramref name="model"/> to the chunked wire commands for a full
    /// init (<c>graphCommit</c>: destroy + fit), joining the AP 3.4 severity wire fields
    /// (ADR-010): <paramref name="report"/> + <paramref name="belowMap"/> are forwarded
    /// straight into <see cref="GraphJson.MapNodes"/> — the ONE shared node path — so the
    /// per-chunk <c>sev</c>/<c>below</c>/<c>belowSev</c> are string-identical to the flat
    /// dump for the same model and can never drift.</summary>
    public static IReadOnlyList<string> ToChunkCommands(
        GraphModel model,
        RuleReport report,
        IReadOnlyDictionary<string, (int Count, RuleSeverity Sev)>? belowMap,
        int maxNodesPerChunk = 500,
        int maxEdgesPerChunk = 1000) =>
        ToCommands(model, report, belowMap, maxNodesPerChunk, maxEdgesPerChunk, CommitCommand);

    /// <summary>Maps <paramref name="model"/> to the chunked wire commands for a
    /// replace-in-place update (<c>graphUpdate</c>: live instance, viewport untouched,
    /// ADR-005 D1) — same chunks as <see cref="ToChunkCommands(GraphModel, int, int)"/>,
    /// different trailer; no severity (pre-AP-3.4 wire).</summary>
    public static IReadOnlyList<string> ToUpdateCommands(
        GraphModel model,
        int maxNodesPerChunk = 500,
        int maxEdgesPerChunk = 1000) =>
        ToUpdateCommands(model, RuleReport.Empty, belowMap: null, maxNodesPerChunk, maxEdgesPerChunk);

    /// <summary>Maps <paramref name="model"/> to the chunked wire commands for a
    /// replace-in-place update (<c>graphUpdate</c>), carrying the AP 3.4 severity wire
    /// fields (ADR-010 D3): the sev/below fields are string-identical to show mode for the
    /// same <paramref name="report"/> + <paramref name="belowMap"/>, so a re-sent update
    /// re-attaches the halos on the live instance — only the trailing commit verb differs.</summary>
    public static IReadOnlyList<string> ToUpdateCommands(
        GraphModel model,
        RuleReport report,
        IReadOnlyDictionary<string, (int Count, RuleSeverity Sev)>? belowMap,
        int maxNodesPerChunk = 500,
        int maxEdgesPerChunk = 1000) =>
        ToCommands(model, report, belowMap, maxNodesPerChunk, maxEdgesPerChunk, UpdateCommand);

    private static IReadOnlyList<string> ToCommands(
        GraphModel model,
        RuleReport report,
        IReadOnlyDictionary<string, (int Count, RuleSeverity Sev)>? belowMap,
        int maxNodesPerChunk,
        int maxEdgesPerChunk,
        string trailingCommand)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxNodesPerChunk);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxEdgesPerChunk);

        // GraphJson owns the ONE mapping code path (ids, flip, camelCase, severity) — the
        // chunker only slices its output, so chunked and flat can never drift.
        var nodes = GraphJson.MapNodes(model.Nodes, report, belowMap);
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

        commands.Add(trailingCommand);
        return commands;
    }

    /// <summary>One <c>graphChunk</c> bridge command.</summary>
    private sealed record ChunkDto(
        string Type,
        IReadOnlyList<GraphJson.NodeDto> Nodes,
        IReadOnlyList<GraphJson.EdgeDto> Edges);
}
