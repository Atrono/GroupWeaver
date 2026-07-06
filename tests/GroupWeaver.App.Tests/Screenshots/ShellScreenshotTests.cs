using System.Globalization;
using System.Runtime.InteropServices;

using Avalonia;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;

using GroupWeaver.App.Graph;
using GroupWeaver.App.Rules;
using GroupWeaver.App.Settings;
using GroupWeaver.App.Startup;
using GroupWeaver.App.Tests.Fakes;
using GroupWeaver.App.ViewModels;
using GroupWeaver.App.Views;
using GroupWeaver.Core.Model;
using GroupWeaver.Core.Rules;
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
    /// Issue #268 (crosscut-1): the WebView2-missing banner's LIGHT-theme rendering — never
    /// screenshotted before this test, since <see cref="WorkspaceWebView2Missing"/> only ever
    /// rendered the app-default Dark theme. The banner's background/border moved off a hardcoded
    /// hex onto the theme-scoped <c>WebView2MissingBackgroundBrush</c>/<c>WebView2MissingBorderBrush</c>
    /// resources (<c>src/App/Styles/Tokens.axaml</c>), each with its own Light variant tuned to clear
    /// WCAG 1.4.11 (>=3:1) against <c>PageBackgroundBrush</c> — this is the fixture that lets the
    /// ui-verifier actually judge that Light variant instead of taking the contrast numbers on faith.
    /// Uses the WINDOW-SCOPED theme seam (parity with <c>ViolationsSidebarViewTests</c>'
    /// <c>ActiveRowBand_ReTonesLive_WhenTheThemeFlipsToLight</c>), not the shared
    /// <c>Application.Current.RequestedThemeVariant</c> flip <c>ThemeVariantScreenshotTests</c> warns
    /// is flaky — no restore step, no leak into sibling fixtures.
    /// </summary>
    [AvaloniaTheory]
    [InlineData(1280, 720)]
    [InlineData(1920, 1080)]
    public async Task WorkspaceWebView2Missing_LightTheme(int width, int height)
    {
        var (window, shell) = ShowShell(Missing, width, height, theme: Avalonia.Styling.ThemeVariant.Light);
        await DriveToWorkspaceAsync(shell);

        CapturePng(window, "workspace-webview2-missing-light", width, height);
        window.Close();
    }

    // --- ADR-022: adaptive rail + focus mode ------------------------------------------------

    /// <summary>
    /// ADR-022 D5 (reframed, #186): a loaded workspace with NOTHING selected (the default). The
    /// reclaimed rail shows the compact <c>ScopeSummaryCard</c> (object/edge totals + the active
    /// <b>ruleset name</b> + the hint) instead of the old centered "Click a node…" void. The
    /// fixture pins soundness the PNG can't: the named card Border is realized + effectively-visible
    /// (it binds <c>DetailPanel is null</c>, which holds with no selection) AND the workspace's
    /// <see cref="WorkspaceViewModel.RulesetName"/> is non-empty (the active-ruleset line is the
    /// card's "reads as information" proof) — so the empty rail reads as information. The
    /// ui-verifier judges the rendered frame against the new section-B scope-summary rows.
    /// </summary>
    [AvaloniaTheory]
    [InlineData(1280, 720)]
    [InlineData(1920, 1080)]
    public async Task WorkspaceScopeSummary(int width, int height)
    {
        var (window, shell) = ShowShell(Present, width, height);
        var workspace = await DriveToWorkspaceAsync(shell);
        Dispatcher.UIThread.RunJobs();

        // Nothing selected: the D5 void-fill card is the captured surface (DetailPanel is null).
        Assert.Null(workspace.SelectedDn);
        Assert.Null(workspace.DetailPanel);

        // Force a render pass so the DetailPanelRegion ContentControl realizes its inline content
        // (the card lives inside a ContentPresenter — unlike the directly-gridded sidebar, it
        // materializes only once the window renders). Capture-and-discard flushes the batch.
        window.CaptureRenderedFrame()?.Dispose();
        Dispatcher.UIThread.RunJobs();

        var card = Assert.Single(window.GetVisualDescendants()
            .OfType<Avalonia.Controls.Border>(), b => b.Name == "ScopeSummaryCard");
        Assert.True(
            card.IsEffectivelyVisible,
            "with nothing selected the scope-summary card must replace the empty-rail void (D5)");

        // The active-ruleset line is populated — the empty rail reads as information, not a void.
        Assert.False(string.IsNullOrWhiteSpace(workspace.RulesetName));

        CapturePng(window, "workspace-scope-summary", width, height);
        window.Close();
    }

    /// <summary>
    /// ADR-022 D3: a loaded workspace with the rail COLLAPSED (<see cref="WorkspaceViewModel.IsRailCollapsed"/>
    /// true). The rail column collapses to 0 and the whole rail Border (with the violations sidebar)
    /// drops out of the layout, leaving only the thin native seam + ▸ expand chevron beside GraphHost,
    /// which still fills. The fixture pins soundness the PNG can't: the <c>ViolationsSidebarView</c>
    /// (the rail content) is NOT effectively-visible once collapsed, while GraphHost stays realized —
    /// the graph reclaims the width. The ui-verifier judges the collapsed-rail frame.
    /// </summary>
    [AvaloniaTheory]
    [InlineData(1280, 720)]
    [InlineData(1920, 1080)]
    public async Task WorkspaceRailCollapsed(int width, int height)
    {
        var (window, shell) = ShowShell(Present, width, height);
        var workspace = await DriveToWorkspaceAsync(shell);
        Dispatcher.UIThread.RunJobs();

        // The rail is visible before collapse (the sidebar realizes), then collapses to nothing.
        var sidebar = Assert.Single(window.GetVisualDescendants().OfType<ViolationsSidebarView>());
        Assert.True(sidebar.IsEffectivelyVisible, "the rail must be visible before collapse");

        workspace.IsRailCollapsed = true;
        Dispatcher.UIThread.RunJobs();

        // Collapsed: the rail (and its sidebar) is no longer effectively-visible — the rail Border
        // binds IsVisible="!IsRailCollapsed" and the column width is 0.
        Assert.False(
            sidebar.IsEffectivelyVisible,
            "a collapsed rail must drop its sidebar out of the layout (D3)");

        // GraphHost still fills — the graph reclaimed the rail's width (never re-laid-out, just reflowed).
        var graphHost = Assert.Single(window.GetVisualDescendants()
            .OfType<Avalonia.Controls.ContentControl>(), c => c.Name == "GraphHost");
        Assert.True(graphHost.IsEffectivelyVisible, "GraphHost must still fill when the rail collapses");
        Assert.True(graphHost.Bounds.Width > 0, "GraphHost must keep a positive width");

        // The collapse is a large relayout (the rail drops out, GraphHost widens) — flush an extra
        // compositor batch so the settled frame is captured, not a transient blank mid-relayout.
        window.CaptureRenderedFrame()?.Dispose();
        Dispatcher.UIThread.RunJobs();

        CapturePng(window, "workspace-rail-collapsed", width, height);
        window.Close();
    }

    /// <summary>
    /// ADR-022 D2: focus (presentation) mode on a loaded workspace, driven through
    /// <see cref="ShellViewModel.ToggleFocusModeCommand"/> (the same seam the workspace "Focus"
    /// button reaches via its installed callback). The top command strip hides
    /// (<c>IsVisible="!IsFocusMode"</c>) and the active workspace rail collapses, giving the graph
    /// the whole frame. The fixture pins soundness the PNG can't: <see cref="ShellViewModel.IsFocusMode"/>
    /// is true, the top command-strip Border (the ⚙ Settings bar) is NOT effectively-visible, and the
    /// workspace rail collapsed. The ui-verifier judges the strip-gone focus frame.
    ///
    /// <para>Unlike the other workspace frames, this one mounts a graph-surface stand-in into
    /// GraphHost (a filled <see cref="Avalonia.Controls.Border"/> behind the renderer seam) — the
    /// faithful focus-mode fixture is "the graph FILLS the screen" (strip gone, rail gone). The real
    /// WebView2 graph cannot render headless, so the filled surface stands in for it; it also makes
    /// the captured frame a non-uniform raster (the sparse renderer-LESS placeholder is a thin
    /// centered block the void-of-chrome focus frame would otherwise reduce to).</para>
    /// </summary>
    [AvaloniaTheory]
    [InlineData(1280, 720)]
    [InlineData(1920, 1080)]
    public async Task WorkspaceFocus(int width, int height)
    {
        // A graph surface that FILLS GraphHost (the focus-mode intent: the graph gets the whole
        // screen). Mounted through the renderer seam, replacing the placeholder — exactly the real
        // mount path, with a filled Border standing in for the headless-unavailable WebView2 graph.
        var graphSurface = new Avalonia.Controls.Border { Background = Brushes.SteelBlue };
        var (window, shell) = ShowShell(
            Present, width, height, () => new FakeGraphRenderer { View = graphSurface });
        var workspace = await DriveToWorkspaceAsync(shell);
        Dispatcher.UIThread.RunJobs();

        // The top command strip (the ⚙ Settings bar) is the Border ancestor of that button's label.
        var strip = CommandStripBorder(window);
        Assert.True(strip.IsEffectivelyVisible, "the top command strip must show before focus mode");

        shell.ToggleFocusModeCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();

        // Focus on: shell flag set, the strip gone, the active workspace rail collapsed (D2).
        Assert.True(shell.IsFocusMode);
        Assert.False(
            strip.IsEffectivelyVisible,
            "focus mode must hide the top command strip (IsVisible binds !IsFocusMode)");
        Assert.True(workspace.IsRailCollapsed, "focus mode must collapse the active workspace rail");

        // The graph surface fills GraphHost — the strip-and-rail-free focus frame is all graph.
        Assert.True(graphSurface.IsEffectivelyVisible, "the graph surface must fill GraphHost in focus mode");

        // ADR-022 addendum: the "⛶ Focus" button lives INSIDE the strip Border, so once focus mode
        // hides the whole strip the button is no longer effectively-visible either — entered from
        // the strip, the strip melts away, and Esc/F (not the button) bring it back. Pins that the
        // entry point shares the strip's fate (no orphaned, still-clickable button over the graph).
        var focusButton = FocusModeButton(window);
        Assert.False(
            focusButton!.IsEffectivelyVisible,
            "the Focus button (inside the hidden strip) must not be effectively-visible in focus mode");

        CapturePng(window, "workspace-focus", width, height);
        window.Close();
    }

    /// <summary>
    /// ADR-022 addendum (the focus-mode ENTRY POINT D2 specified but the first cut omitted): the
    /// "⛶ Focus" toggle in the top command strip. A soundness pin (not a screenshot): on a loaded
    /// workspace the named <c>FocusModeButton</c> is realized AND effectively-visible (it binds
    /// <c>IsVisible="{Binding IsWorkspaceStep}"</c>, true on the workspace), so focus mode is
    /// user-reachable; on the Connect step the same button is NOT effectively-visible (and may not
    /// be realized at all), so the Connect/RootPicker strips stay byte-identical. Without a control
    /// the addendum closes, focus mode shipped reachable only programmatically.
    /// </summary>
    [AvaloniaFact]
    public async Task FocusModeButton_VisibleOnWorkspace_HiddenOnConnect()
    {
        // Workspace: the entry point is realized and visible — focus mode is user-reachable.
        var (workspaceWindow, workspaceShell) = ShowShell(Present, 1280, 720);
        await DriveToWorkspaceAsync(workspaceShell);
        Dispatcher.UIThread.RunJobs();

        var onWorkspace = FocusModeButton(workspaceWindow);
        Assert.NotNull(onWorkspace);
        Assert.True(
            onWorkspace!.IsEffectivelyVisible,
            "the Focus button must be visible on the workspace step (IsVisible binds IsWorkspaceStep)");
        workspaceWindow.Close();

        // Connect step (no drive): the strip's StackPanel realizes, but the Focus button binds
        // IsWorkspaceStep=false, so it is collapsed (or not realized) — never a live affordance.
        var (connectWindow, connectShell) = ShowShell(Present, 1280, 720);
        Assert.IsType<ConnectionViewModel>(connectShell.CurrentStep);
        Dispatcher.UIThread.RunJobs();

        var onConnect = FocusModeButton(connectWindow);
        Assert.True(
            onConnect is null || !onConnect.IsEffectivelyVisible,
            "the Focus button must not be a live affordance off the workspace step");
        connectWindow.Close();
    }

    /// <summary>
    /// ADR-022 addendum, the <c>[I]</c> INTERACTIVE layer: a single <c>F</c> key on the workspace
    /// toggles focus mode through the REAL input pipeline — a dispatched headless KeyDown reaches
    /// <see cref="MainWindow.OnKeyDown"/>, which (gated to the workspace step) executes
    /// <see cref="ShellViewModel.ToggleFocusModeCommand"/>, flipping <see cref="ShellViewModel.IsFocusMode"/>
    /// and collapsing the active workspace rail (D2). F again exits. This is the gesture the demo
    /// recorder posts via <c>WM_KEYDOWN</c> (single-key, not a chord) so the chrome melts away. The
    /// key is delivered with Avalonia.Headless's <c>KeyPressQwerty</c> extension (physical→Key.F,
    /// down+up) — a genuine dispatched key, NOT the command path, so this pins the OnKeyDown wiring.
    ///
    /// <para>#230 / ADR-022 third addendum ("keyboard-focus continuity"): the focus parking fires on
    /// the <see cref="ShellViewModel.IsFocusMode"/> property change, so the <c>F</c> vector must park
    /// keyboard focus on the seam chevron exactly like the button vector — and the SECOND <c>F</c>
    /// below is therefore dispatched while the chevron HOLDS focus, pinning that a focused chevron
    /// Button still lets the key bubble to <see cref="MainWindow.OnKeyDown"/>.</para>
    /// </summary>
    [AvaloniaFact]
    public async Task WorkspaceFocus_FKey_TogglesFocusMode_BothWays()
    {
        var (window, shell) = ShowShell(Present, 1280, 720);
        var workspace = await DriveToWorkspaceAsync(shell);
        Dispatcher.UIThread.RunJobs();

        Assert.False(shell.IsFocusMode);
        Assert.False(workspace.IsRailCollapsed);

        // A real dispatched 'F' KeyDown (QWERTY physical→Key.F; down+up) through the input pipeline.
        window.KeyPressQwerty(PhysicalKey.F, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();

        Assert.True(shell.IsFocusMode, "a dispatched 'F' on the workspace must enter focus mode (OnKeyDown)");
        Assert.True(workspace.IsRailCollapsed, "focus mode must collapse the active workspace rail (D2)");

        // #230: entry parking is vector-agnostic — 'F' must park keyboard focus on the surviving
        // seam chevron too, so the second 'F' below is dispatched WHILE the chevron holds focus.
        Assert.Same(
            RailChevron(window),
            window.FocusManager!.GetFocusedElement());

        // 'F' again exits — the same single-key gesture toggles back.
        window.KeyPressQwerty(PhysicalKey.F, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();

        Assert.False(shell.IsFocusMode, "a second 'F' must exit focus mode");
        Assert.False(workspace.IsRailCollapsed, "exiting focus mode must re-expand the rail");

        // #230: the symmetric exit restore — focus returns to the (again visible) Focus toggle.
        var focusButton = FocusModeButton(window);
        Assert.NotNull(focusButton);
        Assert.Same(focusButton, window.FocusManager!.GetFocusedElement());

        window.Close();
    }

    /// <summary>
    /// #230 / ADR-022 third addendum ("keyboard-focus continuity"), THE regression pin: the Focus
    /// toggle lives inside the very strip focus mode hides, so activating it removes the focused
    /// control from the tree and keyboard focus is silently lost (WCAG 2.4.3-adjacent) — today
    /// nothing holds focus after the chrome melts. The addendum parks keyboard focus on the one
    /// designated surviving affordance, the seam chevron (<c>RailCollapseToggle</c>,
    /// WorkspaceView.axaml — the persistent exit affordance ADR-022's Consequences named), via
    /// <c>Focus(NavigationMethod.Tab)</c> so the accent focus ring renders as the "way back out".
    /// The fixture drives the defect's EXACT vector: the button HOLDS keyboard focus (a real
    /// Tab-style focus, the keyboard user's state) and Enter clicks it through the real input
    /// pipeline (an Avalonia Button raises Click on Enter while focused) — never the command seam,
    /// which would sidestep the "focused control melts away" half of the bug.
    /// </summary>
    [AvaloniaFact]
    public async Task WorkspaceFocus_EnterViaButton_ParksKeyboardFocusOnRailChevron()
    {
        var (window, shell) = ShowShell(Present, 1280, 720);
        await DriveToWorkspaceAsync(shell);
        Dispatcher.UIThread.RunJobs();

        // The keyboard user's state: the visible Focus button holds genuine keyboard focus.
        var focusButton = FocusModeButton(window);
        Assert.NotNull(focusButton);
        Assert.True(
            focusButton!.Focus(NavigationMethod.Tab),
            "the visible Focus button must accept keyboard focus on the workspace step");
        Assert.Same(focusButton, window.FocusManager!.GetFocusedElement());

        // Enter clicks the focused button through the real pipeline — the strip (and with it the
        // focused control) melts away (D2).
        window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();

        Assert.True(shell.IsFocusMode, "Enter on the focused Focus button must enter focus mode");

        // THE #230 pin: keyboard focus is PARKED on the surviving seam chevron — today it is
        // silently lost with the hidden strip (focused element null/window, never the chevron).
        var chevron = RailChevron(window);
        Assert.Same(chevron, window.FocusManager!.GetFocusedElement());
        Assert.True(
            chevron.IsEffectivelyVisible,
            "the seam chevron must survive the chrome melt — the parked focus must be VISIBLE");

        window.Close();
    }

    /// <summary>
    /// #230 / ADR-022 third addendum ("keyboard-focus continuity"), the symmetric round-trip: on
    /// leaving focus mode, keyboard focus is RESTORED to the Focus toggle (again visible with the
    /// strip), guarded by <c>IsEffectivelyVisible</c>. Entry here is the COMMAND vector — the
    /// addendum wires the parking to the <see cref="ShellViewModel.IsFocusMode"/> property change,
    /// one choke point for every vector (button, <c>F</c>, command) — and the exit is a real
    /// dispatched Escape delivered WHILE the chevron holds focus, which ALSO pins that a focused
    /// chevron Button lets Esc bubble to <see cref="MainWindow.OnKeyDown"/> (the exit gesture must
    /// keep working with focus parked on it).
    /// </summary>
    [AvaloniaFact]
    public async Task WorkspaceFocus_EscExit_RestoresKeyboardFocusToFocusButton()
    {
        var (window, shell) = ShowShell(Present, 1280, 720);
        await DriveToWorkspaceAsync(shell);
        Dispatcher.UIThread.RunJobs();

        // Enter via the command vector: the property-change choke point must park focus on the
        // chevron even when no control was focused beforehand.
        shell.ToggleFocusModeCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();

        Assert.True(shell.IsFocusMode);
        Assert.Same(RailChevron(window), window.FocusManager!.GetFocusedElement());

        // Esc through the real pipeline, dispatched while the chevron HOLDS focus — it must bubble
        // to MainWindow.OnKeyDown and exit (a focused Button does not swallow Escape).
        window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();

        Assert.False(shell.IsFocusMode, "Esc must exit focus mode while the chevron holds focus");

        // The restore half of the round-trip: the Focus toggle is visible again AND holds focus.
        var focusButton = FocusModeButton(window);
        Assert.NotNull(focusButton);
        Assert.True(
            focusButton!.IsEffectivelyVisible,
            "the Focus button must be visible again once the strip returns (exit restores chrome)");
        Assert.Same(focusButton, window.FocusManager!.GetFocusedElement());

        window.Close();
    }

    /// <summary>
    /// #230 / ADR-022 third addendum ("keyboard-focus continuity"), the guard rail: OFF the
    /// workspace step (Connect — the Focus button binds <c>IsWorkspaceStep</c>=false) toggling
    /// focus mode programmatically must be a focus NO-OP. The addendum's exit-restore is guarded
    /// by <c>IsEffectivelyVisible</c>, so a non-workspace exit skips silently: no throw, and the
    /// HIDDEN Focus button never receives focus (focusing an invisible control would strand the
    /// keyboard user on a control that cannot render its focus ring).
    /// </summary>
    [AvaloniaFact]
    public void FocusModeToggle_OffWorkspace_FocusWiring_NoOps()
    {
        var (window, shell) = ShowShell(Present, 1280, 720);
        Assert.IsType<ConnectionViewModel>(shell.CurrentStep);
        Dispatcher.UIThread.RunJobs();

        // A full programmatic on/off round-trip on the Connect step — the wiring's off-workspace
        // no-op path. Must not throw (no chevron realizes here; the restore guard skips).
        shell.ToggleFocusModeCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        shell.ToggleFocusModeCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();

        Assert.False(shell.IsFocusMode);

        // The IsEffectivelyVisible guard: the hidden Focus button did NOT receive restore-focus.
        // Off the workspace the helper may return null (the button may not even realize), so the
        // assertion targets the focused element's identity, not the helper's nullability.
        var focused = window.FocusManager!.GetFocusedElement();
        Assert.False(
            focused is Avalonia.Controls.Button { Name: "FocusModeButton" },
            "the hidden Focus button must never receive restore-focus off the workspace step");

        window.Close();
    }

    /// <summary>The named "⛶ Focus" command-strip button (ADR-022 addendum) — the focus-mode entry
    /// point, located by <see cref="Avalonia.StyledElement.Name"/> ("FocusModeButton"). Returns null
    /// when not realized (e.g. on a step that never materializes the strip's StackPanel), so callers
    /// can assert "absent OR not-visible" off the workspace.</summary>
    private static Avalonia.Controls.Button? FocusModeButton(MainWindow window) =>
        window.GetVisualDescendants()
            .OfType<Avalonia.Controls.Button>()
            .FirstOrDefault(b => b.Name == "FocusModeButton");

    /// <summary>The seam chevron (<c>RailCollapseToggle</c>, WorkspaceView.axaml) — the one
    /// always-surviving affordance ADR-022's third addendum parks keyboard focus on when focus
    /// mode melts the chrome (#230). Unlike <see cref="FocusModeButton"/> it ASSERTS presence
    /// (exactly one): every caller drives the workspace step, where the chevron must exist —
    /// absence is a fixture defect, not an assertable state.</summary>
    private static Avalonia.Controls.Button RailChevron(MainWindow window) =>
        Assert.Single(
            window.GetVisualDescendants().OfType<Avalonia.Controls.Button>(),
            b => b.Name == "RailCollapseToggle");

    /// <summary>
    /// ADR-022 (the large-monitor proof): a loaded workspace at a 2560×1080 ULTRAWIDE frame — the
    /// single size that evidences the void is gone. With the adaptive rail (340px + scope-summary
    /// content) the rail fills sensibly while the graph takes the rest, instead of the old hard-pinned
    /// 300px rail floating a tiny placeholder in a vast empty column. The fixture pins soundness (the
    /// scope-summary card realized, the kind tally populated); the ui-verifier judges that the rail
    /// content fills sensibly with no giant empty rail. One size only (the wide-monitor case).
    /// </summary>
    [AvaloniaTheory]
    [InlineData(2560, 1080)]
    public async Task WorkspaceUltrawide(int width, int height)
    {
        var (window, shell) = ShowShell(Present, width, height);
        var workspace = await DriveToWorkspaceAsync(shell);
        Dispatcher.UIThread.RunJobs();

        // The reclaimed rail shows the scope summary (nothing selected) — the void-fill that proves
        // the large-monitor empty-rail problem is solved. Force a render pass so the
        // DetailPanelRegion ContentControl realizes its inline card content (see WorkspaceScopeSummary).
        Assert.Null(workspace.DetailPanel);
        window.CaptureRenderedFrame()?.Dispose();
        Dispatcher.UIThread.RunJobs();

        var card = Assert.Single(window.GetVisualDescendants()
            .OfType<Avalonia.Controls.Border>(), b => b.Name == "ScopeSummaryCard");
        Assert.True(card.IsEffectivelyVisible, "the ultrawide rail must show the scope summary, not a void");
        // The active-ruleset line is populated — the empty rail reads as information, not a void.
        Assert.False(string.IsNullOrWhiteSpace(workspace.RulesetName));

        CapturePng(window, "workspace-ultrawide", width, height);
        window.Close();
    }

    /// <summary>The top command-strip <see cref="Avalonia.Controls.Border"/> (the ⚙ Settings bar,
    /// ADR-011 §1): located as the NEAREST Border ancestor of the "⚙ Settings" button, so the focus
    /// fixture can assert it hides when <see cref="ShellViewModel.IsFocusMode"/> flips (ADR-022 D2).
    /// The strip Border is the button's immediate Border container (XAML:
    /// <c>&lt;Border&gt;&lt;Button/&gt;&lt;/Border&gt;</c>) and binds <c>IsVisible="!IsFocusMode"</c>
    /// — the one control whose effective-visibility focus mode drives. The nearest ancestor (not the
    /// outermost, which would be always-visible window chrome) is the strip itself.</summary>
    private static Avalonia.Controls.Border CommandStripBorder(MainWindow window)
    {
        var settingsButton = Assert.Single(window.GetVisualDescendants()
            .OfType<Avalonia.Controls.Button>(),
            b => (b.Content as Avalonia.Controls.TextBlock)?.Text == "⚙ Settings");
        return settingsButton.GetVisualAncestors()
            .OfType<Avalonia.Controls.Border>().First();
    }

    /// <summary>
    /// AP 3.4 (ADR-010 §5): the violations sidebar topping the right column, above the
    /// AP 2.5 detail stack (the <c>2*,Auto,3*</c> vertical split, beside GraphHost, never
    /// over it — ADR-001 airspace). Driven to the settled workspace via the same
    /// <see cref="DriveToWorkspaceAsync"/> the other workspace frames use, default ruleset
    /// (the AP 3.2 demo baseline = 19 findings = E 4 · W 3 · i 12).
    ///
    /// Beyond the airspace pin (sidebar renders right of GraphHost), this fixture pins the
    /// header severity-summary chip strip — the ui-verifier B-2 evidence gap. The canonical
    /// errors-first row order pushes Warning/Info below the scroll fold, so the chip strip
    /// is the ONLY above-the-fold evidence that all three severities exist; without a pin it
    /// could silently regress (e.g. a chip collapses, or a glyph drifts off the ADR-010
    /// palette) and every static frame would still look plausible. Since #197 the strip is an
    /// <c>ItemsControl</c> over the VM's <see cref="WorkspaceViewModel.SeverityChips"/> — each
    /// chip an interactive toggle <c>Button</c> (a severity FILTER) — so
    /// <see cref="AssertSeverityChipStrip"/> addresses the three glyph squares in the visual
    /// tree (the 16×16 chip borders are unique to the strip — findings rows use 20×20) and
    /// asserts, per severity: the count text (E 4 / W 3 / i 12, the demo baseline AND parity
    /// with the matching <see cref="WorkspaceViewModel.SeverityChips"/> chip
    /// <see cref="AuditFilterChip.Count"/>) and that the glyph brush is the pinned ADR-010
    /// palette color (#D13438 / #F7A30B / #4FA3E3, parity with <c>SeverityConverters.ToBrush</c>).
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

        // The violations sidebar VIEW actually rendered, and it lives in the right detail
        // column (right of GraphHost — the airspace pin, as in DetailPanelViewTests).
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

        // The header chip strip evidences ALL THREE severities above the fold (ui-verifier
        // B-2): the demo baseline is E 4 · W 3 · i 12, each glyph in the ADR-010 palette.
        AssertSeverityChipStrip(workspace, sidebar);

        CapturePng(window, "workspace-violations", width, height);
        window.Close();
    }

    /// <summary>
    /// Pins the AP 3.4 header severity-summary chip strip (ADR-010 §5; ui-verifier B-2), now the
    /// #197 interactive <c>ItemsControl</c> over <see cref="WorkspaceViewModel.SeverityChips"/>:
    /// all THREE severity chips are present, visible, and individually correct — count text AND
    /// glyph-square palette color. The chips are not named controls, so they are located
    /// structurally: the three 16×16 glyph <see cref="Avalonia.Controls.Border"/>s are unique to
    /// the strip (the findings ListBox rows use 20×20), and each chip's letter (E / W / i) ties it
    /// to its severity. Expectations are DERIVED, never hardcoded: the glyph letter and brush come
    /// from the one <see cref="SeverityConverters"/> palette the XAML binds, and the count from the
    /// matching <see cref="WorkspaceViewModel.SeverityChips"/> chip's <see cref="AuditFilterChip.Count"/>
    /// (the strip now binds the chip's own Count, not the old <c>CountForSeverity</c> MultiBinding)
    /// — so this fails the instant the strip drifts off the palette or miscounts. The demo-baseline
    /// totals (E 4 · W 3 · i 12 = 19) are additionally pinned as a literal, so a dataset or engine
    /// drift that silently changes the per-severity mix is also caught here.
    /// </summary>
    private static void AssertSeverityChipStrip(WorkspaceViewModel workspace, ViolationsSidebarView sidebar)
    {
        Assert.True(workspace.HasViolations, "the demo baseline must populate the chip strip");

        // The 16×16 glyph squares are unique to the #197 chip strip (findings rows use 20×20) —
        // exactly one per severity, all rendered above the fold.
        var chipBorders = sidebar.GetVisualDescendants()
            .OfType<Avalonia.Controls.Border>()
            .Where(b => b.IsEffectivelyVisible && b.Width == 16 && b.Height == 16)
            .ToList();
        Assert.Equal(3, chipBorders.Count);

        // The demo baseline: Error 4 (3 nesting + 1 circular), Warning 3 (naming), Info 12
        // (empty-group) — literal pins so a per-severity mix drift is caught, paired with the
        // matching SeverityChips chip Count so the chip text can never diverge from the data.
        var expectedCounts = new Dictionary<RuleSeverity, int>
        {
            [RuleSeverity.Error] = 4,
            [RuleSeverity.Warning] = 3,
            [RuleSeverity.Info] = 12,
        };

        foreach (var severity in new[] { RuleSeverity.Error, RuleSeverity.Warning, RuleSeverity.Info })
        {
            // Locate THIS severity's chip by its redundant letter (E / W / i) — derived from
            // the one palette, the colorblind-safe channel the chip renders in the square.
            var glyph = Assert.IsType<string>(SeverityConverters.ToGlyph.Convert(
                severity, typeof(string), null, CultureInfo.InvariantCulture));
            var letterBlocks = chipBorders
                .Select(b => b.Child as Avalonia.Controls.TextBlock)
                .ToList();
            var letterBlock = Assert.Single(letterBlocks, t => t is not null && t.Text == glyph)!;
            var border = (Avalonia.Controls.Border)letterBlock.Parent!;

            // (1) the glyph square is the pinned ADR-010 palette color (parity with ToBrush).
            var expectedBrush = Assert.IsAssignableFrom<ISolidColorBrush>(
                SeverityConverters.ToBrush.Convert(
                    severity, typeof(IBrush), null, CultureInfo.InvariantCulture));
            var actualBrush = Assert.IsAssignableFrom<ISolidColorBrush>(border.Background);
            Assert.Equal(expectedBrush.Color, actualBrush.Color);

            // (2) the chip's count TextBlock — the "chip-count" class disambiguates the count
            // digits from the sibling Label TextBlock (both live in the chip Button's StackPanel;
            // the glyph letter lives INSIDE the square). It reads the per-severity tally: the live
            // SeverityChips chip Count AND the pinned demo-baseline literal must agree, and the
            // rendered text must equal it.
            var chipButton = border.GetVisualAncestors()
                .OfType<Avalonia.Controls.Button>()
                .First(b => b.Classes.Contains("chip"));
            var countBlock = Assert.Single(chipButton.GetVisualDescendants()
                    .OfType<Avalonia.Controls.TextBlock>()
, t => t.IsEffectivelyVisible && t.Classes.Contains("chip-count"));

            var liveCount = workspace.SeverityChips.Single(c => c.Severity == severity).Count;
            Assert.Equal(expectedCounts[severity], liveCount);
            Assert.Equal(liveCount.ToString(CultureInfo.InvariantCulture), countBlock.Text);
        }
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

    // --- Settings window: Naming tab (AP 3.3 / S5) ----------------------------------------------

    /// <summary>The Naming-tab live-preview chip's success glyph — the ✓ a card shows when
    /// its <c>PreviewSample</c> matches its pattern (parity with
    /// <c>NamingPreviewConverter</c>'s Ok glyph; #45 / ADR-011 §4).</summary>
    private const string OkChipGlyph = "✓";

    /// <summary>
    /// AP 3.3 / S5 (ADR-011, spec "ui-checklist additions"): the modal
    /// <see cref="SettingsWindow"/> rendered STANDALONE — exactly the headless seam the spec
    /// pins (<c>ShowDialog</c> is headless-hostile; open-risk #3), so the fixture constructs
    /// <c>new SettingsWindow { DataContext = vm }</c>, calls <c>.Show()</c> (NOT
    /// <c>ShowDialog</c>), pumps the dispatcher, capture-and-discards the lagging compositor
    /// batch, then captures <c>settings-naming-{W}x{H}.png</c> at both checklist sizes for the
    /// ui-verifier to judge against the new section-B "Settings / rule editor" rows.
    ///
    /// <para>The VM is seeded from <see cref="RulesetLoader.LoadDefault"/> through the mirror
    /// (<see cref="SettingsViewModel.LoadFrom"/>) — the demo-mode-safe default, never a lab
    /// file. The Naming tab is selected so the cards (the three default <c>naming-*</c> rules)
    /// are the captured surface. Two of those cards are given a live <c>PreviewSample</c> so the
    /// frame evidences BOTH preview verdicts at once: the GG card matches its pattern
    /// (<c>GG_Vertrieb_Lesen</c> ⇒ a green ✓ "matches"), the DL card does NOT match its
    /// (<c>^DL_..._(RW|RO)$</c>) pattern (the same sample ⇒ a ✗ "would be flagged" chip in the
    /// rule's own severity color). Without two seeded samples the frame could show only an idle
    /// chip and silently lose the ✓/✗ evidence the checklist row demands.</para>
    ///
    /// <para>Beyond the PNG, <see cref="AssertNamingPreviewChip"/> pins fixture soundness: the
    /// two verdict chips are actually present and individually correct (glyph + palette color),
    /// DERIVED from the one <see cref="NamingPreviewConverter"/> seam the XAML binds (never a
    /// hardcoded hex) — so a static frame can never look plausible while the chip drifts off
    /// the verdict palette or collapses to idle. RED until
    /// <c>src/App/Views/SettingsWindow.axaml(.cs)</c>, the Naming tab, and
    /// <c>NamingRuleEditor.PreviewSample</c> exist.</para>
    /// </summary>
    [AvaloniaTheory]
    [InlineData(1280, 720)]
    [InlineData(1920, 1080)]
    public void Settings_Naming(int width, int height)
    {
        var vm = SettingsViewModel.LoadFrom(RulesetLoader.LoadDefault());

        // The default ships three naming cards (naming-gg / naming-dl / naming-ug); the fixture
        // needs at least one match and one non-match in frame.
        Assert.True(vm.Naming.Count >= 2, "the default ruleset must seed >= 2 naming cards");
        var ggCard = vm.Naming.Single(r => r.Kind == AdObjectKind.GlobalGroup);
        var nonGgCard = vm.Naming.First(r => r.Kind != AdObjectKind.GlobalGroup);

        // One ✓ verdict and one ✗ verdict, live: the GG card's sample matches the GG pattern;
        // the same sample fed to a non-GG card (e.g. the DL_..._(RW|RO) pattern) does not.
        ggCard.PreviewSample = "GG_Vertrieb_Lesen";
        nonGgCard.PreviewSample = "GG_Vertrieb_Lesen";

        // Soundness BEFORE the window: the chosen samples really do straddle the verdict —
        // a match on the GG card and a non-match on the other — through the production
        // preview engine, so the fixture cannot silently capture two identical chips.
        Assert.Equal(
            NamingPreviewKind.Ok,
            NamingPreview.Evaluate(ggCard.Pattern, ggCard.PreviewSample).Kind);
        Assert.Equal(
            NamingPreviewKind.Violation,
            NamingPreview.Evaluate(nonGgCard.Pattern, nonGgCard.PreviewSample).Kind);

        var window = new SettingsWindow { DataContext = vm, Width = width, Height = height };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        // Bring the Naming tab to the front so its cards (the captured surface) are realized.
        SelectTab(window, "Naming");
        Dispatcher.UIThread.RunJobs();

        // The two verdict chips render correctly in frame — the soundness pin the PNG can't make.
        AssertNamingPreviewChip(window, ggCard, nonGgCard);

        CaptureWindowPng(window, "settings-naming", width, height);
        window.Close();
    }

    /// <summary>
    /// Fixture-soundness pin for the Naming-tab live preview (AP 3.3 / S5): the seeded ✓ and ✗
    /// cards each render a chip whose glyph AND palette color match what the ONE
    /// <see cref="NamingPreviewConverter"/> binding seam produces for that card's
    /// <c>(Pattern, PreviewSample, Severity)</c> — derived, never hardcoded, so this fails the
    /// instant the chip drifts off the verdict palette (the Ok green or the rule's own severity
    /// color) or collapses to idle. Located structurally by glyph + brush (the chips are not
    /// named controls), exactly the <see cref="AssertSeverityChipStrip"/> shape.
    /// </summary>
    private static void AssertNamingPreviewChip(
        SettingsWindow window, NamingRuleEditor okCard, NamingRuleEditor violationCard)
    {
        // The ✓ card: the converter must yield an Ok visual (green ✓) for the matching sample.
        var okVisual = PreviewVisual(okCard);
        Assert.Equal(NamingPreviewKind.Ok, okVisual.Kind);
        Assert.Equal(OkChipGlyph, okVisual.Glyph);
        AssertChipRendered(window, okVisual);

        // The ✗ card: a Violation visual in the rule's OWN severity glyph + palette color.
        var violationVisual = PreviewVisual(violationCard);
        Assert.Equal(NamingPreviewKind.Violation, violationVisual.Kind);
        var expectedGlyph = Assert.IsType<string>(SeverityConverters.ToGlyph.Convert(
            violationCard.Severity, typeof(string), null, CultureInfo.InvariantCulture));
        Assert.Equal(expectedGlyph, violationVisual.Glyph);
        AssertChipRendered(window, violationVisual);

        // The two verdicts must be visually DISTINCT — a ✓ and a ✗ chip, not two of the same.
        var okColor = Assert.IsAssignableFrom<ISolidColorBrush>(okVisual.Brush).Color;
        var violationColor = Assert.IsAssignableFrom<ISolidColorBrush>(violationVisual.Brush).Color;
        Assert.True(
            okVisual.Glyph != violationVisual.Glyph || okColor != violationColor,
            "the ✓ and ✗ preview chips must be visually distinct (glyph or color)");
    }

    /// <summary>The chip descriptor the XAML binds for <paramref name="card"/>: the live
    /// <c>NamingPreviewConverter</c> over <c>[Pattern, PreviewSample]</c> with the card's
    /// severity as the parameter — the exact seam the Naming-tab chip uses.</summary>
    private static NamingPreviewVisual PreviewVisual(NamingRuleEditor card) =>
        Assert.IsType<NamingPreviewVisual>(NamingPreviewConverter.Instance.Convert(
            new object?[] { card.Pattern, card.PreviewSample },
            typeof(NamingPreviewVisual),
            card.Severity,
            CultureInfo.InvariantCulture));

    /// <summary>Asserts the realized visual tree contains a rendered chip carrying
    /// <paramref name="expected"/>'s glyph in its pinned brush color — a glyph
    /// <see cref="Avalonia.Controls.TextBlock"/> whose <c>Text</c> equals the expected glyph and
    /// whose own <c>Foreground</c> OR whose chip <c>Border</c> ancestor's <c>Background</c> is
    /// the expected palette color. Brush parity is derived from the converter output (never a
    /// hardcoded hex), so the chip can never silently drift off the verdict palette.</summary>
    private static void AssertChipRendered(SettingsWindow window, NamingPreviewVisual expected)
    {
        var expectedColor = Assert.IsAssignableFrom<ISolidColorBrush>(expected.Brush).Color;

        var glyphBlocks = window.GetVisualDescendants()
            .OfType<Avalonia.Controls.TextBlock>()
            .Where(t => t.IsEffectivelyVisible && t.Text == expected.Glyph)
            .ToList();
        Assert.NotEmpty(glyphBlocks);

        var painted = glyphBlocks.Any(t =>
            (t.Foreground is ISolidColorBrush fg && fg.Color == expectedColor)
            || t.GetVisualAncestors()
                .OfType<Avalonia.Controls.Border>()
                .Any(b => b.Background is ISolidColorBrush bg && bg.Color == expectedColor));

        Assert.True(
            painted,
            $"a '{expected.Glyph}' preview chip must render in the verdict color "
            + $"#{expectedColor.R:X2}{expectedColor.G:X2}{expectedColor.B:X2} "
            + "(parity with NamingPreviewConverter)");
    }

    // --- Settings window: Rules master grid (AP 3.3 / S6) -----------------------------------------

    /// <summary>
    /// AP 3.3 / S6 (ADR-011; spec "Final slice plan" S6 + "ui-checklist additions"): the
    /// modal <see cref="SettingsWindow"/>'s <b>Rules master grid</b> rendered standalone —
    /// the same headless seam <see cref="Settings_Naming"/> pins (<c>.Show()</c>, NOT
    /// <c>ShowDialog</c>; open-risk #3), capturing <c>settings-rules-{W}x{H}.png</c> at both
    /// checklist sizes for the ui-verifier to judge against the new section-B Rules-grid row.
    ///
    /// <para>The VM is seeded from <see cref="RulesetLoader.LoadDefault"/> through the mirror
    /// (<see cref="SettingsViewModel.LoadFrom"/>) — the demo-mode-safe default, never a lab
    /// file. The default's master grid spans every rule kind the <c>EnumerateRules</c> shape
    /// emits: the nesting matrix (error), the three <c>naming-*</c> rules (warning), circular
    /// (error) and empty-group (info) — so ALL THREE severities are evidenced in one frame
    /// AND the two SimpleRule cards (circular + empty-group) the spec wants in the Rules-tab
    /// capture are present.</para>
    ///
    /// <para>Beyond the PNG, <see cref="AssertRulesGridSeverityParity"/> pins fixture
    /// soundness — the <b>severity-parity assertion on the Rules grid</b> the checklist's
    /// <c>[T:SettingsScreenshotTests — severity parity]</c> tag demands: every
    /// <see cref="RuleRowEditor"/> renders, above the fold, its E/W/i glyph in the pinned
    /// <see cref="SeverityConverters"/> palette (#D13438 / #F7A30B / #4FA3E3), DERIVED from
    /// the live row severity (never a hardcoded hex), so a static frame can never look
    /// plausible while a row's severity selector drifts off the palette or the grid silently
    /// loses a row. <b>RED</b> until <c>src/App/Views/SettingsWindow.axaml(.cs)</c> and the
    /// Rules tab (the <c>EnumerateRules</c>-shaped grid binding <see cref="SettingsViewModel.Rules"/>)
    /// exist — today no grid row realizes, so the parity assertion finds no glyphs.</para>
    /// </summary>
    [AvaloniaTheory]
    [InlineData(1280, 720)]
    [InlineData(1920, 1080)]
    public void Settings_Rules(int width, int height)
    {
        var vm = SettingsViewModel.LoadFrom(RulesetLoader.LoadDefault());

        // Soundness BEFORE the window: the default seeds the full EnumerateRules shape — the
        // nesting row, every naming rule, circular and empty-group — covering all three
        // severities (nesting=Error, naming=Warning, empty-group=Info) so the parity check
        // below has a row in each palette color to verify.
        Assert.Equal(
            vm.Naming.Count + 3, // nesting + each naming rule + circular + empty-group
            vm.Rules.Count);
        var severities = vm.Rules.Select(r => r.Severity).ToHashSet();
        Assert.Contains(RuleSeverity.Error, severities);
        Assert.Contains(RuleSeverity.Warning, severities);
        Assert.Contains(RuleSeverity.Info, severities);

        var window = new SettingsWindow { DataContext = vm, Width = width, Height = height };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        // Bring the Rules tab to the front so its master grid (the captured surface) is realized.
        SelectTab(window, "Rules");
        Dispatcher.UIThread.RunJobs();

        // The grid renders every rule's severity glyph in the pinned palette — the soundness
        // pin the PNG can't make (and the checklist's "severity parity" test tag).
        AssertRulesGridSeverityParity(window, vm);

        CaptureWindowPng(window, "settings-rules", width, height);
        window.Close();
    }

    /// <summary>
    /// Fixture-soundness pin for the Rules master grid (AP 3.3 / S6; checklist
    /// <c>[T:SettingsScreenshotTests — severity parity]</c>): every <see cref="RuleRowEditor"/>
    /// in <see cref="SettingsViewModel.Rules"/> renders its severity in the ONE pinned
    /// <see cref="SeverityConverters"/> palette — the E/W/i glyph AND its #D13438/#F7A30B/#4FA3E3
    /// square color, DERIVED from the live <see cref="RuleRowEditor.Severity"/> (never a hardcoded
    /// hex), exactly the <see cref="AssertSeverityChipStrip"/> shape. For each distinct severity
    /// present in the grid the realized Rules-tab content must contain at least one rendered glyph
    /// <see cref="Avalonia.Controls.TextBlock"/> whose <c>Text</c> equals that severity's glyph and
    /// whose own <c>Foreground</c> OR a <see cref="Avalonia.Controls.Border"/> ancestor's
    /// <c>Background</c> is the pinned palette color — so the grid can never silently drift off the
    /// palette, miscolor a row, or lose a severity entirely.
    /// </summary>
    private static void AssertRulesGridSeverityParity(SettingsWindow window, SettingsViewModel vm)
    {
        Assert.NotEmpty(vm.Rules);

        // The glyph TextBlocks rendered anywhere in the realized window. The Rules grid binds
        // its severity selector through the same SeverityConverters the sidebar chip strip does,
        // so each row contributes a glyph painted in that severity's palette color.
        var glyphBlocks = window.GetVisualDescendants()
            .OfType<Avalonia.Controls.TextBlock>()
            .Where(t => t.IsEffectivelyVisible && !string.IsNullOrEmpty(t.Text))
            .ToList();

        foreach (var severity in vm.Rules.Select(r => r.Severity).Distinct())
        {
            // Expectations DERIVED from the one palette the XAML binds — never hardcoded.
            var glyph = Assert.IsType<string>(SeverityConverters.ToGlyph.Convert(
                severity, typeof(string), null, CultureInfo.InvariantCulture));
            var expectedColor = Assert.IsAssignableFrom<ISolidColorBrush>(
                SeverityConverters.ToBrush.Convert(
                    severity, typeof(IBrush), null, CultureInfo.InvariantCulture)).Color;

            var painted = glyphBlocks.Any(t =>
                t.Text == glyph
                && ((t.Foreground is ISolidColorBrush fg && fg.Color == expectedColor)
                    || t.GetVisualAncestors()
                        .OfType<Avalonia.Controls.Border>()
                        .Any(b => b.Background is ISolidColorBrush bg && bg.Color == expectedColor)));

            Assert.True(
                painted,
                $"the Rules grid must render a '{glyph}' severity glyph in the pinned palette color "
                + $"#{expectedColor.R:X2}{expectedColor.G:X2}{expectedColor.B:X2} "
                + $"for severity {severity} (parity with SeverityConverters)");
        }
    }

    // --- Settings window: Matrix tab (AP 3.3 / S6) ------------------------------------------------

    /// <summary>
    /// AP 3.3 / S6 (ADR-011; spec "Matrix editor" + "ui-checklist additions"): the modal
    /// <see cref="SettingsWindow"/>'s <b>nesting-matrix tab</b> rendered standalone — same
    /// headless seam (<c>.Show()</c>, NOT <c>ShowDialog</c>), capturing
    /// <c>settings-matrix-{W}x{H}.png</c> at both checklist sizes for the ui-verifier to judge
    /// the 3×6 grid (3 parent rows GG/DL/UG × 6 member cols User/Computer/GG/DL/UG/External, no
    /// OU), the kind-badge headers, the per-cell allow/deny/error/warning/info chips, and the
    /// Unlisted-fallback + rule-wide-default-severity controls (the AGUDLP lane readable).
    ///
    /// <para>The VM is seeded from <see cref="RulesetLoader.LoadDefault"/> through the mirror —
    /// the default ships the full strict-AGDLP matrix (every cell present), so the captured grid
    /// is the canonical 18-cell shape, including the AGUDLP lane (DL←UG allow, UG←GG allow) the
    /// checklist wants legible.</para>
    ///
    /// <para><see cref="AssertMatrixGridCells"/> pins fixture soundness the PNG can't: the
    /// realized window contains a rendered, addressable cell surface for all 18 parent×member
    /// pairings the <see cref="NestingEditor"/> grid spans — so a static frame can never look
    /// plausible while the matrix grid is missing or short rows/columns. <b>RED</b> until
    /// <c>src/App/Views/SettingsWindow.axaml(.cs)</c> and the Matrix tab (the 3×6 grid binding
    /// <see cref="NestingEditor.Cells"/>) exist — today no matrix cell control realizes.</para>
    /// </summary>
    [AvaloniaTheory]
    [InlineData(1280, 720)]
    [InlineData(1920, 1080)]
    public void Settings_Matrix(int width, int height)
    {
        var vm = SettingsViewModel.LoadFrom(RulesetLoader.LoadDefault());

        // Soundness BEFORE the window: the default seeds the dense 3×6 grid (18 cells, every one
        // Present) so the captured matrix is the full strict-AGDLP shape — never a sparse stub.
        Assert.Equal(
            NestingEditor.ParentKinds.Count * NestingEditor.MemberKinds.Count,
            vm.Nesting.Cells.Count);
        Assert.All(vm.Nesting.Cells, c => Assert.True(
            c.Present, $"default cell {c.Parent}<-{c.Member} must load Present for the full matrix capture"));

        // The two SimpleRule rules the spec wants surfaced alongside the matrix capture exist and
        // are enabled in the default (circular=error, empty-group=info) — the Rules-tab cards.
        Assert.True(vm.Circular.Enabled);
        Assert.True(vm.EmptyGroup.Enabled);

        var window = new SettingsWindow { DataContext = vm, Width = width, Height = height };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        // Bring the Matrix tab to the front so its 3×6 grid (the captured surface) is realized.
        SelectTab(window, "Matrix");
        Dispatcher.UIThread.RunJobs();

        // The 18-cell grid actually realized a control per cell — the soundness pin the PNG can't make.
        AssertMatrixGridCells(window, vm);

        CaptureWindowPng(window, "settings-matrix", width, height);
        window.Close();
    }

    /// <summary>
    /// Fixture-soundness pin for the nesting-matrix tab (AP 3.3 / S6): the realized window
    /// surfaces a rendered, bound editor control for EVERY one of the 18
    /// parent×member cells the <see cref="NestingEditor"/> grid spans — located via the
    /// <see cref="NestingCellEditor"/> instances themselves, which the spec binds as each cell's
    /// <c>DataContext</c> (a compact per-cell <see cref="Avalonia.Controls.ComboBox"/> over
    /// <see cref="NestingCellEditor.Choice"/>). Every cell editor must back at least one realized,
    /// effectively-visible <see cref="Avalonia.Controls.Control"/> in the visual tree — so the grid
    /// can never silently drop a row or column and still look plausible in a static frame.
    /// </summary>
    private static void AssertMatrixGridCells(SettingsWindow window, SettingsViewModel vm)
    {
        Assert.Equal(18, vm.Nesting.Cells.Count); // 3 parents × 6 members, the canonical shape

        var boundDataContexts = window.GetVisualDescendants()
            .OfType<Avalonia.Controls.Control>()
            .Where(c => c.IsEffectivelyVisible)
            .Select(c => c.DataContext)
            .ToHashSet();

        foreach (var cell in vm.Nesting.Cells)
        {
            Assert.True(
                boundDataContexts.Contains(cell),
                $"the matrix grid must realize a bound cell control for {cell.Parent}<-{cell.Member}");
        }
    }

    // --- Settings window: Ignore & Exceptions tab (AP 3.3 / S7) -----------------------------------

    /// <summary>The control char the plain-text note pins (#45 / ADR-011 §4): a BEL (<c></c>)
    /// followed by an ANSI SGR red-foreground escape (<c>[31m</c>). An untrusted ruleset file
    /// can carry exactly this in a <c>note</c>; the editor must render it as INERT plain text — the
    /// BEL never rings, the escape never colors the terminal/markup — verbatim, ordinal, byte-for-byte
    /// (the dedicated <c>MatchEntryNotePlainTextTests</c> pins the rendered <c>Text</c>; here the
    /// fixture only needs the string to round into the captured surface).</summary>
    private const string ControlCharNote = "[31mwould-color-a-terminal";

    /// <summary>
    /// AP 3.3 / S7 (ADR-011; spec "Ignore + exceptions" + "ui-checklist additions"): the modal
    /// <see cref="SettingsWindow"/>'s <b>Ignore &amp; Exceptions</b> tab rendered standalone — the
    /// same headless seam the other settings fixtures pin (<c>.Show()</c>, NOT <c>ShowDialog</c>;
    /// open-risk #3), capturing <c>settings-ignore-{W}x{H}.png</c> at both checklist sizes for the
    /// ui-verifier to judge against the new section-B "Ignore + exceptions" row.
    ///
    /// <para>The VM is seeded from <see cref="RulesetLoader.LoadDefault"/> through the mirror
    /// (<see cref="SettingsViewModel.LoadFrom"/>) — the demo-mode-safe default, never a lab file.
    /// The default ships the full built-in ignore set (the strict-AGDLP suppression entries), so the
    /// captured list is the canonical global-ignore shape: dn/name mode toggle, the glob value, and
    /// the plain-text note per row. To evidence the per-rule EXCEPTION surface in the SAME frame —
    /// specifically the endpoint control that is legal ONLY on a nesting exception (Any/Parent/Member)
    /// — one nesting exception is seeded (<c>vm.Nesting.Exceptions</c>, the one endpoint-EDITABLE
    /// list) with <c>Endpoint=Member</c>; and to pin #45 in the captured surface its
    /// <see cref="MatchEntryEditor.Note"/> carries a <see cref="ControlCharNote">control char</see>
    /// that must render as inert plain text.</para>
    ///
    /// <para>Beyond the PNG, <see cref="AssertIgnoreTabSurface"/> pins fixture soundness the PNG
    /// can't: every one of the built-in ignore entries AND the one nesting exception backs a realized,
    /// bound control in the visual tree (so the list can never silently drop rows and still look
    /// plausible), the nesting exception's <see cref="MatchEntryEditor.EndpointEditable"/> is true
    /// (the endpoint control's presence is bound to it — naming/simple exceptions hide it), and the
    /// control-char note is rendered VERBATIM into a <c>TextBlock</c>/<c>SelectableTextBlock</c>
    /// (#45 — never interpreted, never a markup surface). <b>RED</b> until
    /// <c>src/App/Views/SettingsWindow.axaml(.cs)</c>'s Ignore &amp; Exceptions tab binds
    /// <see cref="SettingsViewModel.Ignore"/> and the per-rule exception lists (today the tab is a
    /// placeholder TextBlock — no ignore row or endpoint control realizes).</para>
    /// </summary>
    [AvaloniaTheory]
    [InlineData(1280, 720)]
    [InlineData(1920, 1080)]
    public void Settings_Ignore(int width, int height)
    {
        var vm = SettingsViewModel.LoadFrom(RulesetLoader.LoadDefault());

        // Soundness BEFORE the window: the default ships the full built-in ignore set (the
        // strict-AGDLP suppression entries) — the canonical global-ignore shape the capture wants.
        // The exact count is DERIVED from the live default mirror (it is 24 dn entries today; the
        // task prompt's "23" is stale — the default carries 24), never a brittle literal that would
        // fail this fixture for an off-by-one rather than for the missing Ignore tab. A >= floor
        // guards against an empty list so the captured surface is never a stub.
        var ignoreCount = vm.Ignore.Count;
        Assert.True(ignoreCount >= 20, $"the default must ship the full built-in ignore set; got {ignoreCount}");

        // One nesting exception, the only place an endpoint (Any/Parent/Member) is legal: it must
        // load endpoint-EDITABLE (the endpoint control's presence binds to this) and carries a
        // control-char note so the captured surface evidences the #45 plain-text rendering.
        var nestingException = MatchEntryEditor.LoadFrom(
            new MatchEntry { Dn = "CN=Svc-Backup,OU=Service,*", Note = ControlCharNote, Endpoint = MatchEndpoint.Member },
            endpointEditable: true);
        Assert.True(
            nestingException.EndpointEditable,
            "a nesting exception must be endpoint-editable (the endpoint control's presence binds to it)");
        Assert.Equal(MatchEndpoint.Member, nestingException.Endpoint);
        vm.Nesting.Exceptions.Add(nestingException);

        var window = new SettingsWindow { DataContext = vm, Width = width, Height = height };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        // Bring the Ignore & Exceptions tab to the front so its lists (the captured surface) realize.
        SelectTab(window, "Ignore");
        Dispatcher.UIThread.RunJobs();

        // The ignore rows + the nesting exception (with its endpoint control + plain-text note)
        // actually realized — the soundness pins the PNG can't make.
        AssertIgnoreTabSurface(window, vm, nestingException);

        CaptureWindowPng(window, "settings-ignore", width, height);
        window.Close();
    }

    /// <summary>
    /// Fixture-soundness pin for the Ignore &amp; Exceptions tab (AP 3.3 / S7): the realized window
    /// (a) backs EVERY one of the 23 <see cref="SettingsViewModel.Ignore"/> entries AND the seeded
    /// nesting <paramref name="nestingException"/> with a bound, effectively-visible control (located
    /// via the <see cref="MatchEntryEditor"/> instances themselves, which the tab binds as each row's
    /// DataContext) — so the list can never silently drop rows and still look plausible in a static
    /// frame; and (b) renders the control-char note VERBATIM into a <c>TextBlock</c> /
    /// <c>SelectableTextBlock</c> editing control's text or content (#45 — never interpreted as a BEL
    /// or an ANSI escape, never a markup surface). The endpoint control's presence is asserted at the
    /// model level (<c>EndpointEditable</c> true) by the caller; this pin proves the row realized.
    /// </summary>
    private static void AssertIgnoreTabSurface(
        SettingsWindow window, SettingsViewModel vm, MatchEntryEditor nestingException)
    {
        Assert.NotEmpty(vm.Ignore); // the canonical built-in ignore set (24 entries in the default)

        var boundDataContexts = window.GetVisualDescendants()
            .OfType<Avalonia.Controls.Control>()
            .Where(c => c.IsEffectivelyVisible)
            .Select(c => c.DataContext)
            .ToHashSet();

        foreach (var entry in vm.Ignore)
        {
            Assert.True(
                boundDataContexts.Contains(entry),
                $"the ignore list must realize a bound row control for '{entry.Value}'");
        }

        Assert.True(
            boundDataContexts.Contains(nestingException),
            "the nesting exception list must realize a bound row control (with its endpoint control)");

        // #45: the control-char note rendered VERBATIM somewhere in frame — a TextBlock or
        // SelectableTextBlock whose Text is byte/ordinal-equal to the seeded note (the BEL and the
        // ANSI escape are inert characters, never interpreted). A TextBox EDITING the note (Text ==
        // the note) is also fine — the rule is "never a format/markup surface", not "never editable".
        var verbatim = window.GetVisualDescendants()
            .OfType<Avalonia.Controls.TextBlock>()
            .Any(t => string.Equals(t.Text, ControlCharNote, StringComparison.Ordinal))
            || window.GetVisualDescendants()
                .OfType<Avalonia.Controls.TextBox>()
                .Any(t => string.Equals(t.Text, ControlCharNote, StringComparison.Ordinal));

        Assert.True(
            verbatim,
            "the control-char note must render VERBATIM (TextBlock/SelectableTextBlock/TextBox), "
            + "never interpreted as a BEL or ANSI escape and never a markup surface (#45)");
    }

    // --- Settings window: per-rule exception ENDPOINT control (AP 3.3 / S7, S7 evidence gap) -------

    /// <summary>
    /// AP 3.3 / S7 (ADR-011; spec "Ignore + exceptions"; S7 ui-verifier EVIDENCE-COVERAGE gap):
    /// the modal <see cref="SettingsWindow"/>'s <b>Ignore &amp; Exceptions</b> tab rendered with the
    /// per-rule <b>nesting-exception</b> section SCROLLED INTO THE VIEWPORT, so the captured pixels
    /// finally evidence the two surfaces the checklist asks the verifier to JUDGE visually but which
    /// sit below the <see cref="Settings_Ignore"/> scroll fold (the global ignore list renders first):
    /// (a) the nesting-exception <b>Any/Parent/Member endpoint ComboBox</b> — the control that is
    /// legal ONLY on a nesting exception (naming/circular/empty-group exceptions HIDE it, bound to
    /// <see cref="MatchEntryEditor.EndpointEditable"/>); and (b) a nesting-exception <c>Note</c>
    /// carrying a <see cref="ControlCharNote">control char</see> rendered INERT/plain (#45).
    ///
    /// <para>The companion <see cref="Settings_Ignore"/> fixture already seeds an identical nesting
    /// exception, but its <c>settings-ignore-*.png</c> frame is anchored at the top of the tab (the
    /// 24-entry global ignore list), so the endpoint control and the note never enter its captured
    /// pixels — the precise gap the S7 ui-verifier flagged. Here the realized endpoint ComboBox is
    /// located by its <c>DataContext</c> (the seeded nesting exception) and <c>BringIntoView()</c>d
    /// so the tab's <see cref="ScrollViewer"/> scrolls it into frame BEFORE capture; the
    /// non-virtualizing exception <c>ItemsControl</c> realizes every row regardless, so the scroll is
    /// purely to move the already-realized control into the captured viewport.</para>
    ///
    /// <para><see cref="AssertExceptionEndpointSurface"/> pins fixture soundness the PNG can't: the
    /// nesting exception's endpoint ComboBox is realized AND effectively-visible (the
    /// <c>EndpointEditable</c>-bound control the naming/circular/empty-group exceptions hide), its
    /// <c>SelectedItem</c> is the seeded <see cref="MatchEndpoint.Member"/>, and the control-char note
    /// renders VERBATIM into a plain <c>Text</c> target (#45 — never interpreted, never a markup
    /// surface).</para>
    /// </summary>
    [AvaloniaTheory]
    [InlineData(1280, 720)]
    [InlineData(1920, 1080)]
    public void Settings_Exceptions(int width, int height)
    {
        var vm = SettingsViewModel.LoadFrom(RulesetLoader.LoadDefault());

        // One nesting exception — the ONLY exception list whose endpoint (Any/Parent/Member) is legal.
        // It must load endpoint-EDITABLE (the endpoint control's presence binds to this) and carries a
        // control-char note so the scrolled-in frame evidences the #45 plain-text rendering. Endpoint is
        // seeded to Member so the captured ComboBox shows a non-default (non-Any) selection.
        var nestingException = MatchEntryEditor.LoadFrom(
            new MatchEntry { Dn = "CN=Svc-Backup,OU=Service,*", Note = ControlCharNote, Endpoint = MatchEndpoint.Member },
            endpointEditable: true);
        Assert.True(
            nestingException.EndpointEditable,
            "a nesting exception must be endpoint-editable (the endpoint control's presence binds to it)");
        Assert.Equal(MatchEndpoint.Member, nestingException.Endpoint);
        vm.Nesting.Exceptions.Add(nestingException);

        var window = new SettingsWindow { DataContext = vm, Width = width, Height = height };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        // Bring the Ignore & Exceptions tab to the front so its lists (the captured surface) realize.
        SelectTab(window, "Ignore");
        Dispatcher.UIThread.RunJobs();

        // Scroll the nesting-exception ENDPOINT control into the captured viewport — the S7 evidence
        // gap. The endpoint ComboBox is located by its DataContext (the seeded nesting exception); a
        // headless BringIntoView drives the tab's ScrollViewer (no input desktop needed — the same
        // programmatic-scroll seam the other settings fixtures rely on for tab realization).
        var endpointBox = ScrollEndpointIntoView(window, nestingException);
        Dispatcher.UIThread.RunJobs();

        // The endpoint control + the plain-text note actually realized and are in view — the soundness
        // pins the PNG can't make. The visual-tree assertion the task requires lives here.
        AssertExceptionEndpointSurface(window, endpointBox, nestingException);

        CaptureWindowPng(window, "settings-exceptions", width, height);
        window.Close();
    }

    /// <summary>Locates the realized Any/Parent/Member endpoint <see cref="Avalonia.Controls.ComboBox"/>
    /// for <paramref name="nestingException"/> (the one whose <c>DataContext</c> IS that exception) and
    /// <c>BringIntoView()</c>s it so the tab's <see cref="ScrollViewer"/> scrolls it into the captured
    /// viewport. The exception <c>ItemsControl</c> is non-virtualizing, so the row is already realized;
    /// this only moves it into frame. Returns the endpoint ComboBox for the soundness assertion.</summary>
    private static Avalonia.Controls.ComboBox ScrollEndpointIntoView(
        SettingsWindow window, MatchEntryEditor nestingException)
    {
        // The endpoint ComboBox binds SelectedItem to the entry's Endpoint and lives on the row whose
        // DataContext is the entry; it is the ONLY ComboBox under that row over MatchEndpoint items.
        var endpointBox = Assert.Single(
            window.GetVisualDescendants().OfType<Avalonia.Controls.ComboBox>(),
            cb => ReferenceEquals(cb.DataContext, nestingException)
                && cb.Items.OfType<MatchEndpoint>().Any());

        // Scroll the BOTTOM of the exception row — its note TextBox (the Grid.Row=1 cell below the
        // endpoint) — into view, so BOTH the endpoint ComboBox (the row above) AND the control-char
        // note land in the captured viewport together; bringing only the ComboBox into view leaves the
        // note one line past the fold. BringIntoView is an extension on ControlExtensions; this file
        // fully-qualifies Avalonia.Controls types (no `using Avalonia.Controls;`), so invoke it statically.
        var noteBox = Assert.Single(
            window.GetVisualDescendants().OfType<Avalonia.Controls.TextBox>(),
            tb => ReferenceEquals(tb.DataContext, nestingException)
                && string.Equals(tb.Text, ControlCharNote, StringComparison.Ordinal));
        Avalonia.Controls.ControlExtensions.BringIntoView(noteBox);
        return endpointBox;
    }

    /// <summary>
    /// Fixture-soundness pin for the exception-endpoint surface (AP 3.3 / S7; the S7 evidence gap):
    /// the realized window (a) backs the seeded nesting <paramref name="nestingException"/> with a
    /// realized, effectively-visible endpoint <see cref="Avalonia.Controls.ComboBox"/> over the
    /// Any/Parent/Member items — the <c>EndpointEditable</c>-bound control naming/circular/empty-group
    /// exceptions HIDE — whose <c>SelectedItem</c> is the seeded <see cref="MatchEndpoint.Member"/>; and
    /// (b) renders the control-char note VERBATIM into a plain <c>Text</c> target (#45). The endpoint
    /// control is located in <see cref="ScrollEndpointIntoView"/> by its <c>DataContext</c>; this pin
    /// proves it realized, is visible, carries the three endpoint choices, and shows the right one.
    /// </summary>
    private static void AssertExceptionEndpointSurface(
        SettingsWindow window, Avalonia.Controls.ComboBox endpointBox, MatchEntryEditor nestingException)
    {
        // The endpoint control is the EndpointEditable-bound one — naming/circular/empty-group
        // exceptions hide it; only this nesting exception realizes it. It is realized AND visible.
        Assert.True(
            endpointBox.IsEffectivelyVisible,
            "the nesting-exception endpoint ComboBox must be realized and visible (naming/circular/"
            + "empty-group exceptions hide it — EndpointEditable=false)");

        // It carries the three legal endpoints and shows the seeded one (Member, not the Any default).
        Assert.Equal(
            new[] { MatchEndpoint.Any, MatchEndpoint.Parent, MatchEndpoint.Member },
            endpointBox.Items.OfType<MatchEndpoint>().ToArray());
        Assert.Equal(MatchEndpoint.Member, endpointBox.SelectedItem);
        Assert.True(
            nestingException.EndpointEditable,
            "the endpoint control's presence is bound to EndpointEditable (true only for nesting)");

        // #45: the control-char note rendered VERBATIM somewhere in frame — a TextBlock/
        // SelectableTextBlock whose Text is byte/ordinal-equal to the seeded note, or a TextBox
        // EDITING it. The BEL and ANSI escape are inert characters, never interpreted, never markup.
        var verbatim = window.GetVisualDescendants()
            .OfType<Avalonia.Controls.TextBlock>()
            .Any(t => string.Equals(t.Text, ControlCharNote, StringComparison.Ordinal))
            || window.GetVisualDescendants()
                .OfType<Avalonia.Controls.TextBox>()
                .Any(t => string.Equals(t.Text, ControlCharNote, StringComparison.Ordinal));

        Assert.True(
            verbatim,
            "the nesting-exception note must render VERBATIM (TextBlock/SelectableTextBlock/TextBox), "
            + "never interpreted as a BEL or ANSI escape and never a markup surface (#45)");
    }

    // --- Settings window: File tab (AP 3.3 / S7, S7 evidence gap) ----------------------------------

    /// <summary>
    /// AP 3.3 / S7 (ADR-011; spec "Import/Export/Reset"; S7 ui-verifier EVIDENCE-COVERAGE gap): the
    /// modal <see cref="SettingsWindow"/>'s <b>File</b> tab rendered standalone — the same headless
    /// seam the other settings fixtures pin (<c>.Show()</c>, NOT <c>ShowDialog</c>; open-risk #3),
    /// capturing <c>settings-file-{W}x{H}.png</c> at both checklist sizes. The File tab is on an
    /// UNSELECTED tab in every other settings fixture, so its three file-action buttons
    /// (Import… / Export… / Reset to default) and the persistent Apply/Save/Cancel footer never
    /// entered any captured frame — the precise gap the S7 ui-verifier flagged.
    ///
    /// <para>The VM is seeded from <see cref="RulesetLoader.LoadDefault"/> through the mirror
    /// (<see cref="SettingsViewModel.LoadFrom"/>) — the demo-mode-safe default, never a lab file. The
    /// File tab is selected so its three buttons are the captured surface; the Apply/Save/Cancel footer
    /// is OUTSIDE the TabControl, so it is in frame on every tab and this capture re-evidences it.</para>
    ///
    /// <para><see cref="AssertFileTabButtons"/> pins fixture soundness the PNG can't: the three
    /// file-action buttons (Import… / Export… / Reset to default) are realized and effectively-visible
    /// in the File tab, and the footer's Apply/Save/Cancel buttons are realized — so a static frame can
    /// never look plausible while a file action or footer button is missing.</para>
    /// </summary>
    [AvaloniaTheory]
    [InlineData(1280, 720)]
    [InlineData(1920, 1080)]
    public void Settings_File(int width, int height)
    {
        var vm = SettingsViewModel.LoadFrom(RulesetLoader.LoadDefault());

        var window = new SettingsWindow { DataContext = vm, Width = width, Height = height };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        // Bring the File tab to the front so its Import…/Export…/Reset buttons (the captured surface)
        // are realized.
        SelectTab(window, "File");
        Dispatcher.UIThread.RunJobs();

        // The three file-action buttons + the Apply/Save/Cancel footer actually realized — the
        // soundness pins the PNG can't make. The visual-tree assertion the task requires lives here.
        AssertFileTabButtons(window);

        CaptureWindowPng(window, "settings-file", width, height);
        window.Close();
    }

    /// <summary>
    /// Fixture-soundness pin for the File tab (AP 3.3 / S7; the S7 evidence gap): the realized window
    /// surfaces the three file-action <see cref="Avalonia.Controls.Button"/>s — <c>Import…</c>,
    /// <c>Export…</c>, <c>Reset to default</c> — each realized and effectively-visible on the File tab,
    /// plus the persistent footer <c>Apply</c>/<c>Save</c>/<c>Cancel</c> buttons (OUTSIDE the
    /// TabControl, so in frame on every tab). Located by button content; so the tab can never silently
    /// drop a file action and still look plausible in a static frame.
    /// </summary>
    private static void AssertFileTabButtons(SettingsWindow window)
    {
        var buttons = window.GetVisualDescendants()
            .OfType<Avalonia.Controls.Button>()
            .Where(b => b.IsEffectivelyVisible)
            .ToList();

        bool HasButton(string content) => buttons.Any(b =>
            string.Equals(b.Content?.ToString(), content, StringComparison.Ordinal));

        // The three file actions (the captured surface) — content matches the SettingsWindow.axaml labels.
        Assert.True(HasButton("Import…"), "the File tab must realize an Import… button");
        Assert.True(HasButton("Export…"), "the File tab must realize an Export… button");
        Assert.True(HasButton("Reset to default"), "the File tab must realize a Reset to default button");

        // The persistent footer (outside the TabControl) is re-evidenced in this frame.
        Assert.True(HasButton("Apply"), "the footer must realize an Apply button");
        Assert.True(HasButton("Save"), "the footer must realize a Save button");
        Assert.True(HasButton("Cancel"), "the footer must realize a Cancel button");
    }

    // --- Settings window: Validation banner + error panel (AP 3.3 / S7) ---------------------------

    /// <summary>The error path the validation fixture seeds — the loader's own path shape for a
    /// rejected severity token (the <c>$.circular.severity</c> defect the <c>SettingsValidationTests</c>
    /// broken-file arm also produces), so the captured panel reads as a real loader finding.</summary>
    private const string ValidationErrorPath = "$.circular.severity";

    /// <summary>
    /// AP 3.3 / S7 (ADR-011; spec "Validation panel + invalid-file-on-open" + "ui-checklist
    /// additions"): the modal <see cref="SettingsWindow"/> opened on a REJECTED user file rendered
    /// standalone — same headless seam (<c>.Show()</c>, NOT <c>ShowDialog</c>), capturing
    /// <c>settings-validation-{W}x{H}.png</c> at both checklist sizes for the ui-verifier to judge
    /// the persistent validation band + the invalid-user-file banner against the new section-B rows.
    ///
    /// <para>The VM is seeded the production way for the invalid-on-open path:
    /// <see cref="SettingsViewModel.Open"/> over an <see cref="EffectiveRuleset"/> whose
    /// <see cref="EffectiveRuleset.Errors"/> is non-empty and <see cref="EffectiveRuleset.FromUserFile"/>
    /// is false (the app is running on the embedded default because the saved file was rejected — the
    /// AP 3.4 errors that were threaded but unsurfaced finally appear). That seeds the mirror from the
    /// default, sets <c>RunningOnDefaultBecauseInvalid</c> (the banner), and surfaces the errors into
    /// the validation panel. One seeded error's <see cref="RulesetValidationError.Message"/> carries a
    /// <see cref="ControlCharNote">control char</see> so the captured panel ALSO evidences #45: the
    /// loader message is plain text (<c>SelectableTextBlock.Text</c>), never a markup/format surface.
    /// The locator is a temp-dir seam (never real <c>%APPDATA%</c>) and <c>Open</c> writes nothing.</para>
    ///
    /// <para><see cref="AssertValidationSurface"/> pins fixture soundness the PNG can't: the banner is
    /// flagged, every seeded error backs a realized row whose Path and Message render as SEPARATE plain
    /// <c>Text</c> targets (never interpolated into one format template), and the control-char message
    /// renders VERBATIM (#45). <b>RED</b> until the validation band + banner are wired AND the Ignore/File
    /// tabs exist (the window is constructed regardless, but the S7 surface is what makes the capture a
    /// faithful settings-validation frame).</para>
    /// </summary>
    [AvaloniaTheory]
    [InlineData(1280, 720)]
    [InlineData(1920, 1080)]
    public void Settings_Validation(int width, int height)
    {
        using var dir = new TempArtifactDir();
        var locator = new RulesetLocator(dir.Path);

        // The rejected-user-file effective: the app runs on the embedded default, carrying the
        // loader's path-addressed errors. One message carries a control char to pin #45 in the panel.
        var errors = new List<RulesetValidationError>
        {
            new("$.schemaVersion", "Unsupported schemaVersion 99 (this build understands version 1)."),
            new(ValidationErrorPath, "Unknown severity token; " + ControlCharNote),
        };
        var effective = new EffectiveRuleset(RulesetLoader.LoadDefault(), FromUserFile: false, errors);

        var vm = SettingsViewModel.Open(effective, locator);

        // Soundness BEFORE the window: the invalid-on-open seam flagged the banner and surfaced the
        // EXACT errors (no parallel validator — the locator's carried list, verbatim).
        Assert.True(vm.RunningOnDefaultBecauseInvalid, "an invalid user file on open must flag the banner");
        Assert.Equal(errors, vm.ValidationErrors.ToList());

        var window = new SettingsWindow { DataContext = vm, Width = width, Height = height };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        // The banner + error panel actually realized — the soundness pins the PNG can't make.
        AssertValidationSurface(window, vm);

        CaptureWindowPng(window, "settings-validation", width, height);
        window.Close();
    }

    /// <summary>
    /// Fixture-soundness pin for the validation band + invalid-file banner (AP 3.3 / S7): the realized
    /// window (a) flags <see cref="SettingsViewModel.RunningOnDefaultBecauseInvalid"/>; (b) renders,
    /// for every seeded <see cref="RulesetValidationError"/>, its <c>Path</c> AND its <c>Message</c> as
    /// SEPARATE plain <c>Text</c> targets — the message into a <c>TextBlock</c>/<c>SelectableTextBlock</c>,
    /// NEVER interpolated into one "{path} — {message}" template (so an untrusted message can never
    /// reach a format/markup surface, #45); and (c) renders the control-char message VERBATIM
    /// (byte/ordinal-equal). Located structurally (the error rows are not named controls): the panel's
    /// realized text blocks must include each error's Path and each error's Message as distinct strings.
    /// </summary>
    private static void AssertValidationSurface(SettingsWindow window, SettingsViewModel vm)
    {
        Assert.True(vm.RunningOnDefaultBecauseInvalid);
        Assert.NotEmpty(vm.ValidationErrors);

        // Every realized text-bearing control's verbatim Text (TextBlock covers SelectableTextBlock,
        // which derives from it; the band binds Message to SelectableTextBlock.Text per #45).
        var renderedTexts = window.GetVisualDescendants()
            .OfType<Avalonia.Controls.TextBlock>()
            .Where(t => t.IsEffectivelyVisible && !string.IsNullOrEmpty(t.Text))
            .Select(t => t.Text!)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var error in vm.ValidationErrors)
        {
            // Path and Message are SEPARATE Text targets — each appears verbatim and on its own
            // (never fused into one "{path} — {message}" interpolation that would make the message
            // part of a format string). The Message equality is ORDINAL so the control-char message
            // is pinned byte-for-byte (#45).
            Assert.Contains(error.Path, renderedTexts);
            Assert.Contains(error.Message, renderedTexts);
        }
    }

    /// <summary>A self-deleting temp dir for the validation fixture's <see cref="RulesetLocator"/>
    /// seam — <see cref="SettingsViewModel.Open"/> writes nothing on the invalid-on-open path, but the
    /// locator must never point at real <c>%APPDATA%</c> from a screenshot test (demo-mode discipline).</summary>
    private sealed class TempArtifactDir : IDisposable
    {
        public string Path { get; } =
            Directory.CreateTempSubdirectory("groupweaver-settings-validation-fixture-").FullName;

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    /// <summary>Brings the <see cref="Avalonia.Controls.TabItem"/> whose header text contains
    /// <paramref name="header"/> to the front of the window's <see cref="Avalonia.Controls.TabControl"/>,
    /// so its content is realized for capture. Header match is substring + ordinal-ignore-case so
    /// a "Naming" tab still resolves if the implementer labels it "Naming rules".</summary>
    private static void SelectTab(SettingsWindow window, string header)
    {
        var tabs = Assert.Single(window.GetVisualDescendants().OfType<Avalonia.Controls.TabControl>());
        var item = Assert.Single(
            tabs.GetVisualDescendants().OfType<Avalonia.Controls.TabItem>(),
            t => (t.Header?.ToString() ?? string.Empty)
                .Contains(header, StringComparison.OrdinalIgnoreCase));
        tabs.SelectedItem = item;
    }

    /// <summary>The capture core for a standalone settings <see cref="Window"/> — same
    /// capture-and-discard + real-rasterization gate as <see cref="CapturePng"/> for
    /// <see cref="MainWindow"/>, but typed to <see cref="Avalonia.Controls.Window"/> so the
    /// settings fixtures share it.</summary>
    private static void CaptureWindowPng(Avalonia.Controls.Window window, string name, int width, int height)
    {
        Dispatcher.UIThread.RunJobs();

        // Same lagging-compositor rule as CapturePng: the first capture after a mutation
        // (here: Show + tab select) returns the previous frame — discard it, then capture.
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

    /// <summary>Real DemoProvider behind the factory: these frames are the demo-mode truth.
    /// The shell is seeded with a FRESH temp-dir <see cref="UiStateStore"/> (ADR-022 D4) so every
    /// frame renders the DEFAULT rail state (expanded, 340px) reproducibly and — critically —
    /// never reads or writes real <c>%APPDATA%</c>: a focus/collapse frame must not persist a
    /// collapsed rail that would silently collapse every later demo frame (demo-mode discipline,
    /// CLAUDE.md). The temp dir starts empty, so Load yields <see cref="UiState.Default"/>.
    /// <paramref name="rendererFactory"/> is null for the renderer-less frames (GraphHost shows the
    /// placeholder); the focus frame supplies one so a graph surface FILLS the host — the ADR-022 D2
    /// "graph gets the whole screen" intent.</summary>
    private static (MainWindow Window, ShellViewModel Shell) ShowShell(
        WebView2RuntimeStatus status, int width, int height,
        Func<IGraphRenderer>? rendererFactory = null,
        Avalonia.Styling.ThemeVariant? theme = null)
    {
        var uiStateBase = Directory.CreateTempSubdirectory("groupweaver-shellshot-uistate-").FullName;
        var shell = new ShellViewModel(
            _ => new DemoProvider(), new StartupOptions(Demo: false), status,
            graphRendererFactory: rendererFactory, ruleset: null, locator: null,
            uiStateStore: new UiStateStore(uiStateBase));

        // Size BEFORE Show so every layout pass — including ListBox virtualization —
        // happens against the final viewport.
        var window = new MainWindow { DataContext = shell, Width = width, Height = height };

        // Optional WINDOW-SCOPED theme override (parity with ViolationsSidebarViewTests'
        // ActiveRowBand_ReTonesLive_WhenTheThemeFlipsToLight): unlike
        // ThemeVariantScreenshotTests' Application.Current.RequestedThemeVariant flip (flagged
        // there as shared global state that must be restored on every exit path), setting
        // TopLevel.RequestedThemeVariant on THIS window before Show() re-resolves the
        // DynamicResource style setters for this window alone — no global leak, no restore
        // step to forget. Null (the default) preserves every existing call site's behavior
        // byte-for-byte.
        if (theme is not null)
        {
            window.RequestedThemeVariant = theme;
        }

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
