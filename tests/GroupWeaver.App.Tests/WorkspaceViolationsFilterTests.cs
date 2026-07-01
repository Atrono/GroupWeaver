using System;
using System.Linq;
using System.Threading.Tasks;

using GroupWeaver.App.Graph;
using GroupWeaver.App.Rules;
using GroupWeaver.App.Settings;
using GroupWeaver.App.Tests.Fakes;
using GroupWeaver.App.ViewModels;
using GroupWeaver.Core.Model;
using GroupWeaver.Core.Providers;
using GroupWeaver.Core.Rules;
using GroupWeaver.Providers;

using Xunit;

namespace GroupWeaver.App.Tests;

/// <summary>
/// Pins the #197 INTERACTIVE severity-chip filter on the <see cref="WorkspaceViewModel"/> violations
/// sidebar: <see cref="WorkspaceViewModel.Violations"/> is now the FILTERED view over the private
/// unfiltered backing list (<c>_allViolations</c>), and the severity axis is a fail-open,
/// multi-select (OR-within-axis) constraint driven by
/// <see cref="WorkspaceViewModel.ToggleSeverityFilterCommand"/> over the fixed Error/Warning/Info
/// <see cref="WorkspaceViewModel.SeverityChips"/> (each an <see cref="AuditFilterChip"/> reused from
/// the Audit screen, axis <see cref="AuditFilterAxis.Severity"/>). The chip
/// <see cref="AuditFilterChip.Count"/>s and <see cref="WorkspaceViewModel.TotalViolationCount"/> are
/// over the UNFILTERED backing list, so filtering never shrinks either — only the visible rows.
///
/// <para>Sister to <see cref="WorkspaceViolationsTests"/> (the report → sidebar projection, jump,
/// selection-sync) and to <see cref="AuditFilterTests"/> (the Audit screen's richer multi-axis
/// filter). This fixture covers ONLY the new #197 workspace-sidebar severity filter surface: the
/// chip collection + counts, the toggle command's single/multi-select/clear behavior, the unfiltered
/// header total, and the all-clear (no-chips) state.</para>
///
/// <para>The 19-finding baseline runs over the REAL <see cref="DemoProvider"/> rooted at the demo OU
/// — the AP 3.2 authority (4 Error · 3 Warning · 12 Info), incl. the GG_Circle_A ↔ GG_Circle_B cycle
/// every load must terminate over. Compares PROJECTIONS (the <c>(Severity)</c> / <c>PrimaryDn</c>
/// sequences of the visible rows, and the chips' <c>(Severity, Count, IsActive)</c> projections),
/// never whole-record/collection identity — the rule-engine.md / data-model.md determinism
/// discipline.</para>
///
/// <para><b>Test-isolation seam (#124 / lab-environment.md):</b> every VM injects a temp-dir
/// <see cref="UiStateStore"/> so a persisted <c>RailCollapsed:true</c> can't zero the rail while the
/// filter facts (VM-only, no visual tree) are read — even though these are plain-fact pins, the seam
/// keeps them off the real <c>%APPDATA%</c>.</para>
/// </summary>
public sealed class WorkspaceViolationsFilterTests
{
    private const string DemoRootDn = "OU=AGDLP-Demo,DC=weavedemo,DC=example";

    // The AP 3.2 demo baseline per-severity mix (parity with WorkspaceViolationsTests / rule-engine.md).
    private const int ErrorCount = 4;   // 3 nesting + 1 circular
    private const int WarningCount = 3; // naming
    private const int InfoCount = 12;   // empty-group
    private const int TotalCount = ErrorCount + WarningCount + InfoCount; // 19

    // --- (1) Default (no filter): full pass-through, chips built, counts over the backing list ------

    /// <summary>
    /// With nothing toggled, <see cref="WorkspaceViewModel.Violations"/> is the full backing list
    /// (== the report's findings, in canonical report order), <see cref="WorkspaceViewModel.TotalViolationCount"/>
    /// equals the total, the three fixed severity chips are built in descending order with their
    /// per-severity counts over the UNFILTERED list, and no chip is active.
    /// </summary>
    [Fact(Timeout = 60_000)]
    public async Task Default_NoFilter_ShowsAllRows_BuildsChips_CountsOverBackingList()
    {
        var (vm, _) = await DemoWorkspaceAsync();

        // Full pass-through: every report finding is visible, in canonical report order.
        Assert.Equal(TotalCount, vm.Violations.Count);
        Assert.Equal(
            vm.Report.Violations.Select(v => (v.Severity, v.PrimaryDn)).ToArray(),
            vm.Violations.Select(r => (r.Severity, r.PrimaryDn)).ToArray());

        // The header total is the UNFILTERED count (never shrinks under a filter — pinned below).
        Assert.Equal(TotalCount, vm.TotalViolationCount);
        Assert.True(vm.HasViolations);

        // Fixed severity chip domain, descending, axis = Severity, none active by default.
        Assert.Equal(
            new[] { RuleSeverity.Error, RuleSeverity.Warning, RuleSeverity.Info },
            vm.SeverityChips.Select(c => c.Severity!.Value).ToArray());
        Assert.All(vm.SeverityChips, c => Assert.Equal(AuditFilterAxis.Severity, c.Axis));
        Assert.All(vm.SeverityChips, c => Assert.False(c.IsActive));

        // Chip counts are over the UNFILTERED backing list: each equals a fresh tally over the
        // visible (== full, unfiltered here) rows, they sum to the total, and they match the
        // documented AP 3.2 baseline mix.
        foreach (var chip in vm.SeverityChips)
        {
            Assert.Equal(vm.Violations.Count(r => r.Severity == chip.Severity), chip.Count);
        }

        Assert.Equal(TotalCount, vm.SeverityChips.Sum(c => c.Count));
        Assert.Equal(ErrorCount, ChipFor(vm, RuleSeverity.Error).Count);
        Assert.Equal(WarningCount, ChipFor(vm, RuleSeverity.Warning).Count);
        Assert.Equal(InfoCount, ChipFor(vm, RuleSeverity.Info).Count);

        vm.Dispose();
    }

    // --- (2) Single severity: Error-only; total + chip count UNCHANGED; chip active ----------------

    /// <summary>
    /// Toggling the Error chip filters <see cref="WorkspaceViewModel.Violations"/> to Error-only, sets
    /// the chip <see cref="AuditFilterChip.IsActive"/>, and leaves BOTH the header
    /// <see cref="WorkspaceViewModel.TotalViolationCount"/> AND the chip's own
    /// <see cref="AuditFilterChip.Count"/> unchanged (counts are over the unfiltered backing list — a
    /// filter narrows the list, never the counts).
    /// </summary>
    [Fact(Timeout = 60_000)]
    public async Task ToggleError_FiltersToErrorOnly_TotalAndChipCountUnchanged_ChipActive()
    {
        var (vm, _) = await DemoWorkspaceAsync();

        var errorChip = ChipFor(vm, RuleSeverity.Error);
        var errorCountBefore = errorChip.Count;

        vm.ToggleSeverityFilterCommand.Execute(errorChip);

        // The visible rows are exactly the Error rows (still HasViolations — filtering isn't all-clear).
        Assert.True(errorChip.IsActive);
        Assert.True(vm.HasViolations);
        Assert.Equal(ErrorCount, vm.Violations.Count);
        Assert.All(vm.Violations, r => Assert.Equal(RuleSeverity.Error, r.Severity));

        // The visible set is exactly the report's Error findings, in report order (projection).
        Assert.Equal(
            vm.Report.Violations.Where(v => v.Severity == RuleSeverity.Error).Select(v => v.PrimaryDn).ToArray(),
            vm.Violations.Select(r => r.PrimaryDn).ToArray());

        // The header total and the chip count are UNCHANGED — over the unfiltered backing list.
        Assert.Equal(TotalCount, vm.TotalViolationCount);
        Assert.Equal(errorCountBefore, errorChip.Count);
        Assert.Equal(ErrorCount, errorChip.Count);

        vm.Dispose();
    }

    // --- (3) Multi-select within the severity axis is OR ------------------------------------------

    /// <summary>
    /// Toggling Error THEN Warning UNIONs within the severity axis (OR): the visible rows are exactly
    /// the Error ∪ Warning findings; both chips read active; the total stays the full unfiltered count.
    /// </summary>
    [Fact(Timeout = 60_000)]
    public async Task ToggleErrorThenWarning_ShowsUnion_OrWithinAxis()
    {
        var (vm, _) = await DemoWorkspaceAsync();

        var errorChip = ChipFor(vm, RuleSeverity.Error);
        var warningChip = ChipFor(vm, RuleSeverity.Warning);

        vm.ToggleSeverityFilterCommand.Execute(errorChip);
        vm.ToggleSeverityFilterCommand.Execute(warningChip);

        Assert.True(errorChip.IsActive);
        Assert.True(warningChip.IsActive);

        // Error OR Warning: the visible count is the sum, and every visible row is one of the two.
        Assert.Equal(ErrorCount + WarningCount, vm.Violations.Count);
        Assert.All(
            vm.Violations,
            r => Assert.True(r.Severity is RuleSeverity.Error or RuleSeverity.Warning));

        // The exact Error∪Warning report slice, in report order (a union, not the whole list).
        Assert.Equal(
            vm.Report.Violations
                .Where(v => v.Severity is RuleSeverity.Error or RuleSeverity.Warning)
                .Select(v => v.PrimaryDn).ToArray(),
            vm.Violations.Select(r => r.PrimaryDn).ToArray());

        // No Info row leaked into the union; the total is still the full unfiltered count.
        Assert.DoesNotContain(vm.Violations, r => r.Severity == RuleSeverity.Info);
        Assert.Equal(TotalCount, vm.TotalViolationCount);

        vm.Dispose();
    }

    // --- (4) Re-toggling restores the full list; IsActive false -----------------------------------

    /// <summary>
    /// Re-toggling a chip clears its constraint: after Error-only, toggling Error again restores the
    /// full list (fail-open — an empty active set imposes no constraint) with the chip inactive. A
    /// partial clear (Error+Warning → Warning off) leaves only Error, proving remove is per-chip.
    /// </summary>
    [Fact(Timeout = 60_000)]
    public async Task ReToggle_RestoresFullList_ChipInactive_PartialClearLeavesTheOther()
    {
        var (vm, _) = await DemoWorkspaceAsync();

        var errorChip = ChipFor(vm, RuleSeverity.Error);
        var warningChip = ChipFor(vm, RuleSeverity.Warning);

        // Error-only, then toggle Error back off => the full list is restored (fail-open).
        vm.ToggleSeverityFilterCommand.Execute(errorChip);
        Assert.Equal(ErrorCount, vm.Violations.Count);
        vm.ToggleSeverityFilterCommand.Execute(errorChip);
        Assert.False(errorChip.IsActive);
        Assert.Equal(TotalCount, vm.Violations.Count);
        Assert.Equal(
            vm.Report.Violations.Select(v => v.PrimaryDn).ToArray(),
            vm.Violations.Select(r => r.PrimaryDn).ToArray());

        // Error + Warning active, then drop Warning => only Error remains (per-chip removal, not a
        // wholesale clear).
        vm.ToggleSeverityFilterCommand.Execute(errorChip);
        vm.ToggleSeverityFilterCommand.Execute(warningChip);
        Assert.Equal(ErrorCount + WarningCount, vm.Violations.Count);
        vm.ToggleSeverityFilterCommand.Execute(warningChip);
        Assert.True(errorChip.IsActive);
        Assert.False(warningChip.IsActive);
        Assert.Equal(ErrorCount, vm.Violations.Count);
        Assert.All(vm.Violations, r => Assert.Equal(RuleSeverity.Error, r.Severity));

        vm.Dispose();
    }

    // --- (5) All-clear / empty report: no chips, HasViolations false, no crash ---------------------

    /// <summary>
    /// An all-clear report (every check disabled ⇒ zero findings) drives
    /// <see cref="WorkspaceViewModel.HasViolations"/> false, <see cref="WorkspaceViewModel.TotalViolationCount"/>
    /// zero and <see cref="WorkspaceViewModel.Violations"/> empty — the view HIDES the whole chip strip
    /// via its <c>IsVisible="{Binding HasViolations}"</c> gate (the correct all-clear surface: nothing to
    /// filter). The fixed Error/Warning/Info <see cref="WorkspaceViewModel.SeverityChips"/> are still
    /// rebuilt (the domain is fixed, never data-driven), but each carries <c>Count == 0</c> and is
    /// inactive — so a toggle over a zero-count chip is a harmless no-op, no crash.
    /// </summary>
    [Fact(Timeout = 60_000)]
    public async Task AllClear_EmptyReport_StripHidden_ZeroCountChips_NoViolations_NoCrash()
    {
        // Same full demo scope, but every check disabled => zero findings.
        var (vm, _) = await DemoWorkspaceAsync(AllDisabledRuleset());

        Assert.Empty(vm.Report.Violations);
        Assert.Empty(vm.Violations);
        Assert.False(vm.HasViolations); // the view binds the whole chip strip's IsVisible to this
        Assert.Equal(0, vm.TotalViolationCount);

        // The fixed severity domain is still rebuilt, but every chip is zero-count and inactive — the
        // strip is hidden by HasViolations, so these never render, and no count/filter can be positive.
        Assert.Equal(
            new[] { RuleSeverity.Error, RuleSeverity.Warning, RuleSeverity.Info },
            vm.SeverityChips.Select(c => c.Severity!.Value).ToArray());
        Assert.All(vm.SeverityChips, c => Assert.Equal(0, c.Count));
        Assert.All(vm.SeverityChips, c => Assert.False(c.IsActive));

        // Toggling a zero-count chip is a harmless no-op — it never crashes and never conjures rows.
        var ex = Record.Exception(() => vm.ToggleSeverityFilterCommand.Execute(ChipFor(vm, RuleSeverity.Error)));
        Assert.Null(ex);
        Assert.Empty(vm.Violations);

        vm.Dispose();
    }

    // --- (6) Filter is preserved across a re-Evaluate within one workspace (mirrors AuditViewModel) -

    /// <summary>
    /// An active severity filter survives a whole-scope reload (a re-Evaluate that rebuilds the chips):
    /// after Error-only, <see cref="WorkspaceViewModel.ReloadScopeCommand"/> re-yields the 19-finding
    /// baseline yet keeps the view constrained to Error rows, with the freshly-rebuilt Error chip still
    /// active — the <c>_severityFilter</c> set is preserved across rebuilds within one workspace
    /// (parity with the Audit screen's rebuild-preserves-severity contract). The chip is a NEW instance
    /// (the collection is rebuilt), so this asserts the projection, never chip identity.
    /// </summary>
    [Fact(Timeout = 60_000)]
    public async Task ActiveFilter_SurvivesReloadScope_RebuiltChipStillActive_ViewStaysConstrained()
    {
        var (vm, fake) = await DemoWorkspaceAsync();

        vm.ToggleSeverityFilterCommand.Execute(ChipFor(vm, RuleSeverity.Error));
        Assert.Equal(ErrorCount, vm.Violations.Count);

        // A whole-scope reload re-Evaluates and rebuilds the chips (OnReportChanged): the baseline
        // recomputes verbatim, but the preserved severity filter keeps the view Error-only.
        await vm.ReloadScopeCommand.ExecuteAsync(null);

        Assert.Equal(TotalCount, vm.Report.Violations.Count);            // full baseline re-evaluated
        Assert.Equal(TotalCount, vm.TotalViolationCount);               // total over the fresh backing list
        Assert.Equal(ErrorCount, vm.Violations.Count);                 // still Error-only
        Assert.All(vm.Violations, r => Assert.Equal(RuleSeverity.Error, r.Severity));

        var errorChipAfter = ChipFor(vm, RuleSeverity.Error);
        Assert.True(errorChipAfter.IsActive, "the severity filter must survive the re-Evaluate rebuild");

        // Sanity: the reload took the replace-all path (a fresh Show, not an in-place Update) — the
        // filter preservation rides OnReportChanged, not a mutation of the old collection.
        Assert.Equal(2, fake.ShownReports.Count);

        vm.Dispose();
    }

    // --- helpers ----------------------------------------------------------------------------------

    /// <summary>The severity chip for <paramref name="severity"/> — the fixed Error/Warning/Info
    /// domain, so exactly one matches.</summary>
    private static AuditFilterChip ChipFor(WorkspaceViewModel vm, RuleSeverity severity) =>
        vm.SeverityChips.Single(c => c.Severity == severity);

    /// <summary>A workspace over the REAL <see cref="DemoProvider"/> rooted at the demo OU (the full
    /// 19-finding scope), Initialization awaited. A <c>null</c> ruleset resolves to the embedded
    /// default. Injects a temp-dir <see cref="UiStateStore"/> (the #124 isolation seam).</summary>
    private static async Task<(WorkspaceViewModel Vm, FakeGraphRenderer Fake)> DemoWorkspaceAsync(
        EffectiveRuleset? ruleset = null)
    {
        var provider = new DemoProvider();
        var root = await provider.GetObjectAsync(DemoRootDn);
        Assert.NotNull(root);
        var fake = new FakeGraphRenderer();
        var vm = new WorkspaceViewModel(
            provider, root, await provider.ConnectAsync(),
            webView2Missing: false, () => fake, ruleset,
            uiStateStore: new UiStateStore(
                System.IO.Directory.CreateTempSubdirectory("groupweaver-viol-filter-uistate-").FullName));
        await vm.Initialization;
        return (vm, fake);
    }

    /// <summary>An all-checks-disabled ruleset (the all-clear lever): the embedded default with
    /// Nesting/Circular/EmptyGroup off and Naming cleared — zero findings, so the chip strip is empty.
    /// Mirrors <see cref="WorkspaceViolationsTests"/>' all-disabled fixture.</summary>
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
}
