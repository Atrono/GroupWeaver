using GroupWeaver.App.Graph;
using GroupWeaver.App.Rules;
using GroupWeaver.App.Tests.Fakes;
using GroupWeaver.App.ViewModels;
using GroupWeaver.Core.Model;
using GroupWeaver.Core.Providers;
using GroupWeaver.Core.Rules;
using GroupWeaver.Providers;

using Xunit;

namespace GroupWeaver.App.Tests;

/// <summary>
/// Pins the AP 3.4 S5 VM integration (ADR-010 §3/§5): <see cref="WorkspaceViewModel"/>
/// evaluates the loaded scope against the threaded ruleset (a <c>null</c> ctor ruleset
/// = the embedded default — so every pre-AP-3.4 workspace test stays green) at BOTH
/// graph-build sites — LoadAsync and the ExpandAsync fetch branch — BEFORE the renderer
/// call, inside the existing <see cref="WorkspaceViewModel.IsLoading"/> window (Evaluate
/// is pure/sync, ADR-009: no new gate). The cache-hit / focus-only branch does NOT
/// re-evaluate (topology and ruleset unchanged). The report drives the canonical-order
/// <see cref="WorkspaceViewModel.Violations"/> projection, the all-clear
/// (<see cref="WorkspaceViewModel.HasViolations"/>) and unchecked-areas
/// (<see cref="WorkspaceViewModel.HasUncheckedAreas"/>) surfaces, and reaches the
/// renderer seam (the fake records the report it receives). Jump-to-node (ADR-010 §5)
/// sets <see cref="WorkspaceViewModel.SelectedDn"/> AND focuses the anchor DN, is a
/// no-op while loading, and never throws on a raw-External anchor; selection sync
/// highlights every sidebar row whose anchor matches the selection.
///
/// The 19-finding baseline runs over the REAL <see cref="DemoProvider"/> rooted at the
/// demo OU — the same full snapshot AP 3.2's RuleEngineDemoBaselineTests pins exactly
/// (3 nesting errors, 1 circular error, 3 naming warnings, 12 empty-group infos), so the
/// VM is checked against the authoritative dataset, never a re-rolled table. The demo
/// dataset carries the GG_Circle_A ↔ GG_Circle_B cycle: every load over it must
/// terminate. VM-only behavior — plain facts, no visual tree.
/// </summary>
public sealed class WorkspaceViolationsTests
{
    // --- the full demo scope (yields the 19-finding baseline) --------------------------

    private const string DemoRootDn = "OU=AGDLP-Demo,DC=weavedemo,DC=example";
    private const string GroupSuffix = ",OU=Groups,OU=AGDLP-Demo,DC=weavedemo,DC=example";
    private const string UserSuffix = ",OU=Users,OU=AGDLP-Demo,DC=weavedemo,DC=example";

    // A DN that is the PrimaryDn (Dns[0]) of EXACTLY two findings — naming-dl and
    // empty-group both anchor at dl-finance-extra: the multi-row selection-sync pin.
    private const string DlFinanceExtraDn = "CN=dl-finance-extra" + GroupSuffix;

    // A nesting-error PARENT anchor (Dns[0] of the DL<-User deny) — a clean single hit.
    private const string DlFsSalesRwDn = "CN=DL_FS-Sales_RW" + GroupSuffix;

    // The user member endpoint of the DL<-User deny: it carries the finding (MaxSeverity
    // Error) but is NOT a PrimaryDn — selecting it must highlight ZERO rows (highlight is
    // by anchor, not by attached-DN).
    private const string User001Dn = "CN=Anna Acker (u001)" + UserSuffix;

    // The two raw builtin member DNs the demo's UncheckedDns surfaces (load-state truth).
    private const string DomainAdminsDn = "CN=Domain Admins,CN=Users,DC=weavedemo,DC=example";
    private const string PrintOperatorsDn = "CN=Print Operators,CN=Builtin,DC=weavedemo,DC=example";

    /// <summary>The full 19-finding baseline in canonical report order (ADR-009),
    /// projection-form (RuleId, Severity, Dns-joined) — copied from the AP 3.2
    /// RuleEngineDemoBaselineTests authority. If this drifts, suspect the dataset or the
    /// engine, never the VM.</summary>
    private static readonly (string RuleId, RuleSeverity Severity, string Dns)[] ExpectedBaseline =
    [
        (RuleIds.Nesting, RuleSeverity.Error, "CN=DL_FS-Finance_RO" + GroupSuffix + "CN=DL_Nested_RO" + GroupSuffix),
        (RuleIds.Nesting, RuleSeverity.Error, DlFsSalesRwDn + User001Dn),
        (RuleIds.Nesting, RuleSeverity.Error, "CN=UG_AllStaff" + GroupSuffix + "CN=Ben Acker (u002)" + UserSuffix),
        ("naming-gg", RuleSeverity.Warning, "CN=GG_X" + GroupSuffix),
        ("naming-gg", RuleSeverity.Warning, "CN=SalesTeamGlobal" + GroupSuffix),
        ("naming-dl", RuleSeverity.Warning, DlFinanceExtraDn),
        (RuleIds.Circular, RuleSeverity.Error, "CN=GG_Circle_A" + GroupSuffix + "CN=GG_Circle_B" + GroupSuffix),
        (RuleIds.EmptyGroup, RuleSeverity.Info, DlFinanceExtraDn),
        (RuleIds.EmptyGroup, RuleSeverity.Info, "CN=DL_App-CRM_RO" + GroupSuffix),
        (RuleIds.EmptyGroup, RuleSeverity.Info, "CN=DL_App-ERP_RO" + GroupSuffix),
        (RuleIds.EmptyGroup, RuleSeverity.Info, "CN=DL_FS-Legacy_RO" + GroupSuffix),
        (RuleIds.EmptyGroup, RuleSeverity.Info, "CN=DL_Nested_RO" + GroupSuffix),
        (RuleIds.EmptyGroup, RuleSeverity.Info, "CN=DL_Print-HQ_RO" + GroupSuffix),
        (RuleIds.EmptyGroup, RuleSeverity.Info, "CN=GG_Empty_Marketing" + GroupSuffix),
        (RuleIds.EmptyGroup, RuleSeverity.Info, "CN=GG_IT_Backup" + GroupSuffix),
        (RuleIds.EmptyGroup, RuleSeverity.Info, "CN=GG_IT_Helpdesk" + GroupSuffix),
        (RuleIds.EmptyGroup, RuleSeverity.Info, "CN=GG_X" + GroupSuffix),
        (RuleIds.EmptyGroup, RuleSeverity.Info, "CN=SalesTeamGlobal" + GroupSuffix),
        (RuleIds.EmptyGroup, RuleSeverity.Info, "CN=UG_ProjectX" + GroupSuffix),
    ];

    // --- (a) LoadAsync evaluates: report + sidebar projection + renderer push ----------

    [Fact(Timeout = 60_000)]
    public async Task LoadAsync_WithTheDefaultRuleset_ProducesTheNineteenFindingBaseline_InReportOrder()
    {
        var (vm, fake) = await DemoWorkspaceAsync(); // null ruleset => embedded default

        // The VM evaluated the loaded scope: the report is the authoritative baseline,
        // in canonical report order (unshuffled — ADR-009).
        Assert.Equal(
            ExpectedBaseline,
            vm.Report.Violations.Select(v => (v.RuleId, v.Severity, string.Join("", v.Dns))).ToArray());

        // The sidebar projection mirrors the report 1:1, same order, anchored at PrimaryDn.
        Assert.Equal(19, vm.Violations.Count);
        Assert.Equal(
            vm.Report.Violations.Select(v => (v.RuleId, v.Severity, v.PrimaryDn)).ToArray(),
            vm.Violations.Select(r => (RuleIdOf(vm, r), r.Severity, r.PrimaryDn)).ToArray());

        Assert.True(vm.HasViolations);

        // The report reached the renderer seam (the load-time Evaluate is observable on
        // the surface, not just in the VM): exactly one show, carrying THIS report.
        var pushed = Assert.Single(fake.ShownReports);
        Assert.Same(vm.Report, pushed);
        Assert.Empty(fake.UpdatedReports);

        vm.Dispose();
    }

    [Fact(Timeout = 60_000)]
    public async Task LoadAsync_DemoScope_SurfacesTheTwoBuiltinMemberDns_AsUncheckedAreas()
    {
        var (vm, _) = await DemoWorkspaceAsync();

        // UncheckedDns is load-state truth, never ignore-filtered (ADR-009): the two raw
        // builtin member DNs (frontier endpoints absent from the loaded scope) surface,
        // so the "unexpanded areas are unchecked" hint must show even with the full scope.
        Assert.Equal(new[] { DomainAdminsDn, PrintOperatorsDn }, vm.Report.UncheckedDns);
        Assert.True(vm.HasUncheckedAreas);

        vm.Dispose();
    }

    // --- (b) ExpandAsync re-evaluates; cache-hit / focus-only does NOT -----------------

    [Fact]
    public async Task ExpandFetch_ReEvaluates_PushesTheFreshReportThroughTheUpdate()
    {
        var snapshot = GroupScope(SalesDn);
        var provider = StubProvider(snapshot);
        var fake = new FakeGraphRenderer();
        var vm = Workspace(provider, () => fake);
        await vm.Initialization;

        var reportBefore = vm.Report;
        var shownReport = Assert.Single(fake.ShownReports);
        Assert.Same(reportBefore, shownReport);

        // Fetch a real member into GG_Sales: an EMPTY group becomes non-empty, so the
        // empty-group finding on GG_Sales disappears — the report MUST change.
        Assert.Contains(vm.Report.Violations, v =>
            v.RuleId == RuleIds.EmptyGroup && Dn.Comparer.Equals(v.PrimaryDn, SalesDn));
        provider.GetMembersHandler = (_, _) => Task.FromResult<IReadOnlyList<AdObject>>(
            [Obj("Ada Lovelace", AdaDn, AdObjectKind.User)]);

        fake.RaiseNodeExpandRequested(SalesDn, "GlobalGroup");
        await vm.Expansion;

        // Re-evaluated: a NEW report instance, and GG_Sales is no longer empty.
        Assert.NotSame(reportBefore, vm.Report);
        Assert.DoesNotContain(vm.Report.Violations, v =>
            v.RuleId == RuleIds.EmptyGroup && Dn.Comparer.Equals(v.PrimaryDn, SalesDn));

        // The fresh report rode the replace-in-place update to the renderer.
        var updatedReport = Assert.Single(fake.UpdatedReports);
        Assert.Same(vm.Report, updatedReport);

        // The sidebar re-projected from the fresh report.
        Assert.Equal(
            vm.Report.Violations.Select(v => v.PrimaryDn).ToArray(),
            vm.Violations.Select(r => r.PrimaryDn).ToArray());

        vm.Dispose();
    }

    [Fact]
    public async Task FocusOnlyExpand_DoesNotReEvaluate_ReportUnchanged_NoUpdate()
    {
        var snapshot = LoadedGroupScope(); // GG_Sales is LOADED (members cached)
        var provider = StubProvider(snapshot);
        var fake = new FakeGraphRenderer();
        var vm = Workspace(provider, () => fake);
        await vm.Initialization;

        var reportBefore = vm.Report;

        // Cache hit (loaded group) => pure camera move; the spec forbids a re-Evaluate
        // here (topology and ruleset unchanged), so the report object is reference-stable.
        fake.RaiseNodeExpandRequested(SalesDn, "GlobalGroup");
        await vm.Expansion;

        Assert.Same(reportBefore, vm.Report);
        Assert.Empty(fake.UpdatedReports); // no rebuild, no report push
        Assert.Single(fake.FocusCalls);

        vm.Dispose();
    }

    // --- (c) JumpCommand: select + focus, busy-gated, External-safe ---------------------

    [Fact]
    public async Task JumpCommand_SetsSelectedDn_AndFocusesTheAnchor()
    {
        var snapshot = LoadedGroupScope();
        var provider = StubProvider(snapshot);
        var fake = new FakeGraphRenderer();
        var vm = Workspace(provider, () => fake);
        await vm.Initialization;
        var focusBefore = fake.FocusCalls.Count;

        vm.JumpCommand.Execute(SalesDn);

        // The jump selects the anchor (detail-panel sync via OnSelectedDnChanged) AND
        // frames it on the graph (FocusAsync([dn])) — both halves, exactly the one DN.
        Assert.Equal(SalesDn, vm.SelectedDn);
        Assert.Equal(focusBefore + 1, fake.FocusCalls.Count);
        var focused = Assert.Single(fake.FocusCalls[^1]);
        Assert.Equal(SalesDn, focused, Dn.Comparer);

        vm.Dispose();
    }

    [Fact]
    public async Task JumpCommand_WhileLoading_IsANoOp_NeitherSelectsNorFocuses()
    {
        var loadGate = new TaskCompletionSource<DirectorySnapshot>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var provider = StubProvider(new DirectorySnapshot());
        provider.LoadScopeResult = loadGate.Task;
        var fake = new FakeGraphRenderer();
        var vm = Workspace(provider, () => fake);

        Assert.True(vm.IsLoading);
        Assert.False(vm.JumpCommand.CanExecute(SalesDn), "Jump is disarmed while the busy gate is held");

        // RelayCommand.Execute does NOT gate on CanExecute: a stale-armed Execute during
        // the load must still be a silent no-op (it would dereference a half-built state).
        vm.JumpCommand.Execute(SalesDn);

        Assert.Null(vm.SelectedDn);
        Assert.Empty(fake.FocusCalls);

        loadGate.SetResult(LoadedGroupScope());
        await vm.Initialization;

        // The gate released: the SAME jump now works (dropped, never disarmed).
        Assert.True(vm.JumpCommand.CanExecute(SalesDn));
        vm.JumpCommand.Execute(SalesDn);
        Assert.Equal(SalesDn, vm.SelectedDn);
        Assert.Single(fake.FocusCalls);

        vm.Dispose();
    }

    [Fact]
    public async Task JumpCommand_ToARawExternalMemberDn_SelectsAndFocuses_WithoutThrowing()
    {
        var snapshot = LoadedGroupScope(); // ExternalMemberDn is a member-edge endpoint only
        var provider = StubProvider(snapshot);
        var fake = new FakeGraphRenderer();
        var vm = Workspace(provider, () => fake);
        await vm.Initialization;

        // The anchor is a raw member DN absent from Snapshot.Objects (nesting findings
        // mark BOTH endpoints — rule-engine.md). A jump to it must NOT throw: the detail
        // panel renders the honest NotLoaded/External state, FocusAsync silently skips
        // an unknown DN (graph.js focusOn). This is the both-endpoints-must-not-go-dark pin.
        var ex = Record.Exception(() => vm.JumpCommand.Execute(ExternalMemberDn));
        Assert.Null(ex);

        Assert.Equal(ExternalMemberDn, vm.SelectedDn);
        var focused = Assert.Single(fake.FocusCalls[^1]);
        Assert.Equal(ExternalMemberDn, focused, Dn.Comparer);

        // The selection projected the detail panel without a provider call (snapshot-only):
        // a raw External anchor resolves to NotLoaded, never an exception.
        Assert.NotNull(vm.DetailPanel);
        Assert.Equal(ExternalMemberDn, vm.DetailPanel.Dn);
        Assert.Equal(0, provider.GetObjectCalls);
        Assert.Equal(0, provider.GetMembersCalls);

        vm.Dispose();
    }

    // --- (c2) reverse selection sync: every SelectedDn change drives renderer.SelectAsync,
    //          on its OWN channel, NEVER perturbing the FocusAsync count (ADR-020 #96) -----

    [Fact]
    public async Task JumpCommand_DispatchesSelectToItsOwnChannel_FocusStaysExactlyPlusOne()
    {
        var snapshot = LoadedGroupScope();
        var provider = StubProvider(snapshot);
        var fake = new FakeGraphRenderer();
        var vm = Workspace(provider, () => fake);
        await vm.Initialization;
        var focusBefore = fake.FocusCalls.Count;
        var selectBefore = fake.SelectCalls.Count;

        vm.JumpCommand.Execute(SalesDn);

        // The jump sets SelectedDn, which (ADR-020) fires renderer.SelectAsync on the SELECT
        // channel — distinct from the jump's own FocusAsync([dn]). The load-bearing pin
        // (JumpCommand_SetsSelectedDn_AndFocusesTheAnchor) requires FocusCalls to grow by
        // EXACTLY 1 per jump, so the select dispatch must NOT land on the focus channel.
        Assert.Equal(focusBefore + 1, fake.FocusCalls.Count);
        Assert.Equal(selectBefore + 1, fake.SelectCalls.Count);
        Assert.Equal(SalesDn, fake.SelectCalls[^1]);
        Assert.Equal(SalesDn, vm.SelectedDn);

        vm.Dispose();
    }

    [Fact]
    public async Task BareSelectedDnSet_DrivesSelect_WithFocusUntouched_NullDispatchesEmptyString()
    {
        var snapshot = LoadedGroupScope();
        var provider = StubProvider(snapshot);
        var fake = new FakeGraphRenderer();
        var vm = Workspace(provider, () => fake);
        await vm.Initialization;

        // A bare SelectedDn set (no jump) still projects onto the canvas: SelectAsync fires on
        // EVERY change, the null case as the empty string (clears the canvas JS-side). No jump
        // anywhere in this test => the FocusAsync channel must stay EMPTY throughout.
        vm.SelectedDn = SalesDn;
        vm.SelectedDn = AdaDn;
        vm.SelectedDn = null;
        vm.SelectedDn = ExternalMemberDn;

        Assert.Equal(new[] { SalesDn, AdaDn, string.Empty, ExternalMemberDn }, fake.SelectCalls);
        Assert.Empty(fake.FocusCalls);

        vm.Dispose();
    }

    [Fact]
    public async Task GraphTap_AlsoDispatchesSelect_WithoutPerturbingFocus()
    {
        var snapshot = LoadedGroupScope();
        var provider = StubProvider(snapshot);
        var fake = new FakeGraphRenderer();
        var vm = Workspace(provider, () => fake);
        await vm.Initialization;
        var focusBefore = fake.FocusCalls.Count;
        var selectBefore = fake.SelectCalls.Count;

        // A graph tap (NodeClicked) sets SelectedDn just like a jump or a sidebar row — so the
        // SAME reverse-sync select dispatch fires (every selection-change SOURCE syncs the
        // canvas). The tap drives no FocusAsync, so that channel is untouched.
        fake.RaiseNodeClicked(AdaDn, "User");

        Assert.Equal(AdaDn, vm.SelectedDn);
        Assert.Equal(selectBefore + 1, fake.SelectCalls.Count);
        Assert.Equal(AdaDn, fake.SelectCalls[^1]);
        Assert.Equal(focusBefore, fake.FocusCalls.Count);

        vm.Dispose();
    }

    // --- (d) selection sync: matching sidebar rows highlight ----------------------------

    [Fact(Timeout = 60_000)]
    public async Task SelectionSync_HighlightsEverySidebarRowAnchoredAtTheSelectedDn()
    {
        var (vm, _) = await DemoWorkspaceAsync();

        // dl-finance-extra is the anchor (PrimaryDn) of EXACTLY two findings — naming-dl
        // and empty-group. Selecting it highlights both rows; every other row stays cold.
        vm.SelectedDn = DlFinanceExtraDn;

        var active = vm.Violations.Where(r => r.IsActive).ToList();
        Assert.Equal(2, active.Count);
        Assert.All(active, r => Assert.Equal(DlFinanceExtraDn, r.PrimaryDn, Dn.Comparer));
        Assert.All(
            vm.Violations.Where(r => !Dn.Comparer.Equals(r.PrimaryDn, DlFinanceExtraDn)),
            r => Assert.False(r.IsActive, $"row '{r.PrimaryDn}' must not highlight for a different selection"));

        // Moving the selection to a clean parent anchor (DL<-User deny parent) lights
        // exactly its one nesting row and clears dl-finance-extra's two.
        vm.SelectedDn = DlFsSalesRwDn;
        var afterMove = vm.Violations.Where(r => r.IsActive).ToList();
        Assert.All(afterMove, r => Assert.Equal(DlFsSalesRwDn, r.PrimaryDn, Dn.Comparer));
        Assert.DoesNotContain(vm.Violations, r => r.IsActive && Dn.Comparer.Equals(r.PrimaryDn, DlFinanceExtraDn));

        // A member-endpoint DN that carries a finding but is NOT any row's anchor lights
        // nothing — highlight is by anchor (PrimaryDn), not by attached-DN.
        vm.SelectedDn = User001Dn;
        Assert.DoesNotContain(vm.Violations, r => r.IsActive);

        // Deselection clears all highlights.
        vm.SelectedDn = null;
        Assert.DoesNotContain(vm.Violations, r => r.IsActive);

        vm.Dispose();
    }

    // --- (e) all-clear: empty ruleset => no findings, "no violations" surfaced ----------

    [Fact(Timeout = 60_000)]
    public async Task AllClear_WithAnAllDisabledRuleset_HasNoViolations_ButStillFlagsUncheckedAreas()
    {
        // Same full demo scope, but every check disabled => zero findings (the all-clear
        // state) WHILE UncheckedDns is always computed (load-state truth, never gated on
        // a rule being enabled — ADR-009): the hint must still fire in the all-clear state.
        var (vm, _) = await DemoWorkspaceAsync(AllDisabledRuleset());

        Assert.Empty(vm.Report.Violations);
        Assert.Empty(vm.Violations);
        Assert.False(vm.HasViolations); // drives the "No findings." all-clear surface

        Assert.Equal(new[] { DomainAdminsDn, PrintOperatorsDn }, vm.Report.UncheckedDns);
        Assert.True(vm.HasUncheckedAreas, "clean != fully checked: the unchecked hint still shows");

        vm.Dispose();
    }

    [Fact]
    public async Task AllClear_WithACleanSnapshot_HasNoViolations_AndNoUncheckedAreas()
    {
        // A genuinely clean, fully loaded scope under the default ruleset: no findings,
        // and nothing unchecked (the empty all-clear: both surfaces off).
        var snapshot = CleanScope();
        var provider = StubProvider(snapshot);
        var fake = new FakeGraphRenderer();
        var vm = Workspace(provider, () => fake);
        await vm.Initialization;

        Assert.Empty(vm.Report.Violations);
        Assert.Empty(vm.Violations);
        Assert.False(vm.HasViolations);
        Assert.Empty(vm.Report.UncheckedDns);
        Assert.False(vm.HasUncheckedAreas);

        vm.Dispose();
    }

    // --- (f) null ruleset == default: the pre-AP-3.4 tests' contract is preserved -------

    [Fact(Timeout = 60_000)]
    public async Task NullRuleset_EqualsTheEmbeddedDefault_SameBaselineAsAnExplicitDefault()
    {
        var (nullRuleset, _) = await DemoWorkspaceAsync(ruleset: null);
        var (explicitDefault, _) = await DemoWorkspaceAsync(
            new EffectiveRuleset(RulesetLoader.LoadDefault(), FromUserFile: false, []));

        // The defaulted ctor param (null) MUST resolve to the embedded default — the
        // guarantee WorkspaceLoadTests/ExpandTests/DetailTests rely on (they construct
        // the VM with no ruleset). Both runs yield the identical 19-finding baseline.
        Assert.Equal(
            explicitDefault.Report.Violations.Select(v => (v.RuleId, v.Severity, string.Join("", v.Dns))).ToArray(),
            nullRuleset.Report.Violations.Select(v => (v.RuleId, v.Severity, string.Join("", v.Dns))).ToArray());
        Assert.Equal(19, nullRuleset.Report.Violations.Count);

        nullRuleset.Dispose();
        explicitDefault.Dispose();
    }

    // --- (g) Reload-scope re-Evaluates: fresh report, fresh frontier (issue #30 S1) -----

    [Fact(Timeout = 60_000)]
    public async Task ReloadScope_ReYieldsTheNineteenFindingBaseline_ThroughAFreshShow_NotAnUpdate()
    {
        var (vm, fake) = await DemoWorkspaceAsync(); // null ruleset => embedded default

        // The ctor load already produced the baseline and pushed it via ShowGraphAsync.
        Assert.Equal(19, vm.Report.Violations.Count);
        Assert.Single(fake.ShownReports);
        Assert.Empty(fake.UpdatedReports);

        var reportBefore = vm.Report;

        // A whole-scope reload re-Evaluates the freshly loaded scope against the LIVE
        // ruleset (re-read at Evaluate time, like LoadAsync): the 19-finding baseline
        // recomputes verbatim, in canonical report order (ADR-009).
        await vm.ReloadScopeCommand.ExecuteAsync(null);

        Assert.NotSame(reportBefore, vm.Report); // a fresh evaluation, not the stale instance
        Assert.Equal(
            ExpectedBaseline,
            vm.Report.Violations.Select(v => (v.RuleId, v.Severity, string.Join("", v.Dns))).ToArray());
        Assert.Equal(19, vm.Violations.Count); // OnReportChanged re-projected the sidebar
        Assert.True(vm.HasViolations);

        // KEYSTONE on the report channel: reload is replace-all — the fresh report rode a
        // SECOND ShowGraphAsync, NEVER an UpdateGraphAsync (the in-place verb).
        Assert.Equal(2, fake.ShownReports.Count);
        Assert.Same(vm.Report, fake.ShownReports[^1]);
        Assert.Empty(fake.UpdatedReports);

        vm.Dispose();
    }

    [Fact(Timeout = 60_000)]
    public async Task ReloadScope_RecomputesUncheckedAreas_AgainstTheFreshFrontier()
    {
        var (vm, _) = await DemoWorkspaceAsync();

        // The full demo scope surfaces exactly the two raw builtin member DNs as unchecked
        // (load-state truth — ADR-009).
        Assert.Equal(new[] { DomainAdminsDn, PrintOperatorsDn }, vm.Report.UncheckedDns);
        Assert.True(vm.HasUncheckedAreas);

        // A reload rebuilds the snapshot from scratch: the frontier resets to exactly what
        // the fresh whole-scope load found — the same two builtin DNs, recomputed truthfully
        // (never carried over from the old snapshot).
        await vm.ReloadScopeCommand.ExecuteAsync(null);

        Assert.Equal(new[] { DomainAdminsDn, PrintOperatorsDn }, vm.Report.UncheckedDns);
        Assert.True(vm.HasUncheckedAreas);

        vm.Dispose();
    }

    [Fact(Timeout = 60_000)]
    public async Task ReloadScope_ClearsAStaleSelectionHighlight_AndDropsTheDetailPanel()
    {
        var (vm, _) = await DemoWorkspaceAsync();

        // Select a finding anchor: its two sidebar rows highlight and the detail panel projects it.
        vm.SelectedDn = DlFinanceExtraDn;
        Assert.Equal(2, vm.Violations.Count(r => r.IsActive));
        Assert.NotNull(vm.DetailPanel);

        await vm.ReloadScopeCommand.ExecuteAsync(null);

        // Reload clears the selection up front: no row stays lit, the panel drops to null,
        // and OnReportChanged re-projected fresh rows over which the (null) selection
        // highlights nothing.
        Assert.Null(vm.SelectedDn);
        Assert.Null(vm.DetailPanel);
        Assert.DoesNotContain(vm.Violations, r => r.IsActive);

        vm.Dispose();
    }

    // --- stub-fixture DNs ---------------------------------------------------------------

    private const string StubRootDn = "OU=Lab,DC=stub,DC=lab";
    private const string SalesDn = "CN=GG_Sales,OU=Lab,DC=stub,DC=lab";
    private const string AdaDn = "CN=Ada Lovelace,OU=Lab,DC=stub,DC=lab";
    private const string ExternalMemberDn = "CN=Ext,DC=elsewhere,DC=lab";

    // --- helpers ------------------------------------------------------------------------

    private static AdObject Obj(
        string name, string dn, AdObjectKind kind = AdObjectKind.GlobalGroup) =>
        new() { Dn = dn, Kind = kind, Name = name, SamAccountName = name };

    /// <summary>A workspace over the REAL <see cref="DemoProvider"/> rooted at the demo
    /// OU (the full 19-finding scope), Initialization awaited. A <c>null</c> ruleset
    /// resolves to the embedded default.</summary>
    private static async Task<(WorkspaceViewModel Vm, FakeGraphRenderer Fake)> DemoWorkspaceAsync(
        EffectiveRuleset? ruleset = null)
    {
        var provider = new DemoProvider();
        var root = await provider.GetObjectAsync(DemoRootDn);
        Assert.NotNull(root);
        var fake = new FakeGraphRenderer();
        var vm = new WorkspaceViewModel(
            provider, root, await provider.ConnectAsync(),
            webView2Missing: false, () => fake, ruleset);
        await vm.Initialization;
        return (vm, fake);
    }

    /// <summary>An all-checks-disabled ruleset (the all-clear lever): the embedded
    /// default with Nesting/Circular/EmptyGroup off and Naming cleared. UncheckedDns is
    /// unaffected (it is never gated on a rule — ADR-009).</summary>
    private static EffectiveRuleset AllDisabledRuleset()
    {
        var d = RulesetLoader.LoadDefault();
        var disabled = d with
        {
            Nesting = d.Nesting with { Enabled = false },
            Naming = [],
            Circular = d.Circular with { Enabled = false },
            EmptyGroup = d.EmptyGroup with { Enabled = false },
        };
        return new EffectiveRuleset(disabled, FromUserFile: false, []);
    }

    /// <summary>A fetch-path stub scope: root OU + GG_Sales present in Objects but NOT
    /// members-loaded — so under the default ruleset GG_Sales is unchecked AND (once a
    /// loaded-empty state is reached) an empty-group subject. Built loaded-and-empty so
    /// the baseline already carries its empty-group finding, which the fetch then clears.</summary>
    private static DirectorySnapshot GroupScope(string groupDn)
    {
        var snapshot = new DirectorySnapshot();
        snapshot.AddObject(Obj("Lab", StubRootDn, AdObjectKind.OrganizationalUnit));
        snapshot.AddObject(Obj("GG_Sales", groupDn));
        snapshot.SetMembers(groupDn, []); // loaded-and-empty => an empty-group finding exists
        return snapshot;
    }

    /// <summary>A cache-hit stub scope: GG_Sales LOADED with a real member (non-empty, so
    /// no empty-group finding), plus a raw External member endpoint for the jump pin.</summary>
    private static DirectorySnapshot LoadedGroupScope()
    {
        var snapshot = new DirectorySnapshot();
        snapshot.AddObject(Obj("Lab", StubRootDn, AdObjectKind.OrganizationalUnit));
        snapshot.AddObject(Obj("GG_Sales", SalesDn));
        snapshot.AddObject(Obj("Ada Lovelace", AdaDn, AdObjectKind.User));
        snapshot.SetMembers(SalesDn, [AdaDn, ExternalMemberDn]); // loaded, non-empty
        return snapshot;
    }

    /// <summary>A genuinely clean, fully loaded scope: a well-named, non-empty,
    /// non-nested, non-cyclic GG with a user member — zero findings, nothing unchecked.</summary>
    private static DirectorySnapshot CleanScope()
    {
        var snapshot = new DirectorySnapshot();
        snapshot.AddObject(Obj("Lab", StubRootDn, AdObjectKind.OrganizationalUnit));
        snapshot.AddObject(Obj("GG_Sales_Staff", "CN=GG_Sales_Staff,OU=Lab,DC=stub,DC=lab"));
        snapshot.AddObject(Obj("Ada Lovelace", AdaDn, AdObjectKind.User));
        snapshot.SetMembers("CN=GG_Sales_Staff,OU=Lab,DC=stub,DC=lab", [AdaDn]);
        return snapshot;
    }

    /// <summary>Stub provider whose scope load yields <paramref name="snapshot"/>.</summary>
    private static StubDirectoryProvider StubProvider(DirectorySnapshot snapshot) =>
        new(Task.FromResult(new DirectoryConnection("stub directory", 5)))
        {
            LoadScopeResult = Task.FromResult(snapshot),
        };

    /// <summary>Workspace VM over a stub provider rooted at <see cref="StubRootDn"/>,
    /// null ruleset (=> embedded default).</summary>
    private static WorkspaceViewModel Workspace(
        StubDirectoryProvider provider, Func<IGraphRenderer> rendererFactory) =>
        new(
            provider,
            Obj("Lab", StubRootDn, AdObjectKind.OrganizationalUnit),
            new DirectoryConnection("stub directory", 5),
            webView2Missing: false,
            rendererFactory);

    /// <summary>The row's source rule id — the sidebar row does not carry RuleId, so the
    /// projection-order pin matches rows to report findings positionally; this reads the
    /// report's id at the same index for the tuple comparison.</summary>
    private static string RuleIdOf(WorkspaceViewModel vm, ViolationRowModel row)
    {
        var index = vm.Violations.IndexOf(row);
        return vm.Report.Violations[index].RuleId;
    }
}
