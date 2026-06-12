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
    public void Parse_Garbage_NeverThrows_ReturnsUnknown(string raw)
    {
        GraphMessage? message = null;

        var exception = Record.Exception(() => message = GraphMessageParser.Parse(raw));

        Assert.Null(exception);
        var unknown = Assert.IsType<UnknownMessage>(message);
        Assert.Equal(raw, unknown.Raw);
    }
}
