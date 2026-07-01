using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Avalonia.Headless.XUnit;

using GroupWeaver.App.Graph;
using GroupWeaver.App.Rules;
using GroupWeaver.App.Settings;
using GroupWeaver.App.Startup;
using GroupWeaver.App.Tests.Fakes;
using GroupWeaver.App.ViewModels;
using GroupWeaver.Core.Model;
using GroupWeaver.Core.Plan;
using GroupWeaver.Core.Providers;
using GroupWeaver.Core.Rules;
using GroupWeaver.Providers;

using Xunit;

namespace GroupWeaver.App.Tests;

/// <summary>
/// Pins AP 4.2.2 (ADR-014): Plan Mode is REACHABLE but EMPTY — a sibling shell step
/// (<see cref="PlanViewModel"/>) the live workspace switches into and back out of, the
/// empty-plan report shape, the dispose discipline (the round-trip never disposes the
/// workspace; teardown disposes BOTH the workspace and any created plan VM), and the
/// settings re-thread of a LIVE plan step. No editing commands here (add group/user/edge
/// land in AP 4.2.3) and no seed-from-Ist — empty-start only.
///
/// <para><b>The deterministic switch seam.</b> Like
/// <see cref="GroupWeaver.App.Tests.Settings.SettingsShellIntegrationTests"/> drives the
/// re-thread through <c>BuildSettingsViewModel</c>/<c>Save</c> rather than the modal
/// <c>ShowDialog</c> path, this fixture drives the Ist↔Plan switch through the shell's
/// own <see cref="ShellViewModel.OnDesignPlan"/> seam (the seam the WorkspaceView's
/// "Design plan" button invokes via its callback) — never a real button gesture, which is
/// not the switch logic under test.</para>
///
/// <para><b>Null-renderer-safe revalidation.</b> Headless tests construct the
/// <see cref="PlanViewModel"/> with NO renderer factory, so
/// <see cref="PlanViewModel.RevalidateAsync"/> must compute the Report (project → Build →
/// Evaluate → ComputeBelow) and simply skip the renderer push — exactly the contract that
/// lets the workspace's <c>ApplyRulesetAsync</c> run renderer-less.</para>
///
/// <para><b>RED until AP 4.2.2</b> adds <see cref="PlanViewModel"/>, the shell's
/// <c>OnDesignPlan</c>/dispose-discipline change, the <c>OnRulesetApplied</c> plan
/// re-thread, and the <c>IsDisposed</c> observability the dispose pins read. The existing
/// Workspace/Shell tests stay green (the shell ctor change is additive/optional-defaulted,
/// and the workspace's <c>DesignPlanCommand</c> defaults to disarmed with no callback).</para>
/// </summary>
public sealed class PlanModeTests
{
    private const string StubRootDn = "OU=Lab,DC=stub,DC=lab";
    private const string DemoRootDn = "OU=AGDLP-Demo,DC=weavedemo,DC=example";
    private const string PlanBaseOuDn = "OU=AGDLP-Lab,DC=agdlp,DC=lab";

    // === (1) the shell switch: Ist -> Plan -> Ist preserves the SAME workspace ===========

    /// <summary>
    /// <see cref="ShellViewModel.OnDesignPlan"/> from a settled workspace makes
    /// <see cref="ShellViewModel.CurrentStep"/> a <see cref="PlanViewModel"/> seeded with
    /// the workspace's root DN as the plan base OU (the empty-start default).
    /// </summary>
    [Fact(Timeout = 60_000)]
    public async Task OnDesignPlan_FromASettledWorkspace_SwitchesCurrentStepToAPlanViewModel()
    {
        var (shell, workspace) = await DemoShellWithLiveWorkspaceAsync();

        shell.OnDesignPlan(workspace);

        var plan = Assert.IsType<PlanViewModel>(shell.CurrentStep);
        Assert.Equal(workspace.RootDn, plan.BaseOuDn, Dn.Comparer);
        Assert.Empty(plan.Plan.Nodes); // empty-start only (no seed-from-Ist)
        Assert.Empty(plan.Plan.Edges);

        shell.Dispose();
    }

    /// <summary>
    /// The KEYSTONE round-trip: Back from the plan step returns the EXACT SAME
    /// <see cref="WorkspaceViewModel"/> instance (<see cref="Assert.Same(object?,object?)"/>),
    /// with its Ist snapshot, drawn graph, and selection intact — the workspace is never
    /// disposed nor reloaded on the switch (no second <c>LoadScopeAsync</c>).
    /// </summary>
    [Fact(Timeout = 60_000)]
    public async Task PlanBack_ReturnsTheSameWorkspaceInstance_IstIntact_NeverReloaded()
    {
        // A per-call factory so the workspace and the (later) plan step each get a DISTINCT
        // fake — the "still exactly one show" pin must read the WORKSPACE's own renderer,
        // never a count polluted by the plan rendering into a shared instance.
        var fakes = new List<FakeGraphRenderer>();
        IGraphRenderer Factory()
        {
            var f = new FakeGraphRenderer();
            fakes.Add(f);
            return f;
        }

        var shell = Shell(new DemoProvider(), Factory);
        var workspace = await DriveShellToWorkspaceAsync(shell);
        var workspaceFake = Assert.IsType<FakeGraphRenderer>(workspace.GraphRenderer);

        // Pre-switch state to prove survives the round-trip.
        var snapshotBefore = workspace.Snapshot;
        var graphBefore = workspace.Graph;
        Assert.NotNull(snapshotBefore);
        Assert.NotNull(graphBefore);
        var firstNodeDn = workspace.Graph!.Nodes.First().Dn;
        workspace.SelectedDn = firstNodeDn;
        Assert.Single(workspaceFake.ShownGraphs); // the one ctor load

        shell.OnDesignPlan(workspace);
        var plan = Assert.IsType<PlanViewModel>(shell.CurrentStep);

        // Back -> the SAME workspace instance, not a fresh one.
        plan.BackCommand.Execute(null);
        var afterBack = Assert.IsType<WorkspaceViewModel>(shell.CurrentStep);
        Assert.Same(workspace, afterBack);

        // Ist intact: same snapshot + same graph reference, selection preserved, and the
        // workspace never re-loaded (its own renderer still shows exactly the one ctor
        // graph — no reload, no second ShowGraphAsync).
        Assert.Same(snapshotBefore, afterBack.Snapshot);
        Assert.Same(graphBefore, afterBack.Graph);
        Assert.Equal(firstNodeDn, afterBack.SelectedDn, Dn.Comparer);
        Assert.Same(workspaceFake, afterBack.GraphRenderer);
        Assert.Single(workspaceFake.ShownGraphs);

        shell.Dispose();
    }

    /// <summary>
    /// The plan step builds its OWN renderer instance from the factory — NEVER the
    /// workspace's renderer (the two steps render independently; reusing the workspace's
    /// renderer would tear the Ist surface when the plan re-renders).
    /// </summary>
    [Fact(Timeout = 60_000)]
    public async Task PlanViewModel_BuildsItsOwnRenderer_NotTheWorkspaceRenderer()
    {
        // A factory that mints a distinct renderer per call: the plan's must differ from
        // the workspace's (each step calls the factory once for its own instance).
        var rendererCount = 0;
        IGraphRenderer Factory()
        {
            rendererCount++;
            return new FakeGraphRenderer();
        }

        var shell = Shell(new DemoProvider(), Factory);
        var workspace = await DriveShellToWorkspaceAsync(shell);
        var workspaceRenderer = workspace.GraphRenderer;
        Assert.NotNull(workspaceRenderer);

        shell.OnDesignPlan(workspace);
        var plan = Assert.IsType<PlanViewModel>(shell.CurrentStep);
        await plan.RevalidateAsync();

        Assert.NotNull(plan.GraphRenderer);
        Assert.NotSame(workspaceRenderer, plan.GraphRenderer);
        Assert.Equal(2, rendererCount); // one per step, never shared

        shell.Dispose();
    }

    // === (2) the empty plan evaluates to the RuleReport.Empty shape ======================

    /// <summary>
    /// A freshly constructed <see cref="PlanViewModel"/> starts with an EMPTY
    /// <see cref="PlanModel"/>; after <see cref="PlanViewModel.RevalidateAsync"/> its
    /// Report is the empty-plan shape: no findings, <see cref="PlanViewModel.HasViolations"/>
    /// false, and <see cref="PlanViewModel.HasUncheckedAreas"/> false — a plan is fully
    /// authored, so <see cref="RuleReport.UncheckedDns"/> is empty by construction.
    /// </summary>
    [Fact]
    public async Task EmptyPlan_AfterRevalidate_HasNoFindings_NoViolations_NoUncheckedAreas()
    {
        var plan = HeadlessPlan(); // no renderer factory => headless, null-renderer-safe

        Assert.Empty(plan.Plan.Nodes);
        Assert.Empty(plan.Plan.Edges);

        await plan.RevalidateAsync();

        Assert.Empty(plan.Report.Violations);
        Assert.Empty(plan.Report.UncheckedDns);
        Assert.Empty(plan.Violations);
        Assert.False(plan.HasViolations);
        Assert.False(
            plan.HasUncheckedAreas,
            "a plan is fully authored — UncheckedDns is empty by construction (no null-member groups)");

        plan.Dispose();
    }

    /// <summary>
    /// <see cref="PlanViewModel.RevalidateAsync"/> is null-renderer-safe: with no renderer
    /// it still projects the plan, builds the graph, and evaluates the Report — it simply
    /// skips the renderer push (no throw). The built graph carries the base-OU root node so
    /// the headless preview state is consistent with the workspace load path.
    /// </summary>
    [Fact]
    public async Task RevalidateAsync_WithNoRenderer_ComputesReportAndGraph_SkipsRenderWithoutThrowing()
    {
        var plan = HeadlessPlan();
        Assert.Null(plan.GraphRenderer); // no factory => no renderer

        var ex = await Record.ExceptionAsync(() => plan.RevalidateAsync());

        Assert.Null(ex);
        Assert.NotNull(plan.Snapshot); // projected
        Assert.NotNull(plan.Graph); // built
        Assert.Empty(plan.Report.Violations); // evaluated (empty plan)

        plan.Dispose();
    }

    /// <summary>
    /// Selection wiring: a node tap arriving over the plan's OWN renderer seam sets
    /// <see cref="PlanViewModel.SelectedDn"/> verbatim (DN strings flow uncanonicalized) —
    /// the same <c>NodeClicked -&gt; SelectedDn</c> contract the workspace has.
    /// </summary>
    [AvaloniaFact]
    public async Task NodeClicked_OverThePlanRenderer_SetsSelectedDn_Verbatim()
    {
        var fake = new FakeGraphRenderer();
        var plan = new PlanViewModel(
            PlanBaseOuDn,
            DefaultEffectiveRuleset(),
            graphRendererFactory: () => fake);
        await plan.RevalidateAsync();

        const string clickedDn = "CN=x\\, y,OU=AGDLP-Lab,DC=agdlp,DC=lab";
        fake.RaiseNodeClicked(clickedDn, "GlobalGroup");

        Assert.Equal(clickedDn, plan.SelectedDn);

        plan.Dispose();
    }

    // === (2b) finding-row jump (Tier 1 unification): JumpToFinding mirrors GapViewModel.JumpTo ===

    /// <summary>
    /// <see cref="PlanViewModel.JumpToFindingCommand"/> over a finding row (the Tier-1 unified
    /// finding-row affordance — the Plan findings row is now a Button binding this command, mirroring
    /// <see cref="GapViewModel.JumpToCommand"/>): it sets <see cref="PlanViewModel.SelectedDn"/> to the
    /// row's <c>PrimaryDn</c> AND frames the anchor on the plan's OWN graph via
    /// <see cref="IGraphRenderer.FocusAsync"/> with EXACTLY <c>[row.PrimaryDn]</c> (recorded by the
    /// fake — one call, one anchor). The matching <see cref="ViolationRowModel.IsActive"/> flips true
    /// via the existing <c>OnSelectedDnChanged → HighlightActiveRows</c> path (and every other row
    /// stays dark). The finding is an authored self-membership circular error, so its anchor is the
    /// group DN; identity is asserted by <c>PrimaryDn</c> (never a message string).
    /// </summary>
    [Fact(Timeout = 60_000)]
    public async Task JumpToFindingCommand_SetsSelectedDn_FocusesTheAnchorOnce_AndLightsTheMatchingRow()
    {
        var fake = new FakeGraphRenderer();
        var plan = new PlanViewModel(
            PlanBaseOuDn,
            DefaultEffectiveRuleset(),
            graphRendererFactory: () => fake);

        // Author a self-membership (A -> A): the default ruleset reports it as a circular finding
        // anchored on A (a real finding whose PrimaryDn is a known plan node). Must terminate.
        // The name PASSES the default naming rule (^GG_<Token>_<Token>) so the ONLY finding on this
        // DN is the circular error — a single, unambiguous row to jump to.
        var groupDn = await AddNodeAsync(plan, PlanCreatableKind.GlobalGroup, "GG_Jump_Self");
        plan.MemberParentRow = plan.GroupNodes.Single(r => Dn.Comparer.Equals(r.Dn, groupDn));
        plan.MemberChildRow = plan.Nodes.Single(r => Dn.Comparer.Equals(r.Dn, groupDn));
        await plan.AddMemberCommand.ExecuteAsync(null);
        Assert.True(plan.HasViolations);

        var row = Assert.Single(
            plan.Violations, r => Dn.Comparer.Equals(r.PrimaryDn, groupDn));

        await plan.JumpToFindingCommand.ExecuteAsync(row);

        // Selection carried verbatim to the row's anchor.
        Assert.Equal(row.PrimaryDn, plan.SelectedDn, Dn.Comparer);

        // FocusAsync called EXACTLY once with a collection equal to [row.PrimaryDn].
        var focused = Assert.Single(fake.FocusCalls);
        Assert.Equal(row.PrimaryDn, Assert.Single(focused), Dn.Comparer);

        // The matching row lit up via the existing highlight path; no other row is active.
        Assert.True(row.IsActive, "the jumped-to finding row must be active");
        Assert.All(
            plan.Violations.Where(r => !Dn.Comparer.Equals(r.PrimaryDn, groupDn)),
            r => Assert.False(r.IsActive, "a non-matching finding row must stay inactive"));

        plan.Dispose();
    }

    /// <summary>
    /// <see cref="PlanViewModel.JumpToFindingCommand"/> is null-safe: a null row is a no-op — no
    /// <see cref="PlanViewModel.SelectedDn"/> change and NO <see cref="IGraphRenderer.FocusAsync"/>
    /// call (the guard the command shares with <see cref="GapViewModel"/>'s jump). Starts with a live
    /// selection so a wrongful reset would be observable.
    /// </summary>
    [Fact(Timeout = 60_000)]
    public async Task JumpToFindingCommand_WithNullRow_IsANoOp_NoSelectionChange_NoFocus()
    {
        var fake = new FakeGraphRenderer();
        var plan = new PlanViewModel(
            PlanBaseOuDn,
            DefaultEffectiveRuleset(),
            graphRendererFactory: () => fake);

        var groupDn = await AddNodeAsync(plan, PlanCreatableKind.GlobalGroup, "GG_Jump_Null");
        plan.SelectedDn = groupDn; // a live selection the no-op must leave untouched
        var focusCallsBefore = fake.FocusCalls.Count;

        var ex = await Record.ExceptionAsync(() => plan.JumpToFindingCommand.ExecuteAsync(null));

        Assert.Null(ex);
        Assert.Equal(groupDn, plan.SelectedDn, Dn.Comparer); // unchanged
        Assert.Equal(focusCallsBefore, fake.FocusCalls.Count); // no new focus dispatch

        plan.Dispose();
    }

    /// <summary>
    /// A DISPOSED VM is a no-op on a stale-armed <see cref="PlanViewModel.JumpToFindingCommand"/>
    /// (RelayCommand.Execute ignores CanExecute, so the body's <c>IsDisposed</c> re-guard is the one
    /// that must drop it): no <see cref="IGraphRenderer.FocusAsync"/> call, no throw. Mirrors the
    /// disposed-guard the Gap/Workspace jumps share.
    /// </summary>
    [Fact(Timeout = 60_000)]
    public async Task JumpToFindingCommand_AfterDispose_IsANoOp_NoFocus()
    {
        var fake = new FakeGraphRenderer();
        var plan = new PlanViewModel(
            PlanBaseOuDn,
            DefaultEffectiveRuleset(),
            graphRendererFactory: () => fake);

        var groupDn = await AddNodeAsync(plan, PlanCreatableKind.GlobalGroup, "GG_Jump_Disposed");
        plan.MemberParentRow = plan.GroupNodes.Single(r => Dn.Comparer.Equals(r.Dn, groupDn));
        plan.MemberChildRow = plan.Nodes.Single(r => Dn.Comparer.Equals(r.Dn, groupDn));
        await plan.AddMemberCommand.ExecuteAsync(null);
        var row = Assert.Single(plan.Violations, r => Dn.Comparer.Equals(r.PrimaryDn, groupDn));

        plan.Dispose();
        var focusCallsBefore = fake.FocusCalls.Count;

        var ex = await Record.ExceptionAsync(() => plan.JumpToFindingCommand.ExecuteAsync(row));

        Assert.Null(ex);
        Assert.Equal(focusCallsBefore, fake.FocusCalls.Count); // disposed => no focus dispatch
    }

    // === (3) dispose discipline: round-trip never disposes; teardown disposes both =======

    /// <summary>
    /// Switching Ist → Plan → Ist must NEVER dispose the workspace (the regression that
    /// matters — a disposed workspace's cancelled token would kill the Ist load). Pinned
    /// directly via the additive <c>IsDisposed</c> flag AP 4.2.2 exposes on the VMs.
    /// </summary>
    [Fact(Timeout = 60_000)]
    public async Task RoundTrip_IstToPlanToIst_NeverDisposesTheWorkspace()
    {
        var (shell, workspace) = await DemoShellWithLiveWorkspaceAsync();

        shell.OnDesignPlan(workspace);
        var plan = Assert.IsType<PlanViewModel>(shell.CurrentStep);
        Assert.False(workspace.IsDisposed, "switching INTO Plan must not dispose the workspace we left");

        plan.BackCommand.Execute(null);
        Assert.Same(workspace, shell.CurrentStep);
        Assert.False(workspace.IsDisposed, "switching BACK to Ist must not dispose the workspace");

        shell.Dispose();
    }

    /// <summary>
    /// The dispose-discipline change: the shell now tracks BOTH the workspace and any
    /// created plan VM and disposes both at teardown (never just the current step). After a
    /// switch INTO Plan, the workspace is no longer the current step, yet shell teardown
    /// must still dispose it — and the plan step too.
    /// </summary>
    [Fact(Timeout = 60_000)]
    public async Task ShellDispose_AfterSwitchingIntoPlan_DisposesBothTheWorkspaceAndThePlan()
    {
        var (shell, workspace) = await DemoShellWithLiveWorkspaceAsync();

        shell.OnDesignPlan(workspace);
        var plan = Assert.IsType<PlanViewModel>(shell.CurrentStep);
        Assert.False(workspace.IsDisposed);
        Assert.False(plan.IsDisposed);

        shell.Dispose();

        // BOTH disposed — the workspace (not the current step) is NOT leaked, and the plan
        // (the current step) is disposed too.
        Assert.True(workspace.IsDisposed, "teardown must dispose the workspace even when it is not the current step");
        Assert.True(plan.IsDisposed, "teardown must dispose the plan step");
    }

    /// <summary>
    /// Belt-and-suspenders regression pin that needs NO <c>IsDisposed</c> surface: a
    /// workspace whose scope load is held in flight keeps its provider token uncancelled
    /// across an Ist → Plan → Ist round-trip (the round-trip never disposes it), and that
    /// SAME token is cancelled at shell teardown (teardown disposes the held-but-not-current
    /// workspace). This is the disposal regression observed purely behaviorally.
    /// </summary>
    [Fact(Timeout = 60_000)]
    public async Task GatedWorkspace_RoundTrip_KeepsTokenLive_TeardownCancelsIt()
    {
        await Task.CompletedTask; // xUnit Timeout requires an async test; the body is otherwise synchronous.
        var loadGate = new TaskCompletionSource<DirectorySnapshot>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var provider = StubProvider();
        provider.LoadScopeResult = loadGate.Task;
        var fake = new FakeGraphRenderer();
        var shell = ShellWithWorkspaceStep(provider, () => fake, out var workspace);

        Assert.True(workspace.IsLoading, "the gated load holds the token open");
        Assert.False(provider.LoadScopeToken.IsCancellationRequested);

        // Ist -> Plan -> Ist: the workspace is never disposed, so its in-flight token stays live.
        shell.OnDesignPlan(workspace);
        var plan = Assert.IsType<PlanViewModel>(shell.CurrentStep);
        plan.BackCommand.Execute(null);
        Assert.Same(workspace, shell.CurrentStep);
        Assert.False(
            provider.LoadScopeToken.IsCancellationRequested,
            "the round-trip must not dispose the workspace (its load token must stay live)");

        // Teardown disposes the workspace (whether or not it is the current step): the
        // held load token is finally cancelled.
        shell.Dispose();
        Assert.True(
            provider.LoadScopeToken.IsCancellationRequested,
            "shell teardown must dispose the workspace and cancel its in-flight load token");

        loadGate.TrySetCanceled();
    }

    // === (4) settings re-thread reaches a LIVE plan step =================================

    /// <summary>
    /// <see cref="ShellViewModel.OnRulesetApplied"/> with a live <see cref="PlanViewModel"/>
    /// re-threads it — mirrors the workspace re-thread (
    /// <see cref="GroupWeaver.App.Tests.Settings.SettingsShellIntegrationTests"/>): a
    /// settings Apply re-Evaluates the live plan against the flipped ruleset. The plan is
    /// authored with a GG←GG nesting edge that the DEFAULT ruleset allows (no finding);
    /// flipping the GG←GG matrix cell to Error must make that edge a new nesting error in
    /// the LIVE plan's Report, proving the re-thread reaches the plan's engine.
    /// </summary>
    [Fact(Timeout = 60_000)]
    public async Task SettingsApply_WithALivePlanStep_ReThreadsThePlan_FlippedGgGgCell_AddsTheNestingError()
    {
        var fake = new FakeGraphRenderer { View = new Avalonia.Controls.Border() };
        var shell = Shell(new DemoProvider(), () => fake);
        var workspace = await DriveShellToWorkspaceAsync(shell);

        shell.OnDesignPlan(workspace);
        var plan = Assert.IsType<PlanViewModel>(shell.CurrentStep);

        // Author a GG←GG nesting edge directly in the plan model, then revalidate: under
        // the default ruleset GG←GG is allowed, so NO nesting finding exists yet.
        var parent = plan.Plan.AddNode(PlanCreatableKind.GlobalGroup, "GG_IT_Admins");
        var child = plan.Plan.AddNode(PlanCreatableKind.GlobalGroup, "GG_IT_Backup");
        plan.Plan.AddEdge(parent.Dn, child.Dn);
        await plan.RevalidateAsync();
        Assert.DoesNotContain(plan.Report.Violations, v => v.RuleId == RuleIds.Nesting);

        // Flip the GG←GG cell to Error via the shell's settings seam and Save: the shell's
        // OnRulesetApplied must re-thread the LIVE plan, surfacing the GG←GG edge as a
        // brand-new nesting error in the plan's report.
        var settings = shell.BuildSettingsViewModel();
        settings.Nesting.Cell(AdObjectKind.GlobalGroup, AdObjectKind.GlobalGroup).Choice = CellChoice.Error;
        Assert.True(settings.Save(), "the flipped-cell mirror is valid and must Save");

        var finding = Assert.Single(plan.Report.Violations, v => v.RuleId == RuleIds.Nesting);
        Assert.Equal(parent.Dn, finding.Dns[0], Dn.Comparer);
        Assert.Equal(child.Dn, finding.Dns[1], Dn.Comparer);
        Assert.True(plan.HasViolations);

        // The sidebar projection mirrors the fresh report (the new error is a row).
        Assert.Contains(
            plan.Violations,
            r => r.Severity == RuleSeverity.Error && Dn.Comparer.Equals(r.PrimaryDn, parent.Dn));

        shell.Dispose();
    }

    /// <summary>
    /// <see cref="PlanViewModel.ApplyRulesetAsync"/> directly (the seam the shell's
    /// re-thread calls): over an authored GG←GG edge, swapping in the flipped ruleset
    /// re-Evaluates the plan and produces the new nesting error — null-renderer-safe, no
    /// throw, a fresh Report instance.
    /// </summary>
    [Fact]
    public async Task PlanApplyRulesetAsync_ReEvaluatesTheAuthoredPlan_UnderTheFlippedRuleset()
    {
        var plan = HeadlessPlan();
        var parent = plan.Plan.AddNode(PlanCreatableKind.GlobalGroup, "GG_IT_Admins");
        var child = plan.Plan.AddNode(PlanCreatableKind.GlobalGroup, "GG_IT_Backup");
        plan.Plan.AddEdge(parent.Dn, child.Dn);
        await plan.RevalidateAsync();

        var reportBefore = plan.Report;
        Assert.DoesNotContain(plan.Report.Violations, v => v.RuleId == RuleIds.Nesting);

        await plan.ApplyRulesetAsync(FlippedGgGgRuleset());

        Assert.NotSame(reportBefore, plan.Report); // a fresh evaluation
        Assert.Single(plan.Report.Violations, v => v.RuleId == RuleIds.Nesting);

        plan.Dispose();
    }

    // === (5) the workspace exposes a Design-plan command wired to the shell seam =========

    /// <summary>
    /// The workspace exposes a <c>DesignPlanCommand</c> (the "Design plan" header button
    /// binds it). Driven from the live shell, executing it switches the shell into a
    /// <see cref="PlanViewModel"/> — the same outcome as the direct
    /// <see cref="ShellViewModel.OnDesignPlan"/> seam, proving the callback is wired.
    /// </summary>
    [Fact(Timeout = 60_000)]
    public async Task WorkspaceDesignPlanCommand_FromTheLiveShell_SwitchesIntoPlanMode()
    {
        var (shell, workspace) = await DemoShellWithLiveWorkspaceAsync();

        Assert.True(
            workspace.DesignPlanCommand.CanExecute(null),
            "Design plan must be armed on a settled workspace");
        workspace.DesignPlanCommand.Execute(null);

        Assert.IsType<PlanViewModel>(shell.CurrentStep);

        shell.Dispose();
    }

    // === (6) #122 plan KEEP-ALIVE: Back parks (never disposes); re-entry re-enters the same =====

    /// <summary>
    /// THE core keep-alive fix (the silent-data-loss-on-Back regression): drive Workspace → Plan,
    /// author content (a node + a self-edge through the plan's add commands), then
    /// <see cref="PlanViewModel.BackCommand"/>. Back must PARK the plan (NOT dispose it) and return
    /// the SAME workspace instance as <see cref="ShellViewModel.CurrentStep"/>; the plan VM stays
    /// alive (<c>IsDisposed == false</c>). Re-entering via <see cref="ShellViewModel.OnDesignPlan"/>
    /// over the SAME workspace root re-enters the EXACT SAME <see cref="PlanViewModel"/> instance
    /// (<see cref="Assert.Same(object?,object?)"/>) with its authored Nodes/Edges intact — never a
    /// fresh empty plan. Before keep-alive this round-trip disposed the plan and rebuilt it empty,
    /// silently dropping the user's authored structure.
    /// </summary>
    [Fact(Timeout = 60_000)]
    public async Task KeepAlive_RoundTrip_ParksPlanNotDisposed_ReEntersSameInstance_ContentIntact()
    {
        var (shell, workspace) = await DemoShellWithLiveWorkspaceAsync();

        shell.OnDesignPlan(workspace);
        var plan = Assert.IsType<PlanViewModel>(shell.CurrentStep);

        // Author content through the plan's own add commands (the production seam).
        var groupDn = await AddNodeAsync(plan, PlanCreatableKind.GlobalGroup, "GG_KeepAlive");
        var childDn = await AddNodeAsync(plan, PlanCreatableKind.GlobalGroup, "GG_KeepAlive_Child");
        plan.MemberParentRow = plan.GroupNodes.Single(r => Dn.Comparer.Equals(r.Dn, groupDn));
        plan.MemberChildRow = plan.Nodes.Single(r => Dn.Comparer.Equals(r.Dn, childDn));
        await plan.AddMemberCommand.ExecuteAsync(null);

        var nodesBefore = plan.Plan.Nodes.Count;
        var edgesBefore = plan.Plan.Edges.Count;
        Assert.Equal(2, nodesBefore);
        Assert.Equal(1, edgesBefore);

        // Back: the plan is PARKED, not disposed; the SAME workspace returns.
        plan.BackCommand.Execute(null);
        Assert.Same(workspace, shell.CurrentStep);
        Assert.False(
            plan.IsDisposed,
            "Back must KEEP the plan alive (keep-alive #122) — the authored content must survive");

        // Re-enter Plan over the SAME workspace root: the SAME instance, content intact.
        shell.OnDesignPlan(workspace);
        var planAgain = Assert.IsType<PlanViewModel>(shell.CurrentStep);
        Assert.Same(plan, planAgain);
        Assert.False(planAgain.IsDisposed);
        Assert.Equal(nodesBefore, planAgain.Plan.Nodes.Count);
        Assert.Equal(edgesBefore, planAgain.Plan.Edges.Count);
        Assert.True(planAgain.Plan.TryGetNode(groupDn, out _), "the authored group survives the round-trip");
        Assert.True(planAgain.Plan.TryGetNode(childDn, out _), "the authored child survives the round-trip");
        Assert.Contains(
            planAgain.Plan.Edges,
            e => Dn.Comparer.Equals(e.ParentDn, groupDn) && Dn.Comparer.Equals(e.ChildDn, childDn));

        shell.Dispose();
    }

    /// <summary>
    /// Keep-alive re-entry parks the workspace surface we Back INTO, then re-mounts the kept-alive
    /// plan as the current step — it must NEVER re-build a fresh empty plan as long as the base OU
    /// is unchanged. Pinned at the seam: two <see cref="ShellViewModel.OnDesignPlan"/> calls with a
    /// Back between them yield <see cref="Assert.Same(object?,object?)"/> the same plan, and its
    /// renderer is the same alive instance (never disposed).
    /// </summary>
    [Fact(Timeout = 60_000)]
    public async Task KeepAlive_ReEntry_SameBaseOu_ReusesTheSamePlanRendererInstance()
    {
        var rendererCount = 0;
        IGraphRenderer Factory()
        {
            rendererCount++;
            return new FakeGraphRenderer();
        }

        var shell = Shell(new DemoProvider(), Factory);
        var workspace = await DriveShellToWorkspaceAsync(shell);

        shell.OnDesignPlan(workspace);
        var plan = Assert.IsType<PlanViewModel>(shell.CurrentStep);
        var planRenderer = plan.GraphRenderer;
        Assert.NotNull(planRenderer);
        var rendererCountAfterFirstPlan = rendererCount; // workspace + plan = 2

        plan.BackCommand.Execute(null);
        Assert.Same(workspace, shell.CurrentStep);

        shell.OnDesignPlan(workspace);
        var planAgain = Assert.IsType<PlanViewModel>(shell.CurrentStep);

        Assert.Same(plan, planAgain);
        Assert.Same(planRenderer, planAgain.GraphRenderer); // the SAME renderer, never rebuilt
        Assert.Equal(rendererCountAfterFirstPlan, rendererCount); // re-entry minted NO new renderer
        Assert.False(((FakeGraphRenderer)planAgain.GraphRenderer!).Disposed);

        shell.Dispose();
    }

    /// <summary>
    /// Base-OU change DISCARDS + rebuilds (the only sanctioned reset, besides teardown): after a
    /// keep-alive round-trip with authored content, calling <see cref="ShellViewModel.OnDesignPlan"/>
    /// with a workspace that has a DIFFERENT <see cref="WorkspaceViewModel.RootDn"/> (the path a fresh
    /// RootChosen / a Reload-scope takes — both yield a new <see cref="WorkspaceViewModel"/> with a new
    /// RootDn) must build a DIFFERENT, fresh, EMPTY plan and dispose the old one. A plan is bound to
    /// its base OU, so it cannot survive a base-OU change.
    /// </summary>
    [Fact(Timeout = 60_000)]
    public async Task KeepAlive_BaseOuChange_DisposesOldPlan_RebuildsFreshEmptyPlan()
    {
        var provider = new DemoProvider();
        var shell = Shell(provider, () => new FakeGraphRenderer());
        var workspace = await DriveShellToWorkspaceAsync(shell);

        shell.OnDesignPlan(workspace);
        var plan = Assert.IsType<PlanViewModel>(shell.CurrentStep);

        // Author content into the first plan, then Back (keep-alive parks it).
        plan.Plan.AddNode(PlanCreatableKind.GlobalGroup, "GG_OldRoot");
        await plan.RevalidateAsync();
        Assert.NotEmpty(plan.Plan.Nodes);
        plan.BackCommand.Execute(null);
        Assert.False(plan.IsDisposed); // kept alive by the round-trip

        // A second workspace rooted at a DIFFERENT OU — the shape a fresh RootChosen / Reload-scope
        // produces (a new WorkspaceViewModel with a new RootDn). Constructed directly (deterministic),
        // never re-driving the picker; a fresh temp-dir UiStateStore (#124 — never real %APPDATA%).
        var otherRoot = new AdObject
        {
            Dn = "OU=Other-Demo,DC=weavedemo,DC=example",
            Kind = AdObjectKind.OrganizationalUnit,
            Name = "Other-Demo",
        };
        using var otherWorkspace = new WorkspaceViewModel(
            provider,
            otherRoot,
            await provider.ConnectAsync(),
            webView2Missing: false,
            () => new FakeGraphRenderer(),
            uiStateStore: new UiStateStore(
                System.IO.Directory.CreateTempSubdirectory("groupweaver-keepalive-otherroot-").FullName));
        Assert.NotEqual(workspace.RootDn, otherWorkspace.RootDn, Dn.Comparer); // a genuinely different base OU

        // Design-plan over the DIFFERENT-root workspace: the old plan is superseded.
        shell.OnDesignPlan(otherWorkspace);
        var freshPlan = Assert.IsType<PlanViewModel>(shell.CurrentStep);

        Assert.NotSame(plan, freshPlan); // a brand-new plan VM
        Assert.True(plan.IsDisposed, "a base-OU change must dispose the superseded (different-base-OU) plan");
        Assert.Empty(freshPlan.Plan.Nodes); // fresh + empty
        Assert.Empty(freshPlan.Plan.Edges);
        Assert.Equal(otherWorkspace.RootDn, freshPlan.BaseOuDn, Dn.Comparer); // bound to the NEW base OU

        shell.Dispose();
    }

    /// <summary>
    /// The "New plan" reset (#122): with authored content, <see cref="PlanViewModel.NewPlanCommand"/>
    /// clears <see cref="PlanModel.Nodes"/>/<see cref="PlanModel.Edges"/> in place (the same instance —
    /// never disposed), keeps the <see cref="PlanViewModel.BaseOuDn"/>, re-gates the Export command to
    /// disabled at zero nodes, and re-runs the live validation (a fresh empty Report). This is the one
    /// way to empty a kept-alive plan on purpose.
    /// </summary>
    [Fact(Timeout = 60_000)]
    public async Task NewPlan_ClearsContent_KeepsBaseOu_ReGatesExportDisabled_RevalidatesEmpty()
    {
        var plan = HeadlessPlan();
        // Arm the export seam so CanExportPlanScript is gated ONLY by the node count (proving the
        // re-gate to disabled is the zero-node arm, not a missing-dialogs arm). UseExportFileDialogs
        // re-gates the command, so it tracks the node count from here on.
        plan.UseExportFileDialogs(new FakeExportDialogs());

        var baseOuBefore = plan.BaseOuDn;

        // Author content through the production add commands (which re-gate Export across the
        // zero-node boundary): a group + a user + a membership.
        var groupDn = await AddNodeAsync(plan, PlanCreatableKind.GlobalGroup, "GG_Reset");
        var userDn = await AddNodeAsync(plan, PlanCreatableKind.User, "Reset User");
        plan.MemberParentRow = plan.GroupNodes.Single(r => Dn.Comparer.Equals(r.Dn, groupDn));
        plan.MemberChildRow = plan.Nodes.Single(r => Dn.Comparer.Equals(r.Dn, userDn));
        await plan.AddMemberCommand.ExecuteAsync(null);
        Assert.NotEmpty(plan.Plan.Nodes);
        Assert.NotEmpty(plan.Plan.Edges);
        Assert.True(
            plan.ExportPlanScriptCommand.CanExecute(null),
            "with nodes + dialogs the export command is armed before the reset");

        await plan.NewPlanCommand.ExecuteAsync(null);

        // Content gone, base OU kept, the SAME instance (NewPlan never disposes).
        Assert.Empty(plan.Plan.Nodes);
        Assert.Empty(plan.Plan.Edges);
        Assert.Equal(baseOuBefore, plan.BaseOuDn, Dn.Comparer);
        Assert.False(plan.IsDisposed);

        // Export re-gated to disabled at zero nodes; validation re-ran to the empty-plan shape.
        Assert.False(
            plan.ExportPlanScriptCommand.CanExecute(null),
            "NewPlan must re-gate Export to disabled at zero nodes");
        Assert.Empty(plan.Report.Violations);
        Assert.False(plan.HasViolations);
        Assert.Null(plan.EditError);
        Assert.Null(plan.SelectedNodeRow);
        Assert.Null(plan.SelectedDn);

        plan.Dispose();
    }

    /// <summary>
    /// No double-track / dispose-once: after SEVERAL Workspace↔Plan round-trips (each Back parks the
    /// kept-alive plan, each re-entry returns the SAME instance), the shell must still track the plan
    /// exactly once — so shell teardown disposes it exactly once. Observed via the plan renderer's
    /// idempotent <c>Disposed</c> flag PLUS the structural guarantee that re-entry never mints a new
    /// renderer (so there is only ever ONE plan to dispose). The workspace is disposed too.
    /// </summary>
    [Fact(Timeout = 60_000)]
    public async Task KeepAlive_MultipleRoundTrips_ShellTeardownDisposesThePlanOnce()
    {
        var rendererCount = 0;
        IGraphRenderer Factory()
        {
            rendererCount++;
            return new FakeGraphRenderer();
        }

        var shell = Shell(new DemoProvider(), Factory);
        var workspace = await DriveShellToWorkspaceAsync(shell);

        shell.OnDesignPlan(workspace);
        var plan = Assert.IsType<PlanViewModel>(shell.CurrentStep);
        var planRenderer = (FakeGraphRenderer)plan.GraphRenderer!;
        var rendererCountAfterFirstPlan = rendererCount;

        // Three full round-trips — every re-entry must reuse the SAME plan (no second renderer).
        for (var i = 0; i < 3; i++)
        {
            plan.BackCommand.Execute(null);
            Assert.Same(workspace, shell.CurrentStep);
            shell.OnDesignPlan(workspace);
            Assert.Same(plan, shell.CurrentStep);
        }

        Assert.True(
            rendererCount == rendererCountAfterFirstPlan,
            "no round-trip may mint a second plan renderer (the plan is kept alive, not rebuilt)");
        Assert.False(planRenderer.Disposed);

        // Teardown disposes BOTH the workspace and the (single, kept-alive) plan exactly once.
        shell.Dispose();
        Assert.True(workspace.IsDisposed, "teardown disposes the workspace");
        Assert.True(plan.IsDisposed, "teardown disposes the kept-alive plan");
        Assert.True(planRenderer.Disposed, "teardown disposes the kept-alive plan's renderer");

        // A second teardown is a no-op (idempotent overall — proves no double-track).
        var ex = Record.Exception(() => shell.Dispose());
        Assert.Null(ex);
    }

    // === helpers ========================================================================

    /// <summary>The embedded default with ONLY the GG←GG matrix cell flipped to Error —
    /// the one-cell lever (mirrors <c>SettingsShellIntegrationTests.FlippedGgGgRuleset</c>).</summary>
    private static Ruleset FlippedGgGgRuleset()
    {
        var d = RulesetLoader.LoadDefault();
        var matrix = d.Nesting.Matrix.ToDictionary(
            row => row.Key,
            row => (IReadOnlyDictionary<AdObjectKind, NestingCell>)new Dictionary<AdObjectKind, NestingCell>(row.Value),
            EqualityComparer<AdObjectKind>.Default);

        var ggRow = new Dictionary<AdObjectKind, NestingCell>(matrix[AdObjectKind.GlobalGroup])
        {
            [AdObjectKind.GlobalGroup] = new NestingCell(false, RuleSeverity.Error),
        };
        matrix[AdObjectKind.GlobalGroup] = ggRow;

        return d with { Nesting = d.Nesting with { Matrix = matrix } };
    }

    private static EffectiveRuleset DefaultEffectiveRuleset() =>
        new(RulesetLoader.LoadDefault(), FromUserFile: false, []);

    /// <summary>A headless plan VM (no renderer factory): RevalidateAsync must be
    /// null-renderer-safe. Rooted at the lab base OU, default ruleset.</summary>
    private static PlanViewModel HeadlessPlan() =>
        new(PlanBaseOuDn, DefaultEffectiveRuleset());

    /// <summary>Authors a node through the add-object COMMAND (the production seam, mirroring
    /// <c>PlanModeEditorTests.AddNodeAsync</c>) so the export-gate re-evaluation fires across the
    /// zero-node boundary; returns its DN.</summary>
    private static async Task<string> AddNodeAsync(
        PlanViewModel plan, PlanCreatableKind kind, string name, string? sam = null)
    {
        plan.NewObjectKind = kind;
        plan.NewObjectName = name;
        plan.NewObjectSam = sam ?? string.Empty;
        await plan.AddObjectCommand.ExecuteAsync(null);
        Assert.Null(plan.EditError); // the helper authors only valid nodes
        return plan.Plan.FormDn(name);
    }

    /// <summary>A shell over a provider with the WebView2 probe forced present (never the
    /// live registry — that would make the banner machine-dependent) and a temp-dir
    /// <see cref="RulesetLocator"/> seam (never real %APPDATA% from a test).</summary>
    private static ShellViewModel Shell(IDirectoryProvider provider) =>
        Shell(provider, graphRendererFactory: null);

    private static ShellViewModel Shell(
        IDirectoryProvider provider, Func<IGraphRenderer>? graphRendererFactory)
    {
        var locator = new RulesetLocator(
            System.IO.Directory.CreateTempSubdirectory("groupweaver-plan-mode-tests-").FullName);
        return new ShellViewModel(
            _ => provider,
            new StartupOptions(Demo: false),
            new WebView2RuntimeStatus(IsInstalled: true, Version: "test"),
            graphRendererFactory,
            locator.LoadEffective(),
            locator);
    }

    /// <summary>A shell whose CurrentStep is already a workspace over the stub provider
    /// (skips the Connect/PickRoot drive — used by the gated-token disposal pin).</summary>
    private static ShellViewModel ShellWithWorkspaceStep(
        StubDirectoryProvider provider,
        Func<IGraphRenderer> rendererFactory,
        out WorkspaceViewModel workspace)
    {
        var shell = Shell(provider, rendererFactory);
        workspace = DriveShellToWorkspaceStepSync(shell, provider);
        return shell;
    }

    /// <summary>Connect (demo stub) → pick the single stub candidate → load. The stub's
    /// gated LoadScopeResult keeps the workspace IsLoading; the workspace step is returned
    /// without awaiting Initialization.</summary>
    private static WorkspaceViewModel DriveShellToWorkspaceStepSync(
        ShellViewModel shell, StubDirectoryProvider provider)
    {
        provider.RootCandidatesResult = Task.FromResult<IReadOnlyList<AdObject>>(
        [
            new AdObject { Dn = StubRootDn, Kind = AdObjectKind.OrganizationalUnit, Name = "Lab" },
        ]);

        var connect = Assert.IsType<ConnectionViewModel>(shell.CurrentStep);
        connect.ConnectDemoCommand.Execute(null);
        var picker = Assert.IsType<RootPickerViewModel>(shell.CurrentStep);
        picker.LoadCandidates.GetAwaiter().GetResult();
        picker.SelectedCandidate = picker.Candidates[0];
        picker.LoadRootCommand.Execute(null);
        return Assert.IsType<WorkspaceViewModel>(shell.CurrentStep);
    }

    /// <summary>Connect → pick the demo root OU → load, awaiting the settled workspace.</summary>
    private static async Task<WorkspaceViewModel> DriveShellToWorkspaceAsync(ShellViewModel shell)
    {
        var connect = Assert.IsType<ConnectionViewModel>(shell.CurrentStep);
        await connect.ConnectDemoCommand.ExecuteAsync(null);
        var picker = Assert.IsType<RootPickerViewModel>(shell.CurrentStep);
        await picker.LoadCandidates;
        picker.SelectedCandidate = picker.Candidates.First(c => Dn.Comparer.Equals(c.Dn, DemoRootDn));
        picker.LoadRootCommand.Execute(null);
        var workspace = Assert.IsType<WorkspaceViewModel>(shell.CurrentStep);
        await workspace.Initialization;
        return workspace;
    }

    private static async Task<(ShellViewModel Shell, WorkspaceViewModel Workspace)>
        DemoShellWithLiveWorkspaceAsync()
    {
        var shell = Shell(new DemoProvider(), () => new FakeGraphRenderer());
        var workspace = await DriveShellToWorkspaceAsync(shell);
        return (shell, workspace);
    }

    private static StubDirectoryProvider StubProvider() =>
        new(Task.FromResult(new DirectoryConnection("stub directory", 5)))
        {
            LoadScopeResult = Task.FromResult(new DirectorySnapshot()),
        };
}
