using System.Runtime.Versioning;

using GroupWeaver.Core.Graph;
using GroupWeaver.Core.Model;
using GroupWeaver.Tests.Providers.Ldap;

using Xunit;

namespace GroupWeaver.Tests.Core.Graph;

/// <summary>
/// GraphBuilder over the LIVE AGDLP-Lab fixtures (Definition of Done step 3):
/// the build must terminate despite the seeded GG_Circle_A↔GG_Circle_B cycle
/// (Timeout guard), stay total (every snapshot object becomes exactly one
/// node, &gt;= 195), keep both cycle directions, materialize the out-of-OU FSP
/// edge target as External (no edge is ever dropped), and hold the no-overlap
/// geometry. Excluded in CI via the class-level <c>Category=RequiresAd</c>
/// trait; skipped with a loud warning off the lab DC via <see cref="AdFactAttribute"/>.
/// </summary>
[SupportedOSPlatform("windows")]
[Trait(TestCategories.Category, TestCategories.RequiresAd)]
public class GraphBuilderLiveAdTests : IClassFixture<LdapLabFixture>
{
    private const string LabDn = LdapLabFixture.LabDn;
    private const string CircleADn = "CN=GG_Circle_A,OU=Groups,OU=AGDLP-Lab,DC=agdlp,DC=lab";
    private const string CircleBDn = "CN=GG_Circle_B,OU=Groups,OU=AGDLP-Lab,DC=agdlp,DC=lab";

    /// <summary>The seed script's dangling cross-forest FSP — it lives in
    /// CN=ForeignSecurityPrincipals, OUTSIDE the lab OU, so it is an edge
    /// endpoint without a snapshot object (see LdapProviderIntegrationTests T14).</summary>
    private const string ForeignFspDn =
        "CN=S-1-5-21-1100000001-2200000002-3300000003-1106,CN=ForeignSecurityPrincipals,DC=agdlp,DC=lab";

    private readonly LdapLabFixture _fixture;

    public GraphBuilderLiveAdTests(LdapLabFixture fixture) => _fixture = fixture;

    [AdFact(Timeout = 60_000)]
    public async Task Build_LabScope_TerminatesDespiteCycle_BothDirectionsKept()
    {
        var model = await Task.Run(() => GraphBuilder.Build(_fixture.FullSnapshot, LabDn));

        var membership = model.Edges
            .Where(e => e.Kind == GraphEdgeKind.Membership)
            .Select(e => new MembershipEdge(e.ParentDn, e.ChildDn))
            .ToList();
        Assert.Contains(new MembershipEdge(CircleADn, CircleBDn), membership);
        Assert.Contains(new MembershipEdge(CircleBDn, CircleADn), membership);
    }

    [AdFact]
    public void Build_LabScope_EverySnapshotObjectIsExactlyOneNode()
    {
        var snapshot = _fixture.FullSnapshot;

        var model = GraphBuilder.Build(snapshot, LabDn);

        Assert.True(
            model.Nodes.Count >= 195,
            $"expected >= 195 nodes for the lab scope, got {model.Nodes.Count}");

        var distinctDns = model.Nodes.Select(n => n.Dn).ToHashSet(Dn.Comparer);
        Assert.Equal(model.Nodes.Count, distinctDns.Count); // no duplicates under Dn.Comparer
        foreach (var obj in snapshot.Objects)
        {
            Assert.Contains(obj.Dn, distinctDns);
        }
    }

    [AdFact]
    public void Build_LabScope_FspEdgeTarget_MaterializedAsExternalNode()
    {
        var model = GraphBuilder.Build(_fixture.FullSnapshot, LabDn);

        // The DL_App-ERP_RW → FSP edge points outside the lab OU; its endpoint
        // must still become a node (edges are never dropped, ADR-004).
        var fsp = Assert.Single(model.Nodes, n => Dn.Comparer.Equals(n.Dn, ForeignFspDn));
        Assert.Equal(AdObjectKind.External, fsp.Kind);
    }

    [AdFact]
    public void Build_LabScope_GeometryHolds_MinCenterDistanceAtLeast44()
    {
        var model = GraphBuilder.Build(_fixture.FullSnapshot, LabDn);

        GeometryAssert.MinPairwiseCenterDistance(model.Nodes, 44d);
    }
}
