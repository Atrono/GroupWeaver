using System.Text.RegularExpressions;

using GroupWeaver.Core.Model;
using GroupWeaver.Core.Rules;

using Xunit;

namespace GroupWeaver.Tests.Core.Rules;

/// <summary>
/// Pins <see cref="RulesetLoader.Load"/> (ADR-008): JSONC in, validated
/// <see cref="Ruleset"/> or path-addressed errors out. Load NEVER throws on
/// bad input. Phase 1 (syntax): malformed JSON or an unknown property yields
/// exactly ONE error with Path "$" and positional info in the message. Phase 2
/// (semantic): ALL independent defects are collected in one pass with JSON
/// paths ("$.naming[0].kind") — AP 3.3's live preview renders the full list.
/// Any error means no ruleset; the Warnings channel exists but is empty in v1.
/// </summary>
public class RulesetLoaderTests
{
    // --- Baseline: one fully valid document, mutated per defect ---------------

    private const string Baseline = """
        {
          "schemaVersion": 1,
          "name": "Loader test ruleset",
          "description": "Baseline document for RulesetLoader tests.",
          "author": "GroupWeaver tests",
          "nesting": {
            "enabled": true,
            "severity": "error",
            "unlisted": "deny",
            "matrix": {
              "GlobalGroup": {
                "User": "allow",
                "DomainLocalGroup": "deny"
              },
              "DomainLocalGroup": {
                "User": "deny",
                "GlobalGroup": "allow",
                "External": "info"
              },
              "UniversalGroup": {
                "UniversalGroup": "warning",
                "DomainLocalGroup": "error"
              }
            },
            "exceptions": [
              { "name": "UG_AllStaff", "endpoint": "member", "note": "migration grace" }
            ]
          },
          "naming": [
            {
              "id": "naming-gg",
              "enabled": true,
              "severity": "warning",
              "kind": "GlobalGroup",
              "pattern": "^GG_[A-Z][A-Za-z0-9]*$",
              "description": "GG names",
              "exceptions": [
                { "name": "SalesTeamGlobal", "note": "grandfathered" }
              ]
            }
          ],
          "circular": { "enabled": true, "severity": "error", "exceptions": [] },
          "emptyGroup": {
            "enabled": false,
            "severity": "info",
            "exceptions": [
              { "name": "UG_ProjectX", "note": "placeholder" }
            ]
          },
          "ignore": [
            { "dn": "*,CN=Builtin,*", "note": "builtins" }
          ]
        }
        """;

    // --- Valid document --------------------------------------------------------

    [Fact]
    public void Load_ValidDocument_SucceedsWithNoErrorsOrWarnings()
    {
        var result = RulesetLoader.Load(Baseline);

        Assert.Empty(result.Errors);
        Assert.True(result.Success);
        Assert.Empty(result.Warnings);
        Assert.NotNull(result.Ruleset);
    }

    [Fact]
    public void Load_ValidDocument_BindsTopLevelMetadata()
    {
        var ruleset = LoadValid(Baseline);

        Assert.Equal(1, ruleset.SchemaVersion);
        Assert.Equal("Loader test ruleset", ruleset.Name);
        Assert.Equal("Baseline document for RulesetLoader tests.", ruleset.Description);
        Assert.Equal("GroupWeaver tests", ruleset.Author);
    }

    [Fact]
    public void Load_ValidDocument_BindsNestingRuleAndMatrixCells()
    {
        var nesting = LoadValid(Baseline).Nesting;

        Assert.True(nesting.Enabled);
        Assert.Equal(RuleSeverity.Error, nesting.Severity);
        Assert.Equal(new NestingCell(false, null), nesting.Unlisted);

        // Token mapping: "allow" / "deny" / per-cell severity override.
        Assert.Equal(
            new NestingCell(true, null),
            nesting.Cell(AdObjectKind.GlobalGroup, AdObjectKind.User));
        Assert.Equal(
            new NestingCell(false, null),
            nesting.Cell(AdObjectKind.GlobalGroup, AdObjectKind.DomainLocalGroup));
        Assert.Equal(
            new NestingCell(false, RuleSeverity.Info),
            nesting.Cell(AdObjectKind.DomainLocalGroup, AdObjectKind.External));
        Assert.Equal(
            new NestingCell(false, RuleSeverity.Warning),
            nesting.Cell(AdObjectKind.UniversalGroup, AdObjectKind.UniversalGroup));

        // An explicit "error" cell is an override, distinct from a plain "deny"
        // even when it equals the rule severity.
        Assert.Equal(
            new NestingCell(false, RuleSeverity.Error),
            nesting.Cell(AdObjectKind.UniversalGroup, AdObjectKind.DomainLocalGroup));

        // Absent column and absent row both fall back to the unlisted cell.
        Assert.Equal(nesting.Unlisted, nesting.Cell(AdObjectKind.GlobalGroup, AdObjectKind.Computer));
        Assert.Equal(nesting.Unlisted, nesting.Cell(AdObjectKind.User, AdObjectKind.User));
    }

    [Fact]
    public void Load_ValidDocument_BindsExceptionsAndIgnoreEntries()
    {
        var ruleset = LoadValid(Baseline);

        Assert.Equal(
            new MatchEntry { Name = "UG_AllStaff", Endpoint = MatchEndpoint.Member, Note = "migration grace" },
            Assert.Single(ruleset.Nesting.Exceptions));

        // Absent "endpoint" means Any; dn entries keep Name null and vice versa.
        Assert.Equal(
            new MatchEntry { Dn = "*,CN=Builtin,*", Note = "builtins" },
            Assert.Single(ruleset.Ignore));
    }

    [Fact]
    public void Load_ValidDocument_BindsNamingAndSimpleRules()
    {
        var ruleset = LoadValid(Baseline);

        var naming = Assert.Single(ruleset.Naming);
        Assert.Equal("naming-gg", naming.Id);
        Assert.True(naming.Enabled);
        Assert.Equal(RuleSeverity.Warning, naming.Severity);
        Assert.Equal(AdObjectKind.GlobalGroup, naming.Kind);
        Assert.Equal("^GG_[A-Z][A-Za-z0-9]*$", naming.Pattern);
        Assert.Equal("GG names", naming.Description);
        Assert.Equal(
            new MatchEntry { Name = "SalesTeamGlobal", Note = "grandfathered" },
            Assert.Single(naming.Exceptions));

        Assert.Equal(RuleIds.Circular, ruleset.Circular.RuleId);
        Assert.True(ruleset.Circular.Enabled);
        Assert.Equal(RuleSeverity.Error, ruleset.Circular.Severity);
        Assert.Empty(ruleset.Circular.Exceptions);

        Assert.Equal(RuleIds.EmptyGroup, ruleset.EmptyGroup.RuleId);
        Assert.False(ruleset.EmptyGroup.Enabled);
        Assert.Equal(RuleSeverity.Info, ruleset.EmptyGroup.Severity);
        Assert.Equal(
            new MatchEntry { Name = "UG_ProjectX", Note = "placeholder" },
            Assert.Single(ruleset.EmptyGroup.Exceptions));
    }

    // --- Tolerated extras: $schema, comments, trailing commas -----------------

    [Fact]
    public void Load_TopLevelSchemaProperty_IsTolerated()
    {
        var json = MutateBaseline(
            "\"schemaVersion\": 1,",
            "\"$schema\": \"https://example.invalid/ruleset.schema.json\", \"schemaVersion\": 1,");

        var ruleset = LoadValid(json);

        Assert.Equal(1, ruleset.SchemaVersion);
    }

    [Fact]
    public void Load_CommentsAndTrailingCommas_ParseFine()
    {
        const string json = """
            {
              // line comment
              "schemaVersion": 1, /* block comment */
              "name": "Commented ruleset",
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
            """;

        var ruleset = LoadValid(json);

        Assert.Equal("Commented ruleset", ruleset.Name);
        Assert.Equal(
            new NestingCell(true, null),
            ruleset.Nesting.Cell(AdObjectKind.GlobalGroup, AdObjectKind.User));
        Assert.Empty(ruleset.Naming);
    }

    // --- Syntax errors: fail-first, one error, Path "$", positional info ------

    [Fact]
    public void Load_MalformedJson_YieldsSingleErrorWithLinePosition()
    {
        var result = RulesetLoader.Load("{ \"schemaVersion\": 1, \"name\": \"x\", ");

        Assert.False(result.Success);
        Assert.Null(result.Ruleset);
        var error = Assert.Single(result.Errors);
        Assert.Equal("$", error.Path);
        Assert.Matches(new Regex("line", RegexOptions.IgnoreCase), error.Message);
    }

    [Fact]
    public void Load_UnknownProperty_TypoSeverty_FailsWithPositionalInfo()
    {
        var json = MutateBaseline("\"severity\": \"warning\",", "\"severty\": \"warning\",");

        var result = RulesetLoader.Load(json);

        Assert.False(result.Success);
        Assert.Null(result.Ruleset);
        var error = Assert.Single(result.Errors);
        Assert.Equal("$", error.Path);
        Assert.Contains("severty", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Matches(new Regex("line", RegexOptions.IgnoreCase), error.Message);
    }

    // --- Semantic defects: one defect, one path-addressed error ---------------

    public static TheoryData<string, string, string, string?> SemanticDefects() => new()
    {
        {
            "schemaVersion other than 1 is rejected",
            MutateBaseline("\"schemaVersion\": 1,", "\"schemaVersion\": 2,"),
            "$.schemaVersion",
            "version"
        },
        {
            "missing required top-level name",
            MutateBaseline("\"name\": \"Loader test ruleset\",", ""),
            "$.name",
            "missing|required"
        },
        {
            "missing required naming pattern",
            MutateBaseline("\"pattern\": \"^GG_[A-Z][A-Za-z0-9]*$\",", ""),
            "$.naming[0].pattern",
            "missing|required"
        },
        {
            "duplicate naming id (case-insensitive)",
            DuplicateNamingIdDocument,
            "$.naming[1].id",
            "duplicate|already"
        },
        {
            "naming rule may not target kind External",
            MutateBaseline("\"kind\": \"GlobalGroup\",", "\"kind\": \"External\","),
            "$.naming[0].kind",
            "External"
        },
        {
            "uncompilable naming regex",
            MutateBaseline("\"pattern\": \"^GG_[A-Z][A-Za-z0-9]*$\",", "\"pattern\": \"^GG_[\","),
            "$.naming[0].pattern",
            null
        },
        {
            "lookahead rejected by NonBacktracking, error names the limitation",
            MutateBaseline("\"pattern\": \"^GG_[A-Z][A-Za-z0-9]*$\",", "\"pattern\": \"^(?=GG_).*$\","),
            "$.naming[0].pattern",
            "look.?around|look.?ahead|backreference"
        },
        {
            "backreference rejected by NonBacktracking, error names the limitation",
            MutateBaseline("\"pattern\": \"^GG_[A-Z][A-Za-z0-9]*$\",", "\"pattern\": \"^(G)\\\\1$\","),
            "$.naming[0].pattern",
            "look.?around|look.?ahead|backreference"
        },
        {
            "unknown matrix row kind",
            MutateBaseline("\"matrix\": {", "\"matrix\": { \"Foo\": { \"User\": \"allow\" },"),
            "$.nesting.matrix.Foo",
            "Foo"
        },
        {
            "unknown matrix column kind",
            MutateBaseline("\"User\": \"allow\",", "\"Foo\": \"allow\","),
            "$.nesting.matrix.GlobalGroup.Foo",
            "Foo"
        },
        {
            "matrix kind keys are exact-case (lowercase row entry rejected)",
            MutateBaseline("\"User\": \"allow\",", "\"user\": \"allow\","),
            "$.nesting.matrix.GlobalGroup.user",
            null
        },
        {
            "invalid matrix cell token",
            MutateBaseline("\"User\": \"allow\",", "\"User\": \"yes\","),
            "$.nesting.matrix.GlobalGroup.User",
            "yes"
        },
        {
            "invalid severity token",
            MutateBaseline(
                "\"circular\": { \"enabled\": true, \"severity\": \"error\", \"exceptions\": [] },",
                "\"circular\": { \"enabled\": true, \"severity\": \"fatal\", \"exceptions\": [] },"),
            "$.circular.severity",
            "fatal"
        },
        {
            "invalid endpoint token",
            MutateBaseline("\"endpoint\": \"member\",", "\"endpoint\": \"both\","),
            "$.nesting.exceptions[0].endpoint",
            "both"
        },
        {
            "non-Any endpoint in naming exceptions rejected",
            MutateBaseline(
                "{ \"name\": \"SalesTeamGlobal\", \"note\": \"grandfathered\" }",
                "{ \"name\": \"SalesTeamGlobal\", \"endpoint\": \"member\", \"note\": \"grandfathered\" }"),
            "$.naming[0].exceptions[0]",
            "endpoint"
        },
        {
            "non-Any endpoint in the ignore list rejected",
            MutateBaseline(
                "{ \"dn\": \"*,CN=Builtin,*\", \"note\": \"builtins\" }",
                "{ \"dn\": \"*,CN=Builtin,*\", \"endpoint\": \"parent\", \"note\": \"builtins\" }"),
            "$.ignore[0]",
            "endpoint"
        },
        {
            "match entry with both dn and name rejected",
            MutateBaseline(
                "{ \"dn\": \"*,CN=Builtin,*\", \"note\": \"builtins\" }",
                "{ \"dn\": \"*,CN=Builtin,*\", \"name\": \"Builtin\", \"note\": \"builtins\" }"),
            "$.ignore[0]",
            "dn"
        },
        {
            "match entry with neither dn nor name rejected",
            MutateBaseline(
                "{ \"dn\": \"*,CN=Builtin,*\", \"note\": \"builtins\" }",
                "{ \"note\": \"builtins\" }"),
            "$.ignore[0]",
            "dn"
        },
    };

    [Theory]
    [MemberData(nameof(SemanticDefects))]
    public void Load_SingleSemanticDefect_FailsWithOnePathAddressedError(
        string label, string json, string expectedPathPrefix, string? messagePattern)
    {
        var result = RulesetLoader.Load(json);

        Assert.False(result.Success, $"{label}: expected validation failure");
        Assert.Null(result.Ruleset);

        var error = Assert.Single(result.Errors);
        Assert.StartsWith(expectedPathPrefix, error.Path, StringComparison.Ordinal);
        if (messagePattern is not null)
        {
            Assert.Matches(new Regex(messagePattern, RegexOptions.IgnoreCase), error.Message);
        }
    }

    // --- Collect-all: every independent semantic error in one pass ------------

    [Fact]
    public void Load_MultipleIndependentSemanticDefects_AllReportedInOnePass()
    {
        // Four independent defects in one document; AP 3.3's live preview
        // renders the complete list, so the loader must not stop at the first.
        var json = Baseline;
        json = Mutate(json, "\"kind\": \"GlobalGroup\",", "\"kind\": \"External\",");
        json = Mutate(
            json,
            "\"circular\": { \"enabled\": true, \"severity\": \"error\", \"exceptions\": [] },",
            "\"circular\": { \"enabled\": true, \"severity\": \"fatal\", \"exceptions\": [] },");
        json = Mutate(
            json,
            "{ \"dn\": \"*,CN=Builtin,*\", \"note\": \"builtins\" }",
            "{ \"dn\": \"*,CN=Builtin,*\", \"name\": \"Builtin\", \"note\": \"builtins\" }");
        json = Mutate(json, "\"User\": \"allow\",", "\"User\": \"yes\",");

        var result = RulesetLoader.Load(json);

        Assert.False(result.Success);
        Assert.Null(result.Ruleset);
        Assert.True(
            result.Errors.Count >= 4,
            $"expected all 4 independent defects reported, got {result.Errors.Count}: "
                + string.Join("; ", result.Errors.Select(e => e.Path)));
        Assert.Contains(result.Errors, e => e.Path == "$.naming[0].kind");
        Assert.Contains(result.Errors, e => e.Path == "$.circular.severity");
        Assert.Contains(result.Errors, e => e.Path.StartsWith("$.ignore[0]", StringComparison.Ordinal));
        Assert.Contains(result.Errors, e => e.Path == "$.nesting.matrix.GlobalGroup.User");
    }

    // --- Load never throws -----------------------------------------------------

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("null")]
    [InlineData("42")]
    [InlineData("\"just a string\"")]
    [InlineData("[1, 2, 3]")]
    [InlineData("{")]
    public void Load_GarbageInput_NeverThrows_ReportsErrors(string json)
    {
        var result = RulesetLoader.Load(json);

        Assert.False(result.Success);
        Assert.Null(result.Ruleset);
        Assert.NotEmpty(result.Errors);
    }

    // --- helpers ----------------------------------------------------------------

    /// <summary>Two naming rules whose ids differ only by case.</summary>
    private const string DuplicateNamingIdDocument = """
        {
          "schemaVersion": 1,
          "name": "Duplicate naming ids",
          "nesting": {
            "enabled": true,
            "severity": "error",
            "unlisted": "deny",
            "matrix": {
              "GlobalGroup": { "User": "allow" }
            },
            "exceptions": []
          },
          "naming": [
            {
              "id": "naming-gg",
              "enabled": true,
              "severity": "warning",
              "kind": "GlobalGroup",
              "pattern": "^GG_",
              "exceptions": []
            },
            {
              "id": "NAMING-GG",
              "enabled": true,
              "severity": "warning",
              "kind": "GlobalGroup",
              "pattern": "^GG_",
              "exceptions": []
            }
          ],
          "circular": { "enabled": true, "severity": "error", "exceptions": [] },
          "emptyGroup": { "enabled": true, "severity": "info", "exceptions": [] },
          "ignore": []
        }
        """;

    private static Ruleset LoadValid(string json)
    {
        var result = RulesetLoader.Load(json);

        Assert.True(
            result.Success,
            "expected success, got: "
                + string.Join("; ", result.Errors.Select(e => $"{e.Path}: {e.Message}")));
        return result.Ruleset!;
    }

    private static string MutateBaseline(string anchor, string replacement) =>
        Mutate(Baseline, anchor, replacement);

    private static string Mutate(string json, string anchor, string replacement)
    {
        if (!json.Contains(anchor, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"test bug: anchor not found in document: {anchor}");
        }

        return json.Replace(anchor, replacement, StringComparison.Ordinal);
    }
}
