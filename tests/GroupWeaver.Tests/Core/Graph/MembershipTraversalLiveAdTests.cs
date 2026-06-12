using System.Runtime.Versioning;

using GroupWeaver.Core.Graph;
using GroupWeaver.Core.Model;
using GroupWeaver.Tests.Providers.Ldap;

using Xunit;

namespace GroupWeaver.Tests.Core.Graph;

/// <summary>
/// <c>MembershipTraversal.Walk</c> (ADR-006, AP 2.4) against the LIVE AGDLP-Lab
/// fixtures (Definition of Done step 3): over the full lab scope the walk
/// terminates on the seeded GG_Circle_A↔GG_Circle_B cycle (Timeout guard) and
/// reports it as a value; over a GROUP-ROOTED scope the very same cycle stays
/// invisible while GG_Circle_B is a never-loaded frontier node, and is reported
/// only after the ADR-005 D3 expand choreography (GetObjectAsync →
/// GetMembersAsync → AddObject(s) + SetMembers, mirroring
/// <see cref="LazyExpandLiveAdTests"/>) loads B — the AP 3.4 "unexpanded areas
/// unchecked" reading. Member lists were verified read-only against the live DC
/// (<c>Get-ADGroup -Server localhost -Properties member</c>: A holds exactly [B],
/// B holds exactly [A]) before pinning. Excluded in CI via the class-level
/// <c>Category=RequiresAd</c> trait; skipped with a loud warning off the lab DC
/// via <see cref="AdFactAttribute"/>.
/// </summary>
[SupportedOSPlatform("windows")]
[Trait(TestCategories.Category, TestCategories.RequiresAd)]
public class MembershipTraversalLiveAdTests : IClassFixture<LdapLabFixture>
{
    private const string CircleADn = "CN=GG_Circle_A,OU=Groups,OU=AGDLP-Lab,DC=agdlp,DC=lab";
    private const string CircleBDn = "CN=GG_Circle_B,OU=Groups,OU=AGDLP-Lab,DC=agdlp,DC=lab";

    private readonly LdapLabFixture _fixture;

    public MembershipTraversalLiveAdTests(LdapLabFixture fixture) => _fixture = fixture;

    [AdFact(Timeout = 60_000)]
    public async Task Walk_Lab_FullScope_FromCircleA_ReportsCycle_Terminates()
    {
        var walk = await Task.Run(
            () => MembershipTraversal.Walk(_fixture.FullSnapshot, CircleADn));

        Assert.Equal(new[] { CircleADn, CircleBDn }, walk.Visited, Dn.Comparer);
        var cycle = Assert.Single(walk.Cycles);
        Assert.Equal(new[] { CircleADn, CircleBDn }, cycle, Dn.Comparer);
        Assert.Empty(walk.Frontier); // full scope: both circle groups are loaded
    }

    [AdFact(Timeout = 60_000)]
    public async Task Walk_Lab_GroupRootedScope_CycleHiddenBehindFrontier_ThenReportedAfterExpand()
    {
        // Group-rooted at GG_Circle_A: A alone in scope, loaded with [B]; B is not
        // in Objects (kind falls back to External) and was never loaded.
        var snapshot = await _fixture.Provider.LoadScopeAsync(CircleADn);
        Assert.Equal(AdObjectKind.External, snapshot.GetKind(CircleBDn));
        Assert.False(snapshot.IsLoaded(CircleBDn));

        // The cycle exists in the DIRECTORY but not in the loaded data: the walk
        // must report NO cycle and flag B as the unexpanded frontier instead.
        var before = MembershipTraversal.Walk(snapshot, CircleADn);
        Assert.Equal(new[] { CircleADn, CircleBDn }, before.Visited, Dn.Comparer);
        Assert.Empty(before.Cycles);
        var frontierDn = Assert.Single(before.Frontier);
        Assert.True(Dn.Comparer.Equals(CircleBDn, frontierDn), $"frontier was '{frontierDn}'");

        // Expand B per ADR-005 D3: GetObjectAsync (B is missing from Objects),
        // GetMembersAsync, THEN AddObject(s) + SetMembers — the exact
        // LazyExpandLiveAdTests choreography.
        var resolvedB = await _fixture.Provider.GetObjectAsync(CircleBDn);
        Assert.NotNull(resolvedB);
        Assert.Equal(AdObjectKind.GlobalGroup, resolvedB.Kind);

        var membersOfB = await _fixture.Provider.GetMembersAsync(CircleBDn);
        var memberOfB = Assert.Single(membersOfB);
        Assert.True(Dn.Comparer.Equals(CircleADn, memberOfB.Dn), $"unexpected member: '{memberOfB.Dn}'");

        snapshot.AddObject(resolvedB);
        snapshot.AddObject(memberOfB); // upsert of the already-present A
        snapshot.SetMembers(CircleBDn, [memberOfB.Dn]);

        // The SAME walk over the expanded snapshot now sees the closing edge:
        // the cycle [A, B] is reported and the frontier is gone.
        var after = await Task.Run(() => MembershipTraversal.Walk(snapshot, CircleADn));
        Assert.Equal(new[] { CircleADn, CircleBDn }, after.Visited, Dn.Comparer);
        var cycle = Assert.Single(after.Cycles);
        Assert.Equal(new[] { CircleADn, CircleBDn }, cycle, Dn.Comparer);
        Assert.Empty(after.Frontier);
    }
}
