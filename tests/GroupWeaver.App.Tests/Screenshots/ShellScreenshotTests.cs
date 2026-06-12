using System.Runtime.InteropServices;

using Avalonia;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using Avalonia.Threading;

using GroupWeaver.App.Startup;
using GroupWeaver.App.ViewModels;
using GroupWeaver.App.Views;
using GroupWeaver.Core.Model;
using GroupWeaver.Providers;

using Xunit;

namespace GroupWeaver.App.Tests.Screenshots;

/// <summary>
/// The ui-verifier fixture (AP 2.1 S8, CLAUDE.md DoD step 2b): renders every shipped
/// AP 2.1 shell state through the REAL pipeline — real <see cref="DemoProvider"/>, real
/// views, real Skia rasterization on the headless platform — and writes the frames to
/// <c>artifacts/ui/&lt;view&gt;-&lt;W&gt;x&lt;H&gt;.png</c> (gitignored) at both checklist
/// sizes, 1280x720 and 1920x1080. The ui-verifier judges these PNGs against
/// <c>docs/ui-checklist.md</c> section B; the assertions here only guarantee the fixture
/// itself is sound (window actually sized, frame actually rendered, file actually
/// written) — visual judgment is the verifier's job, not xUnit's.
/// </summary>
public sealed class ShellScreenshotTests
{
    private static readonly WebView2RuntimeStatus Present = new(IsInstalled: true, Version: "x");
    private static readonly WebView2RuntimeStatus Missing = new(IsInstalled: false, Version: null);

    /// <summary><c>artifacts/ui</c> under the repo root, created on first use.</summary>
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

    // --- Connect step --------------------------------------------------------------------

    [AvaloniaTheory]
    [InlineData(1280, 720)]
    [InlineData(1920, 1080)]
    public void ConnectionIdle(int width, int height)
    {
        var (window, shell) = ShowShell(Present, width, height);
        Assert.IsType<ConnectionViewModel>(shell.CurrentStep);

        CapturePng(window, "connection-idle", width, height);
        window.Close();
    }

    [AvaloniaTheory]
    [InlineData(1280, 720)]
    [InlineData(1920, 1080)]
    public void ConnectionError(int width, int height)
    {
        var (window, shell) = ShowShell(Present, width, height);
        var connect = Assert.IsType<ConnectionViewModel>(shell.CurrentStep);

        // Sentinel error in the exact shape the live path produces (message + demo-mode
        // hint on a second line) so the verifier judges the real error layout.
        connect.ErrorMessage =
            "No domain controller answered (screenshot sentinel).\n"
            + "No domain is reachable in this user context — try Demo mode for the embedded demo directory.";

        CapturePng(window, "connection-error", width, height);
        window.Close();
    }

    // --- PickRoot step ---------------------------------------------------------------------

    [AvaloniaTheory]
    [InlineData(1280, 720)]
    [InlineData(1920, 1080)]
    public async Task RootPickerDemo(int width, int height)
    {
        var (window, shell) = ShowShell(Present, width, height);
        var picker = await ConnectIntoPickerAsync(shell);
        Dispatcher.UIThread.RunJobs();

        // One candidate selected: the frame must show the selection highlight AND the
        // mandatory entry filter satisfied (Load enabled) — assert the latter so the
        // fixture cannot silently capture a disabled Load button.
        picker.SelectedCandidate = picker.Candidates[0];
        Assert.True(picker.LoadRootCommand.CanExecute(null));

        CapturePng(window, "rootpicker-demo", width, height);

        // Tail frame: the name-sorted list puts every GG_*/UG_* row below the scroll
        // fold, so the head frame above can never evidence those two badge kinds.
        // Selecting the LAST UniversalGroup makes the ListBox (AutoScrollToSelectedItem
        // is on by default) scroll the tail into view: GG and UG badges both in frame,
        // plus the selection highlight re-evidenced on a UG row.
        picker.SelectedCandidate = picker.Candidates
            .Last(c => c.Kind == AdObjectKind.UniversalGroup);
        Assert.True(picker.LoadRootCommand.CanExecute(null));

        CapturePng(window, "rootpicker-demo-tail", width, height);
        window.Close();
    }

    // --- Workspace step ----------------------------------------------------------------------

    [AvaloniaTheory]
    [InlineData(1280, 720)]
    [InlineData(1920, 1080)]
    public async Task WorkspaceDemo(int width, int height)
    {
        var (window, shell) = ShowShell(Present, width, height);
        await DriveToWorkspaceAsync(shell);

        CapturePng(window, "workspace-demo", width, height);
        window.Close();
    }

    [AvaloniaTheory]
    [InlineData(1280, 720)]
    [InlineData(1920, 1080)]
    public async Task WorkspaceWebView2Missing(int width, int height)
    {
        var (window, shell) = ShowShell(Missing, width, height);
        await DriveToWorkspaceAsync(shell);

        CapturePng(window, "workspace-webview2-missing", width, height);
        window.Close();
    }

    // --- capture core ---------------------------------------------------------------------------

    /// <summary>
    /// Flush pending jobs, capture the rendered frame, prove it is a real rasterization
    /// of the requested size (not a stub or a blank), and write the PNG.
    /// </summary>
    private static void CapturePng(MainWindow window, string name, int width, int height)
    {
        Dispatcher.UIThread.RunJobs();

        // The headless compositor renders ONE committed batch per render-timer tick, so
        // the first capture after a state mutation returns the PREVIOUS frame (verified
        // empirically: single-capture made connection-error byte-identical to
        // connection-idle). Capture-and-discard flushes the pending batch; the second
        // capture is current. Deterministic — no sleeps, no retries.
        window.CaptureRenderedFrame()?.Dispose();

        using var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        Assert.Equal(new PixelSize(width, height), frame.PixelSize);
        AssertSampledPixelsNonUniform(frame, name);

        var path = Path.Combine(ArtifactsUiDir.Value, $"{name}-{width}x{height}.png");
        frame.Save(path);

        var file = new FileInfo(path);
        Assert.True(file.Exists, $"'{path}' was not written");
        Assert.True(file.Length > 0, $"'{path}' is empty");
    }

    /// <summary>
    /// Non-trivial-frame gate: sample a 32x32 grid and require at least two distinct
    /// pixel values. Robust by construction — every shell state renders text on a
    /// background, while a failed capture is uniformly blank. Deliberately NOT a
    /// file-size threshold: PNG compression of large near-empty frames sits exactly
    /// where a byte cutoff turns flaky.
    /// </summary>
    private static void AssertSampledPixelsNonUniform(WriteableBitmap frame, string name)
    {
        using var fb = frame.Lock();
        Assert.Equal(32, fb.Format.BitsPerPixel); // sampling below reads 4-byte pixels

        var first = Marshal.ReadInt32(fb.Address);
        var stepX = Math.Max(1, fb.Size.Width / 32);
        var stepY = Math.Max(1, fb.Size.Height / 32);
        for (var y = 0; y < fb.Size.Height; y += stepY)
        {
            for (var x = 0; x < fb.Size.Width; x += stepX)
            {
                if (Marshal.ReadInt32(fb.Address, (y * fb.RowBytes) + (x * 4)) != first)
                {
                    return;
                }
            }
        }

        Assert.Fail($"'{name}': every sampled pixel is identical — a blank frame, not the rendered shell");
    }

    // --- shell driving ----------------------------------------------------------------------------

    /// <summary>Real DemoProvider behind the factory: these frames are the demo-mode truth.</summary>
    private static (MainWindow Window, ShellViewModel Shell) ShowShell(
        WebView2RuntimeStatus status, int width, int height)
    {
        var shell = new ShellViewModel(
            _ => new DemoProvider(), new StartupOptions(Demo: false), status);

        // Size BEFORE Show so every layout pass — including ListBox virtualization —
        // happens against the final viewport.
        var window = new MainWindow { DataContext = shell, Width = width, Height = height };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return (window, shell);
    }

    private static async Task<RootPickerViewModel> ConnectIntoPickerAsync(ShellViewModel shell)
    {
        var connect = Assert.IsType<ConnectionViewModel>(shell.CurrentStep);
        await connect.ConnectDemoCommand.ExecuteAsync(null);
        var picker = Assert.IsType<RootPickerViewModel>(shell.CurrentStep);
        await picker.LoadCandidates;
        return picker;
    }

    private static async Task DriveToWorkspaceAsync(ShellViewModel shell)
    {
        var picker = await ConnectIntoPickerAsync(shell);
        Dispatcher.UIThread.RunJobs();

        // A deterministic, representative root: the first OU of the demo dataset.
        picker.SelectedCandidate = picker.Candidates
            .First(c => c.Kind == AdObjectKind.OrganizationalUnit);
        picker.LoadRootCommand.Execute(null);
        var workspace = Assert.IsType<WorkspaceViewModel>(shell.CurrentStep);

        // S6: the workspace frames must capture the settled post-load state — never a
        // transient progress bar. Capture-and-discard (CapturePng) only fixes compositor
        // lag, not load timing, so the load itself is awaited here; the real
        // DemoProvider behind the shell makes this the genuine demo-mode truth.
        await workspace.Initialization;
    }
}
