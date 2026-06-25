using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

using Avalonia.Headless.XUnit;
using Avalonia.Threading;

using GroupWeaver.App.Rules;
using GroupWeaver.App.Settings;
using GroupWeaver.App.Startup;
using GroupWeaver.App.Tests.Fakes;
using GroupWeaver.App.ViewModels;
using GroupWeaver.App.Views;
using GroupWeaver.Core.Model;
using GroupWeaver.Core.Providers;
using GroupWeaver.Core.Rules;
using GroupWeaver.Providers;

using Xunit;

namespace GroupWeaver.App.Tests;

/// <summary>
/// Pins the WP1 audit-findings FILTER on the <see cref="AuditViewModel"/>: <see cref="AuditViewModel.Findings"/>
/// is now the filtered + sorted VISIBLE view over the private master list (<c>_allRows</c>), and three
/// independent fail-open axes — Severity (<see cref="AuditFindingRowModel.Severity"/>), Status
/// (<see cref="AuditFindingRowModel.Status"/>), and Rule class (<see cref="AuditFindingRowModel.RuleId"/>,
/// OrdinalIgnoreCase) — AND together (a row is visible iff it passes EVERY non-empty axis; within an
/// axis, set membership is OR'd). Chip <see cref="AuditFilterChip.Count"/>s are over the unfiltered
/// master list, so they never shrink as filters apply.
///
/// <para>Sister to <see cref="AuditTableTests"/> (the 1:1 row projection / sort / multi-select) and
/// <see cref="AuditTriageTests"/> (the triage write path + per-row status). This fixture covers the
/// NEW WP1 filter surface only: the chip collections, the <see cref="AuditViewModel.ToggleFilterCommand"/>
/// / <see cref="AuditViewModel.ClearFiltersCommand"/> behavior, the filter↔sort and filter↔selection
/// interactions, and the rebuild-preserves-filters / prunes-vanished-rule-class contract.</para>
///
/// <para>Most pins are UNIT-level over a directly-constructed <see cref="AuditViewModel"/> on the SAME
/// hand-built loaded scope the WP5b/WP5c/WP5d tests use (<see cref="LoadedScopeWithFindings"/>). Two
/// pins drive the REAL <see cref="DemoProvider"/> scope through the shell (the AP 3.2 19-finding
/// baseline) so the chip counts / canonical chip order are proven against the documented baseline end
/// to end; the status-filter pin reuses the <see cref="AuditTriageTests"/> shell-gate triage seam so a
/// genuinely-tagged ignore entry produces non-Open rows.</para>
///
/// <para><b>Test-isolation seam (#124 lesson):</b> every shell-driven case injects a temp-dir
/// <see cref="UiStateStore"/> (and, where it triages, a temp-dir <see cref="RulesetLocator"/>) so it
/// never reads/writes real <c>%APPDATA%</c>.</para>
///
/// <para>Compares PROJECTIONS, never record/collection identity (rule-engine.md / data-model.md):
/// rows are compared by their value projections (Severity / RuleId / Status / ReportOrder sequences),
/// chips by their (label, key, count, active) projections.</para>
/// </summary>
public sealed class AuditFilterTests
{
    private static readonly WebView2RuntimeStatus Present = new(IsInstalled: true, Version: "test");

    private const string RootDn = "OU=Lab,DC=stub,DC=lab";

    // The badly-named GG carries BOTH a naming Warning and an empty-group Info finding; its DN is the
    // triage subject the status-filter pin drives (parity with AuditTriageTests).
    private const string GgBadNameDn = "CN=NotAConventionName,OU=Lab,DC=stub,DC=lab";

    // === (1) Default (no filter): full pass-through, chips built, counts over the master list =======

    /// <summary>
    /// With no filter active, <see cref="AuditViewModel.Findings"/> is the full master list
    /// (<see cref="AuditViewModel.VisibleCount"/> == <see cref="AuditViewModel.TotalCount"/>),
    /// <see cref="AuditViewModel.IsFiltered"/>/<see cref="AuditViewModel.HasNoMatches"/> are false, and
    /// <see cref="AuditViewModel.FilterSummary"/> is the unfiltered "{N} findings" form. The chip
    /// collections are built: 3 fixed severity chips, 3 fixed status chips, and one rule-class chip per
    /// finding-bearing class in canonical <see cref="Ruleset.EnumerateRules"/> order. Every chip's
    /// <see cref="AuditFilterChip.Count"/> matches a fresh tally over the master list (counts are
    /// unfiltered) and the per-axis counts sum to <see cref="AuditViewModel.TotalCount"/>.
    /// </summary>
    [Fact]
    public void Default_NoFilter_PassesThroughAll_BuildsChips_CountsOverMasterList()
    {
        var (snapshot, ruleset) = LoadedScopeWithFindings();
        var report = RuleEngine.Evaluate(snapshot, ruleset);
        using var audit = new AuditViewModel(snapshot, report, ruleset, RootDn, onBack: () => { });

        var total = audit.Findings.Count;
        Assert.True(total > 0, "the fixture must produce findings (else this pin is vacuous)");

        // Default: full pass-through, not filtered, no "no matches".
        Assert.Equal(total, audit.TotalCount);
        Assert.Equal(total, audit.VisibleCount);
        Assert.Equal(audit.VisibleCount, audit.Findings.Count);
        Assert.False(audit.IsFiltered);
        Assert.False(audit.HasNoMatches);
        Assert.False(audit.IsAllClear);
        Assert.Equal($"{total} findings", audit.FilterSummary);

        // Fixed severity + status chip domains, in their canonical (descending severity / Open-first) order.
        Assert.All(audit.SeverityChips, c => Assert.NotNull(c.Severity)); // severity chips carry a non-null severity
        Assert.Equal(
            new[] { RuleSeverity.Error, RuleSeverity.Warning, RuleSeverity.Info },
            audit.SeverityChips.Select(c => c.Severity!.Value).ToArray());
        Assert.All(audit.SeverityChips, c => Assert.Equal(AuditFilterAxis.Severity, AxisOf(c)));
        Assert.Equal(
            new[] { "Open", "Acknowledged", "Suppressed" },
            audit.StatusChips.Select(c => c.Label).ToArray());

        // Rule-class chips: one per finding-bearing class, in canonical EnumerateRules order, labelled
        // by the rule's DisplayName. Compare to the SAME canonical order the engine yields (not hardcoded).
        var ruleIdsPresent = audit.Findings.Select(r => RuleIdOf(r)).Distinct(StringComparer.OrdinalIgnoreCase)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var expectedRuleClassLabels = ruleset.EnumerateRules()
            .Where(rule => ruleIdsPresent.Contains(rule.Id))
            .Select(rule => rule.DisplayName)
            .ToArray();
        Assert.Equal(expectedRuleClassLabels, audit.RuleClassChips.Select(c => c.Label).ToArray());
        Assert.True(audit.RuleClassChips.Count > 1, "the fixture must span >1 rule class for a meaningful axis");

        // No chip is active by default.
        Assert.All(audit.SeverityChips, c => Assert.False(c.IsActive));
        Assert.All(audit.StatusChips, c => Assert.False(c.IsActive));
        Assert.All(audit.RuleClassChips, c => Assert.False(c.IsActive));

        // Chip counts are over the UNFILTERED master list: each equals a fresh tally, and each axis's
        // counts sum to TotalCount.
        foreach (var chip in audit.SeverityChips)
        {
            Assert.Equal(audit.Findings.Count(r => r.Severity == chip.Severity), chip.Count);
        }

        Assert.Equal(total, audit.SeverityChips.Sum(c => c.Count));
        Assert.Equal(total, audit.StatusChips.Sum(c => c.Count));
        Assert.Equal(total, audit.RuleClassChips.Sum(c => c.Count)); // each finding has exactly one rule id

        // On the untriaged baseline every row is Open.
        Assert.Equal(total, audit.StatusChips.Single(c => c.Label == "Open").Count);
        Assert.Equal(0, audit.StatusChips.Single(c => c.Label == "Acknowledged").Count);
        Assert.Equal(0, audit.StatusChips.Single(c => c.Label == "Suppressed").Count);
    }

    /// <summary>
    /// Over the REAL demo root-OU scope (the AP 3.2 19-finding baseline incl. the GG_Circle cycle), the
    /// chip counts match the documented baseline exactly: 4 Error (3 nesting + 1 cycle), 3 Warning
    /// (naming), 12 Info (empty-group), all-Open (untriaged). The rule-class chips are emitted in
    /// canonical <see cref="Ruleset.EnumerateRules"/> order. Proven end to end so the unit-level fixture
    /// stays anchored to the rule-engine.md baseline.
    /// </summary>
    [AvaloniaFact(Timeout = 60_000)]
    public async Task ChipCounts_MatchTheDemoBaseline_AndCanonicalRuleClassOrder()
    {
        var (window, shell, workspace) = await DriveDemoWorkspaceAsync();
        var snapshot = workspace.Snapshot!;
        var ruleset = RulesetLoader.LoadDefault();
        using var audit = new AuditViewModel(snapshot, workspace.Report, ruleset, workspace.RootDn, onBack: () => { });

        // The AP 3.2 baseline: 19 findings = 4 Error (3 nesting + 1 cycle) / 3 Warning / 12 Info, all Open.
        Assert.Equal(19, audit.TotalCount);
        Assert.Equal(4, audit.SeverityChips.Single(c => c.Severity == RuleSeverity.Error).Count);
        Assert.Equal(3, audit.SeverityChips.Single(c => c.Severity == RuleSeverity.Warning).Count);
        Assert.Equal(12, audit.SeverityChips.Single(c => c.Severity == RuleSeverity.Info).Count);

        Assert.Equal(19, audit.StatusChips.Single(c => c.Label == "Open").Count);
        Assert.Equal(0, audit.StatusChips.Single(c => c.Label == "Acknowledged").Count);
        Assert.Equal(0, audit.StatusChips.Single(c => c.Label == "Suppressed").Count);

        // Rule-class chips emitted in canonical EnumerateRules order (one per finding-bearing class).
        var ruleIdsPresent = audit.Findings.Select(r => RuleIdOf(r)).Distinct(StringComparer.OrdinalIgnoreCase)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var expectedLabels = ruleset.EnumerateRules()
            .Where(rule => ruleIdsPresent.Contains(rule.Id))
            .Select(rule => rule.DisplayName)
            .ToArray();
        Assert.Equal(expectedLabels, audit.RuleClassChips.Select(c => c.Label).ToArray());

        shell.Dispose();
        window.Close();
    }

    // === (2) Severity filter: single → union (OR within axis) → toggle off restores =================

    /// <summary>
    /// Toggling the Error severity chip keeps only <c>Severity==Error</c> rows
    /// (<see cref="AuditViewModel.VisibleCount"/> == the Error count, <see cref="AuditViewModel.IsFiltered"/>
    /// true, the chip <see cref="AuditFilterChip.IsActive"/>). Toggling a SECOND severity chip UNIONs
    /// (OR within the axis). Toggling the first back OFF restores to just the second's set; toggling
    /// both off restores the full list.
    /// </summary>
    [Fact]
    public void SeverityFilter_SingleThenUnion_ThenToggleOffRestores()
    {
        var (snapshot, ruleset) = LoadedScopeWithFindings();
        var report = RuleEngine.Evaluate(snapshot, ruleset);
        using var audit = new AuditViewModel(snapshot, report, ruleset, RootDn, onBack: () => { });

        var total = audit.TotalCount;
        var errorChip = audit.SeverityChips.Single(c => c.Severity == RuleSeverity.Error);
        var warningChip = audit.SeverityChips.Single(c => c.Severity == RuleSeverity.Warning);
        var errorCount = errorChip.Count;
        var warningCount = warningChip.Count;
        Assert.True(errorCount > 0 && warningCount > 0, "the fixture must carry both Error and Warning rows");

        // Single axis: only Error rows.
        audit.ToggleFilterCommand.Execute(errorChip);
        Assert.True(errorChip.IsActive);
        Assert.True(audit.IsFiltered);
        Assert.Equal(errorCount, audit.VisibleCount);
        Assert.All(audit.Findings, r => Assert.Equal(RuleSeverity.Error, r.Severity));
        Assert.Equal($"Showing {errorCount} of {total}", audit.FilterSummary);
        Assert.False(audit.HasNoMatches);

        // Union within axis: Error OR Warning.
        audit.ToggleFilterCommand.Execute(warningChip);
        Assert.True(warningChip.IsActive);
        Assert.Equal(errorCount + warningCount, audit.VisibleCount);
        Assert.All(audit.Findings, r => Assert.True(r.Severity is RuleSeverity.Error or RuleSeverity.Warning));

        // Toggle Error off: only Warning remains.
        audit.ToggleFilterCommand.Execute(errorChip);
        Assert.False(errorChip.IsActive);
        Assert.Equal(warningCount, audit.VisibleCount);
        Assert.All(audit.Findings, r => Assert.Equal(RuleSeverity.Warning, r.Severity));

        // Toggle Warning off: fully restored.
        audit.ToggleFilterCommand.Execute(warningChip);
        Assert.False(warningChip.IsActive);
        Assert.False(audit.IsFiltered);
        Assert.Equal(total, audit.VisibleCount);
    }

    // === (3) Status filter over a triaged fixture (real shell triage seam) ==========================

    /// <summary>
    /// On a TRIAGED fixture (a finding Suppressed through the real shell triage gate, so its row reads
    /// <see cref="TriageStatus.Suppressed"/> in the would-be table while staying listed), toggling the
    /// Open status chip HIDES the Suppressed row; toggling the Suppressed chip instead shows ONLY the
    /// suppressed row(s). Reuses the AuditTriageTests shell-gate wiring so a genuinely-tagged ignore
    /// entry produces the non-Open status (not a hand-built row).
    /// </summary>
    [AvaloniaFact(Timeout = 60_000)]
    public async Task StatusFilter_OverATriagedFixture_OpenHidesTriaged_SuppressedShowsOnlyTriaged()
    {
        var (window, shell, _, audit) = await DriveToArmedAuditAsync();

        // Suppress the naming finding through the shell gate (the AuditTriageTests path).
        var namingRow = audit.Findings.First(r => IsNamingFindingFor(r, GgBadNameDn));
        namingRow.IsSelected = true;
        audit.SuppressSelectedCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();

        // Pre-condition: the would-be table now carries a Suppressed row and ≥1 Open row.
        var total = audit.TotalCount;
        var suppressed = audit.Findings.Where(r => r.Status == TriageStatus.Suppressed).ToList();
        var openRows = audit.Findings.Where(r => r.Status == TriageStatus.Open).ToList();
        Assert.NotEmpty(suppressed);
        Assert.NotEmpty(openRows);

        var openChip = audit.StatusChips.Single(c => c.Label == "Open");
        var suppressedChip = audit.StatusChips.Single(c => c.Label == "Suppressed");
        // Chip counts are over the master list (unfiltered) and reflect the post-triage statuses.
        Assert.Equal(openRows.Count, openChip.Count);
        Assert.Equal(suppressed.Count, suppressedChip.Count);
        Assert.Equal(total, openChip.Count + suppressedChip.Count); // no Acknowledged here

        // Filter to Open: the Suppressed row(s) are hidden.
        audit.ToggleFilterCommand.Execute(openChip);
        Assert.Equal(openRows.Count, audit.VisibleCount);
        Assert.All(audit.Findings, r => Assert.Equal(TriageStatus.Open, r.Status));
        Assert.DoesNotContain(audit.Findings, r => r.Status == TriageStatus.Suppressed);

        // Switch to Suppressed only: exactly the suppressed row(s).
        audit.ToggleFilterCommand.Execute(openChip);      // off
        audit.ToggleFilterCommand.Execute(suppressedChip); // on
        Assert.Equal(suppressed.Count, audit.VisibleCount);
        Assert.All(audit.Findings, r => Assert.Equal(TriageStatus.Suppressed, r.Status));

        shell.Dispose();
        window.Close();
    }

    // === (4) Rule-class filter: single → union ======================================================

    /// <summary>
    /// Toggling one rule-class chip keeps only that <see cref="AuditFindingRowModel.RuleId"/>; toggling
    /// a second rule-class chip UNIONs (OR within the axis). Compared OrdinalIgnoreCase, since the
    /// rule-class axis is keyed by rule id case-insensitively.
    /// </summary>
    [Fact]
    public void RuleClassFilter_SingleThenUnion()
    {
        var (snapshot, ruleset) = LoadedScopeWithFindings();
        var report = RuleEngine.Evaluate(snapshot, ruleset);
        using var audit = new AuditViewModel(snapshot, report, ruleset, RootDn, onBack: () => { });

        Assert.True(audit.RuleClassChips.Count >= 2, "need ≥2 rule classes for a union pin");
        var chipA = audit.RuleClassChips[0];
        var chipB = audit.RuleClassChips[1];
        var keyA = (string)KeyOf(chipA);
        var keyB = (string)KeyOf(chipB);

        // Single rule class.
        audit.ToggleFilterCommand.Execute(chipA);
        Assert.True(chipA.IsActive);
        Assert.Equal(chipA.Count, audit.VisibleCount);
        Assert.All(audit.Findings, r => Assert.Equal(keyA, RuleIdOf(r), StringComparer.OrdinalIgnoreCase));

        // Union: rule class A OR B.
        audit.ToggleFilterCommand.Execute(chipB);
        Assert.True(chipB.IsActive);
        Assert.Equal(chipA.Count + chipB.Count, audit.VisibleCount);
        Assert.All(
            audit.Findings,
            r => Assert.True(
                string.Equals(RuleIdOf(r), keyA, StringComparison.OrdinalIgnoreCase)
                || string.Equals(RuleIdOf(r), keyB, StringComparison.OrdinalIgnoreCase)));
    }

    // === (5) Axes AND together: a contradictory pair → HasNoMatches, not all-clear ==================

    /// <summary>
    /// The three axes AND together: combining the Error severity chip with a rule-class chip whose class
    /// has NO Error finding yields an empty visible set — <see cref="AuditViewModel.HasNoMatches"/> true
    /// (findings exist but all hidden), <see cref="AuditViewModel.IsAllClear"/> FALSE (the scope is not
    /// clean), and <see cref="AuditViewModel.FilterSummary"/> shows "Showing 0 of {N}".
    /// </summary>
    [Fact]
    public void AxesAndTogether_ContradictoryPair_HasNoMatchesNotAllClear()
    {
        var (snapshot, ruleset) = LoadedScopeWithFindings();
        var report = RuleEngine.Evaluate(snapshot, ruleset);
        using var audit = new AuditViewModel(snapshot, report, ruleset, RootDn, onBack: () => { });

        var total = audit.TotalCount;
        var errorChip = audit.SeverityChips.Single(c => c.Severity == RuleSeverity.Error);
        Assert.True(errorChip.Count > 0, "the fixture must carry Error findings");

        // A rule-class chip whose rule id has NO Error row (empty-group is Info; naming is Warning).
        var nonErrorRuleIds = audit.Findings
            .Where(r => r.Severity != RuleSeverity.Error)
            .Select(r => RuleIdOf(r))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var errorRuleIds = audit.Findings
            .Where(r => r.Severity == RuleSeverity.Error)
            .Select(r => RuleIdOf(r))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var nonErrorOnlyChip = audit.RuleClassChips.First(
            c => nonErrorRuleIds.Contains((string)KeyOf(c)) && !errorRuleIds.Contains((string)KeyOf(c)));

        audit.ToggleFilterCommand.Execute(errorChip);
        audit.ToggleFilterCommand.Execute(nonErrorOnlyChip);

        // Empty intersection: no matches, but the scope is NOT all-clear.
        Assert.Empty(audit.Findings);
        Assert.Equal(0, audit.VisibleCount);
        Assert.True(audit.IsFiltered);
        Assert.True(audit.HasNoMatches);
        Assert.False(audit.IsAllClear);
        Assert.False(audit.HasFindings);
        Assert.Equal($"Showing 0 of {total}", audit.FilterSummary);
    }

    // === (6) ClearFilters: every chip off, full list restored =======================================

    /// <summary>
    /// After several toggles across all three axes, <see cref="AuditViewModel.ClearFiltersCommand"/>
    /// deactivates EVERY chip, restores <see cref="AuditViewModel.Findings"/> to the full master list,
    /// and clears <see cref="AuditViewModel.IsFiltered"/>.
    /// </summary>
    [Fact]
    public void ClearFilters_DeactivatesEveryChip_RestoresFullList()
    {
        var (snapshot, ruleset) = LoadedScopeWithFindings();
        var report = RuleEngine.Evaluate(snapshot, ruleset);
        using var audit = new AuditViewModel(snapshot, report, ruleset, RootDn, onBack: () => { });

        var total = audit.TotalCount;

        // Toggle one chip on each axis (status uses the Open chip — every untriaged row is Open).
        audit.ToggleFilterCommand.Execute(audit.SeverityChips.Single(c => c.Severity == RuleSeverity.Error));
        audit.ToggleFilterCommand.Execute(audit.StatusChips.Single(c => c.Label == "Open"));
        audit.ToggleFilterCommand.Execute(audit.RuleClassChips[0]);
        Assert.True(audit.IsFiltered);

        audit.ClearFiltersCommand.Execute(null);

        Assert.False(audit.IsFiltered);
        Assert.Equal(total, audit.Findings.Count);
        Assert.Equal(total, audit.VisibleCount);
        Assert.False(audit.HasNoMatches);
        Assert.Equal($"{total} findings", audit.FilterSummary);
        Assert.All(audit.SeverityChips, c => Assert.False(c.IsActive));
        Assert.All(audit.StatusChips, c => Assert.False(c.IsActive));
        Assert.All(audit.RuleClassChips, c => Assert.False(c.IsActive));
    }

    // === (7) Filter + sort: membership unchanged, only re-ordered, ReportOrder tie-break stable ======

    /// <summary>
    /// Applying a filter then sorting (<see cref="AuditViewModel.SortCommand"/> on Severity) leaves the
    /// VISIBLE SET unchanged in MEMBERSHIP (same rows, by their ReportOrder identity) — only the order
    /// changes — and the report-order tie-break holds within each severity block. The sort never
    /// re-admits a filtered-out row.
    /// </summary>
    [Fact]
    public void FilterThenSort_MembershipUnchanged_OnlyReordered_ReportOrderTieBreak()
    {
        var (snapshot, ruleset) = LoadedScopeWithFindings();
        var report = RuleEngine.Evaluate(snapshot, ruleset);
        using var audit = new AuditViewModel(snapshot, report, ruleset, RootDn, onBack: () => { });

        // Filter to two severities so the post-filter set spans >1 severity (a meaningful sort).
        audit.ToggleFilterCommand.Execute(audit.SeverityChips.Single(c => c.Severity == RuleSeverity.Warning));
        audit.ToggleFilterCommand.Execute(audit.SeverityChips.Single(c => c.Severity == RuleSeverity.Info));

        var membershipBefore = audit.Findings.Select(r => r.ReportOrder).OrderBy(x => x).ToArray();
        Assert.True(audit.Findings.Select(r => r.Severity).Distinct().Count() > 1, "need a multi-severity visible set");

        // Sort by severity (descending default): same members, re-ordered.
        audit.SortCommand.Execute(AuditSortColumn.Severity);
        Assert.Equal(AuditSortColumn.Severity, audit.SortColumn);

        var membershipAfter = audit.Findings.Select(r => r.ReportOrder).OrderBy(x => x).ToArray();
        Assert.Equal(membershipBefore, membershipAfter); // identical membership

        // The visible rows are exactly the post-filter severity set (no filtered-out severity re-admitted).
        Assert.All(audit.Findings, r => Assert.True(r.Severity is RuleSeverity.Warning or RuleSeverity.Info));

        // Order: severity-descending with the report-order index as the tie-break (stable).
        Assert.Equal(
            audit.Findings.Select(r => r.ReportOrder).ToArray(),
            audit.Findings
                .OrderByDescending(r => (int)r.Severity).ThenBy(r => r.ReportOrder)
                .Select(r => r.ReportOrder).ToArray());
    }

    // === (8) Selection vs filter: hidden rows are deselected, never act on a hidden row ==============

    /// <summary>
    /// Selecting some rows then applying a filter that hides some of them DESELECTS the hidden rows
    /// (<see cref="AuditFindingRowModel.IsSelected"/> false) and <see cref="AuditViewModel.SelectedCount"/>
    /// reflects only the visible-selected rows — the "never act on a hidden row" contract. A row that
    /// remains visible keeps its selection.
    /// </summary>
    [Fact]
    public void SelectionVsFilter_HiddenSelectedRowsAreDeselected_CountReflectsVisibleOnly()
    {
        var (snapshot, ruleset) = LoadedScopeWithFindings();
        var report = RuleEngine.Evaluate(snapshot, ruleset);
        using var audit = new AuditViewModel(snapshot, report, ruleset, RootDn, onBack: () => { });

        // Select EVERY row, then filter to Errors only (so non-Error selected rows must be dropped).
        audit.ToggleSelectAllCommand.Execute(null);
        var total = audit.TotalCount;
        Assert.Equal(total, audit.SelectedCount);

        var errorChip = audit.SeverityChips.Single(c => c.Severity == RuleSeverity.Error);
        var errorCount = errorChip.Count;
        Assert.True(errorCount > 0 && errorCount < total, "need a strict subset for a meaningful hide");

        audit.ToggleFilterCommand.Execute(errorChip);

        // Visible-selected only: the count drops to the (still-selected) visible Error rows.
        Assert.Equal(errorCount, audit.VisibleCount);
        Assert.Equal(errorCount, audit.SelectedCount);
        Assert.All(audit.Findings, r => Assert.True(r.IsSelected)); // the surviving visible rows stayed selected

        // The hidden rows were DESELECTED (never act on a hidden row): no master-list row outside the
        // visible set remains selected.
        var visibleSet = audit.Findings.Select(r => r.ReportOrder).ToHashSet();
        // Re-show everything and confirm only the still-Error rows carry the selection.
        audit.ToggleFilterCommand.Execute(errorChip); // off => full list back
        Assert.Equal(errorCount, audit.SelectedCount); // unchanged: hidden rows stayed deselected
        Assert.All(
            audit.Findings.Where(r => r.IsSelected),
            r => Assert.Equal(RuleSeverity.Error, r.Severity));
        Assert.All(
            audit.Findings.Where(r => r.Severity != RuleSeverity.Error),
            r => Assert.False(r.IsSelected));
    }

    // === (9) Rebuild preserves filters / prunes a vanished rule class ================================

    /// <summary>
    /// With a severity filter active, an <see cref="AuditViewModel.ApplyRuleset"/> re-thread PRESERVES
    /// the severity filter (the fixed-domain axes survive a rebuild), while a rule-class chip whose
    /// class VANISHED from the rebuilt findings is PRUNED — no stale active key silently filters
    /// everything out. Drives the nesting rule OFF: the nesting class disappears (the canonical drop in
    /// AuditTableTests/AuditTriageTests), so a rule-class filter pinned on nesting must be pruned.
    /// </summary>
    [Fact]
    public void Rebuild_PreservesSeverityFilter_PrunesVanishedRuleClass()
    {
        var (snapshot, ruleset) = LoadedScopeWithFindings();
        var report = RuleEngine.Evaluate(snapshot, ruleset);
        using var audit = new AuditViewModel(snapshot, report, ruleset, RootDn, onBack: () => { });

        // Identify the nesting rule-class chip (the class that disappears when nesting is disabled).
        var nestingChip = audit.RuleClassChips.FirstOrDefault(
            c => string.Equals((string)KeyOf(c), RuleIds.Nesting, StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(nestingChip);

        // Activate BOTH a fixed-domain severity filter (Warning) AND the nesting rule-class filter.
        var warningChip = audit.SeverityChips.Single(c => c.Severity == RuleSeverity.Warning);
        audit.ToggleFilterCommand.Execute(warningChip);
        audit.ToggleFilterCommand.Execute(nestingChip!);
        Assert.True(warningChip.IsActive);
        Assert.True(nestingChip!.IsActive);

        // Re-thread with nesting disabled: the nesting class vanishes from the rebuilt findings.
        var defaults = RulesetLoader.LoadDefault();
        var nestingOff = ruleset with { Nesting = defaults.Nesting with { Enabled = false } };
        audit.ApplyRuleset(nestingOff);

        // The severity (Warning) filter SURVIVED — the new Warning chip is active and the view is still
        // constrained to Warning rows.
        var warningChipAfter = audit.SeverityChips.Single(c => c.Severity == RuleSeverity.Warning);
        Assert.True(warningChipAfter.IsActive);
        Assert.True(audit.IsFiltered);
        Assert.All(audit.Findings, r => Assert.Equal(RuleSeverity.Warning, r.Severity));

        // The nesting rule-class chip is GONE (no nesting findings remain) and the stale active key was
        // pruned — so the Warning rows are genuinely visible (a stale nesting key would have AND'd them
        // all out, leaving HasNoMatches).
        Assert.DoesNotContain(
            audit.RuleClassChips,
            c => string.Equals((string)KeyOf(c), RuleIds.Nesting, StringComparison.OrdinalIgnoreCase));
        Assert.NotEmpty(audit.Findings);
        Assert.False(audit.HasNoMatches);
    }

    // === (10) HasNoMatches vs IsAllClear are distinct ==============================================

    /// <summary>
    /// <see cref="AuditViewModel.HasNoMatches"/> and <see cref="AuditViewModel.IsAllClear"/> are
    /// DISTINCT empty states: an ALL-CLEAR scope (no findings at all) drives <see cref="AuditViewModel.IsAllClear"/>
    /// true and <see cref="AuditViewModel.HasNoMatches"/> false; a non-clean scope whose active filters
    /// hide everything drives <see cref="AuditViewModel.HasNoMatches"/> true and
    /// <see cref="AuditViewModel.IsAllClear"/> false.
    /// </summary>
    [Fact]
    public void HasNoMatches_VsIsAllClear_AreDistinctEmptyStates()
    {
        // --- All-clear scope: a single legal DL→GG nesting, nothing trips a rule => zero findings. ---
        var clean = new DirectorySnapshot();
        const string dl = "CN=DL_FileShare_RW,OU=Lab,DC=stub,DC=lab";
        const string gg = "CN=GG_FileShare_Members,OU=Lab,DC=stub,DC=lab";
        const string user = "CN=alice,OU=Lab,DC=stub,DC=lab";
        clean.AddObject(new AdObject { Dn = dl, Kind = AdObjectKind.DomainLocalGroup, Name = "DL_FileShare_RW" });
        clean.AddObject(new AdObject { Dn = gg, Kind = AdObjectKind.GlobalGroup, Name = "GG_FileShare_Members" });
        clean.AddObject(new AdObject { Dn = user, Kind = AdObjectKind.User, Name = "alice" });
        clean.SetMembers(dl, new[] { gg });
        clean.SetMembers(gg, new[] { user }); // GG with a user member: AGDLP-legal, non-empty => no finding
        var cleanRuleset = RulesetLoader.LoadDefault();
        var cleanReport = RuleEngine.Evaluate(clean, cleanRuleset);
        using var cleanAudit = new AuditViewModel(clean, cleanReport, cleanRuleset, RootDn, onBack: () => { });

        Assert.Equal(0, cleanAudit.TotalCount);
        Assert.True(cleanAudit.IsAllClear, "a finding-free scope must be all-clear");
        Assert.False(cleanAudit.HasNoMatches, "all-clear is NOT the filtered no-matches state");
        Assert.False(cleanAudit.HasFindings);

        // --- Filtered-empty scope: real findings, but contradictory filters hide them all. ---
        var (snapshot, ruleset) = LoadedScopeWithFindings();
        var report = RuleEngine.Evaluate(snapshot, ruleset);
        using var audit = new AuditViewModel(snapshot, report, ruleset, RootDn, onBack: () => { });

        var errorChip = audit.SeverityChips.Single(c => c.Severity == RuleSeverity.Error);
        var errorRuleIds = audit.Findings
            .Where(r => r.Severity == RuleSeverity.Error).Select(r => RuleIdOf(r))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var nonErrorOnlyChip = audit.RuleClassChips.First(
            c => !errorRuleIds.Contains((string)KeyOf(c))
                 && audit.Findings.Any(r => string.Equals(RuleIdOf(r), (string)KeyOf(c), StringComparison.OrdinalIgnoreCase)));
        audit.ToggleFilterCommand.Execute(errorChip);
        audit.ToggleFilterCommand.Execute(nonErrorOnlyChip);

        Assert.True(audit.TotalCount > 0, "the master list is non-empty (findings exist)");
        Assert.True(audit.HasNoMatches, "filters hiding all findings is the no-matches state");
        Assert.False(audit.IsAllClear, "a scope WITH findings is never all-clear, even when all are filtered out");
    }

    // === Helpers ===============================================================================

    /// <summary>The chip's <see cref="AuditFilterChip.Axis"/> — read via the public-test seam used by
    /// these pins (the property is <c>internal</c>; the tests assembly has InternalsVisibleTo).</summary>
    private static AuditFilterAxis AxisOf(AuditFilterChip chip) => chip.Axis;

    /// <summary>The chip's boxed <see cref="AuditFilterChip.Key"/> (internal — visible to tests).</summary>
    private static object KeyOf(AuditFilterChip chip) => chip.Key;

    /// <summary>The row's rule id — the rule-class axis key.</summary>
    private static string RuleIdOf(AuditFindingRowModel row) => row.RuleId;

    /// <summary>True when <paramref name="row"/> is the naming Warning finding anchored at
    /// <paramref name="dn"/> (the badly-named GG). Compared by the (severity, anchor) PROJECTION.</summary>
    private static bool IsNamingFindingFor(AuditFindingRowModel row, string dn) =>
        row.Severity == RuleSeverity.Warning && Dn.Comparer.Equals(row.PrimaryDn, dn);

    /// <summary>The WP5b/WP5c/WP5d/WP5e findings fixture (re-stated so the fixtures stay independent): a
    /// fully-LOADED scope tripping the default ruleset's nesting (a DL with a direct User member) +
    /// naming (a badly-named GG) + empty-group rules — a real Error/Warning/Info mix across >1 rule
    /// class. Returns the snapshot + the default ruleset. Matches
    /// <see cref="AuditTableTests"/>/<see cref="AuditTriageTests"/>.</summary>
    private static (DirectorySnapshot Snapshot, Ruleset Ruleset) LoadedScopeWithFindings()
    {
        const string dlOk = "CN=DL_FileShare_RW,OU=Lab,DC=stub,DC=lab";
        const string ggMember = "CN=GG_FileShare_Members,OU=Lab,DC=stub,DC=lab";
        const string dlBad = "CN=DL_DirectUser_RW,OU=Lab,DC=stub,DC=lab";
        const string userDn = "CN=alice,OU=Lab,DC=stub,DC=lab";
        const string ggEmpty = "CN=GG_Empty_Team,OU=Lab,DC=stub,DC=lab";

        var snapshot = new DirectorySnapshot();
        snapshot.AddObject(Group(dlOk, AdObjectKind.DomainLocalGroup));
        snapshot.AddObject(Group(ggMember, AdObjectKind.GlobalGroup));
        snapshot.AddObject(Group(dlBad, AdObjectKind.DomainLocalGroup));
        snapshot.AddObject(new AdObject { Dn = userDn, Kind = AdObjectKind.User, Name = "alice" });
        snapshot.AddObject(Group(GgBadNameDn, AdObjectKind.GlobalGroup));
        snapshot.AddObject(Group(ggEmpty, AdObjectKind.GlobalGroup));

        snapshot.SetMembers(dlOk, new[] { ggMember });
        snapshot.SetMembers(ggMember, Array.Empty<string>());
        snapshot.SetMembers(dlBad, new[] { userDn });
        snapshot.SetMembers(GgBadNameDn, Array.Empty<string>());
        snapshot.SetMembers(ggEmpty, Array.Empty<string>());

        return (snapshot, RulesetLoader.LoadDefault());
    }

    private static AdObject Group(string dn, AdObjectKind kind) => new()
    {
        Dn = dn,
        Kind = kind,
        Name = dn.Split(',')[0]["CN=".Length..],
    };

    /// <summary>Connect (demo) → pick the demo root OU → load, returning the shown window, the real
    /// shell, and the settled demo workspace (the demo root OU scope carries the AP 3.2 baseline incl.
    /// the GG_Circle_A↔GG_Circle_B cycle). Mirrors <see cref="AuditTableTests"/>' drive idiom; injects a
    /// temp-dir <see cref="UiStateStore"/> (the #124 isolation seam — never touches real %APPDATA%).</summary>
    private static async Task<(MainWindow Window, ShellViewModel Shell, WorkspaceViewModel Workspace)> DriveDemoWorkspaceAsync()
    {
        var uiStateBase = Directory.CreateTempSubdirectory("groupweaver-audit-filter-uistate-").FullName;
        var shell = new ShellViewModel(
            _ => new DemoProvider(),
            new StartupOptions(Demo: false),
            Present,
            graphRendererFactory: null,
            ruleset: null,
            locator: null,
            uiStateStore: new UiStateStore(uiStateBase));

        var window = new MainWindow { DataContext = shell, Width = 1280, Height = 720 };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var connect = Assert.IsType<ConnectionViewModel>(shell.CurrentStep);
        await connect.ConnectDemoCommand.ExecuteAsync(null);
        var picker = Assert.IsType<RootPickerViewModel>(shell.CurrentStep);
        await picker.LoadCandidates;
        Dispatcher.UIThread.RunJobs();

        picker.SelectedCandidate = picker.Candidates.First(c => c.Kind == AdObjectKind.OrganizationalUnit);
        picker.LoadRootCommand.Execute(null);
        var workspace = Assert.IsType<WorkspaceViewModel>(shell.CurrentStep);
        await workspace.Initialization;
        Dispatcher.UIThread.RunJobs();

        Assert.NotNull(workspace.Snapshot);
        return (window, shell, workspace);
    }

    /// <summary>Builds a REAL shell over a stub provider that loads the <see cref="LoadedScopeWithFindings"/>
    /// snapshot, drives it into the Audit step with the triage seam armed (OnAudit → ApplyTriage → the
    /// gate) so a Suppress produces a genuinely-tagged ignore entry. Mirrors the AuditTriageTests
    /// DriveToArmedAuditAsync idiom; injects temp-dir UiStateStore + RulesetLocator seams (the #124
    /// lesson) so nothing touches real %APPDATA%.</summary>
    private static async Task<(MainWindow Window, ShellViewModel Shell, WorkspaceViewModel Workspace, AuditViewModel Audit)>
        DriveToArmedAuditAsync()
    {
        var locator = new RulesetLocator(Directory.CreateTempSubdirectory("groupweaver-audit-filter-ruleset-").FullName);
        var (snapshot, _) = LoadedScopeWithFindings();
        var rootObject = new AdObject { Dn = RootDn, Kind = AdObjectKind.OrganizationalUnit, Name = "Lab" };
        var provider = new StubDirectoryProvider(Task.FromResult(new DirectoryConnection("stub directory", 0)))
        {
            RootCandidatesResult = Task.FromResult<IReadOnlyList<AdObject>>([rootObject]),
            LoadScopeResult = Task.FromResult(snapshot),
        };

        var uiStateBase = Directory.CreateTempSubdirectory("groupweaver-audit-filter-armed-uistate-").FullName;
        var shell = new ShellViewModel(
            _ => provider,
            new StartupOptions(Demo: false),
            Present,
            graphRendererFactory: null,
            ruleset: locator.LoadEffective(),
            locator: locator,
            uiStateStore: new UiStateStore(uiStateBase));

        var window = new MainWindow { DataContext = shell, Width = 1280, Height = 720 };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var connect = Assert.IsType<ConnectionViewModel>(shell.CurrentStep);
        await connect.ConnectDemoCommand.ExecuteAsync(null);
        var picker = Assert.IsType<RootPickerViewModel>(shell.CurrentStep);
        await picker.LoadCandidates;
        Dispatcher.UIThread.RunJobs();
        picker.SelectedCandidate = picker.Candidates[0];
        picker.LoadRootCommand.Execute(null);
        var workspace = Assert.IsType<WorkspaceViewModel>(shell.CurrentStep);
        await workspace.Initialization;
        Dispatcher.UIThread.RunJobs();
        Assert.NotNull(workspace.Snapshot);

        shell.OnAudit(workspace);
        var audit = Assert.IsType<AuditViewModel>(shell.CurrentStep);
        return (window, shell, workspace, audit);
    }
}
