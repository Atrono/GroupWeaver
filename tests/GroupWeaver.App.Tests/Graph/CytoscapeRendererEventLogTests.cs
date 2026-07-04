using System.Linq;

using GroupWeaver.App.Graph;
using GroupWeaver.App.Tests.Diagnostics;

using Microsoft.Extensions.Logging;

using Xunit;

using static GroupWeaver.App.Tests.Graph.CytoscapeGraphRendererTestAccess;

namespace GroupWeaver.App.Tests.Graph;

/// <summary>
/// Pins the ADR-037 D5/D8 <c>Graph.Renderer</c>/<c>Graph.Bridge</c> event vocabulary on the REAL
/// <see cref="CytoscapeGraphRenderer"/> — never <see cref="Fakes.FakeGraphRenderer"/>, which has no
/// logging surface at all. Exercises exactly the two hot log sites reachable without a live
/// <c>NativeWebView</c> (see <see cref="CytoscapeGraphRendererTestAccess"/> for why): the inbound
/// bridge-message router and the single-flight guard. The renderer is constructed through its
/// PUBLIC, already-shipped ctor seam (<c>CytoscapeGraphRenderer(ILoggerFactory? loggerFactory =
/// null)</c>, ADR-037 WP1 defaulted-ctor idiom) with a <see cref="CapturingLoggerFactory"/> — no
/// reflection is needed to get the logger IN, only to drive the private methods that consume it.
/// </summary>
public sealed class CytoscapeRendererEventLogTests
{
    // === 1. BridgeReady{webglRenderer,userAgent} — Info, Graph.Bridge (ADR-037 D6) ==============

    [Fact]
    public void ReadyMessage_LogsBridgeReady_Information_WithWebglRendererAndUserAgent()
    {
        var capture = new CapturingLoggerFactory();
        var renderer = new CytoscapeGraphRenderer(capture);

        InvokeHandleMessage(
            renderer,
            """{"type":"ready","webglRenderer":"ANGLE (Intel, Intel(R) UHD Graphics 620)","userAgent":"Mozilla/5.0 WebView2"}""");

        var entry = Assert.Single(capture.EntriesNamed("BridgeReady"));
        Assert.Equal("Graph.Bridge", entry.Category);
        Assert.Equal(LogLevel.Information, entry.Level);
        Assert.Equal("ANGLE (Intel, Intel(R) UHD Graphics 620)", entry.Fields["webglRenderer"]);
        Assert.Equal("Mozilla/5.0 WebView2", entry.Fields["userAgent"]);
    }

    [Fact]
    public void ReadyMessage_LogsBridgeReady_WithNullFields_WhenTheBundleOmitsThem()
    {
        var capture = new CapturingLoggerFactory();
        var renderer = new CytoscapeGraphRenderer(capture);

        InvokeHandleMessage(renderer, """{"type":"ready"}""");

        var entry = Assert.Single(capture.EntriesNamed("BridgeReady"));
        Assert.Null(entry.Fields["webglRenderer"]);
        Assert.Null(entry.Fields["userAgent"]);
    }

    // === 2. BridgeMessageReceived{type,bytes} — Trace, Graph.Bridge, EventId=2 (ADR-037 D4) ======

    [Theory]
    [InlineData("""{"type":"ready"}""", "ready")]
    [InlineData("""{"type":"loaded","nodeCount":5,"edgeCount":7}""", "loaded")]
    [InlineData("""{"type":"nodeClick","id":"CN=X,DC=x","kind":"User"}""", "nodeClick")]
    [InlineData("""{"type":"nodeExpand","id":"CN=X,DC=x"}""", "nodeExpand")]
    [InlineData("""{"type":"focused"}""", "focused")]
    [InlineData("""{"type":"jsError","source":"s","message":"m"}""", "jsError")]
    [InlineData("""{"type":"pngExported","data":"aGVsbG8="}""", "pngExported")]
    [InlineData("""{"type":"teleport"}""", "unknown")]
    public void EveryMessage_LogsBridgeMessageReceived_Trace_WithWireTypeAndByteLength(
        string body, string expectedType)
    {
        var capture = new CapturingLoggerFactory();
        var renderer = new CytoscapeGraphRenderer(capture);

        InvokeHandleMessage(renderer, body);

        var entry = Assert.Single(capture.EntriesNamed("BridgeMessageReceived"));
        Assert.Equal("Graph.Bridge", entry.Category);
        Assert.Equal(LogLevel.Trace, entry.Level);
        Assert.Equal(expectedType, entry.Fields["type"]);
        Assert.Equal(body.Length, entry.Fields["bytes"]);
    }

    // === 3. JsErrorReported{source,msgScrubbed} — Warn, Graph.Bridge, through Redactor.Scrub =====

    [Fact]
    public void JsErrorMessage_LogsJsErrorReported_Warning_WithSourceAndScrubbedMessage()
    {
        var capture = new CapturingLoggerFactory();
        var renderer = new CytoscapeGraphRenderer(capture);

        InvokeHandleMessage(
            renderer,
            """{"type":"jsError","source":"handler:graphCommit","message":"TypeError: cy is undefined"}""");

        var entry = Assert.Single(capture.EntriesNamed("JsErrorReported"));
        Assert.Equal("Graph.Bridge", entry.Category);
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.Equal("handler:graphCommit", entry.Fields["source"]);
        // FIELD NAME anchor for WP10's redaction sweep (ADR-037 D9): "msgScrubbed", not "message".
        Assert.Equal("TypeError: cy is undefined", entry.Fields["msgScrubbed"]);
    }

    [Fact]
    public void JsErrorMessage_StillRaisesRendererError_AlongsideTheLogEntry()
    {
        var capture = new CapturingLoggerFactory();
        var renderer = new CytoscapeGraphRenderer(capture);
        GraphErrorEventArgs? raised = null;
        renderer.RendererError += (_, e) => raised = e;

        InvokeHandleMessage(renderer, """{"type":"jsError","source":"s","message":"boom"}""");

        Assert.NotNull(raised);
        Assert.Equal("s", raised!.Source);
        Assert.Equal("boom", raised.Message);
        Assert.Single(capture.EntriesNamed("JsErrorReported"));
    }

    // === 4. BridgeUnknownMessage{reasonScrubbed,bytes} — Warn, Graph.Bridge =======================

    [Fact]
    public void UnparseableMessage_LogsBridgeUnknownMessage_Warning_WithScrubbedReasonAndBytes()
    {
        var capture = new CapturingLoggerFactory();
        var renderer = new CytoscapeGraphRenderer(capture);
        const string Raw = """{"type":"teleport"}""";

        InvokeHandleMessage(renderer, Raw);

        var entry = Assert.Single(capture.EntriesNamed("BridgeUnknownMessage"));
        Assert.Equal("Graph.Bridge", entry.Category);
        Assert.Equal(LogLevel.Warning, entry.Level);
        // FIELD NAME anchor for WP10's redaction sweep (ADR-037 D9): "reasonScrubbed", not "reason".
        Assert.Equal("unknown message type 'teleport'", entry.Fields["reasonScrubbed"]);
        Assert.Equal(Raw.Length, entry.Fields["bytes"]);
    }

    [Fact]
    public void UnparseableMessage_StillRaisesRendererError_AlongsideTheLogEntry()
    {
        var capture = new CapturingLoggerFactory();
        var renderer = new CytoscapeGraphRenderer(capture);
        GraphErrorEventArgs? raised = null;
        renderer.RendererError += (_, e) => raised = e;

        InvokeHandleMessage(renderer, """{"type":"teleport"}""");

        Assert.NotNull(raised);
        Assert.Equal("renderer", raised!.Source);
        Assert.Single(capture.EntriesNamed("BridgeUnknownMessage"));
    }

    // === 5. SingleFlightViolation{operation} — Error, Graph.Renderer, log-then-throw ==============

    [Fact]
    public void ReentrantCommand_LogsSingleFlightViolation_Error_BeforeThrowing()
    {
        var capture = new CapturingLoggerFactory();
        var renderer = new CytoscapeGraphRenderer(capture);

        InvokeEnterSingleFlight(renderer, "ShowGraphAsync"); // first call: succeeds silently
        Assert.Empty(capture.EntriesNamed("SingleFlightViolation"));

        var ex = Assert.Throws<InvalidOperationException>(
            () => InvokeEnterSingleFlight(renderer, "UpdateGraphAsync"));
        Assert.Contains("UpdateGraphAsync", ex.Message);

        var entry = Assert.Single(capture.EntriesNamed("SingleFlightViolation"));
        Assert.Equal("Graph.Renderer", entry.Category);
        Assert.Equal(LogLevel.Error, entry.Level);
        Assert.Equal("UpdateGraphAsync", entry.Fields["operation"]);
    }

    // === 6. Heartbeat miss-reset-on-message (ADR-037 D8) — no timer needed ========================

    [Fact]
    public void ParseableMessage_ResetsTheHeartbeatMissStreak()
    {
        var capture = new CapturingLoggerFactory();
        var renderer = new CytoscapeGraphRenderer(capture);
        SetHeartbeatMisses(renderer, 2);

        InvokeHandleMessage(renderer, """{"type":"focused"}""");

        Assert.Equal(0, GetHeartbeatMisses(renderer));
    }

    [Theory]
    [InlineData("""{"type":"ready"}""")]
    [InlineData("""{"type":"loaded","nodeCount":1,"edgeCount":1}""")]
    [InlineData("""{"type":"jsError","source":"s","message":"m"}""")]
    public void EveryParseableMessageType_ResetsTheHeartbeatMissStreak(string body)
    {
        var capture = new CapturingLoggerFactory();
        var renderer = new CytoscapeGraphRenderer(capture);
        SetHeartbeatMisses(renderer, 3);

        InvokeHandleMessage(renderer, body);

        Assert.Equal(0, GetHeartbeatMisses(renderer));
    }

    [Fact]
    public void UnknownMessage_DoesNotResetTheHeartbeatMissStreak()
    {
        var capture = new CapturingLoggerFactory();
        var renderer = new CytoscapeGraphRenderer(capture);
        SetHeartbeatMisses(renderer, 2);

        InvokeHandleMessage(renderer, """{"type":"teleport"}""");

        Assert.Equal(2, GetHeartbeatMisses(renderer));
    }

    // === 7. Evt-name uniqueness within the new surface (the machine-contract pin) ================

    /// <summary>Drives every reachable ADR-037 WP2 event once and asserts the captured NAMES are
    /// exactly the expected 5-element vocabulary with NO accidental collisions (a duplicate name
    /// across two semantically different events would silently merge them for the E2E triager's
    /// grep). This does not (and per <see cref="CytoscapeGraphRendererTestAccess"/> cannot) cover
    /// the WebView/timer-gated remainder of the surface — see the WP2 report for that gap.</summary>
    [Fact]
    public void ReachableEventNames_AreExactlyTheExpectedDistinctVocabulary()
    {
        var capture = new CapturingLoggerFactory();
        var renderer = new CytoscapeGraphRenderer(capture);

        InvokeHandleMessage(renderer, """{"type":"ready","webglRenderer":"SwiftShader","userAgent":"UA"}""");
        InvokeHandleMessage(renderer, """{"type":"jsError","source":"s","message":"m"}""");
        InvokeHandleMessage(renderer, """{"type":"teleport"}""");
        InvokeEnterSingleFlight(renderer, "ShowGraphAsync");
        Assert.Throws<InvalidOperationException>(() => InvokeEnterSingleFlight(renderer, "FocusAsync"));

        var names = capture.Entries.Select(e => e.EventName).Distinct().OrderBy(n => n, StringComparer.Ordinal);
        Assert.Equal(
            new[] { "BridgeMessageReceived", "BridgeReady", "BridgeUnknownMessage", "JsErrorReported", "SingleFlightViolation" }
                .OrderBy(n => n, StringComparer.Ordinal),
            names);
    }
}
