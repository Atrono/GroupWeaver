using GroupWeaver.App.Graph;
using GroupWeaver.App.Tests.Fakes;
using GroupWeaver.App.ViewModels;
using GroupWeaver.Core.Graph;
using GroupWeaver.Core.Model;
using GroupWeaver.Core.Providers;
using GroupWeaver.Providers;

using Xunit;

namespace GroupWeaver.App.Tests;

/// <summary>
/// Pins the AP 2.3 S3 lazy-expand pipeline of <see cref="WorkspaceViewModel"/>
/// (ADR-005 D3, refining the ADR-004 D5 seam): a <c>NodeExpandRequested</c> gesture
/// fetches iff the SNAPSHOT kind is GG/DL/UG/External and the node is not yet
/// members-loaded — everything else (loaded groups, users, computers, OUs) gets a pure
/// <c>FocusAsync(node + cached members)</c> with ZERO provider traffic and NEVER a
/// fabricated <c>SetMembers</c> (the null-vs-empty load-state contract that AP 3.2/3.4
/// depend on). The fetch path is transactional — GetObjectAsync (only for DNs missing
/// from <c>Snapshot.Objects</c>: resolves External frontier nodes to their true kind),
/// GetMembersAsync, THEN AddObject(s) + SetMembers — followed by a GraphBuilder rebuild,
/// exactly one replace-in-place <c>UpdateGraphAsync</c> (never a second ShowGraphAsync),
/// a recomputed <see cref="WorkspaceViewModel.GraphSummary"/>, and
/// <c>FocusAsync(parent + members)</c>. One global busy gate (IsLoading reused);
/// overlapping gestures are dropped, never queued. Errors mirror the load policy:
/// <see cref="DirectoryUnavailableException"/> → <see cref="WorkspaceViewModel.LoadError"/>
/// (cleared at the start of each new attempt), cancellation quiet, everything else
/// propagates via the observable <see cref="WorkspaceViewModel.Expansion"/> task
/// (Initialization pattern — never fire-and-forget). The GG_Circle_A ↔ GG_Circle_B
/// cycle mirrors the seeded lab fixture: every path over it must terminate.
/// AP 2.3 S4 (section k) adds the Refresh button's command (ADR-005 D4): a FORCED
/// expand of <see cref="WorkspaceViewModel.SelectedDn"/> — armed iff a fetchable
/// snapshot kind is selected and the busy gate is idle, cache bypassed even when
/// IsLoaded, <c>SetMembers</c> REPLACES (refresh semantics, data-model rule).
/// Review pins (AP 2.3 pre-merge): section (m) — the ONE busy gate spans the WHOLE
/// pipeline INCLUDING the focus-only branch, so overlap during an in-flight focus is
/// dropped exactly like overlap during a fetch; section (n) — a renderer-less
/// (headless, null factory) workspace keeps Refresh disarmed, and a direct Execute is
/// a silent no-op (no NRE, no provider traffic, no snapshot mutation).
/// VM-only behavior — plain facts, no visual tree.
/// </summary>
public sealed class WorkspaceExpandTests
{
    private const string RootDn = "OU=Lab,DC=stub,DC=lab";
    private const string CircleADn = "CN=GG_Circle_A,OU=Lab,DC=stub,DC=lab";
    private const string CircleBDn = "CN=GG_Circle_B,OU=Lab,DC=stub,DC=lab";
    private const string AdaDn = "CN=Ada Lovelace,OU=Lab,DC=stub,DC=lab";
    private const string PcDn = "CN=PC-01,OU=Lab,DC=stub,DC=lab";
    private const string SalesDn = "CN=GG_Sales,OU=Lab,DC=stub,DC=lab";
    private const string VertriebDn = "CN=GG_Vertrieb,OU=Lab,DC=stub,DC=lab";
    private const string OpsDn = "CN=GG_Ops,OU=Lab,DC=stub,DC=lab";
    private const string DlDn = "CN=DL_App_RO,OU=Lab,DC=stub,DC=lab";
    private const string UgDn = "CN=UG_All,OU=Lab,DC=stub,DC=lab";
    private const string ExternalDn = "CN=Ext,DC=elsewhere,DC=lab";
    private const string RemoteDn = "CN=Remote,DC=elsewhere,DC=lab";

    // --- (a) cache hit: focus only, zero provider traffic ------------------------------

    [Fact]
    public async Task ExpandOnLoadedGroup_IsACacheHit_FocusesNodePlusCachedMembers_ZeroProviderCalls()
    {
        var snapshot = CircleScope();
        var provider = Provider(snapshot);
        var fake = new FakeGraphRenderer();
        var vm = Workspace(provider, () => fake);
        await vm.Initialization;
        var graphBefore = vm.Graph;
        var summaryBefore = vm.GraphSummary;

        // Gate the camera move: even the cache-hit path must stay observable through
        // Expansion (never fire-and-forget) and await the renderer's `focused` reply.
        var focusGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fake.FocusResult = focusGate.Task;

        fake.RaiseNodeExpandRequested(CircleADn, "GlobalGroup");

        Assert.False(
            vm.Expansion.IsCompleted,
            "the in-flight focus must be observable through Expansion");
        focusGate.SetResult();
        await vm.Expansion;

        // ZERO provider traffic — GG_Circle_A's members were cached by the scope load.
        Assert.Equal(0, provider.GetObjectCalls);
        Assert.Equal(0, provider.GetMembersCalls);

        // Exactly one focus — the node plus its cached members — and nothing rebuilt,
        // re-shown, or re-summarized.
        AssertFocusSet(Assert.Single(fake.FocusCalls), CircleADn, CircleBDn, AdaDn, ExternalDn);
        Assert.Empty(fake.UpdatedGraphs);
        Assert.Single(fake.ShownGraphs);
        Assert.Same(graphBefore, vm.Graph);
        Assert.Equal(summaryBefore, vm.GraphSummary);
        Assert.Null(vm.LoadError);
        Assert.False(vm.IsLoading);
    }

    // --- (b) non-group kinds: focus only, never a fabricated SetMembers ----------------

    [Theory]
    [InlineData(RootDn, "OrganizationalUnit")]
    [InlineData(AdaDn, "User")]
    [InlineData(PcDn, "Computer")]
    public async Task ExpandOnNonGroupKind_FocusesOnly_AndNeverFabricatesLoadState(
        string dn, string kind)
    {
        var snapshot = CircleScope();
        var provider = Provider(snapshot);
        var fake = new FakeGraphRenderer();
        var vm = Workspace(provider, () => fake);
        await vm.Initialization;
        var graphBefore = vm.Graph;

        fake.RaiseNodeExpandRequested(dn, kind);
        await vm.Expansion;

        Assert.Equal(0, provider.GetObjectCalls);
        Assert.Equal(0, provider.GetMembersCalls);

        // null-vs-empty contract (data-model rule, AP 3.2/3.4): a non-group dbltap must
        // never call SetMembers — IsLoaded stays false, GetMembers stays null (NOT []).
        Assert.False(snapshot.IsLoaded(dn));
        Assert.Null(snapshot.GetMembers(dn));

        AssertFocusSet(Assert.Single(fake.FocusCalls), dn);
        Assert.Empty(fake.UpdatedGraphs);
        Assert.Single(fake.ShownGraphs);
        Assert.Same(graphBefore, vm.Graph);
        Assert.Null(vm.LoadError);
    }

    // --- (c) fetch path: one level, applied transactionally, replace-in-place ----------

    [Fact]
    public async Task ExpandOnUnloadedGroup_FetchesOneLevel_AppliesIt_AndUpdatesInPlace()
    {
        var snapshot = GroupScope(SalesDn);
        var provider = Provider(snapshot);
        var fake = new FakeGraphRenderer();
        var vm = Workspace(provider, () => fake);
        await vm.Initialization;
        var graphBefore = vm.Graph;

        provider.GetMembersHandler = (_, _) => Task.FromResult<IReadOnlyList<AdObject>>(
        [
            Obj("Ada Lovelace", AdaDn, AdObjectKind.User),
            Obj("GG_Ops", OpsDn),
        ]);

        fake.RaiseNodeExpandRequested(SalesDn, "GlobalGroup");
        await vm.Expansion;

        // The parent DN is already in Snapshot.Objects → NO GetObjectAsync round-trip.
        Assert.Equal(0, provider.GetObjectCalls);
        Assert.Equal(SalesDn, Assert.Single(provider.GetMembersDns), Dn.Comparer);

        // The fetch landed: member objects upserted, parent marked loaded with exactly
        // the fetched member DNs (in fetch order).
        Assert.True(snapshot.TryGetObject(AdaDn, out var ada));
        Assert.Equal(AdObjectKind.User, ada!.Kind);
        Assert.True(snapshot.TryGetObject(OpsDn, out var ops));
        Assert.Equal(AdObjectKind.GlobalGroup, ops!.Kind);
        Assert.True(snapshot.IsLoaded(SalesDn));
        Assert.Equal(new[] { AdaDn, OpsDn }, snapshot.GetMembers(SalesDn));

        // Rebuild + replace-in-place: exactly ONE UpdateGraphAsync carrying the new
        // vm.Graph — and NEVER a second ShowGraphAsync (destroy+fit loses the viewport).
        Assert.NotSame(graphBefore, vm.Graph);
        var updated = Assert.Single(fake.UpdatedGraphs);
        Assert.Same(updated, vm.Graph);
        Assert.Single(fake.ShownGraphs);

        var nodeDns = updated.Nodes.Select(n => n.Dn).ToHashSet(Dn.Comparer);
        Assert.Equal(4, nodeDns.Count); // root, Sales, Ada, Ops
        Assert.Contains(AdaDn, nodeDns);
        Assert.Contains(OpsDn, nodeDns);
        Assert.Contains(updated.Edges, e =>
            e.Kind == GraphEdgeKind.Membership
            && Dn.Comparer.Equals(e.ParentDn, SalesDn)
            && Dn.Comparer.Equals(e.ChildDn, AdaDn));
        Assert.Contains(updated.Edges, e =>
            e.Kind == GraphEdgeKind.Membership
            && Dn.Comparer.Equals(e.ParentDn, SalesDn)
            && Dn.Comparer.Equals(e.ChildDn, OpsDn));

        // 4 nodes; 2 membership + 3 containment edges — drawn counts, post-update.
        Assert.Equal("4 objects, 5 edges", vm.GraphSummary);

        AssertFocusSet(Assert.Single(fake.FocusCalls), SalesDn, AdaDn, OpsDn);
        Assert.Null(vm.LoadError);
        Assert.False(vm.IsLoading);
    }

    // --- (d) empty fetch result: loaded-and-empty, never null --------------------------

    [Fact]
    public async Task ExpandFetch_WithNoMembers_MarksLoadedAndEmpty_NeverNull()
    {
        var snapshot = GroupScope(SalesDn);
        var provider = Provider(snapshot);
        var fake = new FakeGraphRenderer();
        var vm = Workspace(provider, () => fake);
        await vm.Initialization;
        var graphBefore = vm.Graph;

        provider.GetMembersHandler = (_, _) =>
            Task.FromResult<IReadOnlyList<AdObject>>([]);

        fake.RaiseNodeExpandRequested(SalesDn, "GlobalGroup");
        await vm.Expansion;

        // Loaded and genuinely empty — the empty-group check (AP 3.2) reads exactly this.
        Assert.True(snapshot.IsLoaded(SalesDn));
        var members = snapshot.GetMembers(SalesDn);
        Assert.NotNull(members);
        Assert.Empty(members);

        // The fetch path still rebuilds and updates (D3 order is unconditional).
        Assert.NotSame(graphBefore, vm.Graph);
        var updated = Assert.Single(fake.UpdatedGraphs);
        Assert.Same(updated, vm.Graph);
        Assert.Equal(
            $"{updated.Nodes.Count} objects, {updated.Edges.Count} edges", vm.GraphSummary);
        AssertFocusSet(Assert.Single(fake.FocusCalls), SalesDn);
    }

    // --- (e) External frontier node: resolved to its true kind via GetObjectAsync ------

    [Fact]
    public async Task ExpandOnExternalFrontierNode_ResolvesItsTrueKind_ThroughGetObjectUpsert()
    {
        var snapshot = CircleScope(); // ExternalDn is a member edge endpoint, NOT in Objects
        var provider = Provider(snapshot);
        var fake = new FakeGraphRenderer();
        var vm = Workspace(provider, () => fake);
        await vm.Initialization;

        provider.GetObjectHandler = (_, _) => Task.FromResult<AdObject?>(
            Obj("Ext", ExternalDn, AdObjectKind.DomainLocalGroup));
        provider.GetMembersHandler = (_, _) => Task.FromResult<IReadOnlyList<AdObject>>(
            [Obj("Remote", RemoteDn, AdObjectKind.User)]);

        fake.RaiseNodeExpandRequested(ExternalDn, "External");
        await vm.Expansion;

        // DN missing from Snapshot.Objects → exactly one GetObjectAsync, then members.
        Assert.Equal(ExternalDn, Assert.Single(provider.GetObjectDns), Dn.Comparer);
        Assert.Equal(ExternalDn, Assert.Single(provider.GetMembersDns), Dn.Comparer);

        // The upsert resolved the frontier node to its TRUE kind (ADR-005 D5: expansion
        // is visible as kind resolution — dashed External becomes a DL group).
        Assert.True(snapshot.TryGetObject(ExternalDn, out var resolved));
        Assert.Equal(AdObjectKind.DomainLocalGroup, resolved!.Kind);
        Assert.True(snapshot.IsLoaded(ExternalDn));

        var updated = Assert.Single(fake.UpdatedGraphs);
        var extNode = Assert.Single(updated.Nodes, n => Dn.Comparer.Equals(n.Dn, ExternalDn));
        Assert.Equal(AdObjectKind.DomainLocalGroup, extNode.Kind);
        Assert.Contains(updated.Nodes, n =>
            Dn.Comparer.Equals(n.Dn, RemoteDn) && n.Kind == AdObjectKind.User);
        Assert.Contains(updated.Edges, e =>
            e.Kind == GraphEdgeKind.Membership
            && Dn.Comparer.Equals(e.ParentDn, ExternalDn)
            && Dn.Comparer.Equals(e.ChildDn, RemoteDn));
        Assert.Equal(
            $"{updated.Nodes.Count} objects, {updated.Edges.Count} edges", vm.GraphSummary);

        AssertFocusSet(Assert.Single(fake.FocusCalls), ExternalDn, RemoteDn);
        Assert.Null(vm.LoadError);
    }

    // --- (f) DirectoryUnavailable: inline, transactional, cleared on the next attempt --

    [Fact]
    public async Task ProviderFailureMidFetch_LeavesSnapshotUntouched_SurfacesInline_NoRendererUpdate()
    {
        var snapshot = CircleScope();
        var provider = Provider(snapshot);
        var fake = new FakeGraphRenderer();
        var vm = Workspace(provider, () => fake);
        await vm.Initialization;
        var graphBefore = vm.Graph;
        var summaryBefore = vm.GraphSummary;

        // GetObjectAsync succeeds, the LATER GetMembersAsync fails: nothing may have
        // been applied yet (transactional order — mutations only after ALL provider
        // calls came back, ADR-005 D3).
        provider.GetObjectHandler = (_, _) => Task.FromResult<AdObject?>(
            Obj("Ext", ExternalDn, AdObjectKind.DomainLocalGroup));
        provider.GetMembersHandler = (_, _) => Task.FromException<IReadOnlyList<AdObject>>(
            new DirectoryUnavailableException("expand boom"));

        fake.RaiseNodeExpandRequested(ExternalDn, "External");
        await vm.Expansion; // handled inline — must NOT throw

        Assert.Equal("expand boom", vm.LoadError);
        Assert.Equal(1, provider.GetObjectCalls);

        // Snapshot untouched: the already-resolved object must NOT have been upserted.
        Assert.False(snapshot.TryGetObject(ExternalDn, out _));
        Assert.False(snapshot.IsLoaded(ExternalDn));

        // No renderer update, no focus, graph and summary unchanged.
        Assert.Empty(fake.UpdatedGraphs);
        Assert.Empty(fake.FocusCalls);
        Assert.Same(graphBefore, vm.Graph);
        Assert.Equal(summaryBefore, vm.GraphSummary);
        Assert.False(vm.IsLoading);
    }

    [Fact]
    public async Task LoadErrorFromAFailedExpand_IsClearedAtTheStartOfTheNextAttempt()
    {
        var snapshot = GroupScope(SalesDn);
        var provider = Provider(snapshot);
        var fake = new FakeGraphRenderer();
        var vm = Workspace(provider, () => fake);
        await vm.Initialization;

        provider.GetMembersHandler = (_, _) => Task.FromException<IReadOnlyList<AdObject>>(
            new DirectoryUnavailableException("expand boom"));
        fake.RaiseNodeExpandRequested(SalesDn, "GlobalGroup");
        await vm.Expansion;
        Assert.Equal("expand boom", vm.LoadError);

        // Next attempt: gate the fetch so we can observe the error being cleared at
        // the START of the attempt, not only after it succeeded.
        var fetchGate = new TaskCompletionSource<IReadOnlyList<AdObject>>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        provider.GetMembersHandler = (_, _) => fetchGate.Task;
        fake.RaiseNodeExpandRequested(SalesDn, "GlobalGroup");

        Assert.Null(vm.LoadError); // cleared while the fetch is still in flight

        fetchGate.SetResult([]);
        await vm.Expansion;

        Assert.Null(vm.LoadError);
        Assert.True(snapshot.IsLoaded(SalesDn));
    }

    // --- (g) the global busy gate: overlapping gestures are dropped, never queued ------

    [Fact]
    public async Task ExpandGesture_WhileTheInitialScopeLoadIsInFlight_IsSilentlyDropped()
    {
        var loadGate = new TaskCompletionSource<DirectorySnapshot>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var provider = Provider(new DirectorySnapshot());
        provider.LoadScopeResult = loadGate.Task;
        var fake = new FakeGraphRenderer();
        var vm = Workspace(provider, () => fake);
        var initialExpansion = vm.Expansion;
        Assert.True(
            initialExpansion.IsCompletedSuccessfully,
            "before any gesture, awaiting Expansion must be safe and immediate");
        Assert.True(vm.IsLoading);

        fake.RaiseNodeExpandRequested(CircleADn, "GlobalGroup");

        // Dropped means NOTHING: no provider call, no focus — and Expansion untouched
        // (replacing it would lie to anyone observing the pipeline).
        Assert.Same(initialExpansion, vm.Expansion);
        Assert.Equal(0, provider.GetObjectCalls);
        Assert.Equal(0, provider.GetMembersCalls);
        Assert.Empty(fake.FocusCalls);

        loadGate.SetResult(CircleScope());
        await vm.Initialization;

        // The dropped gesture was NOT queued; a fresh one after completion is honored.
        Assert.Empty(fake.FocusCalls);
        fake.RaiseNodeExpandRequested(CircleADn, "GlobalGroup");
        await vm.Expansion;
        AssertFocusSet(Assert.Single(fake.FocusCalls), CircleADn, CircleBDn, AdaDn, ExternalDn);
    }

    [Fact]
    public async Task ExpandGesture_WhileAnotherExpandIsInFlight_IsDropped_NotQueued()
    {
        var snapshot = GroupScope(SalesDn, VertriebDn);
        var provider = Provider(snapshot);
        var fake = new FakeGraphRenderer();
        var vm = Workspace(provider, () => fake);
        await vm.Initialization;

        var fetchGate = new TaskCompletionSource<IReadOnlyList<AdObject>>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        provider.GetMembersHandler = (_, _) => fetchGate.Task;

        fake.RaiseNodeExpandRequested(SalesDn, "GlobalGroup");
        var inFlight = vm.Expansion;
        Assert.False(inFlight.IsCompleted);
        Assert.True(
            vm.IsLoading,
            "the fetch path reuses the ONE global busy gate — IsLoading (ADR-005 D3)");
        Assert.Equal(1, provider.GetMembersCalls);

        fake.RaiseNodeExpandRequested(VertriebDn, "GlobalGroup");

        Assert.Same(inFlight, vm.Expansion);
        Assert.Equal(1, provider.GetMembersCalls); // dropped, not queued

        fetchGate.SetResult([]);
        await inFlight;

        Assert.False(vm.IsLoading);
        Assert.Equal(1, provider.GetMembersCalls);
        Assert.True(snapshot.IsLoaded(SalesDn));
        Assert.False(
            snapshot.IsLoaded(VertriebDn),
            "a dropped gesture must never replay after the in-flight expand completes");

        // Sequential second expand after completion works.
        provider.GetMembersHandler = (_, _) => Task.FromResult<IReadOnlyList<AdObject>>([]);
        fake.RaiseNodeExpandRequested(VertriebDn, "GlobalGroup");
        await vm.Expansion;
        Assert.Equal(2, provider.GetMembersCalls);
        Assert.True(snapshot.IsLoaded(VertriebDn));
    }

    // --- (h) the seeded cycle: expanding both sides terminates, both edges drawn -------

    [Fact]
    public async Task ExpandingBothSidesOfAMembershipCycle_Terminates_AndDrawsBothEdges()
    {
        var snapshot = GroupScope(CircleADn, CircleBDn);
        var provider = Provider(snapshot);
        var fake = new FakeGraphRenderer();
        var vm = Workspace(provider, () => fake);
        await vm.Initialization;

        provider.GetMembersHandler = (dn, _) => Task.FromResult<IReadOnlyList<AdObject>>(
            Dn.Comparer.Equals(dn, CircleADn)
                ? [Obj("GG_Circle_B", CircleBDn)]
                : [Obj("GG_Circle_A", CircleADn)]);

        // Mirrors the seeded lab cycle GG_Circle_A ↔ GG_Circle_B — THE permanent guard:
        // both expansions must terminate (one level per gesture, no traversal).
        fake.RaiseNodeExpandRequested(CircleADn, "GlobalGroup");
        await vm.Expansion;
        fake.RaiseNodeExpandRequested(CircleBDn, "GlobalGroup");
        await vm.Expansion;

        Assert.Equal(2, provider.GetMembersCalls); // exactly ONE level per gesture
        Assert.True(snapshot.IsLoaded(CircleADn));
        Assert.True(snapshot.IsLoaded(CircleBDn));

        // Both membership edges of the cycle live in the rebuilt drawn model.
        Assert.Equal(2, fake.UpdatedGraphs.Count);
        var model = vm.Graph;
        Assert.NotNull(model);
        Assert.Contains(model.Edges, e =>
            e.Kind == GraphEdgeKind.Membership
            && Dn.Comparer.Equals(e.ParentDn, CircleADn)
            && Dn.Comparer.Equals(e.ChildDn, CircleBDn));
        Assert.Contains(model.Edges, e =>
            e.Kind == GraphEdgeKind.Membership
            && Dn.Comparer.Equals(e.ParentDn, CircleBDn)
            && Dn.Comparer.Equals(e.ChildDn, CircleADn));

        // A third gesture on the now-loaded cycle node is a pure cache hit.
        fake.RaiseNodeExpandRequested(CircleADn, "GlobalGroup");
        await vm.Expansion;
        Assert.Equal(2, provider.GetMembersCalls);
        Assert.Equal(2, fake.UpdatedGraphs.Count);
    }

    // --- (i) Expansion: the observable pipeline task -----------------------------------

    [Fact]
    public async Task Expansion_IsCompletedAtConstruction_AndBecomesTheInFlightPipelinePerGesture()
    {
        var snapshot = GroupScope(SalesDn);
        var provider = Provider(snapshot);
        var fake = new FakeGraphRenderer();
        var vm = Workspace(provider, () => fake);

        // Initialization pattern: the property exists from the first instant and is
        // safe to await before any gesture ever happened.
        Assert.True(vm.Expansion.IsCompletedSuccessfully);
        await vm.Initialization;
        var beforeGesture = vm.Expansion;
        Assert.True(beforeGesture.IsCompletedSuccessfully);

        var fetchGate = new TaskCompletionSource<IReadOnlyList<AdObject>>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        provider.GetMembersHandler = (_, _) => fetchGate.Task;
        fake.RaiseNodeExpandRequested(SalesDn, "GlobalGroup");

        // The gesture handed its pipeline task to Expansion — never fire-and-forget.
        Assert.NotSame(beforeGesture, vm.Expansion);
        Assert.False(vm.Expansion.IsCompleted);

        fetchGate.SetResult([]);
        await vm.Expansion;
        Assert.True(vm.Expansion.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task NonDirectoryUnavailableException_PropagatesOutOfExpansion_NotIntoLoadError()
    {
        var snapshot = GroupScope(SalesDn);
        var provider = Provider(snapshot);
        var fake = new FakeGraphRenderer();
        var vm = Workspace(provider, () => fake);
        await vm.Initialization;

        provider.GetMembersHandler = (_, _) => Task.FromException<IReadOnlyList<AdObject>>(
            new InvalidOperationException("expand bug"));

        fake.RaiseNodeExpandRequested(SalesDn, "GlobalGroup");

        // Error policy (mirrors Initialization): anything but DirectoryUnavailable is
        // a bug and must stay observable through Expansion, never the inline block.
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => vm.Expansion);

        Assert.Equal("expand bug", ex.Message);
        Assert.Null(vm.LoadError);
        Assert.False(vm.IsLoading, "IsLoading must clear even on a bug-path fault");
        Assert.False(snapshot.IsLoaded(SalesDn));
        Assert.Empty(fake.UpdatedGraphs);
    }

    // --- (j) Dispose mid-expand: cancels and settles quietly ---------------------------

    [Fact]
    public async Task DisposeMidExpand_CancelsTheObservedToken_AndExpansionSettlesQuietly()
    {
        var snapshot = GroupScope(SalesDn);
        var provider = Provider(snapshot);
        var fake = new FakeGraphRenderer();
        var vm = Workspace(provider, () => fake);
        await vm.Initialization;

        // Honors cancellation exactly like a real provider: the result task completes
        // as cancelled when the observed token fires.
        provider.GetMembersHandler = (_, ct) =>
        {
            var tcs = new TaskCompletionSource<IReadOnlyList<AdObject>>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            ct.Register(() => tcs.TrySetCanceled(ct));
            return tcs.Task;
        };

        fake.RaiseNodeExpandRequested(SalesDn, "GlobalGroup");
        Assert.False(vm.Expansion.IsCompleted);

        vm.Dispose();

        Assert.True(
            provider.GetMembersToken.IsCancellationRequested,
            "Dispose must cancel the token the in-flight GetMembersAsync observed");

        // Cancellation never escapes the observable task (same pin as Initialization):
        // a plain await must not throw OperationCanceledException.
        await vm.Expansion;

        Assert.Null(vm.LoadError);
        Assert.False(vm.IsLoading);
        Assert.False(snapshot.IsLoaded(SalesDn)); // a cancelled fetch must never mutate
        Assert.Empty(fake.UpdatedGraphs);
        Assert.Empty(fake.FocusCalls);
    }

    // --- (k) Refresh = forced expand of SelectedDn (ADR-005 D4) ------------------------

    [Theory]
    [InlineData(null, false)] // no selection — nothing to refresh
    [InlineData(CircleADn, true)] // GlobalGroup, already LOADED — refresh exists FOR loaded nodes
    [InlineData(DlDn, true)] // DomainLocalGroup
    [InlineData(UgDn, true)] // UniversalGroup
    [InlineData(ExternalDn, true)] // frontier DN not in Objects: snapshot kind External
    [InlineData(RootDn, false)] // OrganizationalUnit — not fetchable
    [InlineData(AdaDn, false)] // User — not fetchable
    [InlineData(PcDn, false)] // Computer — not fetchable
    public async Task RefreshCanExecute_RequiresAFetchableSnapshotKindSelection_WhileIdle(
        string? selectedDn, bool expected)
    {
        var snapshot = KindScope();
        var provider = Provider(snapshot);
        var fake = new FakeGraphRenderer();
        var vm = Workspace(provider, () => fake);
        await vm.Initialization;

        // The decision reads the SNAPSHOT kind off SelectedDn (ADR-005 D3/D4) — no
        // renderer kind string exists on this path at all.
        vm.SelectedDn = selectedDn;

        Assert.Equal(expected, vm.RefreshCommand.CanExecute(null));
        Assert.Equal(0, provider.GetObjectCalls); // arming must never touch the provider
        Assert.Equal(0, provider.GetMembersCalls);
    }

    [Fact]
    public async Task RefreshCanExecute_IsFalseWhileBusy_AndCanExecuteChangedTracksIsLoading()
    {
        var loadGate = new TaskCompletionSource<DirectorySnapshot>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var provider = Provider(new DirectorySnapshot());
        provider.LoadScopeResult = loadGate.Task;
        var fake = new FakeGraphRenderer();
        var vm = Workspace(provider, () => fake);

        // Even a fetchable selection cannot arm Refresh while the initial scope load
        // holds the ONE global busy gate (IsLoading, ADR-005 D3).
        vm.SelectedDn = CircleADn;
        Assert.True(vm.IsLoading);
        Assert.False(vm.RefreshCommand.CanExecute(null));

        loadGate.SetResult(CircleScope());
        await vm.Initialization;
        Assert.True(vm.RefreshCommand.CanExecute(null));

        var raised = 0;
        vm.RefreshCommand.CanExecuteChanged += (_, _) => raised++;

        // Gate a forced fetch: Refresh on the LOADED GG_Circle_A must hit the provider
        // (cache bypassed, D4) and flip the busy gate while in flight.
        var fetchGate = new TaskCompletionSource<IReadOnlyList<AdObject>>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        provider.GetMembersHandler = (_, _) => fetchGate.Task;
        vm.RefreshCommand.Execute(null);

        Assert.Equal(1, provider.GetMembersCalls);
        Assert.True(vm.IsLoading);
        Assert.False(
            vm.Expansion.IsCompleted,
            "the refresh pipeline must be observable through Expansion — never fire-and-forget");
        Assert.False(vm.RefreshCommand.CanExecute(null));
        Assert.True(raised >= 1, "CanExecuteChanged must fire when IsLoading turns on");

        var raisedWhileBusy = raised;
        fetchGate.SetResult([]);
        await vm.Expansion;

        Assert.False(vm.IsLoading);
        Assert.True(vm.RefreshCommand.CanExecute(null));
        Assert.True(raised > raisedWhileBusy, "CanExecuteChanged must fire when IsLoading turns off");
    }

    [Fact]
    public async Task RefreshCanExecuteChanged_FiresOnSelectionChanges_ViaTheNodeClickPath()
    {
        var snapshot = CircleScope();
        var provider = Provider(snapshot);
        var fake = new FakeGraphRenderer();
        var vm = Workspace(provider, () => fake);
        await vm.Initialization;
        Assert.False(vm.RefreshCommand.CanExecute(null)); // nothing selected yet

        var raised = 0;
        vm.RefreshCommand.CanExecuteChanged += (_, _) => raised++;

        fake.RaiseNodeClicked(CircleADn, "GlobalGroup");
        Assert.Equal(CircleADn, vm.SelectedDn);
        Assert.True(raised >= 1, "selecting a node must re-arm the command");
        Assert.True(vm.RefreshCommand.CanExecute(null));

        var afterGroupClick = raised;
        fake.RaiseNodeClicked(AdaDn, "User");
        Assert.True(raised > afterGroupClick, "changing the selection must re-evaluate");
        Assert.False(vm.RefreshCommand.CanExecute(null)); // a user is not fetchable
    }

    [Fact]
    public async Task Refresh_ForcesTheFetch_SetMembersReplaces_EdgeGoneButExMemberNodeStaysDrawn()
    {
        var snapshot = CircleScope(); // GG_Circle_A LOADED with [B, Ada, Ext]
        var provider = Provider(snapshot);
        var fake = new FakeGraphRenderer();
        var vm = Workspace(provider, () => fake);
        await vm.Initialization;
        var graphBefore = vm.Graph;

        // Between the scope load and this refresh, Ada and the external member left
        // the group: the directory now answers with GG_Circle_B alone.
        provider.GetMembersHandler = (_, _) => Task.FromResult<IReadOnlyList<AdObject>>(
            [Obj("GG_Circle_B", CircleBDn)]);

        fake.RaiseNodeClicked(CircleADn, "GlobalGroup");
        vm.RefreshCommand.Execute(null);
        await vm.Expansion;

        // FORCED fetch (D4): IsLoaded was true, the cache is bypassed anyway — and the
        // DN is already in Snapshot.Objects, so no GetObjectAsync round-trip.
        Assert.Equal(CircleADn, Assert.Single(provider.GetMembersDns), Dn.Comparer);
        Assert.Equal(0, provider.GetObjectCalls);

        // SetMembers REPLACES (refresh semantics, data-model rule): exactly the fresh
        // member list — Ada and the external DN are gone, never merged.
        Assert.Equal(new[] { CircleBDn }, snapshot.GetMembers(CircleADn));

        // Replace-in-place rebuild: ONE UpdateGraphAsync, never a second ShowGraphAsync.
        Assert.NotSame(graphBefore, vm.Graph);
        var updated = Assert.Single(fake.UpdatedGraphs);
        Assert.Same(updated, vm.Graph);
        Assert.Single(fake.ShownGraphs);

        // D4 pinned: the stale membership edge A→Ada vanished from the rebuilt model …
        Assert.DoesNotContain(updated.Edges, e =>
            e.Kind == GraphEdgeKind.Membership
            && Dn.Comparer.Equals(e.ParentDn, CircleADn)
            && Dn.Comparer.Equals(e.ChildDn, AdaDn));

        // … but the ex-member NODE is still drawn: the snapshot never removes objects
        // and GraphBuilder is total over Snapshot.Objects — in-scope Ada keeps her
        // (still-true) containment edge under the root.
        Assert.Contains(updated.Nodes, n =>
            Dn.Comparer.Equals(n.Dn, AdaDn) && n.Kind == AdObjectKind.User);
        Assert.Contains(updated.Edges, e =>
            e.Kind == GraphEdgeKind.Containment
            && Dn.Comparer.Equals(e.ChildDn, AdaDn));

        // The cycle's surviving membership edges are intact in both directions.
        Assert.Contains(updated.Edges, e =>
            e.Kind == GraphEdgeKind.Membership
            && Dn.Comparer.Equals(e.ParentDn, CircleADn)
            && Dn.Comparer.Equals(e.ChildDn, CircleBDn));
        Assert.Contains(updated.Edges, e =>
            e.Kind == GraphEdgeKind.Membership
            && Dn.Comparer.Equals(e.ParentDn, CircleBDn)
            && Dn.Comparer.Equals(e.ChildDn, CircleADn));

        // The ex-member EXTERNAL endpoint was never a snapshot object — External nodes
        // exist only as materialized edge endpoints (ADR-004 D1), so it vanishes whole.
        Assert.DoesNotContain(updated.Nodes, n => Dn.Comparer.Equals(n.Dn, ExternalDn));

        // 5 nodes (root, A, B, Ada, PC-01); 2 membership + 4 containment edges.
        Assert.Equal(5, updated.Nodes.Count);
        Assert.Equal(6, updated.Edges.Count);
        Assert.Equal("5 objects, 6 edges", vm.GraphSummary);

        AssertFocusSet(Assert.Single(fake.FocusCalls), CircleADn, CircleBDn);
        Assert.Null(vm.LoadError);
        Assert.False(vm.IsLoading);
    }

    [Fact]
    public async Task RefreshExecute_WhileAnotherFetchIsInFlight_IsDropped_NotQueued()
    {
        var snapshot = GroupScope(SalesDn, VertriebDn);
        var provider = Provider(snapshot);
        var fake = new FakeGraphRenderer();
        var vm = Workspace(provider, () => fake);
        await vm.Initialization;

        var fetchGate = new TaskCompletionSource<IReadOnlyList<AdObject>>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        provider.GetMembersHandler = (_, _) => fetchGate.Task;

        fake.RaiseNodeExpandRequested(SalesDn, "GlobalGroup");
        var inFlight = vm.Expansion;
        Assert.Equal(1, provider.GetMembersCalls);

        // The disabled button blocks this in the UI; a stale-armed Execute must still
        // be dropped, never queued (ADR-005 D3) — and Expansion stays untouched.
        vm.SelectedDn = VertriebDn;
        vm.RefreshCommand.Execute(null);

        Assert.Same(inFlight, vm.Expansion);
        Assert.Equal(1, provider.GetMembersCalls);

        fetchGate.SetResult([]);
        await inFlight;

        Assert.Equal(1, provider.GetMembersCalls);
        Assert.False(
            snapshot.IsLoaded(VertriebDn),
            "a dropped refresh must never replay after the in-flight fetch completes");
    }

    // --- (l) end-to-end over the REAL DemoProvider (AP 2.3 S5) -------------------------

    [Fact]
    public async Task ExpandOverTheRealDemoProvider_GroupRootedScope_GrowsTheGraph_AndResolvesTheFrontierKind()
    {
        // The embedded demo dataset, no stubs: a GROUP-rooted scope contains only
        // the group itself (a group has no DN children), so BOTH its members —
        // GG_Sales_Staff and the AGDLP-violation direct user u001 — lie outside the
        // loaded scope and surface as External frontier nodes after the scope load.
        const string dlDn = "CN=DL_FS-Sales_RW,OU=Groups,OU=AGDLP-Demo,DC=weavedemo,DC=example";
        const string ggDn = "CN=GG_Sales_Staff,OU=Groups,OU=AGDLP-Demo,DC=weavedemo,DC=example";
        const string u001Dn = "CN=Anna Acker (u001),OU=Users,OU=AGDLP-Demo,DC=weavedemo,DC=example";

        var provider = new DemoProvider();
        var root = await provider.GetObjectAsync(dlDn);
        Assert.NotNull(root);
        Assert.Equal(AdObjectKind.DomainLocalGroup, root.Kind);
        var fake = new FakeGraphRenderer();
        var vm = new WorkspaceViewModel(
            provider, root, await provider.ConnectAsync(), webView2Missing: false, () => fake);
        await vm.Initialization;

        var snapshot = vm.Snapshot;
        Assert.NotNull(snapshot);
        var dl = Assert.Single(snapshot.Objects);
        Assert.True(Dn.Comparer.Equals(dlDn, dl.Dn), $"unexpected object in scope: '{dl.Dn}'");
        Assert.False(snapshot.TryGetObject(ggDn, out _));
        Assert.Equal(AdObjectKind.External, snapshot.GetKind(ggDn));
        Assert.False(snapshot.IsLoaded(ggDn));

        var before = vm.Graph;
        Assert.NotNull(before);
        Assert.Equal(3, before.Nodes.Count); // DL root + 2 materialized frontier nodes
        var frontier = Assert.Single(before.Nodes, n => Dn.Comparer.Equals(n.Dn, ggDn));
        Assert.Equal(AdObjectKind.External, frontier.Kind);

        // Expand the GG frontier member — the full pipeline against the REAL provider.
        fake.RaiseNodeExpandRequested(ggDn, "External");
        await vm.Expansion;

        // The frontier resolved from External to its TRUE kind (ADR-005 D5) and is
        // now members-loaded with the dataset's 20 sales-staff users.
        Assert.True(snapshot.TryGetObject(ggDn, out var resolved));
        Assert.Equal(AdObjectKind.GlobalGroup, resolved!.Kind);
        Assert.True(snapshot.IsLoaded(ggDn));
        Assert.Equal(20, snapshot.GetMembers(ggDn)!.Count);

        // The graph GREW, replace-in-place: 22 nodes (DL + GG + 20 users — u001 is
        // one of the 20, so its own frontier node resolved to a real User too),
        // 22 membership + 21 containment edges.
        var updated = Assert.Single(fake.UpdatedGraphs);
        Assert.Same(updated, vm.Graph);
        Assert.Single(fake.ShownGraphs);
        Assert.True(updated.Nodes.Count > before.Nodes.Count, "the expand must grow the graph");
        Assert.Equal(22, updated.Nodes.Count);
        var ggNode = Assert.Single(updated.Nodes, n => Dn.Comparer.Equals(n.Dn, ggDn));
        Assert.Equal(AdObjectKind.GlobalGroup, ggNode.Kind);
        var annaNode = Assert.Single(updated.Nodes, n => Dn.Comparer.Equals(n.Dn, u001Dn));
        Assert.Equal(AdObjectKind.User, annaNode.Kind);
        Assert.Contains(updated.Edges, e =>
            e.Kind == GraphEdgeKind.Membership
            && Dn.Comparer.Equals(e.ParentDn, ggDn)
            && Dn.Comparer.Equals(e.ChildDn, u001Dn));
        Assert.Equal("22 objects, 43 edges", vm.GraphSummary);

        Assert.Null(vm.LoadError);
        Assert.False(vm.IsLoading);
    }

    // --- (m) review pins: the busy gate spans the WHOLE pipeline, focus-only included --

    [Fact]
    public async Task FocusOnlyExpand_HoldsTheOneGlobalBusyGate_WhileTheFocusIsInFlight()
    {
        var snapshot = CircleScope();
        var provider = Provider(snapshot);
        var fake = new FakeGraphRenderer();
        var vm = Workspace(provider, () => fake);
        await vm.Initialization;

        // Anchor: with the gate idle, a fetchable selection arms Refresh — proving the
        // False below is caused by the gate, not by the selection.
        vm.SelectedDn = CircleBDn;
        Assert.True(vm.RefreshCommand.CanExecute(null));

        var focusGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fake.FocusResult = focusGate.Task;

        fake.RaiseNodeExpandRequested(CircleADn, "GlobalGroup"); // cache hit -> focus path

        // ADR-005 D3: ONE global busy gate across the WHOLE expand pipeline — the
        // focus-only (cache-hit) branch INCLUDED. While the camera move is in flight
        // the gate is held: IsLoading true, Refresh disarmed.
        Assert.False(vm.Expansion.IsCompleted);
        Assert.True(
            vm.IsLoading,
            "the focus-only branch must hold the ONE global busy gate (ADR-005 D3)");
        Assert.False(vm.RefreshCommand.CanExecute(null));

        focusGate.SetResult();
        await vm.Expansion;

        Assert.False(vm.IsLoading);
        Assert.True(vm.RefreshCommand.CanExecute(null), "the gate must release after the focus");
        AssertFocusSet(Assert.Single(fake.FocusCalls), CircleADn, CircleBDn, AdaDn, ExternalDn);
        Assert.Equal(0, provider.GetObjectCalls);
        Assert.Equal(0, provider.GetMembersCalls);
        Assert.Null(vm.LoadError);
    }

    [Fact]
    public async Task ExpandGesture_WhileAFocusOnlyExpandIsInFlight_IsDropped_NotQueued()
    {
        var snapshot = KindScope(); // GG_Circle_A LOADED; DL_App_RO fetchable, NOT loaded
        var provider = Provider(snapshot);
        var fake = new FakeGraphRenderer();
        var vm = Workspace(provider, () => fake);
        await vm.Initialization;
        provider.GetMembersHandler = (_, _) => Task.FromResult<IReadOnlyList<AdObject>>([]);

        var focusGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fake.FocusResult = focusGate.Task;

        fake.RaiseNodeExpandRequested(CircleADn, "GlobalGroup"); // cache hit -> focus path
        var inFlight = vm.Expansion;
        Assert.False(inFlight.IsCompleted);
        Assert.Single(fake.FocusCalls);

        // Mirror of section (g) for the FOCUS path: a second gesture during the
        // in-flight camera move must be dropped exactly like one during a fetch —
        // zero provider traffic, zero additional focus, Expansion reference-unchanged.
        fake.RaiseNodeExpandRequested(DlDn, "DomainLocalGroup");

        Assert.Same(inFlight, vm.Expansion);
        Assert.Equal(0, provider.GetObjectCalls);
        Assert.Equal(0, provider.GetMembersCalls);
        Assert.Single(fake.FocusCalls);
        Assert.Empty(fake.UpdatedGraphs);
        Assert.False(snapshot.IsLoaded(DlDn));

        focusGate.SetResult();
        await inFlight;
        Assert.False(vm.IsLoading);

        // The dropped gesture was NOT queued — and a fresh one afterwards is honored.
        Assert.Equal(0, provider.GetMembersCalls);
        Assert.False(snapshot.IsLoaded(DlDn));
        fake.RaiseNodeExpandRequested(DlDn, "DomainLocalGroup");
        await vm.Expansion;
        Assert.Equal(1, provider.GetMembersCalls);
        Assert.True(snapshot.IsLoaded(DlDn));
        Assert.Equal(2, fake.FocusCalls.Count);
        AssertFocusSet(fake.FocusCalls[^1], DlDn);
    }

    [Fact]
    public async Task RefreshExecute_WhileAFocusOnlyExpandIsInFlight_IsDropped_NotQueued()
    {
        var snapshot = CircleScope(); // GG_Circle_A and GG_Circle_B both LOADED
        var provider = Provider(snapshot);
        var fake = new FakeGraphRenderer();
        var vm = Workspace(provider, () => fake);
        await vm.Initialization;
        var graphBefore = vm.Graph;
        var summaryBefore = vm.GraphSummary;
        provider.GetMembersHandler = (_, _) => Task.FromResult<IReadOnlyList<AdObject>>(
            [Obj("Ada Lovelace", AdaDn, AdObjectKind.User)]);

        var focusGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fake.FocusResult = focusGate.Task;

        fake.RaiseNodeExpandRequested(CircleADn, "GlobalGroup"); // cache hit -> focus path
        var inFlight = vm.Expansion;
        Assert.False(inFlight.IsCompleted);

        // RelayCommand.Execute does NOT gate on CanExecute: a stale-armed Execute during
        // the in-flight FOCUS must be dropped exactly like one during a fetch (section k)
        // — no snapshot mutation, no graph update, no provider traffic, Expansion untouched.
        vm.SelectedDn = CircleBDn;
        vm.RefreshCommand.Execute(null);

        Assert.Same(inFlight, vm.Expansion);
        Assert.Equal(0, provider.GetObjectCalls);
        Assert.Equal(0, provider.GetMembersCalls);
        Assert.Equal(new[] { CircleADn }, snapshot.GetMembers(CircleBDn));
        Assert.Empty(fake.UpdatedGraphs);
        Assert.Single(fake.FocusCalls);
        Assert.Same(graphBefore, vm.Graph);
        Assert.Equal(summaryBefore, vm.GraphSummary);

        focusGate.SetResult();
        await inFlight;
        Assert.False(vm.IsLoading);

        // Dropped, not queued: nothing replays after the focus completes …
        Assert.Equal(0, provider.GetMembersCalls);
        Assert.Equal(new[] { CircleADn }, snapshot.GetMembers(CircleBDn));

        // … but dropped ≠ disarmed: the SAME refresh, re-issued sequentially, fetches.
        Assert.True(vm.RefreshCommand.CanExecute(null));
        vm.RefreshCommand.Execute(null);
        await vm.Expansion;
        Assert.Equal(1, provider.GetMembersCalls);
        Assert.Equal(new[] { AdaDn }, snapshot.GetMembers(CircleBDn)); // SetMembers REPLACES
    }

    // --- (n) review pins: a renderer-less workspace keeps Refresh disarmed and inert ---

    [Fact]
    public async Task RefreshCanExecute_IsFalseWithoutARenderer_EvenWithAFetchableSelection()
    {
        var snapshot = CircleScope();
        var provider = Provider(snapshot);
        var vm = RendererlessWorkspace(provider);
        await vm.Initialization;
        Assert.Null(vm.GraphRenderer);

        // SelectedDn has a public setter (the declared AP 2.5 detail-panel seam) —
        // selection can exist without a renderer, but Refresh must never arm: there is
        // no surface to update, and the pipeline would dereference a null renderer.
        vm.SelectedDn = CircleADn;

        Assert.False(
            vm.RefreshCommand.CanExecute(null),
            "Refresh must stay disarmed when no renderer exists");
        Assert.Equal(0, provider.GetObjectCalls); // arming must never touch the provider
        Assert.Equal(0, provider.GetMembersCalls);
    }

    [Fact]
    public async Task RefreshExecute_WithoutARenderer_IsASilentNoOp_NoFetch_NoMutation_NoCrash()
    {
        var snapshot = CircleScope();
        var provider = Provider(snapshot);
        var vm = RendererlessWorkspace(provider);
        await vm.Initialization;
        var expansionBefore = vm.Expansion;
        var graphBefore = vm.Graph;
        // If the pipeline ever reached the provider, this handler would let it run all
        // the way to the null-renderer dereference — the no-op must stop FAR earlier.
        provider.GetMembersHandler = (_, _) => Task.FromResult<IReadOnlyList<AdObject>>(
            [Obj("GG_Circle_B", CircleBDn)]);

        vm.SelectedDn = CircleADn;
        vm.RefreshCommand.Execute(null);

        // Silent no-op (drop semantics): awaiting Expansion must not throw — today this
        // surfaces the null-renderer NRE — and NOTHING may have happened.
        await vm.Expansion;

        Assert.Same(expansionBefore, vm.Expansion);
        Assert.Equal(0, provider.GetObjectCalls);
        Assert.Equal(0, provider.GetMembersCalls);
        Assert.Equal(new[] { CircleBDn, AdaDn, ExternalDn }, snapshot.GetMembers(CircleADn));
        Assert.True(snapshot.IsLoaded(CircleADn));
        Assert.Same(graphBefore, vm.Graph);
        Assert.Null(vm.LoadError);
        Assert.False(vm.IsLoading);
        // NodeExpandRequested is unreachable without a renderer (no event source exists),
        // so the Refresh pin is the complete null-renderer surface (review finding 2).
    }

    // --- (o) Reload-scope command: arming matrix + busy-gate drop-not-queue (issue #30) -

    [Fact]
    public async Task ReloadScope_CanExecute_IsIndependentOfTheSelection_ArmedWithNoSelectionWhileIdle()
    {
        var snapshot = KindScope();
        var provider = Provider(snapshot);
        var fake = new FakeGraphRenderer();
        var vm = Workspace(provider, () => fake);
        await vm.Initialization;

        // Reload always reloads the ROOT, so unlike Refresh it needs NO selection:
        // armed the instant the busy gate releases, with nothing selected.
        Assert.Null(vm.SelectedDn);
        Assert.True(
            vm.ReloadScopeCommand.CanExecute(null),
            "Reload must arm with no selection — it reloads the whole scope, not a node");

        // A non-fetchable selection (a User) leaves it armed — selection is irrelevant.
        vm.SelectedDn = AdaDn;
        Assert.False(vm.RefreshCommand.CanExecute(null)); // anchor: Refresh needs fetchable
        Assert.True(
            vm.ReloadScopeCommand.CanExecute(null),
            "Reload stays armed regardless of what (if anything) is selected");

        // And a group selection keeps it armed too.
        vm.SelectedDn = CircleADn;
        Assert.True(vm.ReloadScopeCommand.CanExecute(null));

        Assert.Equal(0, provider.GetObjectCalls); // arming never touches the provider
        Assert.Equal(0, provider.GetMembersCalls);
    }

    [Fact]
    public async Task ReloadScope_CanExecute_IsFalseWhileBusy_AndCanExecuteChangedTracksIsLoading()
    {
        var loadGate = new TaskCompletionSource<DirectorySnapshot>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var provider = Provider(new DirectorySnapshot());
        provider.LoadScopeResult = loadGate.Task;
        var fake = new FakeGraphRenderer();
        var vm = Workspace(provider, () => fake);

        // The ONE global busy gate (IsLoading) is held by the initial scope load — Reload
        // must be disarmed even though it has no selection dependency.
        Assert.True(vm.IsLoading);
        Assert.False(vm.ReloadScopeCommand.CanExecute(null));

        loadGate.SetResult(CircleScope());
        await vm.Initialization;

        // The gate released: Reload arms, and CanExecuteChanged tracked the IsLoading flip.
        Assert.True(vm.ReloadScopeCommand.CanExecute(null));

        var raised = 0;
        vm.ReloadScopeCommand.CanExecuteChanged += (_, _) => raised++;

        // Gate the reload's fresh scope load so the busy window is observable.
        var reloadGate = new TaskCompletionSource<DirectorySnapshot>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        provider.LoadScopeResult = reloadGate.Task;
        var reload = vm.ReloadScopeCommand.ExecuteAsync(null);

        Assert.True(vm.IsLoading);
        Assert.False(vm.ReloadScopeCommand.CanExecute(null));
        Assert.True(raised >= 1, "CanExecuteChanged must fire when IsLoading turns on");

        var raisedWhileBusy = raised;
        reloadGate.SetResult(CircleScope());
        await reload;

        Assert.False(vm.IsLoading);
        Assert.True(vm.ReloadScopeCommand.CanExecute(null));
        Assert.True(raised > raisedWhileBusy, "CanExecuteChanged must fire when IsLoading turns off");
    }

    [Fact]
    public async Task ReloadExecute_WhileTheInitialScopeLoadIsInFlight_IsDropped_LoadScopeCallsUnchanged()
    {
        // Gate the INITIAL load through LoadScopeOverride: the ctor load is in flight and
        // holds the busy gate. A reload gesture mid-pipeline is dropped, never queued —
        // LoadScopeAsync must NOT be invoked a second time.
        var loadGate = new TaskCompletionSource<DirectorySnapshot>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var provider = Provider(new DirectorySnapshot());
        provider.LoadScopeOverride = _ => loadGate.Task;
        var fake = new FakeGraphRenderer();
        var vm = Workspace(provider, () => fake);

        Assert.True(vm.IsLoading);
        Assert.Equal(1, provider.LoadScopeCalls); // the in-flight ctor load

        // RelayCommand.Execute does NOT consult CanExecute — the VM must re-guard and drop.
        var reload = vm.ReloadScopeCommand.ExecuteAsync(null);

        // Dropped means NOTHING new: no second LoadScopeAsync, no extra show, gate untouched.
        Assert.Equal(1, provider.LoadScopeCalls);
        Assert.True(reload.IsCompleted, "a dropped reload settles immediately — it never started a pipeline");
        Assert.True(vm.IsLoading);

        // Release the initial load; the dropped reload was NOT queued behind it.
        loadGate.SetResult(CircleScope());
        await vm.Initialization;

        Assert.Equal(1, provider.LoadScopeCalls);
        Assert.False(vm.IsLoading);

        // Not disarmed, merely dropped: a fresh reload after the gate releases is honored.
        await vm.ReloadScopeCommand.ExecuteAsync(null);
        Assert.Equal(2, provider.LoadScopeCalls);
    }

    [Fact]
    public async Task ReloadExecute_WhileAnExpandFetchIsInFlight_IsDropped_NotQueued()
    {
        var snapshot = GroupScope(SalesDn);
        var provider = Provider(snapshot);
        var fake = new FakeGraphRenderer();
        var vm = Workspace(provider, () => fake);
        await vm.Initialization;
        Assert.Equal(1, provider.LoadScopeCalls);

        // An expand fetch holds the ONE global busy gate.
        var fetchGate = new TaskCompletionSource<IReadOnlyList<AdObject>>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        provider.GetMembersHandler = (_, _) => fetchGate.Task;
        fake.RaiseNodeExpandRequested(SalesDn, "GlobalGroup");
        Assert.True(vm.IsLoading);

        // A reload during the in-flight expand is dropped — no whole-scope reload races
        // the expand's replace-in-place update.
        var reload = vm.ReloadScopeCommand.ExecuteAsync(null);
        Assert.Equal(1, provider.LoadScopeCalls);
        Assert.True(reload.IsCompleted);

        fetchGate.SetResult([]);
        await vm.Expansion;

        // The dropped reload never replayed after the expand completed.
        Assert.Equal(1, provider.LoadScopeCalls);
        Assert.False(vm.IsLoading);
    }

    [Fact]
    public async Task ReloadExecute_WithoutARenderer_IsASilentNoOp_NoLoad_NoCrash()
    {
        var snapshot = CircleScope();
        var provider = Provider(snapshot);
        var vm = RendererlessWorkspace(provider);
        await vm.Initialization;
        Assert.Null(vm.GraphRenderer);
        Assert.Equal(1, provider.LoadScopeCalls);

        // No renderer => nothing to show => Reload disarmed and a direct Execute is inert
        // (the pipeline would dereference a null renderer). Mirrors the Refresh null-renderer pin.
        Assert.False(vm.ReloadScopeCommand.CanExecute(null));
        await vm.ReloadScopeCommand.ExecuteAsync(null);

        Assert.Equal(1, provider.LoadScopeCalls);
        Assert.Null(vm.LoadError);
        Assert.False(vm.IsLoading);
    }

    // --- (p) the in-canvas busy ring (ADR-019 / #94) ----------------------------------
    // The busy ring marks a directory round-trip in flight on a lazy-expanded node. It is
    // painted ONLY on the FETCH path — SetBusyAsync(dn,true) BEFORE the provider round-trip,
    // SetBusyAsync(dn,false) in the finally — and recorded on the fake's OWN SetBusyCalls
    // channel, NEVER FocusCalls. The cache-hit/focus-only and non-group branches return
    // early and issue NO busy call. busy is fire-and-forget: it never rides the focus channel
    // and the existing FocusCalls pins below must stay byte-for-byte green.

    [Fact]
    public async Task FetchExpand_PaintsBusyOn_ThenOff_InOrder_AroundTheRoundTrip()
    {
        var snapshot = GroupScope(SalesDn);
        var provider = Provider(snapshot);
        var fake = new FakeGraphRenderer();
        var vm = Workspace(provider, () => fake);
        await vm.Initialization;

        // Gate the provider round-trip so we can observe the busy-ON BEFORE it.
        var fetchGate = new TaskCompletionSource<IReadOnlyList<AdObject>>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        provider.GetMembersHandler = (_, _) => fetchGate.Task;

        fake.RaiseNodeExpandRequested(SalesDn, "GlobalGroup");

        // busy-ON precedes the provider round-trip: the ring is up while the fetch is in
        // flight, recorded on its OWN channel (not FocusCalls).
        Assert.False(vm.Expansion.IsCompleted);
        Assert.Equal([(SalesDn, true)], fake.SetBusyCalls);
        Assert.Empty(fake.FocusCalls);

        fetchGate.SetResult(
        [
            Obj("Ada Lovelace", AdaDn, AdObjectKind.User),
            Obj("GG_Ops", OpsDn),
        ]);
        await vm.Expansion;

        // [(dn,true),(dn,false)] in order — on around the round-trip, off in the finally.
        Assert.Equal([(SalesDn, true), (SalesDn, false)], fake.SetBusyCalls);

        // The fetch-path focus pin is UNAFFECTED — busy is its own channel.
        AssertFocusSet(Assert.Single(fake.FocusCalls), SalesDn, AdaDn, OpsDn);
        Assert.True(snapshot.IsLoaded(SalesDn));
        Assert.Null(vm.LoadError);
        Assert.False(vm.IsLoading);
    }

    [Fact]
    public async Task FetchExpand_ClearsBusy_EvenWhenTheFetchFails()
    {
        var snapshot = GroupScope(SalesDn);
        var provider = Provider(snapshot);
        var fake = new FakeGraphRenderer();
        var vm = Workspace(provider, () => fake);
        await vm.Initialization;

        provider.GetMembersHandler = (_, _) => Task.FromException<IReadOnlyList<AdObject>>(
            new DirectoryUnavailableException("expand boom"));

        fake.RaiseNodeExpandRequested(SalesDn, "GlobalGroup");
        await vm.Expansion; // handled inline — must NOT throw

        // The ring is cleared on the catch path too: the finally clears it regardless of
        // outcome (CancellationToken.None on the off-call).
        Assert.Equal([(SalesDn, true), (SalesDn, false)], fake.SetBusyCalls);
        // The ADR-019 token contract: busy-ON rides the expand's real cancellable token,
        // busy-OFF rides CancellationToken.None so the finally clears even a cancelled expand.
        Assert.True(fake.SetBusyTokens[0].CanBeCanceled);
        Assert.Equal(CancellationToken.None, fake.SetBusyTokens[1]);
        Assert.Equal("expand boom", vm.LoadError);

        // The failed fetch never reached the renderer's graph/focus surface.
        Assert.Empty(fake.UpdatedGraphs);
        Assert.Empty(fake.FocusCalls);
        Assert.False(vm.IsLoading);
    }

    [Fact]
    public async Task CacheHitFocusOnlyExpand_RecordsNoBusy()
    {
        var snapshot = CircleScope(); // GG_Circle_A LOADED with cached members
        var provider = Provider(snapshot);
        var fake = new FakeGraphRenderer();
        var vm = Workspace(provider, () => fake);
        await vm.Initialization;

        fake.RaiseNodeExpandRequested(CircleADn, "GlobalGroup"); // cache hit -> focus path
        await vm.Expansion;

        // The focus-only branch returns before the fetch arm — no round-trip, no ring.
        Assert.Empty(fake.SetBusyCalls);
        Assert.Equal(0, provider.GetMembersCalls);

        // The existing focus-only pin stays green.
        AssertFocusSet(Assert.Single(fake.FocusCalls), CircleADn, CircleBDn, AdaDn, ExternalDn);
        Assert.Null(vm.LoadError);
    }

    [Theory]
    [InlineData(RootDn, "OrganizationalUnit")]
    [InlineData(AdaDn, "User")]
    [InlineData(PcDn, "Computer")]
    public async Task NonGroupExpand_RecordsNoBusy(string dn, string kind)
    {
        var snapshot = CircleScope();
        var provider = Provider(snapshot);
        var fake = new FakeGraphRenderer();
        var vm = Workspace(provider, () => fake);
        await vm.Initialization;

        fake.RaiseNodeExpandRequested(dn, kind); // non-group -> focus path, no fetch
        await vm.Expansion;

        // A non-group dbltap never fetches, so it never paints the ring.
        Assert.Empty(fake.SetBusyCalls);
        Assert.Equal(0, provider.GetMembersCalls);
        AssertFocusSet(Assert.Single(fake.FocusCalls), dn);
        Assert.Null(vm.LoadError);
    }

    // --- helpers ------------------------------------------------------------------------

    private static AdObject Obj(
        string name, string dn, AdObjectKind kind = AdObjectKind.GlobalGroup) =>
        new() { Dn = dn, Kind = kind, Name = name };

    /// <summary>
    /// The cache-hit fixture: root OU + the loaded circular pair GG_Circle_A ↔
    /// GG_Circle_B (mirrors the seeded lab cycle) + a user + a computer, plus one
    /// out-of-scope member DN (<see cref="ExternalDn"/>) that GraphBuilder
    /// materializes as an External frontier node — NOT in Objects, NOT loaded.
    /// </summary>
    private static DirectorySnapshot CircleScope()
    {
        var snapshot = new DirectorySnapshot();
        snapshot.AddObject(Obj("Lab", RootDn, AdObjectKind.OrganizationalUnit));
        snapshot.AddObject(Obj("GG_Circle_A", CircleADn));
        snapshot.AddObject(Obj("GG_Circle_B", CircleBDn));
        snapshot.AddObject(Obj("Ada Lovelace", AdaDn, AdObjectKind.User));
        snapshot.AddObject(Obj("PC-01", PcDn, AdObjectKind.Computer));
        snapshot.SetMembers(CircleADn, [CircleBDn, AdaDn, ExternalDn]);
        snapshot.SetMembers(CircleBDn, [CircleADn]); // closes the A→B→A cycle
        return snapshot;
    }

    /// <summary>The CanExecute-matrix fixture: <see cref="CircleScope"/> plus one
    /// DomainLocal and one Universal group, so every fetchable kind is selectable.</summary>
    private static DirectorySnapshot KindScope()
    {
        var snapshot = CircleScope();
        snapshot.AddObject(Obj("DL_App_RO", DlDn, AdObjectKind.DomainLocalGroup));
        snapshot.AddObject(Obj("UG_All", UgDn, AdObjectKind.UniversalGroup));
        return snapshot;
    }

    /// <summary>The fetch-path fixture: root OU plus the given global groups present
    /// in Objects but NOT members-loaded (the group-rooted-scope frontier).</summary>
    private static DirectorySnapshot GroupScope(params string[] unloadedGroupDns)
    {
        var snapshot = new DirectorySnapshot();
        snapshot.AddObject(Obj("Lab", RootDn, AdObjectKind.OrganizationalUnit));
        foreach (var dn in unloadedGroupDns)
        {
            snapshot.AddObject(Obj(dn, dn));
        }

        return snapshot;
    }

    /// <summary>Stub whose scope load yields <paramref name="snapshot"/>.</summary>
    private static StubDirectoryProvider Provider(DirectorySnapshot snapshot) =>
        new(Task.FromResult(new DirectoryConnection("stub directory", 5)))
        {
            LoadScopeResult = Task.FromResult(snapshot),
        };

    /// <summary>Workspace VM rooted at <see cref="RootDn"/> (AP 2.2 S6 ctor shape).</summary>
    private static WorkspaceViewModel Workspace(
        StubDirectoryProvider provider, Func<IGraphRenderer> rendererFactory) =>
        new(
            provider,
            Obj("Lab", RootDn, AdObjectKind.OrganizationalUnit),
            new DirectoryConnection("stub directory", 5),
            webView2Missing: false,
            rendererFactory);

    /// <summary>Workspace VM constructed HEADLESS — null renderer factory (the same
    /// shape the AP 2.5 detail-panel tests will use): no renderer ever exists, no
    /// renderer events can fire (section n).</summary>
    private static WorkspaceViewModel RendererlessWorkspace(StubDirectoryProvider provider) =>
        new(
            provider,
            Obj("Lab", RootDn, AdObjectKind.OrganizationalUnit),
            new DirectoryConnection("stub directory", 5),
            webView2Missing: false,
            graphRendererFactory: null);

    /// <summary>Asserts one focus call names exactly <paramref name="expected"/>
    /// (set semantics via <see cref="Dn.Comparer"/>, count pinned — no duplicates).</summary>
    private static void AssertFocusSet(
        IReadOnlyCollection<string> focus, params string[] expected)
    {
        Assert.Equal(expected.Length, focus.Count);
        Assert.True(
            focus.ToHashSet(Dn.Comparer).SetEquals(expected),
            $"focus set [{string.Join("; ", focus)}] must equal [{string.Join("; ", expected)}]");
    }
}
