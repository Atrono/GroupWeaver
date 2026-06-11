namespace GroupWeaver.Core.Model;

/// <summary>
/// A directed "has member" edge: <paramref name="ParentDn"/> lists
/// <paramref name="ChildDn"/> in its <c>member</c> attribute. Direction matters:
/// (A,B) != (B,A). Equality and hashing use <see cref="Dn.Comparer"/> (the
/// positional record default would compare DNs case-sensitively).
/// </summary>
/// <param name="ParentDn">DN of the containing group.</param>
/// <param name="ChildDn">DN of the direct member.</param>
public readonly record struct MembershipEdge(string ParentDn, string ChildDn)
{
    /// <summary>Case-insensitive, direction-sensitive equality via <see cref="Dn.Comparer"/>.</summary>
    public bool Equals(MembershipEdge other) =>
        Dn.Comparer.Equals(ParentDn, other.ParentDn) &&
        Dn.Comparer.Equals(ChildDn, other.ChildDn);

    /// <summary>Hash consistent with the case-insensitive equality.</summary>
    public override int GetHashCode() =>
        HashCode.Combine(
            Dn.Comparer.GetHashCode(ParentDn),
            Dn.Comparer.GetHashCode(ChildDn));
}
