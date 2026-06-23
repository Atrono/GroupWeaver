using Avalonia.Controls;
using Avalonia.Threading;

using GroupWeaver.Core.Diff;
using GroupWeaver.Core.Graph;
using GroupWeaver.Core.Rules;

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
public sealed class CytoscapeGraphRenderer : IGraphRenderer
{
    /// <summary>Bound on each bridge wait (ready, loaded, focused) — never a hang
    /// (ADR-004 D5).</summary>
    private static readonly TimeSpan BridgeTimeout = TimeSpan.FromSeconds(60);

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

    /// <summary>The chunk commands of the most recent successful base render (show/update/
    /// diff) — replayed after a re-attach, when Avalonia recreates the native control blank
    /// (<see cref="CreateWebView"/>). Transient camera/selection ops (focus/select/busy) are
    /// NOT cached: they are not the base graph to restore.</summary>
    private IReadOnlyList<string>? _lastRenderChunks;

    /// <summary>The WebView, created on first access (single instance for the
    /// renderer's lifetime). Must be accessed on the UI thread.</summary>
    public Control? View => _webView ??= CreateWebView();

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
                GraphChunker.ToChunkCommands(graph, report, belowMap),
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
                GraphChunker.ToUpdateCommands(graph, report, belowMap),
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
                GraphChunker.ToChunkCommands(
                    union,
                    RuleReport.Empty,
                    belowMap: null,
                    nodeDiffMap: diff.NodeStatus,
                    edgeDiffMap: diff.EdgeStatus),
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
                    _focused.Task, "focus move never completed (60 s)", cancellationToken);
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
                return null;
            }

            try
            {
                // Armed BEFORE the dispatch: the page may confirm faster than
                // InvokeScript returns.
                _pngExported = null;
                _pngReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                await DispatchAsync(
                    [GraphJson.Serialize(new ExportPngDto("exportPng", 2, false, "#1b1f27"))], cancellationToken);
                await AwaitConfirmationAsync(
                    _pngReady.Task, "png export never completed (60 s)", cancellationToken);
                return DecodePngOrNull(_pngExported);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
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
            RaiseError("renderer", $"select dispatch failed: {ex.Message}");
        }
    }

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
    /// an overlap is a caller bug, never queued (ADR-005 D3).</summary>
    private void EnterSingleFlight(string operation)
    {
        if (_commandInFlight)
        {
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
        IReadOnlyList<string> chunks, string timeoutMessage, CancellationToken cancellationToken)
    {
        if (!await TryAwaitReadyAsync(cancellationToken))
        {
            return;
        }

        try
        {
            var loaded = _loaded =
                new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            await DispatchAsync(chunks, cancellationToken);
            await AwaitConfirmationAsync(loaded.Task, timeoutMessage, cancellationToken);

            // Cache as the replay base only on a real confirmation — a soft 60 s timeout
            // (AwaitConfirmationAsync swallows it to a RendererError) leaves it unset/stale.
            if (loaded.Task.IsCompletedSuccessfully)
            {
                _lastRenderChunks = chunks;
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
    /// (never a hang, never a throw; ADR-004 D5).</summary>
    private async Task<bool> TryAwaitReadyAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _ready.Task.WaitAsync(BridgeTimeout, cancellationToken);
            return true;
        }
        catch (TimeoutException)
        {
            RaiseError("renderer", "web bundle never became ready (60 s)");
            return false;
        }
    }

    /// <summary>Bounded wait for a page confirmation (<c>loaded</c>/<c>focused</c>);
    /// timeout → <see cref="RendererError"/> and a normal return.</summary>
    private async Task AwaitConfirmationAsync(
        Task confirmation, string timeoutMessage, CancellationToken cancellationToken)
    {
        try
        {
            await confirmation.WaitAsync(BridgeTimeout, cancellationToken);
        }
        catch (TimeoutException)
        {
            RaiseError("renderer", timeoutMessage);
        }
    }

    private async Task DispatchAsync(
        IReadOnlyList<string> commands, CancellationToken cancellationToken)
    {
        // Defensive: a ready confirmation always implies a live _webView, but a re-attach
        // race could in principle null it between checks. Bail quietly rather than NRE.
        if (_webView is null)
        {
            return;
        }

        foreach (var command in commands)
        {
            cancellationToken.ThrowIfCancellationRequested();

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

    private NativeWebView CreateWebView()
    {
        var webView = new NativeWebView();
        webView.WebMessageReceived += (_, e) => OnWebMessageReceived(e.Body ?? string.Empty);
        webView.AttachedToVisualTree += (_, _) => OnAttached(webView);

        // On detach Avalonia destroys the native WebView2 child and recreates it BLANK on
        // re-attach (NativeControlHost). The old page (and its "ready") is gone, so reset the
        // ready gate to a fresh, uncompleted source: a later command then awaits the REAL new
        // ready (raised by the re-navigated page) instead of resuming on a stale-completed one.
        // All access is UI-thread (this handler, View creation, command resumes), so the plain
        // field swap is safe.
        webView.DetachedFromVisualTree += (_, _) =>
            _ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

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
            return;
        }

        _ = ReNavigateAndReplayAsync(webView);
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
        try
        {
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
                        .InvokeScript("(typeof window.bridge!=='undefined'&&typeof window.bridge.dispatch==='function')?'1':'0'")
                        .WaitAsync(TimeSpan.FromSeconds(2));
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
                    RaiseError("renderer", "recreated page never accepted script after re-attach (60 s)");
                    return;
                }

                await Task.Delay(TimeSpan.FromMilliseconds(150));
            }

            var loaded = _loaded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            await DispatchAsync(chunks, CancellationToken.None);
            await AwaitConfirmationAsync(
                loaded.Task, "graph replay never completed (60 s)", CancellationToken.None);
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
        webView.EnvironmentRequested += (_, e) => e.EnableDevTools = false;
        webView.NewWindowRequested += (_, e) => e.Handled = true;
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

    private void HandleMessage(string body)
    {
        switch (GraphMessageParser.Parse(body))
        {
            case ReadyMessage:
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
                RendererError?.Invoke(this, new GraphErrorEventArgs(error.Source, error.Message));
                break;
            case UnknownMessage unknown:
                RendererError?.Invoke(this, new GraphErrorEventArgs(
                    "renderer", $"unparseable bridge message: {unknown.Reason}"));
                break;
        }
    }

    /// <summary>ShowGraphAsync continuations already resume on the UI thread (the VM
    /// awaits on the dispatcher context), but the event contract is "always UI thread"
    /// — marshal defensively.</summary>
    private void RaiseError(string source, string message)
    {
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
}
