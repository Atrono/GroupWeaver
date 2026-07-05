namespace GroupWeaver.App.Graph;

/// <summary>
/// A parsed JS→.NET bridge message (ADR-004 D4/D5). Produced exclusively by
/// <see cref="GraphMessageParser.Parse"/>; anything unparseable maps to
/// <see cref="UnknownMessage"/> — never an exception.
/// </summary>
public abstract record GraphMessage;

/// <summary>graph.js finished loading and the bridge is live (<c>{"type":"ready"}</c>).
/// ADR-037 D6 (WP2): optionally carries the page's rendering-mode truth —
/// <paramref name="WebglRenderer"/> is the unmasked <c>WEBGL_debug_renderer_info</c> string
/// ("SwiftShader" = software rendering; <c>null</c> when the context/extension is unavailable
/// or the field is absent on the wire) and <paramref name="UserAgent"/> the embedded browser's
/// UA string. Both optional: a bare <c>{"type":"ready"}</c> still parses (never demoted to
/// <see cref="UnknownMessage"/>), so older bundles and existing tests are unaffected.</summary>
public sealed record ReadyMessage(string? WebglRenderer = null, string? UserAgent = null) : GraphMessage;

/// <summary>The committed graph finished rendering (<c>{"type":"loaded"}</c>).</summary>
public sealed record LoadedMessage(int NodeCount, int EdgeCount) : GraphMessage;

/// <summary>A node was tapped (<c>{"type":"nodeClick"}</c>).</summary>
public sealed record NodeClickMessage(string Id, string Kind) : GraphMessage;

/// <summary>A node was double-tapped for expansion (<c>{"type":"nodeExpand"}</c>).</summary>
public sealed record NodeExpandMessage(string Id) : GraphMessage;

/// <summary>The focus camera move finished rendering (<c>{"type":"focused"}</c>, ADR-005 D2).</summary>
public sealed record FocusedMessage : GraphMessage;

/// <summary>A JS-side error report (<c>{"type":"jsError"}</c>, ADR-004 D6).</summary>
public sealed record JsErrorMessage(string Source, string Message) : GraphMessage;

/// <summary>The <c>cy.png()</c> base64 result (<c>{"type":"pngExported"}</c>, ADR-013):
/// <paramref name="Data"/> is a BARE base64 PNG string (image bytes only, no <c>data:</c>
/// prefix, never an untrusted token); <paramref name="Width"/>/<paramref name="Height"/>
/// are diagnostics (the cy canvas size, default 0). A well-formed message MUST parse to
/// this — an <see cref="UnknownMessage"/> would route to <c>RendererError</c>
/// (<c>CytoscapeGraphRenderer.HandleMessage</c>).</summary>
public sealed record PngExportedMessage(string Data, int Width, int Height) : GraphMessage;

/// <summary>The <c>--e2e</c> page-truth reply (<c>{"type":"stateReport"}</c>, ADR-038 D3.2 /
/// WP6, #245) to a <c>stateProbe</c> command — cloned from the existing ping/pong <c>seq</c>
/// idiom (<c>{"type":"ping"}</c>/<c>{"type":"pong"}</c> in graph.js). SCALARS ONLY (the
/// historical Playwright-serialization-trap lesson — never a rich cytoscape object).
/// <paramref name="Seq"/> echoes the probe's seq; <paramref name="Selected"/> is the
/// currently-selected node id, or <c>null</c>; <paramref name="Animated"/> is
/// <c>cy.animated()</c> — the settle-barrier boolean the house flake-mitigation rules require.</summary>
public sealed record StateReportMessage(
    int Seq, int Nodes, int Edges, double Zoom, double PanX, double PanY, string? Selected, bool Animated)
    : GraphMessage;

/// <summary>Fallback for malformed JSON, unknown message types, or missing required fields.</summary>
public sealed record UnknownMessage(string Raw, string? Reason) : GraphMessage;
