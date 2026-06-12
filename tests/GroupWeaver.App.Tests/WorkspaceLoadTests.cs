using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;

using GroupWeaver.App.Graph;
using GroupWeaver.App.Tests.Fakes;
using GroupWeaver.App.ViewModels;
using GroupWeaver.App.Views;
using GroupWeaver.Core.Graph;
using GroupWeaver.Core.Model;
using GroupWeaver.Core.Providers;

using Xunit;

namespace GroupWeaver.App.Tests;

/// <summary>
/// Pins the AP 2.2 S6 workspace scope-load flow behind the <see cref="IGraphRenderer"/>
/// seam (ADR-004 D5): the load kicked off at construction and kept observable through
/// <see cref="WorkspaceViewModel.Initialization"/> (the ShellViewModel/RootPicker
/// observable-task pattern), the GraphBuilder hand-off to the renderer, the
/// <c>"&lt;n&gt; objects, &lt;m&gt; edges"</c> summary, the error policy (ADR-003 D7:
/// <see cref="DirectoryUnavailableException"/> inline WITHOUT the Connect step's demo
/// hint — a connection already succeeded; everything else propagates — crash = bug),
/// Dispose-cancellation, the renderer mount/placeholder switch in <c>GraphHost</c>,
/// and the three renderer events. The fixture snapshot deliberately contains the
/// circular nesting pair (GG_Circle_A ↔ GG_Circle_B) — every load over it must
/// terminate — plus one out-of-scope member DN so the drawn graph differs from the
/// snapshot object list (the summary pins the DRAWN counts, not the snapshot's).
/// VM-only behavior uses plain facts; visual-tree pins use Avalonia.Headless.
/// </summary>
public sealed class WorkspaceLoadTests
{
    private const string RootDn = "OU=Lab,DC=stub,DC=lab";
    private const string CircleADn = "CN=GG_Circle_A,OU=Lab,DC=stub,DC=lab";
    private const string CircleBDn = "CN=GG_Circle_B,OU=Lab,DC=stub,DC=lab";
    private const string AdaDn = "CN=Ada Lovelace,OU=Lab,DC=stub,DC=lab";
    private const string ExternalDn = "CN=Ext,DC=elsewhere,DC=lab";

    /// <summary>GraphHost placeholder when no renderer factory was supplied.</summary>
    private const string UnavailablePlaceholder = "Graph view is unavailable in this environment.";

    /// <summary>GraphHost placeholder headline when the WebView2 Runtime is missing
    /// (same variant AP 2.1 S7 introduced — see WebView2BannerTests).</summary>
    private const string MissingRuntimeHeadline = "The Microsoft Edge WebView2 Runtime was not found.";

    // --- the load flow (ViewModel level) ---------------------------------------------

    [Fact]
    public async Task Initialization_LoadsScope_BuildsTheGraphOnce_AndShowsItThroughTheRenderer()
    {
        var snapshot = SmallSnapshot();
        var provider = Provider(snapshot);
        var fake = new FakeGraphRenderer();
        var vm = Workspace(provider, () => fake);

        // Must terminate despite the GG_Circle_A ↔ GG_Circle_B membership cycle.
        await vm.Initialization;

        Assert.Equal(1, provider.LoadScopeCalls);
        Assert.Equal(RootDn, provider.LoadScopeBaseDn);
        Assert.Same(fake, vm.GraphRenderer);
        Assert.Same(snapshot, vm.Snapshot);

        // Exactly ONE model reached the renderer, and it is the VM's exposed graph.
        var model = Assert.Single(fake.ShownGraphs);
        Assert.Same(model, vm.Graph);

        // All five drawn nodes: the four in-scope objects plus the materialized
        // External endpoint (GraphBuilder never drops an edge endpoint, ADR-004).
        var nodeDns = model.Nodes.Select(n => n.Dn).ToHashSet(Dn.Comparer);
        Assert.Equal(5, nodeDns.Count);
        Assert.Contains(RootDn, nodeDns);
        Assert.Contains(CircleADn, nodeDns);
        Assert.Contains(CircleBDn, nodeDns);
        Assert.Contains(AdaDn, nodeDns);
        Assert.Contains(ExternalDn, nodeDns);

        // The seeded cycle survives as two distinct membership edges (A→B and B→A).
        Assert.Contains(model.Edges, e =>
            e.Kind == GraphEdgeKind.Membership
            && Dn.Comparer.Equals(e.ParentDn, CircleADn)
            && Dn.Comparer.Equals(e.ChildDn, CircleBDn));
        Assert.Contains(model.Edges, e =>
            e.Kind == GraphEdgeKind.Membership
            && Dn.Comparer.Equals(e.ParentDn, CircleBDn)
            && Dn.Comparer.Equals(e.ChildDn, CircleADn));

        // Summary pins the DRAWN graph: 5 nodes (4 in-scope + 1 External), 7 edges
        // (4 membership + 3 containment) — Graph.Nodes/Graph.Edges counts verbatim.
        Assert.Equal(7, model.Edges.Count);
        Assert.Equal("5 objects, 7 edges", vm.GraphSummary);

        Assert.False(vm.IsLoading);
        Assert.Null(vm.LoadError);
    }

    [AvaloniaFact]
    public async Task PendingLoad_ShowsIndeterminateProgressInTheStatusRow_UntilReleased()
    {
        var tcs = new TaskCompletionSource<DirectorySnapshot>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var provider = Provider();
        provider.LoadScopeResult = tcs.Task;
        var vm = Workspace(provider, () => new FakeGraphRenderer());
        var (window, view) = ShowWorkspace(vm);

        Assert.True(vm.IsLoading, "the load kicked off by the ctor must show as in flight");
        Assert.False(vm.Initialization.IsCompleted);

        // The status row's load indicator: indeterminate (no progress source exists)
        // and visible while the load is pending, IsVisible-bound so it disappears after.
        var progressBar = Assert.Single(view.GetVisualDescendants().OfType<ProgressBar>());
        Assert.True(progressBar.IsIndeterminate);
        Assert.True(progressBar.IsEffectivelyVisible, "the progress bar must show while loading");
        var barTop = progressBar.TranslatePoint(new Point(0, 0), view);
        Assert.NotNull(barTop);
        Assert.True(
            barTop.Value.Y >= view.Bounds.Height - 80,
            $"the progress bar belongs in the bottom status row (was at Y={barTop.Value.Y})");

        tcs.SetResult(SmallSnapshot());
        await vm.Initialization;
        Dispatcher.UIThread.RunJobs();

        Assert.False(vm.IsLoading);
        Assert.False(
            progressBar.IsEffectivelyVisible,
            "the progress bar must hide once the load completed");

        window.Close();
    }

    // --- error policy (ADR-003 D7) -----------------------------------------------------

    [AvaloniaFact]
    public async Task DirectoryUnavailable_SurfacesInline_WithoutDemoHint_AndKeepsThePlaceholder()
    {
        var provider = Provider();
        provider.LoadScopeResult = Task.FromException<DirectorySnapshot>(
            new DirectoryUnavailableException("boom"));
        var vm = Workspace(provider, rendererFactory: null);
        var (window, view) = ShowWorkspace(vm);

        // Handled inline — awaiting Initialization must NOT throw.
        await vm.Initialization;
        Dispatcher.UIThread.RunJobs();

        // Exactly the exception message: the "try Demo mode" hint belongs to the
        // Connect step's live path only — here a connection already succeeded.
        Assert.Equal("boom", vm.LoadError);
        Assert.DoesNotContain("Demo mode", vm.LoadError, StringComparison.Ordinal);
        Assert.False(vm.IsLoading);

        // Visual: the inline error lives at the TOP of the detail column (the right
        // 300px grid column) — red-ish, wrapping. It must never overlay GraphHost
        // (ADR-001 airspace guardrail 5), which the X position proves structurally.
        var errorBlock = Assert.Single(
            view.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == "boom");
        Assert.True(errorBlock.IsEffectivelyVisible);
        Assert.Equal(TextWrapping.Wrap, errorBlock.TextWrapping);
        var brush = Assert.IsAssignableFrom<ISolidColorBrush>(errorBlock.Foreground);
        Assert.True(
            brush.Color.R > brush.Color.G && brush.Color.R > brush.Color.B,
            $"the error foreground must read as red-ish (was {brush.Color})");

        var errorTop = errorBlock.TranslatePoint(new Point(0, 0), view);
        Assert.NotNull(errorTop);
        Assert.True(
            errorTop.Value.X >= view.Bounds.Width - 320,
            $"the error belongs in the right detail column, beside GraphHost (was at X={errorTop.Value.X})");
        Assert.True(
            errorTop.Value.Y <= 150,
            $"the error belongs at the top of the detail column (was at Y={errorTop.Value.Y})");

        // GraphHost still shows its placeholder; the error text never enters it.
        var graphHostTexts = VisibleTexts(Region(view, "GraphHost"));
        Assert.Contains(UnavailablePlaceholder, graphHostTexts);
        Assert.DoesNotContain("boom", graphHostTexts);

        window.Close();
    }

    [Fact]
    public async Task NonDirectoryUnavailableException_PropagatesOutOfInitialization()
    {
        var provider = Provider();
        provider.LoadScopeResult = Task.FromException<DirectorySnapshot>(
            new InvalidOperationException("boom"));
        var vm = Workspace(provider, () => new FakeGraphRenderer());

        // D7: anything but DirectoryUnavailableException is a bug and must stay
        // observable through Initialization, never be swallowed into the error block.
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => vm.Initialization);

        Assert.Equal("boom", ex.Message);
        Assert.Null(vm.LoadError);
        Assert.False(vm.IsLoading, "IsLoading must clear even on a bug-path fault");
    }

    // --- Dispose = cancellation ----------------------------------------------------------

    [Fact]
    public async Task Dispose_DuringPendingLoad_CancelsTheProviderToken_AndInitializationSettlesQuietly()
    {
        var provider = Provider();
        // The stub honors cancellation exactly like a real provider would: the result
        // task completes as cancelled when the observed token fires.
        provider.LoadScopeOverride = ct =>
        {
            var tcs = new TaskCompletionSource<DirectorySnapshot>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            ct.Register(() => tcs.TrySetCanceled(ct));
            return tcs.Task;
        };
        var fake = new FakeGraphRenderer();
        var vm = Workspace(provider, () => fake);

        Assert.True(vm.IsLoading);
        Assert.False(vm.Initialization.IsCompleted);

        vm.Dispose();

        Assert.True(
            provider.LoadScopeToken.IsCancellationRequested,
            "Dispose must cancel the token the in-flight LoadScopeAsync observed");

        // Pin: cancellation never escapes the observable task. A plain await must not
        // throw OperationCanceledException — awaiting a task that completed AS cancelled
        // would, so the VM has to swallow the cancellation internally.
        await vm.Initialization;

        Assert.Null(vm.LoadError);
        Assert.False(vm.IsLoading);
        Assert.Empty(fake.ShownGraphs); // a cancelled load must never show a graph
    }

    // --- renderer readiness ----------------------------------------------------------------

    [Fact]
    public async Task RendererThatNeverCompletes_KeepsIsLoadingTrue_AndInitializationPending()
    {
        var provider = Provider(SmallSnapshot());
        var fake = new FakeGraphRenderer
        {
            // Never completes: the renderer accepted nothing yet.
            ShowGraphResult = new TaskCompletionSource().Task,
        };
        var vm = Workspace(provider, () => fake);

        // Non-completion probe: proving "still pending" needs a bounded wait — this is
        // NOT timing-based synchronization. If the VM wrongly completes Initialization,
        // WhenAny returns it and the assert fails deterministically regardless of
        // timing; if the VM is honest, 500 ms merely bounds how long we watch. No
        // sleep-loop, no flakiness window: a false PASS is impossible.
        await Task.WhenAny(vm.Initialization, Task.Delay(500));

        Assert.False(
            vm.Initialization.IsCompleted,
            "Initialization must not complete before the renderer accepted the graph");
        Assert.True(
            vm.IsLoading,
            "the VM must not lie about readiness while ShowGraphAsync is still pending");
        Assert.Single(fake.ShownGraphs);
    }

    // --- GraphHost: placeholder vs. mounted renderer view (view level) ----------------------

    [AvaloniaFact]
    public async Task NullRendererFactory_KeepsTheUnavailablePlaceholder_AndExposesNoRenderer()
    {
        var vm = Workspace(Provider(SmallSnapshot()), rendererFactory: null);
        var (window, view) = ShowWorkspace(vm);

        await vm.Initialization;
        Dispatcher.UIThread.RunJobs();

        Assert.Null(vm.GraphRenderer);
        Assert.Contains(UnavailablePlaceholder, VisibleTexts(view));
        Assert.Empty(
            view.GetVisualDescendants().OfType<NativeControlHost>()); // no WebView/native HWND

        window.Close();
    }

    [AvaloniaFact]
    public async Task WebView2Missing_NeverInvokesTheFactory_AndShowsTheMissingRuntimeVariant()
    {
        var factoryCalls = 0;
        var vm = Workspace(
            Provider(SmallSnapshot()),
            () =>
            {
                factoryCalls++;
                return new FakeGraphRenderer();
            },
            webView2Missing: true);
        var (window, view) = ShowWorkspace(vm);

        await vm.Initialization;
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(0, factoryCalls); // AP 2.2 re-checks the probe BEFORE building a renderer
        Assert.Null(vm.GraphRenderer);

        var graphHostTexts = VisibleTexts(Region(view, "GraphHost"));
        Assert.Contains(MissingRuntimeHeadline, graphHostTexts);
        Assert.DoesNotContain(UnavailablePlaceholder, graphHostTexts);

        window.Close();
    }

    [AvaloniaFact]
    public async Task RendererView_MountsIntoGraphHost_ReplacingThePlaceholder()
    {
        // FakeGraphRenderer.View is null by design; this variant carries a plain
        // Border so the mount is assertable without any WebView.
        var border = new Border();
        var fake = new FakeGraphRenderer { View = border };
        var vm = Workspace(Provider(SmallSnapshot()), () => fake);
        var (window, view) = ShowWorkspace(vm);

        await vm.Initialization;
        Dispatcher.UIThread.RunJobs();

        Assert.Same(fake, vm.GraphRenderer);
        var graphHost = Region(view, "GraphHost");
        Assert.Same(border, graphHost.Content);
        Assert.Contains(border, graphHost.GetVisualDescendants()); // actually materialized

        var graphHostTexts = VisibleTexts(graphHost);
        Assert.DoesNotContain(UnavailablePlaceholder, graphHostTexts);
        Assert.DoesNotContain(MissingRuntimeHeadline, graphHostTexts);

        window.Close();
    }

    // --- renderer events ----------------------------------------------------------------------

    [AvaloniaFact]
    public async Task NodeClicked_UpdatesSelectedDn_AndTheDetailRegionShowsIt_ExpandOnAUserFocusesOnly()
    {
        // Escaped-comma DN on purpose: DN strings flow verbatim (data-model rule —
        // never canonicalized), so the escape must survive into SelectedDn untouched.
        const string clickedDn = "CN=x\\, y,OU=a,DC=l";

        var fake = new FakeGraphRenderer();
        var vm = Workspace(Provider(SmallSnapshot()), () => fake);
        var (window, view) = ShowWorkspace(vm);
        await vm.Initialization;

        fake.RaiseNodeClicked(clickedDn, "User");
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(clickedDn, vm.SelectedDn);

        // Placeholder binding — THE AP 2.5 seam: the detail region shows the DN text.
        var detailRegion = Region(view, "DetailPanelRegion");
        Assert.Contains(
            detailRegion.GetVisualDescendants().OfType<TextBlock>()
                .Where(t => t.IsEffectivelyVisible),
            t => t.Text?.Contains(clickedDn, StringComparison.Ordinal) == true);

        // AP 2.3 deliberately changed this pin (was: expand ignored, ADR-004 D5): a
        // dbltap on a USER node is not fetchable (ADR-005 D3) — pure focus, no provider
        // call (the stub would throw loudly), no SetMembers, no rebuild, no selection.
        fake.RaiseNodeExpandRequested(AdaDn, "User");
        Dispatcher.UIThread.RunJobs();
        await vm.Expansion;

        Assert.Equal(clickedDn, vm.SelectedDn);
        Assert.Null(vm.LoadError);
        Assert.False(vm.IsLoading);
        Assert.Single(fake.ShownGraphs);
        Assert.Empty(fake.UpdatedGraphs);
        var focusedDn = Assert.Single(Assert.Single(fake.FocusCalls));
        Assert.Equal(AdaDn, focusedDn, Dn.Comparer);

        window.Close();
    }

    [Fact]
    public async Task RendererError_ReusesLoadError_AndLeavesIsLoadingAlone()
    {
        var fake = new FakeGraphRenderer();
        var vm = Workspace(Provider(SmallSnapshot()), () => fake);
        await vm.Initialization;
        Assert.False(vm.IsLoading);
        Assert.Null(vm.LoadError);

        fake.RaiseRendererError("graph.js", "cytoscape exploded");

        // Pinned decision: renderer failures reuse the ONE inline error surface
        // (LoadError) instead of growing a parallel RendererError property.
        Assert.NotNull(vm.LoadError);
        Assert.Contains("cytoscape exploded", vm.LoadError, StringComparison.Ordinal);
        Assert.False(vm.IsLoading, "a renderer error report must not touch IsLoading");
    }

    // --- helpers -------------------------------------------------------------------------------

    private static AdObject Obj(
        string name, string dn, AdObjectKind kind = AdObjectKind.GlobalGroup) =>
        new() { Dn = dn, Kind = kind, Name = name };

    /// <summary>
    /// The S6 fixture scope: root OU + the circular pair GG_Circle_A ↔ GG_Circle_B
    /// (mirrors the seeded lab cycle — traversal must terminate) + one user + one
    /// out-of-scope member DN that GraphBuilder materializes as an External node.
    /// Drawn graph: 5 nodes, 7 edges (4 membership + 3 containment).
    /// </summary>
    private static DirectorySnapshot SmallSnapshot()
    {
        var snapshot = new DirectorySnapshot();
        snapshot.AddObject(Obj("Lab", RootDn, AdObjectKind.OrganizationalUnit));
        snapshot.AddObject(Obj("GG_Circle_A", CircleADn));
        snapshot.AddObject(Obj("GG_Circle_B", CircleBDn));
        snapshot.AddObject(Obj("Ada Lovelace", AdaDn, AdObjectKind.User));
        snapshot.SetMembers(CircleADn, [CircleBDn, AdaDn, ExternalDn]);
        snapshot.SetMembers(CircleBDn, [CircleADn]); // closes the A→B→A cycle
        return snapshot;
    }

    /// <summary>Stub whose scope load yields <paramref name="snapshot"/> (default: empty).</summary>
    private static StubDirectoryProvider Provider(DirectorySnapshot? snapshot = null) =>
        new(Task.FromResult(new DirectoryConnection("stub directory", 5)))
        {
            LoadScopeResult = Task.FromResult(snapshot ?? new DirectorySnapshot()),
        };

    /// <summary>Workspace VM rooted at <see cref="RootDn"/> with the S6 ctor shape.</summary>
    private static WorkspaceViewModel Workspace(
        StubDirectoryProvider provider,
        Func<IGraphRenderer>? rendererFactory,
        bool webView2Missing = false) =>
        new(
            provider,
            Obj("Lab", RootDn, AdObjectKind.OrganizationalUnit),
            new DirectoryConnection("stub directory", 5),
            webView2Missing,
            rendererFactory);

    /// <summary>Workspace view in a sized, shown headless window (bindings live).</summary>
    private static (Window Window, WorkspaceView View) ShowWorkspace(WorkspaceViewModel vm)
    {
        var view = new WorkspaceView { DataContext = vm };
        var window = new Window { Content = view, Width = 1280, Height = 720 };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return (window, view);
    }

    /// <summary>One of the two named seam regions (GraphHost / DetailPanelRegion).</summary>
    private static ContentControl Region(WorkspaceView view, string name) =>
        Assert.Single(
            view.GetVisualDescendants().OfType<ContentControl>(), c => c.Name == name);

    private static List<string?> VisibleTexts(Visual scope) =>
        scope.GetVisualDescendants()
            .OfType<TextBlock>()
            .Where(t => t.IsEffectivelyVisible)
            .Select(t => t.Text)
            .ToList();
}
