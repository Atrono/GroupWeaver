using System.ComponentModel;

using Avalonia.Styling;

using GroupWeaver.App.Graph;
using GroupWeaver.App.Settings;
using GroupWeaver.App.Startup;
using GroupWeaver.App.Tests.Fakes;
using GroupWeaver.App.ViewModels;
using GroupWeaver.Core.Model;
using GroupWeaver.Core.Providers;

using Xunit;

namespace GroupWeaver.App.Tests;

/// <summary>
/// Pins the ADR-026 D4 (extended for System/auto) app-chrome theme on <see cref="ShellViewModel"/>:
/// the three-state <see cref="ShellViewModel.ThemeChoice"/> enum (<see cref="AppThemeChoice.Dark"/> /
/// <see cref="AppThemeChoice.Light"/> / <see cref="AppThemeChoice.System"/>) replacing the original
/// binary <c>IsLightTheme</c> bool. Covers: the dark-first default (no ui-state file ⇒ Dark, ☾ glyph),
/// <see cref="ShellViewModel.ToggleThemeCommand"/> cycling Dark→Light→System→Dark with the right
/// <see cref="ShellViewModel.ThemeGlyph"/> (☾/☀/◐) + <see cref="ShellViewModel.ThemeTooltip"/> per
/// state and change-notifying both, the ENUM NAME persisting + round-tripping through the shared store
/// ("Dark"/"Light"/"System"), legacy <c>"Light"</c> and unknown/missing names parsing correctly, the
/// theme write never clobbering the rail/audit fields in the one shared <see cref="UiStateStore"/>, and
/// the System/auto state: resolution through the injected <see cref="IPlatformThemeProvider"/>, the
/// live OS-change re-apply gated on System, the subscribe-only-while-System + unsubscribe-on-Dispose
/// contract, and the never-throw fallback.
///
/// <para>CRITICAL test-isolation seam (lab-environment / the #124 lesson, ADR-026 D4): the
/// <see cref="ShellViewModel"/> ctor READS and <see cref="ShellViewModel.ToggleThemeCommand"/> WRITES
/// <c>ui-state.json</c>, so EVERY test here injects a
/// <see cref="Directory.CreateTempSubdirectory(string)"/>-backed <see cref="UiStateStore"/> — nothing
/// ever touches real <c>%APPDATA%</c>. Assertions are over the per-VM
/// <see cref="ShellViewModel.ThemeChoice"/>/<see cref="ShellViewModel.ThemeGlyph"/>/
/// <see cref="ShellViewModel.ThemeTooltip"/>, the persisted <see cref="UiState.Theme"/> name, the
/// injected <see cref="FakePlatformThemeProvider"/>'s subscriber count, and the
/// <see cref="FakeGraphRenderer.SetThemeCalls"/> push channel — NEVER
/// <c>Application.Current.RequestedThemeVariant</c> (shared global app state, flaky under parallel
/// headless theories). Plain <see cref="FactAttribute"/>: the theme state is UI-free VM data.</para>
/// </summary>
public sealed class ShellThemeTests
{
    private const string MoonGlyph = "☾"; // ☾ — Dark (tap to go Light)
    private const string SunGlyph = "☀"; // ☀ — Light (tap to go System)
    private const string HalfGlyph = "◐"; // ◐ — System (tap to go Dark)

    private const string DarkTooltip = "Theme: Dark — tap for Light";
    private const string LightTooltip = "Theme: Light — tap for System";
    private const string SystemTooltip = "Theme: System — tap for Dark";

    // --- default: dark when no ui-state file exists -------------------------------------

    [Fact]
    public void Default_NoUiStateFile_IsDarkChoice_MoonGlyph_DarkTooltip()
    {
        using var dir = new TempDir();

        // A fresh injected store over an empty temp dir: Load returns UiState.Default (Theme "Dark"),
        // so the dark-first default holds — ThemeChoice Dark, the moon glyph + Dark tooltip show.
        using var shell = NewConnectShell(dir.Path);

        Assert.Equal(AppThemeChoice.Dark, shell.ThemeChoice);
        Assert.Equal(MoonGlyph, shell.ThemeGlyph);
        Assert.Equal(DarkTooltip, shell.ThemeTooltip);
    }

    // --- ToggleThemeCommand cycles Dark->Light->System->Dark, change-notifying glyph+tooltip ---

    [Fact]
    public void ToggleThemeCommand_CyclesThreeStates_AndChangeNotifiesGlyphAndTooltip()
    {
        using var dir = new TempDir();
        using var shell = NewConnectShell(dir.Path);

        // Record the explicit-name notifications for ThemeGlyph + ThemeTooltip — the live proof that
        // the OnThemeChoiceChanged partial hook re-raises BOTH (the top-strip button binding would
        // silently stick to the previous glyph/tooltip otherwise).
        var glyphNotified = 0;
        var tooltipNotified = 0;
        void OnShellChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(ShellViewModel.ThemeGlyph))
            {
                glyphNotified++;
            }
            else if (e.PropertyName == nameof(ShellViewModel.ThemeTooltip))
            {
                tooltipNotified++;
            }
        }

        shell.PropertyChanged += OnShellChanged;

        // Hop 1: Dark ⇒ Light (sun glyph, "tap for System" tooltip).
        shell.ToggleThemeCommand.Execute(null);
        Assert.Equal(AppThemeChoice.Light, shell.ThemeChoice);
        Assert.Equal(SunGlyph, shell.ThemeGlyph);
        Assert.Equal(LightTooltip, shell.ThemeTooltip);

        // Hop 2: Light ⇒ System (half-circle glyph, "tap for Dark" tooltip).
        shell.ToggleThemeCommand.Execute(null);
        Assert.Equal(AppThemeChoice.System, shell.ThemeChoice);
        Assert.Equal(HalfGlyph, shell.ThemeGlyph);
        Assert.Equal(SystemTooltip, shell.ThemeTooltip);

        // Hop 3: System ⇒ Dark (back to the moon glyph + Dark tooltip — the cycle closes).
        shell.ToggleThemeCommand.Execute(null);
        Assert.Equal(AppThemeChoice.Dark, shell.ThemeChoice);
        Assert.Equal(MoonGlyph, shell.ThemeGlyph);
        Assert.Equal(DarkTooltip, shell.ThemeTooltip);

        Assert.Equal(3, glyphNotified);
        Assert.Equal(3, tooltipNotified);

        shell.PropertyChanged -= OnShellChanged;
    }

    // --- toggle persists the enum NAME to the injected store (round-trips all three) -----

    [Fact]
    public void ToggleTheme_PersistsEnumName_ToTheStore_ThroughTheWholeCycle()
    {
        using var dir = new TempDir();
        using var shell = NewConnectShell(dir.Path);

        // Each hop persists the enum NAME (a FRESH store over the same base reads it from disk, not a
        // cached copy — the production restart path).
        shell.ToggleThemeCommand.Execute(null);
        Assert.Equal("Light", new UiStateStore(dir.Path).Load().Theme);

        shell.ToggleThemeCommand.Execute(null);
        Assert.Equal("System", new UiStateStore(dir.Path).Load().Theme);

        shell.ToggleThemeCommand.Execute(null);
        Assert.Equal("Dark", new UiStateStore(dir.Path).Load().Theme);
    }

    // --- ctor seeding: a previously-persisted name round-trips from disk -----------------

    [Theory]
    [InlineData("Dark", AppThemeChoice.Dark, MoonGlyph)]
    [InlineData("Light", AppThemeChoice.Light, SunGlyph)]
    [InlineData("System", AppThemeChoice.System, HalfGlyph)]
    public void Ctor_SeedsThemeChoice_FromPersistedName(string persisted, AppThemeChoice expected, string glyph)
    {
        using var dir = new TempDir();

        // A prior session saved this enum name (read-modify-write preserving the rail fields). A fresh
        // shell over the SAME store must start in that choice — the persisted theme survives a restart.
        new UiStateStore(dir.Path).Save(UiState.Default with { Theme = persisted });

        using var shell = NewConnectShell(dir.Path);

        Assert.Equal(expected, shell.ThemeChoice);
        Assert.Equal(glyph, shell.ThemeGlyph);
    }

    // --- ctor seeding: legacy / unknown / missing names fall back to Dark (never throws) -

    [Theory]
    [InlineData("light")] // legacy lower-case (Enum.TryParse ignoreCase) ⇒ Light, proven separately below
    [InlineData("LIGHT")] // legacy upper-case
    public void Ctor_ParsesLegacyLightCaseInsensitively(string persisted)
    {
        using var dir = new TempDir();
        new UiStateStore(dir.Path).Save(UiState.Default with { Theme = persisted });

        using var shell = NewConnectShell(dir.Path);

        // The original "Light" (any case) still round-trips to the Light choice (case-insensitive parse).
        Assert.Equal(AppThemeChoice.Light, shell.ThemeChoice);
    }

    [Theory]
    [InlineData("")] // empty
    [InlineData("Twilight")] // unknown enum name
    [InlineData("Dark Mode")] // a name with a space — not a member
    public void Ctor_UnknownOrMissingName_FallsBackToDark(string persisted)
    {
        using var dir = new TempDir();
        new UiStateStore(dir.Path).Save(UiState.Default with { Theme = persisted });

        using var shell = NewConnectShell(dir.Path);

        // The ADR-026 D4 never-throw load contract: an unparseable / unknown / empty persisted name
        // falls back to dark-first — the ctor never throws. (A bare numeric string like "3" is NOT
        // covered here: Enum.TryParse accepts the underlying value even out of the defined range, so
        // it round-trips as (AppThemeChoice)3 rather than falling back — the production ParseThemeChoice
        // does not Enum.IsDefined-validate, by design, since only named values are ever persisted.)
        Assert.Equal(AppThemeChoice.Dark, shell.ThemeChoice);
        Assert.Equal(MoonGlyph, shell.ThemeGlyph);
    }

    // --- theme <-> rail/audit: the shared store's read-modify-write never clobbers -------

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

    [Fact]
    public void ThemeWrite_PreservesAuditFields_InTheSharedStore()
    {
        using var dir = new TempDir();

        // A prior session persisted audit view-state alongside the rail fields (the WP "persist view
        // state" fields the Audit screen owns). The theme toggle's read-modify-write must keep them.
        var seeded = UiState.Default with
        {
            Theme = "Dark",
            AuditSeverityFilter = ["Error"],
            AuditStatusFilter = ["Open"],
            AuditRuleClassFilter = ["nesting"],
            AuditSortColumn = "Severity",
            AuditSortDescending = true,
        };
        new UiStateStore(dir.Path).Save(seeded);

        using var shell = NewConnectShell(dir.Path);
        shell.ToggleThemeCommand.Execute(null); // Dark ⇒ Light, writes Theme through the shared store

        var persisted = new UiStateStore(dir.Path).Load();
        Assert.Equal("Light", persisted.Theme); // the theme actually changed
        // Every audit field survived the theme-side read-modify-write (the ADR-028 / WP boundary).
        Assert.Equal(["Error"], persisted.AuditSeverityFilter);
        Assert.Equal(["Open"], persisted.AuditStatusFilter);
        Assert.Equal(["nesting"], persisted.AuditRuleClassFilter);
        Assert.Equal("Severity", persisted.AuditSortColumn);
        Assert.True(persisted.AuditSortDescending);
    }

    // === System/auto state coverage (the ADR-026 extension) =============================

    // --- resolution: System resolves to the injected provider's OS variant --------------

    [Theory]
    [InlineData(/* osIsLight */ true)]
    [InlineData(/* osIsLight */ false)]
    public async Task SystemChoice_ResolvesToProviderOsVariant_OnTheCanvasPush(bool osIsLight)
    {
        using var dir = new TempDir();
        var platform = new FakePlatformThemeProvider
        {
            OsPreference = osIsLight ? ThemeVariant.Light : ThemeVariant.Dark,
        };

        // Drive to a renderer-bearing workspace so ToggleTheme's ApplyCanvasTheme pushes the RESOLVED
        // light/dark variant to the FakeGraphRenderer — the deterministic observation seam for the
        // System-resolved variant (never the shared global RequestedThemeVariant).
        var renderer = new FakeGraphRenderer();
        var shell = await DriveToWorkspaceAsync(dir.Path, () => renderer, platform);

        // Cycle Dark ⇒ Light ⇒ System. The final hop into System pushes the OS-resolved variant.
        shell.ToggleThemeCommand.Execute(null); // ⇒ Light  (pushes true)
        shell.ToggleThemeCommand.Execute(null); // ⇒ System (pushes the resolved OS variant)

        Assert.Equal(AppThemeChoice.System, shell.ThemeChoice);
        Assert.NotEmpty(renderer.SetThemeCalls);
        Assert.Equal(osIsLight, renderer.SetThemeCalls[^1]); // System resolved to the fake's OS variant

        shell.Dispose();
    }

    // --- live OS switch re-applies ONLY while System ------------------------------------

    [Fact]
    public async Task OsPreferenceChanged_ReAppliesCanvas_OnlyWhileSystem()
    {
        using var dir = new TempDir();
        var platform = new FakePlatformThemeProvider { OsPreference = ThemeVariant.Dark };
        var renderer = new FakeGraphRenderer();
        var shell = await DriveToWorkspaceAsync(dir.Path, () => renderer, platform);

        // While Dark: a simulated OS switch must NOT reach the shell (it is not subscribed) — no push.
        renderer.SetThemeCalls.Clear();
        platform.RaiseOsPreferenceChanged();
        Assert.Empty(renderer.SetThemeCalls);

        // Cycle to System (Dark ⇒ Light ⇒ System), then clear the toggle-driven pushes.
        shell.ToggleThemeCommand.Execute(null);
        shell.ToggleThemeCommand.Execute(null);
        Assert.Equal(AppThemeChoice.System, shell.ThemeChoice);
        renderer.SetThemeCalls.Clear();

        // The OS flips to Light: while System, the shell re-applies the freshly-resolved variant.
        platform.OsPreference = ThemeVariant.Light;
        platform.RaiseOsPreferenceChanged();
        Assert.Single(renderer.SetThemeCalls);
        Assert.True(renderer.SetThemeCalls[^1]); // re-resolved to the new OS Light variant

        // A second OS flip back to Dark while still System: re-applies again with the new variant.
        platform.OsPreference = ThemeVariant.Dark;
        platform.RaiseOsPreferenceChanged();
        Assert.Equal(2, renderer.SetThemeCalls.Count);
        Assert.False(renderer.SetThemeCalls[^1]);

        // Leave System (System ⇒ Dark) and clear: a later OS switch must again be ignored.
        shell.ToggleThemeCommand.Execute(null);
        Assert.Equal(AppThemeChoice.Dark, shell.ThemeChoice);
        renderer.SetThemeCalls.Clear();
        platform.OsPreference = ThemeVariant.Light;
        platform.RaiseOsPreferenceChanged();
        Assert.Empty(renderer.SetThemeCalls); // unsubscribed on leaving System — no re-apply

        shell.Dispose();
    }

    // --- subscribe-only-while-System, and unsubscribe on Dispose ------------------------

    [Fact]
    public void OsPreferenceSubscription_TracksSystemState_AndTearsDownOnDispose()
    {
        using var dir = new TempDir();
        var platform = new FakePlatformThemeProvider();
        var shell = NewConnectShell(dir.Path, platform);

        // Seeded Dark: the shell is NOT subscribed to the OS-preference event.
        Assert.Equal(0, platform.SubscriberCount);

        shell.ToggleThemeCommand.Execute(null); // ⇒ Light  — still not subscribed
        Assert.Equal(0, platform.SubscriberCount);

        shell.ToggleThemeCommand.Execute(null); // ⇒ System — subscribes EXACTLY once
        Assert.Equal(1, platform.SubscriberCount);

        shell.ToggleThemeCommand.Execute(null); // ⇒ Dark   — unsubscribes
        Assert.Equal(0, platform.SubscriberCount);

        // Re-enter System (Dark ⇒ Light ⇒ System) so Dispose has a live subscription to tear down.
        shell.ToggleThemeCommand.Execute(null); // ⇒ Light
        shell.ToggleThemeCommand.Execute(null); // ⇒ System
        Assert.Equal(1, platform.SubscriberCount);

        shell.Dispose();
        Assert.Equal(0, platform.SubscriberCount); // Dispose tears the subscription down
    }

    [Fact]
    public async Task AfterDispose_OsPreferenceChanged_NeverReachesTheShell()
    {
        using var dir = new TempDir();
        var platform = new FakePlatformThemeProvider { OsPreference = ThemeVariant.Dark };
        var renderer = new FakeGraphRenderer();
        var shell = await DriveToWorkspaceAsync(dir.Path, () => renderer, platform);

        // Enter System so the shell is subscribed, then dispose it.
        shell.ToggleThemeCommand.Execute(null); // ⇒ Light
        shell.ToggleThemeCommand.Execute(null); // ⇒ System
        Assert.Equal(1, platform.SubscriberCount);

        shell.Dispose();
        Assert.Equal(0, platform.SubscriberCount);

        // A post-Dispose OS switch must NOT invoke any shell callback — no canvas push.
        renderer.SetThemeCalls.Clear();
        platform.OsPreference = ThemeVariant.Light;
        platform.RaiseOsPreferenceChanged();
        Assert.Empty(renderer.SetThemeCalls);
    }

    // --- never-throw fallback: a provider whose read fell back to Dark resolves Dark ----

    /// <summary>The ADR-026 D4 degradation discipline (the "returns Dark ⇒ Dark" arm of the
    /// never-throw fallback): the <see cref="IPlatformThemeProvider"/> contract guarantees a CONCRETE
    /// variant — when the OS preference cannot be read the production
    /// <see cref="DefaultPlatformThemeProvider"/> catches internally and returns Dark (dark-first).
    /// A System choice over a provider that resolves Dark must therefore apply Dark, and nothing in
    /// the ctor (seeded System, resolves + applies on startup) or the toggle path throws.</summary>
    [Fact]
    public async Task SystemChoice_ProviderResolvesDark_AppliesDark_NeverThrows()
    {
        using var dir = new TempDir();
        // The fake's default OsPreference is Dark — exactly the value the production provider yields
        // from its own never-throw fallback. Seed "System" so the ctor resolves + applies on startup.
        var platform = new FakePlatformThemeProvider { OsPreference = ThemeVariant.Dark };
        new UiStateStore(dir.Path).Save(UiState.Default with { Theme = "System" });

        var renderer = new FakeGraphRenderer();

        // Constructing the shell seeded-System over a Dark-resolving provider must NOT throw.
        var ex = await Record.ExceptionAsync(() => DriveToWorkspaceAsync(dir.Path, () => renderer, platform));
        Assert.Null(ex);

        var shell = await DriveToWorkspaceAsync(dir.Path, () => renderer, platform);
        Assert.Equal(AppThemeChoice.System, shell.ThemeChoice);

        // Forcing a canvas re-tone (cycle away and back into System) exercises the resolve path again;
        // it must resolve to dark-first (false) and never surface an error.
        renderer.SetThemeCalls.Clear();
        shell.ToggleThemeCommand.Execute(null); // System ⇒ Dark
        shell.ToggleThemeCommand.Execute(null); // Dark ⇒ Light
        shell.ToggleThemeCommand.Execute(null); // Light ⇒ System (resolves via the Dark-resolving provider)

        Assert.Equal(AppThemeChoice.System, shell.ThemeChoice);
        Assert.NotEmpty(renderer.SetThemeCalls);
        Assert.False(renderer.SetThemeCalls[^1]); // System over a Dark-resolving provider applies Dark

        shell.Dispose();
    }

    /// <summary>The "throws ⇒ Dark" arm is the PRODUCTION provider's own never-throw contract (the
    /// shell applies the result without a guard — see <see cref="IPlatformThemeProvider"/>): the real
    /// <see cref="DefaultPlatformThemeProvider"/> swallows any platform read failure and returns a
    /// concrete dark-first variant. Headless (no <c>Application.Current.PlatformSettings</c>) it must
    /// resolve to <see cref="ThemeVariant.Dark"/> and never throw.</summary>
    [Fact]
    public void DefaultPlatformThemeProvider_NeverThrows_ResolvesConcreteDarkHeadless()
    {
        var provider = new DefaultPlatformThemeProvider();

        ThemeVariant variant = ThemeVariant.Light;
        var ex = Record.Exception(() => variant = provider.GetOsPreference());

        Assert.Null(ex); // the provider's own try/catch never bubbles a read failure
        Assert.Equal(ThemeVariant.Dark, variant); // concrete dark-first fallback, never Default
    }

    // --- helpers ------------------------------------------------------------------------

    private const string RootDn = "OU=Lab,DC=stub,DC=lab";

    /// <summary>A fresh shell on the Connect step (no --demo, no advance) whose theme/rail state
    /// persists to <paramref name="baseDir"/> (never real <c>%APPDATA%</c>), with an explicit
    /// WebView2-present status (the ctor default probes the live registry — per-machine flakiness
    /// a VM test must not inherit). An optional <paramref name="platform"/> injects the OS-preference
    /// seam (defaulted to the production impl, exactly like the store).</summary>
    private static ShellViewModel NewConnectShell(string baseDir, IPlatformThemeProvider? platform = null) =>
        new(
            _ => new StubDirectoryProvider(Task.FromResult(new DirectoryConnection("stub directory", 0))),
            new StartupOptions(Demo: false),
            new WebView2RuntimeStatus(IsInstalled: true, Version: "test"),
            graphRendererFactory: null,
            ruleset: null,
            locator: null,
            uiStateStore: new UiStateStore(baseDir),
            platformThemeProvider: platform);

    /// <summary>Drives Connect → PickRoot → Workspace and settles the scope load, landing the
    /// shell on a loaded <see cref="WorkspaceViewModel"/> step whose rail state persists to
    /// <paramref name="baseDir"/> — the SAME store base the shell's theme uses. An optional
    /// <paramref name="rendererFactory"/> builds the workspace's graph renderer (so the canvas-theme
    /// push is observable); an optional <paramref name="platform"/> injects the OS-preference seam.</summary>
    private static async Task<ShellViewModel> DriveToWorkspaceAsync(
        string baseDir,
        Func<IGraphRenderer>? rendererFactory = null,
        IPlatformThemeProvider? platform = null)
    {
        var provider = new StubDirectoryProvider(
            Task.FromResult(new DirectoryConnection("stub directory", 0)))
        {
            RootCandidatesResult = Task.FromResult<IReadOnlyList<AdObject>>([
                new AdObject { Dn = RootDn, Kind = AdObjectKind.OrganizationalUnit, Name = "Lab" },
            ]),
            LoadScopeResult = Task.FromResult(new DirectorySnapshot()),
        };

        var shell = new ShellViewModel(
            _ => provider,
            new StartupOptions(Demo: false),
            new WebView2RuntimeStatus(IsInstalled: true, Version: "test"),
            graphRendererFactory: rendererFactory,
            ruleset: null,
            locator: null,
            uiStateStore: new UiStateStore(baseDir),
            platformThemeProvider: platform);

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
