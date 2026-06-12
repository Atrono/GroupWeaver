using GroupWeaver.Core.Model;

namespace GroupWeaver.Core.Graph;

/// <summary>
/// Result of <see cref="MembershipTraversal.Walk"/> (ADR-006): what one
/// cycle-safe walk down the membership digraph reached, which cycles it closed,
/// and where loaded data ends. All DN strings are reported as FIRST encountered,
/// never canonicalized.
/// </summary>
/// <param name="Visited">Every DN reached: start DN first, DFS preorder, no
/// duplicates under <see cref="Dn.Comparer"/>.</param>
/// <param name="Cycles">One entry per back edge u→v with v on the DFS path at
/// the time: the path slice [v..u]; the closing edge u→v is implied last→first.
/// Self-membership yields a single-element path. The rotation is start-relative;
/// cross-start cycle identity/normalization is the consumer's concern (AP 3.2).
/// Cycles are values, never exceptions — a cyclic directory stays auditable.</param>
/// <param name="Frontier">Subset of <paramref name="Visited"/> in visit order
/// whose members were never loaded (<see cref="DirectorySnapshot.GetMembers"/>
/// is <c>null</c> — loaded-and-empty is NOT frontier) AND whose kind is in
/// ADR-005's fetchable set {GG, DL, UG, External}: congruent with "what a
/// double-click would fetch". Users, computers, and OUs are leaves, never
/// frontier — the AP 3.4 "unexpanded areas unchecked" hint.</param>
public sealed record MembershipWalk(
    IReadOnlyList<string> Visited,
    IReadOnlyList<IReadOnlyList<string>> Cycles,
    IReadOnlyList<string> Frontier);
