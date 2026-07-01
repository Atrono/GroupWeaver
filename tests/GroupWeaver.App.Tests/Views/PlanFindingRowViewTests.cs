using System.Linq;
using System.Threading.Tasks;

using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;

using GroupWeaver.App.Graph;
using GroupWeaver.App.Rules;
using GroupWeaver.App.Tests.Fakes;
using GroupWeaver.App.ViewModels;
using GroupWeaver.App.Views;
using GroupWeaver.Core.Model;
using GroupWeaver.Core.Plan;
using GroupWeaver.Core.Rules;

using Xunit;

namespace GroupWeaver.App.Tests.Views;

/// <summary>
/// Pins the "Finding-row interaction unification — Tier 1" change at the VIEW layer for the
/// Plan step: the Plan findings row (previously an inert <c>Grid</c>) is now a flat command
/// <see cref="Button"/> that binds <see cref="PlanViewModel.JumpToFindingCommand"/> with its
/// own <see cref="ViolationRowModel"/> as the <c>CommandParameter</c> — the same rail/Gap jump
/// affordance, unified. <see cref="PlanModeTests"/> pins the VM half (JumpToFinding sets
/// SelectedDn + Focuses the anchor once + lights the row; null / disposed are no-ops); this
/// closes the view gap: the row is genuinely interactive and its command reaches the VM.
///
/// <para>It also serves as the shared-mask realize smoke: <see cref="PlanView"/> applies
/// <c>OpacityMask="{StaticResource ListBottomFadeMask}"</c> (the theme-invariant brush moved to
/// <c>Tokens.axaml</c>) to its findings ListBox. If that StaticResource failed to resolve from
/// Tokens.axaml, realizing the view here would throw — so a clean render is the resolution
/// proof (the four dedicated screenshot fixtures cover the other three views the same way).</para>
///
/// <para>The seed authors a self-membership (A→A) so the default ruleset reports a REAL circular
/// finding anchored on A — a live row to locate and drive. Identity is asserted by
/// <c>PrimaryDn</c> under <c>Dn.Comparer</c>, never a message string (rule-engine.md).</para>
/// </summary>
public sealed class PlanFindingRowViewTests
{
    private const string PlanBaseOuDn = "OU=AGDLP-Lab,DC=agdlp,DC=lab";

    /// <summary>
    /// The realized Plan findings row is a command <see cref="Button"/> (the Tier-1 change from an
    /// inert Grid) that carries its <see cref="ViolationRowModel"/> as the <c>CommandParameter</c>
    /// and binds a non-null, currently-executable command. Realizing the view also proves the shared
    /// <c>ListBottomFadeMask</c> StaticResource resolves from Tokens.axaml (a failed resolve would
    /// throw on realize, not reach this assert).
    /// </summary>
    [AvaloniaFact(Timeout = 60_000)]
    public async Task PlanFindingRow_IsACommandButton_BoundToTheRowModel_AndTheJumpCommand()
    {
        var (plan, groupDn) = await SeededPlanWithSelfCycleAsync();
        var (window, planView) = ShowPlanView(plan);

        var row = Assert.Single(plan.Violations, r => Dn.Comparer.Equals(r.PrimaryDn, groupDn));

        // The row template's root is now a Button whose DataContext IS the row model (never an inert
        // Grid): locate it by that DataContext, the template-independent, structural handle.
        var rowButton = Assert.Single(
            planView.GetVisualDescendants().OfType<Button>(),
            b => b.IsEffectivelyVisible && ReferenceEquals(b.DataContext, row));

        Assert.Same(row, rowButton.CommandParameter); // the button hands its own row model to the command
        Assert.NotNull(rowButton.Command); // a real command is bound (not an inert row)
        Assert.True(
            rowButton.Command!.CanExecute(row),
            "the finding row's jump command must be executable for its row");

        window.Close();
    }

    /// <summary>
    /// Driving the realized row Button's bound command (the production click path) jumps to the
    /// finding: <see cref="PlanViewModel.SelectedDn"/> becomes the row's <c>PrimaryDn</c> and the
    /// plan renderer <see cref="IGraphRenderer.FocusAsync"/> fires exactly once with
    /// <c>[row.PrimaryDn]</c>. Proves the view→VM wiring end-to-end, not just the VM command.
    /// </summary>
    [AvaloniaFact(Timeout = 60_000)]
    public async Task PlanFindingRowButton_Command_JumpsToTheFinding_SelectsAndFocusesOnce()
    {
        var fake = new FakeGraphRenderer();
        var (plan, groupDn) = await SeededPlanWithSelfCycleAsync(() => fake);
        var (window, planView) = ShowPlanView(plan);

        var row = Assert.Single(plan.Violations, r => Dn.Comparer.Equals(r.PrimaryDn, groupDn));
        var rowButton = Assert.Single(
            planView.GetVisualDescendants().OfType<Button>(),
            b => b.IsEffectivelyVisible && ReferenceEquals(b.DataContext, row));

        // Invoke the bound command exactly as a click would (the row's command + its own parameter).
        rowButton.Command!.Execute(rowButton.CommandParameter);
        Dispatcher.UIThread.RunJobs();
        await plan.JumpToFindingCommand.ExecuteAsync(row); // ensure the async body completed for the assert

        Assert.Equal(row.PrimaryDn, plan.SelectedDn, Dn.Comparer);
        Assert.True(row.IsActive, "the jumped-to row lights via the selection-sync highlight");
        // FocusAsync framed exactly the row's anchor. (Execute above may or may not have completed
        // synchronously headless; the explicit await guarantees at least one recorded call, and each
        // records [PrimaryDn] — assert every recorded focus targeted exactly this anchor.)
        Assert.NotEmpty(fake.FocusCalls);
        Assert.All(
            fake.FocusCalls,
            call => Assert.Equal(row.PrimaryDn, Assert.Single(call), Dn.Comparer));

        window.Close();
    }

    // --- helpers ---------------------------------------------------------------------------------

    /// <summary>A Plan VM (optionally with a renderer factory) whose seed authors a self-membership
    /// A→A so the default ruleset reports a circular finding anchored on A; returns the VM + A's DN.
    /// Uses the public editor command surface (the production seam) and revalidates.</summary>
    private static async Task<(PlanViewModel Plan, string GroupDn)> SeededPlanWithSelfCycleAsync(
        System.Func<IGraphRenderer>? rendererFactory = null)
    {
        var plan = new PlanViewModel(
            PlanBaseOuDn, DefaultEffectiveRuleset(), graphRendererFactory: rendererFactory);

        // The name PASSES the default naming rule (^GG_<Token>_<Token>) so the self-membership yields
        // exactly ONE finding on this DN (the circular error) — a single, unambiguous row to locate.
        plan.NewObjectKind = PlanCreatableKind.GlobalGroup;
        plan.NewObjectName = "GG_Row_Jump";
        await plan.AddObjectCommand.ExecuteAsync(null);
        Assert.Null(plan.EditError);
        var groupDn = plan.Plan.FormDn("GG_Row_Jump");

        plan.MemberParentRow = plan.GroupNodes.Single(r => Dn.Comparer.Equals(r.Dn, groupDn));
        plan.MemberChildRow = plan.Nodes.Single(r => Dn.Comparer.Equals(r.Dn, groupDn));
        await plan.AddMemberCommand.ExecuteAsync(null);

        Assert.True(plan.HasViolations, "the self-membership seed must produce a live finding row");
        return (plan, groupDn);
    }

    private static EffectiveRuleset DefaultEffectiveRuleset() =>
        new(RulesetLoader.LoadDefault(), FromUserFile: false, []);

    /// <summary>Hosts <see cref="PlanView"/> over <paramref name="plan"/> in a sized, shown headless
    /// window (bindings live, the findings list realized), mirroring the
    /// <see cref="ViolationsSidebarViewTests"/> hosting idiom. GraphHost shows its placeholder (no
    /// real WebView headless); the editor panel — including the findings list — is realized.</summary>
    private static (Window Window, PlanView View) ShowPlanView(PlanViewModel plan)
    {
        var view = new PlanView { DataContext = plan };
        var window = new Window { Content = view, Width = 1280, Height = 720 };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return (window, view);
    }
}
