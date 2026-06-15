using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.RegularExpressions;
using GroupWeaver.Core.Model;

namespace GroupWeaver.Core.Rules;

/// <summary>
/// Parses and validates ruleset files (ADR-008). <see cref="Load"/> NEVER
/// throws on bad input. Phase 1 (syntax): malformed JSON or an unknown
/// property yields exactly one error with path <c>$</c> and line/position in
/// the message. Phase 2 (semantic): ALL independent defects are collected in
/// one pass with JSON paths (<c>$.naming[0].kind</c>) — AP 3.3's live preview
/// renders the complete list. Kind tokens are exact-case enum names (the
/// DemoProvider precedent); severity/cell/endpoint tokens are lowercase.
/// </summary>
public static class RulesetLoader
{
    /// <summary>Loads <paramref name="jsonText"/> into a validated
    /// <see cref="Ruleset"/>, or path-addressed errors — never an exception.</summary>
    public static RulesetLoadResult Load(string jsonText)
    {
        RulesetDocument? document;
        try
        {
            document = JsonSerializer.Deserialize<RulesetDocument>(jsonText, RulesetJson.ReadOptions);
        }
        catch (JsonException ex)
        {
            return new RulesetLoadResult(null, [new RulesetValidationError("$", SyntaxMessage(ex))], []);
        }

        if (document is null)
        {
            return new RulesetLoadResult(
                null,
                [new RulesetValidationError("$", "the document is JSON null - expected a ruleset object.")],
                []);
        }

        var errors = new List<RulesetValidationError>();
        var ruleset = ConvertDocument(document, errors);
        return new RulesetLoadResult(errors.Count == 0 ? ruleset : null, errors, []);
    }

    /// <summary>Loads the embedded strict-AGDLP default ruleset (ADR-008).
    /// A missing or invalid resource is a build defect, not user input, so —
    /// unlike <see cref="Load"/> — this throws <see cref="InvalidDataException"/>.</summary>
    public static Ruleset LoadDefault()
    {
        const string resourceName = "GroupWeaver.Core.Rules.DefaultRuleset.jsonc";
        using var stream = typeof(RulesetLoader).Assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidDataException($"embedded resource '{resourceName}' is missing from GroupWeaver.Core.");
        using var reader = new StreamReader(stream);

        var result = Load(reader.ReadToEnd());
        if (!result.Success)
        {
            throw new InvalidDataException(
                $"the embedded default ruleset '{resourceName}' is corrupt: "
                + string.Join(" | ", result.Errors.Select(e => $"{e.Path}: {e.Message}")));
        }

        return result.Ruleset;
    }

    /// <summary>The phase-1 error message: System.Text.Json already appends
    /// <c>LineNumber</c>/<c>BytePositionInLine</c>; this only backfills when a
    /// future runtime stops doing so.</summary>
    private static string SyntaxMessage(JsonException exception)
    {
        var message = exception.Message;
        if (exception.LineNumber is long line && !message.Contains("LineNumber", StringComparison.Ordinal))
        {
            message += $" LineNumber: {line} | BytePositionInLine: {exception.BytePositionInLine ?? 0}.";
        }

        return message;
    }

    private static Ruleset? ConvertDocument(RulesetDocument document, List<RulesetValidationError> errors)
    {
        if (document.SchemaVersion is null)
        {
            errors.Add(Missing("$.schemaVersion"));
        }
        else if (document.SchemaVersion != 1)
        {
            errors.Add(new RulesetValidationError(
                "$.schemaVersion",
                $"unsupported schema version {document.SchemaVersion} - this GroupWeaver reads version 1 "
                + "(the file was probably written by a newer GroupWeaver)."));
        }

        if (document.Name is null)
        {
            errors.Add(Missing("$.name"));
        }

        var nesting = ConvertNesting(document.Nesting, errors);
        var naming = ConvertNaming(document.Naming, errors);
        var circular = ConvertSimple(document.Circular, RuleIds.Circular, "$.circular", errors);
        var emptyGroup = ConvertSimple(document.EmptyGroup, RuleIds.EmptyGroup, "$.emptyGroup", errors);
        var ignore = ConvertEntries(document.Ignore, "$.ignore", endpointAllowed: false, errors);

        if (errors.Count > 0)
        {
            return null;
        }

        return new Ruleset
        {
            SchemaVersion = document.SchemaVersion!.Value,
            Name = document.Name!,
            Description = document.Description,
            Author = document.Author,
            Nesting = nesting!,
            Naming = naming,
            Circular = circular!,
            EmptyGroup = emptyGroup!,
            Ignore = ignore,
        };
    }

    private static NestingRule? ConvertNesting(NestingDocument? document, List<RulesetValidationError> errors)
    {
        if (document is null)
        {
            errors.Add(Missing("$.nesting"));
            return null;
        }

        int before = errors.Count;
        bool enabled = RequireBool(document.Enabled, "$.nesting.enabled", errors);
        var severity = ConvertSeverity(document.Severity, "$.nesting.severity", errors);
        var unlisted = ConvertCell(document.Unlisted, "$.nesting.unlisted", errors);
        var matrix = ConvertMatrix(document.Matrix, errors);
        var exceptions = ConvertEntries(document.Exceptions, "$.nesting.exceptions", endpointAllowed: true, errors);

        if (errors.Count > before)
        {
            return null;
        }

        return new NestingRule
        {
            Enabled = enabled,
            Severity = severity!.Value,
            Unlisted = unlisted!,
            Matrix = matrix!,
            Exceptions = exceptions,
        };
    }

    private static IReadOnlyDictionary<AdObjectKind, IReadOnlyDictionary<AdObjectKind, NestingCell>>? ConvertMatrix(
        Dictionary<string, Dictionary<string, string>>? document, List<RulesetValidationError> errors)
    {
        if (document is null)
        {
            errors.Add(Missing("$.nesting.matrix"));
            return null;
        }

        int before = errors.Count;
        var matrix = new Dictionary<AdObjectKind, IReadOnlyDictionary<AdObjectKind, NestingCell>>();
        foreach ((string rowKey, Dictionary<string, string> cells) in document)
        {
            string rowPath = $"$.nesting.matrix.{rowKey}";
            if (!TryParseKind(rowKey, out var parentKind)
                || parentKind is not (AdObjectKind.GlobalGroup or AdObjectKind.DomainLocalGroup or AdObjectKind.UniversalGroup))
            {
                // Cells inside an unknown row would only be noise: one defect, one error.
                errors.Add(new RulesetValidationError(
                    rowPath,
                    $"unknown parent group kind '{rowKey}' (expected GlobalGroup, DomainLocalGroup, UniversalGroup)."));
                continue;
            }

            if (cells is null)
            {
                errors.Add(new RulesetValidationError(rowPath, "a matrix row must be an object of member-kind cells."));
                continue;
            }

            var row = new Dictionary<AdObjectKind, NestingCell>();
            foreach ((string cellKey, string token) in cells)
            {
                string cellPath = $"{rowPath}.{cellKey}";
                if (!TryParseKind(cellKey, out var memberKind) || memberKind == AdObjectKind.OrganizationalUnit)
                {
                    errors.Add(new RulesetValidationError(
                        cellPath,
                        $"unknown member kind '{cellKey}' (expected User, Computer, GlobalGroup, "
                        + "DomainLocalGroup, UniversalGroup, External)."));
                    continue;
                }

                var cell = ConvertCell(token, cellPath, errors);
                if (cell is not null)
                {
                    row[memberKind] = cell;
                }
            }

            matrix[parentKind] = row;
        }

        return errors.Count > before ? null : matrix;
    }

    private static IReadOnlyList<NamingRule> ConvertNaming(
        List<NamingRuleDocument>? documents, List<RulesetValidationError> errors)
    {
        if (documents is null)
        {
            errors.Add(Missing("$.naming"));
            return [];
        }

        var rules = new List<NamingRule>(documents.Count);
        var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < documents.Count; i++)
        {
            var document = documents[i];
            string path = $"$.naming[{i}]";
            if (document is null)
            {
                errors.Add(new RulesetValidationError(path, "a naming rule must be an object."));
                continue;
            }

            int before = errors.Count;
            if (document.Id is null)
            {
                errors.Add(Missing($"{path}.id"));
            }
            else if (!seenIds.Add(document.Id))
            {
                errors.Add(new RulesetValidationError(
                    $"{path}.id",
                    $"duplicate naming rule id '{document.Id}' (ids are unique case-insensitively)."));
            }

            bool enabled = RequireBool(document.Enabled, $"{path}.enabled", errors);
            var severity = ConvertSeverity(document.Severity, $"{path}.severity", errors);
            var kind = ConvertNamingKind(document.Kind, $"{path}.kind", errors);
            ValidatePattern(document.Pattern, $"{path}.pattern", errors);
            var exceptions = ConvertEntries(document.Exceptions, $"{path}.exceptions", endpointAllowed: false, errors);

            if (errors.Count > before)
            {
                continue;
            }

            rules.Add(new NamingRule
            {
                Id = document.Id!,
                Enabled = enabled,
                Severity = severity!.Value,
                Kind = kind!.Value,
                Pattern = document.Pattern!,
                Description = document.Description,
                Exceptions = exceptions,
            });
        }

        return rules;
    }

    private static AdObjectKind? ConvertNamingKind(string? token, string path, List<RulesetValidationError> errors)
    {
        if (token is null)
        {
            errors.Add(Missing(path));
            return null;
        }

        if (!TryParseKind(token, out var kind))
        {
            errors.Add(new RulesetValidationError(
                path,
                $"unknown kind '{token}' (expected User, Computer, GlobalGroup, DomainLocalGroup, "
                + "UniversalGroup, OrganizationalUnit)."));
            return null;
        }

        if (kind == AdObjectKind.External)
        {
            errors.Add(new RulesetValidationError(
                path,
                "naming rules may not target kind 'External' - External objects are never findings subjects (ADR-008)."));
            return null;
        }

        return kind;
    }

    /// <summary>The maximum length, in characters, of a naming-rule regex pattern.
    /// RegexOptions.NonBacktracking builds a DFA at CONSTRUCTION time whose cost scales
    /// with pattern size, and GlobMatcher.RegexMatchTimeout bounds MATCHING only — not the
    /// `new Regex(...)` call. The 0.2 adversarial audit measured an untrusted (community-
    /// shared) ~263 KB / 40k-alternation pattern freezing Load for 7.5 s (689 KB -> 58 s),
    /// a DoS a mere file-size cap cannot stop (one huge pattern defeats it). 1000 chars is
    /// generous for any real naming convention (the strict-AGDLP defaults are well under
    /// 60) yet far below the seconds-scale construction range — patterns over the cap are
    /// rejected BEFORE the Regex is ever constructed.</summary>
    private const int MaxPatternLength = 1000;

    /// <summary>Validate-compiles a naming pattern exactly as the engine will
    /// (NonBacktracking | CultureInvariant) so files fail at load, not at scan. An
    /// over-long pattern is rejected on LENGTH before construction (DoS guard, see
    /// <see cref="MaxPatternLength"/>).</summary>
    private static void ValidatePattern(string? pattern, string path, List<RulesetValidationError> errors)
    {
        if (pattern is null)
        {
            errors.Add(Missing(path));
            return;
        }

        if (pattern.Length > MaxPatternLength)
        {
            errors.Add(new RulesetValidationError(
                path,
                $"pattern is {pattern.Length} characters and exceeds the maximum length of "
                + $"{MaxPatternLength} characters (a long pattern can make the linear-time engine's "
                + "Regex construction prohibitively slow)."));
            return;
        }

        try
        {
            _ = new Regex(
                pattern,
                RegexOptions.NonBacktracking | RegexOptions.CultureInvariant,
                GlobMatcher.RegexMatchTimeout);
        }
        catch (NotSupportedException ex)
        {
            errors.Add(new RulesetValidationError(
                path,
                "pattern uses a construct the linear-time engine does not support "
                + $"(lookarounds and backreferences are unavailable): {ex.Message}"));
        }
        catch (ArgumentException ex)
        {
            errors.Add(new RulesetValidationError(path, $"pattern does not compile: {ex.Message}"));
        }
    }

    private static SimpleRule? ConvertSimple(
        SimpleRuleDocument? document, string ruleId, string path, List<RulesetValidationError> errors)
    {
        if (document is null)
        {
            errors.Add(Missing(path));
            return null;
        }

        int before = errors.Count;
        bool enabled = RequireBool(document.Enabled, $"{path}.enabled", errors);
        var severity = ConvertSeverity(document.Severity, $"{path}.severity", errors);
        var exceptions = ConvertEntries(document.Exceptions, $"{path}.exceptions", endpointAllowed: false, errors);

        if (errors.Count > before)
        {
            return null;
        }

        return new SimpleRule
        {
            RuleId = ruleId,
            Enabled = enabled,
            Severity = severity!.Value,
            Exceptions = exceptions,
        };
    }

    private static IReadOnlyList<MatchEntry> ConvertEntries(
        List<MatchEntryDocument>? documents, string path, bool endpointAllowed, List<RulesetValidationError> errors)
    {
        if (documents is null)
        {
            errors.Add(Missing(path));
            return [];
        }

        var entries = new List<MatchEntry>(documents.Count);
        for (int i = 0; i < documents.Count; i++)
        {
            var document = documents[i];
            string entryPath = $"{path}[{i}]";
            if (document is null)
            {
                errors.Add(new RulesetValidationError(entryPath, "a match entry must be an object."));
                continue;
            }

            if (document.Dn is null == document.Name is null)
            {
                errors.Add(new RulesetValidationError(entryPath, "exactly one of 'dn' and 'name' must be set."));
            }

            var endpoint = ConvertEndpoint(document.Endpoint, $"{entryPath}.endpoint", endpointAllowed, errors);
            entries.Add(new MatchEntry
            {
                Dn = document.Dn,
                Name = document.Name,
                Note = document.Note,
                Endpoint = endpoint,
            });
        }

        return entries;
    }

    private static MatchEndpoint ConvertEndpoint(
        string? token, string path, bool endpointAllowed, List<RulesetValidationError> errors)
    {
        MatchEndpoint endpoint;
        switch (token)
        {
            case null:
            case "any":
                return MatchEndpoint.Any;
            case "parent":
                endpoint = MatchEndpoint.Parent;
                break;
            case "member":
                endpoint = MatchEndpoint.Member;
                break;
            default:
                errors.Add(new RulesetValidationError(
                    path, $"unknown endpoint '{token}' (expected parent, member, any)."));
                return MatchEndpoint.Any;
        }

        if (!endpointAllowed)
        {
            errors.Add(new RulesetValidationError(path, "endpoint narrowing is only legal in nesting exceptions."));
            return MatchEndpoint.Any;
        }

        return endpoint;
    }

    private static RuleSeverity? ConvertSeverity(string? token, string path, List<RulesetValidationError> errors)
    {
        switch (token)
        {
            case null:
                errors.Add(Missing(path));
                return null;
            case "error":
                return RuleSeverity.Error;
            case "warning":
                return RuleSeverity.Warning;
            case "info":
                return RuleSeverity.Info;
            default:
                errors.Add(new RulesetValidationError(
                    path, $"unknown severity '{token}' (expected error, warning, info)."));
                return null;
        }
    }

    private static NestingCell? ConvertCell(string? token, string path, List<RulesetValidationError> errors)
    {
        switch (token)
        {
            case null:
                errors.Add(Missing(path));
                return null;
            case "allow":
                return new NestingCell(true, null);
            case "deny":
                return new NestingCell(false, null);
            case "error":
                return new NestingCell(false, RuleSeverity.Error);
            case "warning":
                return new NestingCell(false, RuleSeverity.Warning);
            case "info":
                return new NestingCell(false, RuleSeverity.Info);
            default:
                errors.Add(new RulesetValidationError(
                    path, $"unknown cell token '{token}' (expected allow, deny, error, warning, info)."));
                return null;
        }
    }

    private static bool RequireBool(bool? value, string path, List<RulesetValidationError> errors)
    {
        if (value is null)
        {
            errors.Add(Missing(path));
            return false;
        }

        return value.Value;
    }

    /// <summary>Exact-case enum-name parsing — the DemoProvider kind precedent.</summary>
    private static bool TryParseKind(string token, out AdObjectKind kind) =>
        Enum.TryParse(token, ignoreCase: false, out kind) && Enum.IsDefined(kind);

    private static RulesetValidationError Missing(string path) =>
        new(path, "missing required property.");
}

/// <summary>
/// The outcome of <see cref="RulesetLoader.Load"/>: any error means no
/// ruleset. <see cref="Warnings"/> never blocks and is empty in schema v1 —
/// the channel exists for future non-fatal notices.
/// </summary>
public sealed record RulesetLoadResult(
    Ruleset? Ruleset,
    IReadOnlyList<RulesetValidationError> Errors,
    IReadOnlyList<RulesetValidationError> Warnings)
{
    /// <summary>True when there are no errors; then <see cref="Ruleset"/> is set.</summary>
    [MemberNotNullWhen(true, nameof(Ruleset))]
    public bool Success => Errors.Count == 0;
}

/// <summary>One validation finding. Semantic errors carry the JSON path of
/// the offending value (<c>$.nesting.matrix.GlobalGroup.Foo</c>); syntax
/// errors use path <c>$</c> with line/position info in the message.</summary>
public sealed record RulesetValidationError(string Path, string Message);
