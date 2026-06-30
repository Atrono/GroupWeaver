using GroupWeaver.App.Graph;
using GroupWeaver.App.Settings;
using GroupWeaver.App.Tests.Fakes;
using GroupWeaver.App.ViewModels;
using GroupWeaver.Core.Model;
using GroupWeaver.Core.Providers;

using Xunit;

namespace GroupWeaver.App.Tests;

/// <summary>
/// Pins the ADR-022 adaptive-rail VM state on <see cref="WorkspaceViewModel"/> (D3/D4): the
/// <see cref="WorkspaceViewModel.RailWidth"/> clamp [300, 520], write-on-change persistence to
/// the injected <see cref="UiStateStore"/>, the ctor seeding from a previously-saved
/// <see cref="UiState"/>, and <see cref="WorkspaceViewModel.SetRailCollapsed"/> (the
/// focus-mode driver). Uses the renderer-less workspace path the other workspace tests use —
/// no graph renderer is needed for pure VM rail state — with a temp-dir
/// <see cref="UiStateStore"/> so no test ever touches real <c>%APPDATA%</c>. Plain
/// <see cref="FactAttribute"/>: the rail state is UI-free VM data.
/// </summary>
public sealed class WorkspaceRailStateTests
{
    private const string RootDn = "OU=Lab,DC=stub,DC=lab";

    // --- the clamp (ADR-022 D3): [300, 520] ---------------------------------------------

    [Fact]
    public async Task RailWidth_AboveMax_ClampsTo520()
    {
        using var dir = new TempDir();
        using var vm = await NewWorkspaceAsync(new UiStateStore(dir.Path));

        vm.RailWidth = 999;

        Assert.Equal(520, vm.RailWidth);
    }

    [Fact]
    public async Task RailWidth_BelowMin_ClampsTo300()
    {
        using var dir = new TempDir();
        using var vm = await NewWorkspaceAsync(new UiStateStore(dir.Path));

        vm.RailWidth = 10;

        Assert.Equal(300, vm.RailWidth);
    }

    // --- persistence (ADR-022 D4): write-on-change --------------------------------------

    [Fact]
    public async Task SettingRailWidth_PersistsTheClampedValue_ToTheStore()
    {
        using var dir = new TempDir();
        using var vm = await NewWorkspaceAsync(new UiStateStore(dir.Path));

        // 999 clamps to 520 and the CLAMPED value is what persists (the re-entrant
        // clamp-then-persist path in OnRailWidthChanged).
        vm.RailWidth = 999;

        var persisted = new UiStateStore(dir.Path).Load();
        Assert.Equal(520, persisted.RailWidth);
    }

    [Fact]
    public async Task SettingIsRailCollapsed_Persists_ToTheStore()
    {
        using var dir = new TempDir();
        using var vm = await NewWorkspaceAsync(new UiStateStore(dir.Path));

        vm.IsRailCollapsed = true;

        Assert.True(new UiStateStore(dir.Path).Load().RailCollapsed);
    }

    // --- ctor seeding (ADR-022 D4): a previously-saved state is the starting state -------

    [Fact]
    public async Task Ctor_SeedsRailStateFromAPreviouslySavedStore()
    {
        using var dir = new TempDir();

        // A prior session saved (400, collapsed). A fresh workspace over the SAME store
        // must start in exactly that state — the persisted preferences survive a restart.
        new UiStateStore(dir.Path).Save(new UiState(400, true));

        using var vm = await NewWorkspaceAsync(new UiStateStore(dir.Path));

        Assert.Equal(400, vm.RailWidth);
        Assert.True(vm.IsRailCollapsed);
    }

    [Fact]
    public async Task Ctor_Seeding_DoesNotImmediatelyRewriteTheStore()
    {
        using var dir = new TempDir();
        var store = new UiStateStore(dir.Path);
        store.Save(new UiState(400, true));
        var bytesBeforeCtor = File.ReadAllBytes(store.StatePath);

        using var vm = await NewWorkspaceAsync(new UiStateStore(dir.Path));

        // The ctor's _seeding gate means applying the loaded values must NOT write them straight back as a
        // change; the persisted state is untouched after construction. Assert the FILE BYTES are unchanged
        // — the truest "untouched store" check, and (unlike the original whole-record Assert.Equal) it does
        // NOT depend on UiState record equality, which the "persist view state" change broke for the new
        // init list fields: they default to string[] but deserialize to List<string>, and the record's
        // equality is reference-based over IReadOnlyList<string>, so two equal-but-empty lists never compare
        // equal (audit-summary.md: compare PROJECTIONS, never whole-record identity).
        Assert.Equal(bytesBeforeCtor, File.ReadAllBytes(store.StatePath));
        var persisted = new UiStateStore(dir.Path).Load();
        Assert.Equal((400.0, true), (persisted.RailWidth, persisted.RailCollapsed));
        Assert.Equal(400, vm.RailWidth);
    }

    // --- SetRailCollapsed (ADR-022 D2 focus-mode driver) --------------------------------

    [Fact]
    public async Task SetRailCollapsed_FlipsIsRailCollapsed_BothWays()
    {
        using var dir = new TempDir();
        using var vm = await NewWorkspaceAsync(new UiStateStore(dir.Path));
        Assert.False(vm.IsRailCollapsed); // default-seeded: expanded

        vm.SetRailCollapsed(true);
        Assert.True(vm.IsRailCollapsed);

        vm.SetRailCollapsed(false);
        Assert.False(vm.IsRailCollapsed);
    }

    // --- helpers ------------------------------------------------------------------------

    /// <summary>A renderer-less workspace rooted at <see cref="RootDn"/> seeded from
    /// <paramref name="store"/>, with its ctor scope-load settled (an empty snapshot — no
    /// renderer, so VM rail state is the whole concern). Returned ready for assertions.</summary>
    private static async Task<WorkspaceViewModel> NewWorkspaceAsync(UiStateStore store)
    {
        var provider = new StubDirectoryProvider(
            Task.FromResult(new DirectoryConnection("stub directory", 0)))
        {
            LoadScopeResult = Task.FromResult(new DirectorySnapshot()),
        };

        var vm = new WorkspaceViewModel(
            provider,
            new AdObject { Dn = RootDn, Kind = AdObjectKind.OrganizationalUnit, Name = "Lab" },
            new DirectoryConnection("stub directory", 0),
            webView2Missing: false,
            graphRendererFactory: null,
            ruleset: null,
            exportDialogs: null,
            uiStateStore: store);

        await vm.Initialization;
        return vm;
    }

    private sealed class TempDir : IDisposable
    {
        public string Path { get; } =
            Directory.CreateTempSubdirectory("groupweaver-workspace-rail-tests-").FullName;

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
