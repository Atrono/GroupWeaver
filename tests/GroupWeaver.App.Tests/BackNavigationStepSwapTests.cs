using System;
using System.Linq;
using System.Threading.Tasks;

using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
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
/// Regression for the back-navigation crash on branch <c>fix/back-nav-webview2-crash</c>: pressing
/// "Back" from a graph step (Plan→Back→Workspace, and the Gap round-trip Plan→Gap→Back→Back) threw
///
/// <code>
/// System.InvalidOperationException: The control NativeWebView already has a visual parent
///   ContentPresenter (Host = ContentControl (Name = GraphHost)) while trying to add it as a child
///   of ContentPresenter (Host = ContentControl (Name = GraphHost)).
///   at Avalonia.Controls.Presenters.ContentPresenter.UpdateChild(...)
///   at Avalonia.Layout.Layoutable.MeasureCore(...)   // the measure pass after the step swap
/// </code>
///
/// <para><b>Root cause (NOT WebView2-specific).</b> The shell swaps steps through ONE
/// <c>ContentControl</c> (<c>MainWindow.axaml</c>'s <c>Content="{Binding CurrentStep}"</c>). Each step
/// (Workspace/Plan/Gap) owns its OWN <see cref="IGraphRenderer"/>, and that renderer's single
/// <see cref="IGraphRenderer.View"/> control is mounted into the step view's <c>GraphHost</c>
/// (<c>*View.axaml.cs</c> <c>DataContextChanged</c>: <c>GraphHost.Content = renderer.View</c>). Back
/// returns the SAME step VM (hence the SAME renderer + the SAME cached control); the shell re-templates
/// a fresh step view whose <c>DataContextChanged</c> re-mounts that control — but the OLD view never
/// released it, so the control still has the old <c>GraphHost</c> as parent and the re-parent throws.
/// This is a pure Avalonia visual-tree conflict: ANY real <see cref="Control"/> returned as the
/// renderer's <c>View</c>, re-parented across a step swap and then LAID OUT, throws the same exception
/// — so it reproduces under Avalonia.Headless (unlike the live WebView2 native-control behavior), which
/// is exactly what makes this a real CI regression test.</para>
///
/// <para><b>Why the suite missed it.</b> The default <see cref="FakeGraphRenderer"/> returns
/// <c>View == null</c>, so the real views skip the mount entirely (the <c>GraphHost</c> placeholder
/// stays) and never hit the conflict. These tests opt in via <see cref="FakeGraphRenderer.WithRealSurface"/>
/// — a renderer whose <c>View</c> returns its OWN single cached real <see cref="Control"/> (a
/// <see cref="Border"/>), mirroring the production renderer — WITHOUT changing the default fake (the
/// screenshot fixtures still rely on the null-View placeholder).</para>
///
/// <para><b>Forcing the throw.</b> The exception is thrown from the MEASURE pass, not the mount, so each
/// test forces a layout/render pass after EACH swap via the capture-and-discard render the headless
/// skill prescribes (<c>CaptureRenderedFrame() + RunJobs()</c>). Without it the conflict never surfaces.</para>
///
/// <para>Verified RED against the pre-fix code (HEAD's <c>*View.axaml.cs</c>, which never releases
/// <c>GraphHost.Content</c>): the Back swap throws the exact exception above from
/// <c>ContentPresenter.UpdateChild → Layoutable.MeasureCore</c>. GREEN once the view code-behinds
/// release the shared control on detach (and/or detach it from any stale parent before mounting).</para>
/// </summary>
public sealed class BackNavigationStepSwapTests
{
    /// <summary>WebView2 forced present (never the live registry — that would make the renderer
    /// factory machine-dependent) so the real views actually invoke the factory and mount a surface.</summary>
    private static readonly WebView2RuntimeStatus Present = new(IsInstalled: true, Version: "test");

    // === Test 1: the headless step-swap regression (the important one) =======================

    /// <summary>
    /// Plan→Back→Workspace: the round-trip the crash report names first. Drives the REAL shell through
    /// the REAL views + DataTemplates (mirroring <c>ShellScreenshotTests</c>: a real
    /// <see cref="MainWindow"/> + <see cref="ShellViewModel"/> + <see cref="DemoProvider"/>, real Skia
    /// on the headless platform — <c>UseHeadlessDrawing = false</c> from <c>TestAppBuilder</c>), with a
    /// renderer that mounts a REAL cached control so the visual-tree conflict can occur. A layout pass
    /// is forced after EACH swap. Asserts (a) NO exception across the whole round-trip, and (b) after
    /// Back the shared control is parented to the CURRENT (workspace) step's <c>GraphHost</c>, not still
    /// under a stale Plan host.
    /// </summary>
    [AvaloniaFact(Timeout = 60_000)]
    public async Task PlanBack_ReMountsTheSharedGraphControl_WithoutDoubleParentingCrash()
    {
        var (window, shell) = ShowShellWithRealGraphSurface();
        var workspace = await DriveToWorkspaceAsync(shell);
        ForceLayoutPass(window); // first mount of the workspace renderer's control into its GraphHost

        // Into Plan: a fresh PlanView mounts the PLAN renderer's (own) control; lay it out.
        shell.OnDesignPlan(workspace);
        var plan = Assert.IsType<PlanViewModel>(shell.CurrentStep);
        ForceLayoutPass(window);

        // Back to the SAME workspace: the shell re-templates a fresh WorkspaceView that must re-mount
        // the workspace renderer's SAME cached control — the pre-fix double-parent crash point. The
        // forced layout pass below is where the measure-time conflict would throw.
        plan.BackCommand.Execute(null);
        Assert.Same(workspace, Assert.IsType<WorkspaceViewModel>(shell.CurrentStep));
        var crash = Record.Exception(() => ForceLayoutPass(window));
        Assert.Null(crash); // no "already has a visual parent" InvalidOperationException

        // The shared workspace control is mounted in the CURRENT workspace's GraphHost (not a stale one).
        AssertGraphSurfaceParentedUnderCurrentStep(window, workspace.GraphRenderer);

        window.Close();
    }

    /// <summary>
    /// The Gap round-trip Plan→Gap→Back→Back, the crash report's second repro. Drives Workspace→Plan
    /// (<see cref="ShellViewModel.OnDesignPlan"/>) → Gap (<see cref="ShellViewModel.OnGapAnalysis"/>,
    /// the seam the PlanView "Gap analysis" button invokes) → Back to Plan
    /// (<see cref="GapViewModel.BackCommand"/>) → Back to Workspace
    /// (<see cref="PlanViewModel.BackCommand"/>), forcing a layout pass after EVERY swap. Asserts no
    /// exception anywhere and, after the final Back, the workspace control is parented under the current
    /// workspace step. The crash terminated at the Back measure pass; forcing layout at each hop is the
    /// only way the headless compositor reaches that pass.
    /// </summary>
    [AvaloniaFact(Timeout = 60_000)]
    public async Task PlanGapBackBack_ReMountsEachStepsGraphControl_WithoutDoubleParentingCrash()
    {
        var (window, shell) = ShowShellWithRealGraphSurface();
        var workspace = await DriveToWorkspaceAsync(shell);
        Assert.NotNull(workspace.Snapshot); // OnGapAnalysis gates on a loaded Ist
        ForceLayoutPass(window);

        shell.OnDesignPlan(workspace);
        var plan = Assert.IsType<PlanViewModel>(shell.CurrentStep);
        ForceLayoutPass(window);

        shell.OnGapAnalysis(plan, workspace);
        var gap = Assert.IsType<GapViewModel>(shell.CurrentStep);
        ForceLayoutPass(window);

        // Back to Plan (the SAME plan instance — its GraphHost re-mounts the plan renderer's control).
        gap.BackCommand.Execute(null);
        Assert.Same(plan, Assert.IsType<PlanViewModel>(shell.CurrentStep));
        var crashToPlan = Record.Exception(() => ForceLayoutPass(window));
        Assert.Null(crashToPlan);

        // Back to Workspace (the SAME workspace instance — the original crash hop).
        plan.BackCommand.Execute(null);
        Assert.Same(workspace, Assert.IsType<WorkspaceViewModel>(shell.CurrentStep));
        var crashToWorkspace = Record.Exception(() => ForceLayoutPass(window));
        Assert.Null(crashToWorkspace);

        AssertGraphSurfaceParentedUnderCurrentStep(window, workspace.GraphRenderer);

        shell.Dispose();
        window.Close();
    }

    /// <summary>
    /// CRUCIAL traversal guard (CLAUDE.md test discipline): the demo dataset deliberately contains a
    /// circular nesting (GG_Circle_A ↔ GG_Circle_B). This drives the FULL Plan→Gap→Back→Back round-trip
    /// rooted at the demo root that contains that cycle — proving the back-navigation re-mount path
    /// terminates (no hang, no crash) even with the cyclic scope loaded as the Ist the Gap diffs. The
    /// 60 s xUnit timeout converts any non-termination into a deterministic failure.
    /// </summary>
    [AvaloniaFact(Timeout = 60_000)]
    public async Task BackNavigation_OverTheCyclicDemoScope_Terminates_NoCrash()
    {
        var (window, shell) = ShowShellWithRealGraphSurface();
        // The demo ROOT OU scope contains the GG_Circle_A↔GG_Circle_B cycle (CLAUDE.md fixture spec).
        var workspace = await DriveToWorkspaceAsync(shell);
        Assert.NotNull(workspace.Snapshot);
        // The cyclic pair really is in the loaded Ist the Gap will diff (anti-vacuous guard).
        Assert.Contains(
            workspace.Snapshot!.Objects,
            o => o.Name is "GG_Circle_A" or "GG_Circle_B");
        ForceLayoutPass(window);

        shell.OnDesignPlan(workspace);
        var plan = Assert.IsType<PlanViewModel>(shell.CurrentStep);
        ForceLayoutPass(window);

        shell.OnGapAnalysis(plan, workspace);
        var gap = Assert.IsType<GapViewModel>(shell.CurrentStep);
        await gap.RefreshAsync(); // computes the diff over the cyclic Ist — must terminate
        ForceLayoutPass(window);

        gap.BackCommand.Execute(null);
        ForceLayoutPass(window);
        plan.BackCommand.Execute(null);
        var crash = Record.Exception(() => ForceLayoutPass(window));
        Assert.Null(crash);
        Assert.Same(workspace, shell.CurrentStep);

        shell.Dispose();
        window.Close();
    }

    // === Test 3: focused unit pin — release-then-remount across two ContentControls ===========

    /// <summary>
    /// A small, fast pin of the exact mechanism beneath test 1, with no shell/provider/DemoProvider:
    /// a single real control mounted into one <see cref="ContentControl"/>, then RE-mounted into a
    /// SECOND one. The naive sequence (mount A; mount B without releasing A) reproduces the
    /// double-parent throw on the measure pass; the corrective sequence (release A, then mount B)
    /// must NOT throw and must end with the control parented under B. This isolates the
    /// "release-then-remount" invariant the view code-behinds implement (detach on leave) from the
    /// rest of the shell — a cheap canary if the step-swap test ever regresses for an unrelated reason.
    /// </summary>
    [AvaloniaFact]
    public void RemountingASharedControl_AcrossTwoHosts_RequiresReleasingTheFirstHost()
    {
        var shared = new Border();
        var hostA = new ContentControl { Name = "GraphHost" };
        var hostB = new ContentControl { Name = "GraphHost" };

        // Two stacked hosts in a laid-out window, so a re-parent is followed by a real measure pass.
        var panel = new StackPanel();
        panel.Children.Add(hostA);
        panel.Children.Add(hostB);
        var window = new Window { Content = panel, Width = 400, Height = 400 };
        window.Show();

        // Mount into A and lay it out — A becomes the control's visual parent.
        hostA.Content = shared;
        ForceLayoutPass(window);
        Assert.Same(hostA, shared.GetVisualAncestors().OfType<ContentControl>().First());

        // The corrective sequence (what the view fix does): release A FIRST, then mount into B — no throw.
        hostA.Content = null;
        hostB.Content = shared;
        var ex = Record.Exception(() => ForceLayoutPass(window));
        Assert.Null(ex);
        Assert.Same(hostB, shared.GetVisualAncestors().OfType<ContentControl>().First());

        window.Close();
    }

    // === helpers =============================================================================

    /// <summary>A shown <see cref="MainWindow"/> + real <see cref="ShellViewModel"/> over a real
    /// <see cref="DemoProvider"/>, WebView2 forced present, and a renderer factory that yields a fresh
    /// <see cref="FakeGraphRenderer.WithRealSurface"/> per step — each step thus mounts its OWN single
    /// cached real control, exactly the production shape the crash needs. A fresh temp-dir
    /// <see cref="GroupWeaver.App.Settings.UiStateStore"/> keeps the default rail state and never
    /// touches real %APPDATA% (demo-mode discipline; mirrors ShellScreenshotTests' ShowShell).</summary>
    private static (MainWindow Window, ShellViewModel Shell) ShowShellWithRealGraphSurface()
    {
        var uiStateBase = System.IO.Directory
            .CreateTempSubdirectory("groupweaver-backnav-uistate-").FullName;
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

    /// <summary>Connect (demo) → pick the demo root OU → load, awaiting the settled workspace. Mirrors
    /// ShellScreenshotTests' DriveToWorkspaceAsync; the demo root OU scope carries the seeded cycle.</summary>
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

    /// <summary>Forces the headless compositor through a real layout/render pass — the pass that throws
    /// the double-parent <see cref="InvalidOperationException"/> from <c>ContentPresenter.UpdateChild</c>
    /// during <c>Layoutable.MeasureCore</c> if a shared control was re-parented without release. Uses the
    /// capture-and-discard render the headless skill prescribes (the first capture after a mutation
    /// returns the previous frame), then pumps jobs so the swap's measure pass actually runs.</summary>
    private static void ForceLayoutPass(Window window)
    {
        Dispatcher.UIThread.RunJobs();
        window.CaptureRenderedFrame()?.Dispose();
        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>Asserts the renderer's mounted control is parented under a <c>GraphHost</c>
    /// <see cref="ContentControl"/> that is itself in the CURRENT step's live visual tree (reachable
    /// from the window) — i.e. the shared control followed the step swap and is not orphaned under a
    /// stale, detached host. Skipped only if the renderer is null (no factory), which never holds here.</summary>
    private static void AssertGraphSurfaceParentedUnderCurrentStep(Window window, IGraphRenderer? renderer)
    {
        Assert.NotNull(renderer);
        var surface = renderer!.View;
        Assert.NotNull(surface);

        // The host that currently parents the surface, located via the live visual tree.
        var host = surface!.GetVisualAncestors().OfType<ContentControl>()
            .FirstOrDefault(c => c.Name == "GraphHost");
        Assert.NotNull(host);

        // That host must be reachable from the window (the current step's view), not a detached
        // stale view — i.e. the window is among the surface's visual ancestors.
        Assert.Contains(window, surface.GetVisualAncestors());
        Assert.Same(surface, host!.Content);
    }
}
