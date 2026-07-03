using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

using Avalonia.Headless.XUnit;
using Avalonia.Threading;

using GroupWeaver.App.Audit;
using GroupWeaver.App.Settings;
using GroupWeaver.App.Startup;
using GroupWeaver.App.ViewModels;
using GroupWeaver.App.Views;
using GroupWeaver.Core.Model;
using GroupWeaver.Providers;

using Microsoft.Extensions.Logging;

using Xunit;

namespace GroupWeaver.App.Tests.Diagnostics;

/// <summary>
/// Pins the <c>StepChanged{from,to,trigger}</c> event vocabulary (ADR-037 D5 — the E2E timeline
/// backbone the #239 triager replays): driving the REAL <see cref="ShellViewModel"/> over a real
/// <see cref="DemoProvider"/> through the full journey
/// Connect→PickRoot→Workspace→Plan→Gap→(back)→Plan→(back)→Workspace→Audit→(back)→Workspace must
/// emit EXACTLY the pinned (from, to, trigger) sequence with the trigger vocabulary
/// <c>connected|rootChosen|designPlan|gapAnalysis|backToPlan|backToExplore|audit|backToWorkspace</c>
/// — step NAMES only, never subject data. Changing a trigger string breaks the harness's replay
/// grammar and must be a deliberate, reviewed edit here.
///
/// <para>Isolation: an injected <see cref="CapturingLoggerFactory"/> (never the file sink), a
/// temp-dir <see cref="UiStateStore"/> (the #124 rule) AND a temp-dir <see cref="AuditRunStore"/>
/// — nothing touches the real <c>%APPDATA%</c>. Construction/drive idioms mirror
/// <c>AuditNavigationTests</c>/<c>BackNavigationStepSwapTests</c>.</para>
/// </summary>
public sealed class ShellStepChangedLogTests
{
    /// <summary>WebView2 forced present (never the live registry), so no <c>WebView2Missing</c>
    /// Warn muddies the captured stream and the shell behaves machine-independently.</summary>
    private static readonly WebView2RuntimeStatus Present = new(IsInstalled: true, Version: "test");

    // === 1. The full journey emits the pinned trigger vocabulary, in order =====================

    [AvaloniaFact(Timeout = 60_000)]
    public async Task FullJourney_EmitsThePinnedStepChangedSequence()
    {
        var capture = new CapturingLoggerFactory();
        var (window, shell) = ShowShell(capture);

        // Connect (demo) → pick the demo root OU → loaded workspace.
        var workspace = await DriveToWorkspaceAsync(shell);
        Assert.NotNull(workspace.Snapshot); // OnGapAnalysis/OnAudit gate on a loaded Ist

        // Workspace → Plan → Gap → back to Plan → back to Workspace → Audit → back to Workspace.
        shell.OnDesignPlan(workspace);
        var plan = Assert.IsType<PlanViewModel>(shell.CurrentStep);
        shell.OnGapAnalysis(plan, workspace);
        var gap = Assert.IsType<GapViewModel>(shell.CurrentStep);
        gap.BackCommand.Execute(null);
        Assert.Same(plan, shell.CurrentStep);
        plan.BackCommand.Execute(null);
        Assert.Same(workspace, shell.CurrentStep);
        shell.OnAudit(workspace);
        var audit = Assert.IsType<AuditViewModel>(shell.CurrentStep);
        audit.BackCommand.Execute(null);
        Assert.Same(workspace, shell.CurrentStep);

        // The pinned (from, to, trigger) timeline — EXACT sequence equality: no extra StepChanged,
        // no missing hop, no "direct" fallback anywhere on the command-driven journey. (The ctor's
        // initial Connect step sets the backing field directly and is deliberately NOT logged —
        // index 0 is the connect hop.)
        var steps = capture.EntriesNamed("StepChanged")
            .Select(e => (
                From: Assert.IsType<string>(e.Fields["from"]),
                To: Assert.IsType<string>(e.Fields["to"]),
                Trigger: Assert.IsType<string>(e.Fields["trigger"])))
            .ToArray();

        Assert.Equal(
            new[]
            {
                ("Connect", "PickRoot", "connected"),
                ("PickRoot", "Workspace", "rootChosen"),
                ("Workspace", "Plan", "designPlan"),
                ("Plan", "Gap", "gapAnalysis"),
                ("Gap", "Plan", "backToPlan"),
                ("Plan", "Workspace", "backToExplore"),
                ("Workspace", "Audit", "audit"),
                ("Audit", "Workspace", "backToWorkspace"),
            },
            steps);

        // Wire discipline: Information on the App.Shell category, every hop.
        Assert.All(capture.EntriesNamed("StepChanged"), e =>
        {
            Assert.Equal("App.Shell", e.Category);
            Assert.Equal(LogLevel.Information, e.Level);
        });

        shell.Dispose();
        window.Close();
    }

    // === 2. The untagged-assignment fallback: trigger "direct" =================================

    /// <summary>An UNTAGGED <see cref="ShellViewModel.CurrentStep"/> assignment (tests set it
    /// directly) logs trigger <c>"direct"</c> with the type-name fallback for an unknown step —
    /// the honest marker that a swap bypassed the step machine's tagged sites.</summary>
    [AvaloniaFact]
    public void DirectStepAssignment_LogsTriggerDirect_WithTheTypeNameFallback()
    {
        var capture = new CapturingLoggerFactory();
        var shell = NewShell(capture);

        shell.CurrentStep = new object();

        var entry = Assert.Single(capture.EntriesNamed("StepChanged"));
        Assert.Equal("Connect", Assert.IsType<string>(entry.Fields["from"]));
        Assert.Equal("Object", Assert.IsType<string>(entry.Fields["to"]));
        Assert.Equal("direct", Assert.IsType<string>(entry.Fields["trigger"]));

        shell.Dispose();
    }

    // === helpers ===============================================================================

    private static (MainWindow Window, ShellViewModel Shell) ShowShell(CapturingLoggerFactory capture)
    {
        var shell = NewShell(capture);
        var window = new MainWindow { DataContext = shell, Width = 1280, Height = 720 };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return (window, shell);
    }

    /// <summary>The real shell over a real <see cref="DemoProvider"/>: WebView2 present, null
    /// renderer factory (no graph surface needed for step logging), temp-dir UiState + AuditRun
    /// stores, and the capturing logger factory injected through the ADR-037 WP1 ctor seam.</summary>
    private static ShellViewModel NewShell(CapturingLoggerFactory capture)
    {
        var uiStateBase = Directory.CreateTempSubdirectory("groupweaver-steplog-uistate-").FullName;
        var runsBase = Directory.CreateTempSubdirectory("groupweaver-steplog-runs-").FullName;
        return new ShellViewModel(
            _ => new DemoProvider(),
            new StartupOptions(Demo: false),
            Present,
            graphRendererFactory: null,
            ruleset: null,
            locator: null,
            uiStateStore: new UiStateStore(uiStateBase),
            auditRunStore: new AuditRunStore(runsBase, capture.CreateLogger("Store.AuditRuns")),
            loggerFactory: capture);
    }

    /// <summary>Connect (demo) → pick the demo root OU → load, awaiting the settled workspace (the
    /// demo root OU scope carries the seeded GG_Circle_A↔GG_Circle_B cycle). Mirrors the back-nav
    /// tests' DriveToWorkspaceAsync.</summary>
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
}
