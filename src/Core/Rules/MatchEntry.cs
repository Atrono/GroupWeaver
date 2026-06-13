using GroupWeaver.Core.Model;

namespace GroupWeaver.Core.Rules;

/// <summary>
/// Which endpoint of a membership edge a nesting exception applies to.
/// Non-<see cref="Any"/> values are legal only inside <c>nesting.exceptions</c>
/// (enforced by ruleset validation); <see cref="Any"/> must stay 0 so an
/// absent JSON <c>endpoint</c> means Any.
/// </summary>
public enum MatchEndpoint
{
    /// <summary>Either endpoint of the edge (the default).</summary>
    Any = 0,

    /// <summary>Only the containing group of the edge.</summary>
    Parent = 1,

    /// <summary>Only the direct member of the edge.</summary>
    Member = 2,
}

/// <summary>
/// One ignore/exception entry (ADR-008). Exactly one of <see cref="Dn"/> and
/// <see cref="Name"/> is set (enforced by ruleset validation). A dn entry
/// globs against <see cref="AdObject.Dn"/> only; a name entry globs against
/// <see cref="AdObject.Name"/> OR <see cref="AdObject.SamAccountName"/> only
/// and never against raw DN strings. All matching goes through
/// <see cref="GlobMatcher"/> — this record stays pure data.
/// </summary>
public sealed record MatchEntry
{
    /// <summary>DN glob; mutually exclusive with <see cref="Name"/>.</summary>
    public string? Dn { get; init; }

    /// <summary>Name glob (matches Name or SamAccountName); mutually exclusive
    /// with <see cref="Dn"/>.</summary>
    public string? Name { get; init; }

    /// <summary>Free-form remark. Data, not a comment — it survives every
    /// editor round-trip and AP 3.3 renders it verbatim.</summary>
    public string? Note { get; init; }

    /// <summary>Edge-endpoint narrowing for nesting exceptions.</summary>
    public MatchEndpoint Endpoint { get; init; }

    /// <summary>Whether this entry matches <paramref name="obj"/>: a dn entry
    /// against its Dn, a name entry against its Name or SamAccountName.</summary>
    public bool Matches(AdObject obj)
    {
        if (Dn is not null)
        {
            return GlobMatcher.IsMatch(Dn, obj.Dn);
        }

        return Name is not null
            && (GlobMatcher.IsMatch(Name, obj.Name)
                || (obj.SamAccountName is not null && GlobMatcher.IsMatch(Name, obj.SamAccountName)));
    }

    /// <summary>Whether this entry matches a raw member DN absent from the
    /// snapshot. Name entries NEVER match raw DNs — there is no name to
    /// compare, and a name glob must not accidentally swallow DN strings.</summary>
    public bool MatchesDn(string dn) => Dn is not null && GlobMatcher.IsMatch(Dn, dn);
}
