using GroupWeaver.App.Graph;

using Xunit;

namespace GroupWeaver.App.Tests.Graph;

/// <summary>
/// Pins <see cref="GraphMessageParser.Parse"/> (AP 2.2 S4, ADR-004 D4/D5; grown by
/// ADR-005 D2 with <c>focused</c> for AP 2.3): each bridge message type parses from
/// realistic graph.js JSON (which sends extra fields like <c>userAgent</c> and
/// <c>label</c> — tolerated, never fatal), and the parser is TOTAL: malformed JSON,
/// unknown types, and missing/invalid required fields all map to
/// <see cref="UnknownMessage"/>. Parse never throws — the WebView bridge callback
/// has no sane place to catch.
/// </summary>
public sealed class GraphMessageParserTests
{
    // --- the six known message types, as graph.js actually sends them ---------------

    [Fact]
    public void Parse_Ready_FromRealisticBridgeJson()
    {
        // graph.js: window.bridge.send({ type: 'ready', userAgent: navigator.userAgent })
        var message = GraphMessageParser.Parse(
            """{"type":"ready","userAgent":"Mozilla/5.0 (Windows NT 10.0; Win64; x64) WebView2"}""");

        Assert.IsType<ReadyMessage>(message);
    }

    // --- ready's ADR-037 D6 growth: webglRenderer/userAgent (the rendering-mode truth) ------

    [Fact]
    public void Parse_Ready_ExtractsWebglRendererAndUserAgent_WhenBothPresent()
    {
        var message = GraphMessageParser.Parse(
            """{"type":"ready","webglRenderer":"ANGLE (Intel, Intel(R) UHD Graphics 620 Direct3D11 vs_5_0 ps_5_0)","userAgent":"Mozilla/5.0 WebView2"}""");

        var ready = Assert.IsType<ReadyMessage>(message);
        Assert.Equal("ANGLE (Intel, Intel(R) UHD Graphics 620 Direct3D11 vs_5_0 ps_5_0)", ready.WebglRenderer);
        Assert.Equal("Mozilla/5.0 WebView2", ready.UserAgent);
    }

    [Fact]
    public void Parse_Ready_WebglRendererIsExplicitJsonNull_ExtractsNull_KeepsUserAgent()
    {
        // graph.js sends webglRenderer:null when the WEBGL_debug_renderer_info extension is
        // unavailable — a JSON null must decode to a C# null, not the empty string.
        var message = GraphMessageParser.Parse(
            """{"type":"ready","webglRenderer":null,"userAgent":"Mozilla/5.0 WebView2"}""");

        var ready = Assert.IsType<ReadyMessage>(message);
        Assert.Null(ready.WebglRenderer);
        Assert.Equal("Mozilla/5.0 WebView2", ready.UserAgent);
    }

    [Fact]
    public void Parse_Ready_BothFieldsAbsent_BareLegacyReadyStillParses_BothNull()
    {
        var message = GraphMessageParser.Parse("""{"type":"ready"}""");

        var ready = Assert.IsType<ReadyMessage>(message);
        Assert.Null(ready.WebglRenderer);
        Assert.Null(ready.UserAgent);
    }

    [Fact]
    public void Parse_Ready_WrongJsonTypeForWebglRenderer_ExtractsNull_NeverDemotesToUnknown()
    {
        // A non-string webglRenderer (malformed/older bundle) must not demote a well-formed
        // ready to UnknownMessage — TryGetString's ValueKind guard falls back to null.
        var message = GraphMessageParser.Parse("""{"type":"ready","webglRenderer":123}""");

        var ready = Assert.IsType<ReadyMessage>(message);
        Assert.Null(ready.WebglRenderer);
    }

    [Fact]
    public void Parse_Ready_ToleratesUnknownExtraFields_AlongsideWebglRendererAndUserAgent()
    {
        var message = GraphMessageParser.Parse(
            """{"type":"ready","webglRenderer":"SwiftShader","userAgent":"UA","cyVersion":"3.28.1","seq":1}""");

        var ready = Assert.IsType<ReadyMessage>(message);
        Assert.Equal("SwiftShader", ready.WebglRenderer);
        Assert.Equal("UA", ready.UserAgent);
    }

    [Fact]
    public void Parse_Loaded_ExtractsNodeAndEdgeCounts()
    {
        var message = GraphMessageParser.Parse(
            """{"type":"loaded","nodeCount":196,"edgeCount":337}""");

        var loaded = Assert.IsType<LoadedMessage>(message);
        Assert.Equal(196, loaded.NodeCount);
        Assert.Equal(337, loaded.EdgeCount);
    }

    [Fact]
    public void Parse_NodeClick_ExtractsIdAndKind_ToleratesTheLabelField()
    {
        // graph.js sends id, label AND kind - the parser models only id + kind.
        var message = GraphMessageParser.Parse(
            """{"type":"nodeClick","id":"CN=GG_Sales,OU=Groups,OU=AGDLP-Demo,DC=weavedemo,DC=example","label":"GG_Sales","kind":"GlobalGroup"}""");

        var click = Assert.IsType<NodeClickMessage>(message);
        Assert.Equal("CN=GG_Sales,OU=Groups,OU=AGDLP-Demo,DC=weavedemo,DC=example", click.Id);
        Assert.Equal("GlobalGroup", click.Kind);
    }

    [Fact]
    public void Parse_NodeExpand_ExtractsId()
    {
        var message = GraphMessageParser.Parse(
            """{"type":"nodeExpand","id":"CN=DL_FileShare_RW,OU=Groups,OU=AGDLP-Demo,DC=weavedemo,DC=example"}""");

        var expand = Assert.IsType<NodeExpandMessage>(message);
        Assert.Equal("CN=DL_FileShare_RW,OU=Groups,OU=AGDLP-Demo,DC=weavedemo,DC=example", expand.Id);
    }

    [Fact]
    public void Parse_JsError_ExtractsSourceAndMessage()
    {
        var message = GraphMessageParser.Parse(
            """{"type":"jsError","source":"handler:graphCommit","message":"TypeError: cy is undefined"}""");

        var error = Assert.IsType<JsErrorMessage>(message);
        Assert.Equal("handler:graphCommit", error.Source);
        Assert.Equal("TypeError: cy is undefined", error.Message);
    }

    [Fact]
    public void Parse_Focused_FromRealisticBridgeJson()
    {
        // graph.js focus handler (ADR-005 D2), verbatim:
        //   cy.one('render', function () { window.bridge.send({ type: 'focused' }); });
        // — the type field is ALL it sends; FocusAsync only needs the confirmation.
        var message = GraphMessageParser.Parse("""{"type":"focused"}""");

        Assert.IsType<FocusedMessage>(message);
    }

    [Fact]
    public void Parse_Focused_ExtraFieldsAreTolerated()
    {
        // Forward-compatible like every other message: a later graph.js may attach
        // diagnostics (e.g. the resulting viewport) without breaking the parser.
        var message = GraphMessageParser.Parse(
            """{"type":"focused","seq":3,"viewport":{"zoom":1.5,"pan":[10,-4]}}""");

        Assert.IsType<FocusedMessage>(message);
    }

    [Fact]
    public void Parse_ExtraUnknownFields_AreTolerated()
    {
        var message = GraphMessageParser.Parse(
            """{"type":"loaded","nodeCount":1,"edgeCount":2,"seq":7,"nested":{"a":[1,2]},"flag":true}""");

        var loaded = Assert.IsType<LoadedMessage>(message);
        Assert.Equal(1, loaded.NodeCount);
        Assert.Equal(2, loaded.EdgeCount);
    }

    // --- pngExported (AP 4.1 S6, ADR-013; the cy.png() round-trip's inbound half) -------
    //
    // graph.js exportPng handler sends, verbatim (spec "Final graph-image design (PNG)"):
    //   window.bridge.send({ type:'pngExported', data: <bare base64>,
    //                        width: cy.width(), height: cy.height() });
    // `data` is a BARE base64 string (cy.png({ output:'base64' }) — no `data:` prefix);
    // it carries image bytes only, never an untrusted token.
    //
    // F1 (BINDING/CRITICAL): CytoscapeGraphRenderer.HandleMessage routes EVERY
    // UnknownMessage to RendererError → the VM sets LoadError. So a well-formed
    // `pngExported` MUST parse to PngExportedMessage and NOT to UnknownMessage —
    // otherwise the happy-path export would fire a spurious renderer error. These
    // tests pin exactly that: valid → PngExportedMessage (NOT Unknown).
    //
    // The renderer-seam round-trip (IGraphRenderer.ExportPngAsync arming the
    // _pngReady TCS off this message) is Playwright-verified (verify.mjs PNG-magic
    // phase, S7) and seam-pinned alongside the fake-renderer canned-PNG impl in S6 —
    // the FakeGraphRenderer/IGraphRenderer have no ExportPng surface yet, so it cannot
    // be unit-pinned here without a real WebView.

    [Fact]
    public void Parse_PngExported_ExtractsBase64DataAndDimensions()
    {
        // A realistic (tiny) bare base64 PNG payload as graph.js sends it.
        const string Base64Png =
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg==";

        var message = GraphMessageParser.Parse(
            $$"""{"type":"pngExported","data":"{{Base64Png}}","width":800,"height":600}""");

        // F1: MUST be PngExportedMessage, NOT UnknownMessage (an Unknown trips RendererError).
        var png = Assert.IsType<PngExportedMessage>(message);
        Assert.Equal(Base64Png, png.Data);
        Assert.Equal(800, png.Width);
        Assert.Equal(600, png.Height);
    }

    [Fact]
    public void Parse_PngExported_WidthAndHeightAreOptional_DefaultToZero()
    {
        // width/height are diagnostics only — data alone is the contract. Missing
        // dimensions default to 0 (TryGetInt), they NEVER demote a valid payload to
        // Unknown (which would trip RendererError per F1).
        var message = GraphMessageParser.Parse(
            """{"type":"pngExported","data":"aGVsbG8="}""");

        var png = Assert.IsType<PngExportedMessage>(message);
        Assert.Equal("aGVsbG8=", png.Data);
        Assert.Equal(0, png.Width);
        Assert.Equal(0, png.Height);
    }

    [Fact]
    public void Parse_PngExported_ExtraFieldsAreTolerated()
    {
        // Forward-compatible like every other message: later graph.js diagnostics
        // (e.g. scale/full echoes) must not break the parse.
        var message = GraphMessageParser.Parse(
            """{"type":"pngExported","data":"aGVsbG8=","width":640,"height":480,"scale":2,"full":false}""");

        var png = Assert.IsType<PngExportedMessage>(message);
        Assert.Equal("aGVsbG8=", png.Data);
        Assert.Equal(640, png.Width);
        Assert.Equal(480, png.Height);
    }

    [Theory]
    [InlineData("""{"type":"pngExported","width":800,"height":600}""")] // data missing
    [InlineData("""{"type":"pngExported","data":null,"width":800,"height":600}""")] // data null
    [InlineData("""{"type":"pngExported","data":123,"width":800,"height":600}""")] // data not a string
    public void Parse_PngExported_MissingOrInvalidData_ReturnsUnknown(string raw)
    {
        // `data` is the sole required field; without a string `data` the message
        // cannot carry image bytes, so it falls back to UnknownMessage (which the
        // renderer's exportPng path never receives on a happy path — F1).
        var unknown = Assert.IsType<UnknownMessage>(GraphMessageParser.Parse(raw));

        Assert.Equal(raw, unknown.Raw);
    }

    // --- stateReport (ADR-038 D3.2, WP6, #245; the --e2e stateProbe page-truth reply) ----
    //
    // graph.js's stateProbe handler sends, verbatim (cloned from the ping/pong seq idiom):
    //   window.bridge.send({ type: 'stateReport', seq, nodes, edges, zoom, panX, panY,
    //                         selected, animated });
    // `selected` is the ONE genuinely optional field (JSON null or absent both mean "nothing
    // selected"); every other field is required — a malformed reply must not silently report
    // zeroed state (GetNullableString/TryGetInt/TryGetDouble/TryGetBool in GraphMessageParser).

    [Fact]
    public void Parse_StateReport_ExtractsAllFields_FromRealisticBridgeJson()
    {
        var message = GraphMessageParser.Parse(
            """{"type":"stateReport","seq":3,"nodes":196,"edges":337,"zoom":1.5,"panX":-12.25,"panY":40,"selected":"CN=GG_Sales,OU=Groups,OU=AGDLP-Demo,DC=weavedemo,DC=example","animated":true}""");

        var report = Assert.IsType<StateReportMessage>(message);
        Assert.Equal(3, report.Seq);
        Assert.Equal(196, report.Nodes);
        Assert.Equal(337, report.Edges);
        Assert.Equal(1.5, report.Zoom);
        Assert.Equal(-12.25, report.PanX);
        Assert.Equal(40, report.PanY);
        Assert.Equal("CN=GG_Sales,OU=Groups,OU=AGDLP-Demo,DC=weavedemo,DC=example", report.Selected);
        Assert.True(report.Animated);
    }

    [Fact]
    public void Parse_StateReport_SelectedIsExplicitJsonNull_ExtractsNull_NothingSelected()
    {
        var message = GraphMessageParser.Parse(
            """{"type":"stateReport","seq":1,"nodes":0,"edges":0,"zoom":1,"panX":0,"panY":0,"selected":null,"animated":false}""");

        var report = Assert.IsType<StateReportMessage>(message);
        Assert.Null(report.Selected);
        Assert.False(report.Animated);
    }

    [Fact]
    public void Parse_StateReport_SelectedAbsent_ExtractsNull_NeverDemotesToUnknown()
    {
        // cy===null (probe raced ahead of the first graphCommit) — the idle snapshot graph.js
        // sends never includes `selected` at all; absence must mean the same as JSON null.
        var message = GraphMessageParser.Parse(
            """{"type":"stateReport","seq":1,"nodes":0,"edges":0,"zoom":0,"panX":0,"panY":0,"animated":false}""");

        var report = Assert.IsType<StateReportMessage>(message);
        Assert.Null(report.Selected);
    }

    [Fact]
    public void Parse_StateReport_IntegerZoomPanValues_ParseAsDoubles()
    {
        // zoom/panX/panY are JSON numbers without a fractional part on a freshly-loaded/centered
        // graph (cy.zoom() can be exactly 1, cy.pan() exactly {x:0,y:0}) — TryGetDouble must
        // accept an integer-shaped JSON number, not just ones with a decimal point.
        var message = GraphMessageParser.Parse(
            """{"type":"stateReport","seq":1,"nodes":5,"edges":4,"zoom":1,"panX":0,"panY":-3,"animated":false}""");

        var report = Assert.IsType<StateReportMessage>(message);
        Assert.Equal(1d, report.Zoom);
        Assert.Equal(0d, report.PanX);
        Assert.Equal(-3d, report.PanY);
    }

    [Fact]
    public void Parse_StateReport_ExtraUnknownFieldsAreTolerated()
    {
        var message = GraphMessageParser.Parse(
            """{"type":"stateReport","seq":1,"nodes":1,"edges":0,"zoom":1,"panX":0,"panY":0,"animated":false,"cyVersion":"3.28.1"}""");

        Assert.IsType<StateReportMessage>(message);
    }

    [Theory]
    [InlineData("""{"type":"stateReport","nodes":1,"edges":0,"zoom":1,"panX":0,"panY":0,"animated":false}""")] // seq missing
    [InlineData("""{"type":"stateReport","seq":"1","nodes":1,"edges":0,"zoom":1,"panX":0,"panY":0,"animated":false}""")] // seq wrong type
    [InlineData("""{"type":"stateReport","seq":1,"edges":0,"zoom":1,"panX":0,"panY":0,"animated":false}""")] // nodes missing
    [InlineData("""{"type":"stateReport","seq":1,"nodes":1,"zoom":1,"panX":0,"panY":0,"animated":false}""")] // edges missing
    [InlineData("""{"type":"stateReport","seq":1,"nodes":1,"edges":0,"panX":0,"panY":0,"animated":false}""")] // zoom missing
    [InlineData("""{"type":"stateReport","seq":1,"nodes":1,"edges":0,"zoom":"1","panX":0,"panY":0,"animated":false}""")] // zoom wrong type
    [InlineData("""{"type":"stateReport","seq":1,"nodes":1,"edges":0,"zoom":1,"panY":0,"animated":false}""")] // panX missing
    [InlineData("""{"type":"stateReport","seq":1,"nodes":1,"edges":0,"zoom":1,"panX":0,"animated":false}""")] // panY missing
    [InlineData("""{"type":"stateReport","seq":1,"nodes":1,"edges":0,"zoom":1,"panX":0,"panY":0}""")] // animated missing
    [InlineData("""{"type":"stateReport","seq":1,"nodes":1,"edges":0,"zoom":1,"panX":0,"panY":0,"animated":"yes"}""")] // animated wrong type
    public void Parse_StateReport_MissingOrInvalidRequiredField_ReturnsUnknown(string raw)
    {
        var unknown = Assert.IsType<UnknownMessage>(GraphMessageParser.Parse(raw));

        Assert.Equal(raw, unknown.Raw);
    }

    // --- the Unknown fallback ----------------------------------------------------------

    [Fact]
    public void Parse_MalformedJson_ReturnsUnknownWithRawAndReason()
    {
        const string Raw = """{"type":"ready" """; // truncated mid-object

        var unknown = Assert.IsType<UnknownMessage>(GraphMessageParser.Parse(Raw));

        Assert.Equal(Raw, unknown.Raw);
        Assert.False(string.IsNullOrWhiteSpace(unknown.Reason), "malformed JSON must carry a Reason");
    }

    [Fact]
    public void Parse_UnknownType_ReturnsUnknown()
    {
        const string Raw = """{"type":"teleport","id":"x"}""";

        var unknown = Assert.IsType<UnknownMessage>(GraphMessageParser.Parse(Raw));

        Assert.Equal(Raw, unknown.Raw);
    }

    [Theory]
    [InlineData("""{"type":"nodeClick","kind":"User"}""")] // id missing
    [InlineData("""{"type":"nodeClick","id":"CN=X,DC=x"}""")] // kind missing
    [InlineData("""{"type":"nodeClick","id":null,"kind":"User"}""")] // id null
    [InlineData("""{"type":"nodeExpand"}""")] // id missing
    [InlineData("""{"type":"loaded","nodeCount":3}""")] // edgeCount missing
    [InlineData("""{"type":"loaded","edgeCount":3}""")] // nodeCount missing
    [InlineData("""{"type":"loaded","nodeCount":"many","edgeCount":2}""")] // wrong field type
    [InlineData("""{"type":"jsError","message":"m"}""")] // source missing
    [InlineData("""{"type":"jsError","source":"s"}""")] // message missing
    public void Parse_MissingOrInvalidRequiredField_ReturnsUnknown(string raw)
    {
        var unknown = Assert.IsType<UnknownMessage>(GraphMessageParser.Parse(raw));

        Assert.Equal(raw, unknown.Raw);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("null")]
    [InlineData("true")]
    [InlineData("42")]
    [InlineData("\"ready\"")]
    [InlineData("ready")]
    [InlineData("[]")]
    [InlineData("""[{"type":"ready"}]""")] // array, not object
    [InlineData("{}")]
    [InlineData("""{"type":null}""")]
    [InlineData("""{"type":42}""")]
    [InlineData("{]")]
    [InlineData("}{")]
    [InlineData("""{"type":"pngExported","data":""")] // truncated pngExported mid-string
    public void Parse_Garbage_NeverThrows_ReturnsUnknown(string raw)
    {
        GraphMessage? message = null;

        var exception = Record.Exception(() => message = GraphMessageParser.Parse(raw));

        Assert.Null(exception);
        var unknown = Assert.IsType<UnknownMessage>(message);
        Assert.Equal(raw, unknown.Raw);
    }
}
