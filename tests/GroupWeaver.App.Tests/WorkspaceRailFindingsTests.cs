using GroupWeaver.App.Graph;
using GroupWeaver.App.Settings;
using GroupWeaver.App.Tests.Fakes;
using GroupWeaver.App.ViewModels;
using GroupWeaver.Core.Model;
using GroupWeaver.Core.Providers;

using Xunit;

namespace GroupWeaver.App.Tests;

/// <summary>
/// Pins the WP-B (#178) findings/detail rail split + conditional-Refresh-accent VM state on
/// <see cref="WorkspaceViewModel"/>: <see cref="WorkspaceViewModel.RailFindingsFraction"/> seeding
/// from the injected <see cref="UiStateStore"/>, its [0.2, 0.8] clamp + write-on-change persistence
/// (mirroring the ADR-022 D3/D4 rail-width story in <see cref="WorkspaceRailStateTests"/>), the
/// derived star <see cref="WorkspaceViewModel.FindingsRowHeight"/>/<see cref="WorkspaceViewModel.DetailRowHeight"/>
/// GridLengths, the <see cref="WorkspaceViewModel.SetRailFindingsFraction"/> drag-completed seam, and
/// the <see cref="WorkspaceViewModel.IsNodeSelected"/> signal that toggles the Refresh button's brand
/// accent. Uses the renderer-less workspace path (no graph renderer needed for pure VM state) with a
/// temp-dir <see cref="UiStateStore"/> so no test ever touches real <c>%APPDATA%</c> — the lab
/// hermetic-store rule. Plain <see cref="FactAttribute"/>: rail state is UI-free VM data.
/// </summary>
public sealed class WorkspaceRailFindingsTests
{
    private const string RootDn = "OU=Lab,DC=stub,DC=lab";
    private const string AdaDn = "CN=Ada Lovelace,OU=Lab,DC=stub,DC=lab";

    // === (2) ctor seeding + clamp + write-on-change persistence ============================

    [Fact]
    public async Task Ctor_SeedsRailFindingsFractionFromAPreviouslySavedStore()
    {
        using var dir = new TempDir();

        // A prior session persisted a non-default (and in-band) fraction. A fresh workspace
        // over the SAME store must start at exactly that value — the split survives a restart.
        new UiStateStore(dir.Path).Save(UiState.Default with { RailFindingsFraction = 0.65 });

        using var vm = await NewWorkspaceAsync(new UiStateStore(dir.Path));

        Assert.Equal(0.65, vm.RailFindingsFraction);
    }

    [Fact]
    public async Task Ctor_DefaultStore_SeedsTheOneToOneSplit()
    {
        using var dir = new TempDir();

        // Nothing persisted yet: the store Loads UiState.Default, so the VM seeds the 1:1 split.
        using var vm = await NewWorkspaceAsync(new UiStateStore(dir.Path));

        Assert.Equal(0.5, vm.RailFindingsFraction);
    }

    [Fact]
    public async Task Ctor_Seeding_DoesNotImmediatelyRewriteTheStore()
    {
        using var dir = new TempDir();

        // Seed via a raw JSON file that OMITS railFindingsFraction, so a seed-time write-back
        // (a missing _seeding guard) would be visible: it would rewrite the file WITH the field.
        // The store file must be byte-identical after a pure seed.
        var store = new UiStateStore(dir.Path);
        Directory.CreateDirectory(Path.GetDirectoryName(store.StatePath)!);
        File.WriteAllText(
            store.StatePath,
            """{ "RailWidth": 400, "RailCollapsed": true, "Theme": "Light" }""");
        var bytesBefore = File.ReadAllBytes(store.StatePath);

        using var vm = await NewWorkspaceAsync(new UiStateStore(dir.Path));

        // The ctor's _seeding gate means applying the loaded values must NOT write them straight
        // back; the on-disk bytes are untouched after construction (no railFindingsFraction added).
        Assert.Equal(bytesBefore, File.ReadAllBytes(store.StatePath));
        Assert.Equal(0.5, vm.RailFindingsFraction); // missing field => the 1:1-split default
    }

    [Fact]
    public async Task RailFindingsFraction_BelowMin_ClampsTo020()
    {
        using var dir = new TempDir();
        using var vm = await NewWorkspaceAsync(new UiStateStore(dir.Path));

        vm.RailFindingsFraction = 0.05;

        Assert.Equal(0.2, vm.RailFindingsFraction);
    }

    [Fact]
    public async Task RailFindingsFraction_AboveMax_ClampsTo080()
    {
        using var dir = new TempDir();
        using var vm = await NewWorkspaceAsync(new UiStateStore(dir.Path));

        vm.RailFindingsFraction = 0.95;

        Assert.Equal(0.8, vm.RailFindingsFraction);
    }

    [Fact]
    public async Task RailFindingsFraction_InBand_PassesThroughUnclamped()
    {
        using var dir = new TempDir();
        using var vm = await NewWorkspaceAsync(new UiStateStore(dir.Path));

        vm.RailFindingsFraction = 0.42;

        Assert.Equal(0.42, vm.RailFindingsFraction);
    }

    [Fact]
    public async Task SettingRailFindingsFraction_PersistsTheClampedValue_ToTheStore()
    {
        using var dir = new TempDir();
        using var vm = await NewWorkspaceAsync(new UiStateStore(dir.Path));

        // 0.95 clamps to 0.8 and the CLAMPED value is what persists (the re-entrant
        // clamp-then-persist path in OnRailFindingsFractionChanged).
        vm.RailFindingsFraction = 0.95;

        var persisted = new UiStateStore(dir.Path).Load();
        Assert.Equal(0.8, persisted.RailFindingsFraction);
    }

    [Fact]
    public async Task SettingRailFindingsFraction_PreservesTheOtherPersistedFields()
    {
        using var dir = new TempDir();

        // Seed non-default width/collapsed/theme, then change ONLY the fraction. The
        // read-modify-write in PersistUiState must leave the other three untouched.
        new UiStateStore(dir.Path).Save(new UiState(480, true) { Theme = "Light" });

        using var vm = await NewWorkspaceAsync(new UiStateStore(dir.Path));
        vm.RailFindingsFraction = 0.7;

        var persisted = new UiStateStore(dir.Path).Load();
        Assert.Equal(0.7, persisted.RailFindingsFraction);
        Assert.Equal(480, persisted.RailWidth);
        Assert.True(persisted.RailCollapsed);
        Assert.Equal("Light", persisted.Theme);
    }

    // === (3) derived star GridLengths track the fraction ==================================

    [Fact]
    public async Task FindingsAndDetailRowHeights_AreStar_AndSumToOne_TrackingTheFraction()
    {
        using var dir = new TempDir();
        using var vm = await NewWorkspaceAsync(new UiStateStore(dir.Path));

        vm.RailFindingsFraction = 0.6;

        Assert.True(vm.FindingsRowHeight.IsStar);
        Assert.True(vm.DetailRowHeight.IsStar);
        Assert.Equal(0.6, vm.FindingsRowHeight.Value);
        Assert.Equal(0.4, vm.DetailRowHeight.Value, precision: 10); // 1 - fraction
        Assert.Equal(1.0, vm.FindingsRowHeight.Value + vm.DetailRowHeight.Value, precision: 10);
    }

    [Fact]
    public async Task RowHeights_RecomputeWhenTheFractionChanges()
    {
        using var dir = new TempDir();
        using var vm = await NewWorkspaceAsync(new UiStateStore(dir.Path));

        // Default 1:1 split.
        Assert.Equal(0.5, vm.FindingsRowHeight.Value);
        Assert.Equal(0.5, vm.DetailRowHeight.Value);

        vm.RailFindingsFraction = 0.25;

        Assert.Equal(0.25, vm.FindingsRowHeight.Value);
        Assert.Equal(0.75, vm.DetailRowHeight.Value, precision: 10);
    }

    [Fact]
    public async Task RowHeightChanges_AreObservable_ForTheOneWayXamlBinding()
    {
        using var dir = new TempDir();
        using var vm = await NewWorkspaceAsync(new UiStateStore(dir.Path));

        var changed = new List<string?>();
        vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        vm.RailFindingsFraction = 0.3;

        // Both derived star lengths must re-notify so the bound RowDefinitions update.
        Assert.Contains(nameof(WorkspaceViewModel.FindingsRowHeight), changed);
        Assert.Contains(nameof(WorkspaceViewModel.DetailRowHeight), changed);
    }

    // === (4) SetRailFindingsFraction (the row-GridSplitter drag-completed seam) ============

    [Fact]
    public async Task SetRailFindingsFraction_AHeightPair_RoutesThroughTheClampingSetter()
    {
        using var dir = new TempDir();
        using var vm = await NewWorkspaceAsync(new UiStateStore(dir.Path));

        // findings 300 px, detail 100 px => 300 / 400 = 0.75 (in-band, passes through).
        vm.SetRailFindingsFraction(300, 100);

        Assert.Equal(0.75, vm.RailFindingsFraction);

        // And it persisted (the seam routes through the same write-on-change setter).
        Assert.Equal(0.75, new UiStateStore(dir.Path).Load().RailFindingsFraction);
    }

    [Fact]
    public async Task SetRailFindingsFraction_AnOutOfBandPair_IsClampedByTheSetter()
    {
        using var dir = new TempDir();
        using var vm = await NewWorkspaceAsync(new UiStateStore(dir.Path));

        // findings 380 px, detail 20 px => 0.95, which the [0.2, 0.8] clamp pulls to 0.8.
        vm.SetRailFindingsFraction(380, 20);

        Assert.Equal(0.8, vm.RailFindingsFraction);
    }

    [Theory]
    [InlineData(0, 0)] // not-yet-measured rail: zero total
    [InlineData(double.NaN, 100)] // a NaN read
    [InlineData(-50, -50)] // a negative total
    [InlineData(double.PositiveInfinity, 1)] // a non-finite total
    public async Task SetRailFindingsFraction_ADegenerateTotal_IsANoOp(
        double findingsHeight, double detailHeight)
    {
        using var dir = new TempDir();
        using var vm = await NewWorkspaceAsync(new UiStateStore(dir.Path));

        // Move off the default first so a no-op is distinguishable from "happened to land on 0.5".
        vm.RailFindingsFraction = 0.6;

        vm.SetRailFindingsFraction(findingsHeight, detailHeight);

        // Guarded: a non-finite or <= 0 total must never write (no NaN to the store).
        Assert.Equal(0.6, vm.RailFindingsFraction);
        Assert.True(double.IsFinite(vm.RailFindingsFraction));
    }

    // === (5) IsNodeSelected drives the conditional Refresh brand accent ===================

    [Fact]
    public async Task IsNodeSelected_IsFalse_WhenNoNodeIsSelected()
    {
        using var dir = new TempDir();
        using var vm = await NewSelectableWorkspaceAsync(new UiStateStore(dir.Path));

        Assert.Null(vm.DetailPanel);
        Assert.False(vm.IsNodeSelected);
    }

    [Fact]
    public async Task IsNodeSelected_IsTrue_WhenANodeIsSelected()
    {
        using var dir = new TempDir();
        using var vm = await NewSelectableWorkspaceAsync(new UiStateStore(dir.Path));

        // The AP 2.5 public-setter seam: selecting an in-snapshot DN projects the detail panel.
        vm.SelectedDn = AdaDn;

        Assert.NotNull(vm.DetailPanel);
        Assert.True(vm.IsNodeSelected);
    }

    [Fact]
    public async Task IsNodeSelected_RaisesPropertyChanged_WhenDetailPanelChanges()
    {
        using var dir = new TempDir();
        using var vm = await NewSelectableWorkspaceAsync(new UiStateStore(dir.Path));

        var changed = new List<string?>();
        vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        // Select => DetailPanel becomes non-null => IsNodeSelected must re-notify so the
        // XAML Classes.accent="{Binding IsNodeSelected}" toggles the Refresh button accent.
        vm.SelectedDn = AdaDn;
        Assert.Contains(nameof(WorkspaceViewModel.IsNodeSelected), changed);
        Assert.True(vm.IsNodeSelected);

        changed.Clear();

        // Deselect => DetailPanel becomes null => IsNodeSelected must re-notify back to false.
        vm.SelectedDn = null;
        Assert.Contains(nameof(WorkspaceViewModel.IsNodeSelected), changed);
        Assert.False(vm.IsNodeSelected);
    }

    // --- helpers ------------------------------------------------------------------------

    /// <summary>A renderer-less workspace rooted at <see cref="RootDn"/> seeded from
    /// <paramref name="store"/>, scope-loaded with an EMPTY snapshot (no renderer needed for pure
    /// rail-fraction state). Returned ready for assertions.</summary>
    private static async Task<WorkspaceViewModel> NewWorkspaceAsync(UiStateStore store) =>
        await NewWorkspaceAsync(store, new DirectorySnapshot());

    /// <summary>A renderer-less workspace whose scope load yields a snapshot containing one
    /// selectable in-snapshot user (<see cref="AdaDn"/>) — so the public <c>SelectedDn</c> seam
    /// projects a non-null <c>DetailPanel</c> and <c>IsNodeSelected</c> flips true.</summary>
    private static Task<WorkspaceViewModel> NewSelectableWorkspaceAsync(UiStateStore store)
    {
        var snapshot = new DirectorySnapshot();
        snapshot.AddObject(new AdObject
        {
            Dn = RootDn,
            Kind = AdObjectKind.OrganizationalUnit,
            Name = "Lab",
        });
        snapshot.AddObject(new AdObject
        {
            Dn = AdaDn,
            Kind = AdObjectKind.User,
            Name = "Ada Lovelace",
            SamAccountName = "ada.lovelace",
        });
        return NewWorkspaceAsync(store, snapshot);
    }

    private static async Task<WorkspaceViewModel> NewWorkspaceAsync(
        UiStateStore store, DirectorySnapshot snapshot)
    {
        var provider = new StubDirectoryProvider(
            Task.FromResult(new DirectoryConnection("stub directory", 0)))
        {
            LoadScopeResult = Task.FromResult(snapshot),
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
            Directory.CreateTempSubdirectory("groupweaver-workspace-railfindings-tests-").FullName;

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
