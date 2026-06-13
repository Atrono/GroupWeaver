using System.Linq;
using System.Threading.Tasks;

using Avalonia.Headless.XUnit;
using Avalonia.Threading;

using GroupWeaver.App.Startup;
using GroupWeaver.App.ViewModels;
using GroupWeaver.App.Views;
using GroupWeaver.Core.Model;
using GroupWeaver.Core.Plan;
using GroupWeaver.Providers;

using Xunit;

namespace GroupWeaver.App.Tests;

/// <summary>
/// Region (B) of AP 4.2.4 (ADR-014, closes #63): the production export-dialog-wiring
/// REGRESSION. The AP 4.1 export seam (<c>IExportFileDialogs</c>) shipped INERT because no
/// production view ever wired a <c>StorageProviderExportFileDialogs</c> into the current
/// exportable step — so the Export CSV/HTML/Image buttons were permanently disabled in the
/// shipped app even though every VM-side export command was correct and unit-tested. This
/// slice mirrors <c>SettingsWindow.OnOpened</c>: <see cref="MainWindow"/> overrides
/// <c>OnOpened</c>, builds ONE adapter from its own <c>TopLevel</c>, wires the current step,
/// and re-wires on each <c>CurrentStep</c> change.
///
/// <para><b>Why a headless test catches a gap unit tests missed.</b> The AP 4.1
/// <c>WorkspaceExportTests</c> inject the seam directly into the VM (<c>UseExportFileDialogs</c>),
/// so they prove the COMMAND works but never that the production VIEW wires it. This fixture
/// closes that hole: it opens a real <see cref="MainWindow"/> over a real
/// <see cref="DemoProvider"/> shell driven to a SETTLED workspace, pumps the dispatcher, and
/// asserts the export commands are ARMED purely by opening the window — i.e. opening the
/// window wired <c>StorageProviderExportFileDialogs</c> from the window's
/// <c>TopLevel.StorageProvider</c>. In headless Avalonia <c>GetTopLevel(window)</c> is
/// non-null and <c>StorageProvider</c> exists, so the adapter constructs and the arm happens
/// — the test asserts ARMING only, NEVER an actual OS pick (that [I] layer is untestable).</para>
///
/// <para>The targeted commands are the <c>[RelayCommand]</c>-generated properties:
/// <c>WorkspaceViewModel.ExportReportCsvCommand</c> / <c>ExportReportHtmlCommand</c> /
/// <c>ExportGraphImageCommand</c> and <c>PlanViewModel.ExportPlanScriptCommand</c>. The plan
/// step is created AFTER the window opened (via the workspace <c>DesignPlanCommand</c>), so
/// arming it proves the <c>CurrentStep</c>-change subscription re-wires new exportable steps.</para>
///
/// <para><b>RED pre-fix</b> (the #63 regression pin): before <see cref="MainWindow"/> gains
/// the <c>OnOpened</c> wiring + <c>CurrentStep</c> subscription, opening the window arms
/// nothing — <c>ExportReportCsvCommand.CanExecute(null)</c> is FALSE (no seam ⇒ the
/// <c>_exportDialogs is not null</c> gate fails), which is the failing state this slice fixes.
/// Also RED at compile time until <see cref="PlanViewModel.ExportPlanScriptCommand"/> and the
/// <c>Ps1</c> export wiring exist.</para>
/// </summary>
public sealed class ExportWiringTests
{
    private const string DemoRootDn = "OU=AGDLP-Demo,DC=weavedemo,DC=example";

    /// <summary>
    /// The #63 keystone: opening a real <see cref="MainWindow"/> over a shell driven to a
    /// SETTLED workspace ARMS the workspace export commands — proving the window wired
    /// <c>StorageProviderExportFileDialogs</c> from its <c>TopLevel</c> and re-armed the
    /// commands (their <c>CanExecute</c> gate includes <c>_exportDialogs is not null</c>).
    /// Pre-fix this is FALSE (no production wiring ⇒ the seam is null ⇒ disarmed) — the RED
    /// state. The CSV command is the canonical export gate (<c>Snapshot is not null</c> + seam
    /// installed); the HTML command shares that gate, and the image command additionally needs
    /// a renderer (the demo workspace has a real WebView2-backed renderer factory in production
    /// but a headless shell may have none, so the image command is asserted only-if armable
    /// elsewhere — here CSV/HTML carry the regression pin). Closing the window must not throw.
    /// </summary>
    [AvaloniaFact]
    public async Task OpeningMainWindow_OverASettledWorkspace_ArmsTheWorkspaceExportCommands()
    {
        var (window, shell) = ShowShell();
        var workspace = await DriveToWorkspaceAsync(shell);
        Assert.IsType<WorkspaceViewModel>(shell.CurrentStep);

        // Pump so MainWindow.OnOpened (the wiring) and the settled workspace's command
        // re-arm have both run on the UI thread.
        Dispatcher.UIThread.RunJobs();

        // The regression pin (#63): opening the window must have wired the export seam and
        // ARMED the CSV/HTML export commands. Pre-fix the production view never wires
        // StorageProviderExportFileDialogs, so _exportDialogs stays null and CanExecute is
        // false — this assertion is the RED.
        Assert.True(
            workspace.ExportReportCsvCommand.CanExecute(null),
            "opening MainWindow must wire StorageProviderExportFileDialogs and arm CSV export (#63)");
        Assert.True(
            workspace.ExportReportHtmlCommand.CanExecute(null),
            "opening MainWindow must arm HTML export too (same gate as CSV) (#63)");

        // Closing the window must not throw (the OnOpened subscription is torn down in Closed
        // without leaking — the existing Closed teardown already disposes the shell).
        var closeEx = Record.Exception(() => window.Close());
        Assert.Null(closeEx);
    }

    /// <summary>
    /// The <c>CurrentStep</c>-subscription pin: a Plan step CREATED AFTER the window opened
    /// (via the workspace <c>DesignPlanCommand</c>) is wired too. After seeding one node
    /// through the plan's public AP 4.2.3 command, <c>ExportPlanScriptCommand.CanExecute(null)</c>
    /// must be TRUE — proving <see cref="MainWindow"/> subscribed to the shell's
    /// <c>CurrentStep</c> change and pushed the same <c>StorageProviderExportFileDialogs</c>
    /// into the new <see cref="PlanViewModel"/> (its gate is dialogs-installed AND ≥1 node).
    /// Pre-fix: no subscription ⇒ the plan never receives the seam ⇒ disarmed (RED). Closing
    /// the window must not throw.
    /// </summary>
    [AvaloniaFact]
    public async Task SwitchingIntoPlanMode_AfterOpen_WiresTheNewPlanStepsExportCommand()
    {
        var (window, shell) = ShowShell();
        var workspace = await DriveToWorkspaceAsync(shell);
        Dispatcher.UIThread.RunJobs();

        // Switch into Plan mode through the workspace's [RelayCommand]-generated DesignPlanCommand
        // (the live shell installed its callback) — the plan step is created AFTER the window opened.
        Assert.True(
            workspace.DesignPlanCommand.CanExecute(null),
            "Design plan must be armed on a settled workspace");
        workspace.DesignPlanCommand.Execute(null);
        var plan = Assert.IsType<PlanViewModel>(shell.CurrentStep);

        // Pump so the CurrentStep-change subscription wired the new plan step.
        Dispatcher.UIThread.RunJobs();

        // Seed one node through the plan's public AP 4.2.3 command surface so the ≥1-node half
        // of the plan-export gate is met; the dialogs half must already be satisfied by the
        // CurrentStep wiring (#63 fix) — otherwise the command stays disarmed (RED).
        plan.NewObjectKind = PlanCreatableKind.GlobalGroup;
        plan.NewObjectName = "GG_Sales_Team";
        await plan.AddObjectCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();

        Assert.True(
            plan.ExportPlanScriptCommand.CanExecute(null),
            "a plan step created after the window opened must be wired by the CurrentStep subscription (#63)");

        var closeEx = Record.Exception(() => window.Close());
        Assert.Null(closeEx);
    }

    // === helpers (mirror ShellScreenshotTests.ShowShell / DriveToWorkspaceAsync) =========

    /// <summary>A real <see cref="MainWindow"/> over a real <see cref="DemoProvider"/> shell,
    /// WebView2 probe forced present (never the live registry — that would make the wiring
    /// machine-dependent), shown so its <c>OnOpened</c> runs. Mirrors
    /// <c>ShellScreenshotTests.ShowShell</c> but returns the window for the wiring assertion.</summary>
    private static (MainWindow Window, ShellViewModel Shell) ShowShell()
    {
        var shell = new ShellViewModel(
            _ => new DemoProvider(),
            new StartupOptions(Demo: false),
            new WebView2RuntimeStatus(IsInstalled: true, Version: "test"));

        var window = new MainWindow { DataContext = shell, Width = 1280, Height = 720 };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return (window, shell);
    }

    /// <summary>Connect (demo) → pick the demo root OU → load, awaiting the SETTLED workspace —
    /// mirrors <c>ShellScreenshotTests.DriveToWorkspaceAsync</c> / <c>PlanModeTests</c>, so the
    /// shell's CurrentStep is the workspace by the time the wiring assertion runs.</summary>
    private static async Task<WorkspaceViewModel> DriveToWorkspaceAsync(ShellViewModel shell)
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
}
