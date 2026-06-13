using GroupWeaver.Core.Diff;
using GroupWeaver.Core.Model;
using GroupWeaver.Core.Plan;

using Xunit;

namespace GroupWeaver.Tests.Core.Diff;

/// <summary>
/// Pins ADR-015 Slice 3 (#66, D6): the synthesized <see cref="GapReport"/> —
/// "what changed", a SEPARATE Core type from the pinned <c>RuleReport</c> (it never
/// pollutes that contract nor mints <c>RuleId</c>s) but shaped identically so the App
/// reuses the existing sidebar/<c>FocusAsync</c> machinery via a thin adapter.
///
/// <para>TYPE-SHAPE CHOICE (pinned): the record-with-static-factory shape, mirroring the
/// sibling <see cref="SnapshotDiff.Compute"/> and <c>GapSummary.From</c> already living in
/// this <c>Diff/</c> namespace — a <c>public sealed record GapReport(IReadOnlyList&lt;GapFinding&gt;
/// Findings)</c> with a static factory <c>GapReport.Build(SnapshotDiff diff,
/// DirectorySnapshot ist, DirectorySnapshot plan)</c>. (The ADR's alternative
/// <c>GapReportBuilder.Build(...)</c> is rejected here purely for namespace consistency —
/// the other two gap factories are static methods ON the result record, not separate
/// builder classes.) <see cref="GapFinding"/> is
/// <c>(GapKind Kind, IReadOnlyList&lt;string&gt; Dns, string Message)</c> with
/// <c>Dns[0]</c> the jump anchor and a presentation-only, culture-invariant
/// <c>Message</c>. <see cref="GapKind"/> ∈ {NodeAdded, NodeRemoved, EdgeAdded,
/// EdgeRemoved, UnverifiableArea}.</para>
///
/// <para>EDGE-ANCHOR CHOICE (pinned): an edge finding's <c>Dns</c> is
/// <c>[parentDn, childDn]</c> with <c>Dns[0] = parentDn</c> — the jump frames the GROUP
/// whose membership changed (mirroring nesting's <c>[parent, member]</c> in
/// <c>RuleViolation</c>). Node and UnverifiableArea findings carry a single-DN <c>Dns</c>.</para>
///
/// <para><c>Build</c> SEMANTICS (mirrors <c>RuleEngine.Evaluate</c> / <c>SnapshotDiff.Compute</c>):
/// pure, deterministic, total, UI-free, never throws on directory CONTENT.
/// <list type="bullet">
///   <item>Nodes: each <see cref="DiffStatus.Added"/> ⇒ one <c>NodeAdded([dn])</c>;
///   <see cref="DiffStatus.Removed"/> ⇒ <c>NodeRemoved([dn])</c>;
///   <see cref="DiffStatus.Common"/> ⇒ NO finding.</item>
///   <item>Edges: each <see cref="DiffStatus.Added"/> ⇒ <c>EdgeAdded([parent, child])</c>;
///   <see cref="DiffStatus.Removed"/> ⇒ <c>EdgeRemoved([parent, child])</c>;
///   <see cref="DiffStatus.Common"/> ⇒ NO finding; <see cref="DiffStatus.Unchecked"/> ⇒
///   NO per-edge finding (the parent is represented by UnverifiableArea instead).</item>
///   <item>Unverifiable: each DN in <c>diff.UncheckedParents</c> ⇒ one
///   <c>UnverifiableArea([parentDn])</c>.</item>
/// </list></para>
///
/// <para>NAME RESOLUTION (presentation only): an Added/plan-side subject resolves its
/// <c>Message</c> name via <c>plan.TryGetObject(dn).Name</c>; a Removed/ist-side subject
/// via <c>ist.TryGetObject(dn).Name</c>; an unresolvable DN falls back to the raw DN.
/// Per <c>.claude/rules/rule-engine.md</c> ("identity lives in the structured fields,
/// Message is presentation") tests assert the resolved NAME (or DN fallback) APPEARS in
/// the message and pin the finding STRUCTURE (Kind/Dns) — never the exact wording.</para>
///
/// <para>DETERMINISTIC ORDER (mirrors <c>RuleViolationComparer</c>): a fixed GapKind block
/// order — NodeAdded, NodeRemoved, EdgeAdded, EdgeRemoved, UnverifiableArea — then within a
/// block element-wise OrdinalIgnoreCase over <c>Dns</c>. Independent of dictionary /
/// insertion order.</para>
///
/// <para>EQUALITY DISCIPLINE (rule-engine.md "compare PROJECTIONS, never whole records"):
/// <see cref="GapFinding"/> record equality is value-based over its fields, but to stay
/// faithful to the RuleViolation discipline every assertion compares PROJECTIONS
/// (Kind + the <c>Dns</c> sequence, and the resolved-name substring for messages) — never
/// whole-record identity, and never byte-for-byte message wording.</para>
///
/// Hand-built fixtures only (pure Core, offline; mirrors <see cref="SnapshotDiffTests"/> /
/// <see cref="GapUnionTests"/> idioms). RED until
/// <c>src/Core/Diff/GapReport.cs</c> (<c>GapKind</c> / <c>GapFinding</c> / <c>GapReport</c>)
/// exists — the Core test assembly will not compile until those symbols are defined.
/// </summary>
public class GapReportTests
{
    private const string BaseOu = "OU=AGDLP-Lab,DC=agdlp,DC=lab";
    private const string DlDn = "CN=DL_FileShare_RW,OU=AGDLP-Lab,DC=agdlp,DC=lab";
    private const string GgDn = "CN=GG_Sales_EU,OU=AGDLP-Lab,DC=agdlp,DC=lab";
    private const string UserDn = "CN=Ada Lovelace,OU=AGDLP-Lab,DC=agdlp,DC=lab";

    // --- 1. Node findings: Added / Removed; Common => none -------------------------------

    /// <summary>
    /// Node findings mirror <c>diff.NodeStatus</c>: a plan-only DN
    /// (<see cref="DiffStatus.Added"/>) yields EXACTLY ONE
    /// <see cref="GapKind.NodeAdded"/> anchored on it (<c>Dns = [dn]</c>, <c>Dns[0] =
    /// dn</c>); an ist-only DN (<see cref="DiffStatus.Removed"/>) yields exactly one
    /// <see cref="GapKind.NodeRemoved"/>; a <see cref="DiffStatus.Common"/> DN yields NO
    /// node finding at all.
    /// </summary>
    [Fact]
    public void Build_NodeFindings_AddedAndRemoved_CommonProducesNothing()
    {
        var ist = new DirectorySnapshot();
        ist.AddObject(Obj(DlDn, AdObjectKind.DomainLocalGroup)); // Common -> no finding
        ist.AddObject(Obj(UserDn, AdObjectKind.User));           // ist-only -> NodeRemoved

        var plan = new DirectorySnapshot();
        plan.AddObject(Obj(DlDn, AdObjectKind.DomainLocalGroup)); // Common -> no finding
        plan.AddObject(Obj(GgDn, AdObjectKind.GlobalGroup));      // plan-only -> NodeAdded

        var report = Build(ist, plan);

        // Exactly one NodeAdded anchored on the plan-only DN.
        var added = Single(report, GapKind.NodeAdded);
        Assert.Equal([GgDn], added.Dns);
        Assert.Equal(GgDn, added.Dns[0]); // jump anchor

        // Exactly one NodeRemoved anchored on the ist-only DN.
        var removed = Single(report, GapKind.NodeRemoved);
        Assert.Equal([UserDn], removed.Dns);
        Assert.Equal(UserDn, removed.Dns[0]);

        // The Common DN never appears in any node finding.
        Assert.DoesNotContain(Projections(report), p => p.Dns.Contains(DlDn));

        // No edges in this fixture -> no edge/area findings either.
        Assert.DoesNotContain(report.Findings, f => f.Kind is not (GapKind.NodeAdded or GapKind.NodeRemoved));
    }

    // --- 2. Edge findings over LOADED parents: Added / Removed; Common => none -----------

    /// <summary>
    /// Edge findings mirror <c>diff.EdgeStatus</c> over LOADED parents: a plan-only edge
    /// ⇒ one <see cref="GapKind.EdgeAdded"/> with <c>Dns = [parent, child]</c> and
    /// <c>Dns[0] = parent</c> (the anchor frames the group); an ist-only edge ⇒ one
    /// <see cref="GapKind.EdgeRemoved"/>; a <see cref="DiffStatus.Common"/> edge ⇒ NO edge
    /// finding. No parent here is unloaded, so there is NO
    /// <see cref="GapKind.UnverifiableArea"/>.
    /// </summary>
    [Fact]
    public void Build_EdgeFindings_OverLoadedParents_AddedRemoved_CommonProducesNothing()
    {
        const string anotherGg = "CN=GG_Marketing,OU=AGDLP-Lab,DC=agdlp,DC=lab";

        var ist = new DirectorySnapshot();
        ist.AddObject(Obj(DlDn, AdObjectKind.DomainLocalGroup));
        ist.AddObject(Obj(GgDn, AdObjectKind.GlobalGroup));
        ist.AddObject(Obj(UserDn, AdObjectKind.User));
        ist.SetMembers(DlDn, [GgDn, UserDn]); // DL->GG Common, DL->User Removed

        var plan = new DirectorySnapshot();
        plan.AddObject(Obj(DlDn, AdObjectKind.DomainLocalGroup));
        plan.AddObject(Obj(GgDn, AdObjectKind.GlobalGroup));
        plan.AddObject(Obj(anotherGg, AdObjectKind.GlobalGroup));
        plan.SetMembers(DlDn, [GgDn, anotherGg]); // DL->GG Common, DL->anotherGg Added

        var report = Build(ist, plan);

        // Plan-only edge -> EdgeAdded with parent-anchored [parent, child].
        var added = Single(report, GapKind.EdgeAdded);
        Assert.Equal([DlDn, anotherGg], added.Dns);
        Assert.Equal(DlDn, added.Dns[0]); // parent is the anchor

        // Ist-only edge -> EdgeRemoved, also parent-anchored.
        var removed = Single(report, GapKind.EdgeRemoved);
        Assert.Equal([DlDn, UserDn], removed.Dns);
        Assert.Equal(DlDn, removed.Dns[0]);

        // The Common edge produces no finding (no [DL, GG] pair anywhere).
        Assert.DoesNotContain(
            Projections(report),
            p => p.Kind is GapKind.EdgeAdded or GapKind.EdgeRemoved
                && p.Dns.Count == 2
                && Dn.Comparer.Equals(p.Dns[1], GgDn));

        // No unloaded parents -> no UnverifiableArea.
        Assert.DoesNotContain(report.Findings, f => f.Kind == GapKind.UnverifiableArea);
    }

    // --- 3. KEYSTONE: Unchecked => UnverifiableArea, NOT EdgeAdded -----------------------

    /// <summary>
    /// THE KEYSTONE pin (ADR-015 D5 / D6): a plan edge under a KNOWN-but-unloaded Ist
    /// parent is classified <see cref="DiffStatus.Unchecked"/> by
    /// <see cref="SnapshotDiff.Compute"/>, so <see cref="GapReport.Build"/> emits NO
    /// per-edge finding for it — instead EXACTLY ONE
    /// <see cref="GapKind.UnverifiableArea"/> anchored on the parent (matching
    /// <c>diff.UncheckedParents</c>). An unverifiable area must NEVER masquerade as an
    /// EdgeAdded (that would falsely claim the plan adds an edge the Ist might already
    /// have).
    /// </summary>
    [Fact]
    public void Build_UncheckedEdge_YieldsUnverifiableArea_NotEdgeAdded()
    {
        // Ist KNOWS DL but never expanded it (no SetMembers) -> known + unloaded.
        var ist = new DirectorySnapshot();
        ist.AddObject(Obj(DlDn, AdObjectKind.DomainLocalGroup));
        Assert.True(ist.TryGetObject(DlDn, out _));
        Assert.False(ist.IsLoaded(DlDn));

        // Plan proposes DL has member GG.
        var plan = new DirectorySnapshot();
        plan.AddObject(Obj(DlDn, AdObjectKind.DomainLocalGroup));
        plan.AddObject(Obj(GgDn, AdObjectKind.GlobalGroup));
        plan.SetMembers(DlDn, [GgDn]);

        var diff = SnapshotDiff.Compute(ist, plan);
        Assert.Equal([DlDn], diff.UncheckedParents); // precondition: the edge is Unchecked

        var report = GapReport.Build(diff, ist, plan);

        // NO EdgeAdded (and no EdgeRemoved) for the unchecked edge.
        Assert.DoesNotContain(report.Findings, f => f.Kind is GapKind.EdgeAdded or GapKind.EdgeRemoved);

        // Exactly one UnverifiableArea, anchored on the parent, matching UncheckedParents.
        var area = Single(report, GapKind.UnverifiableArea);
        Assert.Equal([DlDn], area.Dns);
        Assert.Equal(DlDn, area.Dns[0]);
        Assert.Equal(
            diff.UncheckedParents,
            report.Findings.Where(f => f.Kind == GapKind.UnverifiableArea).Select(f => f.Dns[0]));
    }

    // --- 4. Demo-shaped case: removing one edge of a seeded A<->B cycle ------------------

    /// <summary>
    /// The always-include-the-circular-case demo shape: the Ist holds the seeded
    /// 2-cycle (A→B AND B→A); the plan keeps only A→B. Over loaded parents the diff is
    /// A→B Common (no finding) and B→A Removed, so <see cref="GapReport.Build"/> yields
    /// EXACTLY ONE <see cref="GapKind.EdgeRemoved"/> anchored on <c>[B, A]</c> and the
    /// surviving A→B produces nothing. Asserted by Kind + <c>Dns</c> only (never message
    /// wording), and the traversal-free <c>Build</c> simply enumerates the diff maps, so a
    /// cycle in the inputs cannot make it loop.
    /// </summary>
    [Fact]
    public void Build_DemoShapedCycleEdgeRemoved_OneEdgeRemovedOnBA_SurvivorIsCommon()
    {
        const string aDn = "CN=GG_Circle_A,OU=AGDLP-Lab,DC=agdlp,DC=lab";
        const string bDn = "CN=GG_Circle_B,OU=AGDLP-Lab,DC=agdlp,DC=lab";

        // Ist: the full seeded cycle A->B and B->A.
        var ist = new DirectorySnapshot();
        ist.AddObject(Obj(aDn, AdObjectKind.GlobalGroup));
        ist.AddObject(Obj(bDn, AdObjectKind.GlobalGroup));
        ist.SetMembers(aDn, [bDn]); // A -> B
        ist.SetMembers(bDn, [aDn]); // B -> A

        // Plan: keep only A->B (the cycle is broken; B has no members).
        var plan = new DirectorySnapshot();
        plan.AddObject(Obj(aDn, AdObjectKind.GlobalGroup));
        plan.AddObject(Obj(bDn, AdObjectKind.GlobalGroup));
        plan.SetMembers(aDn, [bDn]); // A -> B (Common)
        plan.SetMembers(bDn, []);    // B loaded-empty -> B->A is Removed

        var report = Build(ist, plan);

        // Exactly one edge finding, and it is EdgeRemoved on [B, A].
        var removed = Single(report, GapKind.EdgeRemoved);
        Assert.Equal([bDn, aDn], removed.Dns);
        Assert.Equal(bDn, removed.Dns[0]); // parent B is the anchor

        // The surviving A->B is Common -> no EdgeAdded/EdgeRemoved for it.
        Assert.DoesNotContain(report.Findings, f => f.Kind == GapKind.EdgeAdded);
        Assert.DoesNotContain(
            report.Findings,
            f => f.Kind == GapKind.EdgeRemoved && f.Dns.Count == 2 && Dn.Comparer.Equals(f.Dns[0], aDn));

        // Both A and B are Common nodes -> no node findings.
        Assert.DoesNotContain(report.Findings, f => f.Kind is GapKind.NodeAdded or GapKind.NodeRemoved);
    }

    // --- 5. Name resolution in Message (presentation only) ------------------------------

    /// <summary>
    /// <c>Message</c> name resolution (presentation only, per rule-engine.md): a
    /// <see cref="GapKind.NodeAdded"/> message contains the PLAN object's
    /// <see cref="AdObject.Name"/> (resolved via <c>plan.TryGetObject</c>), a
    /// <see cref="GapKind.NodeRemoved"/> message contains the IST object's Name (resolved
    /// via <c>ist.TryGetObject</c>), and a subject with NO resolvable object on its
    /// resolution side falls back to the raw DN in the message. Structure (Kind/Dns) is
    /// asserted alongside; exact wording is never pinned.
    /// </summary>
    [Fact]
    public void Build_Message_ResolvesPlanNameForAdded_IstNameForRemoved_DnFallbackWhenAbsent()
    {
        // Added subject: present in plan with a friendly Name.
        var addedDn = "CN=GG_Friendly_Added,OU=AGDLP-Lab,DC=agdlp,DC=lab";
        const string addedName = "GG_Friendly_Added";

        // Removed subject: present in ist with a friendly Name.
        var removedDn = "CN=GG_Friendly_Removed,OU=AGDLP-Lab,DC=agdlp,DC=lab";
        const string removedName = "GG_Friendly_Removed";

        var ist = new DirectorySnapshot();
        ist.AddObject(new AdObject { Dn = removedDn, Kind = AdObjectKind.GlobalGroup, Name = removedName });

        var plan = new DirectorySnapshot();
        plan.AddObject(new AdObject { Dn = addedDn, Kind = AdObjectKind.GlobalGroup, Name = addedName });

        var report = Build(ist, plan);

        // NodeAdded message resolves the PLAN object's Name.
        var added = Single(report, GapKind.NodeAdded);
        Assert.Equal([addedDn], added.Dns);
        Assert.Contains(addedName, added.Message, StringComparison.Ordinal);

        // NodeRemoved message resolves the IST object's Name.
        var removed = Single(report, GapKind.NodeRemoved);
        Assert.Equal([removedDn], removed.Dns);
        Assert.Contains(removedName, removed.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The DN-fallback half of name resolution: an UnverifiableArea anchored on a
    /// KNOWN-but-unloaded Ist parent that has NO <see cref="AdObject.Name"/> resolvable on
    /// its resolution side falls back to the raw DN in the <c>Message</c>. We make the
    /// parent unresolvable by referencing it ONLY as the parent of a plan edge (the Ist
    /// "knows" it exists for the diff's purposes via the membership reference, but it
    /// carries no object Name to resolve), so the message must contain the DN verbatim.
    /// </summary>
    [Fact]
    public void Build_Message_FallsBackToDn_WhenSubjectHasNoResolvableObject()
    {
        // A node Added whose DN is in NEITHER snapshot's Objects as a resolvable name:
        // we author it only as the CHILD of an edge over a loaded plan parent, so the
        // child DN is a node in the diff (it appears in EdgeStatus) but resolves to no
        // AdObject Name -> the EdgeAdded message must fall back to the DN.
        const string parentDn = "CN=GG_Parent,OU=AGDLP-Lab,DC=agdlp,DC=lab";
        const string namelessChild = "CN=GG_Nameless_Child,OU=AGDLP-Lab,DC=agdlp,DC=lab";

        var ist = new DirectorySnapshot();
        ist.AddObject(Obj(parentDn, AdObjectKind.GlobalGroup));
        ist.SetMembers(parentDn, []); // loaded-empty -> the plan edge below is Added, not Unchecked

        var plan = new DirectorySnapshot();
        plan.AddObject(Obj(parentDn, AdObjectKind.GlobalGroup));
        // namelessChild is referenced as a member but NEVER AddObject'd -> no resolvable Name.
        plan.SetMembers(parentDn, [namelessChild]);

        var report = Build(ist, plan);

        // The edge is Added; its message must fall back to the raw child DN (no Name to resolve).
        var added = Single(report, GapKind.EdgeAdded);
        Assert.Equal([parentDn, namelessChild], added.Dns);
        Assert.Contains(namelessChild, added.Message, StringComparison.Ordinal);
    }

    // --- 6. Determinism + block ordering -------------------------------------------------

    /// <summary>
    /// Determinism (mirrors <c>RuleEngine.Evaluate</c> / <c>SnapshotDiff.Compute</c>): two
    /// content-equal but differently-insertion-ordered <c>(ist, plan)</c> pairs produce
    /// PROJECTION-EQUAL reports — compared as the ORDERED sequence of <c>(Kind, Dns)</c>
    /// projections, NEVER record identity. AND the fixed GapKind BLOCK ORDER holds
    /// regardless of input order: every <see cref="GapKind.NodeAdded"/> precedes every
    /// <see cref="GapKind.NodeRemoved"/>, which precedes every
    /// <see cref="GapKind.EdgeAdded"/>, then <see cref="GapKind.EdgeRemoved"/>, then
    /// <see cref="GapKind.UnverifiableArea"/>; within a block, element-wise
    /// OrdinalIgnoreCase over <c>Dns</c>.
    /// </summary>
    [Fact]
    public void Build_TwoContentEqualPairs_ProjectionEqual_AndBlockOrderHolds()
    {
        var reportA = Build(BuildMixedIst(), BuildMixedPlan());

        // Pair B: SAME content, inserted in a DIFFERENT order.
        var istB = new DirectorySnapshot();
        istB.AddObject(Obj(UserDn, AdObjectKind.User)); // Removed node + Removed edge child
        istB.AddObject(Obj("CN=DL_Unexpanded,OU=AGDLP-Lab,DC=agdlp,DC=lab", AdObjectKind.DomainLocalGroup)); // known, unloaded -> UnverifiableArea
        istB.AddObject(Obj(DlDn, AdObjectKind.DomainLocalGroup));
        istB.AddObject(Obj(GgDn, AdObjectKind.GlobalGroup));
        istB.SetMembers(DlDn, [GgDn, UserDn]); // DL->GG Common, DL->User Removed

        var planB = new DirectorySnapshot();
        planB.SetMembers("CN=DL_Unexpanded,OU=AGDLP-Lab,DC=agdlp,DC=lab", [GgDn]); // edge under unloaded Ist parent -> Unchecked
        planB.AddObject(Obj("CN=DL_Unexpanded,OU=AGDLP-Lab,DC=agdlp,DC=lab", AdObjectKind.DomainLocalGroup));
        planB.AddObject(Obj("CN=GG_Added,OU=AGDLP-Lab,DC=agdlp,DC=lab", AdObjectKind.GlobalGroup)); // Added node
        planB.AddObject(Obj(DlDn, AdObjectKind.DomainLocalGroup));
        planB.AddObject(Obj(GgDn, AdObjectKind.GlobalGroup));
        planB.SetMembers(DlDn, [GgDn, "CN=GG_Added,OU=AGDLP-Lab,DC=agdlp,DC=lab"]); // DL->GG Common, DL->GG_Added Added

        var reportB = Build(istB, planB);

        // Projection-equal regardless of insertion order. Compared as VALUE-comparable
        // string keys (Kind + joined Dns) so Assert.Equal compares the projection
        // SEQUENCE by value, never by the inner list's reference identity (mirrors the
        // string.Join projection idiom in SnapshotDiffTests/GapUnionTests).
        Assert.Equal(ProjectionKeys(reportA), ProjectionKeys(reportB));

        // Block order holds: the first index of each kind is strictly non-decreasing
        // in the canonical block sequence.
        AssertBlockOrder(reportA);
        AssertBlockOrder(reportB);
    }

    // --- 7. Empty / empty => empty Findings ----------------------------------------------

    /// <summary>
    /// Totality at the boundary: diffing two empty snapshots yields an empty
    /// <see cref="GapReport.Findings"/> — no throw, no synthetic findings.
    /// </summary>
    [Fact]
    public void Build_EmptyVsEmpty_YieldsEmptyFindings()
    {
        var report = Build(new DirectorySnapshot(), new DirectorySnapshot());

        Assert.Empty(report.Findings);
    }

    // --- 8. No drift: ist == projection of the SAME plan => empty Findings ---------------

    /// <summary>
    /// The demo-shaped "no drift" case: when the Ist IS
    /// <c>PlanProjection.ToSnapshot(samePlan)</c> and the plan is the projection of the
    /// SAME plan, every node and edge is <see cref="DiffStatus.Common"/> and there are no
    /// unexpanded areas, so <see cref="GapReport.Build"/> produces ZERO findings (Gap
    /// answers "what changed", and nothing changed). Includes an empty group (loaded-[])
    /// and a user leaf, proving neither produces a spurious finding.
    /// </summary>
    [Fact]
    public void Build_IstEqualsProjectionOfSamePlan_YieldsEmptyFindings()
    {
        var planModel = new PlanModel(BaseOu);
        var dl = planModel.AddNode(PlanCreatableKind.DomainLocalGroup, "DL_FileShare_RW");
        var gg = planModel.AddNode(PlanCreatableKind.GlobalGroup, "GG_Sales_EU");
        var user = planModel.AddNode(PlanCreatableKind.User, "Ada Lovelace");
        planModel.AddNode(PlanCreatableKind.GlobalGroup, "GG_Empty"); // loaded-[] empty group
        planModel.AddEdge(dl.Dn, gg.Dn);
        planModel.AddEdge(gg.Dn, user.Dn);

        var projection = PlanProjection.ToSnapshot(planModel);

        var report = GapReport.Build(SnapshotDiff.Compute(projection, projection), projection, projection);

        Assert.Empty(report.Findings);
    }

    // --- Fixtures for the determinism test -----------------------------------------------

    private static DirectorySnapshot BuildMixedIst()
    {
        var ist = new DirectorySnapshot();
        ist.AddObject(Obj(DlDn, AdObjectKind.DomainLocalGroup));
        ist.AddObject(Obj(GgDn, AdObjectKind.GlobalGroup));
        ist.AddObject(Obj(UserDn, AdObjectKind.User)); // Removed node + Removed edge child
        ist.AddObject(Obj("CN=DL_Unexpanded,OU=AGDLP-Lab,DC=agdlp,DC=lab", AdObjectKind.DomainLocalGroup)); // known, unloaded -> UnverifiableArea
        ist.SetMembers(DlDn, [GgDn, UserDn]); // DL->GG Common, DL->User Removed
        return ist;
    }

    private static DirectorySnapshot BuildMixedPlan()
    {
        var plan = new DirectorySnapshot();
        plan.AddObject(Obj(DlDn, AdObjectKind.DomainLocalGroup));
        plan.AddObject(Obj(GgDn, AdObjectKind.GlobalGroup));
        plan.AddObject(Obj("CN=GG_Added,OU=AGDLP-Lab,DC=agdlp,DC=lab", AdObjectKind.GlobalGroup)); // Added node
        plan.AddObject(Obj("CN=DL_Unexpanded,OU=AGDLP-Lab,DC=agdlp,DC=lab", AdObjectKind.DomainLocalGroup));
        plan.SetMembers(DlDn, [GgDn, "CN=GG_Added,OU=AGDLP-Lab,DC=agdlp,DC=lab"]); // DL->GG Common, DL->GG_Added Added
        plan.SetMembers("CN=DL_Unexpanded,OU=AGDLP-Lab,DC=agdlp,DC=lab", [GgDn]); // edge under unloaded Ist parent -> Unchecked
        return plan;
    }

    // --- Helpers --------------------------------------------------------------------------

    private static AdObject Obj(string dn, AdObjectKind kind) => new()
    {
        Dn = dn,
        Kind = kind,
        Name = dn.Split(',')[0]["CN=".Length..],
    };

    /// <summary>Runs the full pipeline: <c>Compute</c> then <c>GapReport.Build</c>.</summary>
    private static GapReport Build(DirectorySnapshot ist, DirectorySnapshot plan) =>
        GapReport.Build(SnapshotDiff.Compute(ist, plan), ist, plan);

    /// <summary>The single finding of <paramref name="kind"/> (asserts exactly one).</summary>
    private static GapFinding Single(GapReport report, GapKind kind) =>
        Assert.Single(report.Findings, f => f.Kind == kind);

    /// <summary>An identity-free projection of every finding: (Kind, Dns sequence) in
    /// report order — the comparison surface (rule-engine.md "compare PROJECTIONS").</summary>
    private static IReadOnlyList<(GapKind Kind, IReadOnlyList<string> Dns)> Projections(GapReport report) =>
        report.Findings.Select(f => (f.Kind, (IReadOnlyList<string>)f.Dns.ToList())).ToList();

    /// <summary>A VALUE-comparable flattening of <see cref="Projections"/> for sequence
    /// equality: each finding collapsed to a single <c>"Kind|dn|dn"</c> string so
    /// <c>Assert.Equal</c> compares the projection sequence by value (the inner
    /// <c>Dns</c> list otherwise compares by reference inside the tuple — distinct
    /// instances never equal). Mirrors the <c>string.Join</c> projection idiom in
    /// <see cref="SnapshotDiffTests"/> / <see cref="GapUnionTests"/>. Still a PROJECTION
    /// (Kind + Dns sequence), never whole-record identity.</summary>
    private static IReadOnlyList<string> ProjectionKeys(GapReport report) =>
        report.Findings.Select(f => $"{f.Kind}|{string.Join("|", f.Dns)}").ToList();

    /// <summary>Asserts the fixed GapKind block order: each finding's kind block index is
    /// non-decreasing through the report, and within equal kinds the <c>Dns</c> are
    /// element-wise OrdinalIgnoreCase non-decreasing.</summary>
    private static void AssertBlockOrder(GapReport report)
    {
        var blockOrder = new[]
        {
            GapKind.NodeAdded, GapKind.NodeRemoved, GapKind.EdgeAdded, GapKind.EdgeRemoved, GapKind.UnverifiableArea,
        };
        int BlockIndex(GapKind k) => Array.IndexOf(blockOrder, k);

        for (var i = 1; i < report.Findings.Count; i++)
        {
            var prev = report.Findings[i - 1];
            var cur = report.Findings[i];
            var byBlock = BlockIndex(prev.Kind).CompareTo(BlockIndex(cur.Kind));
            Assert.True(byBlock <= 0, $"block order violated at index {i}: {prev.Kind} before {cur.Kind}");

            if (byBlock == 0)
            {
                Assert.True(
                    DnsCompare(prev.Dns, cur.Dns) <= 0,
                    $"within-block Dns order violated at index {i}");
            }
        }
    }

    /// <summary>Element-wise OrdinalIgnoreCase over Dns, shorter-prefix-first
    /// (mirrors <c>RuleViolationComparer</c>).</summary>
    private static int DnsCompare(IReadOnlyList<string> x, IReadOnlyList<string> y)
    {
        var shared = Math.Min(x.Count, y.Count);
        for (var i = 0; i < shared; i++)
        {
            var byElement = StringComparer.OrdinalIgnoreCase.Compare(x[i], y[i]);
            if (byElement != 0)
            {
                return byElement;
            }
        }

        return x.Count.CompareTo(y.Count);
    }
}
