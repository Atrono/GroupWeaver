using GroupWeaver.Core.Rules;

namespace GroupWeaver.App.Settings;

/// <summary>
/// The five editor verdicts for one nesting-matrix cell (AP 3.3 / ADR-011 §2).
/// The mapping to <see cref="NestingCell"/> is the EXACT inverse of
/// <c>RulesetSerializer.CellToken</c> (the round-trip crux):
/// <see cref="Allow"/>→<c>(true,null)</c>, <see cref="Deny"/>→<c>(false,null)</c>,
/// <see cref="Error"/>→<c>(false,Error)</c>, <see cref="Warning"/>→<c>(false,Warning)</c>,
/// <see cref="Info"/>→<c>(false,Info)</c>. A bare <see cref="Deny"/> keeps no severity
/// override (it inherits the rule severity at evaluation); <see cref="Error"/>/
/// <see cref="Warning"/>/<see cref="Info"/> pin a per-cell override — the
/// <c>deny</c>-vs-<c>error</c> token distinction the serializer preserves.
/// </summary>
public enum CellChoice
{
    /// <summary>The pairing is allowed: <c>NestingCell(true, null)</c>.</summary>
    Allow,

    /// <summary>Denied at the rule severity (no override): <c>NestingCell(false, null)</c>.</summary>
    Deny,

    /// <summary>Denied with an Error override: <c>NestingCell(false, Error)</c>.</summary>
    Error,

    /// <summary>Denied with a Warning override: <c>NestingCell(false, Warning)</c>.</summary>
    Warning,

    /// <summary>Denied with an Info override: <c>NestingCell(false, Info)</c>.</summary>
    Info,
}

/// <summary>Conversions between <see cref="CellChoice"/> and the immutable
/// <see cref="NestingCell"/> — the single mapping both directions of the
/// matrix mirror share, kept here so the inverse can never drift.</summary>
internal static class CellChoiceMapping
{
    /// <summary>The <see cref="CellChoice"/> for an existing immutable cell.</summary>
    public static CellChoice FromCell(NestingCell cell) => cell switch
    {
        { Allowed: true } => CellChoice.Allow,
        { SeverityOverride: null } => CellChoice.Deny,
        { SeverityOverride: RuleSeverity.Error } => CellChoice.Error,
        { SeverityOverride: RuleSeverity.Warning } => CellChoice.Warning,
        _ => CellChoice.Info,
    };

    /// <summary>The immutable cell for a <see cref="CellChoice"/> — exact inverse
    /// of <c>RulesetSerializer.CellToken</c>.</summary>
    public static NestingCell ToCell(CellChoice choice) => choice switch
    {
        CellChoice.Allow => new NestingCell(true, null),
        CellChoice.Deny => new NestingCell(false, null),
        CellChoice.Error => new NestingCell(false, RuleSeverity.Error),
        CellChoice.Warning => new NestingCell(false, RuleSeverity.Warning),
        _ => new NestingCell(false, RuleSeverity.Info),
    };
}
