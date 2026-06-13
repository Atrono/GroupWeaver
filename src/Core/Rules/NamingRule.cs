using GroupWeaver.Core.Model;

namespace GroupWeaver.Core.Rules;

/// <summary>
/// One naming-convention rule (ADR-008): a .NET regex evaluated against
/// <c>SamAccountName ?? Name</c> of every object of <see cref="Kind"/>.
/// The pattern is anchored as written, case-SENSITIVE by default (inline
/// <c>(?i)</c> supported), and compiled by the engine with
/// NonBacktracking | CultureInvariant — this record stays pure data.
/// </summary>
public sealed record NamingRule
{
    /// <summary>User-chosen kebab-case id, unique case-insensitively.</summary>
    public required string Id { get; init; }

    /// <summary>Whether the rule produces findings at all.</summary>
    public required bool Enabled { get; init; }

    /// <summary>Severity of every violation.</summary>
    public required RuleSeverity Severity { get; init; }

    /// <summary>The object kind judged; <see cref="AdObjectKind.External"/> is
    /// forbidden (enforced by ruleset validation).</summary>
    public required AdObjectKind Kind { get; init; }

    /// <summary>The regex a conforming name must match.</summary>
    public required string Pattern { get; init; }

    /// <summary>Human-readable grammar of the convention; AP 3.3 preview and
    /// AP 3.4 sidebar text.</summary>
    public string? Description { get; init; }

    /// <summary>Per-rule exceptions (endpoint narrowing is not legal here).</summary>
    public required IReadOnlyList<MatchEntry> Exceptions { get; init; }
}
