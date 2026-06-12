using GroupWeaver.Core.Model;

namespace GroupWeaver.Core.Graph;

/// <summary>
/// Pure, deterministic <see cref="DirectorySnapshot"/> → <see cref="GraphModel"/>
/// transform (ADR-004): concentric preset layout with ring key
/// (relative DN depth below root, kind rank), proven no-overlap geometry, every
/// edge endpoint materialized (never dropped). UI-free; must read
/// <see cref="DirectorySnapshot.Edges"/> exactly once per build (perf contract).
/// </summary>
public static class GraphBuilder
{
    /// <summary>Per-ring angular offset in radians, so consecutive rings never
    /// place nodes on the same radial line (ADR-004).</summary>
    private const double RingStagger = 0.35;

    /// <summary>The one ring key for nodes outside the root's DN subtree
    /// (External endpoints, non-descendants): sorts after every real
    /// (depth, kindRank) key, so these always land on the outermost ring.</summary>
    private static readonly (int Depth, int KindRank) OuterRingKey = (int.MaxValue, int.MaxValue);

    /// <summary>Builds the renderable graph for <paramref name="snapshot"/>
    /// rooted at <paramref name="rootDn"/>; <c>null</c> options mean the
    /// ADR-004 defaults.</summary>
    public static GraphModel Build(
        DirectorySnapshot snapshot,
        string rootDn,
        GraphLayoutOptions? options = null)
    {
        options ??= new GraphLayoutOptions();

        // THE single read of snapshot.Edges per build — pinned perf contract
        // (.claude/rules/data-model.md): the property recomputes O(E) on every
        // access, so everything below works off this local copy only.
        var membershipEdges = snapshot.Edges;

        // Deterministic edge order (ADR-004): drives External anchoring and is
        // already the final within-kind output order.
        var sortedMembership = membershipEdges
            .OrderBy(e => e.ParentDn, StringComparer.OrdinalIgnoreCase)
            .ThenBy(e => e.ChildDn, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var seeds = CollectSeeds(snapshot, rootDn, sortedMembership);
        var rootSeed = seeds[rootDn];

        var nodes = PlaceNodes(seeds, rootSeed, rootDn, sortedMembership, options);
        var edges = BuildEdges(seeds, rootSeed, sortedMembership);
        return new GraphModel(nodes, edges);
    }

    /// <summary>Collects the total node set, keyed via <see cref="Dn.Comparer"/>:
    /// every snapshot object, plus an External seed (Label = DN) for every
    /// membership-edge endpoint missing from the snapshot — edges are never
    /// dropped — plus a synthesized External root when the root itself is missing.</summary>
    private static Dictionary<string, NodeSeed> CollectSeeds(
        DirectorySnapshot snapshot,
        string rootDn,
        List<MembershipEdge> membershipEdges)
    {
        var seeds = new Dictionary<string, NodeSeed>(Dn.Comparer);
        foreach (var obj in snapshot.Objects)
        {
            seeds[obj.Dn] = new NodeSeed(obj.Dn, obj.Name, obj.Kind, InScope: true);
        }

        foreach (var edge in membershipEdges)
        {
            AddExternalIfMissing(seeds, edge.ParentDn);
            AddExternalIfMissing(seeds, edge.ChildDn);
        }

        AddExternalIfMissing(seeds, rootDn);
        return seeds;
    }

    private static void AddExternalIfMissing(Dictionary<string, NodeSeed> seeds, string dn)
    {
        if (!seeds.ContainsKey(dn))
        {
            seeds[dn] = new NodeSeed(dn, Label: dn, AdObjectKind.External, InScope: false);
        }
    }

    /// <summary>Assigns rings inside-out and places every node: ring indices are
    /// consecutive over the non-empty ring keys, radii grow by at least the ring
    /// gap and satisfy the exact chord formula, and within-ring order follows the
    /// already-placed anchor angle (DN tie-break), with a per-ring stagger.</summary>
    private static List<GraphNode> PlaceNodes(
        Dictionary<string, NodeSeed> seeds,
        NodeSeed rootSeed,
        string rootDn,
        List<MembershipEdge> sortedMembership,
        GraphLayoutOptions options)
    {
        var rings = seeds.Values
            .Where(seed => !Dn.Comparer.Equals(seed.Dn, rootSeed.Dn))
            .GroupBy(seed => GetRingKey(seed, rootDn))
            .OrderBy(group => group.Key)
            .ToList();

        // First referencing membership parent per child DN, in the deterministic
        // edge order — the anchor for External nodes, which have no DN ancestry
        // inside the scope.
        var firstParentByChild = new Dictionary<string, string>(Dn.Comparer);
        foreach (var edge in sortedMembership)
        {
            firstParentByChild.TryAdd(edge.ChildDn, edge.ParentDn);
        }

        var placedAngles = new Dictionary<string, double>(Dn.Comparer) { [rootSeed.Dn] = 0d };
        var nodes = new List<GraphNode>(seeds.Count)
        {
            // The root is always ring 0, alone, at exactly the origin.
            new(rootSeed.Dn, rootSeed.Label, rootSeed.Kind, X: 0d, Y: 0d, Ring: 0, IsRoot: true),
        };

        var radius = 0d;
        for (var ring = 1; ring <= rings.Count; ring++)
        {
            var ordered = rings[ring - 1]
                .Select(seed => (Seed: seed, Anchor: GetAnchorAngle(seed, placedAngles, firstParentByChild)))
                .OrderBy(entry => entry.Anchor)
                .ThenBy(entry => entry.Seed.Dn, StringComparer.OrdinalIgnoreCase)
                .Select(entry => entry.Seed)
                .ToList();

            radius = NextRadius(radius, ordered.Count, options);
            for (var i = 0; i < ordered.Count; i++)
            {
                var seed = ordered[i];
                var theta = (2 * Math.PI * i / ordered.Count) + (RingStagger * ring);
                nodes.Add(new GraphNode(
                    seed.Dn,
                    seed.Label,
                    seed.Kind,
                    Math.Round(radius * Math.Cos(theta), 1),
                    Math.Round(radius * Math.Sin(theta), 1),
                    ring,
                    IsRoot: false));
                placedAngles[seed.Dn] = theta;
            }
        }

        return nodes;
    }

    /// <summary>Ring key of a non-root node: (relative DN depth below the root,
    /// kind rank). External nodes and non-descendants (depth -1) share
    /// <see cref="OuterRingKey"/> — the dedicated outermost ring.</summary>
    private static (int Depth, int KindRank) GetRingKey(NodeSeed seed, string rootDn)
    {
        if (seed.Kind == AdObjectKind.External)
        {
            return OuterRingKey;
        }

        var depth = DnPath.RelativeDepth(seed.Dn, rootDn);
        return depth < 0 ? OuterRingKey : (depth, GetKindRank(seed.Kind));
    }

    /// <summary>Kind sub-ring order within one depth (ADR-004): OU=0, DL=1,
    /// UG=2, GG=3, Computer=4, User=5 — the AGDLP reading inside-out.</summary>
    private static int GetKindRank(AdObjectKind kind) => kind switch
    {
        AdObjectKind.OrganizationalUnit => 0,
        AdObjectKind.DomainLocalGroup => 1,
        AdObjectKind.UniversalGroup => 2,
        AdObjectKind.GlobalGroup => 3,
        AdObjectKind.Computer => 4,
        AdObjectKind.User => 5,
        _ => int.MaxValue, // External never reaches here (dedicated outer ring)
    };

    /// <summary>Radius of the next ring out: at least one ring gap beyond the
    /// previous ring, and at least the exact chord-formula radius at which n
    /// equally spaced nodes sit D+m apart (a lone node needs no chord guarantee
    /// — and sin(π/1) = 0 would divide by zero).</summary>
    private static double NextRadius(double previousRadius, int nodeCount, GraphLayoutOptions options)
    {
        var chordRadius = nodeCount == 1
            ? previousRadius + options.RingGap
            : (options.NodeDiameter + options.NodeMargin) / (2 * Math.Sin(Math.PI / nodeCount));
        return Math.Max(previousRadius + options.RingGap, chordRadius);
    }

    /// <summary>Angle a node should gravitate towards: the angle of its nearest
    /// already-placed DN ancestor (rings are processed inside-out, so ancestors
    /// are placed first); for External nodes, the angle of their first
    /// referencing membership parent. No placed anchor means angle 0.</summary>
    private static double GetAnchorAngle(
        NodeSeed seed,
        Dictionary<string, double> placedAngles,
        Dictionary<string, string> firstParentByChild)
    {
        if (seed.Kind == AdObjectKind.External)
        {
            return firstParentByChild.TryGetValue(seed.Dn, out var parentDn) &&
                   placedAngles.TryGetValue(parentDn, out var parentAngle)
                ? parentAngle
                : 0d;
        }

        for (var ancestor = DnPath.Parent(seed.Dn); ancestor is not null; ancestor = DnPath.Parent(ancestor))
        {
            if (placedAngles.TryGetValue(ancestor, out var angle))
            {
                return angle;
            }
        }

        return 0d;
    }

    /// <summary>Emits all membership edges verbatim plus exactly one containment
    /// edge per non-root in-scope node (External/materialized nodes get none),
    /// sorted by (kind, parent DN, child DN) ordinal-ignore-case.</summary>
    private static List<GraphEdge> BuildEdges(
        Dictionary<string, NodeSeed> seeds,
        NodeSeed rootSeed,
        List<MembershipEdge> sortedMembership)
    {
        var edges = new List<GraphEdge>(sortedMembership.Count + seeds.Count);
        foreach (var edge in sortedMembership)
        {
            edges.Add(new GraphEdge(GraphEdgeKind.Membership, edge.ParentDn, edge.ChildDn));
        }

        foreach (var seed in seeds.Values)
        {
            if (!seed.InScope || Dn.Comparer.Equals(seed.Dn, rootSeed.Dn))
            {
                continue;
            }

            edges.Add(new GraphEdge(
                GraphEdgeKind.Containment,
                GetContainmentParentDn(seed, seeds, rootSeed),
                seed.Dn));
        }

        return edges
            .OrderBy(e => e.Kind)
            .ThenBy(e => e.ParentDn, StringComparer.OrdinalIgnoreCase)
            .ThenBy(e => e.ChildDn, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>The nearest DN ancestor that is itself a node, found by climbing
    /// <see cref="DnPath.Parent"/> (DN gaps — ancestors absent from the snapshot —
    /// are skipped over); non-descendants fall back to the root node.</summary>
    private static string GetContainmentParentDn(
        NodeSeed seed,
        Dictionary<string, NodeSeed> seeds,
        NodeSeed rootSeed)
    {
        for (var ancestor = DnPath.Parent(seed.Dn); ancestor is not null; ancestor = DnPath.Parent(ancestor))
        {
            if (seeds.TryGetValue(ancestor, out var parent))
            {
                return parent.Dn;
            }
        }

        return rootSeed.Dn;
    }

    /// <summary>A node before placement: DN identity, display label, kind, and
    /// whether it came from the snapshot (in scope, gets a containment edge) or
    /// was materialized from an edge endpoint / synthesized as the root.</summary>
    private sealed record NodeSeed(string Dn, string Label, AdObjectKind Kind, bool InScope);
}
