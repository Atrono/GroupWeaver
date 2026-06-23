using System.ComponentModel;

using GroupWeaver.App.Settings;
using GroupWeaver.App.Startup;
using GroupWeaver.App.Tests.Fakes;
using GroupWeaver.App.ViewModels;
using GroupWeaver.Core.Model;
using GroupWeaver.Core.Providers;

using Xunit;

namespace GroupWeaver.App.Tests;

/// <summary>
/// Pins the ADR-026 D4 WP1a app-chrome light/dark theme on <see cref="ShellViewModel"/>: the
/// dark-first default (<see cref="ShellViewModel.IsLightTheme"/> false / glyph moon when no
/// ui-state file exists), <see cref="ShellViewModel.ToggleThemeCommand"/> flipping
/// <see cref="ShellViewModel.IsLightTheme"/> + change-notifying <see cref="ShellViewModel.ThemeGlyph"/>,
/// the toggle persisting <see cref="UiState.Theme"/> through the shared store, the ctor seeding
/// <see cref="ShellViewModel.IsLightTheme"/> from a previously-persisted Light, and the
/// theme/rail read-modify-write never clobbering each other in the one shared
/// <see cref="UiStateStore"/>.
///
/// <para>CRITICAL test-isolation seam (lab-environment / the #124 lesson, ADR-026 D4): the
/// <see cref="ShellViewModel"/> ctor now READS and <see cref="ShellViewModel.ToggleThemeCommand"/>
/// WRITES <c>ui-state.json</c>, so EVERY test here injects a
/// <see cref="Directory.CreateTempSubdirectory(string)"/>-backed <see cref="UiStateStore"/> —
/// nothing ever touches real <c>%APPDATA%</c>. Assertions are over the per-VM
/// <see cref="ShellViewModel.IsLightTheme"/>/<see cref="ShellViewModel.ThemeGlyph"/> and the
/// persisted <see cref="UiState.Theme"/> — NEVER <c>Application.Current.RequestedThemeVariant</c>
/// (shared global app state, flaky under parallel headless theories per the implementer note).
/// Plain <see cref="FactAttribute"/>: the theme state is UI-free VM data.</para>
/// </summary>
public sealed class ShellThemeTests
{
    private const string MoonGlyph = "☾"; // ☾ — dark mode (tap to go light)
    private const string SunGlyph = "☀"; // ☀ — light mode (tap to go dark)

    // --- default: dark when no ui-state file exists -------------------------------------

    [Fact]
    public void Default_NoUiStateFile_IsDarkTheme_MoonGlyph()
    {
        using var dir = new TempDir();

        // A fresh injected store over an empty temp dir: Load returns UiState.Default (Theme
        // "Dark"), so the dark-first default holds — IsLightTheme false, the moon glyph shows.
        using var shell = NewConnectShell(dir.Path);

        Assert.False(shell.IsLightTheme);
        Assert.Equal(MoonGlyph, shell.ThemeGlyph);
    }

    // --- ToggleThemeCommand flips state + change-notifies the glyph ---------------------

    [Fact]
    public void ToggleThemeCommand_FlipsTheme_BothWays_AndChangeNotifiesGlyph()
    {
        using var dir = new TempDir();
        using var shell = NewConnectShell(dir.Path);

        // Record the explicit-name notification for ThemeGlyph — the live proof that the
        // OnIsLightThemeChanged partial hook re-raises it (the top-strip button binding would
        // silently stick to the moon otherwise).
        var glyphNotified = false;
        void OnShellChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(ShellViewModel.ThemeGlyph))
            {
                glyphNotified = true;
            }
        }

        shell.PropertyChanged += OnShellChanged;

        // First toggle: dark ⇒ light (sun glyph).
        shell.ToggleThemeCommand.Execute(null);
        Assert.True(shell.IsLightTheme);
        Assert.Equal(SunGlyph, shell.ThemeGlyph);
        Assert.True(
            glyphNotified,
            "toggling the theme must raise PropertyChanged for ThemeGlyph (proves "
            + "OnIsLightThemeChanged fires — the theme button's glyph binding stays live)");

        // Second toggle: light ⇒ dark (back to the moon glyph).
        shell.ToggleThemeCommand.Execute(null);
        Assert.False(shell.IsLightTheme);
        Assert.Equal(MoonGlyph, shell.ThemeGlyph);

        shell.PropertyChanged -= OnShellChanged;
    }

    // --- toggle persists UiState.Theme to the injected store ----------------------------

    [Fact]
    public void ToggleTheme_PersistsTheme_ToTheStore_BothWays()
    {
        using var dir = new TempDir();
        using var shell = NewConnectShell(dir.Path);

        // One toggle ⇒ Light is persisted (a FRESH store over the same base reads it from disk,
        // not a cached copy — the production restart path).
        shell.ToggleThemeCommand.Execute(null);
        Assert.Equal("Light", new UiStateStore(dir.Path).Load().Theme);

        // A second toggle ⇒ back to the persisted Dark.
        shell.ToggleThemeCommand.Execute(null);
        Assert.Equal("Dark", new UiStateStore(dir.Path).Load().Theme);
    }

    // --- ctor seeding: a previously-persisted Light starts light ------------------------

    [Fact]
    public void Ctor_SeedsIsLightTheme_FromPersistedLight()
    {
        using var dir = new TempDir();

        // A prior session saved Light (read-modify-write preserving the rail fields). A fresh
        // shell over the SAME store must start light — the persisted theme survives a restart.
        new UiStateStore(dir.Path).Save(UiState.Default with { Theme = "Light" });

        using var shell = NewConnectShell(dir.Path);

        Assert.True(shell.IsLightTheme);
        Assert.Equal(SunGlyph, shell.ThemeGlyph);
    }

    // --- theme ⇄ rail: the shared store's read-modify-write never clobbers --------------

    [Fact]
    public async Task ThemeAndRail_ShareOneStore_NeitherClobbersTheOther()
    {
        using var dir = new TempDir();

        // The shell + its workspace share ONE store base (the production composition root threads
        // the single UiStateStore down Shell→RootPicker→Workspace). Drive to a loaded workspace.
        var shell = await DriveToWorkspaceAsync(dir.Path);
        var workspace = Assert.IsType<WorkspaceViewModel>(shell.CurrentStep);

        // Workspace persists a rail change (write-on-change via OnIsRailCollapsedChanged →
        // PersistUiState, which read-modify-writes preserving Theme).
        workspace.RailWidth = 400;
        workspace.IsRailCollapsed = true;

        // Shell then toggles the theme (read-modify-write preserving RailWidth/RailCollapsed).
        shell.ToggleThemeCommand.Execute(null);

        // Reload from disk: BOTH the rail fields and the theme field survived — neither side's
        // read-modify-write reset the other back to its default.
        var persisted = new UiStateStore(dir.Path).Load();
        Assert.Equal(400, persisted.RailWidth);
        Assert.True(persisted.RailCollapsed);
        Assert.Equal("Light", persisted.Theme);

        // And the reverse direction: a further rail change must not clobber the now-Light theme.
        workspace.IsRailCollapsed = false;
        var afterRail = new UiStateStore(dir.Path).Load();
        Assert.Equal("Light", afterRail.Theme); // theme preserved by the rail-side read-modify-write
        Assert.False(afterRail.RailCollapsed);
        Assert.Equal(400, afterRail.RailWidth);

        shell.Dispose();
    }

    // --- helpers ------------------------------------------------------------------------

    private const string RootDn = "OU=Lab,DC=stub,DC=lab";

    /// <summary>A fresh shell on the Connect step (no --demo, no advance) whose theme/rail state
    /// persists to <paramref name="baseDir"/> (never real <c>%APPDATA%</c>), with an explicit
    /// WebView2-present status (the ctor default probes the live registry — per-machine flakiness
    /// a VM test must not inherit).</summary>
    private static ShellViewModel NewConnectShell(string baseDir) =>
        new(
            _ => new StubDirectoryProvider(Task.FromResult(new DirectoryConnection("stub directory", 0))),
            new StartupOptions(Demo: false),
            new WebView2RuntimeStatus(IsInstalled: true, Version: "test"),
            graphRendererFactory: null,
            ruleset: null,
            locator: null,
            uiStateStore: new UiStateStore(baseDir));

    /// <summary>Drives Connect → PickRoot → Workspace and settles the scope load, landing the
    /// shell on a loaded renderer-less <see cref="WorkspaceViewModel"/> step whose rail state
    /// persists to <paramref name="baseDir"/> — the SAME store base the shell's theme uses.</summary>
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

        var shell = NewConnectShellWithProvider(provider, baseDir);

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

    private static ShellViewModel NewConnectShellWithProvider(StubDirectoryProvider provider, string baseDir) =>
        new(
            _ => provider,
            new StartupOptions(Demo: false),
            new WebView2RuntimeStatus(IsInstalled: true, Version: "test"),
            graphRendererFactory: null,
            ruleset: null,
            locator: null,
            uiStateStore: new UiStateStore(baseDir));

    private sealed class TempDir : IDisposable
    {
        public string Path { get; } =
            Directory.CreateTempSubdirectory("groupweaver-shell-theme-tests-").FullName;

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
