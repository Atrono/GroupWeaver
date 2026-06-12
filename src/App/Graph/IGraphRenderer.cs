using Avalonia.Controls;

using GroupWeaver.Core.Graph;

namespace GroupWeaver.App.Graph;

/// <summary>
/// The renderer seam (ADR-004 D5) between <see cref="ViewModels.WorkspaceViewModel"/>
/// and the graph surface. The real <c>CytoscapeGraphRenderer</c> owns the WebView
/// lifecycle behind this interface; tests substitute a fake. The workspace VM mounts
/// <see cref="View"/> into the GraphHost region, pushes models through
/// <see cref="ShowGraphAsync"/>, and listens to the three events.
/// Contract pinned by <c>tests/GroupWeaver.App.Tests/WorkspaceLoadTests.cs</c>.
/// </summary>
public interface IGraphRenderer
{
    /// <summary>The control to mount into the workspace's <c>GraphHost</c> region;
    /// <c>null</c> when the renderer has no visual surface (headless/tests).</summary>
    Control? View { get; }

    /// <summary>Renders <paramref name="graph"/>; the returned task completes once the
    /// renderer has accepted the model (the VM's readiness must not lie about this).</summary>
    Task ShowGraphAsync(GraphModel graph, CancellationToken cancellationToken = default);

    /// <summary>A node was tapped (drives the AP 2.5 detail-panel selection).</summary>
    event EventHandler<GraphNodeEventArgs>? NodeClicked;

    /// <summary>A node was double-tapped for expansion — part of the interface now,
    /// ignored by the VM until AP 2.3 (ADR-004 D5).</summary>
    event EventHandler<GraphNodeEventArgs>? NodeExpandRequested;

    /// <summary>The renderer failed (ready timeout, JS error, …) — surfaced as an
    /// event, never an out-of-band exception (ADR-004 D5: never a hang).</summary>
    event EventHandler<GraphErrorEventArgs>? RendererError;
}

/// <summary>Node event payload. A plain record, not an <see cref="EventArgs"/>
/// subclass — records cannot derive from non-record classes, and
/// <see cref="EventHandler{TEventArgs}"/> has no EventArgs constraint.</summary>
public sealed record GraphNodeEventArgs(string Dn, string Kind);

/// <summary>Renderer error payload (same shape as <see cref="JsErrorMessage"/>);
/// plain record for the same reason as <see cref="GraphNodeEventArgs"/>.</summary>
public sealed record GraphErrorEventArgs(string Source, string Message);
