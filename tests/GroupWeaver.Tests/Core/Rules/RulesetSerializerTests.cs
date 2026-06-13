using System.Text.Json;

using GroupWeaver.Core.Model;
using GroupWeaver.Core.Rules;

using Xunit;

namespace GroupWeaver.Tests.Core.Rules;

/// <summary>
/// Pins <see cref="RulesetSerializer"/> (ADR-008 slice 5, the AP 3.3
/// import/export substrate). <c>Serialize</c> produces strict camelCase
/// indented JSON with nulls omitted while matrix dictionary keys stay verbatim
/// PascalCase kind names (NOT camelCased — the loader parses them exact-case).
/// <c>Save</c> writes a "// saved by GroupWeaver" comment header that
/// <see cref="RulesetLoader.Load"/> tolerates, and is atomic via a temp file
/// in the target directory + move-overwrite: a pre-existing target is cleanly
/// overwritten and no stray temp file remains. The round-trip is pinned as a
/// FIXED POINT ON BYTES — record equality is unusable across
/// <c>IReadOnlyList</c> properties, so bytes, not records, are the contract:
/// <c>Serialize(Load(Serialize(x)).Ruleset!) == Serialize(x)</c>.
/// </summary>
public class RulesetSerializerTests
{
    // --- Serialize: strict camelCase indented JSON ------------------------------

    [Fact]
    public void Serialize_ProducesStrictIndentedJson_WithoutCommentHeader()
    {
        var json = RulesetSerializer.Serialize(CreateSampleRuleset());

        // The header is Save's job; Serialize emits pure JSON.
        Assert.StartsWith("{", json, StringComparison.Ordinal);
        Assert.Contains("\n", json, StringComparison.Ordinal);

        // Strict JSON: parses under DEFAULT reader options, i.e. no comments
        // and no trailing commas anywhere in the output.
        using var doc = JsonDocument.Parse(json);
        Assert.Equal(JsonValueKind.Object, doc.RootElement.ValueKind);
    }

    [Fact]
    public void Serialize_UsesCamelCasePropertyNames()
    {
        var json = RulesetSerializer.Serialize(CreateSampleRuleset());
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        foreach (var name in new[]
                 {
                     "schemaVersion", "name", "description", "author",
                     "nesting", "naming", "circular", "emptyGroup", "ignore",
                 })
        {
            Assert.True(root.TryGetProperty(name, out _), $"missing camelCase property '{name}'");
        }

        Assert.False(root.TryGetProperty("SchemaVersion", out _));
        Assert.False(root.TryGetProperty("EmptyGroup", out _));

        var nesting = root.GetProperty("nesting");
        foreach (var name in new[] { "enabled", "severity", "unlisted", "matrix", "exceptions" })
        {
            Assert.True(nesting.TryGetProperty(name, out _), $"missing camelCase property 'nesting.{name}'");
        }

        // SimpleRule.RuleId is fixed by schema position (circular/emptyGroup)
        // and must not leak into the file as a property.
        Assert.False(root.GetProperty("circular").TryGetProperty("ruleId", out _));
        Assert.False(root.GetProperty("emptyGroup").TryGetProperty("ruleId", out _));
    }

    [Fact]
    public void Serialize_MatrixDictionaryKeys_StayVerbatimPascalCase()
    {
        var json = RulesetSerializer.Serialize(CreateSampleRuleset());
        using var doc = JsonDocument.Parse(json);
        var matrix = doc.RootElement.GetProperty("nesting").GetProperty("matrix");

        // Row keys: exact AdObjectKind names, never camelCased by the naming policy.
        Assert.True(matrix.TryGetProperty("GlobalGroup", out var ggRow));
        Assert.True(matrix.TryGetProperty("DomainLocalGroup", out var dlRow));
        Assert.True(matrix.TryGetProperty("UniversalGroup", out _));
        Assert.False(matrix.TryGetProperty("globalGroup", out _));
        Assert.False(matrix.TryGetProperty("domainLocalGroup", out _));

        // Column keys likewise.
        Assert.True(ggRow.TryGetProperty("User", out _));
        Assert.True(ggRow.TryGetProperty("DomainLocalGroup", out _));
        Assert.False(ggRow.TryGetProperty("user", out _));
        Assert.True(dlRow.TryGetProperty("External", out _));
        Assert.False(dlRow.TryGetProperty("external", out _));
    }

    [Fact]
    public void Serialize_WritesSchemaTokens_NotDotNetEnumNames()
    {
        var json = RulesetSerializer.Serialize(CreateSampleRuleset());
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.Equal(1, root.GetProperty("schemaVersion").GetInt32());

        var nesting = root.GetProperty("nesting");
        Assert.True(nesting.GetProperty("enabled").GetBoolean());
        Assert.Equal("error", nesting.GetProperty("severity").GetString());
        Assert.Equal("deny", nesting.GetProperty("unlisted").GetString());

        var matrix = nesting.GetProperty("matrix");
        Assert.Equal("allow", matrix.GetProperty("GlobalGroup").GetProperty("User").GetString());
        Assert.Equal("deny", matrix.GetProperty("GlobalGroup").GetProperty("DomainLocalGroup").GetString());
        Assert.Equal("info", matrix.GetProperty("DomainLocalGroup").GetProperty("External").GetString());
        Assert.Equal("warning", matrix.GetProperty("UniversalGroup").GetProperty("UniversalGroup").GetString());

        // A deny cell with an explicit override equal to the rule severity must
        // stay the severity token, not collapse to "deny" — the loader binds
        // NestingCell(false, Error) and NestingCell(false, null) differently.
        Assert.Equal("error", matrix.GetProperty("UniversalGroup").GetProperty("DomainLocalGroup").GetString());

        Assert.Equal("member", nesting.GetProperty("exceptions")[0].GetProperty("endpoint").GetString());
        Assert.Equal("parent", nesting.GetProperty("exceptions")[1].GetProperty("endpoint").GetString());

        var namingGg = root.GetProperty("naming")[0];
        Assert.Equal("warning", namingGg.GetProperty("severity").GetString());
        Assert.Equal("GlobalGroup", namingGg.GetProperty("kind").GetString());

        Assert.False(root.GetProperty("emptyGroup").GetProperty("enabled").GetBoolean());
        Assert.Equal("info", root.GetProperty("emptyGroup").GetProperty("severity").GetString());
    }

    // --- Serialize: nulls omitted -----------------------------------------------

    [Fact]
    public void Serialize_OmitsNullTopLevelMetadata()
    {
        var json = RulesetSerializer.Serialize(CreateMinimalRuleset());
        using var doc = JsonDocument.Parse(json);

        Assert.False(doc.RootElement.TryGetProperty("description", out _));
        Assert.False(doc.RootElement.TryGetProperty("author", out _));
    }

    [Fact]
    public void Serialize_OmitsNullsOnNamingRulesAndMatchEntries()
    {
        var json = RulesetSerializer.Serialize(CreateSampleRuleset());
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        // naming[1] has no description.
        Assert.False(root.GetProperty("naming")[1].TryGetProperty("description", out _));

        // A dn entry carries no "name"; a name entry no "dn"; null notes vanish.
        var dnEntry = root.GetProperty("ignore")[0];
        Assert.False(dnEntry.TryGetProperty("name", out _));
        var nameEntry = root.GetProperty("ignore")[1];
        Assert.False(nameEntry.TryGetProperty("dn", out _));
        Assert.False(nameEntry.TryGetProperty("note", out _));

        // Endpoint Any is the JSON default ("absent endpoint means Any") and is
        // omitted, never written as "any".
        Assert.False(dnEntry.TryGetProperty("endpoint", out _));
        Assert.False(nameEntry.TryGetProperty("endpoint", out _));

        // A nesting exception without a note: endpoint present, note absent.
        var parentException = root.GetProperty("nesting").GetProperty("exceptions")[1];
        Assert.True(parentException.TryGetProperty("endpoint", out _));
        Assert.False(parentException.TryGetProperty("note", out _));
    }

    // --- Fixed point on BYTES ----------------------------------------------------

    [Fact]
    public void Serialize_LoadSerialize_IsAFixedPointOnBytes()
    {
        // Record equality is unusable across IReadOnlyList properties — the
        // round-trip contract is byte equality of the serialized form.
        var first = RulesetSerializer.Serialize(CreateSampleRuleset());

        var second = RulesetSerializer.Serialize(LoadValid(first));

        Assert.Equal(first, second);
    }

    [Fact]
    public void Serialize_LoadSerialize_FixedPointHolds_ForAMinimalRuleset()
    {
        var first = RulesetSerializer.Serialize(CreateMinimalRuleset());

        var second = RulesetSerializer.Serialize(LoadValid(first));

        Assert.Equal(first, second);
    }

    // --- Save: comment header + Load round-trip -----------------------------------

    [Fact]
    public void Save_WritesCommentHeader_ThatLoadTolerates()
    {
        using var dir = new TempDir();
        var path = Path.Combine(dir.Path, "ruleset.jsonc");
        var ruleset = CreateSampleRuleset();

        RulesetSerializer.Save(ruleset, path);

        var text = File.ReadAllText(path);
        Assert.StartsWith("// saved by GroupWeaver", text, StringComparison.Ordinal);

        // Save = generated header + Serialize, nothing else.
        Assert.Contains(RulesetSerializer.Serialize(ruleset), text, StringComparison.Ordinal);

        var result = RulesetLoader.Load(text);
        Assert.True(result.Success, Describe(result));
    }

    [Fact]
    public void SaveThenLoad_YieldsAnEquivalentRuleset()
    {
        using var dir = new TempDir();
        var path = Path.Combine(dir.Path, "ruleset.jsonc");
        var original = CreateSampleRuleset();

        RulesetSerializer.Save(original, path);
        var reloaded = LoadValid(File.ReadAllText(path));

        // Spot checks for legible failures...
        Assert.Equal(original.Name, reloaded.Name);
        Assert.Equal(original.Description, reloaded.Description);
        Assert.Equal(original.Author, reloaded.Author);
        Assert.Equal(original.Naming.Count, reloaded.Naming.Count);
        Assert.Equal(
            new NestingCell(false, RuleSeverity.Info),
            reloaded.Nesting.Cell(AdObjectKind.DomainLocalGroup, AdObjectKind.External));
        Assert.Equal(original.Nesting.Exceptions[0], reloaded.Nesting.Exceptions[0]);
        Assert.Equal(original.Ignore[0], reloaded.Ignore[0]);

        // ...and full equivalence on the serialized bytes.
        Assert.Equal(RulesetSerializer.Serialize(original), RulesetSerializer.Serialize(reloaded));
    }

    // --- Save: atomic overwrite, no stray temp file --------------------------------

    [Fact]
    public void Save_OverExistingTarget_CleanlyOverwrites()
    {
        using var dir = new TempDir();
        var path = Path.Combine(dir.Path, "ruleset.jsonc");

        // Pre-existing content LONGER than the new file: an in-place partial
        // write would leave a stale tail behind; move-overwrite must not.
        File.WriteAllText(path, "STALE" + new string('x', 256 * 1024));

        RulesetSerializer.Save(CreateSampleRuleset(), path);

        var text = File.ReadAllText(path);
        Assert.StartsWith("// saved by GroupWeaver", text, StringComparison.Ordinal);
        Assert.DoesNotContain("STALE", text, StringComparison.Ordinal);

        var result = RulesetLoader.Load(text);
        Assert.True(result.Success, Describe(result));
    }

    [Fact]
    public void Save_LeavesNoStrayTempFileInTheTargetDirectory()
    {
        using var dir = new TempDir();
        var path = Path.Combine(dir.Path, "ruleset.jsonc");

        // Both paths of the atomic dance: fresh create and move-overwrite.
        RulesetSerializer.Save(CreateSampleRuleset(), path);
        RulesetSerializer.Save(CreateMinimalRuleset(), path);

        var file = Assert.Single(Directory.GetFiles(dir.Path, "*", SearchOption.AllDirectories));
        Assert.Equal(path, file);
    }

    // --- helpers --------------------------------------------------------------------

    /// <summary>Exercises every serializable feature while staying loader-valid:
    /// per-cell severity overrides, endpoint-narrowed nesting exceptions, dn and
    /// name entries, null and non-null optionals.</summary>
    private static Ruleset CreateSampleRuleset() => new()
    {
        SchemaVersion = 1,
        Name = "Serializer test ruleset",
        Description = "Round-trip fixture for RulesetSerializer tests.",
        Author = "GroupWeaver tests",
        Nesting = new NestingRule
        {
            Enabled = true,
            Severity = RuleSeverity.Error,
            Unlisted = new NestingCell(false, null),
            Matrix = new Dictionary<AdObjectKind, IReadOnlyDictionary<AdObjectKind, NestingCell>>
            {
                [AdObjectKind.GlobalGroup] = new Dictionary<AdObjectKind, NestingCell>
                {
                    [AdObjectKind.User] = new NestingCell(true, null),
                    [AdObjectKind.Computer] = new NestingCell(true, null),
                    [AdObjectKind.DomainLocalGroup] = new NestingCell(false, null),
                },
                [AdObjectKind.DomainLocalGroup] = new Dictionary<AdObjectKind, NestingCell>
                {
                    [AdObjectKind.GlobalGroup] = new NestingCell(true, null),
                    [AdObjectKind.External] = new NestingCell(false, RuleSeverity.Info),
                },
                [AdObjectKind.UniversalGroup] = new Dictionary<AdObjectKind, NestingCell>
                {
                    [AdObjectKind.UniversalGroup] = new NestingCell(false, RuleSeverity.Warning),
                    [AdObjectKind.DomainLocalGroup] = new NestingCell(false, RuleSeverity.Error),
                },
            },
            Exceptions = new[]
            {
                new MatchEntry { Name = "UG_AllStaff", Endpoint = MatchEndpoint.Member, Note = "migration grace" },
                new MatchEntry { Dn = "CN=UG_Managers,OU=Groups,*", Endpoint = MatchEndpoint.Parent },
            },
        },
        Naming = new[]
        {
            new NamingRule
            {
                Id = "naming-gg",
                Enabled = true,
                Severity = RuleSeverity.Warning,
                Kind = AdObjectKind.GlobalGroup,
                Pattern = "^GG_[A-Z][A-Za-z0-9]*$",
                Description = "GG names",
                Exceptions = new[] { new MatchEntry { Name = "SalesTeamGlobal", Note = "grandfathered" } },
            },
            new NamingRule
            {
                Id = "naming-dl",
                Enabled = false,
                Severity = RuleSeverity.Info,
                Kind = AdObjectKind.DomainLocalGroup,
                Pattern = "^DL_[A-Z][A-Za-z0-9]*_(RW|RO)$",
                Description = null,
                Exceptions = Array.Empty<MatchEntry>(),
            },
        },
        Circular = new SimpleRule
        {
            RuleId = RuleIds.Circular,
            Enabled = true,
            Severity = RuleSeverity.Error,
            Exceptions = Array.Empty<MatchEntry>(),
        },
        EmptyGroup = new SimpleRule
        {
            RuleId = RuleIds.EmptyGroup,
            Enabled = false,
            Severity = RuleSeverity.Info,
            Exceptions = new[] { new MatchEntry { Name = "UG_ProjectX", Note = "placeholder" } },
        },
        Ignore = new[]
        {
            new MatchEntry { Dn = "*,CN=Builtin,*", Note = "builtins" },
            new MatchEntry { Name = "krbtgt" },
        },
    };

    /// <summary>The smallest loader-valid ruleset: every optional null or empty.</summary>
    private static Ruleset CreateMinimalRuleset() => new()
    {
        SchemaVersion = 1,
        Name = "Minimal",
        Nesting = new NestingRule
        {
            Enabled = true,
            Severity = RuleSeverity.Error,
            Unlisted = new NestingCell(false, null),
            Matrix = new Dictionary<AdObjectKind, IReadOnlyDictionary<AdObjectKind, NestingCell>>
            {
                [AdObjectKind.GlobalGroup] = new Dictionary<AdObjectKind, NestingCell>
                {
                    [AdObjectKind.User] = new NestingCell(true, null),
                },
            },
            Exceptions = Array.Empty<MatchEntry>(),
        },
        Naming = Array.Empty<NamingRule>(),
        Circular = new SimpleRule
        {
            RuleId = RuleIds.Circular,
            Enabled = true,
            Severity = RuleSeverity.Error,
            Exceptions = Array.Empty<MatchEntry>(),
        },
        EmptyGroup = new SimpleRule
        {
            RuleId = RuleIds.EmptyGroup,
            Enabled = true,
            Severity = RuleSeverity.Info,
            Exceptions = Array.Empty<MatchEntry>(),
        },
        Ignore = Array.Empty<MatchEntry>(),
    };

    private static Ruleset LoadValid(string json)
    {
        var result = RulesetLoader.Load(json);

        Assert.True(result.Success, Describe(result));
        return result.Ruleset!;
    }

    private static string Describe(RulesetLoadResult result) =>
        "expected success, got: "
            + string.Join("; ", result.Errors.Select(e => $"{e.Path}: {e.Message}"));

    private sealed class TempDir : IDisposable
    {
        public string Path { get; } =
            Directory.CreateTempSubdirectory("groupweaver-ruleset-tests-").FullName;

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
