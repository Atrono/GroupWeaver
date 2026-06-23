using Avalonia.Controls;

using GroupWeaver.Core.Diff;
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
/// <para><see cref="IDisposable"/> (#122): each step VM disposes its renderer when the
/// step is abandoned (the shell's reclaim paths) — tearing down the single
/// <c>NativeWebView</c> it owns, retiring the ADR-024 never-disposed-renderer leak.</para>
/// </summary>
public interface IGraphRenderer : IDisposable
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

    /// <summary>Renders a fresh wholesale GAP topology (ADR-015 Slice 6, #66): the
    /// <paramref name="union"/> graph carries every Added/Removed/Common node, and the
    /// <paramref name="diff"/> supplies the per-element Added/Removed/Common/Unchecked status
    /// painted via the slice-4 wire fields. This is a wholesale destroy+fit init (NOT a
    /// replace-in-place <see cref="UpdateGraphAsync(GraphModel, RuleReport,
    /// IReadOnlyDictionary{string, ValueTuple{int, RuleSeverity}}, CancellationToken)"/>); it
    /// carries NO <see cref="RuleReport"/> — the Gap view shows the DIFF, not severity. Default
    /// no-op (mirroring <see cref="ExportPngAsync"/>): renderer fakes without a diff surface
    /// inherit it; the real <c>CytoscapeGraphRenderer</c> overrides it.</summary>
    Task ShowDiffGraphAsync(
        GraphModel union, SnapshotDiff diff, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    /// <summary>Moves the camera to fit the nodes named by <paramref name="dns"/>
    /// (ADR-005 D2/D3: parent + members after an expand); completes once the renderer
    /// confirmed the move. Unknown DNs are silently skipped by the graph surface.</summary>
    Task FocusAsync(IReadOnlyCollection<string> dns, CancellationToken cancellationToken = default);

    /// <summary>Rasterizes the live graph to PNG via <c>cy.png()</c> (ADR-013): dispatches
    /// <c>exportPng</c> and decodes the <c>pngExported</c> base64 reply into bytes. Returns
    /// <c>null</c> on timeout or any error (the never-throw renderer contract — a degraded
    /// renderer must not crash the VM), so the caller writes a file only on a non-null
    /// result. Default no-op (<c>null</c>): renderer fakes without an image surface inherit
    /// it; the real <c>CytoscapeGraphRenderer</c> overrides it. The bytes are decoded image
    /// data only — the outbound command carries no untrusted tokens (just scale/full/bg).</summary>
    Task<byte[]?> ExportPngAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<byte[]?>(null);

    /// <summary>Toggles the in-canvas BUSY ring on the node named <paramref name="dn"/>
    /// (ADR-019, the F12 follow-up to ADR-017): a static overlay halo marking a directory
    /// round-trip in progress on a lazy-expanded node. Fire-and-forget — dispatched WITHOUT
    /// the renderer single-flight (it must not deadlock the in-flight expand that already
    /// holds it) and WITHOUT a confirmation round-trip (never the 60 s BridgeTimeout, never
    /// the focus channel). No-ops safely before the bundle is ready; the next graphUpdate
    /// clears the transient flag regardless. Default no-op (mirroring
    /// <see cref="ShowDiffGraphAsync"/>); only the real <c>CytoscapeGraphRenderer</c> overrides.</summary>
    Task SetBusyAsync(string dn, bool on, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    /// <summary>Drives <c>node:selected</c> + neighborhood dim on the node named
    /// <paramref name="dn"/> from OUTSIDE a graph tap (ADR-020, the reverse sidebar->graph
    /// sync deferred from ADR-018): a sidebar/jump selection projects onto the canvas.
    /// INSTANT — the bundle uses addClass/removeClass only (never <c>cy.animate</c>), so the
    /// #88 motion counters stay untouched. Fire-and-forget — no single-flight, no
    /// confirmation, NEVER the focus channel (the pinned JumpCommand test requires FocusAsync
    /// to fire exactly once per jump). An empty <paramref name="dn"/> clears the selection.
    /// No-ops safely before the bundle is ready. Default no-op; only the real renderer overrides.</summary>
    Task SelectAsync(string dn, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    /// <summary>Switches the graph canvas theme (ADR-026 WP1b): dispatches the
    /// <c>{type:'theme', variant:'dark'|'light'}</c> command to the live page, which re-styles
    /// the cytoscape instance in place (no destroy, no fit, viewport preserved) and re-tones the
    /// index.html chrome CSS vars. Wire carries ONLY the variant string (graph.js owns the token
    /// tables). Fire-and-forget — like <see cref="SelectAsync"/>/<see cref="SetBusyAsync"/> it
    /// takes NO single-flight guard (the live theme toggle must reach a renderer mid-pipeline) and
    /// awaits NO confirmation; no-ops safely before the bundle is ready, and a freshly-rendered/
    /// re-attached graph already matches the theme because the renderer sends the variant as part of
    /// its render pipeline. Default no-op (mirroring <see cref="SetBusyAsync"/>); only the real
    /// <c>CytoscapeGraphRenderer</c> overrides.</summary>
    Task SetThemeAsync(bool isLightTheme, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

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
