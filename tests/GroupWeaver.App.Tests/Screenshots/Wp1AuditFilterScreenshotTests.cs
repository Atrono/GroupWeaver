using Avalonia;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using Avalonia.Threading;

using GroupWeaver.App.Graph;
using GroupWeaver.App.Settings;
using GroupWeaver.App.Startup;
using GroupWeaver.App.ViewModels;
using GroupWeaver.App.Views;
using GroupWeaver.Core.Model;
using GroupWeaver.Core.Rules;
using GroupWeaver.Providers;

using Xunit;

namespace GroupWeaver.App.Tests.Screenshots;

/// <summary>
/// WP1 (audit findings FILTER) ui-verifier fixture: sibling to <see cref="Wp5cAuditScreenshotTests"/>
/// — drives the real shell to the demo workspace, switches into the Audit step via the workspace
/// AuditCommand, then drives the live <see cref="AuditViewModel"/>'s filter so the captured frame
/// shows the filter strip in an ACTIVE state. Captures BOTH theme variants via the production
/// ToggleThemeCommand seam. Demo-mode data only. Restores Dark on exit.
///
/// <para>Two scenarios, each in both variants:</para>
/// <list type="bullet">
/// <item><c>wp1-audit-filtered-{dark,light}.png</c> — one chip active in EACH of the three axes
/// (Error severity + Open status + the Nesting rule class, all of which match on the demo baseline),
/// so the frame shows the active-chip affordance, the visible "Clear filters" button, and the
/// "Showing N of M" filtered summary.</item>
/// <item><c>wp1-audit-nomatch-{dark,light}.png</c> — a contradictory pair (Error severity + the
/// Empty-groups rule class, which carries only Info findings), so <see cref="AuditViewModel.HasNoMatches"/>
/// is true and the "No findings match the current filters." empty state renders.</item>
/// </list>
///
/// <para>Uses the same capture-and-discard-first-frame idiom the existing Screenshots tests use (the
/// <c>CaptureRenderedFrame</c> lag noted in the lab-environment rules). Hermetic: injects a temp-dir
/// <see cref="UiStateStore"/> per the #124 isolation lesson — never touches real <c>%APPDATA%</c>.</para>
/// </summary>
public sealed class Wp1AuditFilterScreenshotTests
{
    private static readonly WebView2RuntimeStatus Present = new(IsInstalled: true, Version: "x");

    private static readonly Lazy<string> ArtifactsUiDir = new(() =>
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "GroupWeaver.sln")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        return Directory.CreateDirectory(Path.Combine(dir!.FullName, "artifacts", "ui")).FullName;
    });

    [AvaloniaFact]
    public async Task AuditFilter_Active_BothVariants()
    {
        var (window, shell) = ShowShell(1280, 720);
        var workspace = await DriveToWorkspaceAsync(shell);
        Dispatcher.UIThread.RunJobs();

        Assert.True(workspace.AuditCommand.CanExecute(null), "audit command must be armed after demo load");
        workspace.AuditCommand.Execute(null);

        var audit = Assert.IsType<AuditViewModel>(shell.CurrentStep);
        Dispatcher.UIThread.RunJobs();

        // Demo baseline pin (so the captured frame is the documented 19-finding scope).
        Assert.Equal(19, audit.TotalCount);

        // Toggle ONE chip in each of the three axes, each chosen so the demo baseline still yields a
        // non-empty visible set: Error severity (4 rows) AND Open status (all 19 Open) AND the Nesting
        // rule class (3 Error rows). The three axes AND together -> the visible set is the 3 nesting
        // errors, every one Open -> a meaningful active-filter frame.
        var errorChip = audit.SeverityChips.Single(c => c.Severity == RuleSeverity.Error);
        var openChip = audit.StatusChips.Single(c => c.Label == "Open");
        var nestingRow = audit.Categories.Single(
            c => string.Equals(c.RuleId, RuleIds.Nesting, StringComparison.OrdinalIgnoreCase));

        audit.ToggleFilterCommand.Execute(errorChip);
        audit.ToggleFilterCommand.Execute(openChip);
        audit.ToggleCategoryCommand.Execute(nestingRow);
        Dispatcher.UIThread.RunJobs();

        // The frame must show: an active facet per axis, the "Clear filters" button, the filtered summary.
        Assert.True(errorChip.IsActive && openChip.IsActive && nestingRow.IsActive);
        Assert.True(audit.IsFiltered, "filters must be active for the active-state screenshot");
        Assert.False(audit.HasNoMatches, "the chosen combo must still match (non-empty visible set)");
        Assert.Equal(3, audit.VisibleCount); // the 3 nesting errors
        Assert.Equal($"Showing 3 of {audit.TotalCount}", audit.FilterSummary);

        Assert.Equal(AppThemeChoice.Dark, shell.ThemeChoice);
        Settle(window);
        Capture(window, "wp1-audit-filtered-dark", 1280, 720);

        shell.ToggleThemeCommand.Execute(null);
        Assert.Equal(AppThemeChoice.Light, shell.ThemeChoice);
        Settle(window);
        Capture(window, "wp1-audit-filtered-light", 1280, 720);

        // Restore Dark via the toggle seam (two hops Light->System->Dark) — no global-state leak.
        shell.ToggleThemeCommand.Execute(null);
        shell.ToggleThemeCommand.Execute(null);
        Assert.Equal(AppThemeChoice.Dark, shell.ThemeChoice);
        Settle(window);
        window.Close();
    }

    [AvaloniaFact]
    public async Task AuditFilter_NoMatch_BothVariants()
    {
        var (window, shell) = ShowShell(1280, 720);
        var workspace = await DriveToWorkspaceAsync(shell);
        Dispatcher.UIThread.RunJobs();

        Assert.True(workspace.AuditCommand.CanExecute(null), "audit command must be armed after demo load");
        workspace.AuditCommand.Execute(null);

        var audit = Assert.IsType<AuditViewModel>(shell.CurrentStep);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(19, audit.TotalCount);

        // A contradictory pair: Error severity AND the Empty-groups rule class (12 Info rows, zero
        // Error). The two axes AND to an empty visible set -> HasNoMatches true, the dedicated empty
        // state renders.
        var errorChip = audit.SeverityChips.Single(c => c.Severity == RuleSeverity.Error);
        var emptyGroupRow = audit.Categories.Single(
            c => string.Equals(c.RuleId, RuleIds.EmptyGroup, StringComparison.OrdinalIgnoreCase));

        audit.ToggleFilterCommand.Execute(errorChip);
        audit.ToggleCategoryCommand.Execute(emptyGroupRow);
        Dispatcher.UIThread.RunJobs();

        Assert.True(errorChip.IsActive && emptyGroupRow.IsActive);
        Assert.True(audit.IsFiltered);
        Assert.True(audit.HasNoMatches, "the contradictory combo must hide every finding");
        Assert.False(audit.IsAllClear, "a scope WITH findings is never all-clear, even when all filtered out");
        Assert.Equal(0, audit.VisibleCount);
        Assert.Equal($"Showing 0 of {audit.TotalCount}", audit.FilterSummary);

        Assert.Equal(AppThemeChoice.Dark, shell.ThemeChoice);
        Settle(window);
        Capture(window, "wp1-audit-nomatch-dark", 1280, 720);

        shell.ToggleThemeCommand.Execute(null);
        Assert.Equal(AppThemeChoice.Light, shell.ThemeChoice);
        Settle(window);
        Capture(window, "wp1-audit-nomatch-light", 1280, 720);

        // Restore Dark via the toggle seam (two hops Light->System->Dark) — no global-state leak.
        shell.ToggleThemeCommand.Execute(null);
        shell.ToggleThemeCommand.Execute(null);
        Assert.Equal(AppThemeChoice.Dark, shell.ThemeChoice);
        Settle(window);
        window.Close();
    }

    private static void Settle(Avalonia.Controls.Window window)
    {
        for (var i = 0; i < 4; i++)
        {
            Dispatcher.UIThread.RunJobs();
            window.CaptureRenderedFrame()?.Dispose();
        }

        Dispatcher.UIThread.RunJobs();
    }

    private static (MainWindow Window, ShellViewModel Shell) ShowShell(int width, int height)
    {
        var uiStateBase = Directory.CreateTempSubdirectory("groupweaver-wp1-uistate-").FullName;
        var shell = new ShellViewModel(
            _ => new DemoProvider(), new StartupOptions(Demo: false), Present,
            graphRendererFactory: null, ruleset: null, locator: null,
            uiStateStore: new UiStateStore(uiStateBase));

        var window = new MainWindow { DataContext = shell, Width = width, Height = height };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return (window, shell);
    }

    private static async Task<WorkspaceViewModel> DriveToWorkspaceAsync(ShellViewModel shell)
    {
        var connect = Assert.IsType<ConnectionViewModel>(shell.CurrentStep);
        await connect.ConnectDemoCommand.ExecuteAsync(null);
        var picker = Assert.IsType<RootPickerViewModel>(shell.CurrentStep);
        await picker.LoadCandidates;
        Dispatcher.UIThread.RunJobs();

        picker.SelectedCandidate = picker.Candidates
            .First(c => c.Kind == AdObjectKind.OrganizationalUnit);
        picker.LoadRootCommand.Execute(null);
        var workspace = Assert.IsType<WorkspaceViewModel>(shell.CurrentStep);
        await workspace.Initialization;
        return workspace;
    }

    private static void Capture(Avalonia.Controls.Window window, string name, int width, int height)
    {
        Dispatcher.UIThread.RunJobs();
        window.CaptureRenderedFrame()?.Dispose();

        using var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        Assert.Equal(new PixelSize(width, height), frame!.PixelSize);

        var path = Path.Combine(ArtifactsUiDir.Value, $"{name}.png");
        frame.Save(path);
        Assert.True(new FileInfo(path).Length > 0, $"'{path}' is empty");
    }
}
