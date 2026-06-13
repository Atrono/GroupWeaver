using GroupWeaver.Core.Diff;
using GroupWeaver.Core.Graph;
using GroupWeaver.Core.Model;
using GroupWeaver.Core.Plan;

using Xunit;

namespace GroupWeaver.Tests.Core.Diff;

/// <summary>
/// Pins ADR-015 Slice 2 (#66): the gap render+summary layer that rides on top of the
/// Slice-1 <see cref="SnapshotDiff.Compute(DirectorySnapshot, DirectorySnapshot)"/> diff.
///
/// <para><b><see cref="SnapshotDiff.BuildUnion(DirectorySnapshot, DirectorySnapshot)"/></b>
/// (ADR-015 D3) — a static factory on the <see cref="SnapshotDiff"/> record producing a
/// FRESH <see cref="DirectorySnapshot"/> for RENDERING the gap graph, so a Removed Ist node
/// AND an Added Plan node both materialize to be painted. It <c>AddObject</c>s every DN from
/// both sides with <b>deterministic Ist-wins on Common DNs</b> (the real loaded Ist object
/// carries the whitelist-filtered <c>Attributes</c>; the plan object has none) — since
/// <c>AddObject</c> is latest-wins, plan objects are added FIRST, then Ist objects. It
/// <c>SetMembers</c> every parent that EITHER side has LOADED
/// (<c>ist.IsLoaded(P) || plan.IsLoaded(P)</c>) to the distinct (<see cref="Dn.Comparer"/>)
/// UNION of the two sides' members (a side that has NOT loaded P contributes none). It reads
/// <c>IsLoaded</c>/<c>GetMembers</c>/<c>Objects</c> only — never <c>.Edges</c> (no
/// O(E)-recompute) — and mutates NEITHER input.</para>
///
/// <para><b><see cref="GapSummary"/></b> — a flat counts record tallied by
/// <c>GapSummary.From(SnapshotDiff)</c> over <c>diff.NodeStatus</c> (only
/// Common/Added/Removed occur for nodes) and <c>diff.EdgeStatus</c>
/// (Common/Added/Removed/Unchecked). FINAL SHAPE pinned here (see the GapSummary_* tests):
/// <c>GapSummary(int AddedNodes, int RemovedNodes, int CommonNodes, int AddedEdges,
/// int RemovedEdges, int CommonEdges, int UncheckedEdges, int UncheckedParents)</c> — the
/// seven per-status node+edge counts are the core; the trailing <c>UncheckedParents</c> count
/// (= <c>diff.UncheckedParents.Count</c>) is added because ADR-015 D5 makes that honesty the
/// GapView's "N Ist areas unexpanded" banner and <c>From</c> already holds the diff, so
/// exposing it is free.</para>
///
/// <para>EQUALITY DISCIPLINE (rule-engine.md "compare PROJECTIONS, never whole records"):
/// <c>BuildUnion</c>'s snapshot has no value equality, so every union assertion compares
/// CONTENTS — sorted <c>Objects</c> DNs and per-parent sorted member projections — never
/// snapshot identity. <c>GapSummary</c> is a plain record, so it is compared field-by-field
/// (and cross-checked against the live tally of the diff maps).</para>
///
/// Hand-built fixtures only (pure Core, offline; mirrors <see cref="SnapshotDiffTests"/>'s
/// idioms). RED until <c>SnapshotDiff.BuildUnion</c> and <c>src/Core/Diff/GapSummary</c> exist.
/// </summary>
public class GapUnionTests
{
    private const string BaseOu = "OU=AGDLP-Lab,DC=agdlp,DC=lab";
    private const string DlDn = "CN=DL_FileShare_RW,OU=AGDLP-Lab,DC=agdlp,DC=lab";
    private const string GgDn = "CN=GG_Sales_EU,OU=AGDLP-Lab,DC=agdlp,DC=lab";
    private const string UserDn = "CN=Ada Lovelace,OU=AGDLP-Lab,DC=agdlp,DC=lab";

    // --- 1. Union completeness -----------------------------------------------------------

    /// <summary>
    /// <see cref="SnapshotDiff.BuildUnion"/>'s <c>Objects</c> is exactly the UNION of the
    /// ist and plan object DNs — an ist-only DN (Removed), a plan-only DN (Added), and a
    /// Common DN all materialize EXACTLY ONCE (so the gap render can paint Removed Ist nodes
    /// and Added Plan nodes side by side). The Common DN does not double-count.
    /// </summary>
    [Fact]
    public void BuildUnion_Objects_AreTheUnionOfBothSidesDns_EachExactlyOnce()
    {
        var ist = new DirectorySnapshot();
        ist.AddObject(Obj(DlDn, AdObjectKind.DomainLocalGroup)); // Common
        ist.AddObject(Obj(UserDn, AdObjectKind.User));           // ist-only (Removed)

        var plan = new DirectorySnapshot();
        plan.AddObject(Obj(DlDn, AdObjectKind.DomainLocalGroup)); // Common
        plan.AddObject(Obj(GgDn, AdObjectKind.GlobalGroup));      // plan-only (Added)

        var union = SnapshotDiff.BuildUnion(ist, plan);

        Assert.Equal(
            new[] { DlDn, GgDn, UserDn }.OrderBy(d => d, StringComparer.OrdinalIgnoreCase),
            SortedDns(union));
        Assert.Equal(3, union.Objects.Count); // no duplicate Common DN
    }

    // --- 2. Ist-wins kind clash (keystone) -----------------------------------------------

    /// <summary>
    /// THE KEYSTONE tie-break (ADR-015 D3): a Common DN authored as a
    /// <see cref="AdObjectKind.GlobalGroup"/> in the plan but loaded as a
    /// <see cref="AdObjectKind.DomainLocalGroup"/> (carrying a whitelisted Attribute) in the
    /// live Ist resolves to the IST object in the union — Kind is DomainLocalGroup and the
    /// Attribute is present. This is the read-only / whitelist-relevant choice (the real
    /// loaded object carries the provider-filtered Attributes; the plan object has none).
    /// Pinned so a future accidental flip to Plan-wins is caught. (Latest-wins
    /// <c>AddObject</c> ⇒ plan added first, Ist second.)
    /// </summary>
    [Fact]
    public void BuildUnion_CommonDnKindClash_IstObjectWins_KindAndAttributesAreIsts()
    {
        var ist = new DirectorySnapshot();
        ist.AddObject(new AdObject
        {
            Dn = DlDn,
            Kind = AdObjectKind.DomainLocalGroup, // Ist truth
            Name = "DL_FileShare_RW",
            Attributes = new Dictionary<string, string> { ["description"] = "loaded-from-AD" },
        });

        var plan = new DirectorySnapshot();
        plan.AddObject(Obj(DlDn, AdObjectKind.GlobalGroup)); // plan authored the WRONG kind, no Attributes

        var union = SnapshotDiff.BuildUnion(ist, plan);

        Assert.True(union.TryGetObject(DlDn, out var won));
        Assert.Equal(AdObjectKind.DomainLocalGroup, won!.Kind);           // Ist kind wins
        Assert.True(won.Attributes.TryGetValue("description", out var d)); // Ist Attributes survive
        Assert.Equal("loaded-from-AD", d);
    }

    // --- 3. Per-parent member union ------------------------------------------------------

    /// <summary>
    /// A parent P loaded on BOTH sides — ist members <c>{X}</c>, plan members <c>{Y}</c> —
    /// yields a union <c>GetMembers(P)</c> equal to the distinct (<see cref="Dn.Comparer"/>)
    /// union <c>{X, Y}</c>, and a member present on BOTH sides appears EXACTLY ONCE (so a
    /// Common edge is not drawn twice). <c>IsLoaded(P)</c> is true.
    /// </summary>
    [Fact]
    public void BuildUnion_ParentLoadedBothSides_MembersAreDistinctUnion()
    {
        const string sharedMember = GgDn;                                  // on both sides
        const string istOnly = UserDn;                                     // ist only
        const string planOnly = "CN=GG_Marketing,OU=AGDLP-Lab,DC=agdlp,DC=lab"; // plan only

        var ist = new DirectorySnapshot();
        ist.AddObject(Obj(DlDn, AdObjectKind.DomainLocalGroup));
        ist.SetMembers(DlDn, [sharedMember, istOnly]);

        var plan = new DirectorySnapshot();
        plan.AddObject(Obj(DlDn, AdObjectKind.DomainLocalGroup));
        plan.SetMembers(DlDn, [sharedMember, planOnly]);

        var union = SnapshotDiff.BuildUnion(ist, plan);

        Assert.True(union.IsLoaded(DlDn));
        Assert.Equal(
            new[] { sharedMember, istOnly, planOnly }
                .OrderBy(d => d, StringComparer.OrdinalIgnoreCase),
            SortedMembers(union, DlDn));
    }

    // --- 4. loaded-empty [] preserved on both sides --------------------------------------

    /// <summary>
    /// The null-vs-empty tri-state (data-model.md): P is loaded-EMPTY <c>[]</c> on BOTH
    /// sides ⇒ the union keeps it LOADED and EMPTY — <c>IsLoaded(P)</c> true and
    /// <c>GetMembers(P)</c> the empty list, NEVER <c>null</c> (a loaded-empty union parent
    /// must not regress to "never loaded").
    /// </summary>
    [Fact]
    public void BuildUnion_BothSidesLoadedEmpty_UnionIsLoadedAndEmpty_NotNull()
    {
        var ist = new DirectorySnapshot();
        ist.AddObject(Obj(DlDn, AdObjectKind.DomainLocalGroup));
        ist.SetMembers(DlDn, []);

        var plan = new DirectorySnapshot();
        plan.AddObject(Obj(DlDn, AdObjectKind.DomainLocalGroup));
        plan.SetMembers(DlDn, []);

        var union = SnapshotDiff.BuildUnion(ist, plan);

        Assert.True(union.IsLoaded(DlDn));
        var members = union.GetMembers(DlDn);
        Assert.NotNull(members);  // loaded, not the "never loaded" null
        Assert.Empty(members!);   // and genuinely empty
    }

    // --- 5. Ist-unloaded + plan members => union renders the plan edges -------------------

    /// <summary>
    /// P is a KNOWN Ist object that was NEVER loaded (<c>AddObject</c>, no <c>SetMembers</c>),
    /// but the plan loaded P with members <c>{Y}</c>. Because <c>plan.IsLoaded(P)</c> is true,
    /// the union <c>SetMembers</c> P to the union of the two sides' members — ist contributes
    /// none, plan contributes <c>{Y}</c> — so <c>union.IsLoaded(P)</c> is true and
    /// <c>GetMembers(P) == {Y}</c>: the plan edges MATERIALIZE for painting (their
    /// <c>Unchecked</c> STATUS is decided by <see cref="SnapshotDiff.Compute"/>, NOT by
    /// <c>BuildUnion</c>, which only assembles topology). And <c>BuildUnion</c> must NOT
    /// mutate the input ist — <c>ist.IsLoaded(P)</c> stays FALSE.
    /// </summary>
    [Fact]
    public void BuildUnion_IstParentUnloaded_PlanLoadedIt_UnionRendersPlanEdges_IstUntouched()
    {
        var ist = new DirectorySnapshot();
        ist.AddObject(Obj(DlDn, AdObjectKind.DomainLocalGroup)); // KNOWN, never loaded
        Assert.False(ist.IsLoaded(DlDn));

        var plan = new DirectorySnapshot();
        plan.AddObject(Obj(DlDn, AdObjectKind.DomainLocalGroup));
        plan.AddObject(Obj(GgDn, AdObjectKind.GlobalGroup));
        plan.SetMembers(DlDn, [GgDn]);

        var union = SnapshotDiff.BuildUnion(ist, plan);

        // The plan edge materializes in the union so it can be painted.
        Assert.True(union.IsLoaded(DlDn));
        Assert.Equal([GgDn], union.GetMembers(DlDn));

        // BuildUnion never mutates the borrowed Ist snapshot (ADR-015 D3 / ADR-005).
        Assert.False(ist.IsLoaded(DlDn));
        Assert.Null(ist.GetMembers(DlDn));
    }

    // --- 6. GraphBuilder over the union terminates on a cycle -----------------------------

    /// <summary>
    /// Totality + termination (the always-include-the-circular-case rule): a plan authoring a
    /// 2-cycle (A→B, B→A) projected and unioned against an empty Ist, then fed to the
    /// UNCHANGED <see cref="GraphBuilder.Build(DirectorySnapshot, string, GraphLayoutOptions?)"/>,
    /// must RETURN (not hang — <c>Timeout</c> guard) and keep BOTH cycle nodes and BOTH cycle
    /// edges. Proves <c>BuildUnion</c> + <c>GraphBuilder</c> stays total over the gap render's
    /// worst case. (House idiom: <c>[Fact(Timeout = ...)]</c> + run on a worker thread, per
    /// <c>GraphBuilderTests.BuildDemo_WithSeededCycle_Terminates_BothDirectionsKept</c>.)
    /// </summary>
    [Fact(Timeout = 60_000)]
    public async Task BuildUnion_OverPlanCycle_GraphBuilderTerminates_CycleNodesAndEdgesPresent()
    {
        var planModel = new PlanModel(BaseOu);
        var a = planModel.AddNode(PlanCreatableKind.GlobalGroup, "GG_Circle_A");
        var b = planModel.AddNode(PlanCreatableKind.GlobalGroup, "GG_Circle_B");
        planModel.AddEdge(a.Dn, b.Dn); // A -> B
        planModel.AddEdge(b.Dn, a.Dn); // B -> A  (the cycle the traversal must survive)
        var planSnapshot = PlanProjection.ToSnapshot(planModel);

        var union = SnapshotDiff.BuildUnion(new DirectorySnapshot(), planSnapshot);

        // Must terminate; root the graph at one cycle node.
        var model = await Task.Run(() => GraphBuilder.Build(union, a.Dn));

        // Both cycle nodes materialize.
        Assert.Contains(model.Nodes, n => Dn.Comparer.Equals(n.Dn, a.Dn));
        Assert.Contains(model.Nodes, n => Dn.Comparer.Equals(n.Dn, b.Dn));

        // Both directions of the cycle survive as membership edges.
        var membership = model.Edges
            .Where(e => e.Kind == GraphEdgeKind.Membership)
            .Select(e => new MembershipEdge(e.ParentDn, e.ChildDn))
            .ToHashSet();
        Assert.Contains(new MembershipEdge(a.Dn, b.Dn), membership);
        Assert.Contains(new MembershipEdge(b.Dn, a.Dn), membership);
    }

    // --- 7. GapSummary.From tallies match the diff maps ----------------------------------

    /// <summary>
    /// <c>GapSummary.From(diff)</c> tallies <c>diff.NodeStatus</c> (Common/Added/Removed) and
    /// <c>diff.EdgeStatus</c> (Common/Added/Removed/Unchecked) into the flat counts record.
    /// Constructed via the REAL <see cref="SnapshotDiff.Compute"/> over a known mix — 1 Added
    /// node, 1 Removed node, 2 Common nodes; 1 Added edge, 1 Removed edge, 1 Common edge, 1
    /// Unchecked edge — every count must equal BOTH the hand-counted expectation AND the live
    /// tally of the diff maps (so the summary can never silently drift from the diff it
    /// summarizes). <c>UncheckedParents</c> count is also pinned (= the one unloaded-known
    /// Ist parent).
    /// </summary>
    [Fact]
    public void GapSummary_From_TalliesEveryStatus_MatchingHandCountAndLiveDiffMaps()
    {
        // Common nodes: DL (loaded both) and a known-but-unloaded Ist parent U; Removed: User;
        // Added: GG. Edges: DL->X Common, DL->User Removed, DL->GG Added, U->GG Unchecked.
        const string xMember = "CN=GG_Eng,OU=AGDLP-Lab,DC=agdlp,DC=lab"; // Common edge child
        const string uParent = "CN=DL_Unexpanded,OU=AGDLP-Lab,DC=agdlp,DC=lab"; // Common node, unloaded Ist

        var ist = new DirectorySnapshot();
        ist.AddObject(Obj(DlDn, AdObjectKind.DomainLocalGroup));   // Common node
        ist.AddObject(Obj(uParent, AdObjectKind.DomainLocalGroup)); // Common node, never loaded
        ist.AddObject(Obj(xMember, AdObjectKind.GlobalGroup));     // Common node
        ist.AddObject(Obj(UserDn, AdObjectKind.User));             // Removed node
        ist.SetMembers(DlDn, [xMember, UserDn]);                   // DL->X (Common), DL->User (Removed)

        var plan = new DirectorySnapshot();
        plan.AddObject(Obj(DlDn, AdObjectKind.DomainLocalGroup));
        plan.AddObject(Obj(uParent, AdObjectKind.DomainLocalGroup));
        plan.AddObject(Obj(xMember, AdObjectKind.GlobalGroup));
        plan.AddObject(Obj(GgDn, AdObjectKind.GlobalGroup));       // Added node
        plan.SetMembers(DlDn, [xMember, GgDn]);                    // DL->X (Common), DL->GG (Added)
        plan.SetMembers(uParent, [GgDn]);                          // U->GG (Unchecked: U is known-Ist, unloaded)

        var diff = SnapshotDiff.Compute(ist, plan);
        var summary = GapSummary.From(diff);

        // Hand-counted expectation.
        Assert.Equal(1, summary.AddedNodes);
        Assert.Equal(1, summary.RemovedNodes);
        Assert.Equal(3, summary.CommonNodes); // DL, uParent, xMember
        Assert.Equal(1, summary.AddedEdges);
        Assert.Equal(1, summary.RemovedEdges);
        Assert.Equal(1, summary.CommonEdges);
        Assert.Equal(1, summary.UncheckedEdges);
        Assert.Equal(1, summary.UncheckedParents); // uParent

        // Cross-check against the LIVE tally of the diff maps (summary cannot drift).
        Assert.Equal(Count(diff.NodeStatus.Values, DiffStatus.Added), summary.AddedNodes);
        Assert.Equal(Count(diff.NodeStatus.Values, DiffStatus.Removed), summary.RemovedNodes);
        Assert.Equal(Count(diff.NodeStatus.Values, DiffStatus.Common), summary.CommonNodes);
        Assert.Equal(Count(diff.EdgeStatus.Values, DiffStatus.Added), summary.AddedEdges);
        Assert.Equal(Count(diff.EdgeStatus.Values, DiffStatus.Removed), summary.RemovedEdges);
        Assert.Equal(Count(diff.EdgeStatus.Values, DiffStatus.Common), summary.CommonEdges);
        Assert.Equal(Count(diff.EdgeStatus.Values, DiffStatus.Unchecked), summary.UncheckedEdges);
        Assert.Equal(diff.UncheckedParents.Count, summary.UncheckedParents);
    }

    // --- 8. Determinism (compare PROJECTIONS, never snapshot identity) -------------------

    /// <summary>
    /// Determinism (mirrors <c>SnapshotDiff.Compute</c>): <see cref="SnapshotDiff.BuildUnion"/>
    /// over two content-equal but differently-insertion-ordered <c>(ist, plan)</c> pairs
    /// yields PROJECTION-EQUAL union snapshots — compared by sorted <c>Objects</c> DNs and the
    /// per-parent sorted member projection, NEVER by snapshot identity (the union snapshot has
    /// no value equality). Includes a Common DN, an ist-only DN, a plan-only DN, a parent
    /// loaded on both sides, and a parent loaded only on the plan side over a known-unloaded
    /// Ist object (the mixed shape that exercises every <c>BuildUnion</c> arm).
    /// </summary>
    [Fact]
    public void BuildUnion_TwoContentEqualPairs_YieldProjectionEqualUnions_RegardlessOfOrder()
    {
        var unionA = SnapshotDiff.BuildUnion(BuildIst(), BuildPlan());

        // Pair B: same content, inserted in a different order.
        var istB = new DirectorySnapshot();
        istB.AddObject(Obj(UserDn, AdObjectKind.User));            // ist-only
        istB.AddObject(Obj("CN=DL_Unexpanded,OU=AGDLP-Lab,DC=agdlp,DC=lab", AdObjectKind.DomainLocalGroup)); // known, unloaded
        istB.AddObject(Obj(DlDn, AdObjectKind.DomainLocalGroup));  // Common, loaded both
        istB.SetMembers(DlDn, [GgDn]);
        var planB = new DirectorySnapshot();
        planB.SetMembers("CN=DL_Unexpanded,OU=AGDLP-Lab,DC=agdlp,DC=lab", [GgDn]); // plan loads the unloaded-Ist parent
        planB.AddObject(Obj("CN=DL_Unexpanded,OU=AGDLP-Lab,DC=agdlp,DC=lab", AdObjectKind.DomainLocalGroup));
        planB.AddObject(Obj(GgDn, AdObjectKind.GlobalGroup));      // plan-only
        planB.AddObject(Obj(DlDn, AdObjectKind.DomainLocalGroup));
        planB.SetMembers(DlDn, [GgDn]);
        var unionB = SnapshotDiff.BuildUnion(istB, planB);

        Assert.Equal(SortedDns(unionA), SortedDns(unionB));
        Assert.Equal(MemberProjection(unionA), MemberProjection(unionB));
    }

    private static DirectorySnapshot BuildIst()
    {
        var ist = new DirectorySnapshot();
        ist.AddObject(Obj(DlDn, AdObjectKind.DomainLocalGroup));   // Common, loaded both
        ist.AddObject(Obj(UserDn, AdObjectKind.User));             // ist-only
        ist.AddObject(Obj("CN=DL_Unexpanded,OU=AGDLP-Lab,DC=agdlp,DC=lab", AdObjectKind.DomainLocalGroup)); // known, unloaded
        ist.SetMembers(DlDn, [GgDn]);
        return ist;
    }

    private static DirectorySnapshot BuildPlan()
    {
        var plan = new DirectorySnapshot();
        plan.AddObject(Obj(DlDn, AdObjectKind.DomainLocalGroup));
        plan.AddObject(Obj(GgDn, AdObjectKind.GlobalGroup));       // plan-only
        plan.AddObject(Obj("CN=DL_Unexpanded,OU=AGDLP-Lab,DC=agdlp,DC=lab", AdObjectKind.DomainLocalGroup));
        plan.SetMembers(DlDn, [GgDn]);
        plan.SetMembers("CN=DL_Unexpanded,OU=AGDLP-Lab,DC=agdlp,DC=lab", [GgDn]); // plan loads it
        return plan;
    }

    // --- Helpers --------------------------------------------------------------------------

    private static AdObject Obj(string dn, AdObjectKind kind) => new()
    {
        Dn = dn,
        Kind = kind,
        Name = dn.Split(',')[0]["CN=".Length..],
    };

    /// <summary>Object DNs of a union snapshot, sorted OrdinalIgnoreCase (identity-free).</summary>
    private static IEnumerable<string> SortedDns(DirectorySnapshot snapshot) =>
        snapshot.Objects.Select(o => o.Dn).OrderBy(d => d, StringComparer.OrdinalIgnoreCase);

    /// <summary>Members of one parent, sorted OrdinalIgnoreCase (identity-free).</summary>
    private static IEnumerable<string> SortedMembers(DirectorySnapshot snapshot, string parentDn) =>
        (snapshot.GetMembers(parentDn) ?? Array.Empty<string>())
            .OrderBy(d => d, StringComparer.OrdinalIgnoreCase);

    /// <summary>A stable, identity-free projection of every loaded parent's member set:
    /// (parentDn upper, sorted-upper member list) pairs, ordered by parent DN — so two
    /// content-equal unions compare equal regardless of insertion order or case spelling.</summary>
    private static IReadOnlyList<(string Parent, string Members)> MemberProjection(DirectorySnapshot snapshot) =>
        snapshot.Objects
            .Select(o => o.Dn)
            .Where(snapshot.IsLoaded)
            .Select(p => (
                Parent: p.ToUpperInvariant(),
                Members: string.Join(
                    "|",
                    (snapshot.GetMembers(p) ?? Array.Empty<string>())
                        .Select(m => m.ToUpperInvariant())
                        .OrderBy(m => m, StringComparer.Ordinal))))
            .OrderBy(e => e.Parent, StringComparer.Ordinal)
            .ToList();

    private static int Count(IEnumerable<DiffStatus> statuses, DiffStatus target) =>
        statuses.Count(s => s == target);
}
