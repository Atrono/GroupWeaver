using System.Runtime.Versioning;

using GroupWeaver.Core.Graph;
using GroupWeaver.Core.Model;
using GroupWeaver.Tests.Core.Graph;

using Xunit;

namespace GroupWeaver.Tests.Providers.Ldap;

/// <summary>
/// AP 2.3 lazy expand against the LIVE AGDLP-Lab fixtures, pinned at the
/// provider + snapshot + builder level (the layer below <c>WorkspaceViewModel</c>;
/// the VM pipeline itself is pinned offline in
/// <c>tests/GroupWeaver.App.Tests/WorkspaceExpandTests.cs</c>): each test drives the
/// exact ADR-005 D3 fetch sequence — <c>GetObjectAsync</c> (only for a frontier DN
/// missing from <c>Snapshot.Objects</c>), <c>GetMembersAsync</c>, THEN
/// <c>AddObject</c>(s) + <c>SetMembers</c>, rebuild — over GROUP-ROOTED scopes,
/// where every member starts as an External frontier node (a group has no DN
/// children, so <c>LoadScopeAsync</c> on a group DN yields exactly that group).
/// Fixture DNs and member lists below were verified read-only against the live DC
/// (<c>Get-ADGroup/Get-ADObject -Server localhost</c>) before pinning. Excluded in
/// CI via the class-level <c>Category=RequiresAd</c> trait; skipped with a loud
/// warning off the lab DC via <see cref="AdFactAttribute"/>.
/// </summary>
[SupportedOSPlatform("windows")]
[Trait(TestCategories.Category, TestCategories.RequiresAd)]
public class LazyExpandLiveAdTests : IClassFixture<LdapLabFixture>
{
    private const string DlFsSalesRwDn = "CN=DL_FS-Sales_RW,OU=Groups,OU=AGDLP-Lab,DC=agdlp,DC=lab";
    private const string GgSalesStaffDn = "CN=GG_Sales_Staff,OU=Groups,OU=AGDLP-Lab,DC=agdlp,DC=lab";
    private const string User001Dn = "CN=Anna Acker (u001),OU=Users,OU=AGDLP-Lab,DC=agdlp,DC=lab";
    private const string CircleADn = "CN=GG_Circle_A,OU=Groups,OU=AGDLP-Lab,DC=agdlp,DC=lab";
    private const string CircleBDn = "CN=GG_Circle_B,OU=Groups,OU=AGDLP-Lab,DC=agdlp,DC=lab";
    private const string DlAppErpRwDn = "CN=DL_App-ERP_RW,OU=Groups,OU=AGDLP-Lab,DC=agdlp,DC=lab";
    private const string UgManagersDn = "CN=UG_Managers,OU=Groups,OU=AGDLP-Lab,DC=agdlp,DC=lab";

    /// <summary>Fixed dangling cross-forest SID seeded by Ensure-ForeignSidMember;
    /// the DC system-created its FSP OUTSIDE the lab OU (see
    /// <see cref="LdapProviderIntegrationTests"/> T13/T14).</summary>
    private const string ForeignSid = "S-1-5-21-1100000001-2200000002-3300000003-1106";

    private const string ForeignFspDn =
        "CN=" + ForeignSid + ",CN=ForeignSecurityPrincipals,DC=agdlp,DC=lab";

    private readonly LdapLabFixture _fixture;

    public LazyExpandLiveAdTests(LdapLabFixture fixture) => _fixture = fixture;

    // --- (a) group-rooted scope: expand a member GG, frontier kind resolves --------------

    [AdFact]
    public async Task GroupRootedScope_ExpandMemberGg_ResolvesFrontierKind_GraphGrows_GeometryHolds()
    {
        // LoadScopeAsync on the DL group DN: the group alone is in scope (a group
        // has no DN children), loaded with its 2 member DNs — GG_Sales_Staff plus
        // the seeded AGDLP-violation direct user (verified live).
        var snapshot = await _fixture.Provider.LoadScopeAsync(DlFsSalesRwDn);

        var only = Assert.Single(snapshot.Objects);
        Assert.Equal(AdObjectKind.DomainLocalGroup, only.Kind);
        Assert.True(snapshot.IsLoaded(DlFsSalesRwDn));
        var memberDns = snapshot.GetMembers(DlFsSalesRwDn);
        Assert.NotNull(memberDns);
        Assert.Equal(2, memberDns.Count);
        Assert.Contains(GgSalesStaffDn, memberDns, Dn.Comparer);
        Assert.Contains(User001Dn, memberDns, Dn.Comparer);

        // Both members lie outside the loaded scope: not in Objects, kind External,
        // never loaded — exactly the frontier the expand gesture fetches at.
        Assert.False(snapshot.TryGetObject(GgSalesStaffDn, out _));
        Assert.Equal(AdObjectKind.External, snapshot.GetKind(GgSalesStaffDn));
        Assert.False(snapshot.IsLoaded(GgSalesStaffDn));

        var before = GraphBuilder.Build(snapshot, DlFsSalesRwDn);
        Assert.Equal(3, before.Nodes.Count); // DL root + 2 materialized frontier nodes
        var frontier = Assert.Single(before.Nodes, n => Dn.Comparer.Equals(n.Dn, GgSalesStaffDn));
        Assert.Equal(AdObjectKind.External, frontier.Kind);

        // The ADR-005 D3 fetch sequence for the GG frontier: GetObjectAsync (DN is
        // missing from Objects → resolve the true kind), GetMembersAsync, THEN apply.
        var resolved = await _fixture.Provider.GetObjectAsync(GgSalesStaffDn);
        Assert.NotNull(resolved);
        Assert.Equal(AdObjectKind.GlobalGroup, resolved.Kind);
        Assert.Equal("GG_Sales_Staff", resolved.Name);

        var fetched = await _fixture.Provider.GetMembersAsync(GgSalesStaffDn);
        Assert.Equal(20, fetched.Count); // the seeded sales staff, verified live
        Assert.All(fetched, m => Assert.Equal(AdObjectKind.User, m.Kind));
        Assert.Contains(fetched, m => Dn.Comparer.Equals(m.Dn, User001Dn));

        snapshot.AddObject(resolved);
        foreach (var member in fetched)
        {
            snapshot.AddObject(member);
        }

        snapshot.SetMembers(GgSalesStaffDn, fetched.Select(m => m.Dn).ToList());

        // The frontier node resolved from External to its TRUE kind (ADR-005 D5),
        // and so did u001 — it was the DL's other frontier member all along.
        Assert.Equal(AdObjectKind.GlobalGroup, snapshot.GetKind(GgSalesStaffDn));
        Assert.True(snapshot.IsLoaded(GgSalesStaffDn));
        Assert.Equal(AdObjectKind.User, snapshot.GetKind(User001Dn));

        // Rebuilt model: 22 nodes (DL + GG + 20 users; u001 is one of the 20) — the
        // graph GREW — with the GG drawn in its true kind and every membership edge
        // present; the no-overlap geometry invariant holds after the expand.
        var after = GraphBuilder.Build(snapshot, DlFsSalesRwDn);
        Assert.Equal(22, after.Nodes.Count);
        Assert.True(after.Nodes.Count > before.Nodes.Count, "the expand must grow the graph");
        var ggNode = Assert.Single(after.Nodes, n => Dn.Comparer.Equals(n.Dn, GgSalesStaffDn));
        Assert.Equal(AdObjectKind.GlobalGroup, ggNode.Kind);
        var membership = after.Edges.Where(e => e.Kind == GraphEdgeKind.Membership).ToList();
        Assert.Equal(22, membership.Count); // DL→{GG, u001} + GG→20 users
        Assert.Contains(membership, e =>
            Dn.Comparer.Equals(e.ParentDn, GgSalesStaffDn)
            && Dn.Comparer.Equals(e.ChildDn, User001Dn));
        GeometryAssert.MinPairwiseCenterDistance(after.Nodes, 44d);
    }

    // --- (b) the seeded cycle, group-rooted: expand, cache hit, refresh — terminates ------

    [AdFact(Timeout = 60_000)]
    public async Task CircularPair_GroupRooted_ExpandCacheHitRefresh_Terminates_BothDirectionsDrawn()
    {
        // Group-rooted at GG_Circle_A: A alone in scope, loaded with [B] (verified
        // live: the cycle is a mutual single membership); B is the External frontier.
        var snapshot = await _fixture.Provider.LoadScopeAsync(CircleADn);

        var only = Assert.Single(snapshot.Objects);
        Assert.True(Dn.Comparer.Equals(CircleADn, only.Dn), $"unexpected object in scope: '{only.Dn}'");
        Assert.True(snapshot.IsLoaded(CircleADn));
        var initialMembers = snapshot.GetMembers(CircleADn);
        Assert.NotNull(initialMembers);
        var initialMember = Assert.Single(initialMembers);
        Assert.True(Dn.Comparer.Equals(CircleBDn, initialMember), $"unexpected member: '{initialMember}'");
        Assert.Equal(AdObjectKind.External, snapshot.GetKind(CircleBDn));

        // Expand B (fetch — one level, no traversal): the member walk lands back on
        // A, live-resolved to its true kind, and must NOT recurse any further.
        var resolvedB = await _fixture.Provider.GetObjectAsync(CircleBDn);
        Assert.NotNull(resolvedB);
        Assert.Equal(AdObjectKind.GlobalGroup, resolvedB.Kind);
        var membersOfB = await _fixture.Provider.GetMembersAsync(CircleBDn);
        var memberOfB = Assert.Single(membersOfB);
        Assert.True(Dn.Comparer.Equals(CircleADn, memberOfB.Dn), $"unexpected member: '{memberOfB.Dn}'");
        Assert.Equal(AdObjectKind.GlobalGroup, memberOfB.Kind);

        snapshot.AddObject(resolvedB);
        snapshot.AddObject(memberOfB); // upsert of the already-present A
        snapshot.SetMembers(CircleBDn, [memberOfB.Dn]);

        // Expand A: a cache HIT at snapshot level — LoadScopeAsync already marked A
        // loaded, so the pipeline (ADR-005 D3) answers from exactly this state with
        // zero provider traffic: kind fetchable, IsLoaded true, members cached.
        Assert.True(snapshot.IsLoaded(CircleADn));
        var cached = snapshot.GetMembers(CircleADn);
        Assert.NotNull(cached);
        Assert.True(Dn.Comparer.Equals(CircleBDn, Assert.Single(cached)));

        // Refresh A (forced fetch, ADR-005 D4): SetMembers REPLACES — the member
        // list stays exactly [B], never merged or duplicated across the cycle.
        var refreshed = await _fixture.Provider.GetMembersAsync(CircleADn);
        var refreshedMember = Assert.Single(refreshed);
        Assert.True(Dn.Comparer.Equals(CircleBDn, refreshedMember.Dn));
        snapshot.AddObject(refreshedMember);
        snapshot.SetMembers(CircleADn, [refreshedMember.Dn]);

        var afterRefresh = snapshot.GetMembers(CircleADn);
        Assert.NotNull(afterRefresh);
        Assert.True(Dn.Comparer.Equals(CircleBDn, Assert.Single(afterRefresh)));

        // The built model terminates (Timeout guard) with BOTH membership edge
        // directions of the cycle present — the permanent traversal guard.
        var model = await Task.Run(() => GraphBuilder.Build(snapshot, CircleADn));
        Assert.Equal(2, model.Nodes.Count);
        var membership = model.Edges
            .Where(e => e.Kind == GraphEdgeKind.Membership)
            .Select(e => new MembershipEdge(e.ParentDn, e.ChildDn))
            .ToList();
        Assert.Equal(2, membership.Count);
        Assert.Contains(new MembershipEdge(CircleADn, CircleBDn), membership);
        Assert.Contains(new MembershipEdge(CircleBDn, CircleADn), membership);
        GeometryAssert.MinPairwiseCenterDistance(model.Nodes, 44d);
    }

    // --- (c) FSP frontier expand: empty member list → loaded-and-empty, node retained ----

    [AdFact]
    public async Task FspFrontierExpand_EmptyMemberList_MarksLoadedAndEmpty_NodeRetained()
    {
        // Group-rooted at DL_App-ERP_RW: loaded with [FSP, UG_Managers] (verified
        // live); the FSP lives in CN=ForeignSecurityPrincipals, outside the lab OU.
        var snapshot = await _fixture.Provider.LoadScopeAsync(DlAppErpRwDn);

        var only = Assert.Single(snapshot.Objects);
        Assert.Equal(AdObjectKind.DomainLocalGroup, only.Kind);
        var memberDns = snapshot.GetMembers(DlAppErpRwDn);
        Assert.NotNull(memberDns);
        Assert.Equal(2, memberDns.Count);
        Assert.Contains(ForeignFspDn, memberDns, Dn.Comparer);
        Assert.Contains(UgManagersDn, memberDns, Dn.Comparer);
        Assert.Equal(AdObjectKind.External, snapshot.GetKind(ForeignFspDn));
        Assert.False(snapshot.IsLoaded(ForeignFspDn));

        // Expand the FSP frontier: GetObjectAsync resolves it LIVE (objectClass
        // foreignSecurityPrincipal → External stays its TRUE kind; Name = the SID
        // proves live resolution, not the MakeExternal fallback) and GetMembersAsync
        // answers with the empty list — an FSP has no members.
        var resolved = await _fixture.Provider.GetObjectAsync(ForeignFspDn);
        Assert.NotNull(resolved);
        Assert.Equal(AdObjectKind.External, resolved.Kind);
        Assert.Equal(ForeignSid, resolved.Name);

        var fetched = await _fixture.Provider.GetMembersAsync(ForeignFspDn);
        Assert.NotNull(fetched);
        Assert.Empty(fetched);

        snapshot.AddObject(resolved);
        snapshot.SetMembers(ForeignFspDn, fetched.Select(m => m.Dn).ToList());

        // Loaded-and-empty, never null (data-model rule) — the AP 3.2 reading.
        Assert.True(snapshot.IsLoaded(ForeignFspDn));
        var members = snapshot.GetMembers(ForeignFspDn);
        Assert.NotNull(members);
        Assert.Empty(members);

        // The node is RETAINED in the rebuilt model — now an in-scope snapshot
        // object (labelled by Name, the SID, instead of the raw frontier DN) — and
        // the DL→FSP membership edge survives untouched.
        var model = GraphBuilder.Build(snapshot, DlAppErpRwDn);
        Assert.Equal(3, model.Nodes.Count); // DL root + FSP + the UG_Managers frontier
        var fspNode = Assert.Single(model.Nodes, n => Dn.Comparer.Equals(n.Dn, ForeignFspDn));
        Assert.Equal(AdObjectKind.External, fspNode.Kind);
        Assert.Equal(ForeignSid, fspNode.Label);
        Assert.Contains(model.Edges, e =>
            e.Kind == GraphEdgeKind.Membership
            && Dn.Comparer.Equals(e.ParentDn, DlAppErpRwDn)
            && Dn.Comparer.Equals(e.ChildDn, ForeignFspDn));
    }
}
