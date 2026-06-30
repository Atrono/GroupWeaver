using System;
using System.Collections.Generic;
using System.Linq;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;

using GroupWeaver.App.ViewModels;
using GroupWeaver.App.Views;
using GroupWeaver.Core.Model;
using GroupWeaver.Core.Rules;

using Xunit;

namespace GroupWeaver.App.Tests.Views;

/// <summary>
/// Pins the in-Audit per-row triage UI that landed AXAML-only on <c>feat/audit-row-triage</c>
/// (<see cref="AuditView"/>): the EXISTING <see cref="AuditViewModel.AcknowledgeRowCommand"/> /
/// <see cref="AuditViewModel.SuppressRowCommand"/> / <see cref="AuditViewModel.UntriageRowCommand"/>
/// surfaced two ways — (a) the detail-pane Acknowledge/Suppress/Un-triage buttons (gated on the
/// selected finding's triage status) and (b) the per-row <c>Grid.ContextFlyout</c>
/// <see cref="MenuFlyout"/>. Both bind to the VM through the <c>$parent[UserControl]</c> hop the
/// categories-pane buttons already use, with the row (or <c>SelectedFinding</c>) as the
/// <c>CommandParameter</c>.
///
/// <para>This file is split into three concerns:</para>
/// <list type="number">
///   <item><b>VM per-row commands</b> — the load-bearing logic (no per-row tests existed yet): with the
///   triage seam armed by a capturing fake, each per-row command emits exactly the right single
///   <see cref="TriageRequest"/>, the <see cref="AuditViewModel.UntriageRowCommand"/> respects the
///   <see cref="AuditFindingRowModel.IsTriaged"/> guard, <see cref="AuditViewModel.AcknowledgeRowCommand"/>
///   no-ops on an already-triaged row (the <c>!IsTriaged</c> filter in <c>TriageRows</c>), and with NO
///   seam armed every command no-ops without throwing.</item>
///   <item><b>Detail-pane button wiring</b> (headless view): the Acknowledge/Suppress/Un-triage buttons
///   render, are command-bound, and are visibility-gated on the SELECTED finding's triage status; firing
///   the bound command triages the selected row.</item>
///   <item><b>ContextFlyout runtime resolution</b> — the open risk: the row <see cref="MenuFlyout"/>
///   <see cref="MenuItem"/>s bind via <c>$parent[UserControl]</c> from INSIDE a popup. This opens a row's
///   flyout and asserts whether the <see cref="MenuItem.Command"/> resolves to a non-null command +
///   carries the row as its parameter at runtime (the decision input for keeping vs. dropping the flyout
///   in src).</item>
/// </list>
///
/// <para>Compares PROJECTIONS, never record/collection identity (rule-engine.md / data-model.md):
/// captured requests are asserted on their (Dn, RuleId, Kind, Reason) value tuple. The detail-pane /
/// flyout cases run over the GG_Circle_A ↔ GG_Circle_B cycle scope so the report build the view projects
/// MUST terminate over the cycle (the circular-case charter). <see cref="AuditView"/> owns no graph
/// renderer and no <c>UiStateStore</c> seam, so the #124 rail-collapse realization hazard does not apply.
/// Read-only product: triage routes through the ADR-028 ignore-entry callback — never an AD write.</para>
/// </summary>
public sealed class AuditRowTriageViewTests
{
    private const string RootDn = "OU=Lab,DC=stub,DC=lab";

    // The badly-named, empty GG: it carries a naming Warning + an empty-group Info in the fixture, so
    // its escaped DN is a stable per-row triage subject. (Mirrors AuditTriageTests' GgBadNameDn.)
    private const string GgBadNameDn = "CN=NotAConventionName,OU=Lab,DC=stub,DC=lab";

    // ============================================================================================
    // (1) VM per-row commands — the load-bearing logic (no shell, capturing-fake seam)
    // ============================================================================================

    /// <summary>
    /// With the triage seam armed by a capturing fake, <see cref="AuditViewModel.AcknowledgeRowCommand"/>
    /// on an OPEN row emits EXACTLY ONE <see cref="TriageRequest"/> of
    /// <c>(Escape(row.PrimaryDn), row.RuleId, Acknowledge, null)</c>; <see cref="AuditViewModel.SuppressRowCommand"/>
    /// is the equal-shape Suppress twin. The DN is glob-escaped via <see cref="TriageEntry.Escape"/>
    /// (identity on a genuine DN), the rule id rides along, the reason is null.
    /// </summary>
    [Fact]
    public void AcknowledgeRow_AndSuppressRow_OnOpenRow_EmitExactlyOneRequest_EscapedDn_RuleId_NullReason()
    {
        var ack = NewAuditCapturing(out var ackBatches);
        var ackRow = OpenNamingRow(ack);

        ack.AcknowledgeRowCommand.Execute(ackRow);

        var ackBatch = Assert.Single(ackBatches);
        var ackReq = Assert.Single(ackBatch);
        Assert.Equal(
            (TriageEntry.Escape(ackRow.PrimaryDn), ackRow.RuleId, TriageKind.Acknowledge, (string?)null),
            (ackReq.Dn, ackReq.RuleId, ackReq.Kind, ackReq.Reason));
        // The escape is the identity on a real DN — the stored Dn is the finding's own PrimaryDn.
        Assert.Equal(ackRow.PrimaryDn, ackReq.Dn);
        ack.Dispose();

        var sup = NewAuditCapturing(out var supBatches);
        var supRow = OpenNamingRow(sup);

        sup.SuppressRowCommand.Execute(supRow);

        var supBatch = Assert.Single(supBatches);
        var supReq = Assert.Single(supBatch);
        Assert.Equal(
            (TriageEntry.Escape(supRow.PrimaryDn), supRow.RuleId, TriageKind.Suppress, (string?)null),
            (supReq.Dn, supReq.RuleId, supReq.Kind, supReq.Reason));
        sup.Dispose();
    }

    /// <summary>
    /// <see cref="AuditViewModel.UntriageRowCommand"/> honours the <see cref="AuditFindingRowModel.IsTriaged"/>
    /// guard: on a TRIAGED row it emits EXACTLY ONE <c>(Escape(dn), ruleId, Untriage, null)</c> request;
    /// on an OPEN row it emits NOTHING (the explicit <c>if (row.IsTriaged)</c> guard in <c>UntriageRow</c>),
    /// never an empty-batch invoke.
    /// </summary>
    [Fact]
    public void UntriageRow_OnTriagedRow_EmitsOneUntriage_OnOpenRow_EmitsNothing()
    {
        // --- TRIAGED row: a tagged [suppress] ignore entry for the naming DN makes the row Suppressed.
        var triagedAudit = NewAuditCapturing(out var batches, suppressNamingFinding: true);
        var triagedRow = NamingRow(triagedAudit);
        Assert.True(triagedRow.IsTriaged, "the fixture must present the naming row as already triaged");

        triagedAudit.UntriageRowCommand.Execute(triagedRow);

        var batch = Assert.Single(batches);
        var req = Assert.Single(batch);
        Assert.Equal(
            (TriageEntry.Escape(triagedRow.PrimaryDn), triagedRow.RuleId, TriageKind.Untriage, (string?)null),
            (req.Dn, req.RuleId, req.Kind, req.Reason));
        triagedAudit.Dispose();

        // --- OPEN row: Un-triage is a no-op (the IsTriaged guard) — the seam is NEVER invoked.
        var openAudit = NewAuditCapturing(out var openBatches);
        var openRow = OpenNamingRow(openAudit);

        openAudit.UntriageRowCommand.Execute(openRow);

        Assert.Empty(openBatches); // no invoke at all (not even an empty batch)
        openAudit.Dispose();
    }

    /// <summary>
    /// <see cref="AuditViewModel.AcknowledgeRowCommand"/> on an ALREADY-Acknowledged row is a no-op: the
    /// <c>!IsTriaged</c> filter in <c>TriageRows</c> drops the only candidate, leaving an EMPTY batch that
    /// <c>Submit</c> never hands to the seam (re-tagging an already-ignored finding is meaningless).
    /// </summary>
    [Fact]
    public void AcknowledgeRow_OnAlreadyTriagedRow_IsNoOp_SeamNeverInvoked()
    {
        var audit = NewAuditCapturing(out var batches, suppressNamingFinding: true);
        var triagedRow = NamingRow(audit);
        Assert.True(triagedRow.IsTriaged);

        audit.AcknowledgeRowCommand.Execute(triagedRow);
        audit.SuppressRowCommand.Execute(triagedRow);

        Assert.Empty(batches); // both per-row Ack/Suppress no-op on a triaged row (the !IsTriaged filter)
        audit.Dispose();
    }

    /// <summary>
    /// With NO triage seam armed (the headless / un-wired default — <c>_triage</c> is null), every per-row
    /// command no-ops cleanly: no throw, nothing captured. The <c>Submit</c> null-conditional invoke
    /// (<c>_triage?.Invoke</c>) is the only write path — there is never a parallel one.
    /// </summary>
    [Fact]
    public void PerRowCommands_WithNoSeamArmed_NoOpWithoutThrowing()
    {
        var (snapshot, ruleset) = LoadedScopeWithFindings();
        var report = RuleEngine.Evaluate(snapshot, ruleset);
        // Note: NO UseTriageCallback — the seam is dead.
        using var audit = new AuditViewModel(snapshot, report, ruleset, RootDn, onBack: () => { });
        var row = OpenNamingRow(audit);

        // None of these throw, and there is no observable effect to assert beyond "did not throw".
        audit.AcknowledgeRowCommand.Execute(row);
        audit.SuppressRowCommand.Execute(row);
        audit.UntriageRowCommand.Execute(row); // an OPEN row anyway, doubly inert
    }

    // ============================================================================================
    // (2) Detail-pane button wiring (headless view)
    // ============================================================================================

    /// <summary>
    /// Selecting an OPEN finding realizes the detail pane's Acknowledge + Suppress buttons (visible,
    /// bound to the per-row commands, parameterised with <see cref="AuditViewModel.SelectedFinding"/>)
    /// and HIDES Un-triage; selecting a TRIAGED finding flips the visibility (Un-triage visible, Ack/
    /// Suppress hidden). Firing the Acknowledge button's bound command triages the selected row (the
    /// capturing seam receives one Acknowledge request for that row).
    /// </summary>
    [AvaloniaFact]
    public void DetailPaneButtons_AreCommandBound_VisibilityGatedByStatus_AndFireTriageForSelectedRow()
    {
        var audit = NewAuditCapturing(out var batches, suppressNamingFinding: true);
        var (window, view) = ShowAudit(audit);

        // --- OPEN finding selected: Ack/Suppress visible + bound, Un-triage hidden.
        var openRow = audit.Findings.First(r => !r.IsTriaged);
        audit.SelectedFinding = openRow;
        Dispatcher.UIThread.RunJobs();

        var ackBtn = NamedButton(view, "AcknowledgeRowButton");
        var supBtn = NamedButton(view, "SuppressRowButton");
        var unBtn = NamedButton(view, "UntriageRowButton");

        Assert.True(ackBtn.IsEffectivelyVisible, "Acknowledge must show for an Open finding");
        Assert.True(supBtn.IsEffectivelyVisible, "Suppress must show for an Open finding");
        Assert.False(unBtn.IsEffectivelyVisible, "Un-triage must be hidden for an Open finding");

        // Bound to the per-row commands (never cross-wired) and parameterised with the SELECTED row.
        Assert.Same(audit.AcknowledgeRowCommand, ackBtn.Command);
        Assert.Same(audit.SuppressRowCommand, supBtn.Command);
        Assert.Same(openRow, ackBtn.CommandParameter);
        Assert.Same(openRow, supBtn.CommandParameter);

        // Firing the bound command triages the selected open row (one Acknowledge request for it).
        ackBtn.Command!.Execute(ackBtn.CommandParameter);
        var batch = Assert.Single(batches);
        var req = Assert.Single(batch);
        Assert.Equal(
            (TriageEntry.Escape(openRow.PrimaryDn), openRow.RuleId, TriageKind.Acknowledge),
            (req.Dn, req.RuleId, req.Kind));

        // --- TRIAGED finding selected: Un-triage visible, Ack/Suppress hidden (the inverse gate).
        var triagedRow = audit.Findings.First(r => r.IsTriaged);
        audit.SelectedFinding = triagedRow;
        Dispatcher.UIThread.RunJobs();

        Assert.True(unBtn.IsEffectivelyVisible, "Un-triage must show for a triaged finding");
        Assert.False(ackBtn.IsEffectivelyVisible, "Acknowledge must be hidden for a triaged finding");
        Assert.False(supBtn.IsEffectivelyVisible, "Suppress must be hidden for a triaged finding");
        Assert.Same(audit.UntriageRowCommand, unBtn.Command);
        Assert.Same(triagedRow, unBtn.CommandParameter);

        window.Close();
        audit.Dispose();
    }

    // ============================================================================================
    // (3) ContextFlyout runtime resolution — the open risk
    // ============================================================================================

    /// <summary>
    /// THE risk this slice has to settle: the per-row <c>Grid.ContextFlyout</c>
    /// <see cref="MenuFlyout"/> binds its <see cref="MenuItem"/>s via <c>$parent[UserControl]</c> from
    /// INSIDE a popup. Compiled bindings type-check, but runtime ancestor resolution from a detached
    /// popup is uncertain (a popup is its own visual root). This test attaches the view to a window,
    /// realizes a row container, OPENS its <c>ContextFlyout</c>, then asserts for each MenuItem whether
    /// <see cref="MenuItem.Command"/> RESOLVES to a non-null command and <see cref="MenuItem.CommandParameter"/>
    /// is the row — and, if so, that invoking Acknowledge fires the triage. The assertions document the
    /// runtime answer either way (a null command fails loudly here, which is the signal to DROP the
    /// flyout in src and keep only the guaranteed detail-pane buttons).
    /// </summary>
    [AvaloniaFact]
    public void ContextFlyout_MenuItemCommands_ResolveAtRuntime_ToVmCommands_WithRowParameter()
    {
        var audit = NewAuditCapturing(out var batches);
        var (window, view) = ShowAudit(audit);

        // Realize a row container for an OPEN naming finding (the ListBox virtualizes; force containers).
        var list = Assert.Single(view.GetVisualDescendants().OfType<ListBox>(), lb => lb.Name == "FindingsList");
        var openRow = NamingRow(audit);
        list.SelectedItem = openRow;
        Dispatcher.UIThread.RunJobs();

        var container = list.ContainerFromItem(openRow);
        Assert.NotNull(container); // the row container realized

        // The per-row ContextFlyout lives on the template's root Grid. Find the row's Grid carrying it.
        var rowGrid = container!.GetVisualDescendants()
            .OfType<Grid>()
            .FirstOrDefault(g => g.ContextFlyout is MenuFlyout);
        Assert.NotNull(rowGrid);
        var flyout = (MenuFlyout)rowGrid!.ContextFlyout!;

        // Open the flyout at the row Grid (the popup mounts its MenuItems so their bindings evaluate).
        flyout.ShowAt(rowGrid);
        Dispatcher.UIThread.RunJobs();
        Assert.True(flyout.IsOpen, "the row ContextFlyout must actually open (else the binding pin is vacuous)");

        // The items MUST come from the genuinely-open popup host (not unrealized template items) — proven
        // by sourcing them from the protected Popup's Host subtree, with a count of exactly the three
        // verbs. (Verified empirically: the popup mounts in an in-window OverlayPopupHost.)
        var items = MenuItemsFromOpenPopup(flyout);
        Assert.Equal(3, items.Count); // Acknowledge / Suppress / Un-triage — from the open popup itself

        var ack = items.Single(i => (i.Header as string) == "Acknowledge");
        var sup = items.Single(i => (i.Header as string) == "Suppress");
        var un = items.Single(i => (i.Header as string) == "Un-triage");

        // THE pin: do the popup-hosted $parent[UserControl] command bindings resolve at runtime?
        // A null command here is the explicit signal to DROP the flyout in src (the detail-pane buttons
        // are the guaranteed path) — this assertion makes the runtime answer load-bearing.
        Assert.NotNull(ack.Command);
        Assert.NotNull(sup.Command);
        Assert.NotNull(un.Command);
        Assert.Same(audit.AcknowledgeRowCommand, ack.Command);
        Assert.Same(audit.SuppressRowCommand, sup.Command);
        Assert.Same(audit.UntriageRowCommand, un.Command);

        // The CommandParameter is the ROW itself ({Binding} = the DataContext inside the row template).
        Assert.Same(openRow, ack.CommandParameter);
        Assert.Same(openRow, sup.CommandParameter);
        Assert.Same(openRow, un.CommandParameter);

        // And invoking the Acknowledge MenuItem actually fires the triage for that row.
        ack.Command!.Execute(ack.CommandParameter);
        var batch = Assert.Single(batches);
        var req = Assert.Single(batch);
        Assert.Equal(
            (TriageEntry.Escape(openRow.PrimaryDn), openRow.RuleId, TriageKind.Acknowledge),
            (req.Dn, req.RuleId, req.Kind));

        flyout.Hide();
        window.Close();
        audit.Dispose();
    }

    // ============================================================================================
    // Helpers
    // ============================================================================================

    /// <summary>The naming-Warning row for <see cref="GgBadNameDn"/> (the per-row triage subject).</summary>
    private static AuditFindingRowModel NamingRow(AuditViewModel audit) =>
        audit.Findings.First(r => r.Severity == RuleSeverity.Warning && Dn.Comparer.Equals(r.PrimaryDn, GgBadNameDn));

    /// <summary>The naming row, asserted OPEN (the per-row Ack/Suppress target).</summary>
    private static AuditFindingRowModel OpenNamingRow(AuditViewModel audit)
    {
        var row = NamingRow(audit);
        Assert.False(row.IsTriaged, "the fixture's naming row must start Open");
        return row;
    }

    /// <summary>The detail-pane / actions <see cref="Button"/> with the given seam <c>Name</c> — exactly
    /// one realized (a rename or a virtualization hiding it fails loudly, not as a null deref).</summary>
    private static Button NamedButton(AuditView view, string name) =>
        Assert.Single(view.GetVisualDescendants().OfType<Button>(), b => b.Name == name);

    /// <summary>Collects the realized <see cref="MenuItem"/>s of an OPEN <see cref="MenuFlyout"/> from the
    /// genuinely-open popup itself (never the dormant row template), so a regression that leaves the
    /// bindings unevaluated fails loudly here rather than silently scanning the window. A flyout opens
    /// inside a <c>Popup</c> whose <c>Host</c> is its own popup visual root (empirically an in-window
    /// <c>OverlayPopupHost</c> in headless 11.3); <c>PopupFlyoutBase.Popup</c> is protected, so this reads
    /// it via reflection (test-only) and walks ONLY that host's subtree.</summary>
    private static IReadOnlyList<MenuItem> MenuItemsFromOpenPopup(MenuFlyout flyout)
    {
        var popup = typeof(PopupFlyoutBase)
            .GetProperty("Popup", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            ?.GetValue(flyout) as Popup;
        Assert.NotNull(popup);
        var host = popup!.Host as Visual;
        Assert.NotNull(host); // the open popup must have a realized host carrying the menu presenter
        return host!.GetVisualDescendants().OfType<MenuItem>().Distinct().ToList();
    }

    /// <summary>The audit view in a sized, shown headless window (bindings live, the table + detail pane
    /// realized) — the AuditExportButtonsViewTests hosting idiom, hosting AuditView directly.</summary>
    private static (Window Window, AuditView View) ShowAudit(AuditViewModel vm)
    {
        var view = new AuditView { DataContext = vm };
        var window = new Window { Content = view, Width = 1280, Height = 720 };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return (window, view);
    }

    /// <summary>A directly-constructed <see cref="AuditViewModel"/> over the shared fixture with a
    /// capturing triage seam armed (each batch handed to <c>_triage</c> is appended to
    /// <paramref name="batches"/>). When <paramref name="suppressNamingFinding"/> is true the ruleset
    /// carries a <c>[suppress]</c>-tagged ignore entry for the naming DN, so that row projects as
    /// Suppressed (a TRIAGED row) while staying listed in the would-be table.</summary>
    private static AuditViewModel NewAuditCapturing(
        out List<IReadOnlyList<TriageRequest>> batches, bool suppressNamingFinding = false)
    {
        var (snapshot, ruleset) = LoadedScopeWithFindings();

        if (suppressNamingFinding)
        {
            var tagged = TriageEntry.Build(
                new TriageRequest(TriageEntry.Escape(GgBadNameDn), "naming-gg", TriageKind.Suppress, null));
            ruleset = ruleset with { Ignore = ruleset.Ignore.Append(tagged).ToList() };
        }

        var report = RuleEngine.Evaluate(snapshot, ruleset);
        var audit = new AuditViewModel(snapshot, report, ruleset, RootDn, onBack: () => { });

        var captured = new List<IReadOnlyList<TriageRequest>>();
        audit.UseTriageCallback(reqs => captured.Add(reqs));
        batches = captured;
        return audit;
    }

    /// <summary>The shared WP5b/WP5c/WP5d/WP5e findings fixture (re-stated so this file stays
    /// independent): a fully-LOADED scope tripping the default ruleset's nesting (a DL with a direct
    /// User member) + naming (a badly-named GG) + empty-group rules — a real Error/Warning/Info mix —
    /// PLUS the GG_Circle_A ↔ GG_Circle_B nesting cycle (A→B→A), so the report build the view projects
    /// MUST terminate over the cycle (the circular-case charter). Returns the snapshot + default ruleset.
    /// Matches <see cref="AuditTriageTests"/>/<see cref="AuditTableTests"/> with the cycle added.</summary>
    private static (DirectorySnapshot Snapshot, Ruleset Ruleset) LoadedScopeWithFindings()
    {
        const string dlOk = "CN=DL_FileShare_RW,OU=Lab,DC=stub,DC=lab";
        const string ggMember = "CN=GG_FileShare_Members,OU=Lab,DC=stub,DC=lab";
        const string dlBad = "CN=DL_DirectUser_RW,OU=Lab,DC=stub,DC=lab";
        const string userDn = "CN=alice,OU=Lab,DC=stub,DC=lab";
        const string ggEmpty = "CN=GG_Empty_Team,OU=Lab,DC=stub,DC=lab";
        const string circleA = "CN=GG_Circle_A,OU=Lab,DC=stub,DC=lab";
        const string circleB = "CN=GG_Circle_B,OU=Lab,DC=stub,DC=lab";

        var snapshot = new DirectorySnapshot();
        snapshot.AddObject(Group(dlOk, AdObjectKind.DomainLocalGroup));
        snapshot.AddObject(Group(ggMember, AdObjectKind.GlobalGroup));
        snapshot.AddObject(Group(dlBad, AdObjectKind.DomainLocalGroup));
        snapshot.AddObject(new AdObject { Dn = userDn, Kind = AdObjectKind.User, Name = "alice" });
        snapshot.AddObject(Group(GgBadNameDn, AdObjectKind.GlobalGroup));
        snapshot.AddObject(Group(ggEmpty, AdObjectKind.GlobalGroup));
        snapshot.AddObject(Group(circleA, AdObjectKind.GlobalGroup));
        snapshot.AddObject(Group(circleB, AdObjectKind.GlobalGroup));

        snapshot.SetMembers(dlOk, new[] { ggMember });
        snapshot.SetMembers(ggMember, Array.Empty<string>());
        snapshot.SetMembers(dlBad, new[] { userDn });
        snapshot.SetMembers(GgBadNameDn, Array.Empty<string>());
        snapshot.SetMembers(ggEmpty, Array.Empty<string>());
        snapshot.SetMembers(circleA, new[] { circleB });
        snapshot.SetMembers(circleB, new[] { circleA }); // closes the A→B→A cycle (must terminate)

        return (snapshot, RulesetLoader.LoadDefault());
    }

    private static AdObject Group(string dn, AdObjectKind kind) => new()
    {
        Dn = dn,
        Kind = kind,
        Name = dn.Split(',')[0]["CN=".Length..],
    };
}
