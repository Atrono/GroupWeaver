using System.Collections.ObjectModel;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using GroupWeaver.Core.Model;
using GroupWeaver.Core.Rules;

namespace GroupWeaver.App.ViewModels;

/// <summary>
/// One categories-pane row (WP5c): a rule class that produced findings — its
/// display name, the count from <see cref="AuditSummary.ByRuleClass"/>, and the
/// max severity across that class's findings (drives the leading dot color). The
/// row is read-only for now (click-to-filter is WP5d/e). Rows are emitted in the
/// canonical rule order (<see cref="Ruleset.EnumerateRules"/>); see
/// <see cref="AuditViewModel.Categories"/>.
/// </summary>
public sealed record AuditCategoryRow(string RuleId, string DisplayName, int Count, RuleSeverity MaxSeverity);

/// <summary>
/// One findings-table row (WP5d / #156): the projection of a <see cref="RuleViolation"/> the
/// <see cref="Views.AuditView"/> table binds. The display fields are immutable; only
/// <see cref="IsSelected"/> mutates (the multi-select checkbox the WP5e bulk Ack/Suppress will
/// consume — this slice only collects the selection, it does NOT act on it).
///
/// <para>Mirrors <see cref="ViolationRowModel"/>'s projection shape: severity (glyph color +
/// redundant letter via the one <see cref="Views.SeverityConverters"/> palette), the canonical
/// message, the snapshot-resolved object name (raw/External anchors fall back to the DN via the
/// shared <see cref="SubjectNameResolver"/>), and the jump anchor (<see cref="RuleViolation.PrimaryDn"/>).
/// <see cref="RuleClass"/> is the human rule-class label from <see cref="Ruleset.EnumerateRules"/>.
/// <see cref="Status"/> is a fixed "Open" placeholder — the Acknowledged/Suppressed states land in
/// WP5e (no state machine here, just an honest neutral label).</para>
/// </summary>
public sealed partial class AuditFindingRowModel : ObservableObject
{
    public AuditFindingRowModel(
        int reportOrder, RuleSeverity severity, string message, string objectName, string ruleClass, string primaryDn)
    {
        ReportOrder = reportOrder;
        Severity = severity;
        Message = message;
        ObjectName = objectName;
        RuleClass = ruleClass;
        PrimaryDn = primaryDn;
    }

    /// <summary>The 0-based rank of this row in the canonical <see cref="RuleReport.Violations"/>
    /// order (assigned at projection). The <see cref="AuditSortColumn.None"/> ordering and the
    /// deterministic tie-break for every other column — stable regardless of the current row order,
    /// so re-sorting an already-sorted table stays correct.</summary>
    public int ReportOrder { get; }

    /// <summary>Effective severity — drives the glyph color/letter (overlay-color parity).</summary>
    public RuleSeverity Severity { get; }

    /// <summary>The finding's presentation message (canonical, culture-invariant).</summary>
    public string Message { get; }

    /// <summary>The anchor object's display name, resolved snapshot-only; raw/External anchors
    /// fall back to the DN.</summary>
    public string ObjectName { get; }

    /// <summary>The human rule-class label (e.g. "Nesting matrix", "Circular nesting").</summary>
    public string RuleClass { get; }

    /// <summary>The jump-to-node anchor (<c>Dns[0]</c>) — the future detail/jump key.</summary>
    public string PrimaryDn { get; }

    /// <summary>Triage status — fixed "Open" in v1 (WP5e adds Acknowledged/Suppressed).</summary>
    public string Status => "Open";

    /// <summary>Multi-select checkbox state (WP5d). The selection bar tallies it; WP5e's bulk
    /// Ack/Suppress will act on the selected rows. The VM owns the toggled notifications via the
    /// per-row <c>PropertyChanged</c> subscription so "{n} selected" stays live.</summary>
    [ObservableProperty]
    private bool _isSelected;
}

/// <summary>The findings-table sort key (WP5d). The default <see cref="None"/> means the canonical
/// report order (severity-blocks then report order — <see cref="RuleReport.Violations"/>); the
/// other keys re-order the bound collection deterministically with the report order as the tie-break.</summary>
public enum AuditSortColumn
{
    /// <summary>No explicit sort — the canonical report order (the default).</summary>
    None,

    /// <summary>By severity (Error &gt; Warning &gt; Info when descending).</summary>
    Severity,

    /// <summary>By resolved object name (OrdinalIgnoreCase).</summary>
    ObjectName,

    /// <summary>By rule-class label (OrdinalIgnoreCase).</summary>
    RuleClass,
}

/// <summary>
/// Audit step (WP5 / #152): a sibling shell step the live <see cref="WorkspaceViewModel"/>
/// switches into (like <see cref="PlanViewModel"/>/<see cref="GapViewModel"/>) to show the
/// dashboard health roll-up over the borrowed live "Ist" <see cref="DirectorySnapshot"/>. This
/// WP5b slice is the navigation seam plus a minimal-but-real placeholder; the rich health
/// ring / tiles / table land in later sub-slices.
///
/// <para>Unlike the other steps the audit step is a TABLE view (v1) — it owns NO graph renderer
/// (so there is no airspace conflict and nothing to park). It is still <see cref="IDisposable"/>
/// because the shell tracks every step through the <c>Track</c>/<c>DisposeAndUntrack</c>
/// <see cref="IDisposable"/> contract; its <see cref="Dispose"/> is a no-op (it owns no
/// renderer and no cancellable in-flight work).</para>
///
/// <para><b>Borrowed Ist is read-only.</b> The Ist snapshot, report and ruleset are borrowed —
/// the audit never mutates them and <see cref="Dispose"/> never disposes them; the shell owns
/// their lifetime. The summary is recomputed purely via <see cref="AuditSummary.Compute"/> (no
/// provider, no AD touch). <see cref="ApplyRuleset"/> re-evaluates against a flipped ruleset.</para>
/// </summary>
public sealed partial class AuditViewModel : ObservableObject, IDisposable
{
    private readonly DirectorySnapshot _snapshot; // BORROWED — read-only, never disposed/mutated.
    private readonly Action _onBack;
    private Ruleset _ruleset;

    /// <summary>The current rule report — borrowed at construction, replaced in place by
    /// <see cref="ApplyRuleset"/> (a settings Apply/Save re-thread). Drives <see cref="Summary"/>.</summary>
    private RuleReport _report;

    /// <summary>The dashboard health roll-up the view binds (WP5). Recomputed from
    /// <see cref="AuditSummary.Compute"/> at construction and on every <see cref="ApplyRuleset"/>;
    /// the bound count/score props re-notify in <see cref="OnSummaryChanged"/>.</summary>
    [ObservableProperty]
    private AuditSummary _summary;

    public AuditViewModel(DirectorySnapshot snapshot, RuleReport report, Ruleset ruleset, string rootDn, Action onBack)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(report);
        ArgumentNullException.ThrowIfNull(ruleset);

        _snapshot = snapshot; // BORROWED — never disposed, never mutated.
        _report = report;
        _ruleset = ruleset;
        RootDn = rootDn;
        _onBack = onBack;

        _summary = AuditSummary.Compute(report, snapshot, ruleset);
        RebuildCategories();
        RebuildFindings();
    }

    /// <summary>The categories pane (WP5c): one row per rule class that produced findings, in the
    /// canonical <see cref="Ruleset.EnumerateRules"/> order (nesting → naming rules in file order →
    /// circular → empty-group), so the list never reshuffles by dictionary order. Read-only for now;
    /// click-to-filter is WP5d/e. Rebuilt in place on every <see cref="ApplyRuleset"/>.</summary>
    public ObservableCollection<AuditCategoryRow> Categories { get; } = [];

    /// <summary>True when at least one rule class produced a finding — gates the categories pane
    /// against an empty list (an all-clear scope shows no category rows).</summary>
    public bool HasCategories => Categories.Count > 0;

    /// <summary>The findings table (WP5d): one <see cref="AuditFindingRowModel"/> per
    /// <see cref="RuleReport.Violations"/> entry, projected with snapshot-resolved names. Ordered
    /// by <see cref="SortColumn"/>/<see cref="SortDescending"/> (default = canonical report order).
    /// Rebuilt in place on every <see cref="ApplyRuleset"/> and re-ordered on every header sort.</summary>
    public ObservableCollection<AuditFindingRowModel> Findings { get; } = [];

    /// <summary>True when there is at least one finding — gates the table against the all-clear
    /// empty state (mirrors <see cref="HasCategories"/>).</summary>
    public bool HasFindings => Findings.Count > 0;

    /// <summary>The active sort column (WP5d). <see cref="AuditSortColumn.None"/> = the canonical
    /// report order; the view's header carets read this + <see cref="SortDescending"/>.</summary>
    [ObservableProperty]
    private AuditSortColumn _sortColumn = AuditSortColumn.None;

    /// <summary>Sort direction for <see cref="SortColumn"/> (ignored when <see cref="AuditSortColumn.None"/>).</summary>
    [ObservableProperty]
    private bool _sortDescending;

    /// <summary>The number of currently-selected findings (the multi-select bar's "{n} selected").
    /// Recomputed in <see cref="OnFindingSelectionChanged"/> when any row's checkbox toggles.</summary>
    [ObservableProperty]
    private int _selectedCount;

    /// <summary>True when at least one finding is selected — gates the selection bar's visibility.</summary>
    public bool HasSelection => SelectedCount > 0;

    /// <summary>True when EVERY finding is selected — drives the header "select all/none" checkbox
    /// checked state (the view binds it two-way through the select-all command, not directly).</summary>
    public bool AllSelected => Findings.Count > 0 && SelectedCount == Findings.Count;

    /// <summary>The base OU the audit was run over (the workspace root); part of the title.</summary>
    public string RootDn { get; }

    /// <summary>The audit step header line.</summary>
    public string Title => $"Audit · {RootDn}";

    /// <summary>The 0-100 health score (<see cref="AuditSummary.Score"/>).</summary>
    public int Score => Summary.Score;

    /// <summary>The qualitative band for <see cref="Score"/> (<see cref="AuditSummary.Band"/>).</summary>
    public string Band => Summary.Band;

    /// <summary>The health-ring fill fraction in [0, 1] (= <see cref="Score"/> / 100), bound to the
    /// view's <see cref="Avalonia.Media.ConicGradientBrush"/> hard stop so the ring fills to the
    /// score. DECORATIVE only — the meaning is carried by the always-present "{Score} / 100" + band
    /// text (WCAG 1.4.1); the ring color is band-coded in the view.</summary>
    public double RingFraction => Score / 100.0;

    /// <summary>The screen-reader label for the health ring (WCAG 1.1.1 / 4.1.2): restates the
    /// score + band so the decorative ring is not the sole channel.</summary>
    public string HealthAutomationName => $"Directory health {Score} of 100, {Band}";

    /// <summary>True when unexpanded areas remain unchecked (<see cref="AuditSummary.UncheckedPresent"/>):
    /// gates the honest "the score covers checked objects only" caveat so the score never implies a
    /// clean bill over unexpanded subtrees.</summary>
    public bool UncheckedPresent => Summary.UncheckedPresent;

    /// <summary>Error-severity finding count (<see cref="AuditSummary.Critical"/>).</summary>
    public int Critical => Summary.Critical;

    /// <summary>Warning-severity finding count (<see cref="AuditSummary.Warnings"/>).</summary>
    public int Warnings => Summary.Warnings;

    /// <summary>Info-severity finding count (<see cref="AuditSummary.Info"/>) — a muted sub-affordance
    /// on the Warnings tile, not a primary tile (it is not part of Critical/Warnings).</summary>
    public int Info => Summary.Info;

    /// <summary>Checked subjects with no finding (<see cref="AuditSummary.Passing"/>).</summary>
    public int Passing => Summary.Passing;

    /// <summary>Enabled rule-block count (<see cref="AuditSummary.RuleClasses"/>).</summary>
    public int RuleClasses => Summary.RuleClasses;

    /// <summary>True once <see cref="Dispose"/> ran — the dispose-discipline observability the
    /// shell teardown pins read; the Workspace↔Audit round-trip must never flip this until Back.</summary>
    public bool IsDisposed { get; private set; }

    /// <summary>Re-projects the score/band/count props whenever the <see cref="Summary"/> record is
    /// replaced (a settings re-thread), so the bound placeholder stays in sync.</summary>
    partial void OnSummaryChanged(AuditSummary value)
    {
        OnPropertyChanged(nameof(Score));
        OnPropertyChanged(nameof(Band));
        OnPropertyChanged(nameof(RingFraction));
        OnPropertyChanged(nameof(HealthAutomationName));
        OnPropertyChanged(nameof(Critical));
        OnPropertyChanged(nameof(Warnings));
        OnPropertyChanged(nameof(Info));
        OnPropertyChanged(nameof(Passing));
        OnPropertyChanged(nameof(RuleClasses));
        OnPropertyChanged(nameof(UncheckedPresent));
        RebuildCategories();
        RebuildFindings();
    }

    /// <summary>Re-projects the selection roll-ups whenever the count changes.</summary>
    partial void OnSelectedCountChanged(int value)
    {
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(AllSelected));
    }

    /// <summary>A header click re-orders the live <see cref="Findings"/> collection in place.</summary>
    partial void OnSortColumnChanged(AuditSortColumn value) => ApplySort();

    /// <summary>A direction flip re-orders the live <see cref="Findings"/> collection in place.</summary>
    partial void OnSortDescendingChanged(bool value) => ApplySort();

    /// <summary>Repopulates <see cref="Categories"/> in place from the current <see cref="Summary"/>
    /// + report. The display name + canonical order come from <see cref="Ruleset.EnumerateRules"/>;
    /// the count from <see cref="AuditSummary.ByRuleClass"/>; the dot's max severity from a single
    /// pass over the report's findings (the borrowed report is read-only — never mutated).</summary>
    private void RebuildCategories()
    {
        // Max severity per rule id, one pass over the canonically-ordered findings (Info<Warning<Error).
        var maxByRuleId = new Dictionary<string, RuleSeverity>(StringComparer.OrdinalIgnoreCase);
        foreach (var violation in _report.Violations)
        {
            if (!maxByRuleId.TryGetValue(violation.RuleId, out var s) || violation.Severity > s)
            {
                maxByRuleId[violation.RuleId] = violation.Severity;
            }
        }

        Categories.Clear();
        foreach (var rule in _ruleset.EnumerateRules())
        {
            // ByRuleClass only carries ids that produced a finding, so a 0-count / clean class is
            // skipped — the pane lists classes with findings, in canonical rule order.
            if (Summary.ByRuleClass.TryGetValue(rule.Id, out var count) && count > 0)
            {
                var severity = maxByRuleId.TryGetValue(rule.Id, out var max) ? max : RuleSeverity.Info;
                Categories.Add(new AuditCategoryRow(rule.Id, rule.DisplayName, count, severity));
            }
        }

        OnPropertyChanged(nameof(HasCategories));
    }

    /// <summary>Repopulates <see cref="Findings"/> in place from the current report: one row per
    /// <see cref="RuleReport.Violations"/> entry, name-resolved snapshot-only via the shared
    /// <see cref="SubjectNameResolver"/> (identical to the sidebar), the rule-class label from
    /// <see cref="Ruleset.EnumerateRules"/>. Each row's checkbox <c>PropertyChanged</c> is wired so
    /// the selection bar stays live; the prior rows are detached first (a settings re-thread drops
    /// the old selection). The collection is then ordered by the active sort (default = report order).</summary>
    private void RebuildFindings()
    {
        foreach (var row in Findings)
        {
            row.PropertyChanged -= OnFindingPropertyChanged;
        }

        // Rule id -> human class label (canonical EnumerateRules order); naming rules carry their id.
        var ruleClassById = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var rule in _ruleset.EnumerateRules())
        {
            ruleClassById[rule.Id] = rule.DisplayName;
        }

        Findings.Clear();
        var reportOrder = 0;
        foreach (var violation in _report.Violations)
        {
            var name = SubjectNameResolver.Resolve(_snapshot, violation.PrimaryDn);
            var ruleClass = ruleClassById.TryGetValue(violation.RuleId, out var label) ? label : violation.RuleId;
            var row = new AuditFindingRowModel(
                reportOrder++, violation.Severity, violation.Message, name, ruleClass, violation.PrimaryDn);
            row.PropertyChanged += OnFindingPropertyChanged;
            Findings.Add(row);
        }

        SelectedCount = 0;
        OnPropertyChanged(nameof(HasFindings));
        OnPropertyChanged(nameof(AllSelected));
        ApplySort();
    }

    /// <summary>Re-orders <see cref="Findings"/> in place to match <see cref="SortColumn"/>/
    /// <see cref="SortDescending"/>. <see cref="AuditSortColumn.None"/> restores the canonical
    /// report order (the engine's <see cref="RuleReport.Violations"/> order — the projection index).
    /// Deterministic: every key falls back to the report-order index as the final tie-break, so a
    /// stable, repeatable ordering results regardless of the move sequence. Rebuilds by clearing and
    /// re-adding (the bound collection reference never changes — in-place Clear/Add).</summary>
    private void ApplySort()
    {
        if (Findings.Count == 0)
        {
            return;
        }

        // r.ReportOrder is the canonical report-projection rank, assigned once at projection — both
        // the None ordering and the deterministic tie-break for the other columns. It is independent
        // of the current row order, so re-sorting an already-sorted table stays correct.
        IEnumerable<AuditFindingRowModel> ordered = SortColumn switch
        {
            AuditSortColumn.Severity => SortDescending
                ? Findings.OrderByDescending(r => (int)r.Severity).ThenBy(r => r.ReportOrder)
                : Findings.OrderBy(r => (int)r.Severity).ThenBy(r => r.ReportOrder),
            AuditSortColumn.ObjectName => SortDescending
                ? Findings.OrderByDescending(r => r.ObjectName, StringComparer.OrdinalIgnoreCase).ThenBy(r => r.ReportOrder)
                : Findings.OrderBy(r => r.ObjectName, StringComparer.OrdinalIgnoreCase).ThenBy(r => r.ReportOrder),
            AuditSortColumn.RuleClass => SortDescending
                ? Findings.OrderByDescending(r => r.RuleClass, StringComparer.OrdinalIgnoreCase).ThenBy(r => r.ReportOrder)
                : Findings.OrderBy(r => r.RuleClass, StringComparer.OrdinalIgnoreCase).ThenBy(r => r.ReportOrder),
            _ => Findings.OrderBy(r => r.ReportOrder),
        };

        var sorted = ordered.ToList();
        Findings.Clear();
        foreach (var row in sorted)
        {
            Findings.Add(row);
        }
    }

    /// <summary>A column header click (WP5d): selecting the same column flips the direction;
    /// selecting a new one switches to it (ascending — except severity, which defaults to descending
    /// so the worst findings sort to the top, matching the canonical errors-first instinct).</summary>
    [RelayCommand]
    private void Sort(AuditSortColumn column)
    {
        if (column == AuditSortColumn.None)
        {
            return;
        }

        if (SortColumn == column)
        {
            SortDescending = !SortDescending;
        }
        else
        {
            SortDescending = column == AuditSortColumn.Severity;
            SortColumn = column; // Triggers ApplySort via OnSortColumnChanged.
        }
    }

    /// <summary>The select-all / select-none header checkbox (WP5d): if every row is already
    /// selected, clears all; otherwise selects all. The per-row notifications re-tally the bar.</summary>
    [RelayCommand]
    private void ToggleSelectAll()
    {
        var target = !AllSelected;
        foreach (var row in Findings)
        {
            row.IsSelected = target;
        }
    }

    /// <summary>The selection bar's "Clear" (WP5d): deselects every row. No Ack/Suppress action
    /// here — that is WP5e.</summary>
    [RelayCommand]
    private void ClearSelection()
    {
        foreach (var row in Findings)
        {
            row.IsSelected = false;
        }
    }

    /// <summary>Re-tallies <see cref="SelectedCount"/> when a row's checkbox toggles (WP5d).</summary>
    private void OnFindingPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AuditFindingRowModel.IsSelected))
        {
            SelectedCount = Findings.Count(r => r.IsSelected);
        }
    }

    /// <summary>Re-threads a settings Apply/Save into this live audit step (WP5; the shell's
    /// <c>OnRulesetApplied</c> calls it): swaps the ruleset, re-Evaluates over the BORROWED Ist
    /// snapshot (<see cref="RuleEngine.Evaluate"/> — pure/sync, never mutates) and recomputes the
    /// <see cref="Summary"/>. The full re-thread-the-parked-workspace refinement is WP5e.</summary>
    public void ApplyRuleset(Ruleset ruleset)
    {
        ArgumentNullException.ThrowIfNull(ruleset);
        _ruleset = ruleset;
        _report = RuleEngine.Evaluate(_snapshot, _ruleset);
        Summary = AuditSummary.Compute(_report, _snapshot, _ruleset);
    }

    /// <summary>Back to the Ist workspace (mirrors <see cref="GapViewModel.BackCommand"/>):
    /// invokes the shell-supplied callback, which restores the SAME workspace instance and
    /// disposes this audit step.</summary>
    [RelayCommand]
    private void Back() => _onBack.Invoke();

    /// <summary>"Show in graph" (WP5 v1): returning to the workspace IS the graph view, so it
    /// routes through the same Back callback. A richer in-graph projection is a later sub-slice.</summary>
    [RelayCommand]
    private void ShowInGraph() => _onBack.Invoke();

    /// <summary>No-op dispose (the audit step owns no renderer and no cancellable in-flight work):
    /// it exists only to satisfy the shell's <see cref="IDisposable"/> track/untrack contract.
    /// Idempotent; NEVER disposes nor mutates the borrowed Ist snapshot / report / ruleset.</summary>
    public void Dispose() => IsDisposed = true;
}
