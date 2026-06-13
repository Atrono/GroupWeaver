using GroupWeaver.Core.Diff;
using GroupWeaver.Core.Model;
using GroupWeaver.Core.Plan;

using Xunit;

namespace GroupWeaver.Tests.Core.Diff;

/// <summary>
/// Pins the pure-Core gap diff of ADR-015 Slice 1 (#66):
/// <see cref="SnapshotDiff.Compute(DirectorySnapshot, DirectorySnapshot)"/> compares the
/// live "Ist" snapshot against the projected "Plan" snapshot
/// (<c>PlanProjection.ToSnapshot</c>), classifying every node and edge as
/// <see cref="DiffStatus.Common"/>, <see cref="DiffStatus.Added"/>,
/// <see cref="DiffStatus.Removed"/>, or <see cref="DiffStatus.Unchecked"/>.
///
/// <para>TYPE-SHAPE CHOICE (pinned): the record-with-static-factory shape from the
/// ADR — a <c>public sealed record SnapshotDiff(IReadOnlyDictionary&lt;string,
/// DiffStatus&gt; NodeStatus, IReadOnlyDictionary&lt;MembershipEdge, DiffStatus&gt;
/// EdgeStatus, IReadOnlyList&lt;string&gt; UncheckedParents)</c> with a static
/// <c>Compute(ist, plan)</c> factory. The implementer follows this shape.</para>
///
/// <para>Mirrors <c>RuleEngine.Evaluate</c>'s discipline: <c>Compute</c> is static,
/// deterministic, total, UI-free, never throws on directory CONTENT, calls no provider,
/// and mutates neither input. <c>NodeStatus</c> is keyed by <see cref="Dn.Comparer"/>;
/// <c>EdgeStatus</c> is keyed by <see cref="MembershipEdge"/> (which already hashes via
/// <c>Dn.Comparer</c>). The honest load-state tri-state (ADR-005) drives
/// <c>Unchecked</c>: an edge under a KNOWN Ist parent whose members were never loaded is
/// <c>Unchecked</c> (never falsely Added/Removed) and that parent surfaces in
/// <c>UncheckedParents</c> — but a PLAN-ONLY parent (not an Ist object at all) is
/// genuinely new, so its edges are <c>Added</c> and it NEVER enters
/// <c>UncheckedParents</c>.</para>
///
/// <para>EQUALITY DISCIPLINE (rule-engine.md "compare PROJECTIONS, never whole
/// records"): the result's dictionaries do not override <c>Equals</c>, so every
/// assertion compares their CONTENTS (sorted (dn, status) / (edge, status) pairs and the
/// <c>UncheckedParents</c> sequence), never the record/dictionary identity.</para>
///
/// Hand-built <see cref="DirectorySnapshot"/> fixtures only (pure Core, offline). RED
/// until <c>src/Core/Diff/DiffStatus</c> and <c>src/Core/Diff/SnapshotDiff</c> exist.
/// </summary>
public class SnapshotDiffTests
{
    private const string BaseOu = "OU=AGDLP-Lab,DC=agdlp,DC=lab";
    private const string DlDn = "CN=DL_FileShare_RW,OU=AGDLP-Lab,DC=agdlp,DC=lab";
    private const string GgDn = "CN=GG_Sales_EU,OU=AGDLP-Lab,DC=agdlp,DC=lab";
    private const string UserDn = "CN=Ada Lovelace,OU=AGDLP-Lab,DC=agdlp,DC=lab";

    // --- 1. Node Added / Removed / Common ------------------------------------------------

    /// <summary>
    /// NodeStatus is computed over the UNION of <c>ist.Objects</c> and
    /// <c>plan.Objects</c> DNs: a DN in BOTH is <see cref="DiffStatus.Common"/>, a
    /// plan-only DN is <see cref="DiffStatus.Added"/>, and an ist-only DN is
    /// <see cref="DiffStatus.Removed"/>. (Direction: plan is the proposed target, so
    /// what the plan adds is "Added" and what only the live Ist has is "Removed".)
    /// </summary>
    [Fact]
    public void NodeStatus_OverTheUnion_MarksCommonAddedAndRemoved()
    {
        var ist = new DirectorySnapshot();
        ist.AddObject(Obj(DlDn, AdObjectKind.DomainLocalGroup));   // in both -> Common
        ist.AddObject(Obj(UserDn, AdObjectKind.User));             // ist only -> Removed

        var plan = new DirectorySnapshot();
        plan.AddObject(Obj(DlDn, AdObjectKind.DomainLocalGroup));  // in both -> Common
        plan.AddObject(Obj(GgDn, AdObjectKind.GlobalGroup));       // plan only -> Added

        var diff = SnapshotDiff.Compute(ist, plan);

        Assert.Equal(DiffStatus.Common, NodeOf(diff, DlDn));
        Assert.Equal(DiffStatus.Removed, NodeOf(diff, UserDn));
        Assert.Equal(DiffStatus.Added, NodeOf(diff, GgDn));
        Assert.Equal(3, diff.NodeStatus.Count); // exactly the union, no extras
    }

    // --- 2. Node Dn.Comparer keying ------------------------------------------------------

    /// <summary>
    /// NodeStatus is keyed by <see cref="Dn.Comparer"/>: a case-variant DN present in
    /// both snapshots collapses to a SINGLE <see cref="DiffStatus.Common"/> entry — not
    /// two ordinal-distinct keys (one Added + one Removed). DN strings are stored
    /// as-given (data-model.md "DN strings stored as-given") and only ever COMPARED
    /// case-insensitively.
    /// </summary>
    [Fact]
    public void NodeStatus_CaseVariantDnInIstVsPlan_CollapsesToOneCommonEntry()
    {
        var ist = new DirectorySnapshot();
        ist.AddObject(Obj(GgDn, AdObjectKind.GlobalGroup));

        var plan = new DirectorySnapshot();
        plan.AddObject(Obj(GgDn.ToUpperInvariant(), AdObjectKind.GlobalGroup));

        var diff = SnapshotDiff.Compute(ist, plan);

        Assert.Single(diff.NodeStatus);
        Assert.Equal(DiffStatus.Common, NodeOf(diff, GgDn));
        Assert.Equal(DiffStatus.Common, NodeOf(diff, GgDn.ToUpperInvariant())); // same key
    }

    // --- 3. Edge Added / Removed / Common over LOADED parents ----------------------------

    /// <summary>
    /// Over LOADED parents (members known on both sides), EdgeStatus mirrors NodeStatus:
    /// an edge in both is <see cref="DiffStatus.Common"/>, a plan-only edge is
    /// <see cref="DiffStatus.Added"/>, and an ist-only edge is
    /// <see cref="DiffStatus.Removed"/>. No parent here is unloaded, so
    /// <c>UncheckedParents</c> is empty.
    /// </summary>
    [Fact]
    public void EdgeStatus_OverLoadedParents_MarksCommonAddedAndRemoved()
    {
        // Ist: DL has members [GG, User]. Plan: DL has members [GG, AnotherGg].
        // -> (DL,GG) Common, (DL,User) Removed, (DL,AnotherGg) Added.
        const string anotherGg = "CN=GG_Marketing,OU=AGDLP-Lab,DC=agdlp,DC=lab";

        var ist = new DirectorySnapshot();
        ist.AddObject(Obj(DlDn, AdObjectKind.DomainLocalGroup));
        ist.SetMembers(DlDn, [GgDn, UserDn]);

        var plan = new DirectorySnapshot();
        plan.AddObject(Obj(DlDn, AdObjectKind.DomainLocalGroup));
        plan.SetMembers(DlDn, [GgDn, anotherGg]);

        var diff = SnapshotDiff.Compute(ist, plan);

        Assert.Equal(DiffStatus.Common, EdgeOf(diff, DlDn, GgDn));
        Assert.Equal(DiffStatus.Removed, EdgeOf(diff, DlDn, UserDn));
        Assert.Equal(DiffStatus.Added, EdgeOf(diff, DlDn, anotherGg));
        Assert.Equal(3, diff.EdgeStatus.Count); // exactly the union of edges
        Assert.Empty(diff.UncheckedParents);    // both parents are loaded
    }

    /// <summary>
    /// Edge keying is direction-sensitive AND case-insensitive (via
    /// <see cref="MembershipEdge"/>'s <see cref="Dn.Comparer"/> hashing): the same edge
    /// spelled with a case-variant parent in Ist vs plan collapses to one
    /// <see cref="DiffStatus.Common"/> entry, not an Added/Removed pair.
    /// </summary>
    [Fact]
    public void EdgeStatus_CaseVariantEdge_CollapsesToOneCommonEntry()
    {
        var ist = new DirectorySnapshot();
        ist.AddObject(Obj(DlDn, AdObjectKind.DomainLocalGroup));
        ist.SetMembers(DlDn, [GgDn]);

        var plan = new DirectorySnapshot();
        plan.AddObject(Obj(DlDn, AdObjectKind.DomainLocalGroup));
        plan.SetMembers(DlDn.ToUpperInvariant(), [GgDn.ToUpperInvariant()]);

        var diff = SnapshotDiff.Compute(ist, plan);

        Assert.Single(diff.EdgeStatus);
        Assert.Equal(DiffStatus.Common, EdgeOf(diff, DlDn, GgDn));
    }

    // --- 4. Unchecked arm: KNOWN Ist parent, members never loaded ------------------------

    /// <summary>
    /// The honest tri-state (ADR-005, ADR-015 D5): a KNOWN Ist group whose members were
    /// NEVER loaded (<c>AddObject</c>'d but never <c>SetMembers</c>'d ⇒
    /// <c>ist.TryGetObject(parent, out _) &amp;&amp; !ist.IsLoaded(parent)</c>). A plan
    /// edge under it is <see cref="DiffStatus.Unchecked"/> — NEVER falsely Added/Removed,
    /// because we cannot know the Ist members — and the parent goes into
    /// <c>UncheckedParents</c>.
    /// </summary>
    [Fact]
    public void EdgeStatus_KnownIstParentNeverLoaded_PlanEdgeIsUnchecked_ParentInUncheckedParents()
    {
        // Ist KNOWS DL exists but never expanded it (no SetMembers).
        var ist = new DirectorySnapshot();
        ist.AddObject(Obj(DlDn, AdObjectKind.DomainLocalGroup));
        Assert.True(ist.TryGetObject(DlDn, out _)); // KNOWN object ...
        Assert.False(ist.IsLoaded(DlDn));           // ... but members unloaded.

        // Plan proposes DL has member GG.
        var plan = new DirectorySnapshot();
        plan.AddObject(Obj(DlDn, AdObjectKind.DomainLocalGroup));
        plan.AddObject(Obj(GgDn, AdObjectKind.GlobalGroup));
        plan.SetMembers(DlDn, [GgDn]);

        var diff = SnapshotDiff.Compute(ist, plan);

        // The edge under the unloaded-but-known parent is Unchecked, not Added.
        Assert.Equal(DiffStatus.Unchecked, EdgeOf(diff, DlDn, GgDn));
        Assert.Single(diff.EdgeStatus);

        // The parent surfaces in UncheckedParents; nothing is falsely Added/Removed.
        Assert.Equal([DlDn], diff.UncheckedParents);
        Assert.DoesNotContain(diff.EdgeStatus.Values, s => s is DiffStatus.Added or DiffStatus.Removed);
    }

    // --- 5. The plan-Added-parent distinction (keystone) ---------------------------------

    /// <summary>
    /// THE KEYSTONE correctness pin (ADR-015 D5): an edge whose parent is a PLAN-ONLY
    /// group — not in <c>ist.Objects</c> at all — is <see cref="DiffStatus.Added"/>, NOT
    /// <see cref="DiffStatus.Unchecked"/>. <c>!ist.IsLoaded(parent)</c> is true for such a
    /// parent too, but the parent is genuinely new (no Ist object), so the
    /// <c>Unchecked</c> arm MUST gate on KNOWN-Ist-object, not on load-state alone. The
    /// plan-only parent must NOT appear in <c>UncheckedParents</c>.
    /// </summary>
    [Fact]
    public void EdgeStatus_PlanOnlyParent_EdgeIsAdded_ParentNotInUncheckedParents()
    {
        // Ist is empty (or at least does NOT know GG). GG is a brand-new plan group.
        var ist = new DirectorySnapshot();
        Assert.False(ist.TryGetObject(GgDn, out _)); // GG is NOT an Ist object ...
        Assert.False(ist.IsLoaded(GgDn));            // ... and naturally unloaded.

        var plan = new DirectorySnapshot();
        plan.AddObject(Obj(GgDn, AdObjectKind.GlobalGroup));
        plan.AddObject(Obj(UserDn, AdObjectKind.User));
        plan.SetMembers(GgDn, [UserDn]);

        var diff = SnapshotDiff.Compute(ist, plan);

        // A genuinely new group's edges are Added, never Unchecked.
        Assert.Equal(DiffStatus.Added, EdgeOf(diff, GgDn, UserDn));
        Assert.DoesNotContain(DiffStatus.Unchecked, diff.EdgeStatus.Values);

        // A plan-only parent is NOT an "unexpanded Ist area".
        Assert.Empty(diff.UncheckedParents);
        Assert.DoesNotContain(GgDn, diff.UncheckedParents);
    }

    // --- 6. loaded-empty [] vs unloaded null ---------------------------------------------

    /// <summary>
    /// The null-vs-empty tri-state distinction drives Unchecked, NOT emptiness: an Ist
    /// parent <c>SetMembers</c>'d to <c>[]</c> is LOADED — its members are known to be
    /// none — so a plan edge under it is <see cref="DiffStatus.Added"/> (and the parent
    /// is NOT in <c>UncheckedParents</c>). The SAME parent left unloaded (never
    /// <c>SetMembers</c>'d) makes that same plan edge <see cref="DiffStatus.Unchecked"/>.
    /// This is the literal "loaded-EMPTY is not Unchecked" pin.
    /// </summary>
    [Fact]
    public void EdgeStatus_LoadedEmptyIstParent_PlanEdgeIsAdded_NotUnchecked()
    {
        var ist = new DirectorySnapshot();
        ist.AddObject(Obj(DlDn, AdObjectKind.DomainLocalGroup));
        ist.SetMembers(DlDn, []); // LOADED and genuinely empty.
        Assert.True(ist.IsLoaded(DlDn));

        var plan = new DirectorySnapshot();
        plan.AddObject(Obj(DlDn, AdObjectKind.DomainLocalGroup));
        plan.AddObject(Obj(GgDn, AdObjectKind.GlobalGroup));
        plan.SetMembers(DlDn, [GgDn]);

        var diff = SnapshotDiff.Compute(ist, plan);

        Assert.Equal(DiffStatus.Added, EdgeOf(diff, DlDn, GgDn)); // known-none -> Added
        Assert.Empty(diff.UncheckedParents);
    }

    /// <summary>
    /// The complementary half of test 6: the SAME parent and SAME plan edge, but the Ist
    /// parent is left UNLOADED (never <c>SetMembers</c>'d) ⇒ the plan edge flips to
    /// <see cref="DiffStatus.Unchecked"/> and the parent enters <c>UncheckedParents</c>.
    /// Proves the classification turns on load-state, not on the parent merely lacking a
    /// matching Ist member.
    /// </summary>
    [Fact]
    public void EdgeStatus_UnloadedIstParent_SamePlanEdge_IsUnchecked()
    {
        var ist = new DirectorySnapshot();
        ist.AddObject(Obj(DlDn, AdObjectKind.DomainLocalGroup)); // KNOWN, never loaded.
        Assert.False(ist.IsLoaded(DlDn));

        var plan = new DirectorySnapshot();
        plan.AddObject(Obj(DlDn, AdObjectKind.DomainLocalGroup));
        plan.AddObject(Obj(GgDn, AdObjectKind.GlobalGroup));
        plan.SetMembers(DlDn, [GgDn]);

        var diff = SnapshotDiff.Compute(ist, plan);

        Assert.Equal(DiffStatus.Unchecked, EdgeOf(diff, DlDn, GgDn));
        Assert.Equal([DlDn], diff.UncheckedParents);
    }

    // --- 7. ist == projection of the SAME plan -> all Common -----------------------------

    /// <summary>
    /// The demo-shaped "no drift" case: when <c>ist</c> IS
    /// <c>PlanProjection.ToSnapshot(samePlan)</c> and <c>plan</c> is the projection of the
    /// SAME plan, every node and edge is <see cref="DiffStatus.Common"/> and
    /// <c>UncheckedParents</c> is empty (a fully-authored projection has no unexpanded
    /// areas — every group is <c>SetMembers</c>'d, even an empty one). Proves Compute's
    /// reflexivity over a realistic projection (groups loaded, users left null leaves
    /// that are never parents, so they never reach the Unchecked arm).
    /// </summary>
    [Fact]
    public void Compute_IstEqualsProjectionOfSamePlan_AllCommon_NoUnchecked()
    {
        var planModel = new PlanModel(BaseOu);
        var dl = planModel.AddNode(PlanCreatableKind.DomainLocalGroup, "DL_FileShare_RW");
        var gg = planModel.AddNode(PlanCreatableKind.GlobalGroup, "GG_Sales_EU");
        var user = planModel.AddNode(PlanCreatableKind.User, "Ada Lovelace");
        var emptyGg = planModel.AddNode(PlanCreatableKind.GlobalGroup, "GG_Empty");
        planModel.AddEdge(dl.Dn, gg.Dn);
        planModel.AddEdge(gg.Dn, user.Dn);

        var projection = PlanProjection.ToSnapshot(planModel);

        // Pass the SAME projection as BOTH ist and plan.
        var diff = SnapshotDiff.Compute(projection, projection);

        // Every node Common.
        Assert.All(diff.NodeStatus.Values, s => Assert.Equal(DiffStatus.Common, s));
        Assert.Equal(DiffStatus.Common, NodeOf(diff, dl.Dn));
        Assert.Equal(DiffStatus.Common, NodeOf(diff, gg.Dn));
        Assert.Equal(DiffStatus.Common, NodeOf(diff, user.Dn));
        Assert.Equal(DiffStatus.Common, NodeOf(diff, emptyGg.Dn));

        // Every edge Common.
        Assert.All(diff.EdgeStatus.Values, s => Assert.Equal(DiffStatus.Common, s));
        Assert.Equal(DiffStatus.Common, EdgeOf(diff, dl.Dn, gg.Dn));
        Assert.Equal(DiffStatus.Common, EdgeOf(diff, gg.Dn, user.Dn));

        // Nothing unexpanded — the empty group is loaded-[] and the user is a never-parent leaf.
        Assert.Empty(diff.UncheckedParents);
    }

    // --- 8. empty / empty ----------------------------------------------------------------

    /// <summary>
    /// Totality at the boundary: two empty snapshots yield empty <c>NodeStatus</c>,
    /// empty <c>EdgeStatus</c>, and empty <c>UncheckedParents</c> — no throw, no synthetic
    /// entries.
    /// </summary>
    [Fact]
    public void Compute_EmptyVsEmpty_YieldsEmptyMapsAndEmptyUncheckedParents()
    {
        var diff = SnapshotDiff.Compute(new DirectorySnapshot(), new DirectorySnapshot());

        Assert.Empty(diff.NodeStatus);
        Assert.Empty(diff.EdgeStatus);
        Assert.Empty(diff.UncheckedParents);
    }

    // --- 9. Determinism (compare PROJECTIONS, never record identity) ---------------------

    /// <summary>
    /// Determinism (mirrors <c>RuleEngine.Evaluate</c>): two independently-built but
    /// content-equal <c>(ist, plan)</c> pairs produce EQUAL <c>NodeStatus</c>,
    /// <c>EdgeStatus</c>, and <c>UncheckedParents</c> CONTENTS — independent of insertion
    /// order. Per rule-engine.md the result's dictionaries do not override
    /// <c>Equals</c>, so this compares PROJECTIONS (sorted <c>(dn, status)</c> and
    /// <c>(edge-as-string, status)</c> pairs, plus the ordered <c>UncheckedParents</c>
    /// sequence), NEVER the record/dictionary identity.
    /// </summary>
    [Fact]
    public void Compute_TwoContentEqualPairs_YieldEqualProjections_RegardlessOfInsertionOrder()
    {
        const string unloadedParent = "CN=DL_Unexpanded,OU=AGDLP-Lab,DC=agdlp,DC=lab";

        // Pair A: objects/members added in one order.
        var istA = new DirectorySnapshot();
        istA.AddObject(Obj(DlDn, AdObjectKind.DomainLocalGroup));
        istA.AddObject(Obj(UserDn, AdObjectKind.User));
        istA.AddObject(Obj(unloadedParent, AdObjectKind.DomainLocalGroup)); // known, unloaded
        istA.SetMembers(DlDn, [UserDn]);
        var planA = new DirectorySnapshot();
        planA.AddObject(Obj(DlDn, AdObjectKind.DomainLocalGroup));
        planA.AddObject(Obj(GgDn, AdObjectKind.GlobalGroup));
        planA.AddObject(Obj(unloadedParent, AdObjectKind.DomainLocalGroup));
        planA.SetMembers(DlDn, [GgDn]);
        planA.SetMembers(unloadedParent, [GgDn]); // plan edge under the unloaded Ist parent

        // Pair B: SAME content, objects/members added in a DIFFERENT order.
        var istB = new DirectorySnapshot();
        istB.AddObject(Obj(unloadedParent, AdObjectKind.DomainLocalGroup));
        istB.AddObject(Obj(UserDn, AdObjectKind.User));
        istB.AddObject(Obj(DlDn, AdObjectKind.DomainLocalGroup));
        istB.SetMembers(DlDn, [UserDn]);
        var planB = new DirectorySnapshot();
        planB.SetMembers(unloadedParent, [GgDn]);
        planB.AddObject(Obj(GgDn, AdObjectKind.GlobalGroup));
        planB.AddObject(Obj(unloadedParent, AdObjectKind.DomainLocalGroup));
        planB.AddObject(Obj(DlDn, AdObjectKind.DomainLocalGroup));
        planB.SetMembers(DlDn, [GgDn]);

        var diffA = SnapshotDiff.Compute(istA, planA);
        var diffB = SnapshotDiff.Compute(istB, planB);

        Assert.Equal(NodeProjection(diffA), NodeProjection(diffB));
        Assert.Equal(EdgeProjection(diffA), EdgeProjection(diffB));
        Assert.Equal(diffA.UncheckedParents, diffB.UncheckedParents); // ordered sequence
    }

    // --- 10. UncheckedParents sorted OrdinalIgnoreCase + distinct ------------------------

    /// <summary>
    /// <c>UncheckedParents</c> is DISTINCT and sorted OrdinalIgnoreCase: two
    /// KNOWN-but-unloaded Ist parents inserted in non-sorted order come back sorted, and
    /// a parent that hosts multiple union edges appears exactly once (no duplicates).
    /// </summary>
    [Fact]
    public void UncheckedParents_AreDistinctAndSortedOrdinalIgnoreCase()
    {
        // Two known-but-unloaded Ist parents, inserted in NON-sorted order (Z before A).
        const string zParent = "CN=DL_Zeta,OU=AGDLP-Lab,DC=agdlp,DC=lab";
        const string aParent = "CN=DL_Alpha,OU=AGDLP-Lab,DC=agdlp,DC=lab";

        var ist = new DirectorySnapshot();
        ist.AddObject(Obj(zParent, AdObjectKind.DomainLocalGroup)); // Z first
        ist.AddObject(Obj(aParent, AdObjectKind.DomainLocalGroup)); // A second
        // Neither SetMembers'd: both KNOWN and unloaded.

        var plan = new DirectorySnapshot();
        plan.AddObject(Obj(zParent, AdObjectKind.DomainLocalGroup));
        plan.AddObject(Obj(aParent, AdObjectKind.DomainLocalGroup));
        plan.AddObject(Obj(GgDn, AdObjectKind.GlobalGroup));
        plan.AddObject(Obj(UserDn, AdObjectKind.User));
        // zParent hosts TWO plan edges -> must still appear exactly once.
        plan.SetMembers(zParent, [GgDn, UserDn]);
        plan.SetMembers(aParent, [GgDn]);

        var diff = SnapshotDiff.Compute(ist, plan);

        // Sorted OrdinalIgnoreCase (Alpha < Zeta) and DISTINCT (zParent appears once).
        Assert.Equal([aParent, zParent], diff.UncheckedParents);

        // Every edge under both unloaded-known parents is Unchecked.
        Assert.Equal(DiffStatus.Unchecked, EdgeOf(diff, zParent, GgDn));
        Assert.Equal(DiffStatus.Unchecked, EdgeOf(diff, zParent, UserDn));
        Assert.Equal(DiffStatus.Unchecked, EdgeOf(diff, aParent, GgDn));
    }

    // --- Helpers --------------------------------------------------------------------------

    private static AdObject Obj(string dn, AdObjectKind kind) => new()
    {
        Dn = dn,
        Kind = kind,
        Name = dn.Split(',')[0]["CN=".Length..],
    };

    /// <summary>NodeStatus lookup via the contract's <see cref="Dn.Comparer"/> keying.</summary>
    private static DiffStatus NodeOf(SnapshotDiff diff, string dn)
    {
        Assert.True(diff.NodeStatus.TryGetValue(dn, out var status), $"no NodeStatus for {dn}");
        return status;
    }

    /// <summary>EdgeStatus lookup keyed by <see cref="MembershipEdge"/>.</summary>
    private static DiffStatus EdgeOf(SnapshotDiff diff, string parentDn, string childDn)
    {
        Assert.True(
            diff.EdgeStatus.TryGetValue(new MembershipEdge(parentDn, childDn), out var status),
            $"no EdgeStatus for ({parentDn} -> {childDn})");
        return status;
    }

    /// <summary>A stable, identity-free projection of NodeStatus for content equality:
    /// sorted (dn, status) pairs, DNs upper-cased to honor case-insensitive keying.</summary>
    private static IReadOnlyList<(string Dn, DiffStatus Status)> NodeProjection(SnapshotDiff diff) =>
        diff.NodeStatus
            .Select(kv => (Dn: kv.Key.ToUpperInvariant(), kv.Value))
            .OrderBy(p => p.Dn, StringComparer.Ordinal)
            .ToList();

    /// <summary>A stable, identity-free projection of EdgeStatus for content equality:
    /// sorted (parent|child, status) pairs, DNs upper-cased to honor case-insensitive
    /// keying.</summary>
    private static IReadOnlyList<(string Edge, DiffStatus Status)> EdgeProjection(SnapshotDiff diff) =>
        diff.EdgeStatus
            .Select(kv => (
                Edge: $"{kv.Key.ParentDn.ToUpperInvariant()}|{kv.Key.ChildDn.ToUpperInvariant()}",
                kv.Value))
            .OrderBy(p => p.Edge, StringComparer.Ordinal)
            .ToList();
}
