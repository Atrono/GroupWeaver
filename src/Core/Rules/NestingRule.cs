using GroupWeaver.Core.Model;

namespace GroupWeaver.Core.Rules;

/// <summary>
/// One nesting-matrix cell: whether the parent←member pairing is allowed, plus
/// an optional per-cell severity. The effective severity of a violating edge is
/// <see cref="SeverityOverride"/> ?? <see cref="NestingRule.Severity"/>.
/// </summary>
public sealed record NestingCell(bool Allowed, RuleSeverity? SeverityOverride);

/// <summary>
/// The configurable nesting matrix (ADR-008; fixed id <see cref="RuleIds.Nesting"/>):
/// rows = parent (containing group) kind, columns = direct member kind. Judged
/// domain (binding for AP 3.2): only edges whose parent kind is GG/DL/UG.
/// </summary>
public sealed record NestingRule
{
    /// <summary>Whether the rule produces findings at all.</summary>
    public required bool Enabled { get; init; }

    /// <summary>Severity of every deny cell without its own override.</summary>
    public required RuleSeverity Severity { get; init; }

    /// <summary>Fallback cell for any row or column absent from
    /// <see cref="Matrix"/> — future kinds fail closed without breaking v1 files.</summary>
    public required NestingCell Unlisted { get; init; }

    /// <summary>Rows keyed by parent kind, columns keyed by member kind.</summary>
    public required IReadOnlyDictionary<AdObjectKind, IReadOnlyDictionary<AdObjectKind, NestingCell>> Matrix { get; init; }

    /// <summary>Per-rule exceptions; the only place where
    /// <see cref="MatchEntry.Endpoint"/> may be non-Any.</summary>
    public required IReadOnlyList<MatchEntry> Exceptions { get; init; }

    /// <summary>The cell for <paramref name="parent"/>←<paramref name="member"/>,
    /// falling back to <see cref="Unlisted"/> when the row or the column is absent.</summary>
    public NestingCell Cell(AdObjectKind parent, AdObjectKind member) =>
        Matrix.TryGetValue(parent, out var row) && row.TryGetValue(member, out var cell)
            ? cell
            : Unlisted;
}
