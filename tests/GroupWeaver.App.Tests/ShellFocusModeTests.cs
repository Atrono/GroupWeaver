using GroupWeaver.App.Settings;
using GroupWeaver.App.Startup;
using GroupWeaver.App.Tests.Fakes;
using GroupWeaver.App.ViewModels;
using GroupWeaver.Core.Model;
using GroupWeaver.Core.Providers;

using Xunit;

namespace GroupWeaver.App.Tests;

/// <summary>
/// Pins the ADR-022 D2 shell-level focus (presentation) mode on <see cref="ShellViewModel"/>:
/// <see cref="ShellViewModel.ToggleFocusModeCommand"/> flips <see cref="ShellViewModel.IsFocusMode"/>
/// AND propagates collapse/expand to the active workspace rail through the existing
/// <see cref="ShellViewModel.CurrentStep"/>-dispatch seam; <see cref="ShellViewModel.ExitFocusModeCommand"/>
/// leaves focus mode (re-expanding the rail) and is a no-op when already off; and a non-workspace
/// step (Connect) toggles focus without throwing (it simply loses the top strip — harmless). Plain
/// <see cref="FactAttribute"/>: the focus state is UI-free VM data. A temp-dir
/// <see cref="UiStateStore"/> is injected so the workspace its rail collapse persistence touches is
/// never real <c>%APPDATA%</c>.
/// </summary>
public sealed class ShellFocusModeTests
{
    private const string RootDn = "OU=Lab,DC=stub,DC=lab";

    // --- ToggleFocusModeCommand on a loaded workspace -----------------------------------

    [Fact]
    public async Task ToggleFocusMode_OnWorkspace_FlipsFocusAndCollapsesRail_BothWays()
    {
        using var dir = new TempDir();
        var shell = await DriveToWorkspaceAsync(dir.Path);
        var workspace = Assert.IsType<WorkspaceViewModel>(shell.CurrentStep);

        Assert.False(shell.IsFocusMode);
        Assert.False(workspace.IsRailCollapsed);

        // First toggle: focus on ⇒ the active workspace rail collapses (D2 propagation).
        shell.ToggleFocusModeCommand.Execute(null);
        Assert.True(shell.IsFocusMode);
        Assert.True(workspace.IsRailCollapsed);

        // Second toggle: focus off ⇒ the rail re-expands.
        shell.ToggleFocusModeCommand.Execute(null);
        Assert.False(shell.IsFocusMode);
        Assert.False(workspace.IsRailCollapsed);

        shell.Dispose();
    }

    [Fact]
    public async Task WorkspaceFocusButton_RoutesThroughTheInstalledCallback_ToShellToggle()
    {
        using var dir = new TempDir();
        var shell = await DriveToWorkspaceAsync(dir.Path);
        var workspace = Assert.IsType<WorkspaceViewModel>(shell.CurrentStep);

        // OnRootChosen armed the workspace "Focus" button via UseFocusToggleCallback, so the
        // workspace command drives the SAME shell-level focus toggle (no half-toggle).
        Assert.True(workspace.ToggleFocusCommand.CanExecute(null));
        workspace.ToggleFocusCommand.Execute(null);

        Assert.True(shell.IsFocusMode);
        Assert.True(workspace.IsRailCollapsed);

        shell.Dispose();
    }

    // --- ExitFocusModeCommand -----------------------------------------------------------

    [Fact]
    public async Task ExitFocusMode_FromFocus_LeavesFocusAndExpandsRail()
    {
        using var dir = new TempDir();
        var shell = await DriveToWorkspaceAsync(dir.Path);
        var workspace = Assert.IsType<WorkspaceViewModel>(shell.CurrentStep);

        shell.ToggleFocusModeCommand.Execute(null);
        Assert.True(shell.IsFocusMode);
        Assert.True(workspace.IsRailCollapsed);

        shell.ExitFocusModeCommand.Execute(null);

        Assert.False(shell.IsFocusMode);
        Assert.False(workspace.IsRailCollapsed);

        shell.Dispose();
    }

    [Fact]
    public async Task ExitFocusMode_WhenAlreadyOff_IsANoOp_LeavesRailAlone()
    {
        using var dir = new TempDir();
        var shell = await DriveToWorkspaceAsync(dir.Path);
        var workspace = Assert.IsType<WorkspaceViewModel>(shell.CurrentStep);

        // Not in focus mode; the rail is collapsed by a manual toggle (NOT by focus). Exit
        // must be a pure no-op — it must NOT re-expand a rail the user collapsed deliberately.
        Assert.False(shell.IsFocusMode);
        workspace.SetRailCollapsed(true);

        shell.ExitFocusModeCommand.Execute(null);

        Assert.False(shell.IsFocusMode);
        Assert.True(workspace.IsRailCollapsed); // untouched — the no-op left it alone
        shell.Dispose();
    }

    // --- non-workspace step (Connect): toggle flips focus, never throws -----------------

    [Fact]
    public void ToggleFocusMode_OnNonWorkspaceStep_FlipsFocus_AndDoesNotThrow()
    {
        // A fresh shell sits on the Connect step (no --demo, no advance).
        var shell = NewShell(new StubDirectoryProvider(
            Task.FromResult(new DirectoryConnection("stub directory", 0))));
        Assert.IsType<ConnectionViewModel>(shell.CurrentStep);

        // The CurrentStep-dispatch seam is `is WorkspaceViewModel` — a Connect step simply
        // loses the top strip; the toggle must still flip IsFocusMode and never throw.
        shell.ToggleFocusModeCommand.Execute(null);
        Assert.True(shell.IsFocusMode);

        shell.ToggleFocusModeCommand.Execute(null);
        Assert.False(shell.IsFocusMode);

        shell.Dispose();
    }

    // --- helpers ------------------------------------------------------------------------

    /// <summary>Builds a shell whose workspace rail state persists to <paramref name="baseDir"/>
    /// (never real <c>%APPDATA%</c>) with an explicit WebView2-present status (the ctor default
    /// probes the live registry — per-machine flakiness a VM test must not inherit).</summary>
    private static ShellViewModel NewShell(StubDirectoryProvider provider, string baseDir) =>
        new(
            _ => provider,
            new StartupOptions(Demo: false),
            new WebView2RuntimeStatus(IsInstalled: true, Version: "test"),
            graphRendererFactory: null,
            ruleset: null,
            locator: null,
            uiStateStore: new UiStateStore(baseDir));

    private static ShellViewModel NewShell(StubDirectoryProvider provider) =>
        new(
            _ => provider,
            new StartupOptions(Demo: false),
            new WebView2RuntimeStatus(IsInstalled: true, Version: "test"));

    /// <summary>Drives Connect → PickRoot → Workspace and settles the scope load, landing the
    /// shell on a loaded <see cref="WorkspaceViewModel"/> step (renderer-less — focus + rail are
    /// VM state only). The workspace rail persists to <paramref name="baseDir"/>.</summary>
    private static async Task<ShellViewModel> DriveToWorkspaceAsync(string baseDir)
    {
        var provider = new StubDirectoryProvider(
            Task.FromResult(new DirectoryConnection("stub directory", 0)))
        {
            RootCandidatesResult = Task.FromResult<IReadOnlyList<AdObject>>([
                new AdObject { Dn = RootDn, Kind = AdObjectKind.OrganizationalUnit, Name = "Lab" },
            ]),
            LoadScopeResult = Task.FromResult(new DirectorySnapshot()),
        };

        var shell = NewShell(provider, baseDir);

        var connect = Assert.IsType<ConnectionViewModel>(shell.CurrentStep);
        await connect.ConnectDemoCommand.ExecuteAsync(null);
        var picker = Assert.IsType<RootPickerViewModel>(shell.CurrentStep);
        await picker.LoadCandidates;

        picker.SelectedCandidate = picker.Candidates[0];
        picker.LoadRootCommand.Execute(null);

        var workspace = Assert.IsType<WorkspaceViewModel>(shell.CurrentStep);
        await workspace.Initialization;
        return shell;
    }

    private sealed class TempDir : IDisposable
    {
        public string Path { get; } =
            Directory.CreateTempSubdirectory("groupweaver-shell-focus-tests-").FullName;

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch (IOException)
            {
                // Best-effort cleanup; never fail a test over temp-dir teardown.
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}
