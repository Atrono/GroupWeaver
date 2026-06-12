using GroupWeaver.App.Graph;
using GroupWeaver.App.Tests.Fakes;
using GroupWeaver.App.ViewModels;
using GroupWeaver.Core.Graph;
using GroupWeaver.Core.Model;
using GroupWeaver.Core.Providers;

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
