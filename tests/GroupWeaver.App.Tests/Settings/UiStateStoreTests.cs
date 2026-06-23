using GroupWeaver.App.Settings;

using Xunit;

namespace GroupWeaver.App.Tests.Settings;

/// <summary>
/// Pins <see cref="UiStateStore"/> (ADR-022 D4): the adaptive-rail UI preferences
/// (<c>RailWidth</c> + <c>RailCollapsed</c>) persist to
/// <c>&lt;base&gt;\GroupWeaver\ui-state.json</c> — the production base dir is
/// <c>%APPDATA%</c>; these tests inject a temp base dir via the ctor overload (the
/// <see cref="Rules.RulesetLocator"/> seam). <see cref="UiStateStore.Save"/> round-trips
/// through <see cref="UiStateStore.Load"/>; <see cref="UiStateStore.Load"/> is NEVER-THROW
/// (missing / corrupt ⇒ <see cref="UiState.Default"/>); and the atomic temp-file+move save
/// leaves no leftover temp file. App-preference state only — no untrusted input, no AD; plain
/// unit tests, no headless UI.
/// </summary>
public sealed class UiStateStoreTests
{
    // --- StatePath: the canonical layout under the injected base dir --------------------

    [Fact]
    public void StatePath_IsGroupWeaverUiStateJson_UnderTheInjectedBaseDir()
    {
        using var dir = new TempDir();

        var store = new UiStateStore(dir.Path);

        Assert.Equal(
            Path.Combine(dir.Path, "GroupWeaver", "ui-state.json"),
            store.StatePath);
    }

    // --- round-trip: Save then Load returns the same values -----------------------------

    [Fact]
    public void Save_ThenLoad_ReturnsTheSameValues()
    {
        using var dir = new TempDir();
        var store = new UiStateStore(dir.Path);

        // Non-default values on BOTH scalars (width != 340, collapsed != false) so a
        // Load that silently returned the default would be caught.
        store.Save(new UiState(420, true));

        // A FRESH store over the same base reads the persisted state from disk (not a
        // cached in-memory copy) — the production Shell→Workspace re-seed path.
        var loaded = new UiStateStore(dir.Path).Load();

        Assert.Equal(420, loaded.RailWidth);
        Assert.True(loaded.RailCollapsed);
        Assert.Equal(new UiState(420, true), loaded);
    }

    // --- never-throw: missing file ⇒ Default --------------------------------------------

    [Fact]
    public void Load_MissingFile_ReturnsDefault_NeverThrows()
    {
        using var dir = new TempDir();
        var store = new UiStateStore(dir.Path);

        // Nothing was ever saved — the file (and its GroupWeaver dir) does not exist.
        Assert.False(File.Exists(store.StatePath));

        var loaded = store.Load();

        Assert.Equal(UiState.Default, loaded);
        Assert.Equal(340, loaded.RailWidth); // the ADR-022 D3 default, pinned as a literal
        Assert.False(loaded.RailCollapsed);
    }

    // --- never-throw: corrupt file ⇒ Default --------------------------------------------

    [Fact]
    public void Load_CorruptFile_ReturnsDefault_NeverThrows()
    {
        using var dir = new TempDir();
        var store = new UiStateStore(dir.Path);

        // Malformed JSON at the exact path Load reads — the degradation arm
        // (RulesetLocator.LoadEffective contract): never throws, falls back to default.
        Directory.CreateDirectory(Path.GetDirectoryName(store.StatePath)!);
        File.WriteAllText(store.StatePath, "{ not json");

        var loaded = store.Load();

        Assert.Equal(UiState.Default, loaded);
    }

    // --- ADR-026 D4: Theme round-trips and is back/forward compatible -------------------

    [Fact]
    public void Save_ThenLoad_RoundTripsTheTheme()
    {
        using var dir = new TempDir();
        var store = new UiStateStore(dir.Path);

        // Theme is a non-positional init property — prove it actually serializes and reads back
        // (a default-only round-trip would pass even if Save dropped the field).
        store.Save(UiState.Default with { Theme = "Light" });

        var loaded = new UiStateStore(dir.Path).Load();

        Assert.Equal("Light", loaded.Theme);
        Assert.Equal(UiState.Default with { Theme = "Light" }, loaded);
    }

    [Fact]
    public void Load_OldJsonWithoutThemeField_DefaultsToDark()
    {
        using var dir = new TempDir();
        var store = new UiStateStore(dir.Path);

        // A pre-ADR-026 ui-state.json: only the two rail fields, no `theme` property. The
        // never-throw Load must deserialize it (it is valid JSON) and the missing Theme must
        // fall back to the dark-first default — the JSON stays back-compatible.
        Directory.CreateDirectory(Path.GetDirectoryName(store.StatePath)!);
        File.WriteAllText(store.StatePath, """{ "RailWidth": 400, "RailCollapsed": true }""");

        var loaded = store.Load();

        Assert.Equal(400, loaded.RailWidth);
        Assert.True(loaded.RailCollapsed);
        Assert.Equal("Dark", loaded.Theme); // missing ⇒ the ADR-026 dark-first default
    }

    // --- atomic-ish save: no leftover temp file, the final file exists ------------------

    [Fact]
    public void Save_IsAtomic_LeavesNoLeftoverTempFile_AndWritesTheFinalFile()
    {
        using var dir = new TempDir();
        var store = new UiStateStore(dir.Path);

        store.Save(new UiState(360, false));

        // The final file exists at the canonical path …
        Assert.True(File.Exists(store.StatePath), "the UI-state file must be written");

        // … and the temp-file+move left NO scratch file behind in the target dir
        // (the .groupweaver-tmp scratch name is deleted on success by the move).
        var groupWeaverDir = Path.GetDirectoryName(store.StatePath)!;
        Assert.Empty(Directory.GetFiles(groupWeaverDir, "*.groupweaver-tmp"));

        // The only file in the dir is the state file itself — re-Load confirms the bytes.
        var file = Assert.Single(Directory.GetFiles(groupWeaverDir));
        Assert.Equal(store.StatePath, file);
        Assert.Equal(new UiState(360, false), store.Load());
    }

    // --- helpers ------------------------------------------------------------------------

    private sealed class TempDir : IDisposable
    {
        public string Path { get; } =
            Directory.CreateTempSubdirectory("groupweaver-uistate-tests-").FullName;

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
