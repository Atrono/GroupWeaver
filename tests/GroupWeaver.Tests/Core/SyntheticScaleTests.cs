using System.Diagnostics;

using GroupWeaver.Core.Diff;
using GroupWeaver.Core.Graph;
using GroupWeaver.Core.Model;
using GroupWeaver.Core.Rules;

using Xunit;

namespace GroupWeaver.Tests.Core;

/// <summary>
/// Proves the perf CONTRACTS pinned by convention/comments in
/// <c>.claude/rules/data-model.md</c>, <c>rule-engine.md</c>, and
/// <c>gap-diff.md</c> ("<see cref="DirectorySnapshot.Edges"/> is read exactly
/// once per call, O(E) recompute") actually hold at the documented ~10K-edge
/// scale target, by running <see cref="GraphBuilder.Build"/>,
/// <see cref="RuleEngine.Evaluate"/>, and <see cref="SnapshotDiff.Compute"/>
/// together over one large, hand-built, deterministic in-memory snapshot
/// (issue #293 — deliberately narrower than #248/WP9's live-AD/CLI/generator
/// initiative). Pure Core, offline, no AD, no file I/O — runs in every CI
/// build (no <c>Category=RequiresAd</c> trait).
///
/// <para>Construction is fully index-derived (no <see cref="Random"/>, no
/// <see cref="Guid.NewGuid"/>, no wall-clock) so the shape — and therefore
/// every count asserted below — is reproducible byte-for-byte across runs and
/// machines. Shape: 1 root OU, 2 child OUs (Groups/Users), 100 GlobalGroups
/// (100 Users each), 20 DomainLocalGroups (5 GlobalGroups each), plus one
/// GG_Circle_A/GG_Circle_B pair that mutually nest — the required
/// circular-nesting case that exercises <c>MembershipTraversal.Walk</c>'s
/// cycle path at scale, not just the acyclic majority.</para>
/// </summary>
public class SyntheticScaleTests
{
    private const string RootDn = "OU=AGDLP-Scale,DC=agdlp,DC=lab";
    private const string GroupsOuDn = "OU=Groups,OU=AGDLP-Scale,DC=agdlp,DC=lab";
    private const string UsersOuDn = "OU=Users,OU=AGDLP-Scale,DC=agdlp,DC=lab";

    private const int GgCount = 100;
    private const int UserCount = 10_000;
    private const int UsersPerGg = UserCount / GgCount; // 100

    private const int DlCount = 20;
    private const int GgsPerDl = GgCount / DlCount; // 5

    private const string CircleADn = $"CN=GG_Circle_A,{GroupsOuDn}";
    private const string CircleBDn = $"CN=GG_Circle_B,{GroupsOuDn}";

    /// <summary>3 OUs + GG + DL + Users + the circular pair.</summary>
    private const int ExpectedObjectCount = 3 + GgCount + DlCount + UserCount + 2;

    /// <summary>GG-&gt;User + DL-&gt;GG + the 2 circular-pair edges.</summary>
    private const int ExpectedMembershipEdgeCount = (GgCount * UsersPerGg) + (DlCount * GgsPerDl) + 2;

    // A generous ceiling (not a tight microbenchmark): observed uninstrumented
    // Release wall time on the lab box was GraphBuilder.Build ~120ms,
    // RuleEngine.Evaluate ~290ms, SnapshotDiff.Compute ~60ms (~17x headroom to
    // this 5s ceiling on the slowest of the three). 5s leaves large headroom
    // for slower/shared CI hardware while still catching a catastrophic
    // (e.g. accidentally-quadratic) regression.
    private static readonly TimeSpan Ceiling = TimeSpan.FromSeconds(5);

    // --- Construction sanity ------------------------------------------------------

    [Fact]
    public void Snapshot_HasExpectedObjectAndEdgeCounts()
    {
        var snapshot = BuildSyntheticSnapshot();

        Assert.Equal(ExpectedObjectCount, snapshot.Objects.Count);
        Assert.Equal(ExpectedMembershipEdgeCount, snapshot.Edges.Count);
        Assert.True(ExpectedObjectCount >= 10_000, "shape must reach the ~10K object target");
        Assert.True(ExpectedMembershipEdgeCount >= 10_000, "shape must reach the ~10K+ edge target");
    }

    // --- GraphBuilder.Build at scale ------------------------------------------------

    [Fact]
    public void GraphBuilder_Build_AtScale_ProducesTotalTopology_WithinTimeCeiling()
    {
        var snapshot = BuildSyntheticSnapshot();

        var stopwatch = Stopwatch.StartNew();
        var model = GraphBuilder.Build(snapshot, RootDn);
        stopwatch.Stop();

        // Totality (ADR-004): every snapshot object becomes exactly one node;
        // no edge endpoint is External here (every DN referenced by an edge is
        // itself a snapshot object), so no synthetic External nodes appear.
        Assert.Equal(ExpectedObjectCount, model.Nodes.Count);
        Assert.Equal(ExpectedObjectCount, model.Nodes.Select(n => n.Dn).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.DoesNotContain(model.Nodes, n => n.Kind == AdObjectKind.External);

        // Every non-root in-scope node gets exactly one containment edge, plus
        // every membership edge verbatim (GraphBuilder.cs BuildEdges contract).
        var expectedEdgeCount = ExpectedMembershipEdgeCount + (ExpectedObjectCount - 1);
        Assert.Equal(expectedEdgeCount, model.Edges.Count);

        Assert.True(
            stopwatch.Elapsed < Ceiling,
            $"GraphBuilder.Build took {stopwatch.Elapsed} for {ExpectedObjectCount} objects / " +
            $"{ExpectedMembershipEdgeCount} edges - exceeded the {Ceiling} ceiling.");
    }

    // --- RuleEngine.Evaluate at scale ------------------------------------------------

    [Fact]
    public void RuleEngine_Evaluate_AtScale_FindsExactlyTheSeededCircularViolation_WithinTimeCeiling()
    {
        var snapshot = BuildSyntheticSnapshot();
        var ruleset = RulesetLoader.LoadDefault();

        var stopwatch = Stopwatch.StartNew();
        var report = RuleEngine.Evaluate(snapshot, ruleset);
        stopwatch.Stop();

        // The synthetic shape is deliberately AGDLP-clean and default-pattern-
        // conformant (GG_Role####_Team / DL_Resource####_RW naming; only
        // GG->User, DL->GG, and GG->GG nesting, all "allow" cells; no empty
        // groups) EXCEPT the one seeded circular pair - so exactly one finding
        // is expected: the circular-nesting error over the two Circle DNs. A
        // sane, non-negative, boundED count - not just "didn't throw".
        var violation = Assert.Single(report.Violations);
        Assert.Equal(RuleIds.Circular, violation.RuleId);
        Assert.Equal(RuleSeverity.Error, violation.Severity);
        Assert.Contains(violation.Dns, dn => Dn.Comparer.Equals(dn, CircleADn));
        Assert.Contains(violation.Dns, dn => Dn.Comparer.Equals(dn, CircleBDn));

        // Every group parent was SetMembers'd (loaded); nothing should surface
        // as unexpanded/unchecked in this fully-loaded synthetic scope.
        Assert.Empty(report.UncheckedDns);

        Assert.True(
            stopwatch.Elapsed < Ceiling,
            $"RuleEngine.Evaluate took {stopwatch.Elapsed} for {ExpectedObjectCount} objects / " +
            $"{ExpectedMembershipEdgeCount} edges - exceeded the {Ceiling} ceiling.");
    }

    // --- SnapshotDiff.Compute at scale ------------------------------------------------

    [Fact]
    public void SnapshotDiff_Compute_AtScale_EverythingCommon_WithinTimeCeiling()
    {
        // Cheap variant per the WP scoping (issue #293): the same fully-loaded
        // snapshot as both Ist and Plan proves Compute completes at scale
        // without re-exercising Added/Removed/Unchecked semantics, which are
        // already pinned by SnapshotDiffTests.cs at small scale.
        var snapshot = BuildSyntheticSnapshot();

        var stopwatch = Stopwatch.StartNew();
        var diff = SnapshotDiff.Compute(snapshot, snapshot);
        stopwatch.Stop();

        Assert.Equal(ExpectedObjectCount, diff.NodeStatus.Count);
        Assert.All(diff.NodeStatus.Values, status => Assert.Equal(DiffStatus.Common, status));

        Assert.Equal(ExpectedMembershipEdgeCount, diff.EdgeStatus.Count);
        Assert.All(diff.EdgeStatus.Values, status => Assert.Equal(DiffStatus.Common, status));

        Assert.Empty(diff.UncheckedParents);

        Assert.True(
            stopwatch.Elapsed < Ceiling,
            $"SnapshotDiff.Compute took {stopwatch.Elapsed} for {ExpectedObjectCount} objects / " +
            $"{ExpectedMembershipEdgeCount} edges - exceeded the {Ceiling} ceiling.");
    }

    // --- Construction ------------------------------------------------------------

    /// <summary>
    /// Builds the synthetic snapshot directly via <see cref="DirectorySnapshot.AddObject"/>
    /// / <see cref="DirectorySnapshot.SetMembers"/> only (no JSON, no provider, no AD).
    /// Every DN/name is derived purely from its loop index, so the result is byte-for-byte
    /// reproducible: same object set, same edge set, same order, every run.
    /// </summary>
    private static DirectorySnapshot BuildSyntheticSnapshot()
    {
        var snapshot = new DirectorySnapshot();

        snapshot.AddObject(new AdObject { Dn = RootDn, Kind = AdObjectKind.OrganizationalUnit, Name = "AGDLP-Scale" });
        snapshot.AddObject(new AdObject { Dn = GroupsOuDn, Kind = AdObjectKind.OrganizationalUnit, Name = "Groups" });
        snapshot.AddObject(new AdObject { Dn = UsersOuDn, Kind = AdObjectKind.OrganizationalUnit, Name = "Users" });

        // Users: leaves only, never SetMembers'd (User is not a fetchable kind,
        // see RuleEngine.IsFetchableKind, so leaving them "never loaded" does
        // not add them to UncheckedDns).
        var userDns = new string[UserCount];
        for (var i = 0; i < UserCount; i++)
        {
            var name = $"User{i:D5}";
            var dn = $"CN={name},{UsersOuDn}";
            userDns[i] = dn;
            snapshot.AddObject(new AdObject { Dn = dn, Kind = AdObjectKind.User, Name = name, SamAccountName = name });
        }

        // GlobalGroups: GG_Role####_Team (matches the default naming-gg pattern),
        // each holding a contiguous, disjoint slice of UsersPerGg users.
        var ggDns = new string[GgCount];
        for (var i = 0; i < GgCount; i++)
        {
            var name = $"GG_Role{i:D4}_Team";
            var dn = $"CN={name},{GroupsOuDn}";
            ggDns[i] = dn;
            snapshot.AddObject(new AdObject { Dn = dn, Kind = AdObjectKind.GlobalGroup, Name = name, SamAccountName = name });

            var members = new string[UsersPerGg];
            Array.Copy(userDns, i * UsersPerGg, members, 0, UsersPerGg);
            snapshot.SetMembers(dn, members);
        }

        // DomainLocalGroups: DL_Resource####_RW (matches the default naming-dl
        // pattern), each holding a contiguous, disjoint slice of GgsPerDl GGs
        // (the conformant G->DL AGDLP lane).
        for (var i = 0; i < DlCount; i++)
        {
            var name = $"DL_Resource{i:D4}_RW";
            var dn = $"CN={name},{GroupsOuDn}";
            snapshot.AddObject(new AdObject { Dn = dn, Kind = AdObjectKind.DomainLocalGroup, Name = name, SamAccountName = name });

            var members = new string[GgsPerDl];
            Array.Copy(ggDns, i * GgsPerDl, members, 0, GgsPerDl);
            snapshot.SetMembers(dn, members);
        }

        // The required circular case (GG_Circle_A <-> GG_Circle_B, mirroring the
        // AD lab fixture's GG_Circle_A/GG_Circle_B convention): mutual nesting
        // exercises MembershipTraversal.Walk's cycle path at scale, proving the
        // sweep still terminates and reports correctly with a ~10K-edge frontier.
        snapshot.AddObject(new AdObject { Dn = CircleADn, Kind = AdObjectKind.GlobalGroup, Name = "GG_Circle_A", SamAccountName = "GG_Circle_A" });
        snapshot.AddObject(new AdObject { Dn = CircleBDn, Kind = AdObjectKind.GlobalGroup, Name = "GG_Circle_B", SamAccountName = "GG_Circle_B" });
        snapshot.SetMembers(CircleADn, [CircleBDn]);
        snapshot.SetMembers(CircleBDn, [CircleADn]);

        return snapshot;
    }
}
