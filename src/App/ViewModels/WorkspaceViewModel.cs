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
/// AP 2.3 adds the lazy-expand pipeline (ADR-005 D3) behind
/// <see cref="IGraphRenderer.NodeExpandRequested"/>, observable via <see cref="Expansion"/>
/// under the same error policy, plus <see cref="RefreshCommand"/> (ADR-005 D4): a
/// FORCED expand of <see cref="SelectedDn"/> through the same pipeline. AP 2.5 (ADR-007)
/// projects the selection into <see cref="DetailPanel"/> — snapshot-only, recomputed on
/// selection change and in each pipeline's <c>finally</c>. Contract pinned by
/// <c>tests/GroupWeaver.App.Tests/WorkspaceLoadTests.cs</c>, <c>WorkspaceExpandTests.cs</c>
/// and <c>WorkspaceDetailTests.cs</c>.
/// </summary>
public sealed partial class WorkspaceViewModel : ObservableObject, IDisposable
{
    private readonly CancellationTokenSource _cts = new();
    private bool _disposed;

    /// <summary>True while the scope load + first render — or an expand pipeline
    /// (AP 2.3; fetch AND focus-only branch alike) — is in flight; the ONE global
    /// busy gate (ADR-005 D3).</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RefreshCommand))]
    private bool _isLoading;

    /// <summary>Inline load/renderer error; <c>null</c> hides the error block.</summary>
    [ObservableProperty]
    private string? _loadError;

    /// <summary>DN of the last clicked graph node — the AP 2.5 detail-panel seam.
    /// Carried verbatim (data-model rule: DN strings are never canonicalized).</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RefreshCommand))]
    private string? _selectedDn;

    /// <summary>Status line for the loaded graph: <c>"&lt;n&gt; objects, &lt;m&gt; edges"</c>
    /// over the DRAWN graph (<see cref="Graph"/> node/edge counts, not the snapshot's).</summary>
    [ObservableProperty]
    private string? _graphSummary;

    /// <summary>The AP 2.5 detail-panel projection of <see cref="SelectedDn"/> (ADR-007):
    /// the ONLY thing the panel binds. Recomputed at exactly three points — selection
    /// change plus each pipeline's <c>finally</c>; <c>null</c> while nothing is selected
    /// or no snapshot exists yet. Pinned by <c>WorkspaceDetailTests.cs</c>.</summary>
    [ObservableProperty]
    private DetailPanelModel? _detailPanel;

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
            // AP 2.3 (ADR-005 D3): the expand decision is made from the SNAPSHOT
            // (kind + load state), never the event's Kind string — only the DN flows.
            GraphRenderer.NodeExpandRequested += (_, e) => OnNodeExpandRequested(e.Dn);
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

    /// <summary>
    /// The lazy-expand pipeline of the most recent ACCEPTED expand gesture (ADR-005 D3)
    /// — the same observable-task pattern as <see cref="Initialization"/>, never
    /// fire-and-forget. Completed from construction (safe to await before any gesture);
    /// left reference-unchanged when a gesture is dropped by the busy gate.
    /// </summary>
    public Task Expansion { get; private set; } = Task.CompletedTask;

    /// <summary>Renderer built from the ctor factory; <c>null</c> when the factory is
    /// null or the WebView2 Runtime is missing — the view then keeps its placeholder.</summary>
    public IGraphRenderer? GraphRenderer { get; }

    /// <summary>The loaded scope; <c>null</c> until the load completed (AP 2.3/2.5 seam).</summary>
    public DirectorySnapshot? Snapshot { get; private set; }

    /// <summary>The built graph model handed to the renderer; <c>null</c> until built.</summary>
    public GraphModel? Graph { get; private set; }

    /// <summary>ADR-007 D1: selection re-projects IMMEDIATELY — snapshot-only reads,
    /// never busy-gated, so the panel stays responsive during any in-flight pipeline.</summary>
    partial void OnSelectedDnChanged(string? value) => RecomputeDetailPanel();

    /// <summary>The single projection write: a pure snapshot read through the
    /// <see cref="DetailPanelModel.Build"/> choke point — never calls the provider,
    /// never checks or takes the busy gate (ADR-007 D1).</summary>
    private void RecomputeDetailPanel() =>
        DetailPanel = Snapshot is null ? null : DetailPanelModel.Build(Snapshot, SelectedDn);

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
            // ADR-007 D1: every pipeline run re-projects (error paths included) — a
            // selection made during the gated load shows up without a further gesture.
            RecomputeDetailPanel();
            IsLoading = false;
        }
    }

    private void OnNodeExpandRequested(string dn)
    {
        // ADR-005 D3: ONE global busy gate (IsLoading) — a gesture during the initial
        // scope load or another in-flight expand is silently dropped, never queued, and
        // Expansion stays reference-unchanged (replacing it would lie to observers).
        if (_disposed || IsLoading || Snapshot is null)
        {
            return;
        }

        Expansion = ExpandAsync(dn, forceFetch: false, _cts.Token);
    }

    /// <summary>
    /// Refresh = forced expand of <see cref="SelectedDn"/> (ADR-005 D4): the SAME
    /// pipeline as a dbltap gesture with the IsLoaded cache check BYPASSED — refresh
    /// exists FOR loaded nodes (<see cref="DirectorySnapshot.SetMembers"/> REPLACES,
    /// the refresh semantics of the data-model contract). A stale-armed Execute
    /// (busy/disposed/renderer-less/selection no longer fetchable) is dropped,
    /// never queued.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanRefresh))]
    private void Refresh()
    {
        if (_disposed || IsLoading || Snapshot is null || GraphRenderer is null
            || SelectedDn is not { } dn || !IsFetchable(Snapshot.GetKind(dn)))
        {
            return;
        }

        Expansion = ExpandAsync(dn, forceFetch: true, _cts.Token);
    }

    /// <summary>Armed iff a renderer exists (the AP 2.5 seam allows selection without
    /// one — nothing to update then), a fetchable snapshot kind is selected, and the
    /// ONE global busy gate is idle (ADR-005 D4); never touches the provider.</summary>
    private bool CanRefresh() =>
        !IsLoading
        && GraphRenderer is not null
        && Snapshot is not null
        && SelectedDn is { } dn
        && IsFetchable(Snapshot.GetKind(dn));

    /// <summary>The fetchable kinds (ADR-005 D3): groups plus External — a frontier
    /// DN missing from the snapshot's Objects resolves to External by contract.</summary>
    private static bool IsFetchable(AdObjectKind kind) => kind
        is AdObjectKind.GlobalGroup
        or AdObjectKind.DomainLocalGroup
        or AdObjectKind.UniversalGroup
        or AdObjectKind.External;

    private async Task ExpandAsync(string dn, bool forceFetch, CancellationToken cancellationToken)
    {
        var snapshot = Snapshot!;
        // Both entry points already drop renderer-less calls (gestures only arrive
        // from a built renderer; Refresh re-guards) — defense in depth for the
        // AP 2.5 seam, where a selection can exist without a renderer.
        if (GraphRenderer is not { } renderer)
        {
            return;
        }

        // ADR-005 D3: the ONE global busy gate is held across the WHOLE pipeline —
        // the focus-only branch included, so the entry guards drop overlapping
        // gestures during an in-flight camera move exactly like during a fetch.
        IsLoading = true;
        try
        {
            var fetchable = IsFetchable(snapshot.GetKind(dn));
            if (!fetchable || (!forceFetch && snapshot.IsLoaded(dn)))
            {
                // Cache hit / non-group dbltap: a pure camera move over the node plus
                // its cached members — NEVER a fabricated SetMembers (null ≠ empty; the
                // AP 3.2/3.4 checks read exactly this load state).
                var cached = snapshot.GetMembers(dn);
                IReadOnlyCollection<string> focus = cached is null ? [dn] : [dn, .. cached];
                await renderer.FocusAsync(focus, cancellationToken);
                return;
            }

            LoadError = null; // every new attempt clears the inline error (load policy)

            // Transactional fetch (ADR-005 D3): resolve the parent object only when its
            // DN is missing from the snapshot (External frontier node → true kind), then
            // fetch the members; NOTHING is applied until both provider calls came back.
            AdObject? resolved = null;
            if (!snapshot.TryGetObject(dn, out _))
            {
                resolved = await Provider.GetObjectAsync(dn, cancellationToken);
            }

            // A frontier DN may resolve to a NON-group (e.g. User): GetMembersAsync then
            // answers truthfully empty and SetMembers records REAL load state — never
            // fabricated (null-vs-empty contract; AP 3.2 filters by kind, not this).
            var fetched = await Provider.GetMembersAsync(dn, cancellationToken);
            var memberDns = fetched.Select(m => m.Dn).ToList();

            if (resolved is not null)
            {
                snapshot.AddObject(resolved);
            }

            foreach (var member in fetched)
            {
                snapshot.AddObject(member);
            }

            // SetMembers LAST: marks the parent loaded (possibly loaded-and-empty),
            // member DNs in fetch order.
            snapshot.SetMembers(dn, memberDns);

            Graph = GraphBuilder.Build(snapshot, RootDn);
            // Replace-in-place (ADR-005 D1/D2): exactly ONE UpdateGraphAsync — never a
            // second ShowGraphAsync (destroy + fit would lose the viewport).
            await renderer.UpdateGraphAsync(Graph, cancellationToken);
            GraphSummary = $"{Graph.Nodes.Count} objects, {Graph.Edges.Count} edges";
            await renderer.FocusAsync([dn, .. memberDns], cancellationToken);
        }
        catch (DirectoryUnavailableException ex)
        {
            // Same inline surface as the scope load; snapshot/graph/renderer untouched.
            LoadError = ex.Message;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Our own Dispose cancelled the expand — settle Expansion quietly.
        }
        finally
        {
            // ADR-007 D1: re-project the CURRENT selection — an upserted/refreshed
            // selected object updates the open panel; a stale panel is impossible.
            RecomputeDetailPanel();
            IsLoading = false;
        }
    }

    /// <summary>Cancels an in-flight scope load or expand fetch; idempotent.</summary>
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
