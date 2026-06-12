using GroupWeaver.Core.Model;

namespace GroupWeaver.Core.Graph;

/// <summary>
/// THE one transitive membership walk (ADR-006, AP 2.4) — AP 3.2's circularity
/// check and AP 3.4's roll-up consume this instead of re-rolling one. Pure,
/// deterministic, UI-free, never calls a provider. Iterative DFS (explicit
/// stack; no recursion — deep nesting and 10K-member groups must not risk stack
/// depth), children in stored <see cref="DirectorySnapshot.SetMembers"/> order,
/// adjacency via <see cref="DirectorySnapshot.GetMembers"/> per node — never
/// <see cref="DirectorySnapshot.Edges"/> (O(E) recompute per access, and only
/// GetMembers carries the null-vs-empty tri-state). No depth/visit bounds: the
/// visited set guarantees O(V+E) termination on the finite in-memory snapshot,
/// and the walk takes no provider — there is no I/O channel to become unbounded
/// through (ADR-006 D4).
/// </summary>
public static class MembershipTraversal
{
    /// <summary>Walks the membership digraph (group → member) downward from
    /// <paramref name="startDn"/> over the loaded data in
    /// <paramref name="snapshot"/>. Cycles and unexpanded frontier come back as
    /// result values, never exceptions.</summary>
    public static MembershipWalk Walk(DirectorySnapshot snapshot, string startDn)
    {
        var visited = new List<string>();
        var cycles = new List<IReadOnlyList<string>>();
        var frontier = new List<string>();

        // White/gray/black coloring keyed via Dn.Comparer (ADR-006 D2): white =
        // in neither set, gray = on the current DFS path, black = finished. A
        // plain visited set would misreport diamond reconvergence as a cycle.
        var gray = new HashSet<string>(Dn.Comparer);
        var black = new HashSet<string>(Dn.Comparer);

        // The explicit DFS stack; bottom-to-top it IS the current DFS path, so
        // a back edge can slice its cycle path straight out of it.
        var path = new List<Frame>();
        Enter(snapshot, startDn, visited, frontier, gray, path);

        while (path.Count > 0)
        {
            var frame = path[^1];
            if (frame.NextChild >= frame.Members.Count)
            {
                path.RemoveAt(path.Count - 1);
                gray.Remove(frame.Dn);
                black.Add(frame.Dn);
                continue;
            }

            var child = frame.Members[frame.NextChild++];
            if (gray.Contains(child))
            {
                // Back edge u→v with v on the current DFS path: the cycle is the
                // path slice [v..u]; the closing edge u→v is implied last→first
                // (self-membership: u = v, a single-element slice). The slice
                // carries the first-encountered DN strings already on the path —
                // a case-variant closing edge never leaks its spelling.
                cycles.Add(SlicePathFrom(path, child));
            }
            else if (!black.Contains(child))
            {
                Enter(snapshot, child, visited, frontier, gray, path);
            }

            // Black child: forward/cross edge (diamond reconvergence) — neither
            // a cycle nor a re-visit.
        }

        return new MembershipWalk(visited, cycles, frontier);
    }

    /// <summary>Preorder visit: records <paramref name="dn"/> as first
    /// encountered, classifies it as frontier (never loaded AND fetchable kind),
    /// and opens its DFS frame — a never-loaded node has no children to descend
    /// into (null ≠ empty, the load-state tri-state's first load-bearing
    /// consumer).</summary>
    private static void Enter(
        DirectorySnapshot snapshot,
        string dn,
        List<string> visited,
        List<string> frontier,
        HashSet<string> gray,
        List<Frame> path)
    {
        visited.Add(dn);
        gray.Add(dn);

        var members = snapshot.GetMembers(dn);
        if (members is null && IsFetchableKind(snapshot.GetKind(dn)))
        {
            frontier.Add(dn);
        }

        path.Add(new Frame(dn, members ?? []));
    }

    /// <summary>The DNs of the current DFS path from the re-entered gray node
    /// (located via <see cref="Dn.Comparer"/> — it is guaranteed on the path)
    /// up to and including the top.</summary>
    private static IReadOnlyList<string> SlicePathFrom(List<Frame> path, string reenteredDn)
    {
        var start = path.FindIndex(frame => Dn.Comparer.Equals(frame.Dn, reenteredDn));
        var slice = new List<string>(path.Count - start);
        for (var i = start; i < path.Count; i++)
        {
            slice.Add(path[i].Dn);
        }

        return slice;
    }

    /// <summary>ADR-005's fetchable kinds — "frontier" is congruent with "what a
    /// double-click would fetch": groups plus External (a DN missing from the
    /// snapshot resolves to External by contract). Users, computers, and OUs are
    /// leaves whose members are never fetched — flagging them would gut the
    /// AP 3.4 unchecked hint (ADR-006 D2).</summary>
    private static bool IsFetchableKind(AdObjectKind kind) => kind
        is AdObjectKind.GlobalGroup
        or AdObjectKind.DomainLocalGroup
        or AdObjectKind.UniversalGroup
        or AdObjectKind.External;

    /// <summary>An open DFS frame: the node's first-encountered DN string, its
    /// stored direct members (empty when never loaded), and the cursor of the
    /// next child to process.</summary>
    private sealed record Frame(string Dn, IReadOnlyList<string> Members)
    {
        public int NextChild { get; set; }
    }
}
