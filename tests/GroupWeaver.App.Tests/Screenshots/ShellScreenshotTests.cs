using System.Runtime.InteropServices;

using Avalonia;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;

using GroupWeaver.App.Startup;
using GroupWeaver.App.ViewModels;
using GroupWeaver.App.Views;
using GroupWeaver.Core.Model;
using GroupWeaver.Providers;

using Xunit;

namespace GroupWeaver.App.Tests.Screenshots;

/// <summary>
/// The ui-verifier fixture (AP 2.1 S8, CLAUDE.md DoD step 2b): renders every shipped
/// shell state (AP 2.1 + the AP 2.5 detail panel) through the REAL pipeline — real
/// <see cref="DemoProvider"/>, real
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

    /// <summary>The AP 2.5 detail-panel subject: the demo dataset's first user.</summary>
    private const string DemoUserDn =
        "CN=Anna Acker (u001),OU=Users,OU=AGDLP-Demo,DC=weavedemo,DC=example";

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

    /// <summary>
    /// AP 3.4 S4 (ADR-010 §5): the violations sidebar topping the right column, above
    /// the AP 2.5 detail stack (the <c>2*,Auto,3*</c> vertical split, beside GraphHost,
    /// never over it — ADR-001 airspace). Driven to the settled workspace via the same
    /// <see cref="DriveToWorkspaceAsync"/> the other workspace frames use, default
    /// ruleset (the AP 3.2 demo baseline = 19 findings).
    ///
    /// DEFERRED to S5 (VM wiring): the workspace VM does not yet expose <c>Report</c>
    /// /<c>Violations</c> — that integration (Evaluate at LoadAsync/ExpandAsync, the
    /// <c>ViolationRowModel</c> projection, jump-to-node, selection sync) lands in S5.
    /// So this fixture pins ONLY what S4 owns: the sidebar VIEW renders inside the right
    /// column (the new <see cref="ViolationsSidebarView"/> region exists and is
    /// positioned right of GraphHost), with its design-time/empty collection. The
    /// populated-19-row screenshot assertions (header "Findings (19)", glyph rows in
    /// report order, the unchecked-areas hint) are added in S5 when the VM actually
    /// surfaces the report — until then this is red because neither
    /// <see cref="ViolationsSidebarView"/> nor the right-column split exists.
    /// </summary>
    [AvaloniaTheory]
    [InlineData(1280, 720)]
    [InlineData(1920, 1080)]
    public async Task WorkspaceViolations(int width, int height)
    {
        var (window, shell) = ShowShell(Present, width, height);
        var workspace = await DriveToWorkspaceAsync(shell);
        Assert.IsType<WorkspaceViewModel>(shell.CurrentStep);
        Dispatcher.UIThread.RunJobs();

        // S4 fixture soundness: the violations sidebar VIEW actually rendered, and it
        // lives in the right detail column (right of GraphHost — the airspace pin, as in
        // DetailPanelViewTests). Its content (real rows) is an S5 concern.
        var sidebar = Assert.Single(
            window.GetVisualDescendants().OfType<ViolationsSidebarView>());
        Assert.True(sidebar.IsEffectivelyVisible, "the violations sidebar must be rendered");

        var graphHost = Assert.Single(window.GetVisualDescendants().OfType<Avalonia.Controls.ContentControl>()
, c => c.Name == "GraphHost");
        var sidebarLeft = sidebar.TranslatePoint(new Point(0, 0), window);
        var graphRight = graphHost.TranslatePoint(
            new Point(graphHost.Bounds.Width, 0), window);
        Assert.NotNull(sidebarLeft);
        Assert.NotNull(graphRight);
        Assert.True(
            sidebarLeft.Value.X >= graphRight.Value.X - 0.5,
            $"the violations sidebar (X={sidebarLeft.Value.X}) must sit right of "
            + $"GraphHost (right edge X={graphRight.Value.X}) — never over the graph");

        CapturePng(window, "workspace-violations", width, height);
        window.Close();
    }

    /// <summary>
    /// AP 2.5 (ADR-007): a selected demo USER at the 5-row MAXIMUM of the user display
    /// set — description, whenCreated, department, title, primaryGroupID. The demo
    /// dataset gives its users only department + title, so the subject is upserted in
    /// the EXACT live-LDAP shape (all five whitelisted attributes, whenCreated in
    /// LdapEntry's normalized invariant-UTC form) through the same public AddObject
    /// upsert the expand pipeline uses — the sentinel approach of ConnectionError:
    /// the state is staged, the rendering path is the real one (snapshot →
    /// DetailPanelModel.Build → DetailPanelView).
    /// </summary>
    [AvaloniaTheory]
    [InlineData(1280, 720)]
    [InlineData(1920, 1080)]
    public async Task WorkspaceDetail(int width, int height)
    {
        var (window, shell) = ShowShell(Present, width, height);
        var workspace = await DriveToWorkspaceAsync(shell);
        var snapshot = workspace.Snapshot;
        Assert.NotNull(snapshot);

        Assert.True(snapshot.TryGetObject(DemoUserDn, out var demoUser));
        snapshot.AddObject(new AdObject
        {
            Dn = demoUser!.Dn,
            Kind = demoUser.Kind,
            Name = demoUser.Name,
            SamAccountName = demoUser.SamAccountName,
            Attributes = new Dictionary<string, string>(
                demoUser.Attributes, StringComparer.OrdinalIgnoreCase)
            {
                ["description"] = "Sales department staff account (screenshot sentinel).",
                ["whenCreated"] = "2024-03-18T09:30:00Z",
                ["primaryGroupID"] = "513",
            },
        });

        // Selection through the declared AP 2.5 seam (renderer-less workspace:
        // the public SelectedDn setter IS the click).
        workspace.SelectedDn = DemoUserDn;

        // Fixture soundness: the frame must show the 5-row maximum case in whitelist
        // declaration order (ADR-007 D4) — never the dataset's 2-row shape.
        var model = workspace.DetailPanel;
        Assert.NotNull(model);
        Assert.Equal(DetailPanelState.Loaded, model.State);
        Assert.Equal(AdObjectKind.User, model.Kind);
        Assert.Equal(
            new[] { "description", "whenCreated", "department", "title", "primaryGroupID" },
            model.Rows.Select(r => r.Label));

        CapturePng(window, "workspace-detail", width, height);
        window.Close();
    }

    /// <summary>
    /// AP 2.5 (ADR-007 D3): the NOT-LOADED panel state, staged the honest way — a
    /// group-rooted scope puts ONLY the group in Objects, so every member DN is a
    /// genuine frontier endpoint; selecting one renders the External badge, the DN
    /// verbatim, the "not loaded yet" hint, and zero attribute rows.
    /// </summary>
    [AvaloniaTheory]
    [InlineData(1280, 720)]
    [InlineData(1920, 1080)]
    public async Task WorkspaceDetailFrontier(int width, int height)
    {
        var (window, shell) = ShowShell(Present, width, height);
        var workspace = await DriveToWorkspaceAsync(
            shell, c => c.Name == "GG_Sales_Staff"); // 20 members, all out of scope
        var snapshot = workspace.Snapshot;
        Assert.NotNull(snapshot);

        var members = snapshot.GetMembers(workspace.RootDn);
        Assert.NotNull(members); // the group root itself is loaded by LoadScopeAsync
        Assert.NotEmpty(members);
        var frontierDn = members[0];
        Assert.False(
            snapshot.TryGetObject(frontierDn, out _),
            $"'{frontierDn}' is in Objects — not a frontier DN; the capture would lie");

        workspace.SelectedDn = frontierDn;

        // Fixture soundness: the frame must show the honest NotLoaded projection.
        var model = workspace.DetailPanel;
        Assert.NotNull(model);
        Assert.Equal(DetailPanelState.NotLoaded, model.State);
        Assert.Equal(AdObjectKind.External, model.Kind);
        Assert.Empty(model.Rows);

        CapturePng(window, "workspace-detail-frontier", width, height);
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

    /// <summary>Connect → pick → load, landing on the SETTLED workspace, which is
    /// returned for the AP 2.5 captures. <paramref name="pickRoot"/> chooses the root
    /// candidate; default: the first OU of the demo dataset — deterministic and
    /// representative.</summary>
    private static async Task<WorkspaceViewModel> DriveToWorkspaceAsync(
        ShellViewModel shell, Func<AdObject, bool>? pickRoot = null)
    {
        var picker = await ConnectIntoPickerAsync(shell);
        Dispatcher.UIThread.RunJobs();

        picker.SelectedCandidate = picker.Candidates
            .First(pickRoot ?? (c => c.Kind == AdObjectKind.OrganizationalUnit));
        picker.LoadRootCommand.Execute(null);
        var workspace = Assert.IsType<WorkspaceViewModel>(shell.CurrentStep);

        // S6: the workspace frames must capture the settled post-load state — never a
        // transient progress bar. Capture-and-discard (CapturePng) only fixes compositor
        // lag, not load timing, so the load itself is awaited here; the real
        // DemoProvider behind the shell makes this the genuine demo-mode truth.
        await workspace.Initialization;
        return workspace;
    }
}
