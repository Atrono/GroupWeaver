using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Avalonia.Headless.XUnit;

using GroupWeaver.App.Graph;
using GroupWeaver.App.Tests.Fakes;
using GroupWeaver.App.ViewModels;
using GroupWeaver.Core.Diff;
using GroupWeaver.Core.Graph;
using GroupWeaver.Core.Model;
using GroupWeaver.Core.Plan;

using Xunit;

namespace GroupWeaver.App.Tests;

/// <summary>
/// Pins ADR-015 Slices 6+7 (#66): the renderer seam <see cref="IGraphRenderer.ShowDiffGraphAsync"/>
/// and the <see cref="GapViewModel"/> that drives it. The Gap step is a sibling of
/// <see cref="PlanViewModel"/> (ADR-014 dispose discipline, own-renderer, null-renderer-safe
/// pipeline, <c>NodeClicked → SelectedDn</c> selection seam, <c>Back</c>/jump commands) but
/// SIMPLER in one respect: it shows the Ist-vs-Plan DIFF, never rule severity — it never calls
/// <c>RuleEngine.Evaluate</c>, and <see cref="IGraphRenderer.ShowDiffGraphAsync"/> carries a
/// <see cref="SnapshotDiff"/>, not a <c>RuleReport</c>.
///
/// <para><b>The compute pipeline.</b> <see cref="GapViewModel.RefreshAsync"/> projects the plan
/// (<c>PlanProjection.ToSnapshot</c>), computes <see cref="SnapshotDiff.Compute"/>, builds the
/// render union (<see cref="SnapshotDiff.BuildUnion"/>), the graph
/// (<c>GraphBuilder.Build(union, RootDn)</c>), the <see cref="GapSummary"/>, and the
/// <see cref="GapReport"/> — then (only when a renderer exists) pushes the union graph + the diff
/// through <see cref="IGraphRenderer.ShowDiffGraphAsync"/>. It is NULL-RENDERER-SAFE: with no
/// renderer it computes everything and skips the push (so it runs headless), mirroring
/// <see cref="PlanViewModel.RevalidateAsync"/>.</para>
///
/// <para><b>Borrowed Ist is read-only.</b> The Ist snapshot is borrowed — the diff/union never
/// mutate it and <see cref="GapViewModel.Dispose"/> never disposes it (ADR-015 D3 / ADR-005
/// append-only). Pinned by reading the input ist's Objects/IsLoaded/GetMembers after a full
/// Refresh+Dispose round-trip.</para>
///
/// <para><b>RED until Slices 6+7</b> add <see cref="IGraphRenderer.ShowDiffGraphAsync"/> (default
/// no-op, mirroring <c>ExportPngAsync</c>), <see cref="GapViewModel"/>, <see cref="GapRowModel"/>,
/// and a <c>GapReport.Empty</c> static. Equality discipline (rule-engine.md "compare PROJECTIONS"):
/// every <see cref="GapRowModel"/>/finding assertion is by <c>Kind</c> + <c>PrimaryDn</c> (the
/// structured identity), NEVER by message wording.</para>
/// </summary>
public sealed class GapModeTests
{
    private const string RootDn = "OU=AGDLP-Lab,DC=agdlp,DC=lab";

    // Fixture DNs (CN under the root). Names derived from the CN by the Obj() helper.
    private const string CommonDn = "CN=DL_FileShare_RW,OU=AGDLP-Lab,DC=agdlp,DC=lab";
    private const string CommonChildDn = "CN=GG_FileShare_Members,OU=AGDLP-Lab,DC=agdlp,DC=lab";
    private const string RemovedDn = "CN=GG_LegacyTeam,OU=AGDLP-Lab,DC=agdlp,DC=lab";
    private const string AddedDn = "CN=GG_Sales_EU,OU=AGDLP-Lab,DC=agdlp,DC=lab";
    private const string UnloadedParentDn = "CN=DL_Unexpanded,OU=AGDLP-Lab,DC=agdlp,DC=lab";

    // === (1) renderer seam: the default is a no-op; the fake records the call ==============

    /// <summary>
    /// The <see cref="IGraphRenderer.ShowDiffGraphAsync"/> seam is a DEFAULT no-op interface
    /// method (mirroring <c>ExportPngAsync</c>): a MINIMAL renderer that overrides NOTHING
    /// inherits the default and returns <see cref="Task.CompletedTask"/> without throwing — so a
    /// renderer fake with no diff surface still compiles and the VM's push degrades gracefully.
    /// </summary>
    [Fact]
    public async Task ShowDiffGraphAsync_DefaultInterfaceMethod_IsANoOp_ReturnsCompletedTask()
    {
        IGraphRenderer minimal = new MinimalRenderer();

        var diff = SnapshotDiff.Compute(new DirectorySnapshot(), new DirectorySnapshot());
        var graph = GraphBuilder.Build(new DirectorySnapshot(), RootDn);

        var task = minimal.ShowDiffGraphAsync(graph, diff);

        Assert.Same(Task.CompletedTask, task);
        var ex = await Record.ExceptionAsync(() => task);
        Assert.Null(ex);
    }

    /// <summary>
    /// <see cref="FakeGraphRenderer"/> OVERRIDES the seam and records the call on its own
    /// channels: the union graph, the diff, and the observed token — mirroring its
    /// <c>ShowGraphAsync</c> recording shape, kept separate from the severity channels.
    /// </summary>
    [Fact]
    public async Task FakeGraphRenderer_ShowDiffGraphAsync_RecordsGraphDiffAndToken()
    {
        var fake = new FakeGraphRenderer();
        var diff = SnapshotDiff.Compute(new DirectorySnapshot(), new DirectorySnapshot());
        var graph = GraphBuilder.Build(new DirectorySnapshot(), RootDn);
        using var cts = new CancellationTokenSource();

        await fake.ShowDiffGraphAsync(graph, diff, cts.Token);

        Assert.Same(graph, Assert.Single(fake.ShownDiffGraphs));
        Assert.Same(diff, Assert.Single(fake.ShownDiffs));
        Assert.Equal(cts.Token, Assert.Single(fake.ShowDiffGraphTokens));
        // The gap push carries NO severity — the show/update severity channels stay empty.
        Assert.Empty(fake.ShownGraphs);
        Assert.Empty(fake.UpdatedGraphs);
    }

    /// <summary>
    /// A freshly constructed <see cref="GapViewModel"/> starts with the empty
    /// <c>GapReport.Empty</c> shape: no findings, no rows, and (before any Refresh) no
    /// <see cref="GapViewModel.Diff"/> ⇒ <see cref="GapViewModel.HasFindings"/> and
    /// <see cref="GapViewModel.HasUncheckedAreas"/> both false; <see cref="GapViewModel.Snapshot"/>/
    /// <see cref="GapViewModel.Graph"/>/<see cref="GapViewModel.Summary"/> null until computed.
    /// </summary>
    [Fact]
    public void NewGapViewModel_StartsEmpty_NoFindings_NoComputeYet()
    {
        var gap = HeadlessGap(new DirectorySnapshot(), new PlanModel(RootDn));

        Assert.Same(GapReport.Empty, gap.Report);
        Assert.Empty(gap.GapRows);
        Assert.False(gap.HasFindings);
        Assert.False(gap.HasUncheckedAreas);
        Assert.Null(gap.Diff);
        Assert.Null(gap.Snapshot);
        Assert.Null(gap.Graph);
        Assert.Null(gap.Summary);
        Assert.Null(gap.GraphRenderer); // no factory => headless
        Assert.Equal(RootDn, gap.RootDn, Dn.Comparer);

        gap.Dispose();
    }

    // === (2) the compute pipeline (null-renderer-safe) ====================================

    /// <summary>
    /// THE pipeline pin. A hand-built Ist (a Common node, a Removed-from-Ist group, and a
    /// KNOWN-but-unloaded Ist parent) diffed against a <see cref="PlanModel"/> (the Common node,
    /// an Added group, and an edge under the unloaded Ist parent → an Unchecked area). After
    /// <see cref="GapViewModel.RefreshAsync"/>: <see cref="GapViewModel.Diff"/>/
    /// <see cref="GapViewModel.Graph"/>/<see cref="GapViewModel.Snapshot"/> (the union)/
    /// <see cref="GapViewModel.Summary"/> are all non-null; <see cref="GapViewModel.GapRows"/>
    /// carries the expected <see cref="GapKind.NodeAdded"/>/<see cref="GapKind.NodeRemoved"/>/
    /// <see cref="GapKind.UnverifiableArea"/> rows (asserted by <c>Kind</c> + <c>PrimaryDn</c>,
    /// NEVER message wording); <see cref="GapViewModel.HasUncheckedAreas"/> and
    /// <see cref="GapViewModel.HasFindings"/> are true. NO renderer ⇒ no throw, no push.
    /// </summary>
    [Fact]
    public async Task RefreshAsync_WithKnownDelta_ComputesDiffGraphSummaryReport_RowsByKindAndDn()
    {
        var ist = BuildDeltaIst();
        var plan = BuildDeltaPlan();
        var gap = HeadlessGap(ist, plan); // no renderer factory => headless, null-renderer-safe

        var ex = await Record.ExceptionAsync(() => gap.RefreshAsync());

        Assert.Null(ex);                  // null-renderer-safe: computes, no push, no throw
        Assert.NotNull(gap.Diff);
        Assert.NotNull(gap.Graph);
        Assert.NotNull(gap.Snapshot);     // the union
        Assert.NotNull(gap.Summary);

        // The union snapshot resolves both Ist + Plan DNs (BuildUnion materializes both sides).
        Assert.True(gap.Snapshot!.TryGetObject(AddedDn, out _));
        Assert.True(gap.Snapshot.TryGetObject(RemovedDn, out _));
        Assert.True(gap.Snapshot.TryGetObject(CommonDn, out _));

        // Rows by structured identity (Kind + PrimaryDn) — never by message text.
        Assert.Single(
            gap.GapRows,
            r => r.Kind == GapKind.NodeAdded && Dn.Comparer.Equals(r.PrimaryDn, AddedDn));
        Assert.Single(
            gap.GapRows,
            r => r.Kind == GapKind.NodeRemoved && Dn.Comparer.Equals(r.PrimaryDn, RemovedDn));
        Assert.Single(
            gap.GapRows,
            r => r.Kind == GapKind.UnverifiableArea && Dn.Comparer.Equals(r.PrimaryDn, UnloadedParentDn));

        // The Common node is NOT a finding (no row anchored on it).
        Assert.DoesNotContain(gap.GapRows, r => Dn.Comparer.Equals(r.PrimaryDn, CommonDn));

        Assert.True(gap.HasFindings);
        Assert.True(gap.HasUncheckedAreas);

        // The summary mirrors the diff it summarizes (no silent drift): ≥1 unchecked parent.
        Assert.True(gap.Summary!.UncheckedParents >= 1);

        gap.Dispose();
    }

    /// <summary>
    /// Subject-name resolution rides the UNION snapshot (set BEFORE Report so
    /// <c>OnReportChanged</c> can resolve against it): an Added plan node and a Removed Ist node
    /// both resolve to their friendly <c>AdObject.Name</c> (their CN), proving the union carries
    /// both sides' names. (Identity stays the structured <c>Kind</c>+<c>PrimaryDn</c>; this only
    /// pins that <c>SubjectName</c> is the resolved name, not the raw DN.)
    /// </summary>
    [Fact]
    public async Task RefreshAsync_ResolvesRowSubjectNames_AgainstTheUnion_BothSides()
    {
        var gap = HeadlessGap(BuildDeltaIst(), BuildDeltaPlan());

        await gap.RefreshAsync();

        var added = Assert.Single(
            gap.GapRows,
            r => r.Kind == GapKind.NodeAdded && Dn.Comparer.Equals(r.PrimaryDn, AddedDn));
        Assert.Equal(NameOf(AddedDn), added.SubjectName); // plan-side name, via the union

        var removed = Assert.Single(
            gap.GapRows,
            r => r.Kind == GapKind.NodeRemoved && Dn.Comparer.Equals(r.PrimaryDn, RemovedDn));
        Assert.Equal(NameOf(RemovedDn), removed.SubjectName); // ist-side name, via the union

        gap.Dispose();
    }

    /// <summary>
    /// Null-renderer-safe explicitly: with NO renderer factory <see cref="GapViewModel.GraphRenderer"/>
    /// is null and <see cref="GapViewModel.RefreshAsync"/> computes the full pipeline yet performs
    /// NO renderer push (no throw) — the contract that lets the Gap step run headless.
    /// </summary>
    [Fact]
    public async Task RefreshAsync_WithNoRenderer_ComputesPipeline_SkipsPush_WithoutThrowing()
    {
        var gap = HeadlessGap(BuildDeltaIst(), BuildDeltaPlan());
        Assert.Null(gap.GraphRenderer);

        var ex = await Record.ExceptionAsync(() => gap.RefreshAsync());

        Assert.Null(ex);
        Assert.NotNull(gap.Diff);
        Assert.NotNull(gap.Graph);
        Assert.NotNull(gap.Snapshot);

        gap.Dispose();
    }

    // === (3) the renderer push (destroy+fit, ShowDiffGraphAsync — never update) ============

    /// <summary>
    /// With a recording fake, <see cref="GapViewModel.RefreshAsync"/> calls
    /// <see cref="IGraphRenderer.ShowDiffGraphAsync"/> EXACTLY ONCE, handing the union
    /// <see cref="GapViewModel.Graph"/> and the <see cref="GapViewModel.Diff"/> — the wholesale
    /// destroy+fit gap push, NOT a replace-in-place update (the update/show severity channels
    /// stay empty: a gap render is its own seam and carries no <c>RuleReport</c>).
    /// </summary>
    [Fact]
    public async Task RefreshAsync_WithRenderer_PushesUnionGraphAndDiff_OnceViaShowDiffGraph()
    {
        var fake = new FakeGraphRenderer();
        var gap = new GapViewModel(
            BuildDeltaIst(), BuildDeltaPlan(), RootDn, graphRendererFactory: () => fake);

        await gap.RefreshAsync();

        Assert.Same(gap.Graph, Assert.Single(fake.ShownDiffGraphs)); // the union graph
        Assert.Same(gap.Diff, Assert.Single(fake.ShownDiffs));       // the computed diff
        Assert.Single(fake.ShowDiffGraphTokens);

        // It is ShowDiffGraphAsync (destroy+fit) — never the severity show/update path.
        Assert.Empty(fake.ShownGraphs);
        Assert.Empty(fake.UpdatedGraphs);

        gap.Dispose();
    }

    // === (4) borrowed Ist is read-only ===================================================

    /// <summary>
    /// The borrowed Ist snapshot is consumed READ-ONLY: after a full
    /// <see cref="GapViewModel.RefreshAsync"/> + <see cref="GapViewModel.Dispose"/> round-trip,
    /// the input ist's <c>Objects</c> count, its known-but-unloaded parent's <c>IsLoaded</c>
    /// (still false) and <c>GetMembers</c> (still null), and a loaded parent's member list are
    /// ALL unchanged — the diff/union never write to it and Dispose never disposes it (ADR-015 D3
    /// / ADR-005 append-only).
    /// </summary>
    [Fact]
    public async Task RefreshAsync_ThenDispose_LeavesBorrowedIstUntouched()
    {
        var ist = BuildDeltaIst();

        var objectsBefore = ist.Objects.Select(o => o.Dn).OrderBy(d => d, Dn.Comparer).ToList();
        var commonMembersBefore = ist.GetMembers(CommonDn)?.ToList(); // a loaded parent

        var gap = HeadlessGap(ist, BuildDeltaPlan());
        await gap.RefreshAsync();
        gap.Dispose();

        Assert.Equal(
            objectsBefore,
            ist.Objects.Select(o => o.Dn).OrderBy(d => d, Dn.Comparer).ToList());

        // The known-but-unloaded Ist parent stays never-loaded (the union loaded ITS copy, not ist).
        Assert.False(ist.IsLoaded(UnloadedParentDn));
        Assert.Null(ist.GetMembers(UnloadedParentDn));

        // The loaded parent's member list is unchanged (the union members live on a fresh snapshot).
        Assert.Equal(commonMembersBefore, ist.GetMembers(CommonDn)?.ToList());
    }

    // === (5) selection + jump ============================================================

    /// <summary>
    /// Selection wiring: a node tap arriving over the Gap step's OWN renderer seam sets
    /// <see cref="GapViewModel.SelectedDn"/> VERBATIM (DN strings flow uncanonicalized — the same
    /// <c>NodeClicked → SelectedDn</c> contract <see cref="PlanViewModel"/> has). A
    /// <see cref="GapRowModel"/> whose <c>PrimaryDn</c> matches the selection (under
    /// <c>Dn.Comparer</c>) lights up (<see cref="GapRowModel.IsActive"/> true); a non-matching
    /// row stays dark.
    /// </summary>
    [AvaloniaFact]
    public async Task NodeClicked_SetsSelectedDnVerbatim_AndLightsTheMatchingRow()
    {
        var fake = new FakeGraphRenderer();
        var gap = new GapViewModel(
            BuildDeltaIst(), BuildDeltaPlan(), RootDn, graphRendererFactory: () => fake);
        await gap.RefreshAsync();

        // Tap the Added node — its row must light, the Removed row must not.
        fake.RaiseNodeClicked(AddedDn, "GlobalGroup");

        Assert.Equal(AddedDn, gap.SelectedDn); // verbatim, never canonicalized

        var addedRow = Assert.Single(
            gap.GapRows,
            r => r.Kind == GapKind.NodeAdded && Dn.Comparer.Equals(r.PrimaryDn, AddedDn));
        Assert.True(addedRow.IsActive, "the row whose PrimaryDn matches the selection is active");

        var removedRow = Assert.Single(
            gap.GapRows,
            r => r.Kind == GapKind.NodeRemoved && Dn.Comparer.Equals(r.PrimaryDn, RemovedDn));
        Assert.False(removedRow.IsActive, "a non-matching row stays inactive");

        gap.Dispose();
    }

    /// <summary>
    /// A node tap carrying a CASE-VARIANT spelling of a row's anchor still lights that row
    /// (selection-sync matches under <c>Dn.Comparer</c>, never ordinal) — the data-model
    /// case-insensitive identity rule the sidebar highlight depends on.
    /// </summary>
    [AvaloniaFact]
    public async Task NodeClicked_CaseVariantDn_StillLightsTheRow()
    {
        var fake = new FakeGraphRenderer();
        var gap = new GapViewModel(
            BuildDeltaIst(), BuildDeltaPlan(), RootDn, graphRendererFactory: () => fake);
        await gap.RefreshAsync();

        fake.RaiseNodeClicked(AddedDn.ToUpperInvariant(), "GlobalGroup");

        var addedRow = Assert.Single(
            gap.GapRows,
            r => r.Kind == GapKind.NodeAdded && Dn.Comparer.Equals(r.PrimaryDn, AddedDn));
        Assert.True(addedRow.IsActive);

        gap.Dispose();
    }

    /// <summary>
    /// <see cref="GapViewModel.JumpToCommand"/> over a row sets <see cref="GapViewModel.SelectedDn"/>
    /// to the row's <c>PrimaryDn</c> (driving the highlight) AND frames the anchor on the graph via
    /// <see cref="IGraphRenderer.FocusAsync"/> with exactly <c>[row.PrimaryDn]</c> (recorded by the
    /// fake) — the gap jump, mirroring the workspace/plan jump.
    /// </summary>
    [Fact]
    public async Task JumpToCommand_SetsSelectedDn_AndFocusesTheAnchor()
    {
        var fake = new FakeGraphRenderer();
        var gap = new GapViewModel(
            BuildDeltaIst(), BuildDeltaPlan(), RootDn, graphRendererFactory: () => fake);
        await gap.RefreshAsync();

        var row = Assert.Single(
            gap.GapRows,
            r => r.Kind == GapKind.NodeRemoved && Dn.Comparer.Equals(r.PrimaryDn, RemovedDn));

        await gap.JumpToCommand.ExecuteAsync(row);

        Assert.Equal(RemovedDn, gap.SelectedDn, Dn.Comparer);
        var focused = Assert.Single(fake.FocusCalls);
        Assert.Equal(RemovedDn, Assert.Single(focused), Dn.Comparer);

        gap.Dispose();
    }

    /// <summary>
    /// <see cref="GapViewModel.JumpToCommand"/> is null-safe: a null row is a no-op (no throw, no
    /// selection change, no <see cref="IGraphRenderer.FocusAsync"/> call) — the guard the command
    /// shares with <see cref="PlanViewModel"/>'s jump.
    /// </summary>
    [Fact]
    public async Task JumpToCommand_WithNullRow_IsANoOp()
    {
        var fake = new FakeGraphRenderer();
        var gap = new GapViewModel(
            BuildDeltaIst(), BuildDeltaPlan(), RootDn, graphRendererFactory: () => fake);
        await gap.RefreshAsync();

        var ex = await Record.ExceptionAsync(() => gap.JumpToCommand.ExecuteAsync(null));

        Assert.Null(ex);
        Assert.Null(gap.SelectedDn);
        Assert.Empty(fake.FocusCalls);

        gap.Dispose();
    }

    // === (6) empty / clean: ist == projection of the same plan ============================

    /// <summary>
    /// The clean case: when the Ist snapshot is EXACTLY <c>PlanProjection.ToSnapshot(plan)</c>
    /// (the same plan), the diff is all-Common — <see cref="GapViewModel.RefreshAsync"/> yields
    /// EMPTY <see cref="GapViewModel.GapRows"/>, <see cref="GapViewModel.HasFindings"/> false,
    /// <see cref="GapViewModel.HasUncheckedAreas"/> false, and a
    /// <see cref="GapViewModel.Summary"/> with zero Added/Removed/Unchecked deltas (only the
    /// Common counts non-zero).
    /// </summary>
    [Fact]
    public async Task RefreshAsync_IstEqualsProjectionOfSamePlan_NoFindings_NoUnchecked_ZeroDelta()
    {
        var plan = BuildCleanPlan();
        var ist = PlanProjection.ToSnapshot(plan); // ist IS the projection of the same plan

        var gap = HeadlessGap(ist, plan);

        await gap.RefreshAsync();

        Assert.Empty(gap.GapRows);
        Assert.False(gap.HasFindings);
        Assert.False(gap.HasUncheckedAreas);

        Assert.NotNull(gap.Summary);
        Assert.Equal(0, gap.Summary!.AddedNodes);
        Assert.Equal(0, gap.Summary.RemovedNodes);
        Assert.Equal(0, gap.Summary.AddedEdges);
        Assert.Equal(0, gap.Summary.RemovedEdges);
        Assert.Equal(0, gap.Summary.UncheckedEdges);
        Assert.Equal(0, gap.Summary.UncheckedParents);

        // The clean diff is not empty of content — it is all-Common (the plan really matched).
        Assert.True(gap.Summary.CommonNodes > 0);

        gap.Dispose();
    }

    // === (7) dispose discipline ==========================================================

    /// <summary>
    /// <see cref="GapViewModel.Dispose"/> is idempotent: the first call flips
    /// <see cref="GapViewModel.IsDisposed"/>; a second call is a no-op (no throw). Mirrors the
    /// <see cref="PlanViewModel"/> dispose guard.
    /// </summary>
    [Fact]
    public void Dispose_IsIdempotent_FlipsIsDisposed()
    {
        var gap = HeadlessGap(new DirectorySnapshot(), new PlanModel(RootDn));
        Assert.False(gap.IsDisposed);

        gap.Dispose();
        Assert.True(gap.IsDisposed);

        var ex = Record.Exception(() => gap.Dispose()); // second call is a no-op
        Assert.Null(ex);
        Assert.True(gap.IsDisposed);
    }

    /// <summary>
    /// <see cref="GapViewModel.Dispose"/> never disposes nor mutates the borrowed inputs: after
    /// Dispose the input ist (its Objects) and the input plan (its Nodes/Edges) are untouched —
    /// the shell, never the Gap step, owns their lifetime.
    /// </summary>
    [Fact]
    public void Dispose_DoesNotTouchBorrowedIstOrPlan()
    {
        var ist = BuildDeltaIst();
        var plan = BuildDeltaPlan();

        var istObjectsBefore = ist.Objects.Count;
        var planNodesBefore = plan.Nodes.Count;
        var planEdgesBefore = plan.Edges.Count;

        var gap = HeadlessGap(ist, plan);
        gap.Dispose();

        Assert.Equal(istObjectsBefore, ist.Objects.Count);
        Assert.Equal(planNodesBefore, plan.Nodes.Count);
        Assert.Equal(planEdgesBefore, plan.Edges.Count);
    }

    // === fixtures + helpers ==============================================================

    /// <summary>
    /// The Ist side of the known delta: a Common node (loaded, with one Common-child member), a
    /// distinct Common child group, a Removed-from-Ist group (loaded-empty), and a KNOWN-but-unloaded
    /// Ist parent (added, never SetMembers'd — the Unchecked-area seed). The Added group
    /// (<see cref="AddedDn"/>) is DELIBERATELY ABSENT here so it is plan-only ⇒ NodeAdded; the
    /// Common edge (CommonDn → CommonChildDn) is identical on both sides ⇒ no edge finding (so no row
    /// is anchored on CommonDn).
    /// </summary>
    private static DirectorySnapshot BuildDeltaIst()
    {
        var ist = new DirectorySnapshot();
        ist.AddObject(Obj(CommonDn, AdObjectKind.DomainLocalGroup));   // Common (loaded parent)
        ist.AddObject(Obj(CommonChildDn, AdObjectKind.GlobalGroup));   // Common child (the Common edge target)
        ist.AddObject(Obj(RemovedDn, AdObjectKind.GlobalGroup));       // Removed-from-Ist
        ist.AddObject(Obj(UnloadedParentDn, AdObjectKind.DomainLocalGroup)); // known, NEVER loaded
        ist.SetMembers(CommonDn, [CommonChildDn]);                     // a Common edge (same both sides)
        ist.SetMembers(RemovedDn, []);                                 // loaded-empty (no unchecked)
        // AddedDn deliberately NOT in the Ist -> plan-only -> NodeAdded.
        // UnloadedParentDn deliberately NOT SetMembers'd -> known-but-unloaded.
        return ist;
    }

    /// <summary>
    /// The Plan side of the known delta: the Common node + the distinct Common child + the SAME
    /// Common member edge (CommonDn → CommonChildDn, identical on both sides ⇒ no edge finding),
    /// PLUS a plan-only Added group (<see cref="AddedDn"/>, absent from the Ist ⇒ NodeAdded), PLUS
    /// an edge under the (Ist-)unloaded parent (DL_Unexpanded → GG_Sales_EU, so that edge is
    /// Unchecked and the parent surfaces as an UnverifiableArea). The Removed-from-Ist group is
    /// absent here (so it is NodeRemoved).
    /// </summary>
    private static PlanModel BuildDeltaPlan()
    {
        var plan = new PlanModel(RootDn);
        var common = plan.AddNode(PlanCreatableKind.DomainLocalGroup, "DL_FileShare_RW"); // Common
        var commonChild = plan.AddNode(PlanCreatableKind.GlobalGroup, "GG_FileShare_Members"); // Common child
        var added = plan.AddNode(PlanCreatableKind.GlobalGroup, "GG_Sales_EU");           // Added (plan-only)
        var unloaded = plan.AddNode(PlanCreatableKind.DomainLocalGroup, "DL_Unexpanded"); // Common node, edge unchecked

        // DL_FileShare_RW -> GG_FileShare_Members: the SAME edge the Ist has (a Common edge,
        // identical on both sides) -> NO edge finding -> no row anchored on CommonDn.
        plan.AddEdge(common.Dn, commonChild.Dn);
        // DL_Unexpanded -> GG_Sales_EU: under a known-but-unloaded Ist parent -> Unchecked.
        plan.AddEdge(unloaded.Dn, added.Dn);

        // Sanity: the formed DNs match the fixture constants (CN-escaping is identity here).
        Assert.Equal(CommonDn, common.Dn, Dn.Comparer);
        Assert.Equal(CommonChildDn, commonChild.Dn, Dn.Comparer);
        Assert.Equal(AddedDn, added.Dn, Dn.Comparer);
        Assert.Equal(UnloadedParentDn, unloaded.Dn, Dn.Comparer);
        return plan;
    }

    /// <summary>A plan with no Unchecked seed and a real membership — used for the clean case
    /// where the Ist is exactly this plan's projection (all-Common diff).</summary>
    private static PlanModel BuildCleanPlan()
    {
        var plan = new PlanModel(RootDn);
        var dl = plan.AddNode(PlanCreatableKind.DomainLocalGroup, "DL_FileShare_RW");
        var gg = plan.AddNode(PlanCreatableKind.GlobalGroup, "GG_Sales_EU");
        plan.AddEdge(dl.Dn, gg.Dn);
        return plan;
    }

    /// <summary>A headless Gap VM (no renderer factory): <see cref="GapViewModel.RefreshAsync"/>
    /// must be null-renderer-safe. Rooted at the lab OU.</summary>
    private static GapViewModel HeadlessGap(DirectorySnapshot ist, PlanModel plan) =>
        new(ist, plan, RootDn);

    /// <summary>A hand-built object whose Name is its CN (mirrors the Core diff tests' idiom).</summary>
    private static AdObject Obj(string dn, AdObjectKind kind) => new()
    {
        Dn = dn,
        Kind = kind,
        Name = NameOf(dn),
    };

    /// <summary>The CN of a fixture DN (the resolved subject Name the union exposes).</summary>
    private static string NameOf(string dn) => dn.Split(',')[0]["CN=".Length..];

    /// <summary>A minimal <see cref="IGraphRenderer"/> that overrides NOTHING — it inherits the
    /// default no-op <see cref="IGraphRenderer.ShowDiffGraphAsync"/> (and every other default
    /// interface method). Pins that a renderer fake without a diff surface still compiles.</summary>
    private sealed class MinimalRenderer : IGraphRenderer
    {
        public Avalonia.Controls.Control? View => null;

        public Task ShowGraphAsync(
            GraphModel graph,
            GroupWeaver.Core.Rules.RuleReport report,
            IReadOnlyDictionary<string, (int Count, GroupWeaver.Core.Rules.RuleSeverity Sev)>? belowMap,
            CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task UpdateGraphAsync(
            GraphModel graph,
            GroupWeaver.Core.Rules.RuleReport report,
            IReadOnlyDictionary<string, (int Count, GroupWeaver.Core.Rules.RuleSeverity Sev)>? belowMap,
            CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task FocusAsync(
            IReadOnlyCollection<string> dns, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        // Explicit add/remove accessors (never CS0067 "event never used"): this minimal impl
        // exists only to inherit the default-no-op seam, so it never raises these.
        public event EventHandler<GraphNodeEventArgs>? NodeClicked
        {
            add { }
            remove { }
        }

        public event EventHandler<GraphNodeEventArgs>? NodeExpandRequested
        {
            add { }
            remove { }
        }

        public event EventHandler<GraphErrorEventArgs>? RendererError
        {
            add { }
            remove { }
        }

        // #122: IGraphRenderer is now IDisposable — minimal no-op to keep this compile-only
        // fake compiling (no surface to tear down).
        public void Dispose()
        {
        }
    }
}
