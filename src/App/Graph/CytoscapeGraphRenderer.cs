using System.Diagnostics;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;

using GroupWeaver.App.Diagnostics;
using GroupWeaver.Core.Diff;
using GroupWeaver.Core.Graph;
using GroupWeaver.Core.Rules;

using Microsoft.Extensions.Logging;

namespace GroupWeaver.App.Graph;

/// <summary>
/// The production <see cref="IGraphRenderer"/> (ADR-004 D5/D6): owns a single
/// <see cref="NativeWebView"/> hosting the vendored Cytoscape bundle, served via
/// file:// from <c>web/index.html</c> next to the exe. The WebView is created
/// lazily on first <see cref="View"/> access (UI thread — it is a control) and
/// navigates on its FIRST attach to the visual tree; on a RE-attach (a step swap
/// destroys and recreates the native child — NativeControlHost) it re-navigates and
/// replays the last render (<see cref="ReNavigateAndReplayAsync"/>) so the graph
/// returns instead of a blank page. Outbound: chunked
/// <c>window.bridge.dispatch(…)</c> through <c>InvokeScript</c> (the
/// GraphSpike-proven transfer path, ADR-001 guardrail 4); inbound:
/// <c>WebMessageReceived</c> → <see cref="GraphMessageParser"/>. ALL events are
/// raised on the UI thread — the workspace VM sets <c>[ObservableProperty]</c>
/// members in its handlers and bindings consume the resulting PropertyChanged.
/// </summary>
public sealed partial class CytoscapeGraphRenderer : IGraphRenderer
{
    /// <summary>Bound on each bridge wait (ready, loaded, focused) — never a hang
    /// (ADR-004 D5).</summary>
    private static readonly TimeSpan BridgeTimeout = TimeSpan.FromSeconds(60);

    /// <summary>The ADR-037 D8 bridge capability probe: <c>'1'</c> iff the page accepts script
    /// AND <c>window.bridge.dispatch</c> is wired. THE one probe — used by both the re-attach
    /// replay loop (<see cref="ReNavigateAndReplayAsync"/>) and the liveness heartbeat
    /// (<see cref="ProbeHeartbeatAsync"/>), so the two can never diverge on what "alive" means.</summary>
    private const string BridgeProbeScript =
        "(typeof window.bridge!=='undefined'&&typeof window.bridge.dispatch==='function')?'1':'0'";

    /// <summary>Heartbeat cadence while attached + navigated (ADR-037 D8).</summary>
    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(10);

    /// <summary>Per-probe bound (same 2 s as the replay loop's probes): a wedged
    /// <c>InvokeScript</c> is itself the miss signal, never a hang.</summary>
    private static readonly TimeSpan HeartbeatProbeTimeout = TimeSpan.FromSeconds(2);

    /// <summary>Consecutive misses that escalate to <c>HeartbeatLost</c> + the RendererError
    /// banner (ADR-037 D8: 3 × 10 s ⇒ ~30 s of dead bridge).</summary>
    private const int HeartbeatLostThreshold = 3;

    /// <summary>The <c>Graph.Renderer</c> / <c>Graph.Bridge</c> loggers (ADR-037 D5, WP2 #241).
    /// Defaulted from <see cref="AppLog.Factory"/> (no-op NullLogger in headless tests, the
    /// installed sink in production) — the WP1 defaulted-ctor idiom, so the composition root's
    /// <c>() => new CytoscapeGraphRenderer()</c> and every existing test compile unchanged.</summary>
    private readonly ILogger _rendererLog;
    private readonly ILogger _bridgeLog;

    public CytoscapeGraphRenderer(ILoggerFactory? loggerFactory = null)
    {
        var logFactory = loggerFactory ?? AppLog.Factory;
        _rendererLog = logFactory.CreateLogger("Graph.Renderer");
        _bridgeLog = logFactory.CreateLogger("Graph.Bridge");
    }

    // ADR-037 D8 heartbeat state — UI-thread-only, like every other field. The timer exists
    // from the first StartHeartbeat until Dispose; Start/Stop toggles it across detach cycles.
    private DispatcherTimer? _heartbeatTimer;
    private int _heartbeatMisses;
    private bool _heartbeatProbeInFlight;

    // Not readonly: reset to a fresh, uncompleted source when the native control is
    // destroyed on detach (its old "ready" no longer reflects a live page), so the next
    // command awaits a REAL new ready instead of a stale-completed one. All access is
    // UI-thread-marshalled (View creation, the attach/detach handlers, and every
    // command path resume on the UI thread), so a plain field swap is safe.
    private TaskCompletionSource _ready =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private TaskCompletionSource? _loaded;
    private TaskCompletionSource? _focused;
    private TaskCompletionSource? _pngReady;
    private string? _pngExported;
    private NativeWebView? _webView;
    private bool _navigated;
    private bool _commandInFlight;

    /// <summary>Cancelled by <see cref="Dispose"/> (#122) so the lifecycle
    /// <see cref="ReNavigateAndReplayAsync"/> probe loop — which otherwise polls on its own
    /// timer after a re-attach — observes own-disposal and unwinds instead of running to its
    /// 60 s deadline against a torn-down control. Caller-driven commands keep awaiting on the
    /// CALLER's token; on dispose they unwind harmlessly because the native control is detached
    /// and the <see cref="_disposed"/> guard turns any further dispatch/RaiseError into a quiet
    /// no-op (so a faulted in-flight command can neither crash nor leak the WebView).</summary>
    private readonly CancellationTokenSource _disposeCts = new();

    /// <summary>True once <see cref="Dispose"/> ran (#122): the <see cref="View"/> getter then
    /// returns the existing control (or <c>null</c>) and NEVER recreates the torn-down WebView.</summary>
    private bool _disposed;

    /// <summary>The chunk commands of the most recent successful base render (show/update/
    /// diff) — replayed after a re-attach, when Avalonia recreates the native control blank
    /// (<see cref="CreateWebView"/>). Transient camera/selection ops (focus/select/busy) are
    /// NOT cached: they are not the base graph to restore.</summary>
    private IReadOnlyList<string>? _lastRenderChunks;

    /// <summary>The WebView, created on first access (single instance for the
    /// renderer's lifetime). Must be accessed on the UI thread. After <see cref="Dispose"/>
    /// (#122) it returns the existing control (or <c>null</c> if one was never created) and
    /// NEVER recreates the torn-down WebView.</summary>
    public Control? View => _disposed ? _webView : _webView ??= CreateWebView();

    public event EventHandler<GraphNodeEventArgs>? NodeClicked;

    public event EventHandler<GraphNodeEventArgs>? NodeExpandRequested;

    public event EventHandler<GraphErrorEventArgs>? RendererError;

    /// <summary>
    /// Ships <paramref name="graph"/> to the bundle and completes once the page
    /// confirmed the render (<c>loaded</c>). Timeout policy (pinned decision): if the
    /// bundle never becomes ready, or never confirms the render, within 60 s, this
    /// raises <see cref="RendererError"/> and returns NORMALLY — consistent with the
    /// WorkspaceLoadTests contract (RendererError → LoadError inline; completion ends
    /// IsLoading). Throwing instead would escape the VM's catch (it handles only
    /// DirectoryUnavailableException) and turn a degraded renderer into a crash-bug,
    /// against the never-hang-but-don't-crash intent. Trade-off accepted: on timeout
    /// the VM still sets GraphSummary, beside the visible LoadError.
    /// Re-entrancy: a second call while ANY renderer call is in flight throws
    /// <see cref="InvalidOperationException"/> — the single-flight guard is shared
    /// with <see cref="UpdateGraphAsync"/>/<see cref="FocusAsync"/> (ADR-005 D2/D3:
    /// the VM's busy gate drops overlapping gestures, so an overlap here is a caller
    /// bug; queueing would hide it).
    /// </summary>
    public async Task ShowGraphAsync(
        GraphModel graph,
        RuleReport report,
        IReadOnlyDictionary<string, (int Count, RuleSeverity Sev)>? belowMap,
        CancellationToken cancellationToken = default)
    {
        EnterSingleFlight(nameof(ShowGraphAsync));
        try
        {
            await DispatchRenderAsync(
                "show",
                GraphChunker.ToChunkCommands(graph, report, belowMap),
                graph.Nodes.Count,
                graph.Edges.Count,
                "graph render never completed (60 s)",
                cancellationToken);
        }
        finally
        {
            _commandInFlight = false;
        }
    }

    /// <summary>
    /// Replace-in-place update (ADR-005 D1/D2): same chunks as a show but committed
    /// with <c>graphUpdate</c> — the page mutates the LIVE cytoscape instance (no
    /// destroy, no fit) and confirms with the same <c>loaded</c> message. Identical
    /// single-flight guard and 60 s bounded-wait → RendererError-and-return-normally
    /// policy as <see cref="ShowGraphAsync"/> (see its remarks).
    /// </summary>
    public async Task UpdateGraphAsync(
        GraphModel graph,
        RuleReport report,
        IReadOnlyDictionary<string, (int Count, RuleSeverity Sev)>? belowMap,
        CancellationToken cancellationToken = default)
    {
        EnterSingleFlight(nameof(UpdateGraphAsync));
        try
        {
            await DispatchRenderAsync(
                "update",
                GraphChunker.ToUpdateCommands(graph, report, belowMap),
                graph.Nodes.Count,
                graph.Edges.Count,
                "graph update never completed (60 s)",
                cancellationToken);
        }
        finally
        {
            _commandInFlight = false;
        }
    }

    /// <summary>
    /// Renders a fresh wholesale GAP topology (ADR-015 Slice 6, #66): the same destroy+fit init
    /// path as <see cref="ShowGraphAsync"/> — the SAME chunk slicing + trailing <c>graphCommit</c>,
    /// the SAME ready/dispatch/<c>loaded</c>-confirmation plumbing — differing ONLY in that it
    /// forwards the <paramref name="diff"/>'s per-element status (via
    /// <see cref="GraphChunker.ToChunkCommands(GraphModel, RuleReport,
    /// IReadOnlyDictionary{string, ValueTuple{int, RuleSeverity}},
    /// IReadOnlyDictionary{string, DiffStatus}, IReadOnlyDictionary{MembershipEdge, DiffStatus},
    /// int, int)"/>) and carries NO report/below-map (an empty report, no roll-up — the gap render
    /// paints the diff overlay, never severity halos). Identical single-flight guard and 60 s
    /// bounded-wait → RendererError-and-return-normally policy as <see cref="ShowGraphAsync"/>
    /// (see its remarks).
    /// </summary>
    public async Task ShowDiffGraphAsync(
        GraphModel union, SnapshotDiff diff, CancellationToken cancellationToken = default)
    {
        EnterSingleFlight(nameof(ShowDiffGraphAsync));
        try
        {
            await DispatchRenderAsync(
                "diff",
                GraphChunker.ToChunkCommands(
                    union,
                    RuleReport.Empty,
                    belowMap: null,
                    nodeDiffMap: diff.NodeStatus,
                    edgeDiffMap: diff.EdgeStatus),
                union.Nodes.Count,
                union.Edges.Count,
                "gap graph render never completed (60 s)",
                cancellationToken);
        }
        finally
        {
            _commandInFlight = false;
        }
    }

    /// <summary>
    /// Camera move (ADR-005 D2): sends the <c>focus</c> command and completes on the
    /// page's <c>focused</c> confirmation. Identical single-flight guard and 60 s
    /// bounded-wait → RendererError-and-return-normally policy as
    /// <see cref="ShowGraphAsync"/> (see its remarks).
    /// </summary>
    public async Task FocusAsync(IReadOnlyCollection<string> dns, CancellationToken cancellationToken = default)
    {
        EnterSingleFlight(nameof(FocusAsync));
        try
        {
            if (!await TryAwaitReadyAsync(cancellationToken))
            {
                return;
            }

            try
            {
                // Armed BEFORE the dispatch: the page may confirm faster than
                // InvokeScript returns.
                _focused = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                await DispatchAsync(
                    [GraphJson.Serialize(new FocusDto("focus", [.. dns]))], cancellationToken);
                await AwaitConfirmationAsync(
                    _focused.Task, "focus move never completed (60 s)", "focus-wait", cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                RaiseError("renderer", $"focus dispatch failed: {ex.Message}");
            }
        }
        finally
        {
            _commandInFlight = false;
        }
    }

    /// <summary>
    /// Rasterizes the live graph to PNG (ADR-013): dispatches <c>exportPng</c> and
    /// completes on the page's <c>pngExported</c> reply, decoding its bare base64 into
    /// bytes. The outbound command carries no untrusted tokens (just <c>scale</c>/
    /// <c>full</c>/<c>bg</c>); the reply's <c>data</c> is image bytes only. Returns
    /// <c>null</c> on timeout/error (never-throw renderer contract) — identical
    /// single-flight guard and 60 s bounded-wait → RendererError-and-return-normally
    /// policy as <see cref="FocusAsync"/> (see <see cref="ShowGraphAsync"/> remarks).
    /// Defaults match ADR-013: viewport-only (<c>full:false</c>), <c>scale:2</c>,
    /// <c>bg:'#1b1f27'</c>.
    /// </summary>
    public async Task<byte[]?> ExportPngAsync(CancellationToken cancellationToken = default)
    {
        EnterSingleFlight(nameof(ExportPngAsync));
        try
        {
            if (!await TryAwaitReadyAsync(cancellationToken))
            {
                _rendererLog.LogWarning(
                    new EventId(0, "ExportPngFailed"), "ExportPngFailed {reason}", "timeout");
                return null;
            }

            try
            {
                var started = Stopwatch.GetTimestamp();

                // Armed BEFORE the dispatch: the page may confirm faster than
                // InvokeScript returns.
                _pngExported = null;
                var pngReady = _pngReady =
                    new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                await DispatchAsync(
                    [GraphJson.Serialize(new ExportPngDto("exportPng", 2, false, "#1b1f27"))], cancellationToken);
                await AwaitConfirmationAsync(
                    pngReady.Task, "png export never completed (60 s)", "export-wait", cancellationToken);
                if (!pngReady.Task.IsCompletedSuccessfully)
                {
                    // The 60 s timeout path: AwaitConfirmationAsync already raised the
                    // RendererError; _pngExported never arrived, so the decode would be null.
                    _rendererLog.LogWarning(
                        new EventId(0, "ExportPngFailed"), "ExportPngFailed {reason}", "timeout");
                    return null;
                }

                var bytes = DecodePngOrNull(_pngExported);
                if (bytes is null)
                {
                    _rendererLog.LogWarning(
                        new EventId(0, "ExportPngFailed"), "ExportPngFailed {reason}", "decodeNull");
                }
                else
                {
                    _rendererLog.LogDebug(
                        new EventId(0, "ExportPngCompleted"),
                        "ExportPngCompleted {bytes} {durationMs}",
                        bytes.Length, (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds);
                }

                return bytes;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _rendererLog.LogWarning(
                    new EventId(0, "ExportPngFailed"), ex, "ExportPngFailed {reason}", "dispatch");
                RaiseError("renderer", $"png export failed: {ex.Message}");
                return null;
            }
        }
        finally
        {
            _commandInFlight = false;
        }
    }

    /// <summary>
    /// Toggles the in-canvas busy ring (ADR-019): dispatches the <c>busy</c> command
    /// fire-and-forget. UNLIKE every other renderer call it does NOT take the single-flight
    /// guard (the expand pipeline that calls busy=on already holds it via its
    /// UpdateGraphAsync/FocusAsync — re-entering would throw/deadlock) and does NOT await a
    /// confirmation (never the 60 s BridgeTimeout, never the focus channel). Non-blocking
    /// readiness check (NOT TryAwaitReadyAsync, which would 60 s-block + raise on timeout):
    /// a busy racing ahead of the bundle simply no-ops — the next graphUpdate clears the
    /// transient flag regardless. Never-throw (the async-void RelayCommand expand path has
    /// no handler): a degraded bridge surfaces as RendererError, returns normally.
    /// </summary>
    public async Task SetBusyAsync(string dn, bool on, CancellationToken cancellationToken = default)
    {
        if (_webView is null || !_ready.Task.IsCompletedSuccessfully)
        {
            return;
        }

        try
        {
            await DispatchAsync([GraphJson.Serialize(new BusyDto("busy", dn, on))], cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // own-Dispose cancellation: nothing to settle (fire-and-forget).
        }
        catch (Exception ex)
        {
            _rendererLog.LogWarning(
                new EventId(0, "FireAndForgetFailed"), ex, "FireAndForgetFailed {command}", "busy");
            RaiseError("renderer", $"busy dispatch failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Drives the reverse selection sync (ADR-020): dispatches the <c>select</c> command
    /// fire-and-forget. Same seam as <see cref="SetBusyAsync"/> — NO single-flight (the
    /// caller is the selection-change hook, which by ADR-007 D1 is never busy-gated and must
    /// stay responsive during any in-flight pipeline), NO confirmation (never the 60 s
    /// BridgeTimeout, never the focus channel — the JumpCommand FocusAsync-exactly-once pin
    /// depends on it). Non-blocking ready-guard; never-throw (the discarded-task caller has
    /// no handler). An empty <paramref name="dn"/> clears the canvas selection JS-side.
    /// </summary>
    public async Task SelectAsync(string dn, CancellationToken cancellationToken = default)
    {
        if (_webView is null || !_ready.Task.IsCompletedSuccessfully)
        {
            return;
        }

        try
        {
            await DispatchAsync([GraphJson.Serialize(new SelectDto("select", dn))], cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // own-Dispose cancellation: nothing to settle (fire-and-forget).
        }
        catch (Exception ex)
        {
            _rendererLog.LogWarning(
                new EventId(0, "FireAndForgetFailed"), ex, "FireAndForgetFailed {command}", "select");
            RaiseError("renderer", $"select dispatch failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Switches the canvas theme (ADR-026 WP1b): dispatches <c>{type:'theme', variant}</c> to the
    /// live page, which re-styles the cytoscape instance in place and re-tones the chrome CSS vars.
    /// Same fire-and-forget seam as <see cref="SelectAsync"/>/<see cref="SetBusyAsync"/> — NO
    /// single-flight (the live theme toggle must reach a renderer even while a pipeline is in
    /// flight) and NO confirmation round-trip. Non-blocking ready-guard: a theme racing ahead of
    /// the bundle simply no-ops, because every subsequent render prepends the current theme
    /// (DispatchRenderAsync) so the page converges. Never-throw (the discarded-task caller has no
    /// handler): a degraded bridge surfaces as <see cref="RendererError"/> and returns. The
    /// <paramref name="isLightTheme"/> bool is resolved to the wire variant string here, NOT read
    /// from <see cref="Application"/> — the caller (ShellViewModel) owns the authoritative theme state.
    /// </summary>
    public async Task SetThemeAsync(bool isLightTheme, CancellationToken cancellationToken = default)
    {
        if (_webView is null || !_ready.Task.IsCompletedSuccessfully)
        {
            return;
        }

        try
        {
            await DispatchAsync([ThemeCommand(isLightTheme)], cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // own-Dispose cancellation: nothing to settle (fire-and-forget).
        }
        catch (Exception ex)
        {
            _rendererLog.LogWarning(
                new EventId(0, "FireAndForgetFailed"), ex, "FireAndForgetFailed {command}", "theme");
            RaiseError("renderer", $"theme dispatch failed: {ex.Message}");
        }
    }

    /// <summary>The <c>theme</c> bridge command (ADR-026 WP1b); carries ONLY the resolved variant
    /// string ('dark'/'light'). When no explicit variant is passed it is resolved from
    /// <c>Application.Current.RequestedThemeVariant</c> — the render-pipeline prepend
    /// (<see cref="DispatchRenderAsync"/>/<see cref="ReNavigateAndReplayAsync"/>) uses this overload
    /// so a freshly-rendered/re-attached graph matches the current app theme.</summary>
    private static string ThemeCommand() =>
        ThemeCommand(IsAppLightTheme());

    private static string ThemeCommand(bool isLightTheme) =>
        GraphJson.Serialize(new ThemeDto("theme", isLightTheme ? "light" : "dark"));

    /// <summary>Resolves whether the running app is in the light variant. A no-app/headless
    /// context (no <see cref="Application.Current"/>) defaults to dark (the byte-identical default).</summary>
    private static bool IsAppLightTheme() =>
        Application.Current is { } app
        && app.RequestedThemeVariant == Avalonia.Styling.ThemeVariant.Light;

    /// <summary>The <c>theme</c> bridge command (ADR-026 WP1b); presentation only — the variant
    /// string is the sole payload (graph.js owns the dark/light token tables, ADR-021 hand-mirror).</summary>
    private sealed record ThemeDto(string Type, string Variant);

    /// <summary>
    /// Decodes the page's bare-base64 <c>pngExported</c> reply into bytes, FAILING TO
    /// <c>null</c> rather than throwing — the never-throw renderer contract
    /// (<see cref="IGraphRenderer.ExportPngAsync"/>: <c>null</c> on ANY error). The reply
    /// is untrusted text from the bridge: a malformed/garbage body throws
    /// <see cref="FormatException"/> from <see cref="Convert.FromBase64String"/>, which on
    /// the RelayCommand async-void path would rethrow on the UI thread with no handler and
    /// CRASH the app. Catching it here keeps a degraded export from becoming a crash bug.
    /// </summary>
    internal static byte[]? DecodePngOrNull(string? base64)
    {
        if (string.IsNullOrEmpty(base64))
        {
            return null;
        }

        try
        {
            return Convert.FromBase64String(base64);
        }
        catch (FormatException)
        {
            return null;
        }
    }

    /// <summary>The <c>exportPng</c> bridge command (ADR-013); carries no untrusted
    /// tokens — only the raster knobs scale/full/background.</summary>
    private sealed record ExportPngDto(string Type, int Scale, bool Full, string Bg);

    /// <summary>One renderer call at a time, shared across show/update/focus —
    /// an overlap is a caller bug, never queued (ADR-005 D3). ADR-037 D5: the violation is
    /// logged (<c>SingleFlightViolation</c>, Error) BEFORE the existing throw — log-then-
    /// existing-behavior, the caller bug now leaves machine-readable evidence. Internal
    /// (ADR-037 WP2, #241 test seam) — same rationale as <see cref="HandleMessage"/>.
    /// <para><b>ACCEPTED BOUNDARY (WP2 test-engineer finding, ratified — not fixed):</b> the log
    /// call below is NOT wrapped in its own try/catch, so a hypothetically-throwing
    /// <see cref="ILogger"/> would propagate ITS exception here, masking this method's own
    /// intended <see cref="InvalidOperationException"/> (pinned by
    /// <c>RendererFaultNeverCrashesTests.ThrowingLogger_PropagatesFromEnterSingleFlight_
    /// MaskingItsOwnIntentionalException</c>). Deliberately left as-is: "never-throw" is a
    /// property OF the installed sink itself (<see cref="Diagnostics.FileLogSink"/>, ADR-037
    /// D3 — every one of its public methods is try/catch-wrapped), not a defensive duty every
    /// call site must repeat, and production only ever installs <c>FileLogSink</c> or
    /// <see cref="Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory"/>
    /// (<see cref="AppLog"/>'s default) — both contractually never-throw — so this scenario
    /// cannot occur outside an adversarial test double. Wrapping it here would be defense
    /// against a threat that does not exist in this codebase's architecture, at the cost of
    /// silently swallowing a REAL future logger bug right where re-entrancy is diagnosed.</para>
    /// </summary>
    internal void EnterSingleFlight(string operation)
    {
        if (_commandInFlight)
        {
            _rendererLog.LogError(
                new EventId(0, "SingleFlightViolation"), "SingleFlightViolation {operation}", operation);
            throw new InvalidOperationException(
                $"{operation} while another renderer call is in flight — the renderer runs one command at a time.");
        }

        _commandInFlight = true;
    }

    /// <summary>
    /// Shared base-render path for <see cref="ShowGraphAsync"/>/<see cref="UpdateGraphAsync"/>/
    /// <see cref="ShowDiffGraphAsync"/>: await ready, arm <c>_loaded</c> BEFORE the first
    /// dispatch (the page may confirm before the last <c>InvokeScript</c> returns), dispatch the
    /// chunks, await the <c>loaded</c> confirmation, then cache the chunks as the render to replay
    /// after a re-attach (<see cref="ReNavigateAndReplayAsync"/>) — cached ONLY on a genuine
    /// confirmation, never on a soft timeout. NEVER-THROW (ADR-004 D5): any
    /// non-own-cancellation fault from the dispatch/confirm body becomes a
    /// <see cref="RendererError"/> and a normal return — an <c>InvokeScript</c> fault must not
    /// escape onto an awaited/async-void caller and crash the app. Own-Dispose cancellation
    /// rethrows to preserve the Dispose-cancel behavior. The single-flight guard and its
    /// <c>finally</c> stay in the caller, OUTSIDE this body (ADR-005 D3, ADR-004 D5 untouched).
    /// </summary>
    private async Task DispatchRenderAsync(
        string kind,
        IReadOnlyList<string> chunks,
        int nodes,
        int edges,
        string timeoutMessage,
        CancellationToken cancellationToken)
    {
        if (!await TryAwaitReadyAsync(cancellationToken))
        {
            return;
        }

        try
        {
            // ADR-037 D5: the render pipeline's Started/Completed pair. totalBytes is the wire
            // volume (the command JSON is ASCII by construction — GraphJson escapes non-ASCII —
            // so char count == byte count); computed only when Debug is actually written.
            if (_rendererLog.IsEnabled(LogLevel.Debug))
            {
                var totalBytes = 0L;
                foreach (var chunk in chunks)
                {
                    totalBytes += chunk.Length;
                }

                _rendererLog.LogDebug(
                    new EventId(0, "RenderDispatchStarted"),
                    "RenderDispatchStarted {kind} {chunks} {nodes} {edges} {totalBytes}",
                    kind, chunks.Count, nodes, edges, totalBytes);
            }

            var started = Stopwatch.GetTimestamp();
            var loaded = _loaded =
                new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            // ADR-026 WP1b: set the canvas theme BEFORE the chunks/commit so the fresh
            // cytoscape instance is built directly in the current variant (graph.js's theme
            // handler sets currentVariant unconditionally; initGraph reads THEME[currentVariant]).
            // A freshly-rendered or re-attached graph therefore matches the live app theme with
            // no post-render restyle flash. Default (dark) => the prepended command is a no-op
            // restyle to the byte-identical default.
            await DispatchAsync([ThemeCommand(), .. chunks], cancellationToken);
            await AwaitConfirmationAsync(loaded.Task, timeoutMessage, "commit-wait", cancellationToken);

            // Cache as the replay base only on a real confirmation — a soft 60 s timeout
            // (AwaitConfirmationAsync swallows it to a RendererError) leaves it unset/stale.
            if (loaded.Task.IsCompletedSuccessfully)
            {
                _lastRenderChunks = chunks;
                _rendererLog.LogInformation(
                    new EventId(0, "RenderCompleted"),
                    "RenderCompleted {kind} {durationMs}",
                    kind, (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            RaiseError("renderer", $"graph render failed: {ex.Message}");
        }
    }

    /// <summary>Bounded wait for the bundle's <c>ready</c>; on timeout raises
    /// <see cref="RendererError"/> and reports <c>false</c> — callers return normally
    /// (never a hang, never a throw; ADR-004 D5). ADR-037 D5: the timeout is attributed as
    /// <c>RenderTimeout{phase="ready"}</c>.</summary>
    private async Task<bool> TryAwaitReadyAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _ready.Task.WaitAsync(BridgeTimeout, cancellationToken);
            return true;
        }
        catch (TimeoutException)
        {
            _rendererLog.LogError(new EventId(0, "RenderTimeout"), "RenderTimeout {phase}", "ready");
            RaiseError("renderer", "web bundle never became ready (60 s)");
            return false;
        }
    }

    /// <summary>Bounded wait for a page confirmation (<c>loaded</c>/<c>focused</c>/
    /// <c>pngExported</c>); timeout → <c>RenderTimeout{phase}</c> (ADR-037 D5 — which wait
    /// timed out: commit-wait, focus-wait, export-wait, replay-commit) + <see cref="RendererError"/>
    /// and a normal return.</summary>
    private async Task AwaitConfirmationAsync(
        Task confirmation, string timeoutMessage, string phase, CancellationToken cancellationToken)
    {
        try
        {
            await confirmation.WaitAsync(BridgeTimeout, cancellationToken);
        }
        catch (TimeoutException)
        {
            _rendererLog.LogError(new EventId(0, "RenderTimeout"), "RenderTimeout {phase}", phase);
            RaiseError("renderer", timeoutMessage);
        }
    }

    private async Task DispatchAsync(
        IReadOnlyList<string> commands, CancellationToken cancellationToken)
    {
        // Defensive: a ready confirmation always implies a live _webView, but a re-attach
        // race could in principle null it between checks. Bail quietly rather than NRE. A
        // disposed renderer (#122) has torn the WebView down — also bail.
        if (_disposed || _webView is null)
        {
            return;
        }

        for (var i = 0; i < commands.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var command = commands[i];
            // ADR-037 D4 Trace: size only, never the payload. LoggerMessage source-gen —
            // IsEnabled-guarded and allocation-free at the default Information level.
            LogBridgeChunkSent(_bridgeLog, i, command.Length);

            // The command JSON is embedded verbatim as a JS object literal: safe,
            // because GraphJson's default STJ encoder emits ASCII-only output
            // (non-ASCII — including the JS-literal-breaking U+2028/U+2029 —
            // escaped to \uXXXX), and ASCII-safe JSON is a valid JS expression.
            await _webView.InvokeScript($"window.bridge.dispatch({command})");
        }
    }

    /// <summary>The <c>focus</c> bridge command (ADR-005 D2); serialized through
    /// <see cref="GraphJson"/> so comma/quote-containing DNs are escaped correctly.</summary>
    private sealed record FocusDto(string Type, IReadOnlyList<string> Ids);

    /// <summary>The <c>busy</c> bridge command (ADR-019); serialized through
    /// <see cref="GraphJson"/> so a comma/quote-containing DN is escaped (same rationale
    /// as <see cref="FocusDto"/>).</summary>
    private sealed record BusyDto(string Type, string Id, bool On);

    /// <summary>The <c>select</c> bridge command (ADR-020); serialized through
    /// <see cref="GraphJson"/> so a comma/quote-containing DN is escaped (same rationale as
    /// <see cref="FocusDto"/>). An empty <c>Id</c> => clearSelection JS-side.</summary>
    private sealed record SelectDto(string Type, string Id);

    // The exact handler delegates wired in CreateWebView/HardenWebView, kept as fields so
    // Dispose (#122) can unsubscribe them from the NativeWebView before tearing it down — the
    // anonymous lambdas could not otherwise be detached. UI-thread-only, like every other field.
    private EventHandler<WebMessageReceivedEventArgs>? _onWebMessage;
    private EventHandler<Avalonia.VisualTreeAttachmentEventArgs>? _onAttachedToTree;
    private EventHandler<Avalonia.VisualTreeAttachmentEventArgs>? _onDetachedFromTree;
    private EventHandler<WebViewEnvironmentRequestedEventArgs>? _onEnvironmentRequested;
    private EventHandler<WebViewNewWindowRequestedEventArgs>? _onNewWindowRequested;
    private EventHandler<WebViewAdapterEventArgs>? _onAdapterCreated;
    private EventHandler<WebViewAdapterEventArgs>? _onAdapterDestroyed;

    private NativeWebView CreateWebView()
    {
        var webView = new NativeWebView();
        _onWebMessage = (_, e) => OnWebMessageReceived(e.Body ?? string.Empty);
        _onAttachedToTree = (_, _) => OnAttached(webView);

        // On detach Avalonia destroys the native WebView2 child and recreates it BLANK on
        // re-attach (NativeControlHost). The old page (and its "ready") is gone, so reset the
        // ready gate to a fresh, uncompleted source: a later command then awaits the REAL new
        // ready (raised by the re-navigated page) instead of resuming on a stale-completed one.
        // All access is UI-thread (this handler, View creation, command resumes), so the plain
        // field swap is safe. ADR-037 D8: the heartbeat stops with the native child — while
        // detached there is no page to probe, so a dead-page verdict here would be a lie.
        _onDetachedFromTree = (_, _) =>
        {
            StopHeartbeat();
            _ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        };

        // ADR-037 D8: AdapterCreated/AdapterDestroyed bracket the native child's life — an
        // AdapterDestroyed that is neither a Dispose nor a detach-driven teardown (disposed
        // false, commandInFlight possibly true) is the WebView2-crash smell the E2E triager
        // correlates with the heartbeat.
        _onAdapterCreated = (_, _) =>
            _rendererLog.LogInformation(new EventId(0, "AdapterCreated"), "AdapterCreated");
        _onAdapterDestroyed = (_, _) =>
            _rendererLog.LogInformation(
                new EventId(0, "AdapterDestroyed"),
                "AdapterDestroyed {navigated} {commandInFlight} {disposed}",
                _navigated, _commandInFlight, _disposed);

        webView.WebMessageReceived += _onWebMessage;
        webView.AttachedToVisualTree += _onAttachedToTree;
        webView.DetachedFromVisualTree += _onDetachedFromTree;
        webView.AdapterCreated += _onAdapterCreated;
        webView.AdapterDestroyed += _onAdapterDestroyed;

        HardenWebView(webView);
        return webView;
    }

    /// <summary>
    /// FIRST attach → navigate (the GraphSpike path). RE-attach (already navigated once, the
    /// native control was destroyed on detach and recreated blank) → re-navigate the bundle
    /// and, once it is ready again, replay the last base render so the graph comes back instead
    /// of a blank page. The visual-tree event is synchronous and has no exception handler, so the
    /// replay is fired as a guarded async method that never throws back into the handler.
    /// </summary>
    private void OnAttached(NativeWebView webView)
    {
        if (!_navigated)
        {
            NavigateOnce(webView);
        }
        else
        {
            _ = ReNavigateAndReplayAsync(webView);
        }

        // ADR-037 D8: the heartbeat runs while the surface is attached and navigated (started
        // on BOTH the first-nav and re-attach paths, stopped on detach/Dispose). Ticks are
        // ready-gated (ProbeHeartbeatAsync), so during page (re)load — _ready reset on detach,
        // completed again only by the fresh page's 'ready' — no probe runs and no miss is
        // counted; the replay loop's own bounded probe covers that window.
        StartHeartbeat();
    }

    /// <summary>
    /// Re-attach recovery: reload <c>index.html</c> on the recreated-blank native control, wait
    /// (by probing) until the page accepts script and <c>window.bridge.dispatch</c> exists, then
    /// replay <see cref="_lastRenderChunks"/> so the previously shown graph is restored. Never-throw
    /// (the discarded-task caller has no handler): a degraded bridge surfaces as
    /// <see cref="RendererError"/> and returns. Does NOT take the single-flight guard — it is a
    /// lifecycle recovery, not a caller command; should a real command race in, the page's
    /// idempotent re-render reconciles.
    /// </summary>
    private async Task ReNavigateAndReplayAsync(NativeWebView webView)
    {
        // #122: this lifecycle recovery runs on the own-disposal token (not CancellationToken.None),
        // so a Dispose mid-replay cancels the probe loop + dispatch instead of running the full 60 s.
        var cancellationToken = _disposeCts.Token;
        try
        {
            if (_disposed)
            {
                return;
            }

            var indexUri = new Uri(Path.Combine(AppContext.BaseDirectory, "web", "index.html"));
            webView.Navigate(indexUri);

            if (_lastRenderChunks is not { } chunks)
            {
                return;
            }

            // Wait until the RECREATED page actually accepts script and the bridge is wired, by
            // PROBING the real precondition rather than racing the NavigationCompleted / bridge
            // `ready` events. Across repeated re-attaches those events get satisfied by stale,
            // dispatcher-queued signals (and the recreated control's own blank initial nav) from
            // the prior page, so the replay InvokeScript could still run "before any page was
            // loaded" (observed on the 2nd re-attach, both with a reset _ready and with a
            // NavigationCompleted gate). InvokeScript throws exactly that until THIS navigation
            // lands, and returns '1' only once window.bridge.dispatch exists — so polling it is
            // immune to every stale-signal race. Each probe is itself time-bounded in case a
            // not-yet-ready control leaves the call pending rather than faulting.
            var deadline = DateTime.UtcNow + BridgeTimeout;
            while (true)
            {
                string? probe = null;
                try
                {
                    probe = await webView
                        .InvokeScript(BridgeProbeScript)
                        .WaitAsync(HeartbeatProbeTimeout);
                }
                catch
                {
                    // Page not loaded yet (InvokeScript faults "before any page was loaded") or the
                    // probe is briefly pending — keep polling until the deadline.
                }

                if (probe is not null && probe.Contains('1'))
                {
                    break;
                }

                if (DateTime.UtcNow > deadline)
                {
                    _rendererLog.LogError(
                        new EventId(0, "RenderTimeout"), "RenderTimeout {phase}", "replay-probe");
                    RaiseError("renderer", "recreated page never accepted script after re-attach (60 s)");
                    return;
                }

                await Task.Delay(TimeSpan.FromMilliseconds(150), cancellationToken);
            }

            var loaded = _loaded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            // ADR-026 WP1b: the recreated-blank page defaults to dark — prepend the current
            // theme so the replayed graph is rebuilt in the live app variant (same rationale
            // as DispatchRenderAsync), not a stale dark.
            await DispatchAsync([ThemeCommand(), .. chunks], cancellationToken);
            await AwaitConfirmationAsync(
                loaded.Task, "graph replay never completed (60 s)", "replay-commit", cancellationToken);
        }
        catch (OperationCanceledException) when (_disposeCts.IsCancellationRequested)
        {
            // Own-Dispose cancellation (#122): the replay was abandoned because the renderer is
            // being torn down — nothing to surface.
        }
        catch (Exception ex)
        {
            RaiseError("renderer", $"graph replay failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Defense-in-depth (#52): the renderer hosts a local <c>file://</c> graph page
    /// with a single embedded bundle — it never needs DevTools, child windows, or
    /// off-page navigation. These three hooks are everything the Avalonia
    /// <c>NativeWebView</c> 11.4.0 wrapper exposes:
    /// <list type="bullet">
    /// <item><see cref="NativeWebView.EnvironmentRequested"/> →
    /// <c>EnableDevTools = false</c> disables the DevTools surface (F12 / Inspect)
    /// at WebView2 environment-creation time.</item>
    /// <item><see cref="NativeWebView.NewWindowRequested"/> →
    /// <c>Handled = true</c> swallows every <c>window.open</c>/target=_blank request
    /// rather than spawning an out-of-frame WebView2 window.</item>
    /// <item><see cref="NativeWebView.NavigationStarted"/> →
    /// <c>Cancel = true</c> for any non-<c>file://</c> document, pinning the WebView
    /// to local content. This app only ever serves a local <c>file://</c> page, so a
    /// scheme check both always admits the legitimate <c>index.html</c> load and
    /// blocks the realistic threat — a main-frame navigation to a remote URL.</item>
    /// </list>
    /// The CoreWebView2Settings the wrapper does NOT surface managed accessors for —
    /// <c>AreDefaultContextMenusEnabled</c>, <c>IsStatusBarEnabled</c>,
    /// <c>AreBrowserAcceleratorKeysEnabled</c> — are reachable on this package only as
    /// a raw COM <c>IntPtr</c> (<c>IWindowsWebView2PlatformHandle.CoreWebView2</c>);
    /// poking them would mean hand-rolled vtable P/Invoke, deliberately out of scope.
    /// </summary>
    private void HardenWebView(NativeWebView webView)
    {
        // Kept as fields so Dispose (#122) can unsubscribe them (NavigationStarted is a method
        // group, detachable directly); same UI-thread-only contract as the CreateWebView handlers.
        _onEnvironmentRequested = (_, e) => e.EnableDevTools = false;
        _onNewWindowRequested = (_, e) => e.Handled = true;
        webView.EnvironmentRequested += _onEnvironmentRequested;
        webView.NewWindowRequested += _onNewWindowRequested;
        webView.NavigationStarted += OnNavigationStarted;
    }

    /// <summary>
    /// Locality guard (#52): ALLOWS any local <c>file://</c> document — the only
    /// thing this app ever serves (the vendored <c>index.html</c> bundle) — and
    /// CANCELS every non-<c>file</c> scheme (http/https/…), i.e. a main-frame
    /// navigation to a remote URL. Matching on scheme rather than the exact
    /// <c>index.html</c> URI is robust by construction: WebView2 may report the
    /// initial <c>file://</c> URI with different drive-letter casing, space
    /// percent-encoding, or trailing form than the <see cref="Uri"/> we navigated
    /// with, and an over-strict equality check there would cancel the legitimate
    /// load and blank the graph. The scheme is invariant under that canonicalization,
    /// so file:// is always admitted. A missing request URI is treated as allow:
    /// the app's own load must never be cancelled on absent information (fail open).
    /// </summary>
    private void OnNavigationStarted(object? sender, WebViewNavigationStartingEventArgs e)
    {
        if (e.Request is { Scheme: var scheme } &&
            !scheme.Equals(Uri.UriSchemeFile, StringComparison.OrdinalIgnoreCase))
        {
            e.Cancel = true;
        }
    }

    /// <summary>Navigates on the FIRST attach only (sets <see cref="_navigated"/>). A
    /// subsequent attach is a RE-attach of a recreated native child and is handled by
    /// <see cref="OnAttached"/> → <see cref="ReNavigateAndReplayAsync"/>, never here.</summary>
    private void NavigateOnce(NativeWebView webView)
    {
        if (_navigated)
        {
            return;
        }

        _navigated = true;

        // new Uri(<absolute path>) yields a properly percent-encoded file:/// URI
        // (spaces → %20 etc.) — the GraphSpike navigation pattern (ADR-004 D6).
        // It is always file://, so the NavigationStarted scheme guard always admits
        // it regardless of how WebView2 later canonicalizes the reported URI.
        var indexUri = new Uri(Path.Combine(AppContext.BaseDirectory, "web", "index.html"));
        webView.Navigate(indexUri);
    }

    /// <summary>
    /// WebMessageReceived may fire off the UI thread (WebView2 plumbing); everything
    /// downstream — TCS completions feeding the ShowGraphAsync awaits and every event
    /// raise into the VM's observable properties — is marshaled here once.
    /// </summary>
    private void OnWebMessageReceived(string body)
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            HandleMessage(body);
        }
        else
        {
            Dispatcher.UIThread.Post(() => HandleMessage(body));
        }
    }

    /// <summary>The inbound bridge-message router (ADR-037 D5/D6/D8: BridgeMessageReceived/
    /// BridgeReady/JsErrorReported/BridgeUnknownMessage + the heartbeat miss-reset). Internal
    /// (ADR-037 WP2, #241 test seam) — <c>GroupWeaver.App.Tests</c> drives this directly (no
    /// live <see cref="NativeWebView"/> needed: it is synchronous and operates purely on parsed
    /// JSON + primitive state), pinning the event vocabulary without reflection.</summary>
    internal void HandleMessage(string body)
    {
        var message = GraphMessageParser.Parse(body);

        // ADR-037 D4 Trace: type + size only, never the payload (source-gen, IsEnabled-guarded).
        LogBridgeMessageReceived(_bridgeLog, WireType(message), body.Length);

        // ADR-037 D8: ANY parseable inbound message proves the page's JS is running — reset
        // the heartbeat miss streak (successful bridge confirmations are a subset of this).
        if (message is not UnknownMessage)
        {
            _heartbeatMisses = 0;
        }

        switch (message)
        {
            case ReadyMessage ready:
                // ADR-037 D6: the rendering-mode truth — "SwiftShader" = software rendering
                // (the lab box loses its GPU driver on rebuild; perf numbers must state their
                // mode). Both fields are D9-insensitive (GPU string, browser version string).
                _bridgeLog.LogInformation(
                    new EventId(0, "BridgeReady"),
                    "BridgeReady {webglRenderer} {userAgent}",
                    ready.WebglRenderer, ready.UserAgent);
                _ready.TrySetResult();
                break;
            case LoadedMessage:
                _loaded?.TrySetResult();
                break;
            case FocusedMessage:
                _focused?.TrySetResult();
                break;
            case PngExportedMessage png:
                _pngExported = png.Data;
                _pngReady?.TrySetResult();
                break;
            case NodeClickMessage click:
                NodeClicked?.Invoke(this, new GraphNodeEventArgs(click.Id, click.Kind));
                break;
            case NodeExpandMessage expand:
                // The nodeExpand wire message carries no kind (graph.js dbltap handler);
                // empty string keeps the seam's shape — the VM ignores expand until AP 2.3.
                NodeExpandRequested?.Invoke(this, new GraphNodeEventArgs(expand.Id, string.Empty));
                break;
            case JsErrorMessage error:
                // ADR-037 D9: jsError text can embed DNs (node ids in stacks) — the structured
                // field is scrubbed at the call site. The RendererError event keeps the raw
                // message (UI surface, not a log).
                _bridgeLog.LogWarning(
                    new EventId(0, "JsErrorReported"),
                    "JsErrorReported {source} {msgScrubbed}",
                    error.Source, Redactor.Scrub(error.Message));
                RendererError?.Invoke(this, new GraphErrorEventArgs(error.Source, error.Message));
                break;
            case UnknownMessage unknown:
                // Reason is parser-authored diagnostic text (never the raw payload), but a
                // malformed-JSON reason can quote input fragments — scrub it too.
                _bridgeLog.LogWarning(
                    new EventId(0, "BridgeUnknownMessage"),
                    "BridgeUnknownMessage {reasonScrubbed} {bytes}",
                    Redactor.Scrub(unknown.Reason), body.Length);
                RendererError?.Invoke(this, new GraphErrorEventArgs(
                    "renderer", $"unparseable bridge message: {unknown.Reason}"));
                break;
        }
    }

    /// <summary>The wire <c>type</c> string of a parsed bridge message — for the Trace-level
    /// <c>BridgeMessageReceived</c> summary (types and sizes only, ADR-037 D4).</summary>
    private static string WireType(GraphMessage message) => message switch
    {
        ReadyMessage => "ready",
        LoadedMessage => "loaded",
        NodeClickMessage => "nodeClick",
        NodeExpandMessage => "nodeExpand",
        FocusedMessage => "focused",
        JsErrorMessage => "jsError",
        PngExportedMessage => "pngExported",
        _ => "unknown",
    };

    /// <summary>ShowGraphAsync continuations already resume on the UI thread (the VM
    /// awaits on the dispatcher context), but the event contract is "always UI thread"
    /// — marshal defensively. A disposed renderer (#122) raises nothing — the VM that
    /// owned the error surface is being torn down.</summary>
    private void RaiseError(string source, string message)
    {
        if (_disposed)
        {
            return;
        }

        if (Dispatcher.UIThread.CheckAccess())
        {
            RendererError?.Invoke(this, new GraphErrorEventArgs(source, message));
        }
        else
        {
            Dispatcher.UIThread.Post(
                () => RendererError?.Invoke(this, new GraphErrorEventArgs(source, message)));
        }
    }

    /// <summary>
    /// Tears the renderer down (#122, retires the ADR-024 never-disposed leak): cancels any
    /// in-flight command + the re-attach replay loop via <see cref="_disposeCts"/> (the existing
    /// never-throw command paths special-case own-cancellation), unsubscribes every handler wired
    /// in <see cref="CreateWebView"/>/<see cref="HardenWebView"/>, and detaches the single
    /// <see cref="NativeWebView"/> from its current host so its native WebView2 child is destroyed
    /// (NativeControlHost frees the child on detach — ADR-024). <see cref="NativeWebView"/> exposes
    /// no <c>Dispose()</c>; the detach IS the native teardown. Idempotent. After this the
    /// <see cref="View"/> getter returns the existing control (or <c>null</c>) and NEVER recreates
    /// it. UI-thread-only, like every other member (the owning VM's Dispose runs on the UI thread).
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _disposeCts.Cancel();

        // ADR-037 D8: the heartbeat dies with the renderer (the step teardown — no graph step,
        // no liveness to assert). Tick handler detached so the timer cannot root this instance.
        if (_heartbeatTimer is { } timer)
        {
            timer.Stop();
            timer.Tick -= OnHeartbeatTick;
            _heartbeatTimer = null;
        }

        if (_webView is { } webView)
        {
            // Unsubscribe the exact delegates wired at creation.
            if (_onWebMessage is not null)
            {
                webView.WebMessageReceived -= _onWebMessage;
            }

            if (_onAttachedToTree is not null)
            {
                webView.AttachedToVisualTree -= _onAttachedToTree;
            }

            if (_onDetachedFromTree is not null)
            {
                webView.DetachedFromVisualTree -= _onDetachedFromTree;
            }

            if (_onEnvironmentRequested is not null)
            {
                webView.EnvironmentRequested -= _onEnvironmentRequested;
            }

            if (_onNewWindowRequested is not null)
            {
                webView.NewWindowRequested -= _onNewWindowRequested;
            }

            if (_onAdapterCreated is not null)
            {
                webView.AdapterCreated -= _onAdapterCreated;
            }

            if (_onAdapterDestroyed is not null)
            {
                webView.AdapterDestroyed -= _onAdapterDestroyed;
            }

            webView.NavigationStarted -= OnNavigationStarted;

            // Detach from whatever host still parents it (a GraphHost ContentControl, the parking
            // Panel, or nothing) so NativeControlHost destroys the native WebView2 child. The
            // owning VM is gone, so this surface must leave the tree to stop painting + free the HWND.
            switch (webView.Parent)
            {
                case ContentControl host when ReferenceEquals(host.Content, webView):
                    host.Content = null;
                    break;
                case Panel panel:
                    panel.Children.Remove(webView);
                    break;
            }
        }

        _disposeCts.Dispose();
    }

    // ---------- ADR-037 D8: bridge liveness heartbeat (the WebView2-crash detector) ----------
    // The wrapper exposes no ProcessFailed; a crashed WebView2 child leaves the Avalonia control
    // attached and _ready stale-completed, so the ONLY reliable signal is the capability probe
    // failing on a page that previously declared ready. State machine (UI-thread-only):
    //   attach (first nav OR re-attach) -> StartHeartbeat (misses reset, timer running)
    //   detach                          -> StopHeartbeat (+ _ready reset by the detach handler)
    //   Dispose                         -> timer stopped + Tick detached
    //   tick: skip (no miss) while a single-flight command is in flight, or while _ready is not
    //         completed (page still (re)loading — the ADR-024 replay loop owns that window);
    //         probe '1' or ANY inbound bridge message -> misses = 0;
    //         probe fault/timeout/'0' -> HeartbeatMissed Warn; 3rd consecutive -> HeartbeatLost
    //         Error + RaiseError into the existing RendererError -> LoadError surface.
    //
    // ADR-037 WP2 (#241) TEST SEAM: the tick/miss/lost state machine is otherwise undrivable in a
    // unit test (no live NativeWebView, no injectable DispatcherTimer clock). TestScriptInvoker
    // (internal, test-only) substitutes for `_webView.InvokeScript` and — when set — ALSO bypasses
    // the "_webView present + _ready completed" gate below (a test renderer never navigates), so
    // ProbeHeartbeatOnceAsync() drives the REAL miss/reset/HeartbeatLost logic deterministically:
    // construct a renderer, set TestScriptInvoker to a stub returning '1'/'0'/throwing, call
    // ProbeHeartbeatOnceAsync() repeatedly, read HeartbeatMisses. Production never sets
    // TestScriptInvoker, so this changes no production behavior.

    /// <summary>Test seam (ADR-037 WP2, #241): substitutes for the live
    /// <see cref="NativeWebView.InvokeScript"/> call the heartbeat probe would otherwise make.
    /// Production leaves this <c>null</c>. When set, <see cref="ProbeHeartbeatAsync"/> ALSO skips
    /// its normal "<c>_webView</c> present + <c>_ready</c> completed" precondition — a test
    /// renderer never attaches/navigates, so that gate would otherwise never open.</summary>
    internal Func<string, Task<string?>>? TestScriptInvoker { get; set; }

    /// <summary>Test seam (ADR-037 WP2, #241): runs exactly one heartbeat probe cycle,
    /// awaitably, bypassing the real 10 s <see cref="DispatcherTimer"/> entirely. Callers drive
    /// the miss-streak state machine by awaiting this repeatedly with <see cref="TestScriptInvoker"/>
    /// set.</summary>
    internal Task ProbeHeartbeatOnceAsync() => ProbeHeartbeatAsync();

    /// <summary>Test seam (ADR-037 WP2, #241): the current consecutive-miss streak — read after
    /// <see cref="ProbeHeartbeatOnceAsync"/> to assert increment-on-miss / reset-on-success /
    /// reset-on-message without reflection.</summary>
    internal int HeartbeatMisses => _heartbeatMisses;

    /// <summary>Test seam (ADR-037 WP2, #241): zeroes the miss streak so a single test can arrange
    /// a fresh baseline between assertions without constructing a new renderer.</summary>
    internal void ResetHeartbeatForTest() => _heartbeatMisses = 0;

    /// <summary>Starts (or resumes) the heartbeat. Called from <see cref="OnAttached"/> on the
    /// UI thread; idempotent; a disposed renderer never restarts it.</summary>
    private void StartHeartbeat()
    {
        if (_disposed)
        {
            return;
        }

        _heartbeatMisses = 0;
        if (_heartbeatTimer is null)
        {
            _heartbeatTimer = new DispatcherTimer { Interval = HeartbeatInterval };
            _heartbeatTimer.Tick += OnHeartbeatTick;
        }

        _heartbeatTimer.Start();
    }

    /// <summary>Stops the heartbeat (detach — no page to probe). The timer object survives for
    /// the next <see cref="StartHeartbeat"/>; only <see cref="Dispose"/> retires it.</summary>
    private void StopHeartbeat() => _heartbeatTimer?.Stop();

    /// <summary>DispatcherTimer.Tick is an async-void seam: NOTHING may escape (an unhandled
    /// throw would crash the app). The probe body handles every expected failure shape as a
    /// miss; this wrapper is the last-chance guard.</summary>
    private async void OnHeartbeatTick(object? sender, EventArgs e)
    {
        try
        {
            await ProbeHeartbeatAsync();
        }
        catch
        {
            // Never-throw: a heartbeat failure must never become an app failure.
        }
    }

    private async Task ProbeHeartbeatAsync()
    {
        // Skip — never count a miss — while: torn down; a probe is already awaiting (a wedged
        // probe outlasting the 10 s tick); a single-flight command is in flight (ADR-037 D8 —
        // the command's own 60 s bounded wait is the liveness authority then); or the page has
        // not (re)declared ready (startup / the detach-reset window the ADR-024 replay owns).
        // TestScriptInvoker (WP2 test seam) skips the webView/ready precondition entirely — a
        // test renderer never attaches/navigates, so that gate would otherwise never open.
        if (_disposed || _heartbeatProbeInFlight || _commandInFlight)
        {
            return;
        }

        if (TestScriptInvoker is null && (_webView is null || !_ready.Task.IsCompletedSuccessfully))
        {
            return;
        }

        _heartbeatProbeInFlight = true;
        try
        {
            string? probe = null;
            try
            {
                probe = await InvokeProbeScriptAsync()
                    .WaitAsync(HeartbeatProbeTimeout, _disposeCts.Token);
            }
            catch (OperationCanceledException) when (_disposeCts.IsCancellationRequested)
            {
                return; // own-Dispose: the renderer is tearing down, nothing to judge.
            }
            catch
            {
                // A faulted or 2 s-pending InvokeScript on a ready page IS the miss signal
                // (dead child process, wedged renderer) — fall through to the miss accounting.
            }

            if (_disposed)
            {
                return;
            }

            if (probe is not null && probe.Contains('1'))
            {
                _heartbeatMisses = 0;
                return;
            }

            _heartbeatMisses++;
            _rendererLog.LogWarning(
                new EventId(0, "HeartbeatMissed"), "HeartbeatMissed {misses}", _heartbeatMisses);
            if (_heartbeatMisses == HeartbeatLostThreshold)
            {
                // Raised exactly once per loss streak (== not >=): further misses keep the Warn
                // trail without re-banner-ing; a later successful probe/message resets the streak.
                _rendererLog.LogError(
                    new EventId(0, "HeartbeatLost"), "HeartbeatLost {misses}", _heartbeatMisses);
                RaiseError(
                    "renderer",
                    "graph view stopped responding (3 heartbeat probes missed) - Reload scope");
            }
        }
        finally
        {
            _heartbeatProbeInFlight = false;
        }
    }

    /// <summary>The probe's sole script-invocation point (ADR-037 WP2, #241): <see cref="TestScriptInvoker"/>
    /// when set (tests — no live control touched), else the real <see cref="NativeWebView.InvokeScript"/>
    /// on the attached <see cref="_webView"/> (production — guaranteed non-null here by the caller's
    /// gate, which requires either a live <c>_webView</c> or a test invoker).</summary>
    private Task<string?> InvokeProbeScriptAsync() =>
        TestScriptInvoker is { } invoker ? invoker(BridgeProbeScript) : _webView!.InvokeScript(BridgeProbeScript);

    // ADR-037 D4 (the FileLogSink writer-loop breadcrumb): the per-chunk / per-message Trace
    // paths use LoggerMessage source generators — IsEnabled-guarded and allocation-free at the
    // default Information level — NEVER params-array LoggerExtensions. Sizes and types only.
    [LoggerMessage(EventId = 1, EventName = "BridgeChunkSent", Level = LogLevel.Trace,
        Message = "BridgeChunkSent {i} {bytes}")]
    private static partial void LogBridgeChunkSent(ILogger logger, int i, int bytes);

    [LoggerMessage(EventId = 2, EventName = "BridgeMessageReceived", Level = LogLevel.Trace,
        Message = "BridgeMessageReceived {type} {bytes}")]
    private static partial void LogBridgeMessageReceived(ILogger logger, string type, int bytes);
}
