using System.Globalization;
using System.Text.Json;

using GroupWeaver.App.Graph;
using GroupWeaver.Core.Graph;
using GroupWeaver.Core.Model;
using GroupWeaver.Core.Rules;
using GroupWeaver.Providers;

using Xunit;

namespace GroupWeaver.App.Tests.Graph;

/// <summary>
/// Pins the ADR-004 D4 wire contract of <see cref="GraphJson.SerializeFlat"/> with
/// exact-string assertions over small hand-built models (AP 2.2 S4): camelCase
/// property names, DN ids byte-verbatim (never canonicalized), kind enum names
/// verbatim (never camel-cased), <c>"root":true</c> emitted only on the root node
/// (absent otherwise — never <c>false</c>), per-kind <c>m</c>/<c>c</c> edge-id
/// counters in model edge order, the membership s/t orientation flip
/// (s := member, t := group), invariant '.' decimals — the German-localized
/// box guard (.claude/rules/lab-environment.md) — and the default-STJ-encoder
/// all-ASCII wire guarantee that keeps InvokeScript JS-literal embedding safe
/// (issue #28).
/// </summary>
public sealed class GraphJsonTests
{
    // --- nodes ---------------------------------------------------------------------

    [Fact]
    public void SerializeFlat_RootNode_EmitsRootTrue_ExactString()
    {
        var model = new GraphModel(
            [new GraphNode(
                "OU=AGDLP-Demo,DC=weavedemo,DC=example", "AGDLP-Demo",
                AdObjectKind.OrganizationalUnit, X: 0d, Y: 0d, Ring: 0, IsRoot: true)],
            []);

        Assert.Equal(
            """{"nodes":[{"id":"OU=AGDLP-Demo,DC=weavedemo,DC=example","label":"AGDLP-Demo","kind":"OrganizationalUnit","x":0,"y":0,"root":true}],"edges":[]}""",
            GraphJson.SerializeFlat(model));
    }

    [Fact]
    public void SerializeFlat_NonRootNode_HasNoRootProperty_ExactString()
    {
        var model = new GraphModel(
            [new GraphNode(
                "CN=GG_Sales,OU=Groups,OU=Root,DC=x", "GG_Sales",
                AdObjectKind.GlobalGroup, X: 12.5, Y: -3.5, Ring: 2, IsRoot: false)],
            []);

        var json = GraphJson.SerializeFlat(model);

        // "root" is ABSENT on non-root nodes - never emitted as false (ADR-004 D4).
        Assert.Equal(
            """{"nodes":[{"id":"CN=GG_Sales,OU=Groups,OU=Root,DC=x","label":"GG_Sales","kind":"GlobalGroup","x":12.5,"y":-3.5}],"edges":[]}""",
            json);
        Assert.DoesNotContain("\"root\":false", json, StringComparison.Ordinal);
    }

    [Fact]
    public void SerializeFlat_NodesKeepModelOrder()
    {
        var model = new GraphModel(
            [
                new GraphNode("OU=Root,DC=x", "Root", AdObjectKind.OrganizationalUnit, 0d, 0d, 0, IsRoot: true),
                new GraphNode("CN=U1,OU=Root,DC=x", "U1", AdObjectKind.User, 150d, 0.5, 1, IsRoot: false),
            ],
            []);

        Assert.Equal(
            """{"nodes":[{"id":"OU=Root,DC=x","label":"Root","kind":"OrganizationalUnit","x":0,"y":0,"root":true},{"id":"CN=U1,OU=Root,DC=x","label":"U1","kind":"User","x":150,"y":0.5}],"edges":[]}""",
            GraphJson.SerializeFlat(model));
    }

    [Theory]
    [InlineData(AdObjectKind.User)]
    [InlineData(AdObjectKind.GlobalGroup)]
    [InlineData(AdObjectKind.DomainLocalGroup)]
    [InlineData(AdObjectKind.UniversalGroup)]
    [InlineData(AdObjectKind.OrganizationalUnit)]
    [InlineData(AdObjectKind.Computer)]
    [InlineData(AdObjectKind.External)]
    public void SerializeFlat_KindIsEnumNameVerbatim_NeverCamelCased(AdObjectKind kind)
    {
        var model = new GraphModel(
            [new GraphNode("CN=N,DC=x", "N", kind, 1d, 2d, 1, IsRoot: false)],
            []);

        // The camelCase naming policy applies to PROPERTY names only - kind values
        // are AdObjectKind enum names verbatim (e.g. "GlobalGroup", not "globalGroup").
        Assert.Contains($"\"kind\":\"{kind}\"", GraphJson.SerializeFlat(model), StringComparison.Ordinal);
    }

    // --- edges ---------------------------------------------------------------------

    [Fact]
    public void SerializeFlat_MembershipEdge_FlipsOrientation_MemberIsSource_ExactString()
    {
        // Core keeps the semantic direction (ParentDn = group, ChildDn = member);
        // the wire flips it here: s := member, t := group ("is member of", the
        // A→G→DL reading - ADR-004 D2/D4).
        var model = new GraphModel(
            [],
            [new GraphEdge(
                GraphEdgeKind.Membership,
                ParentDn: "CN=GG_Sales,OU=Groups,OU=Root,DC=x",
                ChildDn: "CN=Anna,OU=Users,OU=Root,DC=x")]);

        Assert.Equal(
            """{"nodes":[],"edges":[{"id":"m0","s":"CN=Anna,OU=Users,OU=Root,DC=x","t":"CN=GG_Sales,OU=Groups,OU=Root,DC=x","rel":"member"}]}""",
            GraphJson.SerializeFlat(model));
    }

    [Fact]
    public void SerializeFlat_ContainmentEdge_ContainerIsSource_ExactString()
    {
        // Containment is NOT flipped: s := container (ParentDn), t := contained child.
        var model = new GraphModel(
            [],
            [new GraphEdge(
                GraphEdgeKind.Containment,
                ParentDn: "OU=Users,OU=Root,DC=x",
                ChildDn: "CN=Anna,OU=Users,OU=Root,DC=x")]);

        Assert.Equal(
            """{"nodes":[],"edges":[{"id":"c0","s":"OU=Users,OU=Root,DC=x","t":"CN=Anna,OU=Users,OU=Root,DC=x","rel":"contains"}]}""",
            GraphJson.SerializeFlat(model));
    }

    [Fact]
    public void SerializeFlat_EdgeIds_CountPerKindInModelEdgeOrder_ExactString()
    {
        // m- and c-counters run independently, each in model edge order, and the
        // emitted edge order IS the model order (kinds interleave freely).
        const string Ou = "OU=X,DC=x";
        var model = new GraphModel(
            [],
            [
                new GraphEdge(GraphEdgeKind.Membership, "CN=G1,OU=X,DC=x", "CN=U1,OU=X,DC=x"),
                new GraphEdge(GraphEdgeKind.Containment, Ou, "CN=U1,OU=X,DC=x"),
                new GraphEdge(GraphEdgeKind.Membership, "CN=G2,OU=X,DC=x", "CN=U2,OU=X,DC=x"),
                new GraphEdge(GraphEdgeKind.Containment, Ou, "CN=G1,OU=X,DC=x"),
                new GraphEdge(GraphEdgeKind.Containment, Ou, "CN=G2,OU=X,DC=x"),
            ]);

        Assert.Equal(
            """{"nodes":[],"edges":[{"id":"m0","s":"CN=U1,OU=X,DC=x","t":"CN=G1,OU=X,DC=x","rel":"member"},{"id":"c0","s":"OU=X,DC=x","t":"CN=U1,OU=X,DC=x","rel":"contains"},{"id":"m1","s":"CN=U2,OU=X,DC=x","t":"CN=G2,OU=X,DC=x","rel":"member"},{"id":"c1","s":"OU=X,DC=x","t":"CN=G1,OU=X,DC=x","rel":"contains"},{"id":"c2","s":"OU=X,DC=x","t":"CN=G2,OU=X,DC=x","rel":"contains"}]}""",
            GraphJson.SerializeFlat(model));
    }

    // --- localization & escaping ----------------------------------------------------

    [Fact]
    public void SerializeFlat_UnderGermanCulture_StillUsesInvariantDecimalPoint()
    {
        var culture = CultureInfo.CurrentCulture;
        var uiCulture = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");
            CultureInfo.CurrentUICulture = new CultureInfo("de-DE");

            var model = new GraphModel(
                [new GraphNode("CN=N,DC=x", "N", AdObjectKind.User, X: 12.5, Y: -0.5, Ring: 1, IsRoot: false)],
                []);

            // de-DE formats 12.5 as "12,5" - the wire is invariant, ALWAYS '.'
            // (this box's DC is German-localized; see .claude/rules/lab-environment.md).
            Assert.Equal(
                """{"nodes":[{"id":"CN=N,DC=x","label":"N","kind":"User","x":12.5,"y":-0.5}],"edges":[]}""",
                GraphJson.SerializeFlat(model));
        }
        finally
        {
            CultureInfo.CurrentCulture = culture;
            CultureInfo.CurrentUICulture = uiCulture;
        }
    }

    [Fact]
    public void SerializeFlat_DnWithEscapedCommaAndUmlaut_SurvivesRoundTripByteIdentically()
    {
        // An escaped comma inside the RDN plus umlauts: the DN must come back
        // byte-identical after a JSON round-trip - ids are DNs stored as-given,
        // never canonicalized (.claude/rules/data-model.md).
        const string Dn = "CN=Müller\\, Jörg,OU=Gruppen,OU=Root,DC=x";
        const string ParentDn = "OU=Gruppen,OU=Root,DC=x";
        var model = new GraphModel(
            [new GraphNode(Dn, "Müller, Jörg", AdObjectKind.User, 1.5, 2.5, 1, IsRoot: false)],
            [new GraphEdge(GraphEdgeKind.Containment, ParentDn, Dn)]);

        using var document = JsonDocument.Parse(GraphJson.SerializeFlat(model));

        var root = document.RootElement;
        Assert.Equal(Dn, root.GetProperty("nodes")[0].GetProperty("id").GetString());
        Assert.Equal("Müller, Jörg", root.GetProperty("nodes")[0].GetProperty("label").GetString());
        Assert.Equal(ParentDn, root.GetProperty("edges")[0].GetProperty("s").GetString());
        Assert.Equal(Dn, root.GetProperty("edges")[0].GetProperty("t").GetString());
    }

    [Fact]
    public void SerializeFlat_LabelWithLineSeparatorsAndUmlaut_WireStringIsAllAscii()
    {
        // CytoscapeGraphRenderer embeds every wire string verbatim as a JS object
        // literal inside InvokeScript. That is only safe while GraphJson keeps the
        // DEFAULT STJ encoder, which escapes ALL non-ASCII to \uXXXX - including
        // U+2028/U+2029 (legal in JSON strings, fatal inside a JS string literal).
        // This pins the encoder (reviewer finding, PR #27 / issue #28): on
        // .NET 8, BOTH known relaxed encoders - UnsafeRelaxedJsonEscaping and
        // JavaScriptEncoder.Create(...) - keep escaping U+2028/U+2029, yet both
        // emit other non-ASCII like 'ü' raw. The umlaut is therefore the active
        // tripwire for either relaxed encoder; the U+2028/U+2029 inputs stay in
        // the test data because they document the actual InvokeScript hazard
        // and guard against hypothetical encoders that would emit them raw.
        // Either way a relaxed encoder makes the wire non-ASCII and this test
        // fails in CI instead of InvokeScript failing at runtime.
        const string Label = "Vertrieb\u2028Süd\u2029Team";
        var model = new GraphModel(
            [new GraphNode("CN=GG_VertriebSüd,OU=Gruppen,DC=x", Label, AdObjectKind.GlobalGroup, 1d, 2d, 1, IsRoot: false)],
            []);

        var json = GraphJson.SerializeFlat(model);

        Assert.All(json, c => Assert.True(
            c < 0x80,
            $"non-ASCII char U+{(int)c:X4} reached the wire - JS-literal embedding in InvokeScript is no longer safe"));

        // Escaped as data, not dropped: the separators and the umlaut must still
        // round-trip - the pin forbids raw bytes on the wire, not the characters.
        Assert.Contains("\\u2028", json, StringComparison.Ordinal);
        Assert.Contains("\\u2029", json, StringComparison.Ordinal);
        using var document = JsonDocument.Parse(json);
        Assert.Equal(Label, document.RootElement.GetProperty("nodes")[0].GetProperty("label").GetString());
    }

    // --- severity -> wire mapping (AP 3.4 S1, ADR-010) =================================
    //
    // ADR-010 D1/D2: severity is joined ONLY in the App wire mapper (GraphJson.MapNodes),
    // never in Core/GraphBuilder. Three optional NodeDto fields, all WhenWritingNull-
    // ignored so an UNFLAGGED node stays byte-identical to the pre-AP wire:
    //   sev      = SevToken(report.MaxSeverityByDn[dn])            -> "error"|"warning"|"info"
    //   below    = distinct-finding count among loaded descendants -> int, emitted only > 0
    //   belowSev = SevToken(max severity among those)              -> token form, with `below`
    // The new signature pins the ONE shared node path used by both flat and chunked output:
    //   MapNodes(nodes, report, belowMap)  -> a `SerializeFlat(model, report, belowMap)`
    //   overload exposes it; the single-arg SerializeFlat keeps RuleReport.Empty/null.
    //
    // SPEC CONFLICT (flagged loudly): the "Final rendering decision" pins the MapNodes
    // signature with `IReadOnlyDictionary<string,RuleSeverity>? belowByDn` (value = max
    // severity ONLY), yet the SAME section requires the wire to carry `below` (an int
    // COUNT) AND `belowSev`. A severity-only map cannot yield the count, and MapNodes
    // receives no descendant set to recompute it (Walk is VM-only, data-model.md). The
    // only below-map shape that produces BOTH pinned wire fields from inside MapNodes is
    // the `(int Count, RuleSeverity Sev)` value form the spec itself falls back to
    // ("Final VM/sidebar/eval design": "ship a Dictionary<string,(int Count, RuleSeverity
    // Sev)>; MapNodes reads both"). These tests therefore pin the COUNT-carrying map.
    // The implementer must reconcile the two spec phrasings to this shape.

    /// <summary>The demo dataset root (fewest RDN components), pinned against
    /// <c>src/Providers/Demo/demo-directory.json</c>'s <c>rootDn</c> — same constant the
    /// AppCli dump-graph tests use.</summary>
    private const string DemoRootDn = "OU=AGDLP-Demo,DC=weavedemo,DC=example";

    private const string GroupSuffix = ",OU=Groups,OU=AGDLP-Demo,DC=weavedemo,DC=example";
    private const string UserSuffix = ",OU=Users,OU=AGDLP-Demo,DC=weavedemo,DC=example";

    // Baseline DNs lifted verbatim from the 19-finding subject table
    // (RuleEngineDemoBaselineTests). MaxSeverityByDn for each is pinned there:
    //   DL_FS-Sales_RW  -> Error   (DL <- User nesting error, parent endpoint)
    //   Anna Acker(u001)-> Error   (the member endpoint of that SAME nesting error)
    //   UG_ProjectX     -> Info    (empty-group only)
    //   GG_X            -> Warning (naming-gg warning outranks its empty-group Info)
    private const string DlFsSalesRwDn = "CN=DL_FS-Sales_RW" + GroupSuffix;
    private const string User001Dn = "CN=Anna Acker (u001)" + UserSuffix;
    private const string UgProjectXDn = "CN=UG_ProjectX" + GroupSuffix;
    private const string GgXDn = "CN=GG_X" + GroupSuffix;

    // --- unflagged byte-identity (the omit-when-null guarantee) ------------------------

    [Fact]
    public void SerializeFlat_NoReport_IsByteIdenticalToTheSingleArgOverload_NoSeverityKeys()
    {
        // The single-arg SerializeFlat must keep emitting the pre-AP wire verbatim, and
        // the new (model, report, belowMap) overload with an EMPTY report + null below-map
        // must produce the IDENTICAL bytes — no sev/below/belowSev keys anywhere.
        var model = new GraphModel(
            [
                new GraphNode(DlFsSalesRwDn, "DL_FS-Sales_RW", AdObjectKind.DomainLocalGroup, 1d, 2d, 1, IsRoot: false),
                new GraphNode(User001Dn, "Anna Acker (u001)", AdObjectKind.User, 3d, 4d, 2, IsRoot: false),
            ],
            []);

        var legacy = GraphJson.SerializeFlat(model);
        var withEmptyReport = GraphJson.SerializeFlat(model, RuleReport.Empty, belowMap: null);

        Assert.Equal(legacy, withEmptyReport);
        Assert.DoesNotContain("\"sev\"", withEmptyReport, StringComparison.Ordinal);
        Assert.DoesNotContain("\"below\"", withEmptyReport, StringComparison.Ordinal);
        Assert.DoesNotContain("\"belowSev\"", withEmptyReport, StringComparison.Ordinal);
    }

    [Fact]
    public void SerializeFlat_FlaggedReport_UnflaggedNodeIsByteIdenticalToTheNoReportWire()
    {
        // Even when the report DOES carry findings, a node that is NOT an attached DN must
        // serialize byte-for-byte the same as it would with no report at all (ADR-010 D2:
        // "no finding => no sev field => byte-identical to a pre-AP node").
        const string UnflaggedDn = "CN=GG_Conformant" + GroupSuffix;
        var unflagged = new GraphModel(
            [new GraphNode(UnflaggedDn, "GG_Conformant", AdObjectKind.GlobalGroup, 5d, -6d, 2, IsRoot: false)],
            []);

        var report = DemoDefaultReport();

        Assert.Equal(
            GraphJson.SerializeFlat(unflagged),
            GraphJson.SerializeFlat(unflagged, report, belowMap: null));
    }

    // --- per-node sev token from MaxSeverityByDn ---------------------------------------

    [Theory]
    [InlineData(DlFsSalesRwDn, "error")]   // DL parent of the DL <- User nesting error
    [InlineData(User001Dn, "error")]       // its member endpoint: BOTH-endpoint attribution
    [InlineData(GgXDn, "warning")]         // naming-gg warning outranks its empty-group Info
    [InlineData(UgProjectXDn, "info")]     // empty-group only
    public void SerializeFlat_FlaggedNode_CarriesTheExactSevToken(string dn, string expectedSev)
    {
        // The 19-finding default-ruleset report over the demo snapshot is the live source
        // of truth: MaxSeverityByDn[dn] -> lowercase wire token, decoupled from enum names.
        var report = DemoDefaultReport();
        var model = SingleNodeModel(dn);

        var node = FlatNode(GraphJson.SerializeFlat(model, report, belowMap: null), dn);

        Assert.Equal(expectedSev, node.GetProperty("sev").GetString());
        // A pure per-node sev (no below-map) carries neither roll-up field.
        Assert.False(node.TryGetProperty("below", out _), "below must be absent without a below-map");
        Assert.False(node.TryGetProperty("belowSev", out _), "belowSev must be absent without a below-map");
    }

    [Fact]
    public void SerializeFlat_BothEndpointAttribution_MarksTheMemberUserNodeError_NotJustTheParent()
    {
        // The pinned both-endpoint case from the prompt: the DL <- User nesting error
        // attaches to BOTH the DL parent AND the user member, so the user node (Anna Acker
        // (u001)) must carry sev:"error" even though a User is never itself a rule subject.
        var report = DemoDefaultReport();
        var model = SingleNodeModel(User001Dn);

        var node = FlatNode(GraphJson.SerializeFlat(model, report, belowMap: null), User001Dn);

        Assert.Equal("error", node.GetProperty("sev").GetString());
    }

    [Fact]
    public void SerializeFlat_SevTokenIsLowercase_NotTheEnumName()
    {
        // The wire tokens are lowercase ("error"/"warning"/"info"), NOT the RuleSeverity
        // enum names ("Error"/"Warning"/"Info") — same decoupling as the kind tokens being
        // enum names verbatim, but inverted for severity (ADR-010 D2, SeverityWire helper).
        var report = DemoDefaultReport();
        var json = GraphJson.SerializeFlat(SingleNodeModel(DlFsSalesRwDn), report, belowMap: null);

        Assert.Contains("\"sev\":\"error\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"sev\":\"Error\"", json, StringComparison.Ordinal);
    }

    // --- roll-up below / belowSev from the below-map -----------------------------------

    [Fact]
    public void SerializeFlat_NodeInBelowMap_EmitsBelowCountAndBelowSevToken()
    {
        // The roll-up wire fields come from the VM-computed below-map (count + max severity
        // among loaded descendants). below is the int COUNT; belowSev is the token form.
        var model = SingleNodeModel(DlFsSalesRwDn);
        var belowMap = BelowMap((DlFsSalesRwDn, 3, RuleSeverity.Error));

        var node = FlatNode(GraphJson.SerializeFlat(model, RuleReport.Empty, belowMap), DlFsSalesRwDn);

        Assert.Equal(3, node.GetProperty("below").GetInt32());
        Assert.Equal("error", node.GetProperty("belowSev").GetString());
    }

    [Theory]
    [InlineData(RuleSeverity.Warning, "warning")]
    [InlineData(RuleSeverity.Info, "info")]
    public void SerializeFlat_BelowSev_IsTheLowercaseTokenOfTheBelowMapSeverity(
        RuleSeverity below, string expectedToken)
    {
        var model = SingleNodeModel(DlFsSalesRwDn);
        var belowMap = BelowMap((DlFsSalesRwDn, 1, below));

        var node = FlatNode(GraphJson.SerializeFlat(model, RuleReport.Empty, belowMap), DlFsSalesRwDn);

        Assert.Equal(expectedToken, node.GetProperty("belowSev").GetString());
    }

    [Fact]
    public void SerializeFlat_NodeAbsentFromBelowMap_HasNeitherBelowNorBelowSev()
    {
        // A node not in the below-map (no flagged loaded descendants) must omit BOTH roll-up
        // fields entirely (WhenWritingNull) — never below:0, never belowSev:null.
        var model = SingleNodeModel(DlFsSalesRwDn);
        var belowMap = BelowMap(("CN=Someone_Else" + GroupSuffix, 2, RuleSeverity.Error));

        var node = FlatNode(GraphJson.SerializeFlat(model, RuleReport.Empty, belowMap), DlFsSalesRwDn);

        Assert.False(node.TryGetProperty("below", out _), "below must be absent for a node not in the below-map");
        Assert.False(node.TryGetProperty("belowSev", out _), "belowSev must be absent for a node not in the below-map");
        Assert.DoesNotContain("\"below\":0", FlatJson(model, RuleReport.Empty, belowMap), StringComparison.Ordinal);
    }

    [Fact]
    public void SerializeFlat_ZeroCountBelowEntry_IsTreatedAsAbsent_NeverEmittedAsBelowZero()
    {
        // below is emitted "only when > 0" (ADR-010 D2). A degenerate count-0 below-map entry
        // must NOT reach the wire as below:0 — the omit-when-zero rule is part of the contract.
        var model = SingleNodeModel(DlFsSalesRwDn);
        var belowMap = BelowMap((DlFsSalesRwDn, 0, RuleSeverity.Error));

        var node = FlatNode(GraphJson.SerializeFlat(model, RuleReport.Empty, belowMap), DlFsSalesRwDn);

        Assert.False(node.TryGetProperty("below", out _), "a count-0 below entry must not emit below");
        Assert.False(node.TryGetProperty("belowSev", out _), "a count-0 below entry must not emit belowSev");
    }

    [Fact]
    public void SerializeFlat_SevAndBelowCoexist_OnTheSameNode()
    {
        // sev (own findings) and below (descendant roll-up) are independent channels and may
        // both appear on one node — a flagged group that also hides flagged descendants.
        var report = DemoDefaultReport();
        var model = SingleNodeModel(DlFsSalesRwDn);
        var belowMap = BelowMap((DlFsSalesRwDn, 2, RuleSeverity.Warning));

        var node = FlatNode(GraphJson.SerializeFlat(model, report, belowMap), DlFsSalesRwDn);

        Assert.Equal("error", node.GetProperty("sev").GetString());
        Assert.Equal(2, node.GetProperty("below").GetInt32());
        Assert.Equal("warning", node.GetProperty("belowSev").GetString());
    }

    // --- helpers (severity) ------------------------------------------------------------

    /// <summary>The live 19-finding baseline report: the FULL demo snapshot under the
    /// embedded default ruleset (RuleEngineDemoBaselineTests is the authoritative table).
    /// Built in-test per the slice brief — never a hand-rolled report.</summary>
    private static RuleReport DemoDefaultReport()
    {
        var snapshot = new DemoProvider().LoadScopeAsync(DemoRootDn).GetAwaiter().GetResult();
        return RuleEngine.Evaluate(snapshot, RulesetLoader.LoadDefault());
    }

    /// <summary>A one-node model carrying <paramref name="dn"/> (kind/coords irrelevant to
    /// the severity join, which is keyed purely on the DN id).</summary>
    private static GraphModel SingleNodeModel(string dn) =>
        new([new GraphNode(dn, dn, AdObjectKind.DomainLocalGroup, 1d, 2d, 1, IsRoot: false)], []);

    /// <summary>The count-carrying below-map shape these tests pin (see the SPEC CONFLICT
    /// note above): node DN -> (distinct-finding count among loaded descendants, max
    /// severity among them).</summary>
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

    private static string FlatJson(
        GraphModel model, RuleReport report, IReadOnlyDictionary<string, (int Count, RuleSeverity Sev)>? belowMap) =>
        GraphJson.SerializeFlat(model, report, belowMap);

    /// <summary>The single flat-document node element whose id equals <paramref name="dn"/>.</summary>
    private static JsonElement FlatNode(string flatJson, string dn)
    {
        using var document = JsonDocument.Parse(flatJson);
        var node = document.RootElement.GetProperty("nodes").EnumerateArray()
            .Single(n => n.GetProperty("id").GetString() == dn);
        return node.Clone();
    }
}
