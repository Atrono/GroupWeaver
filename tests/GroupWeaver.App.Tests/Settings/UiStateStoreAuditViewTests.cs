using GroupWeaver.App.Settings;

using Xunit;

namespace GroupWeaver.App.Tests.Settings;

/// <summary>
/// Pins the "persist view state" audit filter/sort persistence story on <see cref="UiStateStore"/>:
/// the five NEW NON-positional <c>init</c> fields the Audit screen owns —
/// <see cref="UiState.AuditSeverityFilter"/>, <see cref="UiState.AuditStatusFilter"/>,
/// <see cref="UiState.AuditRuleClassFilter"/> (each a name list, <c>[]</c> = no constraint),
/// <see cref="UiState.AuditSortColumn"/> (<c>"None"</c> = canonical order) and
/// <see cref="UiState.AuditSortDescending"/>. Mirrors the ADR-026 <c>Theme</c> /
/// WP-B <c>RailFindingsFraction</c> precedents (<see cref="UiStateStoreTests"/> /
/// <see cref="UiStateStoreRailFindingsTests"/>): the fields round-trip through
/// <see cref="UiStateStore.Save"/>/<see cref="UiStateStore.Load"/>, an old <c>ui-state.json</c>
/// lacking them defaults to "no filter / canonical order" under the never-throw Load (forward/back
/// compat), and the <c>with { }</c> read-modify-write neither clobbers nor is clobbered by the
/// rail/theme fields (no sibling-field collision). Plain unit tests, temp-dir base (the
/// <see cref="Rules.RulesetLocator"/> seam) — no real <c>%APPDATA%</c>, no AD, no UI.
/// </summary>
public sealed class UiStateStoreAuditViewTests
{
    // --- defaults: the new fields seed "no filter / canonical order" ---------------------

    [Fact]
    public void Default_HasEmptyAuditFilters_NoneSort_Ascending()
    {
        // The seed when nothing is persisted yet: every audit filter axis is empty (no
        // constraint), the sort column is "None" (the canonical report order), ascending —
        // and the pre-existing fields keep their prior defaults (byte-shape unchanged).
        Assert.Empty(UiState.Default.AuditSeverityFilter);
        Assert.Empty(UiState.Default.AuditStatusFilter);
        Assert.Empty(UiState.Default.AuditRuleClassFilter);
        Assert.Equal("None", UiState.Default.AuditSortColumn);
        Assert.False(UiState.Default.AuditSortDescending);

        // The existing defaults are undisturbed by the five new fields.
        Assert.Equal(340, UiState.Default.RailWidth);
        Assert.False(UiState.Default.RailCollapsed);
        Assert.Equal("Dark", UiState.Default.Theme);
        Assert.Equal(0.5, UiState.Default.RailFindingsFraction);
    }

    // --- round-trip: Save then Load preserves the audit filter/sort values ---------------

    [Fact]
    public void Save_ThenLoad_RoundTripsTheAuditFiltersAndSort()
    {
        using var dir = new TempDir();
        var store = new UiStateStore(dir.Path);

        // NON-default values on every one of the five fields (non-empty filter lists, a real
        // sort column, descending) so a Load that silently returned the default would be caught.
        var saved = UiState.Default with
        {
            AuditSeverityFilter = new[] { "Error", "Warning" },
            AuditStatusFilter = new[] { "Acknowledged" },
            AuditRuleClassFilter = new[] { "empty-group" },
            AuditSortColumn = "Severity",
            AuditSortDescending = true,
        };
        store.Save(saved);

        // A FRESH store over the same base reads the persisted state from disk (not a cached
        // in-memory copy) — the production Shell -> Audit re-seed path.
        var loaded = new UiStateStore(dir.Path).Load();

        Assert.Equal(new[] { "Error", "Warning" }, loaded.AuditSeverityFilter);
        Assert.Equal(new[] { "Acknowledged" }, loaded.AuditStatusFilter);
        Assert.Equal(new[] { "empty-group" }, loaded.AuditRuleClassFilter);
        Assert.Equal("Severity", loaded.AuditSortColumn);
        Assert.True(loaded.AuditSortDescending);
    }

    // --- forward/back compat: an old ui-state.json without the 5 fields => defaults ------

    [Fact]
    public void Load_OldJsonWithoutTheAuditFields_DefaultsToNoFilterCanonicalOrder()
    {
        using var dir = new TempDir();
        var store = new UiStateStore(dir.Path);

        // A pre-"persist view state" ui-state.json: only the rail/theme/fraction fields, none
        // of the five audit-view properties (the Theme / RailFindingsFraction back-compat
        // precedents). The never-throw Load must deserialize it (valid JSON) and every missing
        // audit field must fall back to its "no filter / canonical order" default.
        Directory.CreateDirectory(Path.GetDirectoryName(store.StatePath)!);
        File.WriteAllText(
            store.StatePath,
            """{ "RailWidth": 400, "RailCollapsed": true, "Theme": "Light", "RailFindingsFraction": 0.3 }""");

        var loaded = store.Load();

        // The persisted (old) fields read back …
        Assert.Equal(400, loaded.RailWidth);
        Assert.True(loaded.RailCollapsed);
        Assert.Equal("Light", loaded.Theme);
        Assert.Equal(0.3, loaded.RailFindingsFraction);

        // … and the five missing audit fields default to no-filter / canonical order.
        Assert.Empty(loaded.AuditSeverityFilter);
        Assert.Empty(loaded.AuditStatusFilter);
        Assert.Empty(loaded.AuditRuleClassFilter);
        Assert.Equal("None", loaded.AuditSortColumn);
        Assert.False(loaded.AuditSortDescending);
    }

    // --- never-throw degradation is unaffected by the new fields -------------------------

    [Fact]
    public void Load_CorruptFile_StillReturnsDefault_NeverThrows()
    {
        using var dir = new TempDir();
        var store = new UiStateStore(dir.Path);

        // Malformed JSON at the exact path Load reads: the never-throw degradation arm must
        // still return the Default record (now carrying the five audit defaults) — adding the
        // new fields must not have introduced a throwing parse path.
        Directory.CreateDirectory(Path.GetDirectoryName(store.StatePath)!);
        File.WriteAllText(store.StatePath, "{ not json");

        var loaded = store.Load();

        Assert.Equal(UiState.Default, loaded);
        Assert.Empty(loaded.AuditSeverityFilter);
        Assert.Equal("None", loaded.AuditSortColumn);
    }

    [Fact]
    public void Load_MissingFile_StillReturnsDefault_NeverThrows()
    {
        using var dir = new TempDir();
        var store = new UiStateStore(dir.Path);

        // Nothing was ever saved — the file does not exist. Load returns Default with the five
        // audit fields at their seeds.
        Assert.False(File.Exists(store.StatePath));

        var loaded = store.Load();

        Assert.Equal(UiState.Default, loaded);
    }

    // --- the with { } read-modify-write never clobbers a sibling field -------------------

    [Fact]
    public void WithRailWidthCollapsedThemeFraction_DoesNotClobberTheAuditFields()
    {
        using var dir = new TempDir();
        var store = new UiStateStore(dir.Path);

        // A prior session persisted non-default audit filters + sort.
        store.Save(UiState.Default with
        {
            AuditSeverityFilter = new[] { "Error" },
            AuditStatusFilter = new[] { "Suppressed" },
            AuditRuleClassFilter = new[] { "nesting" },
            AuditSortColumn = "ObjectName",
            AuditSortDescending = true,
        });

        // A rail/theme/fraction writer does the read-modify-write the rail's PersistUiState
        // (and the theme writer) use: load, change ONLY the rail/theme/fraction fields, save.
        // The five audit fields must ride through untouched.
        var current = new UiStateStore(dir.Path).Load();
        store.Save(current with { RailWidth = 480, RailCollapsed = true, Theme = "Light", RailFindingsFraction = 0.7 });

        var loaded = new UiStateStore(dir.Path).Load();

        // The rail/theme/fraction write landed …
        Assert.Equal(480, loaded.RailWidth);
        Assert.True(loaded.RailCollapsed);
        Assert.Equal("Light", loaded.Theme);
        Assert.Equal(0.7, loaded.RailFindingsFraction);

        // … and every audit field survived the sibling-field write.
        Assert.Equal(new[] { "Error" }, loaded.AuditSeverityFilter);
        Assert.Equal(new[] { "Suppressed" }, loaded.AuditStatusFilter);
        Assert.Equal(new[] { "nesting" }, loaded.AuditRuleClassFilter);
        Assert.Equal("ObjectName", loaded.AuditSortColumn);
        Assert.True(loaded.AuditSortDescending);
    }

    [Fact]
    public void WithTheAuditFields_DoesNotClobberRailWidthCollapsedThemeFraction()
    {
        using var dir = new TempDir();
        var store = new UiStateStore(dir.Path);

        // A prior session persisted non-default rail width / collapsed / theme / fraction.
        store.Save(new UiState(420, true) { Theme = "Light", RailFindingsFraction = 0.65 });

        // The Audit VM's PersistView does the mirror read-modify-write: load, change ONLY the
        // five audit fields, save. The four rail/theme/fraction fields must ride through.
        var current = new UiStateStore(dir.Path).Load();
        store.Save(current with
        {
            AuditSeverityFilter = new[] { "Warning" },
            AuditSortColumn = "RuleClass",
            AuditSortDescending = true,
        });

        var loaded = new UiStateStore(dir.Path).Load();

        // The audit write landed …
        Assert.Equal(new[] { "Warning" }, loaded.AuditSeverityFilter);
        Assert.Equal("RuleClass", loaded.AuditSortColumn);
        Assert.True(loaded.AuditSortDescending);

        // … and the rail/theme/fraction fields survived.
        Assert.Equal(420, loaded.RailWidth);
        Assert.True(loaded.RailCollapsed);
        Assert.Equal("Light", loaded.Theme);
        Assert.Equal(0.65, loaded.RailFindingsFraction);
    }

    // --- helpers ------------------------------------------------------------------------

    private sealed class TempDir : IDisposable
    {
        public string Path { get; } =
            Directory.CreateTempSubdirectory("groupweaver-uistate-auditview-tests-").FullName;

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
