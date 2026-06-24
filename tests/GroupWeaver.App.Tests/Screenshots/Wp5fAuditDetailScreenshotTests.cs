using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;

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
/// WP5f (#160) ui-verifier fixture: drives the real shell to the demo workspace, switches into
/// the Audit step via the workspace AuditCommand (so the live AuditViewModel is CurrentStep),
/// selects ONE finding (the table SelectedFinding) so the detail pane populates, and captures
/// the two-pane Audit view in BOTH theme variants via the production ToggleThemeCommand seam.
/// It writes wp5f-detail-{dark,light}.png (top of the detail pane), wp5f-detail-banner-{dark,light}.png
/// (scrolled so the preview-only banner + top of the code block are in frame), and
/// wp5f-detail-snippet-{dark,light}.png (scrolled to the end so the full code block + Copy button
/// are in frame), plus a no-selection empty-state frame. Demo-mode data only. Restores Dark on exit.
/// </summary>
public sealed class Wp5fAuditDetailScreenshotTests
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
    public async Task AuditDetail_BothVariants()
    {
        const int width = 1280;
        const int height = 1000;

        var (window, shell) = ShowShell(width, height);
        var workspace = await DriveToWorkspaceAsync(shell);
        Dispatcher.UIThread.RunJobs();

        Assert.True(workspace.AuditCommand.CanExecute(null), "audit command must be armed after demo load");
        workspace.AuditCommand.Execute(null);

        var audit = Assert.IsType<AuditViewModel>(shell.CurrentStep);
        Dispatcher.UIThread.RunJobs();

        Assert.False(audit.HasDetail);
        Assert.False(shell.IsLightTheme);
        Settle(window);
        Capture(window, "wp5f-detail-empty-dark", width, height);

        var pick = audit.Findings.FirstOrDefault(f => f.Severity == RuleSeverity.Error)
            ?? audit.Findings.First();
        audit.SelectedFinding = pick;
        Dispatcher.UIThread.RunJobs();

        Assert.True(audit.HasDetail);
        Assert.NotNull(audit.Detail);
        Assert.False(pick.IsSelected, "selecting a row for detail must NOT toggle its triage checkbox");
        Assert.False(string.IsNullOrWhiteSpace(audit.Detail!.Snippet));

        CaptureDetailTriptych(window, "dark");

        shell.ToggleThemeCommand.Execute(null);
        Assert.True(shell.IsLightTheme);
        CaptureDetailTriptych(window, "light");

        shell.ToggleThemeCommand.Execute(null);
        Settle(window);
        window.Close();
    }

    /// <summary>Three captures of the populated detail pane in <paramref name="variant"/>: the top, a
    /// mid-scroll that frames the preview-only banner + top of the code block, and the end (full code
    /// block + Copy button). The detail content exceeds the pane viewport, so the banner/snippet/copy
    /// sit below the fold and are only judgeable by scrolling.</summary>
    private static void CaptureDetailTriptych(Window window, string variant)
    {
        const int width = 1280;
        const int height = 1000;

        ScrollDetail(window, _ => 0);
        Settle(window);
        Capture(window, $"wp5f-detail-{variant}", width, height);

        // Banner sits just above the code block; aim the viewport so the banner is near the top.
        // The extent minus a viewport-and-a-bit lands on the REMEDIATION eyebrow + banner.
        ScrollDetail(window, sv => Math.Max(0, sv.Extent.Height - sv.Viewport.Height - 150));
        Settle(window);
        Capture(window, $"wp5f-detail-banner-{variant}", width, height);

        ScrollDetail(window, sv => sv.Extent.Height);
        Settle(window);
        Capture(window, $"wp5f-detail-snippet-{variant}", width, height);
    }

    private static void ScrollDetail(Window window, Func<ScrollViewer, double> targetY)
    {
        var sv = window.GetVisualDescendants()
            .OfType<AuditView>()
            .Single()
            .GetVisualDescendants()
            .OfType<ScrollViewer>()
            .Where(s => s.GetVisualAncestors().OfType<ListBox>().FirstOrDefault() is null)
            .OrderByDescending(s => s.Extent.Height)
            .First();
        sv.Offset = new Vector(0, targetY(sv));
        Dispatcher.UIThread.RunJobs();
    }

    private static void Settle(Window window)
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
        var uiStateBase = Directory.CreateTempSubdirectory("groupweaver-wp5f-uistate-").FullName;
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

    private static void Capture(Window window, string name, int width, int height)
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
