using GroupWeaver.Core.Graph;
using GroupWeaver.Core.Model;
using GroupWeaver.Tests.Providers;

using Xunit;

namespace GroupWeaver.Tests.Core.Graph;

/// <summary>
/// Pins the GraphBuilder semantic contract (ADR-004, AP 2.2 S2) over the real
/// DemoProvider dataset plus hand-built snapshots: ring assignment
/// (relative DN depth below root, then kind rank OU=0 DL=1 UG=2 GG=3
/// Computer=4 User=5, indices consecutive, External on one dedicated outermost
/// ring), totality (every snapshot object AND every edge endpoint becomes
/// exactly one node — no edge is ever dropped), nearest-ancestor containment
/// edges, determinism, and DN keying via <see cref="Dn.Comparer"/>.
/// </summary>
/// <remarks>
/// Demo dataset shape relied on below (pinned by <c>DemoProviderTests</c>):
/// the root OU at depth 0; the 3 other OUs at depth 1; all 140 users, 40
/// groups and 10 computers at depth 2; exactly two external member DNs
/// (Domain Admins, Print Operators); 141 membership edges. Non-empty ring keys
/// sorted ascending therefore map to: 0=root, 1=depth-1 OUs, 2=DL, 3=UG,
/// 4=GG, 5=Computer, 6=User, 7=External.
/// </remarks>
public class GraphBuilderTests : IClassFixture<DemoProviderFixture>
{
    private const string RootDn = DemoProviderFixture.RootDn;
    private const string UsersOuDn = "OU=Users,OU=AGDLP-Demo,DC=weavedemo,DC=example";
    private const string GroupsOuDn = "OU=Groups,OU=AGDLP-Demo,DC=weavedemo,DC=example";
    private const string CircleADn = "CN=GG_Circle_A,OU=Groups,OU=AGDLP-Demo,DC=weavedemo,DC=example";
    private const string CircleBDn = "CN=GG_Circle_B,OU=Groups,OU=AGDLP-Demo,DC=weavedemo,DC=example";
    private const string User001Dn = "CN=Anna Acker (u001),OU=Users,OU=AGDLP-Demo,DC=weavedemo,DC=example";
    private const string DomainAdminsDn = "CN=Domain Admins,CN=Users,DC=weavedemo,DC=example";
    private const string PrintOperatorsDn = "CN=Print Operators,CN=Builtin,DC=weavedemo,DC=example";

    private readonly DemoProviderFixture _fixture;

    public GraphBuilderTests(DemoProviderFixture fixture) => _fixture = fixture;

    // --- Root pinning ---------------------------------------------------------

    [Fact]
    public void BuildDemo_RootIsAloneOnRingZero_AtExactOrigin_OnlyIsRoot()
    {
        var model = BuildDemo();

        var root = Assert.Single(model.Nodes, n => n.IsRoot);
        Assert.True(Dn.Comparer.Equals(RootDn, root.Dn), $"IsRoot node is '{root.Dn}'");
        Assert.Equal(0, root.Ring);
        Assert.Equal(0d, root.X);
        Assert.Equal(0d, root.Y);
        Assert.Equal(AdObjectKind.OrganizationalUnit, root.Kind);
        Assert.Equal("AGDLP-Demo", root.Label);

        var ringZero = Assert.Single(model.Nodes, n => n.Ring == 0);
        Assert.True(Dn.Comparer.Equals(RootDn, ringZero.Dn), "ring 0 must hold the root alone");
    }

    // --- Ring assignment --------------------------------------------------------

    [Fact]
    public void BuildDemo_RingIndicesAreConsecutiveFromZero()
    {
        var model = BuildDemo();

        var rings = model.Nodes.Select(n => n.Ring).Distinct().OrderBy(r => r).ToList();

        Assert.Equal(Enumerable.Range(0, 8), rings);
    }

    [Fact]
    public void BuildDemo_DepthOneOus_ShareRingOne()
    {
        var model = BuildDemo();

        var ous = model.Nodes
            .Where(n => n.Kind == AdObjectKind.OrganizationalUnit && !n.IsRoot)
            .ToList();

        Assert.Equal(3, ous.Count);
        Assert.All(ous, n => Assert.Equal(1, n.Ring));
    }

    [Theory]
    [InlineData(AdObjectKind.DomainLocalGroup, 2)]
    [InlineData(AdObjectKind.UniversalGroup, 3)]
    [InlineData(AdObjectKind.GlobalGroup, 4)]
    [InlineData(AdObjectKind.Computer, 5)]
    [InlineData(AdObjectKind.User, 6)]
    [InlineData(AdObjectKind.External, 7)]
    public void BuildDemo_KindRankOrdersTheDepthTwoRings(AdObjectKind kind, int expectedRing)
    {
        var model = BuildDemo();

        var nodes = model.Nodes.Where(n => n.Kind == kind).ToList();

        Assert.NotEmpty(nodes);
        Assert.All(nodes, n => Assert.Equal(expectedRing, n.Ring));
    }

    // --- External materialization -------------------------------------------------

    [Fact]
    public void BuildDemo_ExternalEdgeTargets_MaterializedOnDedicatedOutermostRing()
    {
        var model = BuildDemo();

        var externals = model.Nodes.Where(n => n.Kind == AdObjectKind.External).ToList();
        Assert.Equal(2, externals.Count);
        Assert.Contains(externals, n => Dn.Comparer.Equals(n.Dn, DomainAdminsDn));
        Assert.Contains(externals, n => Dn.Comparer.Equals(n.Dn, PrintOperatorsDn));

        var outermost = model.Nodes.Max(n => n.Ring);
        Assert.All(externals, n => Assert.Equal(outermost, n.Ring));
        Assert.All(
            model.Nodes.Where(n => n.Ring == outermost),
            n => Assert.Equal(AdObjectKind.External, n.Kind));
    }

    [Fact]
    public void BuildDemo_ExternalNodes_GetNoContainmentEdge()
    {
        var model = BuildDemo();

        var externalDns = model.Nodes
            .Where(n => n.Kind == AdObjectKind.External)
            .Select(n => n.Dn)
            .ToHashSet(Dn.Comparer);

        Assert.DoesNotContain(
            model.Edges,
            e => e.Kind == GraphEdgeKind.Containment && externalDns.Contains(e.ChildDn));
    }

    // --- Totality -------------------------------------------------------------------

    [Fact]
    public void BuildDemo_EverySnapshotObjectIsExactlyOneNode_196Total()
    {
        var model = BuildDemo();

        Assert.Equal(196, model.Nodes.Count); // 194 in-scope objects + 2 external endpoints

        var distinctDns = model.Nodes.Select(n => n.Dn).ToHashSet(Dn.Comparer);
        Assert.Equal(model.Nodes.Count, distinctDns.Count); // no duplicates under Dn.Comparer

        foreach (var obj in _fixture.FullSnapshot.Objects)
        {
            Assert.Contains(obj.Dn, distinctDns);
        }
    }

    [Fact]
    public void Build_OrphanUserEmptyGroupAndEmptyOu_AllGetPositions()
    {
        const string rootDn = "OU=Root,DC=lab";
        var snapshot = new DirectorySnapshot();
        snapshot.AddObject(Obj(rootDn, AdObjectKind.OrganizationalUnit));
        snapshot.AddObject(Obj("OU=Empty,OU=Root,DC=lab", AdObjectKind.OrganizationalUnit));
        snapshot.AddObject(Obj("CN=GG_Empty,OU=Root,DC=lab", AdObjectKind.GlobalGroup));
        snapshot.AddObject(Obj("CN=Orphan User,OU=Root,DC=lab", AdObjectKind.User));
        snapshot.SetMembers("CN=GG_Empty,OU=Root,DC=lab", []);

        var model = GraphBuilder.Build(snapshot, rootDn);

        Assert.Equal(4, model.Nodes.Count);
        Assert.All(model.Nodes, n => Assert.True(
            double.IsFinite(n.X) && double.IsFinite(n.Y),
            $"node '{n.Dn}' has no finite position ({n.X}, {n.Y})"));
    }

    // --- Missing root / group-as-root --------------------------------------------------

    [Fact]
    public void Build_RootDnMissingFromSnapshot_SynthesizesExternalRoot()
    {
        const string rootDn = "OU=Ghost,DC=lab";
        const string userDn = "CN=U1,OU=Ghost,DC=lab";
        var snapshot = new DirectorySnapshot();
        snapshot.AddObject(Obj(userDn, AdObjectKind.User));

        var model = GraphBuilder.Build(snapshot, rootDn);

        Assert.Equal(2, model.Nodes.Count);

        var root = Assert.Single(model.Nodes, n => n.IsRoot);
        Assert.True(Dn.Comparer.Equals(rootDn, root.Dn), $"IsRoot node is '{root.Dn}'");
        Assert.Equal(AdObjectKind.External, root.Kind);
        Assert.Equal(0, root.Ring);
        Assert.Equal(0d, root.X);
        Assert.Equal(0d, root.Y);
        Assert.Equal(rootDn, root.Label); // synthesized nodes are labeled with their DN

        var containment = Assert.Single(model.Edges, e => e.Kind == GraphEdgeKind.Containment);
        Assert.True(Dn.Comparer.Equals(rootDn, containment.ParentDn));
        Assert.True(Dn.Comparer.Equals(userDn, containment.ChildDn));
    }

    [Fact]
    public async Task Build_GroupAsRoot_MemberMaterializesExternal_GraphStaysTotal()
    {
        // LoadScopeAsync over the group DN yields a snapshot whose only object is
        // GG_Circle_A, members loaded — the A→B edge points out of scope.
        var snapshot = await _fixture.Provider.LoadScopeAsync(CircleADn);
        var model = GraphBuilder.Build(snapshot, CircleADn);

        Assert.Equal(2, model.Nodes.Count);

        var root = Assert.Single(model.Nodes, n => n.IsRoot);
        Assert.True(Dn.Comparer.Equals(CircleADn, root.Dn));
        Assert.Equal(AdObjectKind.GlobalGroup, root.Kind);

        var member = Assert.Single(model.Nodes, n => !n.IsRoot);
        Assert.True(Dn.Comparer.Equals(CircleBDn, member.Dn), $"member node is '{member.Dn}'");
        Assert.Equal(AdObjectKind.External, member.Kind);

        // The single edge is the membership edge; the External member gets no
        // containment edge and the root never gets one.
        var edge = Assert.Single(model.Edges);
        Assert.Equal(GraphEdgeKind.Membership, edge.Kind);
        Assert.True(Dn.Comparer.Equals(CircleADn, edge.ParentDn));
        Assert.True(Dn.Comparer.Equals(CircleBDn, edge.ChildDn));
    }

    // --- Containment edges ----------------------------------------------------------------

    [Fact]
    public void BuildDemo_ContainmentEdges_ExactlyOnePerNonRootInScopeNode()
    {
        var model = BuildDemo();

        var containment = model.Edges.Where(e => e.Kind == GraphEdgeKind.Containment).ToList();
        Assert.Equal(193, containment.Count); // 194 snapshot objects minus the root

        var childCounts = containment
            .GroupBy(e => e.ChildDn, Dn.Comparer)
            .ToDictionary(g => g.Key, g => g.Count(), Dn.Comparer);
        foreach (var obj in _fixture.FullSnapshot.Objects)
        {
            if (Dn.Comparer.Equals(obj.Dn, RootDn))
            {
                continue;
            }

            Assert.True(
                childCounts.TryGetValue(obj.Dn, out var count) && count == 1,
                $"expected exactly one containment edge with child '{obj.Dn}'");
        }
    }

    [Theory]
    [InlineData(User001Dn, UsersOuDn)]
    [InlineData(CircleADn, GroupsOuDn)]
    [InlineData(UsersOuDn, RootDn)]
    public void BuildDemo_ContainmentParent_IsNearestSnapshotAncestor(
        string childDn, string expectedParentDn)
    {
        var model = BuildDemo();

        var edge = Assert.Single(
            model.Edges,
            e => e.Kind == GraphEdgeKind.Containment && Dn.Comparer.Equals(e.ChildDn, childDn));
        Assert.True(
            Dn.Comparer.Equals(expectedParentDn, edge.ParentDn),
            $"containment parent of '{childDn}' was '{edge.ParentDn}'");
    }

    [Fact]
    public void Build_ContainmentSkipsDnGaps_ClimbsToNearestSnapshotAncestor()
    {
        const string rootDn = "OU=Root,DC=lab";
        const string userDn = "CN=U1,OU=Mid,OU=Root,DC=lab"; // OU=Mid is NOT in the snapshot
        var snapshot = new DirectorySnapshot();
        snapshot.AddObject(Obj(rootDn, AdObjectKind.OrganizationalUnit));
        snapshot.AddObject(Obj(userDn, AdObjectKind.User));

        var model = GraphBuilder.Build(snapshot, rootDn);

        var edge = Assert.Single(model.Edges, e => e.Kind == GraphEdgeKind.Containment);
        Assert.True(
            Dn.Comparer.Equals(rootDn, edge.ParentDn),
            $"expected the gap to be skipped up to the root, got parent '{edge.ParentDn}'");
        Assert.True(Dn.Comparer.Equals(userDn, edge.ChildDn));

        // The gap OU itself is materialized by NOTHING: only snapshot objects
        // and edge endpoints become nodes.
        Assert.Equal(2, model.Nodes.Count);
    }

    [Fact]
    public void Build_NonDescendantInScopeObject_ContainmentFallsBackToRoot()
    {
        const string rootDn = "OU=Root,DC=lab";
        const string strayDn = "CN=Stray,DC=elsewhere"; // shares no ancestry with the root
        var snapshot = new DirectorySnapshot();
        snapshot.AddObject(Obj(rootDn, AdObjectKind.OrganizationalUnit));
        snapshot.AddObject(Obj(strayDn, AdObjectKind.User));

        var model = GraphBuilder.Build(snapshot, rootDn);

        Assert.Contains(model.Nodes, n => Dn.Comparer.Equals(n.Dn, strayDn)); // totality
        var edge = Assert.Single(
            model.Edges,
            e => e.Kind == GraphEdgeKind.Containment && Dn.Comparer.Equals(e.ChildDn, strayDn));
        Assert.True(
            Dn.Comparer.Equals(rootDn, edge.ParentDn),
            $"containment fallback parent was '{edge.ParentDn}', expected the root");
    }

    // --- Membership edges -------------------------------------------------------------------

    [Fact]
    public void BuildDemo_MembershipEdges_EqualSnapshotEdges_NoneDropped()
    {
        var model = BuildDemo();

        var membership = MembershipPairs(model);

        Assert.Equal(141, membership.Count);
        Assert.Equal(_fixture.FullSnapshot.Edges.ToHashSet(), membership.ToHashSet());
    }

    [Fact(Timeout = 60_000)]
    public async Task BuildDemo_WithSeededCycle_Terminates_BothDirectionsKept()
    {
        // The demo dataset contains GG_Circle_A <-> GG_Circle_B; Build must
        // terminate (Timeout guard) and keep BOTH directions of the cycle.
        var model = await Task.Run(() => BuildDemo());

        var membership = MembershipPairs(model);
        Assert.Contains(new MembershipEdge(CircleADn, CircleBDn), membership);
        Assert.Contains(new MembershipEdge(CircleBDn, CircleADn), membership);
    }

    // --- Determinism ----------------------------------------------------------------------------

    [Fact]
    public void Build_Twice_YieldsIdenticalNodeAndEdgeSequences()
    {
        var first = BuildDemo();
        var second = BuildDemo();

        Assert.Equal(first.Nodes, second.Nodes);
        Assert.Equal(first.Edges, second.Edges);
    }

    [Fact]
    public void BuildDemo_Edges_SortedByKindThenParentThenChild_OrdinalIgnoreCase()
    {
        var edges = BuildDemo().Edges;

        var expected = edges
            .OrderBy(e => e.Kind)
            .ThenBy(e => e.ParentDn, StringComparer.OrdinalIgnoreCase)
            .ThenBy(e => e.ChildDn, StringComparer.OrdinalIgnoreCase)
            .ToList();

        Assert.Equal(expected, edges);
    }

    [Fact]
    public void BuildDemo_Coordinates_AreRoundedToOneTenth()
    {
        Assert.All(BuildDemo().Nodes, n =>
        {
            Assert.Equal(Math.Round(n.X, 1), n.X);
            Assert.Equal(Math.Round(n.Y, 1), n.Y);
        });
    }

    [Fact]
    public void Build_CaseTwistedMemberDn_KeysViaDnComparer_NoDuplicateNode()
    {
        const string rootDn = "OU=Root,DC=lab";
        const string ggADn = "CN=GG_A,OU=Root,DC=lab";
        const string ggBDn = "CN=GG_B,OU=Root,DC=lab";
        var snapshot = new DirectorySnapshot();
        snapshot.AddObject(Obj(rootDn, AdObjectKind.OrganizationalUnit));
        snapshot.AddObject(Obj(ggADn, AdObjectKind.GlobalGroup, "GG_A"));
        snapshot.AddObject(Obj(ggBDn, AdObjectKind.GlobalGroup, "GG_B"));
        snapshot.SetMembers(ggADn, []);
        snapshot.SetMembers(ggBDn, ["cn=gg_a,ou=root,dc=lab"]); // case-twisted reference to GG_A

        var model = GraphBuilder.Build(snapshot, rootDn);

        // The twisted member DN must key onto the existing GG_A node — no
        // duplicate, no phantom External.
        Assert.Equal(3, model.Nodes.Count);
        var ggA = Assert.Single(model.Nodes, n => Dn.Comparer.Equals(n.Dn, ggADn));
        Assert.Equal(AdObjectKind.GlobalGroup, ggA.Kind);

        var membership = Assert.Single(model.Edges, e => e.Kind == GraphEdgeKind.Membership);
        Assert.True(Dn.Comparer.Equals(ggBDn, membership.ParentDn));
        Assert.True(Dn.Comparer.Equals(ggADn, membership.ChildDn));
    }

    // --- Labels ------------------------------------------------------------------------------------

    [Theory]
    [InlineData(User001Dn, "Anna Acker (u001)")]
    [InlineData(CircleADn, "GG_Circle_A")]
    public void BuildDemo_NodeLabel_IsAdObjectName(string dn, string expectedLabel)
    {
        var model = BuildDemo();

        var node = Assert.Single(model.Nodes, n => Dn.Comparer.Equals(n.Dn, dn));
        Assert.Equal(expectedLabel, node.Label);
    }

    // --- Helpers -------------------------------------------------------------------------------------

    private GraphModel BuildDemo() => GraphBuilder.Build(_fixture.FullSnapshot, RootDn);

    private static List<MembershipEdge> MembershipPairs(GraphModel model) =>
        model.Edges
            .Where(e => e.Kind == GraphEdgeKind.Membership)
            .Select(e => new MembershipEdge(e.ParentDn, e.ChildDn))
            .ToList();

    private static AdObject Obj(string dn, AdObjectKind kind, string? name = null) => new()
    {
        Dn = dn,
        Kind = kind,
        Name = name ?? dn,
    };
}

/// <summary>
/// Pins the <see cref="GraphLayoutOptions"/> contract: ADR-004 defaults
/// (D=44, m=16, g=150) and ctor validation g &gt;= D + m.
/// </summary>
public class GraphLayoutOptionsTests
{
    [Fact]
    public void Defaults_MatchAdr004()
    {
        var options = new GraphLayoutOptions();

        Assert.Equal(44d, options.NodeDiameter);
        Assert.Equal(16d, options.NodeMargin);
        Assert.Equal(150d, options.RingGap);
    }

    [Fact]
    public void RingGapBelowDiameterPlusMargin_ThrowsArgumentException()
    {
        Assert.Throws<ArgumentException>(() => new GraphLayoutOptions(ringGap: 59.9));
    }

    [Fact]
    public void RingGapEqualToDiameterPlusMargin_IsAccepted()
    {
        var options = new GraphLayoutOptions(ringGap: 60);

        Assert.Equal(60d, options.RingGap);
    }
}
