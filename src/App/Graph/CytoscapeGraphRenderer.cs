using Avalonia.Controls;
using Avalonia.Threading;

using GroupWeaver.Core.Graph;
using GroupWeaver.Core.Rules;

namespace GroupWeaver.App.Graph;

/// <summary>
/// The production <see cref="IGraphRenderer"/> (ADR-004 D5/D6): owns a single
/// <see cref="NativeWebView"/> hosting the vendored Cytoscape bundle, served via
/// file:// from <c>web/index.html</c> next to the exe. The WebView is created
/// lazily on first <see cref="View"/> access (UI thread — it is a control) and
/// navigates on its FIRST attach to the visual tree. Outbound: chunked
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

    private readonly TaskCompletionSource _ready =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private TaskCompletionSource? _loaded;
    private TaskCompletionSource? _focused;
    private NativeWebView? _webView;
    private bool _navigated;
    private bool _commandInFlight;

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
            if (!await TryAwaitReadyAsync(cancellationToken))
            {
                return;
            }

            // Armed BEFORE the first dispatch: the page may confirm faster than the
            // last InvokeScript returns.
            _loaded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            await DispatchAsync(GraphChunker.ToChunkCommands(graph, report, belowMap), cancellationToken);
            await AwaitConfirmationAsync(
                _loaded.Task, "graph render never completed (60 s)", cancellationToken);
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
            if (!await TryAwaitReadyAsync(cancellationToken))
            {
                return;
            }

            // Armed BEFORE the first dispatch: the page may confirm faster than the
            // last InvokeScript returns.
            _loaded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            await DispatchAsync(GraphChunker.ToUpdateCommands(graph, report, belowMap), cancellationToken);
            await AwaitConfirmationAsync(
                _loaded.Task, "graph update never completed (60 s)", cancellationToken);
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

            // Armed BEFORE the dispatch: the page may confirm faster than
            // InvokeScript returns.
            _focused = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            await DispatchAsync(
                [GraphJson.Serialize(new FocusDto("focus", [.. dns]))], cancellationToken);
            await AwaitConfirmationAsync(
                _focused.Task, "focus move never completed (60 s)", cancellationToken);
        }
        finally
        {
            _commandInFlight = false;
        }
    }

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
        foreach (var command in commands)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // The command JSON is embedded verbatim as a JS object literal: safe,
            // because GraphJson's default STJ encoder emits ASCII-only output
            // (non-ASCII — including the JS-literal-breaking U+2028/U+2029 —
            // escaped to \uXXXX), and ASCII-safe JSON is a valid JS expression.
            // _webView cannot be null here: the ready message only arrives from
            // the navigated WebView, which only exists once View was accessed.
            await _webView!.InvokeScript($"window.bridge.dispatch({command})");
        }
    }

    /// <summary>The <c>focus</c> bridge command (ADR-005 D2); serialized through
    /// <see cref="GraphJson"/> so comma/quote-containing DNs are escaped correctly.</summary>
    private sealed record FocusDto(string Type, IReadOnlyList<string> Ids);

    private NativeWebView CreateWebView()
    {
        var webView = new NativeWebView();
        webView.WebMessageReceived += (_, e) => OnWebMessageReceived(e.Body ?? string.Empty);
        webView.AttachedToVisualTree += (_, _) => NavigateOnce(webView);
        HardenWebView(webView);
        return webView;
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

    /// <summary>Navigates on the FIRST attach only — the page (and its accumulated
    /// cytoscape state) must survive re-attach, not reload over it.</summary>
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
