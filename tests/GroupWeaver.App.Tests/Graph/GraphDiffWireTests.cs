using System.Text.Json;

using GroupWeaver.App.Graph;
using GroupWeaver.Core.Diff;
using GroupWeaver.Core.Graph;
using GroupWeaver.Core.Model;
using GroupWeaver.Core.Rules;

using Xunit;

namespace GroupWeaver.App.Tests.Graph;

/// <summary>
/// Pins the v0.3 Gap-analysis Slice 4 WIRE contract (ADR-015, #66): the per-element
/// diff token rides the SINGLE <see cref="GraphJson"/> choke point alongside severity,
/// so flat and chunked output can never drift. The diff channel is purely additive and
/// omit-when-null — an element with no diff status (or <see cref="DiffStatus.Common"/>,
/// which is deliberately NOT on the wire) stays byte-identical to the pre-diff output,
/// which guards every existing severity/flat/chunk test.
///
/// <para>The contract mirrors the AP 3.4 severity wire (<see cref="GraphJson"/>'s
/// <c>SeverityWire.ToToken</c>): an internal <c>DiffWire.ToToken</c> maps
/// <see cref="DiffStatus"/> to a lowercase token DECOUPLED from the enum names (the JS
/// selectors key off the tokens), with <see cref="DiffStatus.Common"/> mapping to
/// <c>null</c> so unchanged elements omit the key; <c>NodeDto.Diff</c> /
/// <c>EdgeDto.Diff</c> are trailing <c>WhenWritingNull</c>-ignored fields; <c>MapNodes</c>
/// /<c>MapEdges</c> grow optional trailing diff-map params; and a new
/// <c>SerializeFlat</c> overload plus the <see cref="GraphChunker"/> overloads forward
/// the two maps into that ONE shared mapping path. Edge tokens key off
/// <c>new MembershipEdge(parent, child)</c>; containment edges never carry a token.</para>
/// </summary>
public sealed class GraphDiffWireTests
{
    private const string ParentDn = "CN=DL_FS-Sales_RW,OU=Groups,OU=AGDLP-Demo,DC=weavedemo,DC=example";
    private const string ChildDn = "CN=Anna Acker (u001),OU=Users,OU=AGDLP-Demo,DC=weavedemo,DC=example";

    // The exact pre-diff wire bytes for PinnedModel(), captured against the CURRENT
    // (pre-Slice-4) GraphJson/GraphChunker output. The implementer must keep a null
    // diff-map byte-identical to these — they are the load-bearing regression anchors.
    private const string PinnedLegacyFlat =
        """{"nodes":[{"id":"CN=DL_FS-Sales_RW,OU=Groups,OU=AGDLP-Demo,DC=weavedemo,DC=example","label":"DL_FS-Sales_RW","kind":"DomainLocalGroup","x":1,"y":2},{"id":"CN=Anna Acker (u001),OU=Users,OU=AGDLP-Demo,DC=weavedemo,DC=example","label":"Anna Acker (u001)","kind":"User","x":3,"y":4}],"edges":[{"id":"m0","s":"CN=Anna Acker (u001),OU=Users,OU=AGDLP-Demo,DC=weavedemo,DC=example","t":"CN=DL_FS-Sales_RW,OU=Groups,OU=AGDLP-Demo,DC=weavedemo,DC=example","rel":"member"}]}""";

    private const string PinnedChunk0 =
        """{"type":"graphChunk","nodes":[{"id":"CN=DL_FS-Sales_RW,OU=Groups,OU=AGDLP-Demo,DC=weavedemo,DC=example","label":"DL_FS-Sales_RW","kind":"DomainLocalGroup","x":1,"y":2},{"id":"CN=Anna Acker (u001),OU=Users,OU=AGDLP-Demo,DC=weavedemo,DC=example","label":"Anna Acker (u001)","kind":"User","x":3,"y":4}],"edges":[{"id":"m0","s":"CN=Anna Acker (u001),OU=Users,OU=AGDLP-Demo,DC=weavedemo,DC=example","t":"CN=DL_FS-Sales_RW,OU=Groups,OU=AGDLP-Demo,DC=weavedemo,DC=example","rel":"member"}]}""";

    // --- 1. DiffWire.ToToken mapping ---------------------------------------------------

    /// <summary>The diff-status -> wire-token mapping, DECOUPLED from the enum names
    /// (ADR-015 Slice 4): Added/Removed/Unchecked map to their lowercase tokens, and
    /// Common maps to <c>null</c> so unchanged elements are OMITTED from the wire.</summary>
    [Theory]
    [InlineData(DiffStatus.Added, "added")]
    [InlineData(DiffStatus.Removed, "removed")]
    [InlineData(DiffStatus.Unchecked, "unchecked")]
    public void DiffWire_ToToken_MapsEachNonCommonStatusToItsLowercaseToken(DiffStatus status, string expected)
    {
        Assert.Equal(expected, GraphJson.DiffWire.ToToken(status));
    }

    /// <summary>Common is the only status that is NOT on the wire: it maps to
    /// <c>null</c> so an unchanged element stays byte-identical to the pre-diff wire.</summary>
    [Fact]
    public void DiffWire_ToToken_Common_IsNull_SoUnchangedElementsAreOmitted()
    {
        Assert.Null(GraphJson.DiffWire.ToToken(DiffStatus.Common));
    }

    // --- 2. null-map byte-identical regression (load-bearing) --------------------------

    /// <summary>The single-arg <c>SerializeFlat</c> and the new diff-map overload with a
    /// <c>null</c> diff map produce IDENTICAL bytes, equal to the pinned pre-diff wire —
    /// no <c>"diff"</c> key anywhere. This guards every existing flat/severity test.</summary>
    [Fact]
    public void SerializeFlat_NullDiffMap_IsByteIdenticalToPreDiffWire_NoDiffKey()
    {
        var model = PinnedModel();
        var report = NestingErrorReport();
        var belowMap = BelowMap((ParentDn, 2, RuleSeverity.Warning));

        // The pre-diff overload (report + below-map, no diff maps) is the current wire.
        var legacy = GraphJson.SerializeFlat(model);
        // The new overload, explicit null diff maps, MUST collapse to the same bytes when
        // there is no report either — and never emit a diff key.
        var withNullDiff = GraphJson.SerializeFlat(model, RuleReport.Empty, belowMap: null, nodeDiffMap: null, edgeDiffMap: null);

        Assert.Equal(PinnedLegacyFlat, legacy);
        Assert.Equal(legacy, withNullDiff);
        Assert.DoesNotContain("\"diff\"", withNullDiff, StringComparison.Ordinal);

        // And with a real report + below-map but null diff maps, still no diff key
        // (the diff channel is orthogonal to severity).
        var severityOnly = GraphJson.SerializeFlat(model, report, belowMap, nodeDiffMap: null, edgeDiffMap: null);
        Assert.DoesNotContain("\"diff\"", severityOnly, StringComparison.Ordinal);
    }

    /// <summary>The chunker's pre-diff path stays byte-identical with null diff maps: the
    /// chunk transfer equals the pinned pre-diff chunk strings and carries no diff key.</summary>
    [Fact]
    public void ToChunkCommands_NullDiffMap_IsByteIdenticalToPreDiffChunks_NoDiffKey()
    {
        var model = PinnedModel();

        var legacyChunks = GraphChunker.ToChunkCommands(model);
        var withNullDiff = GraphChunker.ToChunkCommands(
            model, RuleReport.Empty, belowMap: null, nodeDiffMap: null, edgeDiffMap: null);

        Assert.Equal(new[] { PinnedChunk0, """{"type":"graphCommit"}""" }, legacyChunks);
        Assert.Equal(legacyChunks, withNullDiff);
        Assert.All(withNullDiff, c => Assert.DoesNotContain("\"diff\"", c, StringComparison.Ordinal));
    }

    // --- 3. populated node diff --------------------------------------------------------

    /// <summary>A node diff-map drives the per-node <c>diff</c> token: Added/Removed/
    /// Unchecked nodes carry the matching token; a Common node AND a node absent from the
    /// map carry NO <c>diff</c> key (Common -> null, absent -> null, both omitted).</summary>
    [Fact]
    public void SerializeFlat_PopulatedNodeDiffMap_EmitsTokenPerStatus_OmitsCommonAndAbsent()
    {
        const string AddedDn = "CN=GG_New,OU=Groups,OU=AGDLP-Demo,DC=weavedemo,DC=example";
        const string RemovedDn = "CN=GG_Gone,OU=Groups,OU=AGDLP-Demo,DC=weavedemo,DC=example";
        const string UncheckedDn = "CN=DL_Frontier,OU=Groups,OU=AGDLP-Demo,DC=weavedemo,DC=example";
        const string CommonDn = "CN=GG_Same,OU=Groups,OU=AGDLP-Demo,DC=weavedemo,DC=example";
        const string AbsentDn = "CN=GG_Untouched,OU=Groups,OU=AGDLP-Demo,DC=weavedemo,DC=example";

        var model = new GraphModel(
            [
                Node(AddedDn),
                Node(RemovedDn),
                Node(UncheckedDn),
                Node(CommonDn),
                Node(AbsentDn),
            ],
            []);
        var nodeDiffMap = NodeDiffMap(
            (AddedDn, DiffStatus.Added),
            (RemovedDn, DiffStatus.Removed),
            (UncheckedDn, DiffStatus.Unchecked),
            (CommonDn, DiffStatus.Common));

        var json = GraphJson.SerializeFlat(model, RuleReport.Empty, belowMap: null, nodeDiffMap: nodeDiffMap, edgeDiffMap: null);

        Assert.Equal("added", DiffOf(NodeById(json, AddedDn)));
        Assert.Equal("removed", DiffOf(NodeById(json, RemovedDn)));
        Assert.Equal("unchecked", DiffOf(NodeById(json, UncheckedDn)));
        Assert.False(NodeById(json, CommonDn).TryGetProperty("diff", out _), "Common -> null -> no diff key");
        Assert.False(NodeById(json, AbsentDn).TryGetProperty("diff", out _), "absent from map -> no diff key");
    }

    // --- 4. populated edge diff --------------------------------------------------------

    /// <summary>An edge diff-map drives the per-edge <c>diff</c> token, keyed by
    /// <c>new MembershipEdge(parent, child)</c>: the matching membership edge carries the
    /// token; a Common/absent membership edge and EVERY containment edge carry none.</summary>
    [Fact]
    public void SerializeFlat_PopulatedEdgeDiffMap_TokensMembershipEdges_NeverContainment()
    {
        const string AddedChild = "CN=Added,OU=Users,OU=AGDLP-Demo,DC=weavedemo,DC=example";
        const string CommonChild = "CN=Common,OU=Users,OU=AGDLP-Demo,DC=weavedemo,DC=example";
        const string AbsentChild = "CN=Absent,OU=Users,OU=AGDLP-Demo,DC=weavedemo,DC=example";
        const string OuDn = "OU=Users,OU=AGDLP-Demo,DC=weavedemo,DC=example";

        var model = new GraphModel(
            [],
            [
                new GraphEdge(GraphEdgeKind.Membership, ParentDn, AddedChild),
                new GraphEdge(GraphEdgeKind.Membership, ParentDn, CommonChild),
                new GraphEdge(GraphEdgeKind.Membership, ParentDn, AbsentChild),
                // A containment edge whose (parent, child) is ALSO a diff-map key: it must
                // still never carry a token — containment is not membership.
                new GraphEdge(GraphEdgeKind.Containment, OuDn, AddedChild),
            ]);
        var edgeDiffMap = EdgeDiffMap(
            (new MembershipEdge(ParentDn, AddedChild), DiffStatus.Added),
            (new MembershipEdge(ParentDn, CommonChild), DiffStatus.Common),
            // A key matching the containment edge's (parent, child) - proves containment
            // never reads the edge diff map even on a key hit.
            (new MembershipEdge(OuDn, AddedChild), DiffStatus.Removed));

        var json = GraphJson.SerializeFlat(model, RuleReport.Empty, belowMap: null, nodeDiffMap: null, edgeDiffMap: edgeDiffMap);

        Assert.Equal("added", DiffOf(EdgeById(json, "m0"))); // ParentDn -> AddedChild
        Assert.False(EdgeById(json, "m1").TryGetProperty("diff", out _), "Common membership edge -> no diff key");
        Assert.False(EdgeById(json, "m2").TryGetProperty("diff", out _), "absent membership edge -> no diff key");
        Assert.False(EdgeById(json, "c0").TryGetProperty("diff", out _), "containment edge never carries a diff token");
    }

    // --- 5. chunked == flat parity with diff -------------------------------------------

    /// <summary>The single-choke-point invariant under diff: the per-node and per-edge
    /// diff tokens emitted by <c>ToChunkCommands</c> are identical to <c>SerializeFlat</c>
    /// for the same model + maps — the diff tokens survive slicing identically.</summary>
    [Fact]
    public void ToChunkCommands_WithDiffMaps_EmitsSameDiffTokensAsSerializeFlat()
    {
        const string AddedDn = "CN=GG_New,OU=Groups,OU=AGDLP-Demo,DC=weavedemo,DC=example";
        const string MemberDn = "CN=Member,OU=Users,OU=AGDLP-Demo,DC=weavedemo,DC=example";
        var model = new GraphModel(
            [Node(ParentDn), Node(AddedDn), Node(MemberDn)],
            [new GraphEdge(GraphEdgeKind.Membership, ParentDn, MemberDn)]);
        var nodeDiffMap = NodeDiffMap((AddedDn, DiffStatus.Added), (ParentDn, DiffStatus.Removed));
        var edgeDiffMap = EdgeDiffMap((new MembershipEdge(ParentDn, MemberDn), DiffStatus.Unchecked));

        var flatNodeDiffs = NodeDiffByDn(
            GraphJson.SerializeFlat(model, RuleReport.Empty, belowMap: null, nodeDiffMap: nodeDiffMap, edgeDiffMap: edgeDiffMap));
        var chunkNodeDiffs = ChunkNodeDiffByDn(
            GraphChunker.ToChunkCommands(model, RuleReport.Empty, belowMap: null, nodeDiffMap: nodeDiffMap, edgeDiffMap: edgeDiffMap));

        Assert.Equal(flatNodeDiffs, chunkNodeDiffs);

        var flatEdgeDiffs = EdgeDiffById(
            GraphJson.SerializeFlat(model, RuleReport.Empty, belowMap: null, nodeDiffMap: nodeDiffMap, edgeDiffMap: edgeDiffMap));
        var chunkEdgeDiffs = ChunkEdgeDiffById(
            GraphChunker.ToChunkCommands(model, RuleReport.Empty, belowMap: null, nodeDiffMap: nodeDiffMap, edgeDiffMap: edgeDiffMap));

        Assert.Equal(flatEdgeDiffs, chunkEdgeDiffs);
        // Sanity: the parity assertion is non-vacuous - the tokens are actually present.
        Assert.Equal("added", flatNodeDiffs[AddedDn]);
        Assert.Equal("unchecked", flatEdgeDiffs["m0"]);
    }

    // --- 6. severity + diff coexist ----------------------------------------------------

    /// <summary>A node that is BOTH a finding (carries <c>sev</c>/<c>below</c>/
    /// <c>belowSev</c>) AND Added (carries <c>diff</c>) emits every key — the diff channel
    /// is orthogonal to the severity channel and disturbs neither.</summary>
    [Fact]
    public void SerializeFlat_SeverityAndDiffCoexist_OnTheSameNode()
    {
        var model = new GraphModel([Node(ParentDn)], []);
        var report = NestingErrorReport();
        var belowMap = BelowMap((ParentDn, 2, RuleSeverity.Warning));
        var nodeDiffMap = NodeDiffMap((ParentDn, DiffStatus.Added));

        var node = NodeById(
            GraphJson.SerializeFlat(model, report, belowMap, nodeDiffMap: nodeDiffMap, edgeDiffMap: null),
            ParentDn);

        Assert.Equal("error", node.GetProperty("sev").GetString());
        Assert.Equal(2, node.GetProperty("below").GetInt32());
        Assert.Equal("warning", node.GetProperty("belowSev").GetString());
        Assert.Equal("added", node.GetProperty("diff").GetString());
    }

    // --- helpers -----------------------------------------------------------------------

    /// <summary>The pinned two-node + one-membership-edge model the null-map regression
    /// byte-strings were captured against.</summary>
    private static GraphModel PinnedModel() =>
        new(
            [
                new GraphNode(ParentDn, "DL_FS-Sales_RW", AdObjectKind.DomainLocalGroup, 1d, 2d, 1, IsRoot: false),
                new GraphNode(ChildDn, "Anna Acker (u001)", AdObjectKind.User, 3d, 4d, 2, IsRoot: false),
            ],
            [new GraphEdge(GraphEdgeKind.Membership, ParentDn, ChildDn)]);

    /// <summary>A one-finding report: a DL &lt;- User nesting Error on [parent, child], so
    /// MaxSeverityByDn[parent] = Error (the same baseline shape the severity tests use).</summary>
    private static RuleReport NestingErrorReport() =>
        new(
            [
                new RuleViolation
                {
                    RuleId = RuleIds.Nesting,
                    Severity = RuleSeverity.Error,
                    Dns = [ParentDn, ChildDn],
                    Message = "DL must not nest a User",
                },
            ],
            Array.Empty<string>());

    private static GraphNode Node(string dn) =>
        new(dn, dn, AdObjectKind.GlobalGroup, 1d, 2d, 1, IsRoot: false);

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

    private static IReadOnlyDictionary<string, DiffStatus> NodeDiffMap(
        params (string Dn, DiffStatus Status)[] entries)
    {
        var map = new Dictionary<string, DiffStatus>(Dn.Comparer);
        foreach (var (dn, status) in entries)
        {
            map[dn] = status;
        }

        return map;
    }

    private static IReadOnlyDictionary<MembershipEdge, DiffStatus> EdgeDiffMap(
        params (MembershipEdge Edge, DiffStatus Status)[] entries)
    {
        var map = new Dictionary<MembershipEdge, DiffStatus>();
        foreach (var (edge, status) in entries)
        {
            map[edge] = status;
        }

        return map;
    }

    /// <summary>The <c>diff</c> token of a wire element, or <c>null</c> if the key is absent.</summary>
    private static string? DiffOf(JsonElement element) =>
        element.TryGetProperty("diff", out var diff) ? diff.GetString() : null;

    private static JsonElement NodeById(string flatJson, string dn) =>
        ElementById(flatJson, "nodes", "id", dn);

    private static JsonElement EdgeById(string flatJson, string id) =>
        ElementById(flatJson, "edges", "id", id);

    private static JsonElement ElementById(string flatJson, string array, string key, string value)
    {
        using var document = JsonDocument.Parse(flatJson);
        var element = document.RootElement.GetProperty(array).EnumerateArray()
            .Single(e => e.GetProperty(key).GetString() == value);
        return element.Clone();
    }

    /// <summary>Every flat-document node's <c>diff</c> token (or <c>null</c>) keyed by id.</summary>
    private static Dictionary<string, string?> NodeDiffByDn(string flatJson)
    {
        var byDn = new Dictionary<string, string?>(Dn.Comparer);
        using var document = JsonDocument.Parse(flatJson);
        foreach (var node in document.RootElement.GetProperty("nodes").EnumerateArray())
        {
            byDn[node.GetProperty("id").GetString()!] = DiffOf(node);
        }

        return byDn;
    }

    private static Dictionary<string, string?> EdgeDiffById(string flatJson)
    {
        var byId = new Dictionary<string, string?>(StringComparer.Ordinal);
        using var document = JsonDocument.Parse(flatJson);
        foreach (var edge in document.RootElement.GetProperty("edges").EnumerateArray())
        {
            byId[edge.GetProperty("id").GetString()!] = DiffOf(edge);
        }

        return byId;
    }

    /// <summary>Every chunk node's <c>diff</c> token (or <c>null</c>) keyed by id, gathered
    /// across all <c>graphChunk</c> commands of a show-mode transfer.</summary>
    private static Dictionary<string, string?> ChunkNodeDiffByDn(IReadOnlyList<string> commands)
    {
        var byDn = new Dictionary<string, string?>(Dn.Comparer);
        foreach (var node in ChunkArrays(commands, "nodes"))
        {
            byDn[node.GetProperty("id").GetString()!] = DiffOf(node);
        }

        return byDn;
    }

    private static Dictionary<string, string?> ChunkEdgeDiffById(IReadOnlyList<string> commands)
    {
        var byId = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var edge in ChunkArrays(commands, "edges"))
        {
            byId[edge.GetProperty("id").GetString()!] = DiffOf(edge);
        }

        return byId;
    }

    /// <summary>Every element of the named array across all <c>graphChunk</c> commands
    /// (the trailing <c>graphCommit</c>/<c>graphUpdate</c> carries no such array).</summary>
    private static List<JsonElement> ChunkArrays(IReadOnlyList<string> commands, string array)
    {
        var elements = new List<JsonElement>();
        foreach (var command in commands)
        {
            using var document = JsonDocument.Parse(command);
            if (document.RootElement.TryGetProperty(array, out var arr))
            {
                foreach (var element in arr.EnumerateArray())
                {
                    elements.Add(element.Clone());
                }
            }
        }

        return elements;
    }
}
