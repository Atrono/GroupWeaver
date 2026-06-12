using Avalonia.Controls;
using Avalonia.Threading;

using GroupWeaver.Core.Graph;

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
    /// <summary>Bound on each bridge wait (ready, loaded) — never a hang (ADR-004 D5).</summary>
    private static readonly TimeSpan BridgeTimeout = TimeSpan.FromSeconds(60);

    private readonly TaskCompletionSource _ready =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private TaskCompletionSource? _loaded;
    private NativeWebView? _webView;
    private bool _navigated;
    private bool _showInFlight;

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
    /// Re-entrancy: a second call while one is in flight throws
    /// <see cref="InvalidOperationException"/> — the VM shows exactly one graph per
    /// workspace lifetime, so an overlap is a caller bug; queueing would hide it
    /// (AP 2.3's lazy expand uses its own command, not a second full show).
    /// </summary>
    public async Task ShowGraphAsync(GraphModel graph, CancellationToken cancellationToken = default)
    {
        if (_showInFlight)
        {
            throw new InvalidOperationException(
                "ShowGraphAsync is already in flight — the renderer shows one graph at a time.");
        }

        _showInFlight = true;
        try
        {
            try
            {
                await _ready.Task.WaitAsync(BridgeTimeout, cancellationToken);
            }
            catch (TimeoutException)
            {
                RaiseError("renderer", "web bundle never became ready (60 s)");
                return;
            }

            // Armed BEFORE the first dispatch: the page may confirm faster than the
            // last InvokeScript returns.
            _loaded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            foreach (var command in GraphChunker.ToChunkCommands(graph))
            {
                cancellationToken.ThrowIfCancellationRequested();

                // The chunk JSON is embedded verbatim as a JS object literal: safe,
                // because GraphJson's default STJ encoder emits ASCII-only output
                // (non-ASCII — including the JS-literal-breaking U+2028/U+2029 —
                // escaped to \uXXXX), and ASCII-safe JSON is a valid JS expression.
                // _webView cannot be null here: the ready message only arrives from
                // the navigated WebView, which only exists once View was accessed.
                await _webView!.InvokeScript($"window.bridge.dispatch({command})");
            }

            try
            {
                await _loaded.Task.WaitAsync(BridgeTimeout, cancellationToken);
            }
            catch (TimeoutException)
            {
                RaiseError("renderer", "graph render never completed (60 s)");
            }
        }
        finally
        {
            _showInFlight = false;
        }
    }

    private NativeWebView CreateWebView()
    {
        var webView = new NativeWebView();
        webView.WebMessageReceived += (_, e) => OnWebMessageReceived(e.Body ?? string.Empty);
        webView.AttachedToVisualTree += (_, _) => NavigateOnce(webView);
        return webView;
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
        webView.Navigate(new Uri(Path.Combine(AppContext.BaseDirectory, "web", "index.html")));
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
