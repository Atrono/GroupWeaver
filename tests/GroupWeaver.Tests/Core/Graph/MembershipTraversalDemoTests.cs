using GroupWeaver.Core.Graph;
using GroupWeaver.Core.Model;
using GroupWeaver.Tests.Providers;

using Xunit;

namespace GroupWeaver.Tests.Core.Graph;

/// <summary>
/// <c>MembershipTraversal.Walk</c> (ADR-006, AP 2.4) over the REAL DemoProvider
/// full-scope snapshot — the first-class offline test bed: the seeded
/// GG_Circle_A↔GG_Circle_B cycle is reported as a value while the walk terminates
/// (Timeout guard), and a group whose membership reaches out-of-scope endpoints
/// puts EXACTLY those External DNs into the frontier — the 20 in-scope users with
/// never-loaded members stay out of it (the ADR-006 frontier kind filter).
/// </summary>
/// <remarks>
/// Dataset shape relied on below is pinned by <c>DemoProviderTests</c>:
/// GG_Circle_A and GG_Circle_B hold exactly each other; DL_FS-IT_RW holds
/// [GG_IT_Staff, CN=Domain Admins,CN=Users,… (external)] in that stored order and
/// GG_IT_Staff holds 20 users; the full scope loads every group's members.
/// <c>DemoProviderTests.Violations_CircularNesting_BoundedMemberWalkTerminates</c>
/// deliberately stays independent of this utility — it pins the DATASET with its
/// own inline bounded walk and must not be rebased onto MembershipTraversal.
/// </remarks>
public class MembershipTraversalDemoTests : IClassFixture<DemoProviderFixture>
{
    private const string CircleADn = "CN=GG_Circle_A,OU=Groups,OU=AGDLP-Demo,DC=weavedemo,DC=example";
    private const string CircleBDn = "CN=GG_Circle_B,OU=Groups,OU=AGDLP-Demo,DC=weavedemo,DC=example";
    private const string DlFsItRwDn = "CN=DL_FS-IT_RW,OU=Groups,OU=AGDLP-Demo,DC=weavedemo,DC=example";
    private const string GgItStaffDn = "CN=GG_IT_Staff,OU=Groups,OU=AGDLP-Demo,DC=weavedemo,DC=example";
    private const string DomainAdminsDn = "CN=Domain Admins,CN=Users,DC=weavedemo,DC=example";

    private readonly DemoProviderFixture _fixture;

    public MembershipTraversalDemoTests(DemoProviderFixture fixture) => _fixture = fixture;

    [Fact(Timeout = 60_000)]
    public async Task Walk_FromSeededCircleA_OverFullDemoScope_TerminatesAndReportsTheCycle()
    {
        var walk = await Task.Run(
            () => MembershipTraversal.Walk(_fixture.FullSnapshot, CircleADn));

        // The cycle reaches no third object: visited is exactly {A, B} in preorder,
        // the single back edge B→A yields the single cycle path [A, B], and the
        // frontier is empty — the full scope loaded both circle groups.
        Assert.Equal(new[] { CircleADn, CircleBDn }, walk.Visited, Dn.Comparer);
        var cycle = Assert.Single(walk.Cycles);
        Assert.Equal(new[] { CircleADn, CircleBDn }, cycle, Dn.Comparer);
        Assert.Empty(walk.Frontier);
    }

    [Fact]
    public void Walk_FromAGroupReferencingExternalEndpoints_FrontierListsOnlyThose()
    {
        var walk = MembershipTraversal.Walk(_fixture.FullSnapshot, DlFsItRwDn);

        // Preorder over the stored member order: the DL, its GG subtree (20 users,
        // leaves), then the external endpoint last — 23 visits, no duplicates.
        Assert.Equal(23, walk.Visited.Count);
        Assert.True(Dn.Comparer.Equals(DlFsItRwDn, walk.Visited[0]), $"first visit was '{walk.Visited[0]}'");
        Assert.True(Dn.Comparer.Equals(GgItStaffDn, walk.Visited[1]), $"second visit was '{walk.Visited[1]}'");
        Assert.True(Dn.Comparer.Equals(DomainAdminsDn, walk.Visited[^1]), $"last visit was '{walk.Visited[^1]}'");
        Assert.Equal(walk.Visited.Count, walk.Visited.ToHashSet(Dn.Comparer).Count);

        // ONLY the out-of-scope endpoint is frontier: the 20 users also have
        // never-loaded members, but their kind is not fetchable (ADR-006 D2).
        var frontier = Assert.Single(walk.Frontier);
        Assert.True(Dn.Comparer.Equals(DomainAdminsDn, frontier), $"frontier was '{frontier}'");
        Assert.Empty(walk.Cycles);
    }
}
