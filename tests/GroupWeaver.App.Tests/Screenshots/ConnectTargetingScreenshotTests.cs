using Avalonia;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;

using GroupWeaver.App.Settings;
using GroupWeaver.App.Startup;
using GroupWeaver.App.ViewModels;
using GroupWeaver.App.Views;
using GroupWeaver.Core.Model;
using GroupWeaver.Core.Providers;
using GroupWeaver.Providers;

using Xunit;

namespace GroupWeaver.App.Tests.Screenshots;

/// <summary>
/// ADR-031 (#189) ui-verifier fixture: renders the Connect card with the "Advanced — target a
/// specific domain or DC" disclosure EXPANDED (the server + base-DN fields shown, the pre-bind
/// target line tracking the entered values), in BOTH theme variants via the production
/// ToggleThemeCommand seam — writing artifacts/ui/connect-advanced-{dark,light}.png for the
/// ui-verifier to judge against docs/ui-checklist.md §Connect. PRODUCES the PNGs; it does NOT
/// pixel-assert. No domain needed — the Connect step is the fresh shell's first step.
/// Restores Dark on exit. Injects a temp-dir UiStateStore (#124 isolation seam — never reads/writes
/// real %APPDATA%).
/// </summary>
public sealed class ConnectTargetingScreenshotTests
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
    public void ConnectAdvanced_BothVariants()
    {
        var (window, shell) = ShowShell(1280, 720);

        var connect = Assert.IsType<ConnectionViewModel>(shell.CurrentStep);

        // Open the Advanced disclosure and populate both fields so the server + base-DN inputs and
        // the pre-bind target line are visible in the captured frame (the ui-verifier judges them).
        connect.ToggleAdvancedCommand.Execute(null);
        connect.TargetServer = "dc1.corp.example";
        connect.TargetBaseDn = "OU=Groups,DC=corp,DC=example";
        Dispatcher.UIThread.RunJobs();

        Assert.True(connect.IsAdvancedExpanded, "the Advanced disclosure must be open for this capture");
        Assert.Equal(
            $"as {connect.CurrentUserContext} against dc1.corp.example — OU=Groups,DC=corp,DC=example",
            connect.TargetLine);

        Assert.False(shell.IsLightTheme);
        Settle(window);
        Capture(window, "connect-advanced-dark", 1280, 720);

        shell.ToggleThemeCommand.Execute(null);
        Assert.True(shell.IsLightTheme);
        Settle(window);
        Capture(window, "connect-advanced-light", 1280, 720);

        shell.ToggleThemeCommand.Execute(null);
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
        var uiStateBase = Directory.CreateTempSubdirectory("groupweaver-connect-adv-uistate-").FullName;
        var shell = new ShellViewModel(
            _ => new DemoProvider(),
            new StartupOptions(Demo: false),
            Present,
            graphRendererFactory: null,
            ruleset: null,
            locator: null,
            uiStateStore: new UiStateStore(uiStateBase));

        var window = new MainWindow { DataContext = shell, Width = width, Height = height };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return (window, shell);
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
