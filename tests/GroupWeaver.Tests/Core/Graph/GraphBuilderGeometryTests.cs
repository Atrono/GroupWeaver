using GroupWeaver.Core.Graph;
using GroupWeaver.Core.Model;
using GroupWeaver.Tests.Providers;

using Xunit;

namespace GroupWeaver.Tests.Core.Graph;

/// <summary>
/// Makes the ADR-004 no-overlap proof executable. With the default options
/// (D=44, m=16, g=150) the exact-space layout guarantees same-ring chord
/// &gt;= D+m = 60 and cross-ring distance &gt;= g = 150, so the minimum pairwise
/// CENTER distance of any build must stay &gt;= D = 44 with room to spare even
/// after the 0.1 coordinate rounding (absorbed by <see cref="Tolerance"/>).
/// Adversarial shapes pin the chord-formula edge cases: a packed ring (radius
/// must grow beyond the ring gap), n=1 (the chord formula divides by
/// sin(pi/n) — must not blow up), n=2, and a deep one-node-per-ring chain.
/// </summary>
public class GraphBuilderGeometryTests : IClassFixture<DemoProviderFixture>
{
    /// <summary>Default node diameter D — the minimum allowed center distance.</summary>
    private const double MinCenterDistance = 44d;

    /// <summary>Default ring gap g between consecutive occupied ring radii.</summary>
    private const double RingGap = 150d;

    /// <summary>Absorbs the 0.1-rounding of coordinates (&lt;= ~0.15 per radius,
    /// &lt;= ~0.3 per pairwise distance) — far below the exact-space slack.</summary>
    private const double Tolerance = 0.5;

    private readonly DemoProviderFixture _fixture;

    public GraphBuilderGeometryTests(DemoProviderFixture fixture) => _fixture = fixture;

    // --- Full demo dataset ------------------------------------------------------

    [Fact]
    public void FullDemoBuild_MinPairwiseCenterDistance_AtLeastNodeDiameter()
    {
        var model = GraphBuilder.Build(_fixture.FullSnapshot, DemoProviderFixture.RootDn);

        Assert.Equal(196, model.Nodes.Count);
        GeometryAssert.MinPairwiseCenterDistance(model.Nodes, MinCenterDistance);
    }

    [Fact]
    public void FullDemoBuild_RingRadii_MonotonicAndKeepRingGap()
    {
        var model = GraphBuilder.Build(_fixture.FullSnapshot, DemoProviderFixture.RootDn);

        AssertRingRadiiMonotonicWithGap(model);
    }

    // --- Adversarial synthetics ----------------------------------------------------

    [Fact]
    public void PackedRing_150NodesSameDepthSameKind_ShareOneRing_NoOverlap()
    {
        const string rootDn = "OU=Root,DC=lab";
        var snapshot = RootPlusUsers(rootDn, 150);

        var model = GraphBuilder.Build(snapshot, rootDn);

        Assert.Equal(151, model.Nodes.Count);
        var userRing = Assert.Single(
            model.Nodes.Where(n => n.Kind == AdObjectKind.User).Select(n => n.Ring).Distinct());
        Assert.NotEqual(0, userRing); // same depth + same kind = exactly one non-root ring

        GeometryAssert.MinPairwiseCenterDistance(model.Nodes, MinCenterDistance);
    }

    [Fact]
    public void SingleNodeRing_BuildSucceeds_NodeSitsAtLeastOneRingGapOut()
    {
        // n=1 breaks the naive chord formula (sin(pi/1) = 0) — the build must
        // still succeed and place the lone node on its ring radius.
        const string rootDn = "OU=Root,DC=lab";
        var snapshot = RootPlusUsers(rootDn, 1);

        var model = GraphBuilder.Build(snapshot, rootDn);

        Assert.Equal(2, model.Nodes.Count);
        var lone = Assert.Single(model.Nodes, n => !n.IsRoot);
        Assert.True(double.IsFinite(lone.X) && double.IsFinite(lone.Y));
        Assert.True(
            Radius(lone) >= RingGap - Tolerance,
            $"single-node ring radius {Radius(lone):F1} fell below the ring gap {RingGap}");
    }

    [Fact]
    public void TwoNodeRing_NoOverlap()
    {
        const string rootDn = "OU=Root,DC=lab";
        var snapshot = RootPlusUsers(rootDn, 2);

        var model = GraphBuilder.Build(snapshot, rootDn);

        Assert.Equal(3, model.Nodes.Count);
        GeometryAssert.MinPairwiseCenterDistance(model.Nodes, MinCenterDistance);
    }

    [Fact]
    public void DeepOuChain_OneNodePerRing_RadiiMonotonicAndKeepRingGap()
    {
        const string rootDn = "OU=D0,DC=lab";
        var snapshot = new DirectorySnapshot();
        snapshot.AddObject(Obj(rootDn, AdObjectKind.OrganizationalUnit));
        var dn = rootDn;
        for (var depth = 1; depth <= 8; depth++)
        {
            dn = $"OU=D{depth},{dn}";
            snapshot.AddObject(Obj(dn, AdObjectKind.OrganizationalUnit));
        }

        var model = GraphBuilder.Build(snapshot, rootDn);

        Assert.Equal(9, model.Nodes.Count);
        Assert.Equal(
            Enumerable.Range(0, 9),
            model.Nodes.Select(n => n.Ring).OrderBy(r => r).ToList()); // one ring per depth

        AssertRingRadiiMonotonicWithGap(model);
        GeometryAssert.MinPairwiseCenterDistance(model.Nodes, MinCenterDistance);
    }

    // --- Helpers ----------------------------------------------------------------------

    /// <summary>Distance of a node from the origin (the root sits at exactly (0,0)).</summary>
    private static double Radius(GraphNode node) => Math.Sqrt((node.X * node.X) + (node.Y * node.Y));

    /// <summary>Groups nodes by ring index and asserts that the radius bands of
    /// consecutive rings are separated by at least the ring gap (so radii are
    /// also strictly monotonically increasing per ring index).</summary>
    private static void AssertRingRadiiMonotonicWithGap(GraphModel model)
    {
        var bands = model.Nodes
            .GroupBy(n => n.Ring)
            .OrderBy(g => g.Key)
            .Select(g => (Ring: g.Key, Min: g.Min(Radius), Max: g.Max(Radius)))
            .ToList();

        Assert.True(bands.Count >= 2, "expected at least two occupied rings");
        for (var i = 1; i < bands.Count; i++)
        {
            Assert.True(
                bands[i].Min >= bands[i - 1].Max + RingGap - Tolerance,
                $"ring {bands[i].Ring} starts at radius {bands[i].Min:F1}, " +
                $"ring {bands[i - 1].Ring} ends at {bands[i - 1].Max:F1} — gap < {RingGap}");
        }
    }

    private static DirectorySnapshot RootPlusUsers(string rootDn, int userCount)
    {
        var snapshot = new DirectorySnapshot();
        snapshot.AddObject(Obj(rootDn, AdObjectKind.OrganizationalUnit));
        for (var i = 1; i <= userCount; i++)
        {
            snapshot.AddObject(Obj($"CN=User {i:D3},{rootDn}", AdObjectKind.User));
        }

        return snapshot;
    }

    private static AdObject Obj(string dn, AdObjectKind kind) => new()
    {
        Dn = dn,
        Kind = kind,
        Name = dn,
    };
}

/// <summary>
/// Shared O(n²) geometry assertion — also used by the live-AD graph tests.
/// </summary>
internal static class GeometryAssert
{
    /// <summary>Asserts every pair of node centers is at least
    /// <paramref name="minimum"/> apart, naming the first offending pair.</summary>
    internal static void MinPairwiseCenterDistance(IReadOnlyList<GraphNode> nodes, double minimum)
    {
        for (var i = 0; i < nodes.Count; i++)
        {
            for (var j = i + 1; j < nodes.Count; j++)
            {
                var dx = nodes[i].X - nodes[j].X;
                var dy = nodes[i].Y - nodes[j].Y;
                var distance = Math.Sqrt((dx * dx) + (dy * dy));
                Assert.True(
                    distance >= minimum,
                    $"nodes '{nodes[i].Dn}' and '{nodes[j].Dn}' are only {distance:F1} apart (< {minimum})");
            }
        }
    }
}
