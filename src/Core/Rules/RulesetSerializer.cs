using System.Globalization;
using System.Text.Json;
using GroupWeaver.Core.Model;

namespace GroupWeaver.Core.Rules;

/// <summary>
/// Writes rulesets back to disk (ADR-008; the AP 3.3 import/export substrate).
/// <see cref="Serialize"/> emits strict camelCase indented JSON with nulls
/// omitted — matrix dictionary keys stay verbatim PascalCase kind names — by
/// mapping the model onto the same DTO layer the loader reads, so
/// <c>Save→Load→Save</c> is a fixed point on bytes. <see cref="Save"/>
/// prepends a generated comment header (which <see cref="RulesetLoader.Load"/>
/// tolerates) and is atomic: temp file in the target directory, then
/// <see cref="File.Move(string, string, bool)"/> with overwrite.
/// </summary>
public static class RulesetSerializer
{
    /// <summary>Serializes <paramref name="ruleset"/> to strict JSON — no
    /// comment header, parseable under default reader options.</summary>
    public static string Serialize(Ruleset ruleset) =>
        JsonSerializer.Serialize(ToDocument(ruleset), RulesetJson.WriteOptions);

    /// <summary>Writes <paramref name="ruleset"/> to <paramref name="path"/>
    /// as a generated comment header plus <see cref="Serialize"/>. Atomic:
    /// a torn write can never destroy the previous file.</summary>
    public static void Save(Ruleset ruleset, string path)
    {
        string content =
            "// saved by GroupWeaver on "
            + DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss'Z'", CultureInfo.InvariantCulture)
            + Environment.NewLine
            + "// Comments are not preserved when GroupWeaver rewrites this file - keep durable remarks in \"note\" fields."
            + Environment.NewLine
            + Serialize(ruleset)
            + Environment.NewLine;

        string fullPath = Path.GetFullPath(path);
        string tempPath = Path.Combine(
            Path.GetDirectoryName(fullPath)!, Path.GetRandomFileName() + ".groupweaver-tmp");
        try
        {
            File.WriteAllText(tempPath, content);
            File.Move(tempPath, fullPath, overwrite: true);
        }
        catch
        {
            try
            {
                File.Delete(tempPath);
            }
            catch (IOException)
            {
                // Best-effort cleanup; the original failure is the one to surface.
            }

            throw;
        }
    }

    // --- model -> DTO -------------------------------------------------------

    private static RulesetDocument ToDocument(Ruleset ruleset) => new()
    {
        SchemaVersion = ruleset.SchemaVersion,
        Name = ruleset.Name,
        Description = ruleset.Description,
        Author = ruleset.Author,
        Nesting = new NestingDocument
        {
            Enabled = ruleset.Nesting.Enabled,
            Severity = SeverityToken(ruleset.Nesting.Severity),
            Unlisted = CellToken(ruleset.Nesting.Unlisted),
            Matrix = ToDocument(ruleset.Nesting.Matrix),
            Exceptions = ToDocuments(ruleset.Nesting.Exceptions),
        },
        Naming = ruleset.Naming.Select(ToDocument).ToList(),
        Circular = ToDocument(ruleset.Circular),
        EmptyGroup = ToDocument(ruleset.EmptyGroup),
        Ignore = ToDocuments(ruleset.Ignore),
    };

    private static Dictionary<string, Dictionary<string, string>> ToDocument(
        IReadOnlyDictionary<AdObjectKind, IReadOnlyDictionary<AdObjectKind, NestingCell>> matrix)
    {
        var document = new Dictionary<string, Dictionary<string, string>>();
        foreach ((var parent, var row) in matrix)
        {
            var cells = new Dictionary<string, string>();
            foreach ((var member, var cell) in row)
            {
                cells[member.ToString()] = CellToken(cell);
            }

            document[parent.ToString()] = cells;
        }

        return document;
    }

    private static NamingRuleDocument ToDocument(NamingRule rule) => new()
    {
        Id = rule.Id,
        Enabled = rule.Enabled,
        Severity = SeverityToken(rule.Severity),
        Kind = rule.Kind.ToString(),
        Pattern = rule.Pattern,
        Description = rule.Description,
        Exceptions = ToDocuments(rule.Exceptions),
    };

    /// <summary><see cref="SimpleRule.RuleId"/> is fixed by schema position
    /// (circular/emptyGroup) and deliberately never written.</summary>
    private static SimpleRuleDocument ToDocument(SimpleRule rule) => new()
    {
        Enabled = rule.Enabled,
        Severity = SeverityToken(rule.Severity),
        Exceptions = ToDocuments(rule.Exceptions),
    };

    private static List<MatchEntryDocument> ToDocuments(IReadOnlyList<MatchEntry> entries) =>
        entries
            .Select(entry => new MatchEntryDocument
            {
                Dn = entry.Dn,
                Name = entry.Name,
                Note = entry.Note,
                Endpoint = EndpointToken(entry.Endpoint),
            })
            .ToList();

    /// <summary>A deny cell keeps its explicit override token even when it
    /// equals the rule severity — <c>NestingCell(false, Error)</c> and
    /// <c>NestingCell(false, null)</c> load back differently.</summary>
    private static string CellToken(NestingCell cell) => cell switch
    {
        { Allowed: true } => "allow",
        { SeverityOverride: null } => "deny",
        _ => SeverityToken(cell.SeverityOverride.Value),
    };

    private static string SeverityToken(RuleSeverity severity) => severity switch
    {
        RuleSeverity.Error => "error",
        RuleSeverity.Warning => "warning",
        RuleSeverity.Info => "info",
        _ => throw new ArgumentOutOfRangeException(nameof(severity), severity, "unknown severity."),
    };

    /// <summary><see cref="MatchEndpoint.Any"/> maps to null (omitted) — an
    /// absent JSON <c>endpoint</c> means Any; <c>"any"</c> is never written.</summary>
    private static string? EndpointToken(MatchEndpoint endpoint) => endpoint switch
    {
        MatchEndpoint.Any => null,
        MatchEndpoint.Parent => "parent",
        MatchEndpoint.Member => "member",
        _ => throw new ArgumentOutOfRangeException(nameof(endpoint), endpoint, "unknown endpoint."),
    };
}
