using Avalonia;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
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
/// WP2 (ADR-026 D6) window-chrome polish verification fixture: renders the top command
/// strip (with the always-on Read-only pill AND the demo-gated DEMO badge), the Connection
/// screen (its accent "Connect" button) and the Settings window (its accent "Save" button)
/// in BOTH theme variants, writing artifacts/ui/wp2-*.png for the ui-verifier to judge.
/// Demo-mode data only. The theme is forced via ToggleThemeCommand AFTER window.Show() then
/// render batches are pumped (the WP1a gotcha). Restores Dark on exit.
/// </summary>
public sealed class Wp2ChromePolishScreenshotTests
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
        return Directory.CreateDirectory(Path.Combine(dir.FullName, "artifacts", "ui")).FullName;
    });

    /// <summary>
    /// The top command strip with BOTH badges: driving into the workspace via the demo path
    /// sets IsDemoMode=true (the honest signal ConnectionViewModel threads to OnConnected), so
    /// the demo-gated DEMO badge shows alongside the always-on Read-only pill. Captured in both
    /// themes. The whole window is captured (the strip lives across the top).
    /// </summary>
    [AvaloniaFact]
    public async Task CommandStrip_BothBadges_BothVariants()
    {
        var (window, shell) = ShowShell(1280, 720);
        await DriveToWorkspaceAsync(shell);
        Dispatcher.UIThread.RunJobs();

        // The demo path set IsDemoMode (so the DEMO badge shows); the Read-only pill is always on.
        Assert.True(shell.IsDemoMode, "the demo connection must flag IsDemoMode so the DEMO badge shows");
        Assert.Equal(AppThemeChoice.Dark, shell.ThemeChoice);

        Settle(window);
        Capture(window, "wp2-strip-dark", 1280, 720);

        shell.ToggleThemeCommand.Execute(null);
        Assert.Equal(AppThemeChoice.Light, shell.ThemeChoice);
        Settle(window);
        Capture(window, "wp2-strip-light", 1280, 720);

        // Restore Dark via the toggle seam (two hops Light->System->Dark) — no global-state leak.
        shell.ToggleThemeCommand.Execute(null);
        shell.ToggleThemeCommand.Execute(null);
        Assert.Equal(AppThemeChoice.Dark, shell.ThemeChoice);
        Settle(window);
        window.Close();
    }

    /// <summary>The Connection screen and its accent "Connect to domain" button in both themes.</summary>
    [AvaloniaFact]
    public void Connection_AccentButton_BothVariants()
    {
        var (window, shell) = ShowShell(1280, 720);
        Assert.IsType<ConnectionViewModel>(shell.CurrentStep);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(AppThemeChoice.Dark, shell.ThemeChoice);
        Settle(window);
        Capture(window, "wp2-connection-dark", 1280, 720);

        shell.ToggleThemeCommand.Execute(null);
        Assert.Equal(AppThemeChoice.Light, shell.ThemeChoice);
        Settle(window);
        Capture(window, "wp2-connection-light", 1280, 720);

        // Restore Dark via the toggle seam (two hops Light->System->Dark) — no global-state leak.
        shell.ToggleThemeCommand.Execute(null);
        shell.ToggleThemeCommand.Execute(null);
        Assert.Equal(AppThemeChoice.Dark, shell.ThemeChoice);
        Settle(window);
        window.Close();
    }

    /// <summary>The Settings window and its accent "Save" button in both themes.</summary>
    [AvaloniaFact]
    public void Settings_AccentSave_BothVariants()
    {
        SetVariant(false);
        var vm = SettingsViewModel.LoadFrom(RulesetLoader.LoadDefault());
        var window = new SettingsWindow { DataContext = vm, Width = 1280, Height = 720 };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        Settle(window);
        Capture(window, "wp2-settings-save-dark", 1280, 720);

        SetVariant(true);
        Settle(window);
        Capture(window, "wp2-settings-save-light", 1280, 720);

        SetVariant(false);
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

    private static void SetVariant(bool light)
    {
        if (Application.Current is { } app)
        {
            app.RequestedThemeVariant = light ? ThemeVariant.Light : ThemeVariant.Dark;
        }
    }

    private static (MainWindow Window, ShellViewModel Shell) ShowShell(int width, int height)
    {
        var uiStateBase = Directory.CreateTempSubdirectory("groupweaver-wp2-uistate-").FullName;
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
        Assert.Equal(new PixelSize(width, height), frame.PixelSize);

        var path = Path.Combine(ArtifactsUiDir.Value, $"{name}.png");
        frame.Save(path);
        Assert.True(new FileInfo(path).Length > 0, $"'{path}' is empty");
    }
}
