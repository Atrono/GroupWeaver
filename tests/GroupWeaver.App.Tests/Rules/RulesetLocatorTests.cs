using System.Text.RegularExpressions;

using GroupWeaver.App.Rules;
using GroupWeaver.Core.Rules;

using Xunit;

namespace GroupWeaver.App.Tests.Rules;

/// <summary>
/// Pins <see cref="RulesetLocator"/> (ADR-008 slice 8): the user ruleset lives
/// at <c>&lt;base&gt;\GroupWeaver\ruleset.jsonc</c> (the production base dir is
/// <c>%APPDATA%</c>; tests inject a temp base dir via the ctor overload), and
/// <c>LoadEffective</c> applies WHOLE-FILE precedence — no merging, ever:
/// missing file → (embedded default, FromUserFile=false, no errors); valid
/// user file → (user ruleset, true, no errors); invalid user file → (default,
/// false, the loader's errors surfaced verbatim). The locator never
/// materializes the user file (that is AP 3.3's first save) and never throws
/// on bad user input — plain unit tests, no headless UI.
/// </summary>
public sealed class RulesetLocatorTests
{
    // --- UserRulesetPath ---------------------------------------------------------

    [Fact]
    public void UserRulesetPath_IsGroupWeaverRulesetJsonc_UnderTheInjectedBaseDir()
    {
        using var dir = new TempDir();

        var locator = new RulesetLocator(dir.Path);

        Assert.Equal(
            Path.Combine(dir.Path, "GroupWeaver", "ruleset.jsonc"),
            locator.UserRulesetPath);
    }

    // --- precedence: missing user file → embedded default --------------------------

    [Fact]
    public void LoadEffective_NoGroupWeaverDirAtAll_ReturnsTheEmbeddedDefault()
    {
        using var dir = new TempDir();
        var locator = new RulesetLocator(dir.Path);

        var effective = locator.LoadEffective();

        Assert.False(effective.FromUserFile);
        Assert.Empty(effective.Errors);
        AssertIsTheEmbeddedDefault(effective.Ruleset);
    }

    [Fact]
    public void LoadEffective_GroupWeaverDirExistsButFileIsAbsent_ReturnsTheEmbeddedDefault()
    {
        using var dir = new TempDir();
        var locator = new RulesetLocator(dir.Path);
        Directory.CreateDirectory(Path.GetDirectoryName(locator.UserRulesetPath)!);

        var effective = locator.LoadEffective();

        Assert.False(effective.FromUserFile);
        Assert.Empty(effective.Errors);
        AssertIsTheEmbeddedDefault(effective.Ruleset);
    }

    [Fact]
    public void LoadEffective_MissingUserFile_IsNeverMaterialized()
    {
        // ADR-008: the default is materialized only on the first AP 3.3 save,
        // never auto-copied — loading must leave the base dir untouched.
        using var dir = new TempDir();
        var locator = new RulesetLocator(dir.Path);

        _ = locator.LoadEffective();

        Assert.False(File.Exists(locator.UserRulesetPath));
        Assert.Empty(Directory.GetFiles(dir.Path, "*", SearchOption.AllDirectories));
    }

    // --- precedence: valid user file wins outright ----------------------------------

    [Fact]
    public void LoadEffective_ValidUserFile_WinsOutright_WithNoErrors()
    {
        using var dir = new TempDir();
        var locator = new RulesetLocator(dir.Path);
        var user = RulesetLoader.LoadDefault() with
        {
            Name = "My custom ruleset",
            Author = "RulesetLocatorTests",
        };
        Directory.CreateDirectory(Path.GetDirectoryName(locator.UserRulesetPath)!);

        // Save prepends a "// saved by GroupWeaver" comment header — reading it
        // back also pins that the locator loads JSONC, not strict JSON.
        RulesetSerializer.Save(user, locator.UserRulesetPath);

        var effective = locator.LoadEffective();

        Assert.True(effective.FromUserFile);
        Assert.Empty(effective.Errors);
        Assert.Equal("My custom ruleset", effective.Ruleset.Name);
        Assert.Equal(
            RulesetSerializer.Serialize(user),
            RulesetSerializer.Serialize(effective.Ruleset));
    }

    [Fact]
    public void LoadEffective_ValidHandEditedJsonc_CommentsAndTrailingCommas_IsAccepted()
    {
        using var dir = new TempDir();
        var locator = new RulesetLocator(dir.Path);
        WriteUserFile(
            locator,
            """
            {
              // hand-edited user file: comments and trailing commas are legal JSONC
              "schemaVersion": 1,
              "name": "Verbatim user ruleset",
              "nesting": {
                "enabled": true,
                "severity": "error",
                "unlisted": "deny",
                "matrix": {
                  "GlobalGroup": { "User": "allow", },
                },
                "exceptions": [],
              },
              "naming": [],
              "circular": { "enabled": true, "severity": "error", "exceptions": [] },
              "emptyGroup": { "enabled": true, "severity": "info", "exceptions": [] },
              "ignore": [],
            }
            """);

        var effective = locator.LoadEffective();

        Assert.True(effective.FromUserFile);
        Assert.Empty(effective.Errors);
        Assert.Equal("Verbatim user ruleset", effective.Ruleset.Name);
    }

    // --- precedence: invalid user file → default + the errors, never a throw ---------

    [Fact]
    public void LoadEffective_SemanticallyInvalidUserFile_ReturnsDefaultPlusAllLoaderErrors()
    {
        using var dir = new TempDir();
        var locator = new RulesetLocator(dir.Path);

        // Parseable JSON with THREE independent semantic defects: a future
        // schema version, a missing name, and an unknown severity token.
        const string brokenJsonc =
            """
            {
              // deliberately broken user file
              "schemaVersion": 99,
              "nesting": {
                "enabled": true,
                "severity": "error",
                "unlisted": "deny",
                "matrix": { "GlobalGroup": { "User": "allow" } },
                "exceptions": []
              },
              "naming": [],
              "circular": { "enabled": true, "severity": "nuclear", "exceptions": [] },
              "emptyGroup": { "enabled": true, "severity": "info", "exceptions": [] },
              "ignore": []
            }
            """;
        WriteUserFile(locator, brokenJsonc);

        var effective = locator.LoadEffective();

        Assert.False(effective.FromUserFile);
        AssertIsTheEmbeddedDefault(effective.Ruleset);

        // "The errors" means the loader's collect-all list, surfaced verbatim
        // (AP 3.3's live preview renders exactly this list).
        Assert.Equal(RulesetLoader.Load(brokenJsonc).Errors, effective.Errors);
        Assert.Contains(effective.Errors, e => e.Path == "$.schemaVersion");
        Assert.Contains(effective.Errors, e => e.Path == "$.name");
        Assert.Contains(effective.Errors, e => e.Path == "$.circular.severity");
    }

    [Fact]
    public void LoadEffective_MalformedJsonUserFile_ReturnsDefaultPlusOnePositionalError()
    {
        using var dir = new TempDir();
        var locator = new RulesetLocator(dir.Path);
        WriteUserFile(locator, "{ \"schemaVersion\": 1, \"name\": \"x\", ");

        var effective = locator.LoadEffective();

        Assert.False(effective.FromUserFile);
        AssertIsTheEmbeddedDefault(effective.Ruleset);

        var error = Assert.Single(effective.Errors);
        Assert.Equal("$", error.Path);
        Assert.Matches(new Regex("line", RegexOptions.IgnoreCase), error.Message);
    }

    // --- helpers --------------------------------------------------------------------

    private static void WriteUserFile(RulesetLocator locator, string jsonc)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(locator.UserRulesetPath)!);
        File.WriteAllText(locator.UserRulesetPath, jsonc);
    }

    /// <summary>Record equality is unusable across <c>IReadOnlyList</c>
    /// properties (slice 5 precedent) — default-ness is pinned on the
    /// serialized bytes instead.</summary>
    private static void AssertIsTheEmbeddedDefault(Ruleset ruleset)
    {
        Assert.Equal(
            RulesetSerializer.Serialize(RulesetLoader.LoadDefault()),
            RulesetSerializer.Serialize(ruleset));
    }

    private sealed class TempDir : IDisposable
    {
        public string Path { get; } =
            Directory.CreateTempSubdirectory("groupweaver-locator-tests-").FullName;

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
