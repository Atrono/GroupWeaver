using GroupWeaver.App.Settings;

using Xunit;

namespace GroupWeaver.App.Tests.Settings;

/// <summary>
/// Pins the WP-B (#178) <see cref="UiState.RailFindingsFraction"/> persistence story on
/// <see cref="UiStateStore"/> — the findings/detail rail split — mirroring the ADR-026
/// <c>Theme</c> precedent in <see cref="UiStateStoreTests"/>: a NON-positional <c>init</c>
/// property that round-trips through <see cref="UiStateStore.Save"/>/<see cref="UiStateStore.Load"/>,
/// defaults to <c>0.5</c> when an old <c>ui-state.json</c> omits the field (forward/back compat
/// under the never-throw Load), and survives the <c>with { }</c> read-modify-write that the
/// rail-width/collapsed/theme writers use (no field clobbers another). Plain unit tests, temp-dir
/// base (the <see cref="Rules.RulesetLocator"/> seam) — no real <c>%APPDATA%</c>, no AD, no UI.
/// </summary>
public sealed class UiStateStoreRailFindingsTests
{
    // --- round-trip: Save then Load preserves RailFindingsFraction ----------------------

    [Fact]
    public void Save_ThenLoad_RoundTripsTheRailFindingsFraction()
    {
        using var dir = new TempDir();
        var store = new UiStateStore(dir.Path);

        // A NON-default fraction (!= 0.5) so a Load that silently returned the default
        // would be caught — prove the field actually serializes and reads back.
        store.Save(UiState.Default with { RailFindingsFraction = 0.7 });

        // A FRESH store over the same base reads from disk (not a cached copy) — the
        // production Shell->Workspace re-seed path.
        var loaded = new UiStateStore(dir.Path).Load();

        Assert.Equal(0.7, loaded.RailFindingsFraction);
        // Scalar projection, not the whole record: the "persist view state" change added init list fields
        // that default to string[] but deserialize to List<string>, and UiState's record equality is
        // reference-based over IReadOnlyList<string>, so a whole-record Assert.Equal on otherwise-equal
        // values is never true. This matches the projection-comparison discipline (audit-summary.md). The
        // round-trip's intent — the RailFindingsFraction field plus the other scalars staying at default —
        // is preserved.
        Assert.Equal((340.0, false, "Dark", 0.7), (loaded.RailWidth, loaded.RailCollapsed, loaded.Theme, loaded.RailFindingsFraction));
        Assert.Empty(loaded.AuditSeverityFilter);
        Assert.Equal("None", loaded.AuditSortColumn);
    }

    // --- forward/back compat: an old ui-state.json without the field defaults to 0.5 ----

    [Fact]
    public void Load_OldJsonWithoutRailFindingsFractionField_DefaultsToHalf()
    {
        using var dir = new TempDir();
        var store = new UiStateStore(dir.Path);

        // A pre-WP-B ui-state.json: the two rail fields + theme, no `railFindingsFraction`
        // property (the Theme back-compat precedent). The never-throw Load must deserialize
        // it (valid JSON) and the missing fraction must fall back to the 1:1-split default.
        Directory.CreateDirectory(Path.GetDirectoryName(store.StatePath)!);
        File.WriteAllText(
            store.StatePath,
            """{ "RailWidth": 400, "RailCollapsed": true, "Theme": "Light" }""");

        var loaded = store.Load();

        Assert.Equal(400, loaded.RailWidth);
        Assert.True(loaded.RailCollapsed);
        Assert.Equal("Light", loaded.Theme);
        Assert.Equal(0.5, loaded.RailFindingsFraction); // missing => the WP-B 1:1-split default
    }

    [Fact]
    public void Default_HasTheOneToOneSplitFraction()
    {
        // The seed when nothing is persisted yet is the 1:1 findings/detail split, and
        // UiState.Default is byte-shape-unchanged otherwise (new(340, false)).
        Assert.Equal(0.5, UiState.Default.RailFindingsFraction);
        Assert.Equal(340, UiState.Default.RailWidth);
        Assert.False(UiState.Default.RailCollapsed);
        Assert.Equal("Dark", UiState.Default.Theme);
    }

    // --- the with { } read-modify-write never clobbers a sibling field ------------------

    [Fact]
    public void WithRailWidthCollapsedTheme_DoesNotClobberTheRailFindingsFraction()
    {
        using var dir = new TempDir();
        var store = new UiStateStore(dir.Path);

        // A prior session persisted a non-default fraction.
        store.Save(UiState.Default with { RailFindingsFraction = 0.3 });

        // A rail-width / collapsed / theme writer does the read-modify-write the VM's
        // PersistUiState (and ShellViewModel's theme writer) use: load, change the OTHER
        // fields, save. The fraction must ride through untouched.
        var current = new UiStateStore(dir.Path).Load();
        store.Save(current with { RailWidth = 480, RailCollapsed = true, Theme = "Light" });

        var loaded = new UiStateStore(dir.Path).Load();

        Assert.Equal(480, loaded.RailWidth);
        Assert.True(loaded.RailCollapsed);
        Assert.Equal("Light", loaded.Theme);
        Assert.Equal(0.3, loaded.RailFindingsFraction); // survived the sibling-field write
    }

    [Fact]
    public void WithRailFindingsFraction_DoesNotClobberRailWidthCollapsedTheme()
    {
        using var dir = new TempDir();
        var store = new UiStateStore(dir.Path);

        // A prior session persisted non-default rail width / collapsed / theme.
        store.Save(new UiState(420, true) { Theme = "Light" });

        // The VM's RailFindingsFraction writer does the mirror read-modify-write: load,
        // change ONLY the fraction, save. The other three fields must ride through untouched.
        var current = new UiStateStore(dir.Path).Load();
        store.Save(current with { RailFindingsFraction = 0.65 });

        var loaded = new UiStateStore(dir.Path).Load();

        Assert.Equal(0.65, loaded.RailFindingsFraction);
        Assert.Equal(420, loaded.RailWidth); // survived
        Assert.True(loaded.RailCollapsed); // survived
        Assert.Equal("Light", loaded.Theme); // survived
    }

    // --- helpers ------------------------------------------------------------------------

    private sealed class TempDir : IDisposable
    {
        public string Path { get; } =
            Directory.CreateTempSubdirectory("groupweaver-uistate-railfindings-tests-").FullName;

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
