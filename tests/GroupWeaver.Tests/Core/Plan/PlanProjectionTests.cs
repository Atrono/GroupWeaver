using GroupWeaver.Core.Graph;
using GroupWeaver.Core.Model;
using GroupWeaver.Core.Plan;
using GroupWeaver.Core.Rules;

using Xunit;

namespace GroupWeaver.Tests.Core.Plan;

/// <summary>
/// Pins the reuse thesis of ADR-014: <see cref="PlanProjection.ToSnapshot"/> builds a
/// plain <see cref="DirectorySnapshot"/> that the UNCHANGED
/// <see cref="RuleEngine.Evaluate"/> and <see cref="GraphBuilder.Build"/> consume.
/// <list type="bullet">
/// <item>EVERY group gets <c>SetMembers</c> — even an empty one, which becomes the
/// loaded-empty <c>[]</c> arm (never <c>null</c>) so the empty-group rule fires and
/// <see cref="RuleReport.UncheckedDns"/> is EMPTY by construction (a plan is fully
/// authored: nothing is "unexpanded"). Users (non-parents) are never
/// <c>SetMembers</c>'d.</item>
/// <item>Projected typed kinds drive the rules: a new empty group → empty-group info;
/// a user directly in a DL → a nesting error (DL←User deny); an A→B→A plan → a
/// circular error.</item>
/// <item><see cref="GraphBuilder.Build"/> on the projection runs and produces a node
/// for every plan object (plus the synthesized base-OU root).</item>
/// </list>
/// Findings compared via PROJECTIONS (RuleId/Severity/Dns), never RuleViolation
/// record equality. Hand-built fixtures, default ruleset with the global ignore list
/// cleared (the plan DNs sit under OU=AGDLP-Lab, not in any default-ignore container).
/// RED until <c>src/Core/Plan</c> exists.
/// </summary>
public class PlanProjectionTests
{
    private const string BaseOu = "OU=AGDLP-Lab,DC=agdlp,DC=lab";

    // --- Projection mechanics: groups loaded (incl. empty []), users never loaded --------

    [Fact]
    public void ToSnapshot_SetsMembersForEveryGroup_IncludingEmptyAsLoadedEmptyList()
    {
        var plan = new PlanModel(BaseOu);
        var dl = plan.AddNode(PlanCreatableKind.DomainLocalGroup, "DL_FileShare_RW");
        var gg = plan.AddNode(PlanCreatableKind.GlobalGroup, "GG_Sales_EU");
        var emptyGg = plan.AddNode(PlanCreatableKind.GlobalGroup, "GG_Empty");
        var user = plan.AddNode(PlanCreatableKind.User, "Ada Lovelace");
        plan.AddEdge(dl.Dn, gg.Dn);
        plan.AddEdge(gg.Dn, user.Dn);

        var snapshot = PlanProjection.ToSnapshot(plan);

        // Every object projected with the right typed kind.
        Assert.True(snapshot.TryGetObject(dl.Dn, out var dlObj));
        Assert.Equal(AdObjectKind.DomainLocalGroup, dlObj!.Kind);
        Assert.Equal(AdObjectKind.GlobalGroup, snapshot.GetKind(gg.Dn));
        Assert.Equal(AdObjectKind.User, snapshot.GetKind(user.Dn));

        // Groups are LOADED: members reflect the authored out-edges, in stored order.
        Assert.Equal(new[] { gg.Dn }, snapshot.GetMembers(dl.Dn));
        Assert.Equal(new[] { user.Dn }, snapshot.GetMembers(gg.Dn));

        // An authored-but-childless group is loaded-EMPTY [] — never null (the
        // null-vs-empty tri-state: a plan group is loaded-and-genuinely-empty).
        Assert.True(snapshot.IsLoaded(emptyGg.Dn));
        Assert.NotNull(snapshot.GetMembers(emptyGg.Dn));
        Assert.Empty(snapshot.GetMembers(emptyGg.Dn)!);

        // A user is a leaf: never SetMembers'd (it is never a parent).
        Assert.False(snapshot.IsLoaded(user.Dn));
        Assert.Null(snapshot.GetMembers(user.Dn));
    }

    [Fact]
    public void ToSnapshot_ProjectsNoAttributes_WhitelistStaysMinimal()
    {
        var plan = new PlanModel(BaseOu);
        var gg = plan.AddNode(PlanCreatableKind.GlobalGroup, "GG_Sales", sam: "GG_Sales");

        var snapshot = PlanProjection.ToSnapshot(plan);

        Assert.True(snapshot.TryGetObject(gg.Dn, out var obj));
        Assert.Equal("GG_Sales", obj!.Name);
        Assert.Equal("GG_Sales", obj.SamAccountName);
        Assert.Empty(obj.Attributes); // detail-panel whitelist: nothing beyond the typed fields
    }

    // --- Evaluate on the projection: expected findings, UncheckedDns EMPTY ---------------

    [Fact]
    public void Evaluate_FreshEmptyGroupPlan_YieldsEmptyGroupInfo_AndNoUncheckedDns()
    {
        var plan = new PlanModel(BaseOu);
        var gg = plan.AddNode(PlanCreatableKind.GlobalGroup, "GG_Empty");

        var report = RuleEngine.Evaluate(PlanProjection.ToSnapshot(plan), Defaults());

        var finding = Assert.Single(report.Violations, v => v.RuleId == RuleIds.EmptyGroup);
        Assert.Equal(RuleSeverity.Info, finding.Severity);
        Assert.Equal(new[] { gg.Dn }, finding.Dns);

        // A fully-authored plan has NOTHING unexpanded — the frontier is empty.
        Assert.Empty(report.UncheckedDns);
    }

    [Fact]
    public void Evaluate_UserDirectlyInADomainLocalGroup_YieldsNestingError()
    {
        // The classic AGDLP violation: an account placed straight into a DL
        // (accounts belong in global groups). DL<-User is a deny cell (Error).
        var plan = new PlanModel(BaseOu);
        var dl = plan.AddNode(PlanCreatableKind.DomainLocalGroup, "DL_FileShare_RW");
        var user = plan.AddNode(PlanCreatableKind.User, "Ada Lovelace");
        plan.AddEdge(dl.Dn, user.Dn);

        var report = RuleEngine.Evaluate(PlanProjection.ToSnapshot(plan), Defaults());

        var finding = Assert.Single(report.Violations, v => v.RuleId == RuleIds.Nesting);
        Assert.Equal(RuleSeverity.Error, finding.Severity);
        Assert.Equal(new[] { dl.Dn, user.Dn }, finding.Dns); // [parent, member]
        Assert.Empty(report.UncheckedDns);
    }

    [Fact(Timeout = 60_000)]
    public async Task Evaluate_AToBToAPlan_YieldsCircularError_AndTerminates()
    {
        // ALWAYS include the circular case for traversal code: the projection must
        // feed a finite, terminating walk. Timeout + Task.Run prove termination,
        // never trust it.
        var plan = new PlanModel(BaseOu);
        var a = plan.AddNode(PlanCreatableKind.GlobalGroup, "GG_Circle_A");
        var b = plan.AddNode(PlanCreatableKind.GlobalGroup, "GG_Circle_B");
        plan.AddEdge(a.Dn, b.Dn);
        plan.AddEdge(b.Dn, a.Dn);

        var report = await Task.Run(() => RuleEngine.Evaluate(PlanProjection.ToSnapshot(plan), Defaults()));

        var finding = Assert.Single(report.Violations, v => v.RuleId == RuleIds.Circular);
        Assert.Equal(RuleSeverity.Error, finding.Severity);
        // Canonical rotation: Dn.Comparer-minimal DN first (GG_Circle_A < GG_Circle_B).
        Assert.Equal(new[] { a.Dn, b.Dn }, finding.Dns);
        Assert.Empty(report.UncheckedDns);
    }

    [Fact(Timeout = 60_000)]
    public async Task Evaluate_SelfMembershipPlan_AToA_YieldsCircularFinding_AndTerminates()
    {
        // Self-membership is authorable (audit) AND must project to a terminating
        // walk that the circular rule reports as the canonical [A].
        var plan = new PlanModel(BaseOu);
        var gg = plan.AddNode(PlanCreatableKind.GlobalGroup, "GG_SelfLoop");
        plan.AddEdge(gg.Dn, gg.Dn);

        var report = await Task.Run(() => RuleEngine.Evaluate(PlanProjection.ToSnapshot(plan), Defaults()));

        var finding = Assert.Single(report.Violations, v => v.RuleId == RuleIds.Circular);
        Assert.Equal(new[] { gg.Dn }, finding.Dns); // self-membership canonical form [A]
    }

    [Fact]
    public void Evaluate_CleanAgdlpPlan_GIntoDl_HasNoNestingOrCircularFindings()
    {
        // A conformant slice: GG into DL (the G->DL lane is allowed), both named
        // to the default conventions, both non-empty. The ONLY finding is the
        // info on whatever is genuinely empty — here nothing is empty.
        var plan = new PlanModel(BaseOu);
        var dl = plan.AddNode(PlanCreatableKind.DomainLocalGroup, "DL_FileShare_RW", sam: "DL_FileShare_RW");
        var gg = plan.AddNode(PlanCreatableKind.GlobalGroup, "GG_Sales_EU", sam: "GG_Sales_EU");
        var user = plan.AddNode(PlanCreatableKind.User, "Ada Lovelace");
        plan.AddEdge(dl.Dn, gg.Dn); // G -> DL: allowed
        plan.AddEdge(gg.Dn, user.Dn); // A -> G: the whole point

        var report = RuleEngine.Evaluate(PlanProjection.ToSnapshot(plan), Defaults());

        Assert.DoesNotContain(report.Violations, v => v.RuleId == RuleIds.Nesting);
        Assert.DoesNotContain(report.Violations, v => v.RuleId == RuleIds.Circular);
        Assert.DoesNotContain(report.Violations, v => v.RuleId == RuleIds.EmptyGroup);
        Assert.Empty(report.UncheckedDns);
    }

    // --- GraphBuilder on the projection: a node for every plan object --------------------

    [Fact]
    public void Build_OnTheProjection_ProducesANodeForEveryPlanObject()
    {
        var plan = new PlanModel(BaseOu);
        var dl = plan.AddNode(PlanCreatableKind.DomainLocalGroup, "DL_FileShare_RW");
        var gg = plan.AddNode(PlanCreatableKind.GlobalGroup, "GG_Sales_EU");
        var user = plan.AddNode(PlanCreatableKind.User, "Ada Lovelace");
        plan.AddEdge(dl.Dn, gg.Dn);
        plan.AddEdge(gg.Dn, user.Dn);

        var graph = GraphBuilder.Build(PlanProjection.ToSnapshot(plan), plan.BaseOuDn);

        // Every authored object is a node (the base OU is synthesized as the root
        // on top of these — so the count is objects + 1).
        var nodeDns = graph.Nodes.Select(n => n.Dn).ToHashSet(Dn.Comparer);
        Assert.Contains(dl.Dn, nodeDns);
        Assert.Contains(gg.Dn, nodeDns);
        Assert.Contains(user.Dn, nodeDns);

        // Each authored object renders one RDN under the base OU (depth-1 ring).
        Assert.Equal(1, DnPath.RelativeDepth(dl.Dn, plan.BaseOuDn));
        Assert.Equal(1, DnPath.RelativeDepth(gg.Dn, plan.BaseOuDn));

        // The synthesized base-OU root is present and is the single root node.
        var root = Assert.Single(graph.Nodes, n => n.IsRoot);
        Assert.Equal(plan.BaseOuDn, root.Dn);
    }

    [Fact]
    public void Build_EmptyPlan_ProducesJustTheSynthesizedRoot()
    {
        var plan = new PlanModel(BaseOu);

        var graph = GraphBuilder.Build(PlanProjection.ToSnapshot(plan), plan.BaseOuDn);

        var root = Assert.Single(graph.Nodes);
        Assert.True(root.IsRoot);
        Assert.Equal(plan.BaseOuDn, root.Dn);
    }

    // --- Helpers --------------------------------------------------------------------------

    /// <summary>The embedded default ruleset with the global ignore list cleared —
    /// the plan DNs live under OU=AGDLP-Lab, outside every default-ignore container,
    /// so clearing it only removes incidental coupling, never a needed exemption.</summary>
    private static Ruleset Defaults() =>
        RulesetLoader.LoadDefault() with { Ignore = Array.Empty<MatchEntry>() };
}
