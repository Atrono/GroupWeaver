namespace GroupWeaver.Core.Rules;

/// <summary>
/// One complete, validated ruleset (ADR-008, schema v1). Whole-file
/// precedence: a ruleset is always the complete truth, never merged with
/// another. Construction from JSONC and all semantic validation live in the
/// loader — this record stays pure data.
/// </summary>
public sealed record Ruleset
{
    /// <summary>Always 1 post-validation; anything else is rejected by the loader.</summary>
    public required int SchemaVersion { get; init; }

    /// <summary>Human-readable name of the ruleset.</summary>
    public required string Name { get; init; }

    /// <summary>Optional longer description.</summary>
    public string? Description { get; init; }

    /// <summary>Optional author attribution for shared community files.</summary>
    public string? Author { get; init; }

    /// <summary>The nesting matrix (fixed id <see cref="RuleIds.Nesting"/>).</summary>
    public required NestingRule Nesting { get; init; }

    /// <summary>Naming rules in file order; ids unique case-insensitively.</summary>
    public required IReadOnlyList<NamingRule> Naming { get; init; }

    /// <summary>The circular-membership rule.</summary>
    public required SimpleRule Circular { get; init; }

    /// <summary>The empty-group rule.</summary>
    public required SimpleRule EmptyGroup { get; init; }

    /// <summary>Global ignore list: a matched object is exempt everywhere; an
    /// edge is exempt when either endpoint matches.</summary>
    public required IReadOnlyList<MatchEntry> Ignore { get; init; }

    /// <summary>All rules as flat summaries for the AP 3.3 settings binding:
    /// nesting, each naming rule in file order, circular, empty-group.</summary>
    public IEnumerable<RuleSummary> EnumerateRules()
    {
        yield return new RuleSummary(RuleIds.Nesting, Nesting.Enabled, Nesting.Severity, "Nesting matrix");

        foreach (var rule in Naming)
        {
            yield return new RuleSummary(rule.Id, rule.Enabled, rule.Severity, $"Naming: {rule.Id}");
        }

        yield return new RuleSummary(RuleIds.Circular, Circular.Enabled, Circular.Severity, "Circular nesting");
        yield return new RuleSummary(RuleIds.EmptyGroup, EmptyGroup.Enabled, EmptyGroup.Severity, "Empty groups");
    }
}

/// <summary>One row of <see cref="Ruleset.EnumerateRules"/>; Id/Enabled/Severity
/// flow through from the underlying rule, DisplayName is never null or blank.</summary>
public sealed record RuleSummary(string Id, bool Enabled, RuleSeverity Severity, string DisplayName);
