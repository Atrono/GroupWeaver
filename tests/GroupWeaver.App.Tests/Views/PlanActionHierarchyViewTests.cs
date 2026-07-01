using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;

using GroupWeaver.App.Rules;
using GroupWeaver.App.ViewModels;
using GroupWeaver.App.Views;
using GroupWeaver.Core.Model;
using GroupWeaver.Core.Plan;
using GroupWeaver.Core.Rules;

using Xunit;

namespace GroupWeaver.App.Tests.Views;

/// <summary>
/// Pins issue #221 (Lever 2, action hierarchy) at the VIEW layer for the Plan editor: the
/// create/apply PRIMARIES carry <c>accent</c> and the destructive/secondary actions carry
/// <c>ghost</c>, so the hierarchy the implementer set (only the <c>Classes=</c> attribute
/// changed — the existing accent/ghost classes are reused) cannot silently drift back to
/// all-grey. A class regression on any of these buttons fails here, not in a PNG review.
///
/// <list type="bullet">
///   <item><b>Primaries (accent):</b> Add-object <c>Add</c> (<c>AddObjectButton</c>),
///   Add-membership <c>Add member</c> (<c>AddMemberButton</c>), selected-node
///   <c>Rename</c> (<c>RenameButton</c>).</item>
///   <item><b>Secondary / destructive (ghost):</b> selected-node <c>Remove</c>
///   (<c>RemoveButton</c>), the per-membership-row <c>Remove</c> (unnamed template button,
///   located by its <c>RemoveMemberCommand</c> binding), and the four header-toolbar buttons —
///   <c>New plan</c> (<c>NewPlanButton</c>), <c>Gap analysis</c> (<c>GapAnalysisButton</c>),
///   <c>Export script</c> (<c>ExportScriptButton</c>), back-to-explore (<c>BackButton</c>).</item>
/// </list>
///
/// <para>The <see cref="PlanView"/> is realized headless over a seeded plan: a group, a user,
/// a membership between them, and a selected node — so the selected-node Rename/Remove pair and
/// the per-row Remove are ACTUALLY realized (they are gated on <c>HasSelectedNode</c> / a
/// membership existing). GraphHost shows its placeholder (no real WebView headless); the editor
/// panel — every button under test — realizes. The named buttons are located by their stable
/// <c>x:Name</c> seam; the unnamed per-row Remove is located by its bound command instance
/// (robust against the two "Remove" contents). <see cref="PlanView"/> owns no
/// <c>UiStateStore</c> seam and no rail, so the #124 rail-collapse realization hazard does not
/// apply here — it is a plain editor UserControl.</para>
/// </summary>
public sealed class PlanActionHierarchyViewTests
{
    private const string PlanBaseOuDn = "OU=AGDLP-Lab,DC=agdlp,DC=lab";

    // --- create/apply primaries carry accent --------------------------------------------

    [AvaloniaFact(Timeout = 60_000)]
    public async Task AddObjectButton_IsAccentPrimary()
    {
        var (window, view, _) = await ShowSeededPlanAsync();

        var add = Named(view, "AddObjectButton");
        Assert.Contains("accent", add.Classes);
        Assert.DoesNotContain("ghost", add.Classes);

        window.Close();
    }

    [AvaloniaFact(Timeout = 60_000)]
    public async Task AddMemberButton_IsAccentPrimary()
    {
        var (window, view, _) = await ShowSeededPlanAsync();

        var addMember = Named(view, "AddMemberButton");
        Assert.Contains("accent", addMember.Classes);
        Assert.DoesNotContain("ghost", addMember.Classes);

        window.Close();
    }

    [AvaloniaFact(Timeout = 60_000)]
    public async Task RenameButton_IsAccentPrimary()
    {
        var (window, view, _) = await ShowSeededPlanAsync();

        var rename = Named(view, "RenameButton");
        Assert.Contains("accent", rename.Classes);
        Assert.DoesNotContain("ghost", rename.Classes);

        window.Close();
    }

    // --- destructive / secondary actions carry ghost ------------------------------------

    [AvaloniaFact(Timeout = 60_000)]
    public async Task SelectedNodeRemoveButton_IsGhostSecondary()
    {
        var (window, view, _) = await ShowSeededPlanAsync();

        var remove = Named(view, "RemoveButton");
        Assert.Contains("ghost", remove.Classes);
        Assert.DoesNotContain("accent", remove.Classes);

        window.Close();
    }

    [AvaloniaFact(Timeout = 60_000)]
    public async Task PerMembershipRowRemoveButton_IsGhostSecondary()
    {
        var (window, view, plan) = await ShowSeededPlanAsync();

        // The per-row Remove is an unnamed template button; disambiguate from the selected-node
        // Remove (RemoveButton, RemoveSelectedCommand) by its bound RemoveMemberCommand instance.
        var rowRemove = Assert.Single(
            view.GetVisualDescendants().OfType<Button>(),
            b => b.IsEffectivelyVisible && b.Command == plan.RemoveMemberCommand);

        Assert.Contains("ghost", rowRemove.Classes);
        Assert.DoesNotContain("accent", rowRemove.Classes);

        window.Close();
    }

    [AvaloniaTheory(Timeout = 60_000)]
    [InlineData("NewPlanButton")]
    [InlineData("GapAnalysisButton")]
    [InlineData("ExportScriptButton")]
    [InlineData("BackButton")]
    public async Task HeaderToolbarButton_IsGhostSecondary(string name)
    {
        var (window, view, _) = await ShowSeededPlanAsync();

        var button = Named(view, name);
        Assert.Contains("ghost", button.Classes);
        Assert.DoesNotContain("accent", button.Classes);

        window.Close();
    }

    // --- helpers ------------------------------------------------------------------------

    /// <summary>The single realized, visible <see cref="Button"/> carrying <paramref name="name"/>
    /// (the stable <c>x:Name</c> seam) — <see cref="Assert.Single{T}(System.Collections.Generic.IEnumerable{T}, System.Func{T, bool})"/>
    /// makes the locator non-vacuous (a rename or a gating regression fails here).</summary>
    private static Button Named(PlanView view, string name) =>
        Assert.Single(
            view.GetVisualDescendants().OfType<Button>(),
            b => b.IsEffectivelyVisible && b.Name == name);

    /// <summary>Realize <see cref="PlanView"/> over a plan seeded with a group, a user, a
    /// membership between them, and a selected node — so every button under test (including the
    /// <c>HasSelectedNode</c>-gated Rename/Remove pair and the per-row Remove) is realized.</summary>
    private static async Task<(Window Window, PlanView View, PlanViewModel Plan)> ShowSeededPlanAsync()
    {
        var plan = new PlanViewModel(PlanBaseOuDn, DefaultEffectiveRuleset());

        var groupDn = await AddNodeAsync(plan, PlanCreatableKind.GlobalGroup, "GG_Team");
        var userDn = await AddNodeAsync(plan, PlanCreatableKind.User, "Alice Adams", sam: "aadams");

        plan.MemberParentRow = Row(plan.GroupNodes, groupDn);
        plan.MemberChildRow = Row(plan.Nodes, userDn);
        await plan.AddMemberCommand.ExecuteAsync(null);
        Assert.NotEmpty(plan.Memberships); // the per-row Remove must have a row to render

        // Select a node so the selected-node Rename/Remove pair realizes (HasSelectedNode gate).
        plan.SelectedNodeRow = Row(plan.Nodes, groupDn);
        Assert.True(plan.HasSelectedNode);

        var view = new PlanView { DataContext = plan };
        var window = new Window { Content = view, Width = 1280, Height = 720 };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return (window, view, plan);
    }

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

    private static PlanNodeRowModel Row(IEnumerable<PlanNodeRowModel> rows, string dn) =>
        Assert.Single(rows, r => Dn.Comparer.Equals(r.Dn, dn));

    private static EffectiveRuleset DefaultEffectiveRuleset() =>
        new(RulesetLoader.LoadDefault(), FromUserFile: false, []);
}
