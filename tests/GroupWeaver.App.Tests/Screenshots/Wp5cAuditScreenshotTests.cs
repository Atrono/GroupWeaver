using System.Linq;

using Avalonia;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using Avalonia.Threading;

using GroupWeaver.App.Graph;
using GroupWeaver.App.Rules;
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
/// WP5c (#154) ui-verifier fixture: drives the real shell to the demo workspace, switches
/// into the Audit step via the workspace AuditCommand (so the live AuditViewModel is
/// CurrentStep), and captures the audit dashboard in BOTH theme variants via the
/// production ToggleThemeCommand seam — writing artifacts/ui/wp5c-audit-{dark,light}.png.
/// Demo-mode data only. Restores Dark on exit.
/// </summary>
public sealed class Wp5cAuditScreenshotTests
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
    public async Task Audit_BothVariants()
    {
        var (window, shell) = ShowShell(1280, 720);
        var workspace = await DriveToWorkspaceAsync(shell);
        Dispatcher.UIThread.RunJobs();

        Assert.True(workspace.AuditCommand.CanExecute(null), "audit command must be armed after demo load");
        workspace.AuditCommand.Execute(null);

        var audit = Assert.IsType<AuditViewModel>(shell.CurrentStep);
        Dispatcher.UIThread.RunJobs();

        // Demo baseline pins (so the screenshots judged are the documented scope).
        Assert.Equal(55, audit.Score);
        // ADR-030 (#188): the demo scope carries 4 live Errors (Critical > 0), so the band now gates to
        // "Action required" at any score — the scalar Score 55 is unchanged, but the qualitative band is
        // decoupled from it and led by the worst LIVE severity (was "Fair", the pure score band).
        Assert.Equal("Action required", audit.Band);
        Assert.Equal(4, audit.Critical);
        Assert.Equal(3, audit.Warnings);
        Assert.Equal(24, audit.Passing);
        Assert.Equal(6, audit.RuleClasses);
        Assert.True(audit.UncheckedPresent, "demo scope has unchecked areas");

        Assert.False(shell.IsLightTheme);
        Settle(window);
        Capture(window, "wp5c-audit-dark", 1280, 720);

        shell.ToggleThemeCommand.Execute(null);
        Assert.True(shell.IsLightTheme);
        Settle(window);
        Capture(window, "wp5c-audit-light", 1280, 720);

        shell.ToggleThemeCommand.Execute(null);
        Settle(window);
        window.Close();
    }

    /// <summary>
    /// ADR-030 D2 (#188) ui-verifier fixture: drives the demo workspace into the Audit step, arms the
    /// shell triage seam (<see cref="ShellViewModel.OnAudit"/>) over a SANDBOXED temp-dir
    /// <see cref="RulesetLocator"/> (so the Suppress write never touches real <c>%APPDATA%</c>), then
    /// Suppresses one Open finding so <see cref="AuditViewModel.HasTriaged"/> flips true and the D2
    /// triaged caveat renders beside the band. Captures BOTH theme variants —
    /// artifacts/ui/wp5c-audit-triaged-{dark,light}.png — for the ui-verifier to judge against
    /// docs/ui-checklist.md §Audit. PRODUCES the screenshots; it does NOT pixel-assert the caveat text.
    /// </summary>
    [AvaloniaFact(Timeout = 60_000)]
    public async Task Audit_TriagedVariant()
    {
        // Sandbox the triage write: a temp-dir RulesetLocator (the gate writes ONLY here, never %APPDATA%).
        var rulesetBase = Directory.CreateTempSubdirectory("groupweaver-wp5c-triaged-ruleset-").FullName;
        var locator = new RulesetLocator(rulesetBase);

        var (window, shell) = ShowShell(1280, 720, locator);
        var workspace = await DriveToWorkspaceAsync(shell);
        Dispatcher.UIThread.RunJobs();

        // Into the Audit step via OnAudit (it ARMS the triage seam: audit.UseTriageCallback → ApplyTriage
        // → the SettingsViewModel gate), so SuppressSelectedCommand actually drops a finding from the
        // live report. (Audit_BothVariants uses the workspace AuditCommand, which does NOT arm triage.)
        shell.OnAudit(workspace);
        var audit = Assert.IsType<AuditViewModel>(shell.CurrentStep);
        Dispatcher.UIThread.RunJobs();

        Assert.False(audit.HasTriaged, "no finding is triaged before the Suppress");

        // Suppress one Open finding => it drops from the LIVE report (TriagedCount > 0 => HasTriaged).
        var openRow = audit.Findings.First(r => !r.IsTriaged);
        openRow.IsSelected = true;
        audit.SuppressSelectedCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();

        Assert.True(audit.HasTriaged, "suppressing a finding must flip HasTriaged so the D2 caveat renders");
        Assert.True(audit.TriagedCount > 0);

        Assert.False(shell.IsLightTheme);
        Settle(window);
        Capture(window, "wp5c-audit-triaged-dark", 1280, 720);

        shell.ToggleThemeCommand.Execute(null);
        Assert.True(shell.IsLightTheme);
        Settle(window);
        Capture(window, "wp5c-audit-triaged-light", 1280, 720);

        shell.ToggleThemeCommand.Execute(null);
        Settle(window);
        shell.Dispose();
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

    private static (MainWindow Window, ShellViewModel Shell) ShowShell(int width, int height, RulesetLocator? locator = null)
    {
        var uiStateBase = Directory.CreateTempSubdirectory("groupweaver-wp5c-uistate-").FullName;
        var shell = new ShellViewModel(
            _ => new DemoProvider(), new StartupOptions(Demo: false), Present,
            graphRendererFactory: null,
            // The triaged variant injects a temp-dir RulesetLocator so the Suppress write is sandboxed
            // (the gate's atomic temp+move never touches real %APPDATA%, lab-environment.md / #124); the
            // ruleset seam follows it so the shell loads the same effective ruleset it will write back.
            ruleset: locator?.LoadEffective(),
            locator: locator,
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
