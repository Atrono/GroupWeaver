using Avalonia.Controls;

using GroupWeaver.Core.Graph;
using GroupWeaver.Core.Rules;

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

    /// <summary>Renders <paramref name="graph"/> with its AP 3.4 severity join (ADR-010
    /// §3): <paramref name="report"/> drives the per-node halos and
    /// <paramref name="belowMap"/> the roll-up rings; both ride the wire fields straight
    /// into <see cref="GraphChunker.ToChunkCommands"/>. The returned task completes once
    /// the renderer has accepted the model (the VM's readiness must not lie about this).</summary>
    Task ShowGraphAsync(
        GraphModel graph,
        RuleReport report,
        IReadOnlyDictionary<string, (int Count, RuleSeverity Sev)>? belowMap,
        CancellationToken cancellationToken = default);

    /// <summary>Replace-in-place update of the live graph (ADR-005 D1/D2): no destroy,
    /// no fit, viewport untouched; completes once the renderer confirmed the update.
    /// Only valid after a successful <see cref="ShowGraphAsync"/>. Carries the re-Evaluated
    /// <paramref name="report"/> + <paramref name="belowMap"/> (ADR-010 §3) so the
    /// halos re-attach on the live instance.</summary>
    Task UpdateGraphAsync(
        GraphModel graph,
        RuleReport report,
        IReadOnlyDictionary<string, (int Count, RuleSeverity Sev)>? belowMap,
        CancellationToken cancellationToken = default);

    /// <summary>Severity-free convenience overload (the pre-AP-3.4 seam shape): forwards
    /// to the severity-carrying <see cref="ShowGraphAsync(GraphModel, RuleReport,
    /// IReadOnlyDictionary{string, ValueTuple{int, RuleSeverity}}, CancellationToken)"/>
    /// with an empty report and no roll-up — kept as a default interface method so a
    /// caller that has no report yet (e.g. the AP 2.3 seam tests) need not synthesize one.</summary>
    Task ShowGraphAsync(GraphModel graph, CancellationToken cancellationToken = default) =>
        ShowGraphAsync(graph, RuleReport.Empty, belowMap: null, cancellationToken);

    /// <summary>Severity-free convenience overload of <see cref="UpdateGraphAsync(GraphModel,
    /// RuleReport, IReadOnlyDictionary{string, ValueTuple{int, RuleSeverity}},
    /// CancellationToken)"/> — same forwarding rationale as the
    /// <see cref="ShowGraphAsync(GraphModel, CancellationToken)"/> overload.</summary>
    Task UpdateGraphAsync(GraphModel graph, CancellationToken cancellationToken = default) =>
        UpdateGraphAsync(graph, RuleReport.Empty, belowMap: null, cancellationToken);

    /// <summary>Moves the camera to fit the nodes named by <paramref name="dns"/>
    /// (ADR-005 D2/D3: parent + members after an expand); completes once the renderer
    /// confirmed the move. Unknown DNs are silently skipped by the graph surface.</summary>
    Task FocusAsync(IReadOnlyCollection<string> dns, CancellationToken cancellationToken = default);

    /// <summary>A node was tapped (drives the AP 2.5 detail-panel selection).</summary>
    event EventHandler<GraphNodeEventArgs>? NodeClicked;

    /// <summary>A node was double-tapped for expansion — drives the AP 2.3 lazy-expand
    /// pipeline in the workspace VM (ADR-005 D3).</summary>
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
