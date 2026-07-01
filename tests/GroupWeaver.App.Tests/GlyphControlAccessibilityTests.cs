using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;

using GroupWeaver.App.Graph;
using GroupWeaver.App.Settings;
using GroupWeaver.App.Startup;
using GroupWeaver.App.Tests.Fakes;
using GroupWeaver.App.ViewModels;
using GroupWeaver.App.Views;
using GroupWeaver.Core.Model;
using GroupWeaver.Providers;

using Xunit;

namespace GroupWeaver.App.Tests;

/// <summary>
/// Pins the #219 presentation-only accessibility slice (branch
/// <c>fix/219-glyph-control-a11y-names</c>): the four glyph-only chrome buttons each carry an
/// explicit <see cref="AutomationProperties"/> Name (Avalonia does NOT map a <c>ToolTip.Tip</c>
/// to the accessible/UIA Name, so a screen reader would otherwise announce only the bare glyph),
/// and the keyboard-help sheet gains an F1 keyboard entry point (WCAG 2.2 SC 2.1.1 / 2.4.4).
///
/// <para>Three of the four buttons (theme toggle, focus-mode, "?") live on
/// <see cref="MainWindow"/>'s top command strip and the fourth (the rail collapse toggle) inside
/// <see cref="WorkspaceView"/>, so this fixture realizes the FULL window and drives the shell to
/// the loaded workspace step — the focus-mode button is <c>IsVisible</c>-gated on
/// <c>IsWorkspaceStep</c> and the rail toggle only exists once the workspace renders. The window
/// is built through the same demo-driver seam <see cref="Settings.SettingsShellIntegrationTests"/>
/// uses, extended with a temp-dir <see cref="UiStateStore"/> (lab-environment.md: an untracked
/// real <c>%APPDATA%\GroupWeaver\ui-state.json</c> with <c>RailCollapsed:true</c> would zero the
/// ADR-022 rail so view-realization sees <c>[]</c> locally while CI stays green — inject the seam).
/// The shell propagates that one store down Shell→Workspace, so the same temp dir covers both the
/// shell theme seeding and the workspace rail state.</para>
///
/// <para>The F1 entry point is pinned by INSPECTING the <see cref="MainWindow"/>'s
/// <c>KeyBindings</c> (gesture = F1, command resolves to
/// <see cref="ShellViewModel.OpenKeyboardHelpCommand"/>) and asserting the bound command is armed
/// — never by simulating the keystroke, which would call the production <c>ShowDialog</c> and hang
/// the headless run on a modal loop (the same ADR-011 open-risk #3 the sibling shell tests avoid).
/// </para>
/// </summary>
public sealed class GlyphControlAccessibilityTests
{
    private const string DemoRootDn = "OU=AGDLP-Demo,DC=weavedemo,DC=example";

    // === the four glyph-only buttons expose a non-empty accessible name =====================

    /// <summary>
    /// The theme-toggle button's accessible Name is bound to the state-aware
    /// <see cref="ShellViewModel.ThemeTooltip"/> (same binding as its tooltip): non-empty, and it
    /// mirrors the live VM value exactly — a screen reader announces the current theme state, not
    /// the bare sun/moon glyph.
    /// </summary>
    [AvaloniaFact]
    public async Task ThemeToggleButton_ExposesTheStateAwareThemeTooltipAsItsAccessibleName()
    {
        await using var ctx = await DriveToWorkspaceWindowAsync();

        var button = FindByName(ctx.Window, "ThemeToggleButton");
        var name = AutomationProperties.GetName(button);

        Assert.False(string.IsNullOrWhiteSpace(name), "the theme toggle must expose an accessible name");
        Assert.Equal(ctx.Shell.ThemeTooltip, name);

        ctx.Window.Close();
    }

    /// <summary>
    /// The focus-mode button's accessible Name is the exact static string "Toggle focus mode"
    /// (its glyph is the bare "&#x26F6; Focus" symbol). Realized only on the workspace step, which
    /// this fixture drives to.
    /// </summary>
    [AvaloniaFact]
    public async Task FocusModeButton_ExposesTheExactStaticAccessibleName()
    {
        await using var ctx = await DriveToWorkspaceWindowAsync();

        var button = FindByName(ctx.Window, "FocusModeButton");

        Assert.True(button.IsEffectivelyVisible, "the focus-mode button is workspace-step gated and must be visible here");
        Assert.Equal("Toggle focus mode", AutomationProperties.GetName(button));

        ctx.Window.Close();
    }

    /// <summary>
    /// The keyboard-help "?" button's accessible Name is the exact static string
    /// "Keyboard shortcuts". Located by its COMMAND binding (robust against the bare "?" glyph
    /// appearing elsewhere), mirroring the sibling shell test's locator.
    /// </summary>
    [AvaloniaFact]
    public async Task KeyboardHelpButton_ExposesTheExactStaticAccessibleName()
    {
        await using var ctx = await DriveToWorkspaceWindowAsync();

        var button = Assert.Single(
            ctx.Window.GetVisualDescendants().OfType<Button>(),
            b => ReferenceEquals(b.Command, ctx.Shell.OpenKeyboardHelpCommand));

        Assert.Equal("Keyboard shortcuts", AutomationProperties.GetName(button));

        ctx.Window.Close();
    }

    /// <summary>
    /// The rail collapse toggle's accessible Name is bound (same converter, same text as its
    /// tooltip) through <see cref="RailChevronConverter.ToTooltip"/>: non-empty, and it mirrors the
    /// live <c>IsRailCollapsed</c> state exactly (the expanded state announces "Collapse rail…",
    /// not the bare &#x25C2;/&#x25B8; chevron glyph).
    /// </summary>
    [AvaloniaFact]
    public async Task RailCollapseToggle_ExposesTheStateAwareChevronTooltipAsItsAccessibleName()
    {
        await using var ctx = await DriveToWorkspaceWindowAsync();

        var button = FindByName(ctx.Window, "RailCollapseToggle");
        var name = AutomationProperties.GetName(button);

        Assert.False(string.IsNullOrWhiteSpace(name), "the rail collapse toggle must expose an accessible name");
        // The workspace starts expanded (default-seeded), so the name is the "collapse" affordance.
        var expected = (string)RailChevronConverter.ToTooltip.Convert(
            ctx.Workspace.IsRailCollapsed, typeof(string), null, System.Globalization.CultureInfo.InvariantCulture)!;
        Assert.Equal(expected, name);

        ctx.Window.Close();
    }

    /// <summary>
    /// The rail toggle's accessible name is STATE-BOUND: the converter that feeds it yields two
    /// distinct, non-empty strings for collapsed vs. expanded, so a screen reader announces the
    /// correct affordance in each state. Pinning the converter directly (rather than re-driving a
    /// live re-render) keeps this cheap and flake-free while still proving the name changes with
    /// state — the button binds this very converter over <c>IsRailCollapsed</c>.
    /// </summary>
    [Fact]
    public void RailChevronToTooltip_YieldsDistinctNonEmptyNames_ForCollapsedVsExpanded()
    {
        var expanded = Convert(collapsed: false);
        var collapsed = Convert(collapsed: true);

        Assert.False(string.IsNullOrWhiteSpace(expanded));
        Assert.False(string.IsNullOrWhiteSpace(collapsed));
        Assert.NotEqual(expanded, collapsed);

        static string Convert(bool collapsed) => (string)RailChevronConverter.ToTooltip.Convert(
            collapsed, typeof(string), null, System.Globalization.CultureInfo.InvariantCulture)!;
    }

    // === the F1 keyboard entry point (inspect the binding, never simulate the modal) ========

    /// <summary>
    /// The keyboard-help sheet has an F1 keyboard entry point: <see cref="MainWindow"/> carries a
    /// <see cref="KeyBinding"/> whose gesture is F1 and whose command IS
    /// <see cref="ShellViewModel.OpenKeyboardHelpCommand"/> and is armed. Asserted by INSPECTING the
    /// window's <c>KeyBindings</c> — never by pressing F1, which would invoke the production
    /// <c>ShowDialog</c> and hang the headless run on the modal loop (ADR-011 open-risk #3).
    /// </summary>
    [AvaloniaFact]
    public async Task MainWindow_HasAnF1KeyBinding_BoundToTheArmedKeyboardHelpCommand()
    {
        await using var ctx = await DriveToWorkspaceWindowAsync();

        var f1 = Assert.Single(
            ctx.Window.KeyBindings,
            kb => kb.Gesture is { Key: Key.F1, KeyModifiers: KeyModifiers.None });

        Assert.Same(ctx.Shell.OpenKeyboardHelpCommand, f1.Command);
        Assert.True(
            f1.Command!.CanExecute(null),
            "the F1-bound keyboard-help command must be armed (reachable via the keystroke)");

        ctx.Window.Close();
    }

    // === harness ============================================================================

    /// <summary>
    /// Builds the real <see cref="MainWindow"/> over a demo <see cref="ShellViewModel"/> (temp-dir
    /// <see cref="UiStateStore"/> seam per lab-environment.md), shows it, and drives Connect →
    /// PickRoot → Workspace so the workspace-step-gated buttons and the rail toggle are realized.
    /// Returns the window + shell + settled workspace, plus the temp dir for cleanup.
    /// </summary>
    private static async Task<WindowContext> DriveToWorkspaceWindowAsync()
    {
        var stateDir = Directory.CreateTempSubdirectory("groupweaver-a11y-glyph-tests-").FullName;
        var fake = new FakeGraphRenderer { View = new Border() };

        var shell = new ShellViewModel(
            _ => new DemoProvider(),
            new StartupOptions(Demo: false),
            new WebView2RuntimeStatus(IsInstalled: true, Version: "test"),
            graphRendererFactory: () => fake,
            ruleset: null,
            locator: null,
            uiStateStore: new UiStateStore(stateDir));

        var window = new MainWindow { DataContext = shell, Width = 1280, Height = 720 };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var connect = Assert.IsType<ConnectionViewModel>(shell.CurrentStep);
        await connect.ConnectDemoCommand.ExecuteAsync(null);
        var picker = Assert.IsType<RootPickerViewModel>(shell.CurrentStep);
        await picker.LoadCandidates;
        picker.SelectedCandidate = picker.Candidates.First(c => Dn.Comparer.Equals(c.Dn, DemoRootDn));
        picker.LoadRootCommand.Execute(null);
        var workspace = Assert.IsType<WorkspaceViewModel>(shell.CurrentStep);
        await workspace.Initialization;
        Dispatcher.UIThread.RunJobs();

        return new WindowContext(window, shell, workspace, stateDir);
    }

    private static Button FindByName(Visual scope, string name) =>
        Assert.Single(
            scope.GetVisualDescendants().OfType<Button>(),
            b => b.Name == name);

    private sealed record WindowContext(
        MainWindow Window, ShellViewModel Shell, WorkspaceViewModel Workspace, string StateDir)
        : IAsyncDisposable
    {
        public ValueTask DisposeAsync()
        {
            Shell.Dispose();
            try
            {
                Directory.Delete(StateDir, recursive: true);
            }
            catch (IOException)
            {
                // Best-effort cleanup; never fail a test over temp-dir teardown.
            }
            catch (UnauthorizedAccessException)
            {
            }

            return ValueTask.CompletedTask;
        }
    }
}
