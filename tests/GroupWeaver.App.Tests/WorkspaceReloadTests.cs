using GroupWeaver.App.Graph;
using GroupWeaver.App.Tests.Fakes;
using GroupWeaver.App.ViewModels;
using GroupWeaver.Core.Model;
using GroupWeaver.Core.Providers;
using GroupWeaver.Providers;
using GroupWeaver.Tests;
using GroupWeaver.Tests.Providers.Ldap;

using Xunit;

namespace GroupWeaver.App.Tests;

/// <summary>
/// Issue #30 Slice 3: whole-scope reload drops node-Refresh ex-member ORPHANS by
/// construction — NO pruning code (ADR-005 D4 addendum). The crux: a reload re-runs
/// <c>LoadScopeAsync(RootDn)</c>, which both providers answer with a <em>fresh</em>
/// <see cref="DirectorySnapshot"/> rebuilt from scratch; the VM replaces the
/// <c>Snapshot</c> reference wholesale and <see cref="Core.Graph.GraphBuilder"/> is total
/// over <c>Snapshot.Objects</c>, so any object that was in the OLD snapshot but is NOT in
/// the fresh load simply cannot survive into the rebuilt graph. The renderer verb is
/// <see cref="IGraphRenderer.ShowGraphAsync"/> (replace-all), NEVER
/// <see cref="IGraphRenderer.UpdateGraphAsync"/> — topology is wholesale-new.
///
/// <para><b>Demo arm</b> (<see cref="StubDirectoryProvider"/>, fully offline): expand a
/// group so an ex-member node exists in the snapshot + graph, then mutate
/// <see cref="StubDirectoryProvider.LoadScopeResult"/> to a snapshot WITHOUT that member
/// and reload — the reloaded <see cref="WorkspaceViewModel.Graph"/> must not contain the
/// ex-member DN (orphan gone, no pruning), <c>ShownGraphs.Count</c> incremented, and
/// <see cref="WorkspaceViewModel.SelectedDn"/> cleared to <c>null</c>.</para>
///
/// <para><b>Live arm</b> (<c>[Trait(Category, RequiresAd)]</c>, excluded in CI via
/// <c>build.ps1 -SkipAdTests</c>; skipped-and-warned off the lab DC by
/// <see cref="AdFactAttribute"/>): over the REAL <see cref="LdapProvider"/> rooted at the
/// seeded <c>OU=AGDLP-Lab</c>, an idempotent reload re-yields the SAME node set, and the
/// reload MUST TERMINATE despite the seeded GG_Circle_A↔GG_Circle_B cycle — the
/// <c>ComputeBelow</c> walk over the fresh snapshot is bounded with
/// <c>Task.Run</c> + <c>Timeout</c> like every other live-AD test. The lab is already
/// seeded; this agent NEVER mutates it.</para>
/// </summary>
public sealed class WorkspaceReloadTests
{
    private const string RootDn = "OU=Lab,DC=stub,DC=lab";
    private const string SalesDn = "CN=GG_Sales,OU=Lab,DC=stub,DC=lab";
    private const string AdaDn = "CN=Ada Lovelace,OU=Lab,DC=stub,DC=lab";
    private const string OpsDn = "CN=GG_Ops,OU=Lab,DC=stub,DC=lab";

    // --- demo arm: reload drops the node-Refresh ex-member orphan by construction -------

    [Fact]
    public async Task Reload_DropsTheExMemberOrphanNode_ByConstruction_ShowsNotUpdates_SelectionCleared()
    {
        // Initial scope: root OU + an unloaded global group GG_Sales (the group-rooted
        // frontier shape). Expanding it will fetch Ada — who becomes a real snapshot node.
        var provider = Provider(GroupScope(SalesDn));
        var fake = new FakeGraphRenderer();
        var vm = Workspace(provider, () => fake);
        await vm.Initialization;
        Assert.Equal(1, provider.LoadScopeCalls);

        // Expand GG_Sales: Ada joins the snapshot + graph as a genuine User node — exactly
        // the situation a later node-Refresh that drops Ada would leave orphaned.
        provider.GetMembersHandler = (_, _) => Task.FromResult<IReadOnlyList<AdObject>>(
            [Obj("Ada Lovelace", AdaDn, AdObjectKind.User), Obj("GG_Ops", OpsDn)]);
        fake.RaiseNodeExpandRequested(SalesDn, "GlobalGroup");
        await vm.Expansion;

        // Sanity: Ada is now a drawn node in the CURRENT graph (the orphan-to-be).
        var expanded = vm.Graph;
        Assert.NotNull(expanded);
        Assert.Contains(expanded.Nodes, n => Dn.Comparer.Equals(n.Dn, AdaDn));
        Assert.True(vm.Snapshot!.TryGetObject(AdaDn, out _));
        Assert.Single(fake.ShownGraphs); // the ctor load's one show, no reload yet

        // The expand was a replace-in-place UpdateGraphAsync (ADR-005 D2) — record the
        // count so the reload can be pinned to add NOTHING to the update channel.
        var updatesBeforeReload = fake.UpdatedGraphs.Count;
        Assert.Equal(1, updatesBeforeReload);

        // Select Ada so we can pin that reload clears the selection (it may not survive).
        fake.RaiseNodeClicked(AdaDn, "User");
        Assert.Equal(AdaDn, vm.SelectedDn);

        // The directory changed: Ada left GG_Sales AND the OU. The reload's fresh scope
        // load no longer contains her at all — the new snapshot is rebuilt from scratch,
        // so there is no pruning step, the orphan simply never gets created.
        var reloaded = GroupScope(SalesDn); // root + GG_Sales only — Ada is GONE
        provider.LoadScopeResult = Task.FromResult(reloaded);

        await vm.ReloadScopeCommand.ExecuteAsync(null);

        // A SECOND LoadScopeAsync ran and replaced the snapshot wholesale.
        Assert.Equal(2, provider.LoadScopeCalls);
        Assert.NotSame(expanded, vm.Graph);
        Assert.False(
            vm.Snapshot!.TryGetObject(AdaDn, out _),
            "the fresh scope load must not carry the ex-member — append-only snapshot, rebuilt from scratch");

        // THE crux assertion: the orphan ex-member node is gone from the rebuilt graph —
        // by construction (GraphBuilder is total over the fresh Objects), NOT by pruning.
        Assert.DoesNotContain(vm.Graph!.Nodes, n => Dn.Comparer.Equals(n.Dn, AdaDn));
        Assert.DoesNotContain(vm.Graph!.Nodes, n => Dn.Comparer.Equals(n.Dn, OpsDn));

        // Replace-all render verb: the reload lands in ShownGraphs (destroy+fit), NEVER
        // UpdateGraphAsync — the show count incremented past the ctor load's single show,
        // and the update channel is UNCHANGED across the reload (the keystone assertion:
        // reload uses ShowGraphAsync, the expand's earlier UpdateGraphAsync is not added to).
        Assert.Equal(2, fake.ShownGraphs.Count);
        Assert.Same(vm.Graph, fake.ShownGraphs[^1]);
        Assert.Equal(updatesBeforeReload, fake.UpdatedGraphs.Count);

        // Selection cleared up front (the selected DN may not survive the rebuild).
        Assert.Null(vm.SelectedDn);
        Assert.Null(vm.DetailPanel);
        Assert.Null(vm.LoadError);
        Assert.False(vm.IsLoading);
    }

    // --- live arm: reload over the seeded lab re-yields the same node set, terminates ---

    /// <summary>
    /// Over the REAL provider against <c>OU=AGDLP-Lab</c>: an idempotent reload (the lab is
    /// unchanged between loads) re-yields the SAME drawn node set, lands in ShowGraph (not
    /// UpdateGraph), and TERMINATES despite the seeded GG_Circle_A↔GG_Circle_B cycle — the
    /// whole pipeline (including <c>ComputeBelow</c>'s membership walk over the fresh
    /// snapshot) is bounded by <c>Task.Run</c> + <see cref="AdFactAttribute.Timeout"/>, so a
    /// non-terminating walk fails as a timeout instead of hanging the suite. Excluded in CI
    /// via the class-level <c>Category=RequiresAd</c> trait; skipped-and-warned off the lab
    /// DC via <see cref="AdFactAttribute"/>. The lab is already seeded — NOT mutated here.
    /// </summary>
    [Trait(TestCategories.Category, TestCategories.RequiresAd)]
    public sealed class LiveLab
    {
        private const string Server = "localhost";
        private const string LabDn = "OU=AGDLP-Lab,DC=agdlp,DC=lab";

        [AdFact(Timeout = 120_000)]
        public async Task ReloadOverTheLabScope_TerminatesDespiteTheSeededCycle_ReYieldsTheSameNodeSet()
        {
            var provider = new LdapProvider(Server, LabDn);
            var connection = await provider.ConnectAsync();
            var root = await provider.GetObjectAsync(LabDn)
                ?? throw new InvalidOperationException($"lab root unresolvable: {LabDn}");

            var fake = new FakeGraphRenderer();
            using var vm = new WorkspaceViewModel(
                provider, root, connection, webView2Missing: false, () => fake);

            // Task.Run + the AdFact Timeout bound BOTH the initial load AND the reload — if
            // ComputeBelow's walk ever failed to terminate on the seeded cycle, this would
            // surface as the xUnit timeout, never an indefinite hang.
            await Task.Run(async () =>
            {
                await vm.Initialization;
                Assert.Null(vm.LoadError);

                var before = vm.Graph;
                Assert.NotNull(before);
                var beforeDns = before.Nodes.Select(n => n.Dn).ToHashSet(Dn.Comparer);
                Assert.True(beforeDns.Count >= 195, $"expected >= 195 lab nodes, got {beforeDns.Count}");
                Assert.Single(fake.ShownGraphs);

                // Reload the WHOLE scope: a fresh LoadScopeAsync(RootDn) → fresh snapshot →
                // GraphBuilder.Build → ComputeBelow walk (must terminate over the cycle) →
                // ShowGraphAsync. The lab is unchanged, so the node set is identical.
                await vm.ReloadScopeCommand.ExecuteAsync(null);
                Assert.Null(vm.LoadError);

                var after = vm.Graph;
                Assert.NotNull(after);
                Assert.NotSame(before, after); // rebuilt from a fresh snapshot, not reused

                var afterDns = after.Nodes.Select(n => n.Dn).ToHashSet(Dn.Comparer);
                Assert.True(
                    afterDns.SetEquals(beforeDns),
                    "an idempotent reload of the unchanged lab must re-yield the identical node set");

                // Replace-all verb: the reload is a second ShowGraphAsync, never an update.
                Assert.Equal(2, fake.ShownGraphs.Count);
                Assert.Same(after, fake.ShownGraphs[^1]);
                Assert.Empty(fake.UpdatedGraphs);

                // Reset policy: selection-independent reload clears nothing-was-selected and
                // settles the busy gate.
                Assert.Null(vm.SelectedDn);
                Assert.False(vm.IsLoading);
            });
        }
    }

    // --- helpers ------------------------------------------------------------------------

    private static AdObject Obj(
        string name, string dn, AdObjectKind kind = AdObjectKind.GlobalGroup) =>
        new() { Dn = dn, Kind = kind, Name = name };

    /// <summary>Root OU plus the given global groups present in Objects but NOT
    /// members-loaded — the group-rooted-scope frontier (mirrors WorkspaceExpandTests).</summary>
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
}
