using System.Linq;
using System.Threading.Tasks;

using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Avalonia.VisualTree;

using GroupWeaver.App.Graph;
using GroupWeaver.App.Startup;
using GroupWeaver.App.Tests.Fakes;
using GroupWeaver.App.ViewModels;
using GroupWeaver.App.Views;
using GroupWeaver.Core.Model;
using GroupWeaver.Providers;

using Xunit;

namespace GroupWeaver.App.Tests;

/// <summary>
/// Slice 6 of #122 (ADR-025): the viewport-preserving Back behaviors, layered ON TOP of the
/// pre-existing <see cref="BackNavigationStepSwapTests"/> crash regression (which still pins the
/// no-coordinator / measure-pass-conflict path). These prove the WHOLE wired pipeline end-to-end:
/// a real <see cref="MainWindow"/> is SHOWN, so its <c>OnOpened</c> builds the one
/// <see cref="GraphSurfaceCoordinator"/> over the live <c>ParkingLot</c> <see cref="Panel"/> and
/// pushes it into the shell + each graph step (mirroring the export seam). Confirmed empirically
/// (see <c>ExportWiringTests</c> for the same OnOpened-fires-headless precedent): under
/// Avalonia.Headless <c>OnOpened</c> DOES run and the wired workspace VM's
/// <c>GraphSurfaceCoordinator</c> is non-null and the named <c>ParkingLot</c> Panel is reachable
/// from the window.
///
/// <para><b>Headless WebView caveat.</b> Avalonia.Headless cannot host a real
/// <c>NativeWebView</c>, so the renderer surface here is a real <see cref="Border"/>
/// (<see cref="FakeGraphRenderer.WithRealSurface"/>). The coordinator's <c>BeginReparenting</c>
/// branch fires only for a <c>NativeWebView</c>; a <see cref="Border"/> takes the plain swap —
/// which the ParkSpike proved equivalent for a control with no live page. So these tests pin the
/// VISUAL-TREE choreography (park/mount parent transitions, bounded parked count, abandon
/// disposal) — the actual WebView2-page + same-HWND + viewport survival is the windowed smoke's
/// job (<c>tools/smoke-back-nav.ps1</c>), as is calling out below.</para>
///
/// <para>Comparisons are by surface PROJECTION (the renderer's single cached <c>View</c> control
/// identity, its parent, the <c>ParkingLot.Children</c> count, and the renderer's <c>Disposed</c>
/// flag) — never record/VM identity beyond the documented "same VM on Back" contract the shell
/// already guarantees.</para>
/// </summary>
public sealed class ParkingLotBackNavigationTests
{
    /// <summary>WebView2 forced present so the (real) views invoke the renderer factory and mount a
    /// surface — never the live registry (that would make the factory machine-dependent).</summary>
    private static readonly WebView2RuntimeStatus Present = new(IsInstalled: true, Version: "test");

    // === Viewport-preserving re-mount (the same live control comes back from the lot) ==========

    /// <summary>
    /// Workspace→Plan→Back→Workspace: on Back the SAME workspace renderer <c>View</c> control is
    /// re-mounted under the CURRENT step's GraphHost, and it came from the parking lot — proven two
    /// ways: (a) during the Plan visit the workspace surface sat in <c>ParkingLot.Children</c> (the
    /// shell parked it before the forward swap), and (b) the Back mount reported
    /// <c>wasAliveParked == true</c> through the live wired coordinator. This is the no-reload-flash
    /// path: a parked-and-alive surface is re-mounted, not re-rendered.
    /// </summary>
    [AvaloniaFact(Timeout = 60_000)]
    public async Task WorkspacePlanBack_ReMountsTheSameLiveWorkspaceSurface_FromTheParkingLot()
    {
        var (window, shell) = ShowShellWithRealGraphSurface();
        var parkingLot = FindParkingLot(window);

        var workspace = await DriveToWorkspaceAsync(shell);
        var coordinator = WiredCoordinator(workspace);
        ForceLayoutPass(window);
        var workspaceSurface = workspace.GraphRenderer!.View!;

        // Into Plan: the shell parks the workspace surface BEFORE the swap. While Plan is current,
        // the workspace surface must be sitting in the parking lot (parked-and-alive).
        shell.OnDesignPlan(workspace);
        var plan = Assert.IsType<PlanViewModel>(shell.CurrentStep);
        ForceLayoutPass(window);
        Assert.Contains(workspaceSurface, parkingLot.Children);
        Assert.Same(parkingLot, workspaceSurface.Parent);

        // Back to the SAME workspace: a fresh WorkspaceView mounts the workspace surface through the
        // wired coordinator. Because it was parked-and-alive, Mount reports true (skip the re-render).
        plan.BackCommand.Execute(null);
        Assert.Same(workspace, Assert.IsType<WorkspaceViewModel>(shell.CurrentStep));
        ForceLayoutPass(window);

        // The re-mount probe: the coordinator reports parked-and-alive for this surface vs. the
        // current workspace GraphHost. (The view already mounted it, so this is the idempotent
        // "already where it belongs" arm — which also returns true: the surface is alive in place.)
        var graphHost = CurrentGraphHost(window);
        Assert.True(
            coordinator.Mount(workspaceSurface, graphHost),
            "Back must re-mount the parked-and-alive workspace surface (wasAliveParked == true)");

        // SAME control instance, now under the CURRENT step's GraphHost, out of the lot.
        Assert.Same(workspaceSurface, workspace.GraphRenderer!.View);
        Assert.Same(workspaceSurface, graphHost.Content);
        Assert.DoesNotContain(workspaceSurface, parkingLot.Children);

        shell.Dispose();
        window.Close();
    }

    /// <summary>
    /// Plan→Gap→Back→Plan: the Plan-side mirror of the workspace case. Driving into Gap parks the
    /// plan surface; while Gap is current it sits in the lot; Back re-mounts the SAME plan surface
    /// and the coordinator reports <c>wasAliveParked == true</c>.
    /// </summary>
    [AvaloniaFact(Timeout = 60_000)]
    public async Task PlanGapBack_ReMountsTheSameLivePlanSurface_FromTheParkingLot()
    {
        var (window, shell) = ShowShellWithRealGraphSurface();
        var parkingLot = FindParkingLot(window);

        var workspace = await DriveToWorkspaceAsync(shell);
        var coordinator = WiredCoordinator(workspace);
        Assert.NotNull(workspace.Snapshot); // OnGapAnalysis gates on a loaded Ist
        ForceLayoutPass(window);

        shell.OnDesignPlan(workspace);
        var plan = Assert.IsType<PlanViewModel>(shell.CurrentStep);
        ForceLayoutPass(window);
        // The same wired coordinator must reach the new Plan step (CurrentStep-change re-wire).
        Assert.Same(coordinator, plan.GraphSurfaceCoordinator);
        var planSurface = plan.GraphRenderer!.View!;

        // Into Gap: the shell parks the plan surface before the swap.
        shell.OnGapAnalysis(plan, workspace);
        Assert.IsType<GapViewModel>(shell.CurrentStep);
        ForceLayoutPass(window);
        Assert.Contains(planSurface, parkingLot.Children);
        Assert.Same(parkingLot, planSurface.Parent);

        // Back to the SAME plan: a fresh PlanView re-mounts the plan surface from the lot.
        var gap = Assert.IsType<GapViewModel>(shell.CurrentStep);
        gap.BackCommand.Execute(null);
        Assert.Same(plan, Assert.IsType<PlanViewModel>(shell.CurrentStep));
        ForceLayoutPass(window);

        var graphHost = CurrentGraphHost(window);
        Assert.True(
            coordinator.Mount(planSurface, graphHost),
            "Back must re-mount the parked-and-alive plan surface (wasAliveParked == true)");
        Assert.Same(planSurface, plan.GraphRenderer!.View);
        Assert.Same(planSurface, graphHost.Content);
        Assert.DoesNotContain(planSurface, parkingLot.Children);

        shell.Dispose();
        window.Close();
    }

    // === Bounded parked count (live WebViews stay bounded) =====================================

    /// <summary>
    /// The parked-surface budget the reclaim logic guarantees, read off the LIVE <c>ParkingLot</c>
    /// panel: after Workspace→Plan→Gap exactly 2 surfaces are parked (Workspace + Plan); after
    /// Gap→Back→Plan exactly 1 (Workspace stays parked, Plan unparked on re-mount); after
    /// Plan→Back→Workspace exactly 1 (Workspace unparked on re-mount, but the kept-alive Plan's
    /// surface is now PARKED). The count is the parking lot's own <c>Children.Count</c> — the
    /// structural proof live surfaces stay BOUNDED (≤ Workspace + current Plan), never accumulating.
    ///
    /// <para><b>UPDATED for the #122 plan KEEP-ALIVE fix.</b> The final assertion was
    /// <c>Assert.Empty</c> (0 parked) under the pre-keep-alive contract where Plan→Back-to-Workspace
    /// DISPOSED the plan, so its surface left the lot entirely. Keep-alive now PARKS the plan surface
    /// on Back (the plan + its graph preview survive the round-trip, mirroring how the workspace
    /// surface is parked when leaving it), so exactly 1 surface (the parked plan) remains. This is the
    /// intended truth: the bound (≤ 2 live surfaces) is unchanged; only the resting count after a
    /// Plan→Workspace Back reflects that the plan is no longer thrown away.</para>
    /// </summary>
    [AvaloniaFact(Timeout = 60_000)]
    public async Task ForwardThenBack_KeepsParkedSurfacesBounded()
    {
        var (window, shell) = ShowShellWithRealGraphSurface();
        var parkingLot = FindParkingLot(window);

        var workspace = await DriveToWorkspaceAsync(shell);
        WiredCoordinator(workspace); // assert OnOpened wired the coordinator into the live step
        Assert.NotNull(workspace.Snapshot);
        ForceLayoutPass(window);
        Assert.Empty(parkingLot.Children); // nothing parked yet (workspace is mounted)

        shell.OnDesignPlan(workspace);
        var plan = Assert.IsType<PlanViewModel>(shell.CurrentStep);
        ForceLayoutPass(window);
        Assert.Single(parkingLot.Children); // Workspace parked
        var planSurface = plan.GraphRenderer!.View!;

        shell.OnGapAnalysis(plan, workspace);
        var gap = Assert.IsType<GapViewModel>(shell.CurrentStep);
        ForceLayoutPass(window);
        Assert.Equal(2, parkingLot.Children.Count); // Workspace + Plan parked

        gap.BackCommand.Execute(null);
        Assert.Same(plan, shell.CurrentStep);
        ForceLayoutPass(window);
        Assert.Single(parkingLot.Children); // Plan unparked (re-mounted); Workspace stays

        plan.BackCommand.Execute(null);
        Assert.Same(workspace, shell.CurrentStep);
        ForceLayoutPass(window);
        // Keep-alive: the Workspace surface is unparked (re-mounted), and the kept-alive Plan's
        // surface is now parked — exactly 1 surface, still within the bound (never 2 live at rest).
        Assert.Single(parkingLot.Children);
        Assert.Contains(planSurface, parkingLot.Children);
        Assert.False(plan.IsDisposed, "Plan→Back keeps the plan alive (#122) — its surface is parked, not torn down");

        shell.Dispose();
        window.Close();
    }

    // === Abandoned surfaces are disposed (the reclaim that frees the WebView) ==================

    /// <summary>
    /// Gap→Back abandons the Gap (a fresh Gap is always built on re-entry), so the shell disposes +
    /// untracks it — the Gap renderer's <c>Disposed</c> flag must flip true, proving the WebView
    /// would be freed in production. The plan and workspace renderers must NOT be disposed (they are
    /// the survivors the Back returns to).
    /// </summary>
    [AvaloniaFact(Timeout = 60_000)]
    public async Task GapBack_DisposesTheAbandonedGapRenderer()
    {
        var (window, shell) = ShowShellWithRealGraphSurface();

        var workspace = await DriveToWorkspaceAsync(shell);
        WiredCoordinator(workspace); // assert OnOpened wired the coordinator into the live step
        Assert.NotNull(workspace.Snapshot);
        ForceLayoutPass(window);

        shell.OnDesignPlan(workspace);
        var plan = Assert.IsType<PlanViewModel>(shell.CurrentStep);
        ForceLayoutPass(window);

        shell.OnGapAnalysis(plan, workspace);
        var gap = Assert.IsType<GapViewModel>(shell.CurrentStep);
        ForceLayoutPass(window);
        var gapRenderer = (FakeGraphRenderer)gap.GraphRenderer!;
        Assert.False(gapRenderer.Disposed); // alive while current

        gap.BackCommand.Execute(null);
        Assert.Same(plan, shell.CurrentStep);
        ForceLayoutPass(window);

        Assert.True(gapRenderer.Disposed, "Back from Gap must dispose the abandoned Gap renderer (#122 reclaim)");
        Assert.False(((FakeGraphRenderer)plan.GraphRenderer!).Disposed); // the survivor Plan
        Assert.False(((FakeGraphRenderer)workspace.GraphRenderer!).Disposed); // the survivor Workspace

        shell.Dispose();
        window.Close();
    }

    /// <summary>
    /// Workspace→Plan→Back→Workspace→Plan: under the #122 PLAN KEEP-ALIVE fix this no longer
    /// abandons + rebuilds the Plan. Back-to-Workspace PARKS the plan surface but KEEPS the plan
    /// alive (mirroring the workspace's own never-disposed-on-Back lifecycle), so the next
    /// Design-plan over the SAME workspace root re-enters the SAME plan instance + renderer (its
    /// authored content survives) — never a fresh second VM.
    ///
    /// <para><b>UPDATED from the pre-keep-alive contract</b> (was
    /// <c>PlanBackWorkspacePlanAgain_DisposesTheFirstAbandonedPlanRenderer</c>): it asserted Back
    /// disposed Plan #1's renderer and re-entry yielded a different Plan #2. The keep-alive change
    /// (<c>ShellViewModel.OnDesignPlan</c> no longer disposes/replaces a same-base-OU plan on Back)
    /// makes that the WRONG behavior — it caused silent data loss of the authored plan on Back. The
    /// assertions are inverted to the intended truth: NOT disposed, SAME instance, SAME renderer.
    /// The superseded-Plan reclaim arm (a DIFFERENT base OU disposes + rebuilds) is pinned in
    /// <see cref="PlanModeTests"/>'s base-OU-change test.</para>
    /// </summary>
    [AvaloniaFact(Timeout = 60_000)]
    public async Task PlanBackWorkspacePlanAgain_KeepsTheSamePlanAlive_NotDisposed()
    {
        var (window, shell) = ShowShellWithRealGraphSurface();

        var workspace = await DriveToWorkspaceAsync(shell);
        WiredCoordinator(workspace); // assert OnOpened wired the coordinator into the live step
        ForceLayoutPass(window);

        // First Plan.
        shell.OnDesignPlan(workspace);
        var plan1 = Assert.IsType<PlanViewModel>(shell.CurrentStep);
        ForceLayoutPass(window);
        var plan1Renderer = (FakeGraphRenderer)plan1.GraphRenderer!;

        // Back to the SAME workspace — KEEP-ALIVE: the plan is parked, NOT disposed.
        plan1.BackCommand.Execute(null);
        Assert.Same(workspace, shell.CurrentStep);
        ForceLayoutPass(window);
        Assert.False(
            plan1Renderer.Disposed,
            "Back-to-Workspace must KEEP the plan alive (keep-alive #122 — its renderer is not disposed)");
        Assert.False(plan1.IsDisposed, "Back-to-Workspace must not dispose the plan VM (keep-alive #122)");

        // Re-enter Plan over the SAME workspace root: the SAME plan instance + renderer comes back
        // (re-threaded, content intact) — never a fresh second VM.
        shell.OnDesignPlan(workspace);
        var plan2 = Assert.IsType<PlanViewModel>(shell.CurrentStep);
        ForceLayoutPass(window);
        Assert.Same(plan1, plan2);
        Assert.Same(plan1.GraphRenderer, plan2.GraphRenderer);
        Assert.False(((FakeGraphRenderer)plan2.GraphRenderer!).Disposed); // the kept-alive plan
        Assert.False(((FakeGraphRenderer)workspace.GraphRenderer!).Disposed); // the survivor Workspace

        shell.Dispose();
        window.Close();
    }

    // === helpers (mirror BackNavigationStepSwapTests) ==========================================

    /// <summary>A shown <see cref="MainWindow"/> + real <see cref="ShellViewModel"/> over a real
    /// <see cref="DemoProvider"/>, WebView2 forced present, and a renderer factory that yields a
    /// fresh <see cref="FakeGraphRenderer.WithRealSurface"/> per step (each step thus mounts its OWN
    /// single cached real control). Shown so <c>MainWindow.OnOpened</c> builds + wires the
    /// coordinator. Fresh temp-dir UiStateStore (demo-mode discipline; never touches real %APPDATA%).
    /// Mirrors <c>BackNavigationStepSwapTests.ShowShellWithRealGraphSurface</c>.</summary>
    private static (MainWindow Window, ShellViewModel Shell) ShowShellWithRealGraphSurface()
    {
        var uiStateBase = System.IO.Directory
            .CreateTempSubdirectory("groupweaver-parklot-uistate-").FullName;
        var shell = new ShellViewModel(
            _ => new DemoProvider(),
            new StartupOptions(Demo: false),
            Present,
            graphRendererFactory: FakeGraphRenderer.WithRealSurface,
            ruleset: null,
            locator: null,
            uiStateStore: new GroupWeaver.App.Settings.UiStateStore(uiStateBase));

        var window = new MainWindow { DataContext = shell, Width = 1280, Height = 720 };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return (window, shell);
    }

    /// <summary>Connect (demo) → pick the demo root OU → load, awaiting the settled workspace.
    /// Mirrors <c>BackNavigationStepSwapTests.DriveToWorkspaceAsync</c>; the demo root OU scope
    /// carries the seeded GG_Circle_A↔GG_Circle_B cycle.</summary>
    private static async Task<WorkspaceViewModel> DriveToWorkspaceAsync(ShellViewModel shell)
    {
        var connect = Assert.IsType<ConnectionViewModel>(shell.CurrentStep);
        await connect.ConnectDemoCommand.ExecuteAsync(null);
        var picker = Assert.IsType<RootPickerViewModel>(shell.CurrentStep);
        await picker.LoadCandidates;
        Dispatcher.UIThread.RunJobs();

        picker.SelectedCandidate = picker.Candidates.First(c => c.Kind == AdObjectKind.OrganizationalUnit);
        picker.LoadRootCommand.Execute(null);
        var workspace = Assert.IsType<WorkspaceViewModel>(shell.CurrentStep);
        await workspace.Initialization;
        Dispatcher.UIThread.RunJobs();
        return workspace;
    }

    /// <summary>Forces the headless compositor through a real layout/render pass (capture-and-discard
    /// + pump). Identical to <c>BackNavigationStepSwapTests.ForceLayoutPass</c> — the pass that runs
    /// the step swap's measure + the views' mount/detach handlers.</summary>
    private static void ForceLayoutPass(Window window)
    {
        Dispatcher.UIThread.RunJobs();
        window.CaptureRenderedFrame()?.Dispose();
        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>The live, named <c>ParkingLot</c> Panel reachable from the shown window (the one the
    /// coordinator parks into). Empirically present headless after <c>OnOpened</c>.</summary>
    private static Panel FindParkingLot(MainWindow window)
    {
        var parkingLot = window.GetLogicalDescendants().OfType<Panel>()
            .FirstOrDefault(p => p.Name == "ParkingLot");
        Assert.NotNull(parkingLot);
        return parkingLot!;
    }

    /// <summary>Reads the REAL coordinator the window's <c>OnOpened</c> wired into the given graph
    /// step VM (mirroring the export seam) and asserts it is non-null — i.e. opening the window
    /// actually built + pushed the one <see cref="GraphSurfaceCoordinator"/> over the live
    /// <c>ParkingLot</c>. Returns that same live instance for the re-mount probes. This is the
    /// production-faithful read (no synthetic coordinator): the same instance the views mount
    /// through. Empirically confirmed non-null headless (OnOpened fires under Avalonia.Headless,
    /// the <c>ExportWiringTests</c> precedent).</summary>
    private static IGraphSurfaceCoordinator WiredCoordinator(WorkspaceViewModel workspace)
    {
        Dispatcher.UIThread.RunJobs();
        Assert.NotNull(workspace.GraphSurfaceCoordinator);
        return workspace.GraphSurfaceCoordinator!;
    }

    /// <summary>The GraphHost <see cref="ContentControl"/> currently in the visual tree (the current
    /// step's). Located by name from the window, like
    /// <c>BackNavigationStepSwapTests.AssertGraphSurfaceParentedUnderCurrentStep</c>.</summary>
    private static ContentControl CurrentGraphHost(MainWindow window)
    {
        var host = window.GetVisualDescendants().OfType<ContentControl>()
            .FirstOrDefault(c => c.Name == "GraphHost");
        Assert.NotNull(host);
        return host!;
    }
}
