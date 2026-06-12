using GroupWeaver.Core.Graph;
using GroupWeaver.Core.Model;

using Xunit;

namespace GroupWeaver.Tests.Core.Graph;

/// <summary>
/// Pins the <c>MembershipTraversal.Walk</c> contract (ADR-006, AP 2.4) over
/// hand-built snapshots. The walk is a pure, iterative DFS down the membership
/// digraph, children in stored SetMembers order, adjacency via
/// <see cref="DirectorySnapshot.GetMembers"/> (null-vs-empty tri-state — never
/// <see cref="DirectorySnapshot.Edges"/>), returning
/// <c>MembershipWalk(Visited, Cycles, Frontier)</c>:
/// <list type="bullet">
/// <item><c>Visited</c> — every DN reached, startDn first, DFS preorder, no
/// duplicates under <see cref="Dn.Comparer"/>, DN strings as FIRST encountered
/// (never canonicalized) — pinned with ordinal equality below.</item>
/// <item><c>Cycles</c> — one entry per back edge u→v with v on the current DFS
/// path (white/gray/black coloring keyed via <see cref="Dn.Comparer"/>): the
/// path slice [v..u], closing edge implied last→first; self-membership is a
/// single-element path; diamond reconvergence is NOT a cycle.</item>
/// <item><c>Frontier</c> — subset of Visited in visit order whose
/// <c>GetMembers == null</c> AND kind (via <see cref="DirectorySnapshot.GetKind"/>,
/// unknown → External) is in ADR-005's fetchable set {GG, DL, UG, External};
/// loaded-and-empty and user/computer/OU leaves are never frontier.</item>
/// </list>
/// Termination on cyclic input is proven by Timeout guards plus an independent
/// visit-count bound (≤ V+E+1) — never by trusting the implementation (ADR-006 D4).
/// </summary>
public class MembershipTraversalTests
{
    private const string ADn = "CN=GG_A,OU=Walk,DC=lab";
    private const string BDn = "CN=GG_B,OU=Walk,DC=lab";
    private const string CDn = "CN=GG_C,OU=Walk,DC=lab";
    private const string DDn = "CN=GG_D,OU=Walk,DC=lab";
    private const string EDn = "CN=GG_E,OU=Walk,DC=lab";
    private const string XDn = "CN=GG_X,OU=Walk,DC=lab";

    // --- Visit order ------------------------------------------------------------

    [Fact]
    public void Walk_LinearLoadedChain_VisitsPreorder_NoCyclesNoFrontier()
    {
        var snapshot = SnapshotOfGroups(ADn, BDn, CDn);
        snapshot.SetMembers(ADn, [BDn]);
        snapshot.SetMembers(BDn, [CDn]);
        snapshot.SetMembers(CDn, []); // loaded and genuinely empty

        var walk = MembershipTraversal.Walk(snapshot, ADn);

        Assert.Equal(new[] { ADn, BDn, CDn }, walk.Visited);
        Assert.Empty(walk.Cycles);
        Assert.Empty(walk.Frontier);
    }

    // --- Cycles as values ---------------------------------------------------------

    [Fact(Timeout = 60_000)]
    public async Task Walk_TwoNodeCycle_Terminates_ReportsExactlyOneCyclePath()
    {
        var snapshot = SnapshotOfGroups(ADn, BDn);
        snapshot.SetMembers(ADn, [BDn]);
        snapshot.SetMembers(BDn, [ADn]);

        var walk = await Task.Run(() => MembershipTraversal.Walk(snapshot, ADn));

        Assert.Equal(new[] { ADn, BDn }, walk.Visited);
        var cycle = Assert.Single(walk.Cycles);
        Assert.Equal(new[] { ADn, BDn }, cycle); // closing edge B→A implied last→first
        Assert.Empty(walk.Frontier);
    }

    [Fact(Timeout = 60_000)]
    public async Task Walk_CycleClosingEdgeWithCaseDifferingDn_IsDetectedViaDnComparer()
    {
        const string twistedADn = "cn=gg_a,ou=walk,dc=lab"; // case-variant of ADn
        var snapshot = SnapshotOfGroups(ADn, BDn);
        snapshot.SetMembers(ADn, [BDn]);
        snapshot.SetMembers(BDn, [twistedADn]);

        var walk = await Task.Run(() => MembershipTraversal.Walk(snapshot, ADn));

        // The case-variant closing edge keys onto A via Dn.Comparer — exactly one
        // cycle — and every reported DN string is the FIRST-encountered one: the
        // ordinal (case-sensitive) pins below reject the twisted variant.
        Assert.Equal(new[] { ADn, BDn }, walk.Visited);
        var cycle = Assert.Single(walk.Cycles);
        Assert.Equal(new[] { ADn, BDn }, cycle);
        Assert.Empty(walk.Frontier);
    }

    [Fact(Timeout = 60_000)]
    public async Task Walk_SelfMembership_ReportsSingleElementCycle()
    {
        var snapshot = SnapshotOfGroups(ADn);
        snapshot.SetMembers(ADn, [ADn]);

        var walk = await Task.Run(() => MembershipTraversal.Walk(snapshot, ADn));

        Assert.Equal(new[] { ADn }, walk.Visited);
        var cycle = Assert.Single(walk.Cycles);
        Assert.Equal(new[] { ADn }, cycle); // closing edge A→A implied
        Assert.Empty(walk.Frontier);
    }

    [Fact]
    public void Walk_Diamond_ReconvergenceIsNotACycle()
    {
        var snapshot = SnapshotOfGroups(ADn, BDn, CDn, DDn);
        snapshot.SetMembers(ADn, [BDn, CDn]);
        snapshot.SetMembers(BDn, [DDn]);
        snapshot.SetMembers(CDn, [DDn]);
        snapshot.SetMembers(DDn, []);

        var walk = MembershipTraversal.Walk(snapshot, ADn);

        // D is reached through B first and only RE-seen through C: visited once,
        // and the black re-encounter is NOT a back edge — a plain visited set
        // would misreport this diamond as a cycle (gray-vs-black, ADR-006 D2).
        Assert.Equal(new[] { ADn, BDn, DDn, CDn }, walk.Visited);
        Assert.Empty(walk.Cycles);
        Assert.Empty(walk.Frontier);
    }

    [Fact(Timeout = 60_000)]
    public async Task Walk_CycleReachedFromOutside_PathStartsAtReenteredNode()
    {
        var snapshot = SnapshotOfGroups(XDn, ADn, BDn);
        snapshot.SetMembers(XDn, [ADn]);
        snapshot.SetMembers(ADn, [BDn]);
        snapshot.SetMembers(BDn, [ADn]);

        var walk = await Task.Run(() => MembershipTraversal.Walk(snapshot, XDn));

        Assert.Equal(new[] { XDn, ADn, BDn }, walk.Visited);

        // The path slice [v..u] starts at the RE-ENTERED node A — the lead-in X
        // sits on the DFS path but is no part of the cycle.
        var cycle = Assert.Single(walk.Cycles);
        Assert.Equal(new[] { ADn, BDn }, cycle);
        Assert.Empty(walk.Frontier);
    }

    // --- Frontier kind filter -------------------------------------------------------

    [Fact]
    public void Walk_NeverLoadedGroupAndExternalMember_LandInFrontier_AndAreNotRecursed()
    {
        const string ggDn = "CN=GG_Unloaded,OU=Walk,DC=lab";
        const string dlDn = "CN=DL_Unloaded,OU=Walk,DC=lab";
        const string ugDn = "CN=UG_Unloaded,OU=Walk,DC=lab";
        const string externalDn = "CN=Domain Admins,CN=Users,DC=elsewhere";
        var snapshot = SnapshotOfGroups(ADn);
        snapshot.AddObject(Obj(ggDn, AdObjectKind.GlobalGroup));
        snapshot.AddObject(Obj(dlDn, AdObjectKind.DomainLocalGroup));
        snapshot.AddObject(Obj(ugDn, AdObjectKind.UniversalGroup));
        // externalDn deliberately NOT in the snapshot: GetKind falls back to External.
        snapshot.SetMembers(ADn, [ggDn, dlDn, ugDn, externalDn]);

        var walk = MembershipTraversal.Walk(snapshot, ADn);

        // The full ADR-005 fetchable set {GG, DL, UG, External} lands in Frontier,
        // in visit order; none of them is recursed into — nothing beyond the four
        // members is visited ("frontier" ≙ what a double-click would fetch).
        Assert.Equal(new[] { ADn, ggDn, dlDn, ugDn, externalDn }, walk.Visited);
        Assert.Equal(new[] { ggDn, dlDn, ugDn, externalDn }, walk.Frontier);
        Assert.Empty(walk.Cycles);
    }

    [Fact]
    public void Walk_LoadedEmptyGroup_IsNotFrontier()
    {
        var snapshot = SnapshotOfGroups(ADn, BDn);
        snapshot.SetMembers(ADn, [BDn]);
        snapshot.SetMembers(BDn, []); // loaded-and-empty ≠ never loaded (data-model rule)

        var walk = MembershipTraversal.Walk(snapshot, ADn);

        Assert.Equal(new[] { ADn, BDn }, walk.Visited);
        Assert.Empty(walk.Frontier);
        Assert.Empty(walk.Cycles);
    }

    [Fact]
    public void Walk_UserComputerOuLeaves_AreNeverFrontier()
    {
        const string userDn = "CN=Ada Lovelace,OU=Walk,DC=lab";
        const string computerDn = "CN=PC-001,OU=Walk,DC=lab";
        const string ouDn = "OU=Nested,OU=Walk,DC=lab";
        var snapshot = SnapshotOfGroups(ADn);
        snapshot.AddObject(Obj(userDn, AdObjectKind.User));
        snapshot.AddObject(Obj(computerDn, AdObjectKind.Computer));
        snapshot.AddObject(Obj(ouDn, AdObjectKind.OrganizationalUnit));
        snapshot.SetMembers(ADn, [userDn, computerDn, ouDn]);

        var walk = MembershipTraversal.Walk(snapshot, ADn);

        // All three have null GetMembers, but their kinds are leaves whose members
        // are never fetched — flagging them would gut the AP 3.4 unchecked hint.
        Assert.Equal(new[] { ADn, userDn, computerDn, ouDn }, walk.Visited);
        Assert.Empty(walk.Frontier);
        Assert.Empty(walk.Cycles);
    }

    [Fact]
    public void Walk_StartDnNeverLoaded_YieldsSelfAsOnlyVisitAndFrontier()
    {
        var snapshot = SnapshotOfGroups(ADn); // present, fetchable kind, never loaded

        var walk = MembershipTraversal.Walk(snapshot, ADn);

        Assert.Equal(new[] { ADn }, walk.Visited);
        Assert.Equal(new[] { ADn }, walk.Frontier);
        Assert.Empty(walk.Cycles);
    }

    // --- Termination bound ------------------------------------------------------------

    [Fact(Timeout = 60_000)]
    public async Task Walk_InterlockingCycles_TerminatesWithinVPlusEBound_NoDuplicateVisits()
    {
        // Four interlocking cycles sharing nodes — denser than the seeded lab pair:
        // A→B→C→A, B→C→D→B, C→D→E→C and the full loop A→B→C→D→E→A over 8 edges.
        var snapshot = SnapshotOfGroups(ADn, BDn, CDn, DDn, EDn);
        snapshot.SetMembers(ADn, [BDn]);
        snapshot.SetMembers(BDn, [CDn]);
        snapshot.SetMembers(CDn, [ADn, DDn]);
        snapshot.SetMembers(DDn, [BDn, EDn]);
        snapshot.SetMembers(EDn, [CDn, ADn]);

        var walk = await Task.Run(() => MembershipTraversal.Walk(snapshot, ADn));

        // Independent V+E+1 bound (ADR-006 D4: tests prove termination without
        // trusting the implementation) and visit-once under Dn.Comparer.
        var bound = snapshot.Objects.Count + snapshot.Edges.Count + 1; // 5 + 8 + 1
        Assert.InRange(walk.Visited.Count, 1, bound);
        Assert.Equal(walk.Visited.Count, walk.Visited.ToHashSet(Dn.Comparer).Count);
        Assert.Equal(new[] { ADn, BDn, CDn, DDn, EDn }, walk.Visited);

        // One cycle per back edge, bounded by the membership edge count (ADR-006 D2).
        Assert.Equal(4, walk.Cycles.Count);
        AssertContainsCycle(walk, ADn, BDn, CDn);
        AssertContainsCycle(walk, BDn, CDn, DDn);
        AssertContainsCycle(walk, CDn, DDn, EDn);
        AssertContainsCycle(walk, ADn, BDn, CDn, DDn, EDn);
        Assert.Empty(walk.Frontier);
    }

    // --- Helpers -------------------------------------------------------------------------

    private static void AssertContainsCycle(MembershipWalk walk, params string[] expectedPath) =>
        Assert.Contains(walk.Cycles, cycle => cycle.SequenceEqual(expectedPath));

    private static DirectorySnapshot SnapshotOfGroups(params string[] dns)
    {
        var snapshot = new DirectorySnapshot();
        foreach (var dn in dns)
        {
            snapshot.AddObject(Obj(dn, AdObjectKind.GlobalGroup));
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
