using GroupWeaver.Core.Model;
using GroupWeaver.Core.Plan;

using Xunit;

namespace GroupWeaver.Tests.Core.Plan;

/// <summary>
/// Pins the AP 4.2.1 authoring surface of <see cref="PlanModel"/> (ADR-014) — the
/// mutable, DN-keyed edit store the read-only <see cref="DirectorySnapshot"/>
/// deliberately is not:
/// <list type="bullet">
/// <item>The DN is the sole identity (data-model.md): every node/edge collection
/// keys via <see cref="Dn.Comparer"/>. A new node's DN is
/// <c>CN=&lt;rfc4514-escaped name&gt;,&lt;BaseOuDn&gt;</c>, stored as-formed and
/// never re-canonicalized.</item>
/// <item><c>AddNode</c> rejects a duplicate DN with
/// <see cref="PlanConflictException"/>; <c>RemoveNode</c> cascades — every incident
/// edge (in either direction) is dropped with the node.</item>
/// <item><c>RenameNode</c> is replace-by-DN: it forms the new DN, rewrites the
/// endpoints of every incident edge, and drops the OLD DN (the DN is identity, so a
/// rename is not an in-place mutation).</item>
/// <item><c>SetKind</c> mutates the typed kind in place.</item>
/// <item><c>AddEdge</c>/<c>RemoveEdge</c> author a direction-sensitive
/// <see cref="MembershipEdge"/> keyed via <see cref="Dn.Comparer"/>; <c>ChildrenOf</c>
/// reads the out-edges of one parent.</item>
/// <item>AUDIT CORRECTION (binding): self-membership A→A and a cycle A→B→A are
/// AUTHORABLE — the model never rejects them (they are findings the engine reports,
/// not structural errors). Only a non-group parent and an unknown endpoint are
/// rejected at the model boundary.</item>
/// </list>
/// Hand-built fixtures only (pure Core, offline). RED until <c>src/Core/Plan</c> exists.
/// </summary>
public class PlanModelTests
{
    private const string BaseOu = "OU=AGDLP-Lab,DC=agdlp,DC=lab";

    // --- AddNode: DN formation, kind/name, dup-DN reject ---------------------------------

    [Fact]
    public void AddNode_FormsDnUnderTheBaseOu_AndStoresTheTypedFields()
    {
        var plan = new PlanModel(BaseOu);

        var group = plan.AddNode(PlanCreatableKind.DomainLocalGroup, "DL_FileShare_RW", sam: "DL_FileShare_RW");

        Assert.Equal("CN=DL_FileShare_RW," + BaseOu, group.Dn);
        Assert.Equal(PlanCreatableKind.DomainLocalGroup, group.Kind);
        Assert.Equal("DL_FileShare_RW", group.Name);
        Assert.Equal("DL_FileShare_RW", group.SamAccountName);

        // The model exposes the node, keyed by its formed DN.
        Assert.True(plan.TryGetNode(group.Dn, out var fetched));
        Assert.Same(group, fetched);
    }

    [Fact]
    public void FormDn_IsTheSingleDnFormationRule_CnEscapedNameCommaBaseOu()
    {
        var plan = new PlanModel(BaseOu);

        Assert.Equal("CN=GG_Sales," + BaseOu, plan.FormDn("GG_Sales"));
    }

    [Fact]
    public void TryGetNode_IsDnComparerKeyed_CaseInsensitive()
    {
        var plan = new PlanModel(BaseOu);
        var node = plan.AddNode(PlanCreatableKind.GlobalGroup, "GG_Sales_EU");

        // A case-variant spelling of the same DN resolves the same node.
        Assert.True(plan.TryGetNode(node.Dn.ToLowerInvariant(), out var fetched));
        Assert.Same(node, fetched);
    }

    [Fact]
    public void AddNode_DuplicateDn_ThrowsPlanConflict_CaseInsensitively()
    {
        var plan = new PlanModel(BaseOu);
        plan.AddNode(PlanCreatableKind.GlobalGroup, "GG_Sales");

        // Same CN under the same base OU => same DN under Dn.Comparer => rejected.
        Assert.Throws<PlanConflictException>(() => plan.AddNode(PlanCreatableKind.GlobalGroup, "gg_sales"));

        // The first node survives the rejected duplicate.
        Assert.Single(plan.Nodes);
    }

    [Fact]
    public void AddNode_UserAndGroupKinds_AllProjectToTheRightTypedKind()
    {
        var plan = new PlanModel(BaseOu);

        Assert.Equal(AdObjectKind.User, PlanKindMap.ToAdObjectKind(plan.AddNode(PlanCreatableKind.User, "Ada Lovelace").Kind));
        Assert.Equal(AdObjectKind.GlobalGroup, PlanKindMap.ToAdObjectKind(plan.AddNode(PlanCreatableKind.GlobalGroup, "GG_X").Kind));
        Assert.Equal(AdObjectKind.DomainLocalGroup, PlanKindMap.ToAdObjectKind(plan.AddNode(PlanCreatableKind.DomainLocalGroup, "DL_X_RW").Kind));
        Assert.Equal(AdObjectKind.UniversalGroup, PlanKindMap.ToAdObjectKind(plan.AddNode(PlanCreatableKind.UniversalGroup, "UG_X").Kind));

        Assert.False(PlanKindMap.IsGroup(PlanCreatableKind.User));
        Assert.True(PlanKindMap.IsGroup(PlanCreatableKind.GlobalGroup));
        Assert.True(PlanKindMap.IsGroup(PlanCreatableKind.DomainLocalGroup));
        Assert.True(PlanKindMap.IsGroup(PlanCreatableKind.UniversalGroup));
    }

    // --- AddEdge / RemoveEdge / ChildrenOf: direction-sensitive, Dn.Comparer ------------

    [Fact]
    public void AddEdge_AuthorsADirectedMembership_ChildrenOfReadsTheParentsOutEdges()
    {
        var plan = new PlanModel(BaseOu);
        var dl = plan.AddNode(PlanCreatableKind.DomainLocalGroup, "DL_FileShare_RW");
        var gg = plan.AddNode(PlanCreatableKind.GlobalGroup, "GG_Sales_EU");

        Assert.True(plan.AddEdge(dl.Dn, gg.Dn)); // G-into-DL
        Assert.False(plan.AddEdge(dl.Dn, gg.Dn)); // idempotent: the edge set de-dups

        Assert.Equal(new[] { gg.Dn }, plan.ChildrenOf(dl.Dn).ToArray());
        Assert.Empty(plan.ChildrenOf(gg.Dn)); // direction matters: GG has no out-edges
        Assert.Contains(new MembershipEdge(dl.Dn, gg.Dn), plan.Edges);
    }

    [Fact]
    public void AddEdge_IsDirectionSensitive_ParentChildAndChildParentAreDistinct()
    {
        var plan = new PlanModel(BaseOu);
        var a = plan.AddNode(PlanCreatableKind.GlobalGroup, "GG_A");
        var b = plan.AddNode(PlanCreatableKind.GlobalGroup, "GG_B");

        Assert.True(plan.AddEdge(a.Dn, b.Dn));
        Assert.True(plan.AddEdge(b.Dn, a.Dn)); // (B,A) != (A,B)

        Assert.Equal(2, plan.Edges.Count);
    }

    [Fact]
    public void AddEdge_UnknownEndpoint_ThrowsPlanConflict()
    {
        var plan = new PlanModel(BaseOu);
        var dl = plan.AddNode(PlanCreatableKind.DomainLocalGroup, "DL_X_RW");
        var ghost = "CN=GG_Ghost," + BaseOu;

        Assert.Throws<PlanConflictException>(() => plan.AddEdge(dl.Dn, ghost)); // unknown child
        Assert.Throws<PlanConflictException>(() => plan.AddEdge(ghost, dl.Dn)); // unknown parent
    }

    [Fact]
    public void AddEdge_NonGroupParent_ThrowsPlanConflict_OnlyAGroupCanHaveMembers()
    {
        var plan = new PlanModel(BaseOu);
        var user = plan.AddNode(PlanCreatableKind.User, "Ada Lovelace");
        var gg = plan.AddNode(PlanCreatableKind.GlobalGroup, "GG_Sales");

        Assert.Throws<PlanConflictException>(() => plan.AddEdge(user.Dn, gg.Dn));
    }

    [Fact]
    public void RemoveEdge_DropsExactlyTheDirectedEdge_DnComparerKeyed()
    {
        var plan = new PlanModel(BaseOu);
        var dl = plan.AddNode(PlanCreatableKind.DomainLocalGroup, "DL_X_RW");
        var gg = plan.AddNode(PlanCreatableKind.GlobalGroup, "GG_X");
        plan.AddEdge(dl.Dn, gg.Dn);

        // Case-variant spellings still identify the edge (Dn.Comparer).
        Assert.True(plan.RemoveEdge(dl.Dn.ToUpperInvariant(), gg.Dn.ToLowerInvariant()));
        Assert.Empty(plan.Edges);
        Assert.False(plan.RemoveEdge(dl.Dn, gg.Dn)); // already gone
    }

    // --- AUDIT: A->A and A->B->A are AUTHORABLE, the model never rejects them -------------

    [Fact]
    public void AddEdge_SelfMembership_AToA_IsAuthorable_NeverRejected()
    {
        // Audit correction: the model must NOT reject A->A. Self-membership is a
        // finding the engine reports (RuleEngine RotateToMinimalDn handles [A]),
        // so the plan must be able to author it.
        var plan = new PlanModel(BaseOu);
        var dl = plan.AddNode(PlanCreatableKind.DomainLocalGroup, "DL_Self_RW");

        Assert.True(plan.AddEdge(dl.Dn, dl.Dn));
        Assert.Contains(new MembershipEdge(dl.Dn, dl.Dn), plan.Edges);
        Assert.Equal(new[] { dl.Dn }, plan.ChildrenOf(dl.Dn).ToArray());
    }

    [Fact]
    public void AddEdge_TwoNodeCycle_AToBToA_IsAuthorable_NeverRejected()
    {
        // Audit correction: A->B->A must be authorable too — the closing edge is
        // not rejected. The circular rule reports it; the model just stores it.
        var plan = new PlanModel(BaseOu);
        var a = plan.AddNode(PlanCreatableKind.GlobalGroup, "GG_Circle_A");
        var b = plan.AddNode(PlanCreatableKind.GlobalGroup, "GG_Circle_B");

        Assert.True(plan.AddEdge(a.Dn, b.Dn));
        Assert.True(plan.AddEdge(b.Dn, a.Dn)); // closes the cycle — must succeed

        Assert.Equal(2, plan.Edges.Count);
    }

    // --- RemoveNode: edge CASCADE in both directions -------------------------------------

    [Fact]
    public void RemoveNode_CascadesEveryIncidentEdge_AsParentAndAsChild()
    {
        var plan = new PlanModel(BaseOu);
        var dl = plan.AddNode(PlanCreatableKind.DomainLocalGroup, "DL_X_RW");
        var gg = plan.AddNode(PlanCreatableKind.GlobalGroup, "GG_X");
        var ug = plan.AddNode(PlanCreatableKind.UniversalGroup, "UG_X");
        plan.AddEdge(dl.Dn, gg.Dn); // gg is a CHILD of dl
        plan.AddEdge(gg.Dn, ug.Dn); // gg is a PARENT of ug

        Assert.True(plan.RemoveNode(gg.Dn));

        // The node is gone and BOTH incident edges (in/out) cascaded with it.
        Assert.False(plan.TryGetNode(gg.Dn, out _));
        Assert.Empty(plan.Edges);
        Assert.Equal(2, plan.Nodes.Count); // dl and ug survive
    }

    [Fact]
    public void RemoveNode_SelfEdge_CascadesIt()
    {
        var plan = new PlanModel(BaseOu);
        var dl = plan.AddNode(PlanCreatableKind.DomainLocalGroup, "DL_Self_RW");
        plan.AddEdge(dl.Dn, dl.Dn);

        Assert.True(plan.RemoveNode(dl.Dn));
        Assert.Empty(plan.Edges);
        Assert.Empty(plan.Nodes);
    }

    [Fact]
    public void RemoveNode_UnknownDn_ReturnsFalse_NoThrow()
    {
        var plan = new PlanModel(BaseOu);

        Assert.False(plan.RemoveNode("CN=GG_Ghost," + BaseOu));
    }

    // --- RenameNode: replace-by-DN, endpoint rewrite, old DN dropped ----------------------

    [Fact]
    public void RenameNode_RewritesIncidentEdgeEndpoints_AndDropsTheOldDn()
    {
        var plan = new PlanModel(BaseOu);
        var dl = plan.AddNode(PlanCreatableKind.DomainLocalGroup, "DL_X_RW");
        var gg = plan.AddNode(PlanCreatableKind.GlobalGroup, "GG_Old");
        var ug = plan.AddNode(PlanCreatableKind.UniversalGroup, "UG_X");
        plan.AddEdge(dl.Dn, gg.Dn); // gg as child
        plan.AddEdge(gg.Dn, ug.Dn); // gg as parent
        var oldDn = gg.Dn;

        plan.RenameNode(oldDn, "GG_New");
        var newDn = plan.FormDn("GG_New");

        // The DN is identity: the old key is gone, the new key resolves, the kind
        // and the other fields carry over.
        Assert.False(plan.TryGetNode(oldDn, out _));
        Assert.True(plan.TryGetNode(newDn, out var renamed));
        Assert.Equal("GG_New", renamed!.Name);
        Assert.Equal(PlanCreatableKind.GlobalGroup, renamed.Kind);

        // BOTH incident edges had their gg endpoint rewritten to the new DN; the
        // old-DN edges are gone, the other endpoints untouched.
        Assert.Contains(new MembershipEdge(dl.Dn, newDn), plan.Edges);
        Assert.Contains(new MembershipEdge(newDn, ug.Dn), plan.Edges);
        Assert.DoesNotContain(new MembershipEdge(dl.Dn, oldDn), plan.Edges);
        Assert.DoesNotContain(new MembershipEdge(oldDn, ug.Dn), plan.Edges);
        Assert.Equal(2, plan.Edges.Count);
    }

    [Fact]
    public void RenameNode_OntoAnExistingDn_ThrowsPlanConflict()
    {
        var plan = new PlanModel(BaseOu);
        plan.AddNode(PlanCreatableKind.GlobalGroup, "GG_A");
        var b = plan.AddNode(PlanCreatableKind.GlobalGroup, "GG_B");

        Assert.Throws<PlanConflictException>(() => plan.RenameNode(b.Dn, "GG_A"));
    }

    [Fact]
    public void RenameNode_UnknownDn_ThrowsPlanConflict()
    {
        var plan = new PlanModel(BaseOu);

        Assert.Throws<PlanConflictException>(() => plan.RenameNode("CN=GG_Ghost," + BaseOu, "GG_New"));
    }

    [Fact]
    public void RenameNode_PreservesSelfEdge_RewritingBothEndpoints()
    {
        var plan = new PlanModel(BaseOu);
        var dl = plan.AddNode(PlanCreatableKind.DomainLocalGroup, "DL_Self_RW");
        plan.AddEdge(dl.Dn, dl.Dn);

        plan.RenameNode(dl.Dn, "DL_Self2_RW");
        var newDn = plan.FormDn("DL_Self2_RW");

        Assert.Equal(new[] { new MembershipEdge(newDn, newDn) }, plan.Edges.ToArray());
    }

    // --- SetKind --------------------------------------------------------------------------

    [Fact]
    public void SetKind_MutatesTheTypedKindInPlace()
    {
        var plan = new PlanModel(BaseOu);
        var node = plan.AddNode(PlanCreatableKind.GlobalGroup, "GG_X");

        plan.SetKind(node.Dn, PlanCreatableKind.UniversalGroup);

        Assert.True(plan.TryGetNode(node.Dn, out var fetched));
        Assert.Equal(PlanCreatableKind.UniversalGroup, fetched!.Kind);
    }
}
