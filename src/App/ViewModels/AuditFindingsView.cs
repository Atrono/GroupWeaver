using System.Collections.ObjectModel;
using System.ComponentModel;

using GroupWeaver.Core.Audit;
using GroupWeaver.Core.Model;
using GroupWeaver.Core.Rules;

namespace GroupWeaver.App.ViewModels;

/// <summary>
/// The findings-table + categories-pane + filter-chips collaborator extracted from
/// <see cref="AuditViewModel"/> (#297): owns the WP1/WP5c/WP5d master-list, filter axes, sort/filter
/// projection and per-row selection bookkeeping behind a small event surface
/// (<see cref="ViewChanged"/>/<see cref="CategoriesChanged"/>/<see cref="SelectionChanged"/>) the VM
/// re-projects into its own bound `OnPropertyChanged` calls. Plain class (never bound to directly by
/// XAML) — the VM exposes <see cref="Categories"/>/<see cref="Findings"/>/<see cref="SeverityChips"/>/
/// <see cref="StatusChips"/> via get-only passthrough properties so the bound collection identity never
/// changes. Sort state (<see cref="AuditSortColumn"/>/descending), <c>Summary</c>, the live/would-be
/// reports and the snapshot/ruleset stay VM-owned and are passed in per call — this collaborator holds
/// no reference to them between calls.
/// </summary>
public sealed class AuditFindingsView
{
    /// <summary>The FULL findings projection (WP1) — the master source of truth. <see cref="Findings"/>
    /// is the filtered + sorted VISIBLE view derived from it in <see cref="ApplyView"/>. Each row's
    /// checkbox <c>PropertyChanged</c> subscription lives on THESE rows (so the selection tally stays
    /// live even for rows the current filter hides); rebuilt in <see cref="RebuildFindings"/>.</summary>
    private readonly List<AuditFindingRowModel> _allRows = [];

    /// <summary>Active severity-axis filter (WP1). EMPTY = no constraint (fail-open); non-empty keeps
    /// only rows whose <see cref="AuditFindingRowModel.Severity"/> is contained. Preserved across a
    /// <see cref="RebuildFindings"/> (severities are a fixed domain).</summary>
    private readonly HashSet<RuleSeverity> _severityFilter = [];

    /// <summary>Active triage-status-axis filter (WP1). EMPTY = no constraint; preserved across a rebuild.</summary>
    private readonly HashSet<TriageStatus> _statusFilter = [];

    /// <summary>Active rule-class-axis filter (WP1), keyed by rule id (OrdinalIgnoreCase). EMPTY = no
    /// constraint; PRUNED on rebuild to the rule ids still present in <see cref="_allRows"/>.</summary>
    private readonly HashSet<string> _ruleClassFilter = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The categories pane (WP5c): one row per rule class that produced findings, in the
    /// canonical <see cref="Ruleset.EnumerateRules"/> order (nesting → naming rules in file order →
    /// circular → empty-group), so the list never reshuffles by dictionary order. Each row IS the
    /// rule-class filter facet (WP-C/#180) — clicking it runs <see cref="ToggleCategory"/>; its
    /// <see cref="AuditCategoryRow.IsActive"/> mirrors the <c>_ruleClassFilter</c> set. Rebuilt in
    /// place on every <see cref="RebuildCategories"/>.</summary>
    public ObservableCollection<AuditCategoryRow> Categories { get; } = [];

    /// <summary>True when at least one rule class produced a finding — gates the categories pane
    /// against an empty list (an all-clear scope shows no category rows).</summary>
    public bool HasCategories => Categories.Count > 0;

    /// <summary>The findings table (WP5d): one <see cref="AuditFindingRowModel"/> per
    /// <see cref="RuleReport.Violations"/> entry, projected with snapshot-resolved names. Ordered
    /// by the active sort (default = canonical report order). Rebuilt in place on every
    /// <see cref="RebuildFindings"/> and re-ordered on every <see cref="ApplyView"/>.</summary>
    public ObservableCollection<AuditFindingRowModel> Findings { get; } = [];

    /// <summary>True when there is at least one finding — gates the table against the all-clear
    /// empty state (mirrors <see cref="HasCategories"/>).</summary>
    public bool HasFindings => Findings.Count > 0;

    /// <summary>The severity filter chips (WP1) — fixed Error/Warning/Info, in descending severity
    /// order. Each chip's <see cref="AuditFilterChip.Count"/> is computed over <see cref="_allRows"/>;
    /// the <see cref="AuditFilterChip.Severity"/> drives the colored glyph. Rebuilt in place by
    /// <see cref="RebuildFilterChips"/> after <see cref="_allRows"/> is built.</summary>
    public ObservableCollection<AuditFilterChip> SeverityChips { get; } = [];

    /// <summary>The triage-status filter chips (WP1) — fixed Open/Acknowledged/Suppressed.</summary>
    public ObservableCollection<AuditFilterChip> StatusChips { get; } = [];

    /// <summary>The count of currently-visible (filtered) findings (= <see cref="Findings"/>.Count).</summary>
    public int VisibleCount => Findings.Count;

    /// <summary>The total count of findings before filtering (= <see cref="_allRows"/>.Count).</summary>
    public int TotalCount => _allRows.Count;

    /// <summary>True when any axis has an active constraint — gates the "Clear filters" button + the
    /// "Showing N of M" wording.</summary>
    public bool IsFiltered => _severityFilter.Count > 0 || _statusFilter.Count > 0 || _ruleClassFilter.Count > 0;

    /// <summary>The filter-strip summary line: "Showing {VisibleCount} of {TotalCount}" while filtered,
    /// else "{TotalCount} findings".</summary>
    public string FilterSummary => IsFiltered
        ? $"Showing {VisibleCount} of {TotalCount}"
        : $"{TotalCount} findings";

    /// <summary>True when findings exist but the active filters hide them all — gates the dedicated
    /// "no matches" empty state, DISTINCT from the all-clear empty state (<see cref="_allRows"/> empty).</summary>
    public bool HasNoMatches => _allRows.Count > 0 && Findings.Count == 0;

    /// <summary>True when the scope produced NO findings at all (the master list is empty) — gates the
    /// all-clear "No findings to list." text apart from the filtered <see cref="HasNoMatches"/> state.</summary>
    public bool IsAllClear => _allRows.Count == 0;

    /// <summary>The number of currently-selected (visible) findings — mirrors what the VM's own
    /// <c>SelectedCount</c> is kept in sync with via <see cref="SelectionChanged"/>.</summary>
    public int SelectedCount => Findings.Count(r => r.IsSelected);

    /// <summary>The active severity filter set, exposed read-only for the VM's <c>PersistView</c>.</summary>
    public IReadOnlyCollection<RuleSeverity> SeverityFilter => _severityFilter;

    /// <summary>The active status filter set, exposed read-only for the VM's <c>PersistView</c>.</summary>
    public IReadOnlyCollection<TriageStatus> StatusFilter => _statusFilter;

    /// <summary>The active rule-class filter set, exposed read-only for the VM's <c>PersistView</c>.</summary>
    public IReadOnlyCollection<string> RuleClassFilter => _ruleClassFilter;

    /// <summary>Fired at the end of <see cref="ApplyView"/> — the VM re-raises its own
    /// <c>HasFindings</c>/<c>AllSelected</c>/<c>VisibleCount</c>/<c>TotalCount</c>/<c>IsFiltered</c>/
    /// <c>FilterSummary</c>/<c>HasNoMatches</c>/<c>IsAllClear</c> notifications from this.</summary>
    public event Action? ViewChanged;

    /// <summary>Fired at the end of <see cref="RebuildCategories"/> — the VM re-raises
    /// <c>HasCategories</c> from this.</summary>
    public event Action? CategoriesChanged;

    /// <summary>Fired once per row whose <see cref="AuditFindingRowModel.IsSelected"/> flips during
    /// <see cref="ApplyView"/>'s hidden-row deselect pass, plus once unconditionally at its end (a
    /// filter/sort change can alter the visible selected count without any row's flag changing) — the
    /// VM re-reads <see cref="SelectedCount"/> into its own bound <c>SelectedCount</c> property on
    /// every firing. Safe to over-fire: <see cref="SelectedCount"/> is recomputed from the already
    /// current <see cref="Findings"/>, so repeat firings within one <see cref="ApplyView"/> call always
    /// read the same value and the VM's generated property setter no-ops on the unchanged value.</summary>
    public event Action? SelectionChanged;

    /// <summary>Repopulates <see cref="Categories"/> in place from <paramref name="report"/> +
    /// <paramref name="ruleset"/> + <paramref name="summary"/>. The display name + canonical order come
    /// from <see cref="Ruleset.EnumerateRules"/>; the count from <see cref="AuditSummary.ByRuleClass"/>;
    /// the dot's max severity from a single pass over the report's findings (the borrowed report is
    /// read-only — never mutated).</summary>
    public void RebuildCategories(RuleReport report, Ruleset ruleset, AuditSummary summary)
    {
        // Max severity per rule id, one pass over the canonically-ordered findings (Info<Warning<Error).
        var maxByRuleId = new Dictionary<string, RuleSeverity>(StringComparer.OrdinalIgnoreCase);
        foreach (var violation in report.Violations)
        {
            if (!maxByRuleId.TryGetValue(violation.RuleId, out var s) || violation.Severity > s)
            {
                maxByRuleId[violation.RuleId] = violation.Severity;
            }
        }

        Categories.Clear();
        foreach (var rule in ruleset.EnumerateRules())
        {
            // ByRuleClass only carries ids that produced a finding, so a 0-count / clean class is
            // skipped — the pane lists classes with findings, in canonical rule order.
            if (summary.ByRuleClass.TryGetValue(rule.Id, out var count) && count > 0)
            {
                var severity = maxByRuleId.TryGetValue(rule.Id, out var max) ? max : RuleSeverity.Info;
                // IsActive mirrors the rule-class filter set so the active affordance survives a
                // summary rebuild (exactly like the filter chips do — WP-C/#180).
                Categories.Add(new AuditCategoryRow(rule.Id, rule.DisplayName, count, severity)
                {
                    IsActive = _ruleClassFilter.Contains(rule.Id),
                });
            }
        }

        CategoriesChanged?.Invoke();
    }

    /// <summary>Repopulates <see cref="Findings"/> in place from <paramref name="wouldBeReport"/>: one
    /// row per <see cref="RuleReport.Violations"/> entry, name-resolved snapshot-only via the shared
    /// <see cref="SubjectNameResolver"/> (identical to the sidebar), the rule-class label from
    /// <see cref="Ruleset.EnumerateRules"/>. Each row's checkbox <c>PropertyChanged</c> is wired so the
    /// selection tally stays live; the prior rows are detached first (a settings re-thread drops the
    /// old selection). The collection is then ordered by the active sort (default = report order).</summary>
    public void RebuildFindings(
        RuleReport wouldBeReport,
        Ruleset liveRuleset,
        DirectorySnapshot snapshot,
        AuditSortColumn sortColumn,
        bool sortDescending)
    {
        // Detach the OLD master rows (the subscription lives on _allRows, not the visible view — WP1).
        foreach (var row in _allRows)
        {
            row.PropertyChanged -= OnRowPropertyChanged;
        }

        // Rule id -> human class label (canonical EnumerateRules order); naming rules carry their id.
        var ruleClassById = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var rule in liveRuleset.EnumerateRules())
        {
            ruleClassById[rule.Id] = rule.DisplayName;
        }

        _allRows.Clear();
        var reportOrder = 0;
        foreach (var violation in wouldBeReport.Violations)
        {
            var name = SubjectNameResolver.Resolve(snapshot, violation.PrimaryDn);
            var ruleClass = ruleClassById.TryGetValue(violation.RuleId, out var label) ? label : violation.RuleId;
            // Per-row status (ADR-028): match the finding's ESCAPED primary DN against the LIVE
            // ruleset's tagged ignore entries — a hit means a tagged entry covers it (ack/suppress).
            var escapedDn = TriageEntry.Escape(violation.PrimaryDn);
            var status = TriageEntry.StatusFor(liveRuleset.Ignore, escapedDn) switch
            {
                TriageKind.Acknowledge => TriageStatus.Acknowledged,
                TriageKind.Suppress => TriageStatus.Suppressed,
                _ => TriageStatus.Open,
            };
            var row = new AuditFindingRowModel(
                reportOrder++,
                violation,
                name,
                ruleClass,
                status);
            row.PropertyChanged += OnRowPropertyChanged;
            _allRows.Add(row);
        }

        // Rebuild the chips AFTER _allRows (severity/status counts derive from it); this preserves the
        // severity/status sets and prunes the rule-class set to surviving rule ids. The rule-class
        // filter is driven by the categories pane (WP-C/#180), not a separate chip group.
        RebuildFilterChips();
        ApplyView(sortColumn, sortDescending);
    }

    /// <summary>Rebuilds the severity + status chip collections (WP1) from the freshly-built
    /// <see cref="_allRows"/> (both are fixed domains, counts over <see cref="_allRows"/>; their filter
    /// sets are PRESERVED). Also prunes the rule-class filter set to the rule ids still present so a
    /// stale id (after a re-thread dropped a class) never silently filters everything out — that set is
    /// now driven by the categories pane (WP-C/#180), so there is no rule-class chip collection. Each
    /// chip's <see cref="AuditFilterChip.IsActive"/> is set from the (preserved/pruned) sets.</summary>
    public void RebuildFilterChips()
    {
        SeverityChips.Clear();
        foreach (var severity in new[] { RuleSeverity.Error, RuleSeverity.Warning, RuleSeverity.Info })
        {
            var count = _allRows.Count(r => r.Severity == severity);
            SeverityChips.Add(new AuditFilterChip(AuditFilterAxis.Severity, severity, SeverityLabel(severity), count, severity)
            {
                IsActive = _severityFilter.Contains(severity),
            });
        }

        StatusChips.Clear();
        foreach (var status in new[] { TriageStatus.Open, TriageStatus.Acknowledged, TriageStatus.Suppressed })
        {
            var count = _allRows.Count(r => r.Status == status);
            StatusChips.Add(new AuditFilterChip(AuditFilterAxis.Status, status, StatusLabel(status), count, null)
            {
                IsActive = _statusFilter.Contains(status),
            });
        }

        // Prune the rule-class filter set to the rule ids still present (a re-thread can drop a rule
        // class) so a stale id never silently filters everything out. The set is the single source of
        // truth the categories pane's rows mirror (RebuildCategories sets each row's IsActive from it).
        var ruleIds = _allRows
            .Select(r => r.RuleId)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        _ruleClassFilter.RemoveWhere(id => !ruleIds.Contains(id));
    }

    private static string SeverityLabel(RuleSeverity severity) => severity switch
    {
        RuleSeverity.Error => "Errors",
        RuleSeverity.Warning => "Warnings",
        _ => "Info",
    };

    private static string StatusLabel(TriageStatus status) => status switch
    {
        TriageStatus.Acknowledged => "Acknowledged",
        TriageStatus.Suppressed => "Suppressed",
        _ => "Open",
    };

    /// <summary>Rebuilds the visible <see cref="Findings"/> view (WP1) from the <see cref="_allRows"/>
    /// master list: FILTER (the three fail-open axes AND'd) then the existing SORT
    /// (<paramref name="sortColumn"/>/<paramref name="sortDescending"/>, <see cref="AuditSortColumn.None"/>
    /// = the canonical <see cref="RuleReport.Violations"/> report order, with the report-order index as
    /// every key's deterministic tie-break). Rows filtered OUT are deselected (a no-op on a pure sort —
    /// the visible set is unchanged — so selection survives sorting but never lingers on a hidden row).
    /// Rebuilds by in-place Clear/Add (the bound collection reference never changes).</summary>
    public void ApplyView(AuditSortColumn sortColumn, bool sortDescending)
    {
        // FILTER: a row passes iff it satisfies every NON-EMPTY axis (empty axis => no constraint).
        var filtered = _allRows.Where(r =>
            (_severityFilter.Count == 0 || _severityFilter.Contains(r.Severity))
            && (_statusFilter.Count == 0 || _statusFilter.Contains(r.Status))
            && (_ruleClassFilter.Count == 0 || _ruleClassFilter.Contains(r.RuleId)));

        // SORT: r.ReportOrder is the canonical report-projection rank — the None ordering and the
        // deterministic tie-break for every other column, independent of current row order.
        IEnumerable<AuditFindingRowModel> ordered = sortColumn switch
        {
            AuditSortColumn.Severity => sortDescending
                ? filtered.OrderByDescending(r => (int)r.Severity).ThenBy(r => r.ReportOrder)
                : filtered.OrderBy(r => (int)r.Severity).ThenBy(r => r.ReportOrder),
            AuditSortColumn.ObjectName => sortDescending
                ? filtered.OrderByDescending(r => r.ObjectName, StringComparer.OrdinalIgnoreCase).ThenBy(r => r.ReportOrder)
                : filtered.OrderBy(r => r.ObjectName, StringComparer.OrdinalIgnoreCase).ThenBy(r => r.ReportOrder),
            AuditSortColumn.RuleClass => sortDescending
                ? filtered.OrderByDescending(r => r.RuleClass, StringComparer.OrdinalIgnoreCase).ThenBy(r => r.ReportOrder)
                : filtered.OrderBy(r => r.RuleClass, StringComparer.OrdinalIgnoreCase).ThenBy(r => r.ReportOrder),
            _ => filtered.OrderBy(r => r.ReportOrder),
        };

        var visible = ordered.ToList();

        Findings.Clear();
        foreach (var row in visible)
        {
            Findings.Add(row);
        }

        // Deselect rows the filter hid — a no-op on a pure sort (membership unchanged) so selection
        // survives sorting but never lingers on a hidden row.
        var visibleSet = new HashSet<AuditFindingRowModel>(visible);
        foreach (var row in _allRows)
        {
            if (!visibleSet.Contains(row))
            {
                row.IsSelected = false;
            }
        }

        // Unconditional — a filter/sort change can alter the visible selected count without any row's
        // IsSelected flag actually flipping (e.g. a newly-hidden-then-shown row's own membership change).
        SelectionChanged?.Invoke();
        ViewChanged?.Invoke();
    }

    /// <summary>Toggles one filter chip (WP1): flips its <see cref="AuditFilterChip.IsActive"/>, adds/
    /// removes its <see cref="AuditFilterChip.Key"/> in the set selected by its
    /// <see cref="AuditFilterChip.Axis"/>, and rebuilds the visible view. The chips multi-select within
    /// AND across axes (each axis is independent + fail-open).</summary>
    public void ToggleFilter(AuditFilterChip chip, AuditSortColumn sortColumn, bool sortDescending)
    {
        chip.IsActive = !chip.IsActive;
        switch (chip.Axis)
        {
            case AuditFilterAxis.Severity:
                ToggleKey(_severityFilter, (RuleSeverity)chip.Key, chip.IsActive);
                break;
            case AuditFilterAxis.Status:
                ToggleKey(_statusFilter, (TriageStatus)chip.Key, chip.IsActive);
                break;
        }

        ApplyView(sortColumn, sortDescending);
    }

    /// <summary>Toggles the rule-class filter from a categories-pane row (WP-C/#180): flips the row's
    /// <see cref="AuditCategoryRow.IsActive"/>, adds/removes its <see cref="AuditCategoryRow.RuleId"/>
    /// in <c>_ruleClassFilter</c>, and rebuilds the visible view. The rule-class axis is the single
    /// source of truth here — the category rows ARE the rule-class filter facet (the separate strip
    /// chip group was removed as a duplicate). Multi-select within the axis; ANDs with severity/status.</summary>
    public void ToggleCategory(AuditCategoryRow row, AuditSortColumn sortColumn, bool sortDescending)
    {
        row.IsActive = !row.IsActive;
        ToggleKey(_ruleClassFilter, row.RuleId, row.IsActive);
        ApplyView(sortColumn, sortDescending);
    }

    private static void ToggleKey<T>(HashSet<T> set, T key, bool active)
    {
        if (active)
        {
            set.Add(key);
        }
        else
        {
            set.Remove(key);
        }
    }

    /// <summary>Clears every filter axis (WP1): empties all three sets, deactivates every chip, and
    /// rebuilds the full (only-sorted) view. The "Clear filters" affordance.</summary>
    public void ClearFilters(AuditSortColumn sortColumn, bool sortDescending)
    {
        _severityFilter.Clear();
        _statusFilter.Clear();
        _ruleClassFilter.Clear();
        foreach (var chip in SeverityChips)
        {
            chip.IsActive = false;
        }

        foreach (var chip in StatusChips)
        {
            chip.IsActive = false;
        }

        // The rule-class axis lives on the categories pane (WP-C/#180) — reset its rows' active state.
        foreach (var row in Categories)
        {
            row.IsActive = false;
        }

        ApplyView(sortColumn, sortDescending);
    }

    /// <summary>The select-all / select-none header checkbox (WP5d): if every VISIBLE row is already
    /// selected, clears all; otherwise selects all. The per-row notifications re-tally the bar. This
    /// local "all selected" check is equivalent to the VM's own <c>AllSelected</c> property only
    /// because the VM's <c>SelectedCount</c> is kept in lock-step with <see cref="SelectedCount"/> via
    /// the <see cref="SelectionChanged"/> subscription installed once in the VM's constructor — if that
    /// wiring ever changes, re-verify this formula against the VM's.</summary>
    public void ToggleSelectAll()
    {
        var allSelected = Findings.Count > 0 && SelectedCount == Findings.Count;
        var target = !allSelected;
        foreach (var row in Findings)
        {
            row.IsSelected = target;
        }
    }

    /// <summary>The selection bar's "Clear" (WP5d): deselects every VISIBLE row. No Ack/Suppress action
    /// here.</summary>
    public void ClearSelection()
    {
        foreach (var row in Findings)
        {
            row.IsSelected = false;
        }
    }

    /// <summary>Replaces the three filter sets wholesale (used by the VM's persisted-state restore) —
    /// never fires any event; the VM re-projects (chips/categories/view) itself immediately after.</summary>
    public void RestoreFilters(
        IEnumerable<RuleSeverity> severities,
        IEnumerable<TriageStatus> statuses,
        IEnumerable<string> ruleClassIds)
    {
        _severityFilter.Clear();
        foreach (var severity in severities)
        {
            _severityFilter.Add(severity);
        }

        _statusFilter.Clear();
        foreach (var status in statuses)
        {
            _statusFilter.Add(status);
        }

        _ruleClassFilter.Clear();
        foreach (var ruleId in ruleClassIds)
        {
            _ruleClassFilter.Add(ruleId);
        }
    }

    /// <summary>Re-tallies <see cref="SelectedCount"/> (via <see cref="SelectionChanged"/>) when a
    /// row's checkbox toggles (WP5d).</summary>
    private void OnRowPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AuditFindingRowModel.IsSelected))
        {
            SelectionChanged?.Invoke();
        }
    }
}
