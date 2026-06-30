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
/// AP 2.3 S4 adds the Refresh button's view pins (ADR-005 D4) — its command matrix
/// lives in <c>WorkspaceExpandTests</c>.
/// </summary>
public sealed class WorkspaceLoadTests
{
    private const string RootDn = "OU=Lab,DC=stub,DC=lab";
    private const string CircleADn = "CN=GG_Circle_A,OU=Lab,DC=stub,DC=lab";
    private const string CircleBDn = "CN=GG_Circle_B,OU=Lab,DC=stub,DC=lab";
    private const string AdaDn = "CN=Ada Lovelace,OU=Lab,DC=stub,DC=lab";
    private const string ExternalDn = "CN=Ext,DC=elsewhere,DC=lab";

    /// <summary>GraphHost placeholder when no renderer factory was supplied.</summary>
    private const string UnavailablePlaceholder = "The graph preview will appear here.";

    /// <summary>ADR-022 D3 re-baseline: the rail moved <c>*,300</c> ⇒ <c>*, Auto, {rail}</c> with
    /// a 340px default rail and a 14px <c>Auto</c> seam (GridSplitter + ◂/▸ chevron) BESIDE
    /// GraphHost. GraphHost (col 0) therefore ends 354px (340 rail + 14 seam) from the right
    /// edge — that boundary is the airspace line (ADR-001 guardrail 5): every right-column
    /// affordance (error block, Refresh/Reload buttons) must sit at or right of it, never over
    /// the graph. Derived from the production layout, not a magic number; an affordance that
    /// ever strayed over GraphHost would render left of it and fail.</summary>
    private const double RailLeftEdgeFromRight = 340 + 14;

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

    [Fact]
    public async Task RulesetName_ReflectsTheCtorRuleset_AndUpdatesWithNotificationOnApply()
    {
        var provider = Provider(SmallSnapshot());
        var fake = new FakeGraphRenderer();
        var vm = Workspace(provider, () => fake);
        await vm.Initialization;

        // #186: the no-selection scope-summary card reads the ACTIVE ruleset's name. At
        // construction that is the default ruleset (the VM falls back to it when handed none) —
        // pinned against the loader, never a hardcoded literal.
        Assert.Equal(GroupWeaver.Core.Rules.RulesetLoader.LoadDefault().Name, vm.RulesetName);

        var changed = new List<string?>();
        vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        // Apply a renamed ruleset: ApplyRulesetAsync raises RulesetName BEFORE its
        // snapshot/IsLoading/renderer guard, so the property + notification fire even renderer-less.
        var renamed = GroupWeaver.Core.Rules.RulesetLoader.LoadDefault() with { Name = "custom-test-ruleset" };
        await vm.ApplyRulesetAsync(renamed);

        Assert.Equal("custom-test-ruleset", vm.RulesetName);
        Assert.Contains(nameof(WorkspaceViewModel.RulesetName), changed);
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

        // AP 3.4 S4 (ADR-010 §5) demoted the Refresh/LoadError/DetailPanelRegion stack
        // to row 2 of the right column's 2*,Auto,3* split — below the violations
        // sidebar (row 0). The error stays in the right column, above the
        // DetailPanelRegion seam, never over GraphHost; it no longer tops the whole
        // column (the sidebar does). Pin the surviving invariants, not the old Y<=150.
        var errorTop = errorBlock.TranslatePoint(new Point(0, 0), view);
        var detailRegionTop = Region(view, "DetailPanelRegion").TranslatePoint(new Point(0, 0), view);
        Assert.NotNull(errorTop);
        Assert.NotNull(detailRegionTop);
        // ADR-022 D3: rail is now 340px + a 14px Auto seam, so the right column sits right of
        // GraphHost's (Width-354) edge — beside the graph, never over it (ADR-001 #5).
        Assert.True(
            errorTop.Value.X >= view.Bounds.Width - RailLeftEdgeFromRight,
            $"the error belongs in the right detail column, beside GraphHost (was at X={errorTop.Value.X})");
        Assert.True(
            errorTop.Value.Y <= detailRegionTop.Value.Y + 0.5,
            $"the error belongs above the DetailPanelRegion seam (was at Y={errorTop.Value.Y})");

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

        // AP 2.5 changed this pin DELIBERATELY (was: the raw-DN placeholder binding):
        // the DetailPanelView carries the clicked DN VERBATIM as its own text element.
        var detailRegion = Region(view, "DetailPanelRegion");
        Assert.Contains(
            detailRegion.GetVisualDescendants().OfType<TextBlock>()
                .Where(t => t.IsEffectivelyVisible),
            t => string.Equals(t.Text, clickedDn, StringComparison.Ordinal));

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

    // --- the Refresh button (AP 2.3 S4, ADR-005 D4) ---------------------------------------

    [AvaloniaFact]
    public async Task RefreshButton_TopsTheDetailColumn_DisabledUntilAGroupIsSelected_WithTooltip()
    {
        var fake = new FakeGraphRenderer();
        var vm = Workspace(Provider(SmallSnapshot()), () => fake);
        var (window, view) = ShowWorkspace(vm);
        await vm.Initialization;
        Dispatcher.UIThread.RunJobs();

        // The named header button (a seam-name pin like GraphHost/DetailPanelRegion),
        // labelled "Refresh node" — shipped UI strings are English (ADR-005 D4). Slice A
        // renamed the content "Refresh" → "Refresh node" (the tooltip is unchanged).
        var refresh = Assert.Single(
            view.GetVisualDescendants().OfType<Button>(), b => b.Name == "RefreshButton");
        Assert.True(refresh.IsEffectivelyVisible);
        Assert.Contains("Refresh node", VisibleTexts(refresh));

        // Native chrome at the TOP of the right detail column: inside the column,
        // ABOVE the AP 2.5 DetailPanelRegion seam (a future detail panel must not be
        // able to evict it), and never over GraphHost (ADR-001 airspace guardrail 5).
        var detailRegion = Region(view, "DetailPanelRegion");
        Assert.DoesNotContain(refresh, Region(view, "GraphHost").GetVisualDescendants());
        Assert.DoesNotContain(refresh, detailRegion.GetVisualDescendants());

        var buttonTop = refresh.TranslatePoint(new Point(0, 0), view);
        var regionTop = detailRegion.TranslatePoint(new Point(0, 0), view);
        Assert.NotNull(buttonTop);
        Assert.NotNull(regionTop);
        // ADR-022 D3: rail 340px + 14px Auto seam ⇒ the column sits right of GraphHost (Width-354).
        Assert.True(
            buttonTop.Value.X >= view.Bounds.Width - RailLeftEdgeFromRight,
            $"the Refresh button belongs in the right detail column (was at X={buttonTop.Value.X})");
        // AP 3.4 S4 (ADR-010 §5) demoted this stack to row 2 of the right column's
        // 2*,Auto,3* split — below the violations sidebar — so the Refresh button no
        // longer tops the whole column. The surviving invariant is that it stays ABOVE
        // the DetailPanelRegion seam (a future detail panel must not evict it).
        Assert.True(
            buttonTop.Value.Y + refresh.Bounds.Height <= regionTop.Value.Y + 0.5,
            "the Refresh button belongs ABOVE the DetailPanelRegion seam");

        // Tooltip: pinned present and non-empty; the wording is the implementer's.
        var tip = Assert.IsType<string>(ToolTip.GetTip(refresh));
        Assert.False(string.IsNullOrWhiteSpace(tip), "the Refresh tooltip must say something");

        // Disabled without a selection (RefreshCommand.CanExecute drives the button) …
        Assert.Null(vm.SelectedDn);
        Assert.False(
            refresh.IsEffectivelyEnabled,
            "without a selection there is nothing to refresh");

        // … and armed by a group click arriving over the renderer seam — even though
        // GG_Circle_A is already members-loaded (refresh exists FOR loaded nodes, D4).
        fake.RaiseNodeClicked(CircleADn, "GlobalGroup");
        Dispatcher.UIThread.RunJobs();
        Assert.True(
            refresh.IsEffectivelyEnabled,
            "a selected (even already-loaded) group must enable Refresh");

        window.Close();
    }

    // --- the Reload-scope button view pin (issue #30 S2, discharges ADR-005 D4 follow-up) -

    [AvaloniaFact]
    public async Task ReloadScopeButton_SitsBesideGraphHost_LeftOfRefresh_ArmedWithNoSelection_WithTooltip()
    {
        var fake = new FakeGraphRenderer();
        var vm = Workspace(Provider(SmallSnapshot()), () => fake);
        var (window, view) = ShowWorkspace(vm);
        await vm.Initialization;
        Dispatcher.UIThread.RunJobs();

        // The named header button (a seam-name pin like RefreshButton/GraphHost),
        // labelled "Reload scope" — shipped UI strings are English (ADR-005 D4).
        var reload = Assert.Single(
            view.GetVisualDescendants().OfType<Button>(), b => b.Name == "ReloadScopeButton");
        Assert.True(reload.IsEffectivelyVisible);
        Assert.Contains("Reload scope", VisibleTexts(reload));

        // Bound to ReloadScopeCommand — the SAME command instance the VM exposes, not
        // some other RelayCommand. (Refresh and Reload must not be cross-wired.)
        Assert.Same(vm.ReloadScopeCommand, reload.Command);

        // Native chrome BESIDE GraphHost, never inside/over it (ADR-001 airspace
        // guardrail 5): it lives in the right detail column, ABOVE the AP 2.5
        // DetailPanelRegion seam (a future detail panel must not be able to evict it).
        var detailRegion = Region(view, "DetailPanelRegion");
        Assert.DoesNotContain(reload, Region(view, "GraphHost").GetVisualDescendants());
        Assert.DoesNotContain(reload, detailRegion.GetVisualDescendants());

        var reloadTop = reload.TranslatePoint(new Point(0, 0), view);
        var regionTop = detailRegion.TranslatePoint(new Point(0, 0), view);
        Assert.NotNull(reloadTop);
        Assert.NotNull(regionTop);
        // ADR-022 D3: rail 340px + 14px Auto seam ⇒ the column sits right of GraphHost (Width-354).
        Assert.True(
            reloadTop.Value.X >= view.Bounds.Width - RailLeftEdgeFromRight,
            $"the Reload scope button belongs in the right detail column, beside GraphHost (was at X={reloadTop.Value.X})");
        Assert.True(
            reloadTop.Value.Y + reload.Bounds.Height <= regionTop.Value.Y + 0.5,
            "the Reload scope button belongs ABOVE the DetailPanelRegion seam");

        // Order in the shared header WrapPanel: Reload scope PRECEDES Refresh (the surviving
        // header invariant — Reload then Refresh, never the reverse). The action row is a
        // right-aligned WrapPanel that reflows at the rail minimum (WorkspaceView D5), so the
        // adjacency is reading-order (row-major: top row first, then left-to-right within a
        // row), NOT a same-row raw-X comparison. Slice A widened "Refresh" → "Refresh node",
        // which can push Refresh onto the next wrap row on the 340px rail — Reload still
        // PRECEDES it in document order, which is what this pins.
        var refresh = Assert.Single(
            view.GetVisualDescendants().OfType<Button>(), b => b.Name == "RefreshButton");
        var reloadTopLeft = reload.TranslatePoint(new Point(0, 0), view);
        var refreshTopLeft = refresh.TranslatePoint(new Point(0, 0), view);
        Assert.NotNull(reloadTopLeft);
        Assert.NotNull(refreshTopLeft);
        // Reading-order precedence: a row break (Reload on an earlier/higher row) OR, on the
        // SAME row, Reload's right edge at/left of Refresh's left edge with no overlap.
        const double rowEpsilon = 1.0;
        var sameRow = Math.Abs(reloadTopLeft.Value.Y - refreshTopLeft.Value.Y) <= rowEpsilon;
        var reloadPrecedes = sameRow
            ? reloadTopLeft.Value.X + reload.Bounds.Width <= refreshTopLeft.Value.X + 0.5
            : reloadTopLeft.Value.Y < refreshTopLeft.Value.Y;
        Assert.True(
            reloadPrecedes,
            $"Reload scope must PRECEDE Refresh in the header reflow order (Reload at "
            + $"{reloadTopLeft.Value}, right edge {reloadTopLeft.Value.X + reload.Bounds.Width}; "
            + $"Refresh at {refreshTopLeft.Value})");

        // Tooltip: pinned present and non-empty; the wording is the implementer's.
        var tip = Assert.IsType<string>(ToolTip.GetTip(reload));
        Assert.False(string.IsNullOrWhiteSpace(tip), "the Reload scope tooltip must say something");

        // KEYSTONE for the button: CanExecute is selection-INDEPENDENT. Unlike Refresh
        // (disabled above with no selection), Reload reloads the WHOLE scope, so it is
        // ARMED with nothing selected the instant the busy gate releases — and the
        // rendered button reflects that (IsEffectivelyEnabled, not just CanExecute).
        Assert.Null(vm.SelectedDn);
        Assert.False(vm.IsLoading);
        Assert.True(
            reload.IsEffectivelyEnabled,
            "Reload scope must be enabled with NO selection — it reloads the whole scope, not a node");

        // A selection (or lack thereof) never changes that: selecting a non-fetchable
        // User disables Refresh but leaves Reload armed.
        fake.RaiseNodeClicked(AdaDn, "User");
        Dispatcher.UIThread.RunJobs();
        Assert.False(refresh.IsEffectivelyEnabled, "anchor: a User selection disarms Refresh");
        Assert.True(
            reload.IsEffectivelyEnabled,
            "Reload scope stays armed regardless of the current selection");

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

    // --- the Reload-scope command (issue #30 S1, discharges ADR-005 D4 follow-up) ---------

    [Fact]
    public async Task ReloadScope_ReRunsLoadScopeAsync_ASecondTime_AndShowsTheFreshGraph_NeverUpdates()
    {
        var provider = Provider(SmallSnapshot());
        var fake = new FakeGraphRenderer();
        var vm = Workspace(provider, () => fake);
        await vm.Initialization;

        // The ctor load is the first LoadScopeAsync; the rebuilt graph rode ShowGraphAsync.
        Assert.Equal(1, provider.LoadScopeCalls);
        Assert.Single(fake.ShownGraphs);
        Assert.Empty(fake.UpdatedGraphs);

        // A fresh whole-scope load: LoadScopeAsync runs a SECOND time, from the SAME root.
        await ReloadAndSettle(vm);

        Assert.Equal(2, provider.LoadScopeCalls);
        Assert.Equal(RootDn, provider.LoadScopeBaseDn);

        // KEYSTONE: reload is replace-all — it lands in ShownGraphs (destroy+fit), NEVER
        // UpdatedGraphs (the in-place verb Refresh/expand use). This is the single
        // assertion that distinguishes whole-scope reload from a node Refresh.
        Assert.Equal(2, fake.ShownGraphs.Count);
        Assert.Empty(fake.UpdatedGraphs);

        // The most recent shown model is the VM's current graph (rebuilt from scratch).
        Assert.Same(fake.ShownGraphs[^1], vm.Graph);
        Assert.Equal("5 objects, 7 edges", vm.GraphSummary);
        Assert.False(vm.IsLoading);
        Assert.Null(vm.LoadError);
    }

    [Fact]
    public async Task ReloadScope_ClearsTheSelection_AndDropsTheDetailPanel()
    {
        var provider = Provider(SmallSnapshot());
        var fake = new FakeGraphRenderer();
        var vm = Workspace(provider, () => fake);
        await vm.Initialization;

        // Select an in-scope object first: the detail panel projects it (snapshot-only).
        fake.RaiseNodeClicked(CircleADn, "GlobalGroup");
        Assert.Equal(CircleADn, vm.SelectedDn);
        Assert.NotNull(vm.DetailPanel);

        await ReloadAndSettle(vm);

        // Reload resets the selection up front (the selected DN may not survive a fresh
        // scope load): SelectedDn -> null, and the panel re-projects to null with it.
        Assert.Null(vm.SelectedDn);
        Assert.Null(vm.DetailPanel);
    }

    [Fact]
    public async Task ReloadScope_ClearsAStaleLoadError_UpFront()
    {
        var provider = Provider(SmallSnapshot());
        var fake = new FakeGraphRenderer();
        var vm = Workspace(provider, () => fake);
        await vm.Initialization;

        // A renderer error has populated the ONE inline error surface.
        fake.RaiseRendererError("graph.js", "cytoscape exploded");
        Assert.NotNull(vm.LoadError);

        await ReloadAndSettle(vm);

        // Every new whole-scope attempt clears the inline error (load policy, like LoadAsync).
        Assert.Null(vm.LoadError);
    }

    [Fact]
    public async Task ReloadScope_RecomputesTheGraphSummary_FromTheFreshlyLoadedScope()
    {
        // The reload returns a DIFFERENT scope than the ctor load: the summary (drawn
        // counts) must track the freshly loaded snapshot, not the original.
        var provider = Provider(SmallSnapshot());
        var fake = new FakeGraphRenderer();
        var vm = Workspace(provider, () => fake);
        await vm.Initialization;
        Assert.Equal("5 objects, 7 edges", vm.GraphSummary);

        // A smaller scope next time: just the root OU plus one well-named group, no cycle,
        // no External endpoint => 2 nodes, 1 containment edge.
        var smaller = new DirectorySnapshot();
        smaller.AddObject(Obj("Lab", RootDn, AdObjectKind.OrganizationalUnit));
        smaller.AddObject(Obj("GG_Sales_Staff", "CN=GG_Sales_Staff,OU=Lab,DC=stub,DC=lab"));
        provider.LoadScopeResult = Task.FromResult(smaller);

        await ReloadAndSettle(vm);

        Assert.Equal("2 objects, 1 edges", vm.GraphSummary);
        Assert.Same(vm.Snapshot, smaller);
    }

    [Fact]
    public async Task ReloadScope_OverANewScope_DropsTheOrphanExMemberNode_NoPruningCode()
    {
        // The crux of issue #30: a fresh LoadScopeAsync rebuilds the snapshot from
        // scratch, so an object present only in the FIRST scope vanishes by construction
        // — no DirectorySnapshot.RemoveObject, no graph-layer prune.
        var provider = Provider(SmallSnapshot()); // contains Ada Lovelace
        var fake = new FakeGraphRenderer();
        var vm = Workspace(provider, () => fake);
        await vm.Initialization;
        Assert.Contains(vm.Graph!.Nodes, n => Dn.Comparer.Equals(n.Dn, AdaDn));

        // The directory no longer returns Ada at all on the next whole-scope load.
        var withoutAda = new DirectorySnapshot();
        withoutAda.AddObject(Obj("Lab", RootDn, AdObjectKind.OrganizationalUnit));
        withoutAda.AddObject(Obj("GG_Circle_A", CircleADn));
        withoutAda.AddObject(Obj("GG_Circle_B", CircleBDn));
        withoutAda.SetMembers(CircleADn, [CircleBDn]);
        withoutAda.SetMembers(CircleBDn, [CircleADn]); // the cycle still closes — must terminate
        provider.LoadScopeResult = Task.FromResult(withoutAda);

        await ReloadAndSettle(vm);

        // Ada is gone from the rebuilt graph entirely — no orphan ex-member node lingers.
        Assert.DoesNotContain(vm.Graph!.Nodes, n => Dn.Comparer.Equals(n.Dn, AdaDn));
        Assert.Same(vm.Snapshot, withoutAda);
    }

    // --- helpers -------------------------------------------------------------------------------

    /// <summary>Invokes <c>ReloadScopeCommand</c> and awaits the scope-load pipeline it
    /// kicks off. <c>ReloadScopeCommand</c> is an async RelayCommand, so its
    /// <c>ExecuteAsync</c> task is the observable handle to await before asserting.</summary>
    private static async Task ReloadAndSettle(WorkspaceViewModel vm)
    {
        await vm.ReloadScopeCommand.ExecuteAsync(null);
    }

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

    /// <summary>Workspace VM rooted at <see cref="RootDn"/> with the S6 ctor shape. Fresh
    /// temp-dir UiStateStore (#124 / ADR-022 D4): never touches the real %APPDATA%
    /// ui-state.json, so a persisted RailCollapsed:true cannot collapse the right rail and
    /// starve the view realization this file asserts over.</summary>
    private static WorkspaceViewModel Workspace(
        StubDirectoryProvider provider,
        Func<IGraphRenderer>? rendererFactory,
        bool webView2Missing = false) =>
        new(
            provider,
            Obj("Lab", RootDn, AdObjectKind.OrganizationalUnit),
            new DirectoryConnection("stub directory", 5),
            webView2Missing,
            rendererFactory,
            uiStateStore: new GroupWeaver.App.Settings.UiStateStore(
                System.IO.Directory.CreateTempSubdirectory("groupweaver-wsload-uistate-").FullName));

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
