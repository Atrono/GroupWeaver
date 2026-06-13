namespace GroupWeaver.App.Graph;

/// <summary>
/// A parsed JS→.NET bridge message (ADR-004 D4/D5). Produced exclusively by
/// <see cref="GraphMessageParser.Parse"/>; anything unparseable maps to
/// <see cref="UnknownMessage"/> — never an exception.
/// </summary>
public abstract record GraphMessage;

/// <summary>graph.js finished loading and the bridge is live (<c>{"type":"ready"}</c>).</summary>
public sealed record ReadyMessage : GraphMessage;

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

/// <summary>Fallback for malformed JSON, unknown message types, or missing required fields.</summary>
public sealed record UnknownMessage(string Raw, string? Reason) : GraphMessage;
