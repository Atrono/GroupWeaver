using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

using GroupWeaver.App.Graph;
using GroupWeaver.App.Rules;
using GroupWeaver.App.Startup;
using GroupWeaver.App.Tests.Fakes;
using GroupWeaver.App.ViewModels;
using GroupWeaver.Core.Model;
using GroupWeaver.Core.Providers;
using GroupWeaver.Providers;

using Xunit;

namespace GroupWeaver.App.Tests;

/// <summary>
/// Pins ADR-015 Slice 8 (#66): Gap mode becomes REACHABLE from Plan mode. The
/// <see cref="PlanViewModel"/> grows a <c>GapAnalysisCommand</c> the shell arms (via
/// <c>UseGapAnalysisCallback</c>) when it creates the plan step, and the shell grows the
/// <see cref="ShellViewModel.OnGapAnalysis"/> seam that switches <see cref="ShellViewModel.CurrentStep"/>
/// into a <see cref="GapViewModel"/> seeded with the workspace's borrowed Ist <c>Snapshot</c>, the
/// plan's <see cref="PlanViewModel.Plan"/> (<c>PlanModel</c>), and <see cref="WorkspaceViewModel.RootDn"/>.
///
/// <para><b>The deterministic switch seam.</b> Exactly like <see cref="PlanModeTests"/> drives the
/// Ist↔Plan switch through the shell's <see cref="ShellViewModel.OnDesignPlan"/> seam rather than a
/// real button gesture, this fixture drives the Plan→Gap switch through the shell's
/// <see cref="ShellViewModel.OnGapAnalysis"/> seam (the seam the PlanView's "Gap analysis" button
/// invokes via the callback the shell installs in <see cref="ShellViewModel.OnDesignPlan"/>) — never
/// a real gesture, which is not the switch logic under test.</para>
///
/// <para><b>Back keystone + dispose discipline.</b> The gap's <see cref="GapViewModel.BackCommand"/>
/// returns the SAME (never-disposed) <see cref="PlanViewModel"/> instance, and shell teardown
/// disposes the Workspace AND the Plan AND the Gap each exactly once — mirroring
/// <see cref="PlanModeTests"/>'s <c>ShellDispose_AfterSwitchingIntoPlan_DisposesBoth...</c>. Entering
/// Gap never disposes the Plan.</para>
///
/// <para><b>The gate.</b> <see cref="ShellViewModel.OnGapAnalysis"/> is a no-op when the workspace
/// has not loaded yet (<see cref="WorkspaceViewModel.Snapshot"/> is null) — a gap is meaningful only
/// against a loaded Ist. (<c>BaseOuDn == RootDn</c> holds by construction since
/// <see cref="ShellViewModel.OnDesignPlan"/> seeds the plan's base OU = the workspace root, so the
/// snapshot-null arm is the testable half of the gate.)</para>
///
/// <para><b>RED until Slice 8</b> adds <see cref="PlanViewModel.GapAnalysisCommand"/> /
/// <c>UseGapAnalysisCallback</c>, the shell's <c>OnGapAnalysis</c> seam, and the
/// <c>OnDesignPlan</c> callback install. The existing Workspace/Plan/Shell tests stay green (every
/// addition is additive; the plan's <c>GapAnalysisCommand</c> defaults to disarmed with no
/// callback).</para>
/// </summary>
public sealed class GapShellTests
{
    private const string StubRootDn = "OU=Lab,DC=stub,DC=lab";
    private const string DemoRootDn = "OU=AGDLP-Demo,DC=weavedemo,DC=example";

    // === (1) the entry command is armed once Plan mode is reached =========================

    /// <summary>
    /// From a live shell driven Connect→Picker→Workspace→Plan (the workspace's
    /// <c>DesignPlanCommand</c> into Plan mode), the plan's <see cref="PlanViewModel.GapAnalysisCommand"/>
    /// is ARMED — the shell installed the gap callback in <see cref="ShellViewModel.OnDesignPlan"/>,
    /// mirroring how the workspace's Design-plan callback is installed in <c>OnRootChosen</c>. (A
    /// fresh plan with no callback would report <c>CanExecute</c> false.)
    /// </summary>
    [Fact(Timeout = 60_000)]
    public async Task PlanGapAnalysisCommand_AfterShellSwitchedIntoPlan_IsArmed()
    {
        var (shell, workspace) = await DemoShellWithLiveWorkspaceAsync();

        shell.OnDesignPlan(workspace);
        var plan = Assert.IsType<PlanViewModel>(shell.CurrentStep);

        Assert.True(
            plan.GapAnalysisCommand.CanExecute(null),
            "the shell must arm the plan's Gap-analysis command when it creates the plan step");

        shell.Dispose();
    }

    /// <summary>
    /// Executing the armed <see cref="PlanViewModel.GapAnalysisCommand"/> (the production seam the
    /// PlanView button binds) switches <see cref="ShellViewModel.CurrentStep"/> to a
    /// <see cref="GapViewModel"/> seeded at the workspace's <see cref="WorkspaceViewModel.RootDn"/>
    /// — the same outcome as the direct <see cref="ShellViewModel.OnGapAnalysis"/> seam, proving the
    /// callback is wired.
    /// </summary>
    [Fact(Timeout = 60_000)]
    public async Task PlanGapAnalysisCommand_FromTheLiveShell_SwitchesIntoGapMode()
    {
        var (shell, workspace) = await DemoShellWithLiveWorkspaceAsync();

        shell.OnDesignPlan(workspace);
        var plan = Assert.IsType<PlanViewModel>(shell.CurrentStep);
        plan.GapAnalysisCommand.Execute(null);

        var gap = Assert.IsType<GapViewModel>(shell.CurrentStep);
        Assert.Equal(workspace.RootDn, gap.RootDn, Dn.Comparer);

        shell.Dispose();
    }

    // === (2) the OnGapAnalysis seam seeds the gap from the workspace + plan ===============

    /// <summary>
    /// <see cref="ShellViewModel.OnGapAnalysis"/> from a settled workspace + its plan makes
    /// <see cref="ShellViewModel.CurrentStep"/> a <see cref="GapViewModel"/> seeded with the
    /// workspace's <see cref="WorkspaceViewModel.RootDn"/> (the gap's <see cref="GapViewModel.RootDn"/>)
    /// and — proven AFTER a refresh — diffing the SAME borrowed live Ist the workspace holds: the
    /// gap's computed diff sees the workspace snapshot's objects as the Ist side. The gap borrows
    /// the workspace <c>Snapshot</c> instance (never re-loads), so a known workspace-Ist DN surfaces
    /// in the gap's union after <see cref="GapViewModel.RefreshAsync"/>.
    /// </summary>
    [Fact(Timeout = 60_000)]
    public async Task OnGapAnalysis_FromASettledWorkspaceAndItsPlan_SeedsGapFromIstAndRoot()
    {
        var (shell, workspace) = await DemoShellWithLiveWorkspaceAsync();
        Assert.NotNull(workspace.Snapshot); // the live Ist the gap must borrow

        shell.OnDesignPlan(workspace);
        var plan = Assert.IsType<PlanViewModel>(shell.CurrentStep);

        shell.OnGapAnalysis(plan, workspace);

        var gap = Assert.IsType<GapViewModel>(shell.CurrentStep);
        Assert.Equal(workspace.RootDn, gap.RootDn, Dn.Comparer);

        // The gap borrows the workspace's live Ist: after a refresh, an Ist-side object DN (taken
        // from the workspace's own snapshot) resolves in the gap's union. The plan is empty, so
        // every Ist object is a Removed delta materialized into the union.
        await gap.RefreshAsync();
        var anIstDn = workspace.Snapshot!.Objects.First().Dn;
        Assert.NotNull(gap.Snapshot);
        Assert.True(
            gap.Snapshot!.TryGetObject(anIstDn, out _),
            "the gap must diff against the SAME borrowed workspace Ist (its objects appear in the union)");

        shell.Dispose();
    }

    /// <summary>
    /// The KEYSTONE round-trip: <see cref="GapViewModel.BackCommand"/> returns the EXACT SAME
    /// <see cref="PlanViewModel"/> instance (<see cref="Assert.Same(object?,object?)"/>) the gap was
    /// entered from — and that plan is never disposed on the switch (its authored model survives).
    /// </summary>
    [Fact(Timeout = 60_000)]
    public async Task GapBack_ReturnsTheSamePlanInstance_PlanNeverDisposed()
    {
        var (shell, workspace) = await DemoShellWithLiveWorkspaceAsync();

        shell.OnDesignPlan(workspace);
        var plan = Assert.IsType<PlanViewModel>(shell.CurrentStep);

        shell.OnGapAnalysis(plan, workspace);
        var gap = Assert.IsType<GapViewModel>(shell.CurrentStep);
        Assert.False(plan.IsDisposed, "entering Gap must not dispose the Plan it was launched from");

        gap.BackCommand.Execute(null);

        var afterBack = Assert.IsType<PlanViewModel>(shell.CurrentStep);
        Assert.Same(plan, afterBack);
        Assert.False(afterBack.IsDisposed, "Back from Gap must not dispose the Plan");

        shell.Dispose();
    }

    // === (3) dispose discipline: teardown disposes Workspace + Plan + Gap each once =======

    /// <summary>
    /// Entering Gap must NEVER dispose the Plan (the regression that matters — a disposed plan's
    /// cancelled token would kill its live-validate render). Pinned directly via the additive
    /// <see cref="PlanViewModel.IsDisposed"/> flag, mirroring
    /// <see cref="PlanModeTests"/>'s round-trip pins.
    /// </summary>
    [Fact(Timeout = 60_000)]
    public async Task EnteringGap_NeverDisposesThePlan()
    {
        var (shell, workspace) = await DemoShellWithLiveWorkspaceAsync();

        shell.OnDesignPlan(workspace);
        var plan = Assert.IsType<PlanViewModel>(shell.CurrentStep);

        shell.OnGapAnalysis(plan, workspace);
        Assert.IsType<GapViewModel>(shell.CurrentStep);

        Assert.False(plan.IsDisposed, "switching INTO Gap must not dispose the plan we left");

        shell.Dispose();
    }

    /// <summary>
    /// The dispose-discipline change extended to the Gap step: after Workspace→Plan→Gap the shell
    /// tracks ALL THREE steps and disposes each exactly once at teardown (never just the current
    /// step). After a switch into Gap, neither the workspace nor the plan is the current step, yet
    /// shell teardown must still dispose them — and the gap step too. Mirrors
    /// <see cref="PlanModeTests"/>'s <c>ShellDispose_AfterSwitchingIntoPlan_DisposesBoth...</c>.
    /// </summary>
    [Fact(Timeout = 60_000)]
    public async Task ShellDispose_AfterSwitchingIntoGap_DisposesWorkspaceAndPlanAndGap()
    {
        var (shell, workspace) = await DemoShellWithLiveWorkspaceAsync();

        shell.OnDesignPlan(workspace);
        var plan = Assert.IsType<PlanViewModel>(shell.CurrentStep);
        shell.OnGapAnalysis(plan, workspace);
        var gap = Assert.IsType<GapViewModel>(shell.CurrentStep);

        Assert.False(workspace.IsDisposed);
        Assert.False(plan.IsDisposed);
        Assert.False(gap.IsDisposed);

        shell.Dispose();

        // ALL THREE disposed — the workspace and plan (not the current step) are NOT leaked, and the
        // gap (the current step) is disposed too.
        Assert.True(workspace.IsDisposed, "teardown must dispose the workspace even when it is not the current step");
        Assert.True(plan.IsDisposed, "teardown must dispose the plan even when it is not the current step");
        Assert.True(gap.IsDisposed, "teardown must dispose the gap step");
    }

    /// <summary>
    /// Dispose is each-once: re-entering the plan from Gap (Back), then re-entering Gap again
    /// before teardown, must not double-track or double-dispose. A double-dispose would be a bug
    /// (the gap/plan dispose is idempotent, but the shell must dedupe by reference like it does for
    /// the workspace re-entered via Back). After the round-trip the single shell teardown leaves
    /// each step disposed exactly once — observed behaviorally: a second teardown is a no-op.
    /// </summary>
    [Fact(Timeout = 60_000)]
    public async Task ShellDispose_AfterGapBackAndReEnter_IsIdempotentAcrossSteps()
    {
        var (shell, workspace) = await DemoShellWithLiveWorkspaceAsync();

        shell.OnDesignPlan(workspace);
        var plan = Assert.IsType<PlanViewModel>(shell.CurrentStep);

        shell.OnGapAnalysis(plan, workspace);
        var gap1 = Assert.IsType<GapViewModel>(shell.CurrentStep);
        gap1.BackCommand.Execute(null);
        Assert.Same(plan, shell.CurrentStep);

        // Re-enter Gap a second time (a fresh gap instance) — the shell tracks it too.
        shell.OnGapAnalysis(plan, workspace);
        var gap2 = Assert.IsType<GapViewModel>(shell.CurrentStep);

        shell.Dispose();
        Assert.True(workspace.IsDisposed);
        Assert.True(plan.IsDisposed);
        Assert.True(gap1.IsDisposed);
        Assert.True(gap2.IsDisposed);

        // A second teardown is a no-op (idempotent overall).
        var ex = Record.Exception(() => shell.Dispose());
        Assert.Null(ex);
    }

    // === (4) the gate: OnGapAnalysis is a no-op when the workspace has not loaded =========

    /// <summary>
    /// THE gate (ADR-015 D7): <see cref="ShellViewModel.OnGapAnalysis"/> with a workspace whose
    /// scope load is still in flight (<see cref="WorkspaceViewModel.Snapshot"/> null) is a NO-OP —
    /// <see cref="ShellViewModel.CurrentStep"/> is UNCHANGED (it stays the plan, never a gap). A gap
    /// is meaningful only against a loaded Ist; the snapshot-null arm is the testable half (the
    /// <c>BaseOuDn == RootDn</c> arm holds by construction since <c>OnDesignPlan</c> seeds the plan's
    /// base OU = the workspace root). Driven through the gated-load <see cref="StubDirectoryProvider"/>
    /// (mirrors <see cref="PlanModeTests"/>'s gated-workspace helper), so the workspace is settled on
    /// the step but has no snapshot.
    /// </summary>
    [Fact(Timeout = 60_000)]
    public async Task OnGapAnalysis_WhenWorkspaceSnapshotIsNull_IsANoOp_CurrentStepUnchanged()
    {
        await Task.CompletedTask; // xUnit Timeout requires an async test; the body is otherwise synchronous.
        var loadGate = new TaskCompletionSource<DirectorySnapshot>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var provider = StubProvider();
        provider.LoadScopeResult = loadGate.Task;
        var shell = ShellWithWorkspaceStep(provider, out var workspace);

        Assert.True(workspace.IsLoading, "the gated load holds the workspace pre-snapshot");
        Assert.Null(workspace.Snapshot); // the gate's null arm

        shell.OnDesignPlan(workspace);
        var plan = Assert.IsType<PlanViewModel>(shell.CurrentStep);
        Assert.Equal(plan.Plan.BaseOuDn, workspace.RootDn, Dn.Comparer); // the BaseOuDn==RootDn arm holds

        // The gate: with no loaded Ist, OnGapAnalysis does NOT switch — the plan stays current.
        shell.OnGapAnalysis(plan, workspace);
        Assert.Same(plan, shell.CurrentStep);
        Assert.IsNotType<GapViewModel>(shell.CurrentStep);

        shell.Dispose();
        loadGate.TrySetCanceled();
    }

    /// <summary>
    /// The armed <see cref="PlanViewModel.GapAnalysisCommand"/> over a NOT-yet-loaded workspace is
    /// also a no-op (it routes through the same gated <see cref="ShellViewModel.OnGapAnalysis"/>
    /// seam): executing it while <see cref="WorkspaceViewModel.Snapshot"/> is null leaves
    /// <see cref="ShellViewModel.CurrentStep"/> on the plan. Proves the command and the seam share
    /// the one gate, not two divergent guards.
    /// </summary>
    [Fact(Timeout = 60_000)]
    public async Task PlanGapAnalysisCommand_WhenWorkspaceSnapshotIsNull_DoesNotSwitch()
    {
        await Task.CompletedTask;
        var loadGate = new TaskCompletionSource<DirectorySnapshot>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var provider = StubProvider();
        provider.LoadScopeResult = loadGate.Task;
        var shell = ShellWithWorkspaceStep(provider, out var workspace);
        Assert.Null(workspace.Snapshot);

        shell.OnDesignPlan(workspace);
        var plan = Assert.IsType<PlanViewModel>(shell.CurrentStep);

        plan.GapAnalysisCommand.Execute(null);

        Assert.Same(plan, shell.CurrentStep);
        Assert.IsNotType<GapViewModel>(shell.CurrentStep);

        shell.Dispose();
        loadGate.TrySetCanceled();
    }

    // === helpers ========================================================================

    /// <summary>A shell over a provider with the WebView2 probe forced present (never the live
    /// registry — that would make the renderer factory machine-dependent) and a temp-dir
    /// <see cref="RulesetLocator"/> seam (never real %APPDATA% from a test). Mirrors
    /// <see cref="PlanModeTests"/>'s <c>Shell</c> factory.</summary>
    private static ShellViewModel Shell(
        IDirectoryProvider provider, Func<IGraphRenderer>? graphRendererFactory)
    {
        var locator = new RulesetLocator(
            System.IO.Directory.CreateTempSubdirectory("groupweaver-gap-shell-tests-").FullName);
        return new ShellViewModel(
            _ => provider,
            new StartupOptions(Demo: false),
            new WebView2RuntimeStatus(IsInstalled: true, Version: "test"),
            graphRendererFactory,
            locator.LoadEffective(),
            locator);
    }

    /// <summary>A shell whose CurrentStep is already a workspace over the stub provider (skips the
    /// Connect/PickRoot drive — used by the gate's gated-snapshot pins). The stub's gated
    /// <see cref="StubDirectoryProvider.LoadScopeResult"/> keeps the workspace pre-snapshot.</summary>
    private static ShellViewModel ShellWithWorkspaceStep(
        StubDirectoryProvider provider, out WorkspaceViewModel workspace)
    {
        var shell = Shell(provider, () => new FakeGraphRenderer());
        workspace = DriveShellToWorkspaceStepSync(shell, provider);
        return shell;
    }

    /// <summary>Connect (demo stub) → pick the single stub candidate → load. The stub's gated
    /// <see cref="StubDirectoryProvider.LoadScopeResult"/> keeps the workspace IsLoading; the
    /// workspace step is returned without awaiting Initialization (mirrors
    /// <see cref="PlanModeTests"/>'s sync drive).</summary>
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

    /// <summary>Connect → pick the demo root OU → load, awaiting the settled workspace (mirrors
    /// <see cref="PlanModeTests"/>'s async drive).</summary>
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
