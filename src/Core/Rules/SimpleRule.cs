namespace GroupWeaver.Core.Rules;

/// <summary>
/// A rule with no parameters beyond the shared enabled/severity/exceptions
/// triple (ADR-008): circular membership (<see cref="RuleIds.Circular"/>) and
/// empty groups (<see cref="RuleIds.EmptyGroup"/>).
/// </summary>
public sealed record SimpleRule
{
    /// <summary>The fixed rule id (<see cref="RuleIds.Circular"/> or
    /// <see cref="RuleIds.EmptyGroup"/>).</summary>
    public required string RuleId { get; init; }

    /// <summary>Whether the rule produces findings at all.</summary>
    public required bool Enabled { get; init; }

    /// <summary>Severity of every violation.</summary>
    public required RuleSeverity Severity { get; init; }

    /// <summary>Per-rule exceptions (endpoint narrowing is not legal here).</summary>
    public required IReadOnlyList<MatchEntry> Exceptions { get; init; }
}
