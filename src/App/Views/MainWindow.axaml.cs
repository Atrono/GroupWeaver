using System;
using System.ComponentModel;

using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;

using GroupWeaver.App.Diagnostics;
using GroupWeaver.App.Export;
using GroupWeaver.App.Graph;
using GroupWeaver.App.ViewModels;

using Microsoft.Extensions.Logging;

namespace GroupWeaver.App.Views;

public sealed partial class MainWindow : Window
{
    /// <summary>The <see cref="WindowState"/> to restore to when full-screen is toggled off
    /// (ADR-022 D1) — captured the moment full-screen is entered, so restore returns to the exact
    /// prior state (Normal / Maximized). <c>null</c> while not in full-screen.</summary>
    private WindowState? _preFullScreenState;

    /// <summary>The ONE production export save-picker seam (AP 4.2.4 / ADR-013 §5, fixes #63):
    /// built once in <see cref="OnOpened"/> from this window's <c>TopLevel</c> and pushed into
    /// whatever exportable <c>CurrentStep</c> is (or becomes) current via <see cref="WireExport"/>.
    /// Without this the AP 4.1 export commands ship permanently disabled (the seam was never
    /// wired in production).</summary>
    private IExportFileDialogs? _exportDialogs;

    /// <summary>The ONE window-scoped graph-surface coordinator (#122 / ADR-025): built once in
    /// <see cref="OnOpened"/> over the hidden <c>ParkingLot</c> Panel and pushed — exactly like
    /// <see cref="_exportDialogs"/> — into the shell and each graph-bearing <c>CurrentStep</c> (via
    /// <see cref="WireGraphSurface"/>). It lets the shell park a Back-target surface before a
    /// forward swap and the returning view re-mount the live control, preserving the viewport.</summary>
    private IGraphSurfaceCoordinator? _surfaceCoordinator;

    /// <summary>The shell whose <c>CurrentStep</c> changes are watched, kept so the subscription
    /// is torn down in <see cref="OnClosed"/> (no leak).</summary>
    private ShellViewModel? _wiredShell;

    /// <summary>The shell <c>PropertyChanged</c> handler (<c>CurrentStep</c> re-arm + the #230
    /// focus-mode focus parking), kept so the exact delegate can be unsubscribed in
    /// <see cref="OnClosed"/>.</summary>
    private PropertyChangedEventHandler? _shellPropertyChanged;

    public MainWindow()
    {
        InitializeComponent();
    }

    /// <summary>ADR-022 D1 key chrome (pure view, [I] interactive): F11 toggles full-screen,
    /// Esc exits full-screen AND focus mode. Handled here rather than as XAML KeyBindings so the
    /// gestures bind to this window's own methods (the DataContext is the shell). Other keys fall
    /// through to the base handler.</summary>
    protected override void OnKeyDown(KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.F11:
                ToggleFullScreen();
                e.Handled = true;
                break;
            case Key.Escape:
                // Only swallow Escape when it actually exited full-screen / focus mode, so a
                // child control's own Escape handling (popups, combo boxes) is left untouched.
                e.Handled = ExitFullScreenAndFocus();
                break;
            case Key.F:
                // ADR-022 addendum: single 'F' toggles focus mode, but ONLY on the workspace step —
                // there is no native text input there to hijack (the web Find box lives inside the
                // WebView's own HWND). Off the workspace it falls through unhandled. Single-key (not
                // a chord) so the demo recorder can post it via WM_KEYDOWN.
                if (DataContext is ShellViewModel shell && shell.CurrentStep is WorkspaceViewModel)
                {
                    shell.ToggleFocusModeCommand.Execute(null);
                    e.Handled = true;
                }

                break;
        }

        base.OnKeyDown(e);
    }

    /// <summary>F11 toggle (ADR-022 D1): entering full-screen remembers the current
    /// <see cref="WindowState"/>; leaving restores it. Avalonia drops the OS title bar in
    /// <see cref="WindowState.FullScreen"/>.</summary>
    private void ToggleFullScreen()
    {
        if (WindowState == WindowState.FullScreen)
        {
            WindowState = _preFullScreenState ?? WindowState.Normal;
            _preFullScreenState = null;
        }
        else
        {
            _preFullScreenState = WindowState;
            WindowState = WindowState.FullScreen;
        }
    }

    /// <summary>Esc (ADR-022 D1): leaves full-screen (restoring the remembered state) and exits
    /// focus mode via the shell's <c>ExitFocusModeCommand</c> — each a no-op when already off.
    /// Returns <c>true</c> iff either actually changed, so the caller swallows Escape only then.</summary>
    private bool ExitFullScreenAndFocus()
    {
        var exited = false;

        if (WindowState == WindowState.FullScreen)
        {
            WindowState = _preFullScreenState ?? WindowState.Normal;
            _preFullScreenState = null;
            exited = true;
        }

        if (DataContext is ShellViewModel shell && shell.IsFocusMode)
        {
            shell.ExitFocusModeCommand.Execute(null);
            exited = true;
        }

        return exited;
    }

    /// <summary>
    /// Wires the production export seam (AP 4.2.4 / ADR-014; mirrors
    /// <c>SettingsWindow.OnOpened</c>, fixes #63): once the window is open it owns a
    /// <c>TopLevel</c>, so it builds ONE <see cref="StorageProviderExportFileDialogs"/> from it
    /// and pushes it into the current exportable step. It also subscribes to the shell's
    /// <c>CurrentStep</c> changes so a workspace or a Plan step created LATER is wired too. The
    /// headless screenshot/test path also opens the window but never invokes a picker, so this
    /// thin <c>[I]</c> wiring only ARMS the export commands there.
    /// </summary>
    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);

        LogUiEnvironment();

        if (GetTopLevel(this) is not { } topLevel || DataContext is not ShellViewModel shell)
        {
            return;
        }

        _exportDialogs = new StorageProviderExportFileDialogs(topLevel);
        _wiredShell = shell;

        // #122 (ADR-025): build the ONE graph-surface coordinator over the hidden parking Panel
        // and push it into the shell (which parks the Back-target surface before a forward swap).
        _surfaceCoordinator = new GraphSurfaceCoordinator(ParkingLot);
        shell.UseGraphSurfaceCoordinator(_surfaceCoordinator);

        // Arm whatever step is current right now…
        WireExport(shell.CurrentStep);
        WireGraphSurface(shell.CurrentStep);

        // …and re-arm each new step (a workspace, or a Plan/Gap step entered later). The SAME
        // subscription carries the #230 focus-continuity branch: firing on the IsFocusMode
        // property change catches every entry vector (button, 'F' key, command) at one choke point.
        _shellPropertyChanged = (_, args) =>
        {
            if (args.PropertyName == nameof(ShellViewModel.CurrentStep))
            {
                WireExport(shell.CurrentStep);
                WireGraphSurface(shell.CurrentStep);
            }
            else if (args.PropertyName == nameof(ShellViewModel.IsFocusMode))
            {
                MoveKeyboardFocusForFocusMode(shell.IsFocusMode);
            }
        };
        shell.PropertyChanged += _shellPropertyChanged;
    }

    /// <summary>The <c>UiEnvironment</c> line (ADR-037 D6): screen count, the primary screen's
    /// pixel bounds, and <see cref="TopLevel.RenderScaling"/> — the DPI/layout facts every
    /// rendering bug report needs. Never-throw (headless platforms may expose no screens); a
    /// NullLogger no-ops this in tests.</summary>
    private void LogUiEnvironment()
    {
        try
        {
            var primary = Screens.Primary ?? Screens.All.FirstOrDefault();
            AppLog.CreateLogger("App.Lifecycle").LogInformation(
                new EventId(0, "UiEnvironment"),
                "UiEnvironment {screens} {resolution} {renderScaling}",
                Screens.All.Count,
                primary is null ? null : $"{primary.Bounds.Width}x{primary.Bounds.Height}",
                RenderScaling);
        }
        catch
        {
            // NEVER-THROW (ADR-037 D3): diagnostics must not break window startup.
        }
    }

    /// <summary>Pushes the one export seam into the current step if it is exportable (a
    /// workspace or a Plan step). A no-op before the seam exists or for a non-exportable step.</summary>
    private void WireExport(object? step)
    {
        if (_exportDialogs is null)
        {
            return;
        }

        switch (step)
        {
            case WorkspaceViewModel workspace:
                workspace.UseExportFileDialogs(_exportDialogs);
                break;
            case PlanViewModel plan:
                plan.UseExportFileDialogs(_exportDialogs);
                break;
            case AuditViewModel audit:
                audit.UseExportFileDialogs(_exportDialogs);
                break;
            case GapViewModel gap:
                gap.UseExportFileDialogs(_exportDialogs);
                break;
        }
    }

    /// <summary>Pushes the one graph-surface coordinator into the current step if it hosts the graph
    /// (Workspace/Plan/Gap), mirroring <see cref="WireExport"/> (#122 / ADR-025). A no-op before the
    /// coordinator exists or for a graph-less step. The view's mount path then reads the coordinator
    /// to re-mount a parked live surface (preserving the viewport) instead of the direct mount.</summary>
    private void WireGraphSurface(object? step)
    {
        if (_surfaceCoordinator is null)
        {
            return;
        }

        switch (step)
        {
            case WorkspaceViewModel workspace:
                workspace.UseGraphSurfaceCoordinator(_surfaceCoordinator);
                break;
            case PlanViewModel plan:
                plan.UseGraphSurfaceCoordinator(_surfaceCoordinator);
                break;
            case GapViewModel gap:
                gap.UseGraphSurfaceCoordinator(_surfaceCoordinator);
                break;
        }
    }

    /// <summary>The app's FIRST programmatic-focus pattern (#230 / ADR-022 2026-07-02 addendum) —
    /// future focus-moving features copy this seam rather than inventing a second one. Entering
    /// focus mode hides the very strip the Focus toggle lives in, so keyboard focus would be
    /// silently lost with it (WCAG 2.4.3-adjacent); this PARKS it on the seam chevron
    /// (<c>RailCollapseToggle</c>, WorkspaceView.axaml) — the designated surviving affordance
    /// ADR-022's Consequences named as the persistent exit — and NEVER the WebView2 HWND, whose
    /// native Win32 focus would swallow the window-level Esc/F handlers (the exit gestures).
    /// Exiting restores focus to the Focus toggle (symmetric round-trip), guarded by
    /// <c>IsEffectivelyVisible</c> so a non-workspace exit (programmatic/tests) skips silently.
    /// Both moves use <see cref="NavigationMethod.Tab"/> so <c>:focus-visible</c> fires and the
    /// ADR-033 accent focus ring RENDERS (WCAG 2.4.7) — the visible ring on an exit-adjacent
    /// control doubles as the "way back out" affordance.</summary>
    private void MoveKeyboardFocusForFocusMode(bool isFocusMode)
    {
        if (isFocusMode)
        {
            // Cross-namescope: the chevron lives in WorkspaceView's scope, not the window's.
            this.FindDescendantOfType<WorkspaceView>()
                ?.FindControl<Button>("RailCollapseToggle")
                ?.Focus(NavigationMethod.Tab);
        }
        else if (FocusModeButton.IsEffectivelyVisible)
        {
            FocusModeButton.Focus(NavigationMethod.Tab);
        }
    }

    /// <summary>
    /// Window teardown: unsubscribes the shell watcher (no leak — the shell
    /// outlives the window only in theory, but the handler closes over it) and disposes the
    /// shell, the one signal that cancels the workspace's in-flight scope load (AP 2.2 S6).
    /// </summary>
    protected override void OnClosed(EventArgs e)
    {
        if (_wiredShell is not null && _shellPropertyChanged is not null)
        {
            _wiredShell.PropertyChanged -= _shellPropertyChanged;
        }

        _wiredShell = null;
        _shellPropertyChanged = null;

        (DataContext as ShellViewModel)?.Dispose();

        base.OnClosed(e);
    }
}
