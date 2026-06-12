using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GroupWeaver.App.Graph;
using GroupWeaver.App.Startup;
using GroupWeaver.Core.Graph;
using GroupWeaver.Core.Model;
using GroupWeaver.Core.Providers;

namespace GroupWeaver.App.ViewModels;

/// <summary>
/// Workspace step (AP 2.2 S6): loads the chosen root's scope at construction, builds
/// the drawn graph and hands it to the <see cref="IGraphRenderer"/> seam (ADR-004
/// D5/D6) — the same observable-task pattern as <see cref="ShellViewModel.Initialization"/>,
/// never fire-and-forget. Error policy (ADR-003 D7): <see cref="DirectoryUnavailableException"/>
/// surfaces inline via <see cref="LoadError"/> WITHOUT the Connect step's demo hint
/// (a connection already succeeded); cancellation (<see cref="Dispose"/>) settles
/// <see cref="Initialization"/> quietly; everything else propagates (crash = bug).
/// Contract pinned by <c>tests/GroupWeaver.App.Tests/WorkspaceLoadTests.cs</c>.
/// </summary>
public sealed partial class WorkspaceViewModel : ObservableObject, IDisposable
{
    private readonly CancellationTokenSource _cts = new();
    private bool _disposed;

    /// <summary>True while the scope load + first render are in flight.</summary>
    [ObservableProperty]
    private bool _isLoading;

    /// <summary>Inline load/renderer error; <c>null</c> hides the error block.</summary>
    [ObservableProperty]
    private string? _loadError;

    /// <summary>DN of the last clicked graph node — the AP 2.5 detail-panel seam.
    /// Carried verbatim (data-model rule: DN strings are never canonicalized).</summary>
    [ObservableProperty]
    private string? _selectedDn;

    /// <summary>Status line for the loaded graph: <c>"&lt;n&gt; objects, &lt;m&gt; edges"</c>
    /// over the DRAWN graph (<see cref="Graph"/> node/edge counts, not the snapshot's).</summary>
    [ObservableProperty]
    private string? _graphSummary;

    public WorkspaceViewModel(
        IDirectoryProvider provider,
        AdObject root,
        DirectoryConnection connection,
        bool webView2Missing = false,
        Func<IGraphRenderer>? graphRendererFactory = null)
    {
        Provider = provider;
        Root = root;
        Connection = connection;
        WebView2Missing = webView2Missing;

        // ADR-004 D5: re-check the runtime probe BEFORE building a renderer — a
        // missing WebView2 Runtime must not even invoke the factory.
        if (graphRendererFactory is not null && !webView2Missing)
        {
            GraphRenderer = graphRendererFactory();
            GraphRenderer.NodeClicked += (_, e) => SelectedDn = e.Dn;
            // Pinned decision: renderer failures reuse the ONE inline error surface
            // instead of growing a parallel property; IsLoading stays untouched.
            GraphRenderer.RendererError += (_, e) => LoadError = $"{e.Source}: {e.Message}";
            // NodeExpandRequested ships with the seam but stays deliberately
            // unsubscribed until lazy expand lands (AP 2.3, ADR-004 D5).
        }

        Initialization = LoadAsync(_cts.Token);
    }

    /// <summary>Provider behind the active connection; the scope is loaded from it.</summary>
    public IDirectoryProvider Provider { get; }

    /// <summary>The root the user picked in the PickRoot step (mandatory entry filter).</summary>
    public AdObject Root { get; }

    /// <summary>Connection summary handed over from the Connect step.</summary>
    public DirectoryConnection Connection { get; }

    /// <summary>DN of the chosen root.</summary>
    public string RootDn => Root.Dn;

    /// <summary>Display name of the chosen root.</summary>
    public string RootName => Root.Name;

    /// <summary>Status-bar line; same shape as the M1 DoD console line.</summary>
    public string ConnectionSummary =>
        $"connected, {Connection.GroupCount} groups loaded — {Connection.Description}";

    /// <summary>
    /// Handed through from <see cref="ShellViewModel"/> at construction (ADR-003 D3):
    /// switches the GraphHost placeholder to its missing-runtime variant and vetoes
    /// the renderer factory.
    /// </summary>
    public bool WebView2Missing { get; }

    /// <summary>Placeholder hyperlink: open the runtime's download page in the browser.</summary>
    public IRelayCommand OpenWebView2DownloadPageCommand { get; } =
        new RelayCommand(WebView2Runtime.OpenDownloadPage);

    /// <summary>
    /// The scope-load-and-render flow kicked off at construction; completes only once
    /// the renderer accepted the graph (its readiness must not lie, ADR-004 D5).
    /// </summary>
    public Task Initialization { get; }

    /// <summary>Renderer built from the ctor factory; <c>null</c> when the factory is
    /// null or the WebView2 Runtime is missing — the view then keeps its placeholder.</summary>
    public IGraphRenderer? GraphRenderer { get; }

    /// <summary>The loaded scope; <c>null</c> until the load completed (AP 2.3/2.5 seam).</summary>
    public DirectorySnapshot? Snapshot { get; private set; }

    /// <summary>The built graph model handed to the renderer; <c>null</c> until built.</summary>
    public GraphModel? Graph { get; private set; }

    private async Task LoadAsync(CancellationToken cancellationToken)
    {
        IsLoading = true;
        try
        {
            Snapshot = await Provider.LoadScopeAsync(RootDn, cancellationToken);
            Graph = GraphBuilder.Build(Snapshot, RootDn);
            if (GraphRenderer is not null)
            {
                await GraphRenderer.ShowGraphAsync(Graph, cancellationToken);
            }

            GraphSummary = $"{Graph.Nodes.Count} objects, {Graph.Edges.Count} edges";
        }
        catch (DirectoryUnavailableException ex)
        {
            // No demo hint here (unlike the Connect step): a connection already succeeded.
            LoadError = ex.Message;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Our own Dispose cancelled the load — settle Initialization quietly.
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>Cancels an in-flight scope load; idempotent.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _cts.Cancel();
        _cts.Dispose();
    }
}
