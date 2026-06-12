using System.Globalization;
using System.Text.Json;

using GroupWeaver.App.Graph;
using GroupWeaver.Core.Graph;
using GroupWeaver.Core.Model;

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
}
