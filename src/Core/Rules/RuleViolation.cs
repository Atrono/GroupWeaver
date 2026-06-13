namespace GroupWeaver.Core.Rules;

/// <summary>
/// One finding: a pure value derived from (snapshot, ruleset) — ADR-009.
/// Record equality is reference-based over <see cref="Dns"/> — tests and
/// consumers compare projections, never whole records.
/// </summary>
public sealed record RuleViolation
{
    /// <summary><see cref="RuleIds.Nesting"/> / <see cref="RuleIds.Circular"/> /
    /// <see cref="RuleIds.EmptyGroup"/>, or a naming rule id.</summary>
    public required string RuleId { get; init; }

    /// <summary>Effective severity (nesting: <c>Cell.SeverityOverride ??
    /// NestingRule.Severity</c>).</summary>
    public required RuleSeverity Severity { get; init; }

    /// <summary>The DNs this finding attaches to, in stored spellings (never canonicalized).
    /// INVARIANT (pinned by tests):
    /// <code>
    ///   nesting:     exactly [parentDn, memberDn]
    ///   naming:      exactly [subjectDn]
    ///   empty-group: exactly [subjectDn]
    ///   circular:    the canonical cycle rotation (min-DN first; closing edge
    ///                implied last→first; self-membership = [dn]).
    /// </code></summary>
    public required IReadOnlyList<string> Dns { get; init; }

    /// <summary>The AP 3.4 jump-to-node anchor: always <c>Dns[0]</c> (nesting parent /
    /// subject / canonical cycle anchor). Computed — excluded from record equality.</summary>
    public string PrimaryDn => Dns[0];

    /// <summary>Deterministic, culture-invariant, English one-liner. Presentation
    /// aid; identity lives in the structured fields.</summary>
    public required string Message { get; init; }
}
