using System.Runtime.InteropServices;

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
/// WP1a (ADR-026) verification fixture: renders the key native screens in BOTH theme
/// variants by forcing <see cref="Application.RequestedThemeVariant"/> (the same seam
/// the production toggle uses) before each capture, writing
/// <c>artifacts/ui/wp1a-&lt;screen&gt;-&lt;variant&gt;.png</c> for the ui-verifier to judge.
/// Demo-mode data only. Each test restores the variant to Dark on exit so it never
/// leaks into the other screenshot fixtures sharing the headless app.
/// </summary>
public sealed class ThemeVariantScreenshotTests
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

    [AvaloniaFact]
    public async Task WorkspaceRail_BothVariants()
    {
        var (window, shell) = ShowShell(1280, 720);
        await DriveToWorkspaceAsync(shell);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(AppThemeChoice.Dark, shell.ThemeChoice);
        CapturePng(window, "wp1a-workspace-rail-dark", 1280, 720);

        shell.ToggleThemeCommand.Execute(null);
        Assert.Equal(AppThemeChoice.Light, shell.ThemeChoice);
        Settle(window);
        CapturePng(window, "wp1a-workspace-rail-light", 1280, 720);

        // Restore Dark through the toggle seam (the cycle is Dark->Light->System->Dark, so two
        // hops from Light) so the shared global RequestedThemeVariant never leaks into sibling fixtures.
        shell.ToggleThemeCommand.Execute(null);
        shell.ToggleThemeCommand.Execute(null);
        Assert.Equal(AppThemeChoice.Dark, shell.ThemeChoice);
        Settle(window);
        window.Close();
    }

    [AvaloniaFact]
    public async Task WorkspaceViolations_BothVariants()
    {
        var (window, shell) = ShowShell(1280, 720);
        var workspace = await DriveToWorkspaceAsync(shell);
        Dispatcher.UIThread.RunJobs();
        Assert.True(workspace.HasViolations);

        Assert.Equal(AppThemeChoice.Dark, shell.ThemeChoice);
        CapturePng(window, "wp1a-workspace-violations-dark", 1280, 720);

        shell.ToggleThemeCommand.Execute(null);
        Assert.Equal(AppThemeChoice.Light, shell.ThemeChoice);
        Settle(window);
        CapturePng(window, "wp1a-workspace-violations-light", 1280, 720);

        // Restore Dark via the toggle seam (two hops Light->System->Dark) — no global-state leak.
        shell.ToggleThemeCommand.Execute(null);
        shell.ToggleThemeCommand.Execute(null);
        Assert.Equal(AppThemeChoice.Dark, shell.ThemeChoice);
        Settle(window);
        window.Close();
    }

    [AvaloniaFact]
    public void Connection_BothVariants()
    {
        var (window, shell) = ShowShell(1280, 720);
        Assert.IsType<ConnectionViewModel>(shell.CurrentStep);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(AppThemeChoice.Dark, shell.ThemeChoice);
        CapturePng(window, "wp1a-connection-dark", 1280, 720);

        shell.ToggleThemeCommand.Execute(null);
        Assert.Equal(AppThemeChoice.Light, shell.ThemeChoice);
        Settle(window);
        CapturePng(window, "wp1a-connection-light", 1280, 720);

        // Restore Dark via the toggle seam (two hops Light->System->Dark) — no global-state leak.
        shell.ToggleThemeCommand.Execute(null);
        shell.ToggleThemeCommand.Execute(null);
        Assert.Equal(AppThemeChoice.Dark, shell.ThemeChoice);
        Settle(window);
        window.Close();
    }

    [AvaloniaFact]
    public void Settings_BothVariants()
    {
        SetVariant(false);
        var vm = SettingsViewModel.LoadFrom(RulesetLoader.LoadDefault());
        var window = new SettingsWindow { DataContext = vm, Width = 1280, Height = 720 };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        CaptureWindowPng(window, "wp1a-settings-rules-dark", 1280, 720);

        SetVariant(true);
        Settle(window);
        CaptureWindowPng(window, "wp1a-settings-rules-light", 1280, 720);

        SetVariant(false);
        Settle(window);
        window.Close();
    }

    /// <summary>The theme toggle button is present in the top command strip on the workspace.</summary>
    [AvaloniaFact]
    public async Task ThemeToggleButton_Present()
    {
        var (window, shell) = ShowShell(1280, 720);
        await DriveToWorkspaceAsync(shell);
        Dispatcher.UIThread.RunJobs();

        var toggle = window.GetVisualDescendants()
            .OfType<Avalonia.Controls.Button>()
            .FirstOrDefault(b => b.Name == "ThemeToggleButton");
        Assert.NotNull(toggle);
        Assert.True(toggle!.IsEffectivelyVisible, "the theme toggle must be visible in the top strip");
        window.Close();
    }

    /// <summary>Pumps several render batches so a RequestedThemeVariant flip re-resolves the
    /// DynamicResource brushes and the compositor settles before capture.</summary>
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
        var uiStateBase = Directory.CreateTempSubdirectory("groupweaver-theme-uistate-").FullName;
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

    private static void CapturePng(MainWindow window, string name, int width, int height) =>
        Capture(window, name, width, height);

    private static void CaptureWindowPng(Avalonia.Controls.Window window, string name, int width, int height) =>
        Capture(window, name, width, height);

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
