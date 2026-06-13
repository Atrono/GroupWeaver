using System.Text.Json;
using System.Text.Json.Nodes;

using GroupWeaver.App.Graph;
using GroupWeaver.Core.Graph;
using GroupWeaver.Core.Model;
using GroupWeaver.Core.Rules;
using GroupWeaver.Providers;

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
/// AP 2.3 (ADR-005 D1) adds the update mode, <c>GraphChunker.ToUpdateCommands</c>:
/// ONE shared slicing path — the <c>graphChunk</c> commands are string-identical to
/// show mode for the same model and caps; only the trailing commit verb differs
/// (<c>{"type":"graphUpdate"}</c> instead of <c>graphCommit</c>).
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
        var model = MixedOverCapModel();

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

    // --- update mode (AP 2.3, ADR-005 D1) -------------------------------------------------

    [Fact]
    public void ToUpdateCommands_LastCommandIsGraphUpdate_AllOthersAreGraphChunk()
    {
        var commands = GraphChunker.ToUpdateCommands(SmallModel());

        var parsed = ParseAll(commands);

        Assert.Equal("graphUpdate", CommandType(parsed[^1]));
        Assert.All(parsed.SkipLast(1), c => Assert.Equal("graphChunk", CommandType(c)));
        Assert.Single(parsed, c => CommandType(c) == "graphUpdate");
        Assert.DoesNotContain(parsed, c => CommandType(c) == "graphCommit");
    }

    [Fact]
    public void ToUpdateCommands_EmptyModel_IsSingleGraphUpdate()
    {
        var commands = GraphChunker.ToUpdateCommands(new GraphModel([], []));

        var command = Assert.Single(commands);
        Assert.Equal("graphUpdate", CommandType(ParseCommand(command)));
    }

    [Fact]
    public void ToUpdateCommands_DefaultCaps_ChunksAreStringIdenticalToShowMode()
    {
        var model = MixedOverCapModel(); // forces multiple chunks under the default caps

        AssertUpdateMatchesShowExceptTrailingVerb(
            GraphChunker.ToChunkCommands(model),
            GraphChunker.ToUpdateCommands(model));
    }

    [Fact]
    public void ToUpdateCommands_CustomCaps_ChunkSizesBehaveExactlyLikeShowMode()
    {
        var model = new GraphModel(
            [.. Enumerable.Range(0, 5).Select(NodeAt)],
            [.. Enumerable.Range(0, 5).Select(MembershipAt)]);

        var chunks = ChunksOf(
            GraphChunker.ToUpdateCommands(model, maxNodesPerChunk: 2, maxEdgesPerChunk: 2),
            commitVerb: "graphUpdate");

        Assert.Equal(new[] { 2, 2, 1 }, NodeChunkSizes(chunks));
        Assert.Equal(new[] { 2, 2, 1 }, EdgeChunkSizes(chunks));
        AssertUpdateMatchesShowExceptTrailingVerb(
            GraphChunker.ToChunkCommands(model, maxNodesPerChunk: 2, maxEdgesPerChunk: 2),
            GraphChunker.ToUpdateCommands(model, maxNodesPerChunk: 2, maxEdgesPerChunk: 2));
    }

    [Fact]
    public void ToUpdateCommands_MixedModel_EveryElementExactlyOnce_CapsHold_EdgeIdsUnique()
    {
        // The direct totality pin for update mode — survives even if the
        // string-identity pin is ever deliberately relaxed in review.
        var model = MixedOverCapModel();

        var chunks = ChunksOf(GraphChunker.ToUpdateCommands(model), commitVerb: "graphUpdate");

        foreach (var chunk in chunks)
        {
            Assert.InRange((chunk["nodes"] as JsonArray)?.Count ?? 0, 0, 500);
            Assert.InRange((chunk["edges"] as JsonArray)?.Count ?? 0, 0, 1000);
        }

        Assert.Equal(model.Nodes.Select(n => n.Dn), NodeIds(chunks));
        var edges = Edges(chunks);
        Assert.Equal(model.Edges.Select(Wire), edges.Select(e => (e.S, e.T, e.Rel)));
        Assert.Distinct(edges.Select(e => e.Id));
    }

    // --- severity rides the ONE shared slicing path (AP 3.4 S1, ADR-010) -----------------
    //
    // GraphChunker forwards the report + below-map straight into GraphJson.MapNodes (the
    // single shared node path), so the sev/below/belowSev fields a chunk carries are
    // STRING-IDENTICAL to the flat dump for the same model — chunked and flat can never
    // drift. The new public overloads grow exactly a (report, belowMap) pair; the existing
    // no-report overloads keep emitting the pre-AP wire (covered above).

    /// <summary>The demo dataset root, pinned against demo-directory.json's rootDn.</summary>
    private const string DemoRootDn = "OU=AGDLP-Demo,DC=weavedemo,DC=example";

    private const string GroupSuffix = ",OU=Groups,OU=AGDLP-Demo,DC=weavedemo,DC=example";
    private const string UserSuffix = ",OU=Users,OU=AGDLP-Demo,DC=weavedemo,DC=example";

    // Baseline DNs + their pinned MaxSeverityByDn (RuleEngineDemoBaselineTests).
    private const string DlFsSalesRwDn = "CN=DL_FS-Sales_RW" + GroupSuffix;   // Error
    private const string User001Dn = "CN=Anna Acker (u001)" + UserSuffix;    // Error (member endpoint)
    private const string UgProjectXDn = "CN=UG_ProjectX" + GroupSuffix;      // Info (empty-group only)
    private const string GgXDn = "CN=GG_X" + GroupSuffix;                    // Warning (naming-gg)

    [Fact]
    public void ToChunkCommands_WithReport_NodeSevTokensMatchTheBaseline_AcrossChunkSplits()
    {
        // Force a split (cap 1 node/chunk) so every flagged node lands in its own chunk:
        // severity must travel with the node wherever the slicer puts it.
        var report = DemoDefaultReport();
        var model = SeverityModel();

        var chunks = ChunksOf(
            GraphChunker.ToChunkCommands(model, report, belowMap: null, maxNodesPerChunk: 1, maxEdgesPerChunk: 1));

        Assert.Equal("error", SevOf(chunks, DlFsSalesRwDn));
        Assert.Equal("error", SevOf(chunks, User001Dn)); // both-endpoint attribution survives chunking
        Assert.Equal("warning", SevOf(chunks, GgXDn));
        Assert.Equal("info", SevOf(chunks, UgProjectXDn));
    }

    [Fact]
    public void ToChunkCommands_ChunkedNodeObjects_AreStringIdenticalToTheFlatDump_ForSevAndBelow()
    {
        // The "chunked == flat for sev/below" pin: collect every node object emitted across
        // all chunks and compare it field-for-field (raw JSON text) against the same node in
        // the flat SerializeFlat document — sev/below/belowSev included, key order included.
        var report = DemoDefaultReport();
        var model = SeverityModel();
        var belowMap = BelowMap(
            (DlFsSalesRwDn, 4, RuleSeverity.Error),
            (GgXDn, 1, RuleSeverity.Warning));

        var flatNodesByDn = FlatNodesByDn(GraphJson.SerializeFlat(model, report, belowMap));
        var chunkNodes = ChunkNodeJsonByDn(GraphChunker.ToChunkCommands(model, report, belowMap));

        Assert.Equal(flatNodesByDn.Count, chunkNodes.Count);
        foreach (var (dn, flatNodeJson) in flatNodesByDn)
        {
            Assert.True(chunkNodes.TryGetValue(dn, out var chunkNodeJson), $"chunk output dropped node {dn}");
            Assert.Equal(flatNodeJson, chunkNodeJson);
        }
    }

    [Fact]
    public void ToUpdateCommands_WithReport_CarriesSeverity_IdenticallyToShowMode()
    {
        // Update mode shares the slicing path, so the sev/below fields it emits are
        // string-identical to show mode for the same (model, report, belowMap) — only the
        // trailing commit verb differs. This is the lazy-expand survival guarantee (ADR-010
        // D3): after a graphUpdate the re-sent wire fields re-attach the halos.
        var report = DemoDefaultReport();
        var model = SeverityModel();
        var belowMap = BelowMap((DlFsSalesRwDn, 2, RuleSeverity.Error));

        AssertUpdateMatchesShowExceptTrailingVerb(
            GraphChunker.ToChunkCommands(model, report, belowMap),
            GraphChunker.ToUpdateCommands(model, report, belowMap));
    }

    [Fact]
    public void ToChunkCommands_NoReportOverload_EmitsNoSeverityKeys()
    {
        // The pre-AP no-report overload stays byte-clean: a chunk transfer with no report
        // carries zero sev/below/belowSev keys anywhere.
        var model = SeverityModel();

        var commands = GraphChunker.ToChunkCommands(model);

        Assert.All(commands, c =>
        {
            Assert.DoesNotContain("\"sev\"", c, StringComparison.Ordinal);
            Assert.DoesNotContain("\"below\"", c, StringComparison.Ordinal);
            Assert.DoesNotContain("\"belowSev\"", c, StringComparison.Ordinal);
        });
    }

    // --- severity helpers ----------------------------------------------------------------

    /// <summary>The live 19-finding baseline report (full demo snapshot, default ruleset).</summary>
    private static RuleReport DemoDefaultReport()
    {
        var snapshot = new DemoProvider().LoadScopeAsync(DemoRootDn).GetAwaiter().GetResult();
        return RuleEngine.Evaluate(snapshot, RulesetLoader.LoadDefault());
    }

    /// <summary>A model carrying the four pinned baseline DNs as nodes (kind/coords
    /// irrelevant to the DN-keyed severity join).</summary>
    private static GraphModel SeverityModel() =>
        new(
            [
                new GraphNode(DlFsSalesRwDn, "DL_FS-Sales_RW", AdObjectKind.DomainLocalGroup, 1d, 2d, 1, IsRoot: false),
                new GraphNode(User001Dn, "Anna Acker (u001)", AdObjectKind.User, 3d, 4d, 2, IsRoot: false),
                new GraphNode(GgXDn, "GG_X", AdObjectKind.GlobalGroup, 5d, 6d, 1, IsRoot: false),
                new GraphNode(UgProjectXDn, "UG_ProjectX", AdObjectKind.UniversalGroup, 7d, 8d, 1, IsRoot: false),
            ],
            []);

    private static IReadOnlyDictionary<string, (int Count, RuleSeverity Sev)> BelowMap(
        params (string Dn, int Count, RuleSeverity Sev)[] entries)
    {
        var map = new Dictionary<string, (int Count, RuleSeverity Sev)>(Dn.Comparer);
        foreach (var (dn, count, sev) in entries)
        {
            map[dn] = (count, sev);
        }

        return map;
    }

    /// <summary>The sev token of the node with id <paramref name="dn"/>, gathered across all chunks.</summary>
    private static string? SevOf(IEnumerable<JsonObject> chunks, string dn)
    {
        var node = chunks
            .SelectMany(c => c["nodes"] as JsonArray ?? new JsonArray())
            .Single(n => n!["id"]!.GetValue<string>() == dn)!
            .AsObject();
        return node.TryGetPropertyValue("sev", out var sev) ? sev!.GetValue<string>() : null;
    }

    /// <summary>Every flat-document node as JSON text, keyed by id. Normalized through the
    /// SAME JsonNode round-trip as the chunk side so the comparison is content (fields +
    /// order + values), never a JsonDocument-vs-JsonNode representation artifact.</summary>
    private static Dictionary<string, string> FlatNodesByDn(string flatJson)
    {
        var byDn = new Dictionary<string, string>(Dn.Comparer);
        var nodes = JsonNode.Parse(flatJson)!["nodes"]!.AsArray();
        foreach (var node in nodes)
        {
            byDn[node!["id"]!.GetValue<string>()] = node.ToJsonString();
        }

        return byDn;
    }

    /// <summary>Every chunk node as its raw JSON text, keyed by id — the chunks parsed via
    /// the show-mode commit shape (graphCommit trailer).</summary>
    private static Dictionary<string, string> ChunkNodeJsonByDn(IReadOnlyList<string> commands)
    {
        var byDn = new Dictionary<string, string>(Dn.Comparer);
        foreach (var chunk in ChunksOf(commands))
        {
            foreach (var node in (chunk["nodes"] as JsonArray) ?? new JsonArray())
            {
                byDn[node!["id"]!.GetValue<string>()] = node.ToJsonString();
            }
        }

        return byDn;
    }

    /// <summary>Pins ADR-005 D1's "one shared slicing path": for the same model and
    /// caps, update mode emits the EXACT same command strings as show mode except
    /// for the trailing commit verb — the two outputs may never drift apart.</summary>
    private static void AssertUpdateMatchesShowExceptTrailingVerb(
        IReadOnlyList<string> showCommands,
        IReadOnlyList<string> updateCommands)
    {
        Assert.Equal(showCommands.Count, updateCommands.Count);
        Assert.Equal(showCommands.SkipLast(1), updateCommands.SkipLast(1));
        Assert.Equal("graphCommit", CommandType(ParseCommand(showCommands[^1])));
        Assert.Equal("graphUpdate", CommandType(ParseCommand(updateCommands[^1])));
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

    /// <summary>501 nodes + 1001 mixed edges: every default cap forces a split.</summary>
    private static GraphModel MixedOverCapModel() =>
        new(
            [.. Enumerable.Range(0, 501).Select(NodeAt)],
            [
                .. Enumerable.Range(0, 600).Select(MembershipAt),
                .. Enumerable.Range(0, 401).Select(ContainmentAt),
            ]);

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
    /// = <paramref name="commitVerb"/> — <c>graphCommit</c> for show mode, <c>graphUpdate</c>
    /// for update mode — everything before it <c>graphChunk</c>), and returns the chunks.</summary>
    private static List<JsonObject> ChunksOf(
        IReadOnlyList<string> commands, string commitVerb = "graphCommit")
    {
        var parsed = ParseAll(commands);
        Assert.NotEmpty(parsed);
        Assert.Equal(commitVerb, CommandType(parsed[^1]));
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
