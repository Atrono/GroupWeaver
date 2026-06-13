using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

using Avalonia.Headless.XUnit;

using GroupWeaver.App.Graph;
using GroupWeaver.App.Rules;
using GroupWeaver.App.Tests.Fakes;
using GroupWeaver.App.ViewModels;
using GroupWeaver.Core.Model;
using GroupWeaver.Core.Plan;
using GroupWeaver.Core.Rules;

using Xunit;

namespace GroupWeaver.App.Tests;

/// <summary>
/// Pins AP 4.2.3 (ADR-014): Plan Mode becomes an EDITOR. The side-panel forms mutate the
/// already-frozen <see cref="PlanModel"/> through a new VM command surface
/// (<c>AddObject</c>/<c>AddMember</c>/<c>RemoveMember</c>/<c>RemoveSelected</c>/
/// <c>RenameSelected</c>), each mutation rebuilds the authored-collection row models
/// (<see cref="PlanNodeRowModel"/>/<see cref="PlanEdgeRowModel"/>) and re-runs the EXISTING
/// <see cref="PlanViewModel.RevalidateAsync"/> live-validation loop. Every assertion here is
/// ADDITIVE to <see cref="PlanModeTests"/> (AP 4.2.2) — those stay green; nothing in this
/// file weakens them.
///
/// <para><b>The command surface is the testable contract.</b> ADR-014 makes Plan Mode a
/// panel-based editor over a READ-ONLY graph (there is no canvas edit gesture, and this
/// environment cannot inject canvas mouse gestures — lab-environment.md "Windowed-smoke
/// driving"), so the VM commands and form properties ARE the surface the tests pin. The
/// NodeClicked renderer seam is exercised only where a test needs it (then via
/// <see cref="FakeGraphRenderer"/>); every other test is renderer-less and relies on
/// <see cref="PlanViewModel.RevalidateAsync"/> being null-renderer-safe (the AP 4.2.2
/// contract).</para>
///
/// <para><b>No <c>src/</c> edits and no AD writes</b> are involved: a plan is an in-memory
/// authoring model projected to a snapshot for the unchanged engine. The live-validation
/// end-to-end finding ("a user directly in a domain-local group") is the canonical AGDLP
/// violation; its exact identity (RuleId <see cref="RuleIds.Nesting"/>, severity
/// <see cref="RuleSeverity.Error"/>, <c>Dns = [DL, user]</c>) is VERIFIED here by building
/// the same projection + <see cref="RuleEngine.Evaluate"/> the VM uses — never by hardcoding
/// a message string.</para>
///
/// <para><b>RED until AP 4.2.3</b> lands the <see cref="PlanViewModel"/> editor members and
/// the two row-model types. The App.Tests assembly will not compile until then — that is the
/// intended TDD state for this slice.</para>
/// </summary>
public sealed class PlanModeEditorTests
{
    private const string PlanBaseOuDn = "OU=AGDLP-Lab,DC=agdlp,DC=lab";

    // =====================================================================================
    //  AddObject — form -> model -> collections -> revalidate
    // =====================================================================================

    /// <summary>
    /// Authoring a GROUP through the add-object form: the new node appears in BOTH
    /// <see cref="PlanViewModel.Nodes"/> and <see cref="PlanViewModel.GroupNodes"/> (a group
    /// can be a membership parent), the live-validation loop ran (Snapshot/Graph built and the
    /// node is in the projected snapshot), and the form cleared its EditError. This pins the
    /// AddObject happy path for a group kind end-to-end.
    /// </summary>
    [Fact(Timeout = 30_000)]
    public async Task AddObject_Group_AppearsInNodesAndGroupNodes_AndRevalidates()
    {
        var plan = HeadlessPlan();
        plan.NewObjectKind = PlanCreatableKind.GlobalGroup;
        plan.NewObjectName = "GG_Sales_Team";

        await plan.AddObjectCommand.ExecuteAsync(null);

        var dn = plan.Plan.FormDn("GG_Sales_Team");
        Assert.Contains(plan.Nodes, r => Dn.Comparer.Equals(r.Dn, dn));
        Assert.Contains(plan.GroupNodes, r => Dn.Comparer.Equals(r.Dn, dn)); // a group can have members

        // RevalidateAsync ran: the pipeline produced a snapshot + graph and the node is in it.
        Assert.NotNull(plan.Snapshot);
        Assert.NotNull(plan.Graph);
        Assert.True(plan.Snapshot!.TryGetObject(dn, out var obj));
        Assert.Equal(AdObjectKind.GlobalGroup, obj!.Kind);
        Assert.Null(plan.EditError);

        plan.Dispose();
    }

    /// <summary>
    /// Authoring a USER: the node is in <see cref="PlanViewModel.Nodes"/> but NOT in
    /// <see cref="PlanViewModel.GroupNodes"/> (a user can never be a membership parent — only
    /// a group can have members), and the user-only SAM is applied. This pins the
    /// group-vs-account split that drives which combo each node row populates.
    /// </summary>
    [Fact(Timeout = 30_000)]
    public async Task AddObject_User_InNodesButNotGroupNodes_AndSamApplied()
    {
        var plan = HeadlessPlan();
        plan.NewObjectKind = PlanCreatableKind.User;
        plan.NewObjectName = "Alice Acker";
        plan.NewObjectSam = "aacker";

        await plan.AddObjectCommand.ExecuteAsync(null);

        var dn = plan.Plan.FormDn("Alice Acker");
        Assert.Contains(plan.Nodes, r => Dn.Comparer.Equals(r.Dn, dn));
        Assert.DoesNotContain(plan.GroupNodes, r => Dn.Comparer.Equals(r.Dn, dn));

        // SAM only meaningful for a User — it is applied on the model node.
        Assert.True(plan.Plan.TryGetNode(dn, out var node));
        Assert.Equal("aacker", node!.SamAccountName);

        plan.Dispose();
    }

    /// <summary>
    /// The SAM box is User-only: authoring a GROUP must NOT carry a SAM onto the model node,
    /// even if the SAM box still holds a value (the form derives <c>NewObjectIsUser</c> from
    /// the kind, so the add command passes <c>null</c> for a group). This pins the
    /// "SAM only for User" half of the AddObject contract.
    /// </summary>
    [Fact(Timeout = 30_000)]
    public async Task AddObject_Group_DoesNotApplySam_EvenIfSamBoxHasAValue()
    {
        var plan = HeadlessPlan();
        plan.NewObjectKind = PlanCreatableKind.DomainLocalGroup;
        plan.NewObjectName = "DL_FS_RW";
        plan.NewObjectSam = "leftover"; // a stale value the user typed before switching kind

        await plan.AddObjectCommand.ExecuteAsync(null);

        var dn = plan.Plan.FormDn("DL_FS_RW");
        Assert.True(plan.Plan.TryGetNode(dn, out var node));
        Assert.Null(node!.SamAccountName); // a group never receives the SAM

        plan.Dispose();
    }

    /// <summary>
    /// A duplicate name is rejected by the model (<see cref="PlanConflictException"/>): the
    /// command must surface the conflict message into <see cref="PlanViewModel.EditError"/>,
    /// leave <see cref="PlanViewModel.Nodes"/> unchanged (no second row), and NOT clear the
    /// form (<see cref="PlanViewModel.NewObjectName"/> is retained so the user can fix it).
    /// </summary>
    [Fact(Timeout = 30_000)]
    public async Task AddObject_DuplicateName_SetsEditError_NodesUnchanged_FormRetained()
    {
        var plan = HeadlessPlan();
        plan.NewObjectKind = PlanCreatableKind.GlobalGroup;
        plan.NewObjectName = "GG_Dup";
        await plan.AddObjectCommand.ExecuteAsync(null);
        var countAfterFirst = plan.Nodes.Count;

        // Same name again -> the model throws PlanConflictException -> the command catches it.
        plan.NewObjectName = "GG_Dup";
        await plan.AddObjectCommand.ExecuteAsync(null);

        Assert.NotNull(plan.EditError); // the conflict message surfaced
        Assert.Equal(countAfterFirst, plan.Nodes.Count); // no second row added
        Assert.Equal("GG_Dup", plan.NewObjectName); // the form is retained for the fix

        plan.Dispose();
    }

    /// <summary>
    /// Defense-in-depth: a name containing a control character is rejected by the model
    /// (<see cref="PlanConflictException"/> — the exporter must stay clean). The command must
    /// set <see cref="PlanViewModel.EditError"/> and add NO node.
    /// </summary>
    [Fact(Timeout = 30_000)]
    public async Task AddObject_ControlCharName_SetsEditError_NoNodeAdded()
    {
        var plan = HeadlessPlan();
        plan.NewObjectKind = PlanCreatableKind.GlobalGroup;
        plan.NewObjectName = "GG_\u0007BadName"; // an embedded BEL (U+0007) control char

        await plan.AddObjectCommand.ExecuteAsync(null);

        Assert.NotNull(plan.EditError);
        Assert.Empty(plan.Nodes); // nothing was authored

        plan.Dispose();
    }

    /// <summary>
    /// A SUCCESSFUL add clears the inline error and the name/SAM boxes (so the next add starts
    /// fresh) while keeping the kind selection (the user may add several of the same kind). This
    /// pins the "success clears EditError + NewObjectName + NewObjectSam" half of the contract,
    /// including that a prior error is cleared on the subsequent success.
    /// </summary>
    [Fact(Timeout = 30_000)]
    public async Task AddObject_Success_ClearsEditError_AndNameAndSamBoxes()
    {
        var plan = HeadlessPlan();

        // First a rejected add to set EditError, then a good one to prove it clears.
        plan.NewObjectKind = PlanCreatableKind.User;
        plan.NewObjectName = "BadName";
        await plan.AddObjectCommand.ExecuteAsync(null);
        Assert.NotNull(plan.EditError);

        plan.NewObjectName = "Carol Carter";
        plan.NewObjectSam = "ccarter";
        await plan.AddObjectCommand.ExecuteAsync(null);

        Assert.Null(plan.EditError);
        Assert.Equal(string.Empty, plan.NewObjectName);
        Assert.Equal(string.Empty, plan.NewObjectSam);
        Assert.Equal(PlanCreatableKind.User, plan.NewObjectKind); // kind kept for repeat adds

        plan.Dispose();
    }

    /// <summary>
    /// <see cref="PlanViewModel.AddObjectCommand"/> CanExecute is gated on a non-blank name: a
    /// blank/whitespace name disarms the command, a real name arms it. This pins the form guard
    /// that the "Add" button binds.
    /// </summary>
    [Fact]
    public void AddObjectCommand_CanExecute_RequiresANonBlankName()
    {
        var plan = HeadlessPlan();

        plan.NewObjectName = "";
        Assert.False(plan.AddObjectCommand.CanExecute(null));

        plan.NewObjectName = "   ";
        Assert.False(plan.AddObjectCommand.CanExecute(null));

        plan.NewObjectName = "GG_Real";
        Assert.True(plan.AddObjectCommand.CanExecute(null));

        plan.Dispose();
    }

    /// <summary>
    /// <see cref="PlanViewModel.NewObjectIsUser"/> is DERIVED from
    /// <see cref="PlanViewModel.NewObjectKind"/>: true only for <see cref="PlanCreatableKind.User"/>
    /// (it drives the SAM box visibility), false for every group kind. Setting the kind must
    /// notify the derived flag (the view binds the SAM box's <c>IsVisible</c> to it).
    /// </summary>
    [Fact]
    public void NewObjectIsUser_IsTrueOnlyForUserKind()
    {
        var plan = HeadlessPlan();

        plan.NewObjectKind = PlanCreatableKind.User;
        Assert.True(plan.NewObjectIsUser);

        plan.NewObjectKind = PlanCreatableKind.GlobalGroup;
        Assert.False(plan.NewObjectIsUser);

        plan.NewObjectKind = PlanCreatableKind.DomainLocalGroup;
        Assert.False(plan.NewObjectIsUser);

        plan.NewObjectKind = PlanCreatableKind.UniversalGroup;
        Assert.False(plan.NewObjectIsUser);

        plan.Dispose();
    }

    /// <summary>
    /// <see cref="PlanViewModel.CreatableKinds"/> exposes exactly the four
    /// <see cref="PlanCreatableKind"/> values for the add-object kind combo (the spec's
    /// VM-exposed static items source). This pins the combo's data source.
    /// </summary>
    [Fact]
    public void CreatableKinds_AreTheFourPlanCreatableKinds()
    {
        var plan = HeadlessPlan();

        Assert.Equal(
            new[]
            {
                PlanCreatableKind.User,
                PlanCreatableKind.GlobalGroup,
                PlanCreatableKind.DomainLocalGroup,
                PlanCreatableKind.UniversalGroup,
            },
            plan.CreatableKinds.ToArray());

        plan.Dispose();
    }

    // =====================================================================================
    //  AddMember — form -> edge -> projected snapshot -> revalidate
    // =====================================================================================

    /// <summary>
    /// Authoring a membership through the form: the edge appears in
    /// <see cref="PlanViewModel.Memberships"/> AND in the projected snapshot's edges, and the
    /// live-validation loop ran. The parent/child combos hold <see cref="PlanNodeRowModel"/>
    /// rows; the command reads their DNs. This pins the AddMember happy path end-to-end.
    /// </summary>
    [Fact(Timeout = 30_000)]
    public async Task AddMember_TwoGroups_EdgeInMembershipsAndProjectedSnapshot_AndRevalidates()
    {
        var plan = HeadlessPlan();
        var parentDn = await AddNodeAsync(plan, PlanCreatableKind.DomainLocalGroup, "DL_FS_RW");
        var childDn = await AddNodeAsync(plan, PlanCreatableKind.GlobalGroup, "GG_FS_Users");

        plan.MemberParentRow = Row(plan.GroupNodes, parentDn);
        plan.MemberChildRow = Row(plan.Nodes, childDn);
        Assert.True(plan.AddMemberCommand.CanExecute(null));

        await plan.AddMemberCommand.ExecuteAsync(null);

        // The membership row is present (parent <- child reading).
        Assert.Contains(
            plan.Memberships,
            e => Dn.Comparer.Equals(e.ParentDn, parentDn) && Dn.Comparer.Equals(e.ChildDn, childDn));

        // The edge is in the projected snapshot the engine evaluated (DL lists GG as member).
        Assert.NotNull(plan.Snapshot);
        var members = plan.Snapshot!.GetMembers(parentDn);
        Assert.NotNull(members);
        Assert.Contains(members!, m => Dn.Comparer.Equals(m, childDn));

        plan.Dispose();
    }

    /// <summary>
    /// <see cref="PlanViewModel.AddMemberCommand"/> CanExecute is false unless BOTH a parent and
    /// a child row are set AND the parent row's kind is a group: a null child, a null parent, or
    /// a USER parent row each disarm the command (a user can never have members — the model would
    /// throw, so the command must guard via CanExecute, never reach the throwing AddEdge).
    /// </summary>
    [Fact(Timeout = 30_000)]
    public async Task AddMemberCommand_CanExecute_RequiresBothRows_AndAGroupParent()
    {
        var plan = HeadlessPlan();
        var groupDn = await AddNodeAsync(plan, PlanCreatableKind.GlobalGroup, "GG_Team");
        var userDn = await AddNodeAsync(plan, PlanCreatableKind.User, "Dave Davis");

        // Nothing selected -> disarmed.
        Assert.False(plan.AddMemberCommand.CanExecute(null));

        // Only the parent set -> disarmed.
        plan.MemberParentRow = Row(plan.GroupNodes, groupDn);
        plan.MemberChildRow = null;
        Assert.False(plan.AddMemberCommand.CanExecute(null));

        // Group parent + a child -> armed (a group parent with a user child is allowed).
        plan.MemberChildRow = Row(plan.Nodes, userDn);
        Assert.True(plan.AddMemberCommand.CanExecute(null));

        // A USER parent row -> disarmed: a user can never have members.
        plan.MemberParentRow = Row(plan.Nodes, userDn);
        Assert.False(plan.AddMemberCommand.CanExecute(null));

        plan.Dispose();
    }

    /// <summary>
    /// A self-membership A→A is AUTHORABLE (ADR-014: the model does not reject it; it is a
    /// finding the engine reports). After AddMember the edge is present and the LIVE validation
    /// surfaces the circular finding into <see cref="PlanViewModel.Violations"/>. The default
    /// ruleset enables the circular rule (error). This is the "must terminate on a cycle"
    /// traversal pin in the plan-editor flavor.
    /// </summary>
    [Fact(Timeout = 30_000)]
    public async Task AddMember_SelfMembership_IsAuthorable_AndSurfacesACircularFinding()
    {
        var plan = HeadlessPlan();
        var dn = await AddNodeAsync(plan, PlanCreatableKind.GlobalGroup, "GG_Self");

        plan.MemberParentRow = Row(plan.GroupNodes, dn);
        plan.MemberChildRow = Row(plan.Nodes, dn); // A -> A
        await plan.AddMemberCommand.ExecuteAsync(null);

        // Authorable: the edge exists in the model and the projected snapshot.
        Assert.Contains(plan.Memberships, e => Dn.Comparer.Equals(e.ParentDn, dn) && Dn.Comparer.Equals(e.ChildDn, dn));

        // The engine reports the cycle (it must terminate, not loop): a circular finding appears.
        Assert.Contains(plan.Report.Violations, v => v.RuleId == RuleIds.Circular);
        Assert.Contains(plan.Violations, r => Dn.Comparer.Equals(r.PrimaryDn, dn));
        Assert.True(plan.HasViolations);

        plan.Dispose();
    }

    /// <summary>
    /// A two-node cycle A→B, B→A is AUTHORABLE and the live validation surfaces a circular
    /// finding (the canonical cycle is reported once, deduped — ADR-006/rule-engine.md). This
    /// is the mandatory circular-traversal case in the editor flow: authoring the closing edge
    /// must terminate and produce exactly the cycle finding, not hang.
    /// </summary>
    [Fact(Timeout = 30_000)]
    public async Task AddMember_TwoNodeCycle_IsAuthorable_AndSurfacesACircularFinding()
    {
        var plan = HeadlessPlan();
        var aDn = await AddNodeAsync(plan, PlanCreatableKind.GlobalGroup, "GG_Circle_A");
        var bDn = await AddNodeAsync(plan, PlanCreatableKind.GlobalGroup, "GG_Circle_B");

        // A -> B
        plan.MemberParentRow = Row(plan.GroupNodes, aDn);
        plan.MemberChildRow = Row(plan.Nodes, bDn);
        await plan.AddMemberCommand.ExecuteAsync(null);

        // B -> A (closes the cycle)
        plan.MemberParentRow = Row(plan.GroupNodes, bDn);
        plan.MemberChildRow = Row(plan.Nodes, aDn);
        await plan.AddMemberCommand.ExecuteAsync(null);

        Assert.Contains(plan.Memberships, e => Dn.Comparer.Equals(e.ParentDn, aDn) && Dn.Comparer.Equals(e.ChildDn, bDn));
        Assert.Contains(plan.Memberships, e => Dn.Comparer.Equals(e.ParentDn, bDn) && Dn.Comparer.Equals(e.ChildDn, aDn));

        var circular = Assert.Single(plan.Report.Violations, v => v.RuleId == RuleIds.Circular);
        // The canonical cycle rotation includes both cycle DNs.
        Assert.Contains(circular.Dns, d => Dn.Comparer.Equals(d, aDn));
        Assert.Contains(circular.Dns, d => Dn.Comparer.Equals(d, bDn));
        Assert.True(plan.HasViolations);

        plan.Dispose();
    }

    // =====================================================================================
    //  RemoveMember
    // =====================================================================================

    /// <summary>
    /// <see cref="PlanViewModel.RemoveMemberCommand"/> over a membership row removes the edge
    /// from the model + <see cref="PlanViewModel.Memberships"/> and re-runs validation (the
    /// projected snapshot no longer carries the edge). This pins the per-row Remove the
    /// memberships list binds.
    /// </summary>
    [Fact(Timeout = 30_000)]
    public async Task RemoveMember_RemovesTheEdge_AndRevalidates()
    {
        var plan = HeadlessPlan();
        var parentDn = await AddNodeAsync(plan, PlanCreatableKind.GlobalGroup, "GG_Parent");
        var childDn = await AddNodeAsync(plan, PlanCreatableKind.GlobalGroup, "GG_Child");
        plan.MemberParentRow = Row(plan.GroupNodes, parentDn);
        plan.MemberChildRow = Row(plan.Nodes, childDn);
        await plan.AddMemberCommand.ExecuteAsync(null);

        var row = Assert.Single(plan.Memberships);
        await plan.RemoveMemberCommand.ExecuteAsync(row);

        Assert.Empty(plan.Memberships);
        Assert.NotNull(plan.Snapshot);
        var members = plan.Snapshot!.GetMembers(parentDn);
        Assert.NotNull(members);
        Assert.DoesNotContain(members!, m => Dn.Comparer.Equals(m, childDn));

        plan.Dispose();
    }

    // =====================================================================================
    //  RemoveSelected — cascade + clear selection + revalidate
    // =====================================================================================

    /// <summary>
    /// <see cref="PlanViewModel.RemoveSelectedCommand"/> removes the selected node AND cascades
    /// its incident edges (the model's RemoveNode cascade): a membership referencing the removed
    /// node is gone from <see cref="PlanViewModel.Memberships"/>, the selection is cleared
    /// (SelectedNodeRow + SelectedDn null), and validation re-ran. This pins the destructive
    /// node action and its selection/cascade aftermath.
    /// </summary>
    [Fact(Timeout = 30_000)]
    public async Task RemoveSelected_CascadesIncidentEdges_ClearsSelection_AndRevalidates()
    {
        var plan = HeadlessPlan();
        var parentDn = await AddNodeAsync(plan, PlanCreatableKind.GlobalGroup, "GG_Owner");
        var childDn = await AddNodeAsync(plan, PlanCreatableKind.User, "Erin Else");
        plan.MemberParentRow = Row(plan.GroupNodes, parentDn);
        plan.MemberChildRow = Row(plan.Nodes, childDn);
        await plan.AddMemberCommand.ExecuteAsync(null);
        Assert.Single(plan.Memberships);

        // Select the parent and remove it: the membership it owns must cascade away.
        plan.SelectedNodeRow = Row(plan.Nodes, parentDn);
        Assert.True(plan.RemoveSelectedCommand.CanExecute(null));
        await plan.RemoveSelectedCommand.ExecuteAsync(null);

        Assert.DoesNotContain(plan.Nodes, r => Dn.Comparer.Equals(r.Dn, parentDn));
        Assert.Empty(plan.Memberships); // the incident edge cascaded
        Assert.Null(plan.SelectedNodeRow);
        Assert.Null(plan.SelectedDn);

        // The child still exists (only the parent was removed); validation re-ran.
        Assert.Contains(plan.Nodes, r => Dn.Comparer.Equals(r.Dn, childDn));
        Assert.NotNull(plan.Snapshot);
        Assert.False(plan.Snapshot!.TryGetObject(parentDn, out _));

        plan.Dispose();
    }

    /// <summary>
    /// <see cref="PlanViewModel.RemoveSelectedCommand"/> CanExecute is gated on a selected node:
    /// disarmed with no selection, armed once a node row is selected. Pins the guard the
    /// selected-node "Remove" button binds.
    /// </summary>
    [Fact(Timeout = 30_000)]
    public async Task RemoveSelectedCommand_CanExecute_RequiresASelectedNode()
    {
        var plan = HeadlessPlan();
        var dn = await AddNodeAsync(plan, PlanCreatableKind.GlobalGroup, "GG_Sel");

        Assert.False(plan.RemoveSelectedCommand.CanExecute(null)); // nothing selected
        plan.SelectedNodeRow = Row(plan.Nodes, dn);
        Assert.True(plan.RemoveSelectedCommand.CanExecute(null));

        plan.Dispose();
    }

    // =====================================================================================
    //  RenameSelected — rename + selection follows + edges keep endpoints + revalidate
    // =====================================================================================

    /// <summary>
    /// <see cref="PlanViewModel.RenameSelectedCommand"/> renames the selected node: its Dn/Name
    /// change (DN is identity, so the model replaces-by-DN), the selection FOLLOWS the rename
    /// (<see cref="PlanViewModel.SelectedNodeRow"/> is the renamed row, SelectedDn = the new DN,
    /// <see cref="PlanViewModel.RenameText"/> tracks the new name), incident memberships keep
    /// their endpoints (rewritten to the new DN), and validation re-ran. This pins the most
    /// intricate editor command end-to-end.
    /// </summary>
    [Fact(Timeout = 30_000)]
    public async Task RenameSelected_RenamesNode_SelectionFollows_EdgesRewritten_AndRevalidates()
    {
        var plan = HeadlessPlan();
        var groupDn = await AddNodeAsync(plan, PlanCreatableKind.GlobalGroup, "GG_OldName");
        var childDn = await AddNodeAsync(plan, PlanCreatableKind.User, "Frank Fox");
        plan.MemberParentRow = Row(plan.GroupNodes, groupDn);
        plan.MemberChildRow = Row(plan.Nodes, childDn);
        await plan.AddMemberCommand.ExecuteAsync(null);

        // Select the group, set a new name, rename.
        plan.SelectedNodeRow = Row(plan.Nodes, groupDn);
        plan.RenameText = "GG_NewName";
        Assert.True(plan.RenameSelectedCommand.CanExecute(null));
        await plan.RenameSelectedCommand.ExecuteAsync(null);

        var newDn = plan.Plan.FormDn("GG_NewName");

        // Old DN is gone; the new node exists with the new name.
        Assert.DoesNotContain(plan.Nodes, r => Dn.Comparer.Equals(r.Dn, groupDn));
        Assert.Contains(plan.Nodes, r => Dn.Comparer.Equals(r.Dn, newDn) && r.Name == "GG_NewName");

        // Selection follows the rename: the renamed row stays selected, SelectedDn + RenameText track it.
        Assert.NotNull(plan.SelectedNodeRow);
        Assert.Equal(newDn, plan.SelectedNodeRow!.Dn, Dn.Comparer);
        Assert.Equal(newDn, plan.SelectedDn, Dn.Comparer);
        Assert.Equal("GG_NewName", plan.RenameText);

        // The incident membership kept its endpoints, the parent endpoint rewritten to the new DN.
        Assert.Contains(
            plan.Memberships,
            e => Dn.Comparer.Equals(e.ParentDn, newDn) && Dn.Comparer.Equals(e.ChildDn, childDn));
        Assert.DoesNotContain(plan.Memberships, e => Dn.Comparer.Equals(e.ParentDn, groupDn));

        // Validation re-ran over the rewritten topology.
        Assert.NotNull(plan.Snapshot);
        Assert.True(plan.Snapshot!.TryGetObject(newDn, out _));
        Assert.False(plan.Snapshot.TryGetObject(groupDn, out _));

        plan.Dispose();
    }

    /// <summary>
    /// Renaming onto an EXISTING different name is rejected by the model
    /// (<see cref="PlanConflictException"/> — the new DN already exists). The command must set
    /// <see cref="PlanViewModel.EditError"/> and make NO change (the selected node keeps its DN,
    /// the other node is untouched).
    /// </summary>
    [Fact(Timeout = 30_000)]
    public async Task RenameSelected_OntoAnExistingName_SetsEditError_NoChange()
    {
        var plan = HeadlessPlan();
        var aDn = await AddNodeAsync(plan, PlanCreatableKind.GlobalGroup, "GG_Alpha");
        var bDn = await AddNodeAsync(plan, PlanCreatableKind.GlobalGroup, "GG_Beta");

        // Try to rename Alpha onto Beta's name.
        plan.SelectedNodeRow = Row(plan.Nodes, aDn);
        plan.RenameText = "GG_Beta";
        await plan.RenameSelectedCommand.ExecuteAsync(null);

        Assert.NotNull(plan.EditError);
        // No change: both DNs still exist, Alpha keeps its DN.
        Assert.Contains(plan.Nodes, r => Dn.Comparer.Equals(r.Dn, aDn));
        Assert.Contains(plan.Nodes, r => Dn.Comparer.Equals(r.Dn, bDn));

        plan.Dispose();
    }

    /// <summary>
    /// <see cref="PlanViewModel.RenameSelectedCommand"/> CanExecute requires BOTH a selected node
    /// AND a non-blank rename text. Pins the guard the "Rename" button binds.
    /// </summary>
    [Fact(Timeout = 30_000)]
    public async Task RenameSelectedCommand_CanExecute_RequiresSelectionAndNonBlankText()
    {
        var plan = HeadlessPlan();
        var dn = await AddNodeAsync(plan, PlanCreatableKind.GlobalGroup, "GG_Ren");

        // No selection -> disarmed even with text.
        plan.RenameText = "GG_Whatever";
        Assert.False(plan.RenameSelectedCommand.CanExecute(null));

        // Selected but blank text -> disarmed.
        plan.SelectedNodeRow = Row(plan.Nodes, dn);
        plan.RenameText = "   ";
        Assert.False(plan.RenameSelectedCommand.CanExecute(null));

        // Selected + real text -> armed.
        plan.RenameText = "GG_Renamed";
        Assert.True(plan.RenameSelectedCommand.CanExecute(null));

        plan.Dispose();
    }

    // =====================================================================================
    //  Selection sync — SelectedNodeRow <-> SelectedDn <-> RenameText, NodeClicked, IsActive
    // =====================================================================================

    /// <summary>
    /// Setting <see cref="PlanViewModel.SelectedNodeRow"/> (the Objects-list selection) sets
    /// <see cref="PlanViewModel.SelectedDn"/> to that row's Dn and seeds
    /// <see cref="PlanViewModel.RenameText"/> with that row's Name (so the rename box pre-fills),
    /// and <see cref="PlanViewModel.HasSelectedNode"/> flips true. Clearing the selection clears
    /// SelectedDn and HasSelectedNode.
    /// </summary>
    [Fact(Timeout = 30_000)]
    public async Task SelectedNodeRow_SetsSelectedDn_AndRenameText_AndHasSelectedNode()
    {
        var plan = HeadlessPlan();
        var dn = await AddNodeAsync(plan, PlanCreatableKind.GlobalGroup, "GG_Pick");

        Assert.False(plan.HasSelectedNode);

        plan.SelectedNodeRow = Row(plan.Nodes, dn);
        Assert.Equal(dn, plan.SelectedDn, Dn.Comparer);
        Assert.Equal("GG_Pick", plan.RenameText);
        Assert.True(plan.HasSelectedNode);

        plan.SelectedNodeRow = null;
        Assert.Null(plan.SelectedDn);
        Assert.False(plan.HasSelectedNode);

        plan.Dispose();
    }

    /// <summary>
    /// A node tap arriving over the plan's OWN renderer seam sets
    /// <see cref="PlanViewModel.SelectedDn"/> verbatim (the AP 4.2.2 NodeClicked wiring) AND the
    /// new selection-sync re-resolves <see cref="PlanViewModel.SelectedNodeRow"/> to the matching
    /// <see cref="PlanViewModel.Nodes"/> row (under <see cref="Dn.Comparer"/>). This pins the
    /// graph-to-list selection round-trip the editor adds on top of the AP 4.2.2 seam.
    /// </summary>
    [AvaloniaFact(Timeout = 30_000)]
    public async Task NodeClicked_OverTheRenderer_SetsSelectedDn_AndReResolvesSelectedNodeRow()
    {
        var fake = new FakeGraphRenderer();
        var plan = new PlanViewModel(
            PlanBaseOuDn,
            DefaultEffectiveRuleset(),
            graphRendererFactory: () => fake);

        var dn = await AddNodeAsync(plan, PlanCreatableKind.GlobalGroup, "GG_Clicked");

        fake.RaiseNodeClicked(dn, "GlobalGroup");

        Assert.Equal(dn, plan.SelectedDn, Dn.Comparer); // verbatim, uncanonicalized
        Assert.NotNull(plan.SelectedNodeRow);
        Assert.Equal(dn, plan.SelectedNodeRow!.Dn, Dn.Comparer); // re-resolved to the list row

        plan.Dispose();
    }

    /// <summary>
    /// Selection-sync highlight (mirrors <c>WorkspaceViewModel.OnSelectedDnChanged</c>): a
    /// <see cref="PlanViewModel.Violations"/> row whose <c>PrimaryDn</c> matches the current
    /// selection gets <see cref="ViolationRowModel.IsActive"/> true; clearing/changing the
    /// selection clears it. Here a user-in-DL nesting finding's anchor (the DL DN) is selected;
    /// the DL row lights, then dims when the selection moves to the unrelated user DN (the user
    /// is the member endpoint, not the finding's anchor).
    /// </summary>
    [Fact(Timeout = 30_000)]
    public async Task Violations_IsActive_HighlightsTheRowWhosePrimaryDnMatchesTheSelection()
    {
        var plan = HeadlessPlan();
        var dlDn = await AddNodeAsync(plan, PlanCreatableKind.DomainLocalGroup, "DL_FS_RW");
        var userDn = await AddNodeAsync(plan, PlanCreatableKind.User, "Gwen Gray");
        plan.MemberParentRow = Row(plan.GroupNodes, dlDn);
        plan.MemberChildRow = Row(plan.Nodes, userDn);
        await plan.AddMemberCommand.ExecuteAsync(null);

        // The nesting finding's anchor is the DL (Dns[0]); selecting it lights the row.
        var row = Assert.Single(plan.Violations, r => Dn.Comparer.Equals(r.PrimaryDn, dlDn));
        plan.SelectedDn = dlDn;
        Assert.True(row.IsActive);

        // Selecting the user DN (the member endpoint, never an anchor) dims it.
        plan.SelectedDn = userDn;
        Assert.False(row.IsActive);

        plan.Dispose();
    }

    // =====================================================================================
    //  Live validation end-to-end — a user DIRECTLY in a DL is the AGDLP nesting violation
    // =====================================================================================

    /// <summary>
    /// THE live-validation end-to-end pin: authoring a USER as a DIRECT member of a
    /// DomainLocalGroup is the canonical AGDLP violation (an account belongs in a global group
    /// routed G→DL, never directly in a DL). After the <see cref="PlanViewModel.AddMemberCommand"/>
    /// the LIVE validation surfaces the finding into <see cref="PlanViewModel.Violations"/> and
    /// <see cref="PlanViewModel.HasViolations"/> is true.
    ///
    /// <para>The expected finding is VERIFIED, not assumed: the same projection +
    /// <see cref="RuleEngine.Evaluate"/> the VM uses is run here against the default ruleset, and
    /// the assertion is by RuleId (<see cref="RuleIds.Nesting"/>) + severity
    /// (<see cref="RuleSeverity.Error"/>) + the anchor referencing the DL and the user DNs —
    /// NEVER by a hardcoded message string (messages are presentation, identity is the structured
    /// fields — rule-engine.md).</para>
    /// </summary>
    [Fact(Timeout = 30_000)]
    public async Task LiveValidation_UserDirectlyInADL_SurfacesTheNestingFinding()
    {
        var plan = HeadlessPlan();
        var dlDn = await AddNodeAsync(plan, PlanCreatableKind.DomainLocalGroup, "DL_FS_RW");
        var userDn = await AddNodeAsync(plan, PlanCreatableKind.User, "Hank Hill");

        // INDEPENDENT ground truth: build the same projection + Evaluate the VM uses and read
        // back the exact finding the default ruleset reports for "a user directly in a DL".
        var expected = ExpectedUserInDlFinding(dlDn, userDn);
        Assert.Equal(RuleIds.Nesting, expected.RuleId);
        Assert.Equal(RuleSeverity.Error, expected.Severity);
        Assert.Equal(dlDn, expected.Dns[0], Dn.Comparer); // anchor is the DL parent
        Assert.Equal(userDn, expected.Dns[1], Dn.Comparer); // member is the user

        // Now drive the EDITOR: select the DL as parent, the user as child, add the member.
        plan.MemberParentRow = Row(plan.GroupNodes, dlDn);
        plan.MemberChildRow = Row(plan.Nodes, userDn);
        await plan.AddMemberCommand.ExecuteAsync(null);

        // The live validation surfaced exactly the verified finding (by RuleId/severity/anchor).
        var finding = Assert.Single(plan.Report.Violations, v => v.RuleId == RuleIds.Nesting);
        Assert.Equal(RuleSeverity.Error, finding.Severity);
        Assert.Equal(dlDn, finding.Dns[0], Dn.Comparer);
        Assert.Equal(userDn, finding.Dns[1], Dn.Comparer);

        Assert.True(plan.HasViolations);
        // The sidebar projection carries the finding anchored on the DL.
        Assert.Contains(
            plan.Violations,
            r => r.Severity == RuleSeverity.Error && Dn.Comparer.Equals(r.PrimaryDn, dlDn));

        plan.Dispose();
    }

    // =====================================================================================
    //  Row-model surface (PlanNodeRowModel / PlanEdgeRowModel)
    // =====================================================================================

    /// <summary>
    /// <see cref="PlanNodeRowModel"/> exposes the immutable fields the combos/list bind and the
    /// derived <c>EngineKind</c> (= <see cref="PlanKindMap.ToAdObjectKind"/>) the kind badge
    /// renders over via <c>AdObjectKindConverters</c> (the ONE kind palette — "same kind = same
    /// color everywhere"). This pins the badge-source so the view cannot invent a second palette.
    /// </summary>
    [Fact(Timeout = 30_000)]
    public async Task PlanNodeRowModel_EngineKind_MapsFromPlanCreatableKind()
    {
        var plan = HeadlessPlan();
        var dlDn = await AddNodeAsync(plan, PlanCreatableKind.DomainLocalGroup, "DL_FS_RW");
        var userDn = await AddNodeAsync(plan, PlanCreatableKind.User, "Ivy Iron");

        var dlRow = Row(plan.Nodes, dlDn);
        Assert.Equal(PlanCreatableKind.DomainLocalGroup, dlRow.Kind);
        Assert.Equal(AdObjectKind.DomainLocalGroup, dlRow.EngineKind);

        var userRow = Row(plan.Nodes, userDn);
        Assert.Equal(AdObjectKind.User, userRow.EngineKind);

        plan.Dispose();
    }

    /// <summary>
    /// <see cref="PlanEdgeRowModel.Display"/> reads "parent ← child" (the parent lists the child,
    /// matching the graph legend "is member of"). The membership list binds this; pinning it keeps
    /// the reading direction stable.
    /// </summary>
    [Fact(Timeout = 30_000)]
    public async Task PlanEdgeRowModel_Display_ReadsParentArrowChild()
    {
        var plan = HeadlessPlan();
        var parentDn = await AddNodeAsync(plan, PlanCreatableKind.DomainLocalGroup, "DL_FS_RW");
        var childDn = await AddNodeAsync(plan, PlanCreatableKind.GlobalGroup, "GG_FS_Users");
        plan.MemberParentRow = Row(plan.GroupNodes, parentDn);
        plan.MemberChildRow = Row(plan.Nodes, childDn);
        await plan.AddMemberCommand.ExecuteAsync(null);

        var edge = Assert.Single(plan.Memberships);
        Assert.Equal("DL_FS_RW ← GG_FS_Users", edge.Display);

        plan.Dispose();
    }

    /// <summary>
    /// The authored-node collections are ORDERED by Name (OrdinalIgnoreCase) so the combos/list
    /// read stably regardless of insertion order; <see cref="PlanViewModel.GroupNodes"/> is the
    /// group-kind subset (a user never appears there). Pins the deterministic display order the
    /// spec's <c>RefreshAuthoredCollections</c> guarantees.
    /// </summary>
    [Fact(Timeout = 30_000)]
    public async Task Nodes_OrderedByName_GroupNodes_AreTheGroupSubset()
    {
        var plan = HeadlessPlan();
        // Insert out of name order; a user is interleaved.
        await AddNodeAsync(plan, PlanCreatableKind.GlobalGroup, "GG_Zulu");
        await AddNodeAsync(plan, PlanCreatableKind.User, "Mike Mock");
        await AddNodeAsync(plan, PlanCreatableKind.DomainLocalGroup, "DL_Alpha_RW");

        var names = plan.Nodes.Select(r => r.Name).ToArray();
        var sorted = names.OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToArray();
        Assert.Equal(sorted, names);

        // GroupNodes is the group subset (the user is excluded).
        Assert.DoesNotContain(plan.GroupNodes, r => r.Kind == PlanCreatableKind.User);
        Assert.Equal(2, plan.GroupNodes.Count); // GG_Zulu + DL_Alpha_RW

        plan.Dispose();
    }

    // =====================================================================================
    //  helpers
    // =====================================================================================

    /// <summary>Independent ground truth for the live-validation test: the EXACT finding the
    /// default ruleset reports for a user authored as a direct member of a DL, computed via the
    /// same projection + <see cref="RuleEngine.Evaluate"/> seam the VM uses (so the test pins the
    /// real engine output, never an assumed message). Returns the single nesting finding.</summary>
    private static RuleViolation ExpectedUserInDlFinding(string dlName, string userName)
    {
        // Build a throwaway plan in the SAME base OU so the formed DNs match the VM's.
        var probe = new PlanModel(PlanBaseOuDn);
        // dlName/userName arrive as DNs here; re-author by NAME to reproduce identical DNs.
        var dl = probe.AddNode(PlanCreatableKind.DomainLocalGroup, NameOf(dlName));
        var user = probe.AddNode(PlanCreatableKind.User, NameOf(userName));
        probe.AddEdge(dl.Dn, user.Dn);

        var snapshot = PlanProjection.ToSnapshot(probe);
        var report = RuleEngine.Evaluate(snapshot, RulesetLoader.LoadDefault());
        return Assert.Single(report.Violations, v => v.RuleId == RuleIds.Nesting);
    }

    /// <summary>The CN RDN value of a plan DN (CN=&lt;name&gt;,&lt;baseOu&gt;) — used by the
    /// ground-truth probe to re-author by name and reproduce the same formed DN.</summary>
    private static string NameOf(string dn)
    {
        var rdn = dn.Split(',', 2)[0];
        return rdn.StartsWith("CN=", StringComparison.OrdinalIgnoreCase) ? rdn[3..] : rdn;
    }

    /// <summary>Authors a node through the add-object COMMAND (the production seam — not a raw
    /// model call) and returns its DN. Drives the whole form: set kind + name (+ SAM for a
    /// user), execute, assert it succeeded.</summary>
    private static async Task<string> AddNodeAsync(
        PlanViewModel plan, PlanCreatableKind kind, string name, string? sam = null)
    {
        plan.NewObjectKind = kind;
        plan.NewObjectName = name;
        plan.NewObjectSam = sam ?? string.Empty;
        await plan.AddObjectCommand.ExecuteAsync(null);
        Assert.Null(plan.EditError); // the helper authors only valid nodes
        return plan.Plan.FormDn(name);
    }

    /// <summary>The row instance in <paramref name="rows"/> whose Dn matches
    /// <paramref name="dn"/> under <see cref="Dn.Comparer"/> — the combos/list bind the SAME
    /// instances, so selection must use them (a fresh row would not be the SelectedItem).</summary>
    private static PlanNodeRowModel Row(IEnumerable<PlanNodeRowModel> rows, string dn) =>
        Assert.Single(rows, r => Dn.Comparer.Equals(r.Dn, dn));

    private static EffectiveRuleset DefaultEffectiveRuleset() =>
        new(RulesetLoader.LoadDefault(), FromUserFile: false, []);

    /// <summary>A headless plan VM (no renderer factory): every editor command must
    /// drive <see cref="PlanViewModel.RevalidateAsync"/> null-renderer-safe. Rooted at the lab
    /// base OU, default ruleset.</summary>
    private static PlanViewModel HeadlessPlan() =>
        new(PlanBaseOuDn, DefaultEffectiveRuleset());
}
