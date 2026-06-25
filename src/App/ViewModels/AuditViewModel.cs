using System.Collections.ObjectModel;
using System.Text;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using GroupWeaver.App.Export;
using GroupWeaver.Core.Export;
using GroupWeaver.Core.Model;
using GroupWeaver.Core.Rules;

namespace GroupWeaver.App.ViewModels;

/// <summary>
/// One categories-pane row (WP5c, made interactive in WP-C/#180): a rule class that produced
/// findings — its display name, the count from <see cref="AuditSummary.ByRuleClass"/>, and the
/// max severity across that class's findings (drives the leading dot color). The row IS the
/// rule-class filter facet (the strip's separate RULE CLASS group was removed as a duplicate):
/// clicking it toggles <see cref="AuditViewModel.ToggleCategoryCommand"/>, and
/// <see cref="IsActive"/> mirrors membership in the VM's <c>_ruleClassFilter</c> set (the single
/// source of truth — exactly like <see cref="AuditFilterChip.IsActive"/>), so the row's active
/// affordance survives a summary rebuild. Rows are emitted in the canonical rule order
/// (<see cref="Ruleset.EnumerateRules"/>); see <see cref="AuditViewModel.Categories"/>.
/// </summary>
public sealed partial class AuditCategoryRow : ObservableObject
{
    public AuditCategoryRow(string ruleId, string displayName, int count, RuleSeverity maxSeverity)
    {
        RuleId = ruleId;
        DisplayName = displayName;
        Count = count;
        MaxSeverity = maxSeverity;
    }

    /// <summary>The rule id this row filters on (the <c>_ruleClassFilter</c> key, OrdinalIgnoreCase).</summary>
    public string RuleId { get; }

    /// <summary>The human rule-class label (e.g. "Nesting matrix"); the row caption.</summary>
    public string DisplayName { get; }

    /// <summary>The finding count for this class (<see cref="AuditSummary.ByRuleClass"/>).</summary>
    public int Count { get; }

    /// <summary>The max severity across this class's findings — drives the leading dot colour.</summary>
    public RuleSeverity MaxSeverity { get; }

    /// <summary>True when this class is in the active rule-class filter set — the view renders the
    /// active visual (a redundant border/fill change, never hue alone; WCAG 1.4.1).</summary>
    [ObservableProperty]
    private bool _isActive;
}

/// <summary>The triage state of one audit finding (WP5e / ADR-028): <see cref="Open"/> = a live
/// finding, <see cref="Acknowledged"/>/<see cref="Suppressed"/> = covered by an equal-strength
/// tagged global-ignore entry (so it is OUT of the live report — health rises, graph/sidebar go
/// quiet — but still listed in the audit "would-be" table for reversibility). The two triaged
/// states differ only by the entry's note tag (a human annotation of intent), never by engine
/// strength.</summary>
public enum TriageStatus
{
    /// <summary>A live finding — no triage ignore entry covers it.</summary>
    Open,

    /// <summary>Covered by an <c>[ack]</c>-tagged ignore entry ("seen, accepted").</summary>
    Acknowledged,

    /// <summary>Covered by a <c>[suppress]</c>-tagged ignore entry ("intentionally hidden").</summary>
    Suppressed,
}

/// <summary>
/// One findings-table row (WP5d / #156, WP5e status): the projection of a would-be
/// <see cref="RuleViolation"/> the <see cref="Views.AuditView"/> table binds. The display fields are
/// immutable; only <see cref="IsSelected"/> mutates (the multi-select checkbox the WP5e bulk
/// Ack/Suppress/Untriage commands consume).
///
/// <para>Mirrors <see cref="ViolationRowModel"/>'s projection shape: severity (glyph color +
/// redundant letter via the one <see cref="Views.SeverityConverters"/> palette), the canonical
/// message, the snapshot-resolved object name (raw/External anchors fall back to the DN via the
/// shared <see cref="SubjectNameResolver"/>), and the jump anchor (<see cref="RuleViolation.PrimaryDn"/>).
/// <see cref="RuleClass"/> is the human rule-class label from <see cref="Ruleset.EnumerateRules"/>.
/// <see cref="Status"/> reflects whether a tagged triage ignore entry covers this finding (WP5e) —
/// the row stays listed even when Acknowledged/Suppressed so the triage is visible + reversible.</para>
/// </summary>
public sealed partial class AuditFindingRowModel : ObservableObject
{
    public AuditFindingRowModel(
        int reportOrder,
        RuleViolation violation,
        string objectName,
        string ruleClass,
        TriageStatus status)
    {
        ReportOrder = reportOrder;
        Violation = violation;
        Severity = violation.Severity;
        Message = violation.Message;
        ObjectName = objectName;
        RuleClass = ruleClass;
        PrimaryDn = violation.PrimaryDn;
        RuleId = violation.RuleId;
        Status = status;
    }

    /// <summary>The underlying would-be <see cref="RuleViolation"/> this row projects (WP5f / #160).
    /// Carried (not bound) so the detail pane can read the FULL <see cref="RuleViolation.Dns"/> set —
    /// the row's <see cref="PrimaryDn"/> is only <c>Dns[0]</c>, but the nesting/circular remediation
    /// snippets need every endpoint. Structured fields only — never an arbitrary AD attribute.</summary>
    public RuleViolation Violation { get; }

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

    /// <summary>The finding's rule id — carried into the <see cref="TriageRequest"/> the bulk/per-row
    /// commands build (the match key is escaped DN + tag; the rule id rides along for the note).</summary>
    public string RuleId { get; }

    /// <summary>Triage status (WP5e): Open, Acknowledged, or Suppressed — set at projection from the
    /// live ruleset's tagged ignore entries (<see cref="TriageEntry.StatusFor"/>).</summary>
    public TriageStatus Status { get; }

    /// <summary>True when this finding is already triaged (Acknowledged or Suppressed) — the view
    /// mutes the row + the bulk Un-triage command targets it; Open rows are the Ack/Suppress targets.</summary>
    public bool IsTriaged => Status != TriageStatus.Open;

    /// <summary>The status pill caption (WCAG-inked in the view): "Open" / "Acknowledged" / "Suppressed".</summary>
    public string StatusLabel => Status switch
    {
        TriageStatus.Acknowledged => "Acknowledged",
        TriageStatus.Suppressed => "Suppressed",
        _ => "Open",
    };

    /// <summary>Multi-select checkbox state (WP5d). The selection bar tallies it; WP5e's bulk
    /// Ack/Suppress will act on the selected rows. The VM owns the toggled notifications via the
    /// per-row <c>PropertyChanged</c> subscription so "{n} selected" stays live.</summary>
    [ObservableProperty]
    private bool _isSelected;
}

/// <summary>The axis a <see cref="AuditFilterChip"/> filters on (WP1). Each axis is an independent
/// fail-open constraint: an EMPTY active set on an axis imposes no constraint; a non-empty set keeps
/// only rows whose value is in the set. The chip axes AND together (the rule-class axis is driven by
/// the categories pane, not a chip — WP-C/#180).</summary>
public enum AuditFilterAxis
{
    /// <summary>Filter by <see cref="AuditFindingRowModel.Severity"/> (boxed <see cref="RuleSeverity"/> key).</summary>
    Severity,

    /// <summary>Filter by <see cref="AuditFindingRowModel.Status"/> (boxed <see cref="TriageStatus"/> key).</summary>
    Status,
}

/// <summary>
/// One filter chip on the audit findings filter strip (WP1): a toggleable facet on a
/// <see cref="AuditFilterAxis"/> axis (Severity or Status; the rule-class facet is the categories
/// pane — WP-C/#180). Severity chips carry a non-null <see cref="Severity"/> (drives the glyph
/// color/letter via <see cref="Views.SeverityConverters"/>); status chips leave it null and show
/// their <see cref="Label"/> + <see cref="Count"/>. <see cref="Key"/> is the boxed value added to /
/// removed from the axis's filter set, and <see cref="IsActive"/> mirrors set membership (the view's
/// redundant non-color active affordance binds it).
/// </summary>
public sealed partial class AuditFilterChip : ObservableObject
{
    public AuditFilterChip(AuditFilterAxis axis, object key, string label, int count, RuleSeverity? severity)
    {
        Axis = axis;
        Key = key;
        Label = label;
        Count = count;
        Severity = severity;
    }

    /// <summary>The axis this chip toggles (selects which of the VM's filter sets <see cref="Key"/>
    /// is added to / removed from).</summary>
    internal AuditFilterAxis Axis { get; }

    /// <summary>The boxed value this chip contributes to its axis's filter set: a boxed
    /// <see cref="RuleSeverity"/> (severity) or a boxed <see cref="TriageStatus"/> (status).</summary>
    internal object Key { get; }

    /// <summary>The chip caption (severity letter source / status name).</summary>
    public string Label { get; }

    /// <summary>The number of master-list rows this chip would match, computed over <c>_allRows</c>.</summary>
    public int Count { get; }

    /// <summary>Non-null only for severity chips — drives the colored glyph square + redundant letter
    /// (<see cref="Views.SeverityConverters"/>); null for status chips.</summary>
    public RuleSeverity? Severity { get; }

    /// <summary>True when this chip's <see cref="Key"/> is in its axis's active filter set — the view
    /// renders the active visual (a non-color-only border/fill change) via a conditional class.</summary>
    [ObservableProperty]
    private bool _isActive;
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

    /// <summary>The current LIVE rule report (post-suppression) — borrowed at construction, replaced
    /// in place by <see cref="ApplyRuleset"/> (a settings Apply/Save re-thread). Drives
    /// <see cref="Summary"/> / health — ack'd AND suppressed findings are BOTH absent from it.</summary>
    private RuleReport _report;

    /// <summary>The would-be report (WP5e / ADR-028): the report the engine WOULD produce with the
    /// triage-tagged ignore entries removed (= the base ignore set). It drives the findings TABLE so
    /// triaged rows stay visible + reversible; per-row <see cref="TriageStatus"/> is then read from
    /// the LIVE ruleset's tagged entries. One extra cheap <see cref="RuleEngine.Evaluate"/> per
    /// ruleset change (accepted per the full-re-run rule-engine contract).</summary>
    private RuleReport _wouldBeReport;

    /// <summary>The shell-supplied triage seam (WP5e / ADR-028): the <see cref="AuditViewModel"/>
    /// hands it a batch of <see cref="TriageRequest"/>s; the SHELL appends/removes the global-ignore
    /// entries and routes them through the existing <c>SettingsViewModel</c> gate (the single write
    /// path — never AD). Dead until armed by <see cref="UseTriageCallback"/> (mirrors the Design-plan
    /// callback idiom), so a headless / un-wired audit never half-acts; the commands no-op when null.</summary>
    private Action<IReadOnlyList<TriageRequest>>? _triage;

    /// <summary>The connection summary threaded from the shell (WP2 / ADR-013 §2) for the HTML
    /// report header. Empty when un-threaded (e.g. the 5-arg test ctors); a non-empty value is used
    /// verbatim, an empty one falls back to a snapshot-derived line (<see cref="BuildReportHeader"/>).</summary>
    private readonly string _connectionSummary;

    /// <summary>Cancels an export dialog/write still in flight at <see cref="Dispose"/> (WP2 / ADR-013),
    /// so a save-picker open during teardown can never write after dispose. Mirrors the workspace's
    /// cancel-on-teardown <c>_cts</c>; the audit otherwise owns no cancellable work.</summary>
    private readonly CancellationTokenSource _cts = new();

    /// <summary>The export save-picker seam (WP2 / ADR-013 §5), installed by the shell via
    /// <see cref="UseExportFileDialogs"/> once a <see cref="Avalonia.Controls.TopLevel"/> exists.
    /// Dead (the Export CSV/HTML commands are disarmed) until installed — a headless / un-wired audit
    /// never opens a picker. Gates <see cref="CanExportReport"/>; the installer re-arms both commands
    /// (the plain-field + manual-notify idiom of <see cref="WorkspaceViewModel.UseExportFileDialogs"/>).</summary>
    private IExportFileDialogs? _exportDialogs;

    /// <summary>The dashboard health roll-up the view binds (WP5). Recomputed from
    /// <see cref="AuditSummary.Compute"/> at construction and on every <see cref="ApplyRuleset"/>;
    /// the bound count/score props re-notify in <see cref="OnSummaryChanged"/>.</summary>
    [ObservableProperty]
    private AuditSummary _summary;

    public AuditViewModel(
        DirectorySnapshot snapshot,
        RuleReport report,
        Ruleset ruleset,
        string rootDn,
        Action onBack,
        string connectionSummary = "")
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(report);
        ArgumentNullException.ThrowIfNull(ruleset);

        _snapshot = snapshot; // BORROWED — never disposed, never mutated.
        _report = report;
        _ruleset = ruleset;
        _wouldBeReport = ComputeWouldBeReport(snapshot, ruleset);
        RootDn = rootDn;
        _onBack = onBack;
        _connectionSummary = connectionSummary;

        _summary = AuditSummary.Compute(report, snapshot, ruleset);
        RebuildCategories();
        RebuildFindings();
    }

    /// <summary>Arms the shell's triage seam (WP5e / ADR-028): the install idiom mirrors the
    /// workspace Design-plan/Audit callbacks (<c>OnRootChosen</c>). Until called the Ack/Suppress/
    /// Un-triage commands are inert no-ops, so a renderer-less / headless audit never half-acts.
    /// Idempotent — the last writer wins.</summary>
    public void UseTriageCallback(Action<IReadOnlyList<TriageRequest>> triage) => _triage = triage;

    /// <summary>Installs the real export save-picker seam (WP2 / ADR-013 §5; mirrors
    /// <see cref="WorkspaceViewModel.UseExportFileDialogs"/>): the production <c>MainWindow</c>
    /// calls this from its own <see cref="Avalonia.Controls.TopLevel"/>
    /// (<c>StorageProviderExportFileDialogs</c>) once the audit step is current, so the export
    /// commands reach the OS picker. Headless tests inject a fake here. Re-arms both export commands
    /// (their gate includes <c>_exportDialogs is not null</c>); idempotent — the last writer wins.</summary>
    public void UseExportFileDialogs(IExportFileDialogs dialogs)
    {
        _exportDialogs = dialogs;
        ExportReportCsvCommand.NotifyCanExecuteChanged();
        ExportReportHtmlCommand.NotifyCanExecuteChanged();
    }

    /// <summary>The would-be report (ADR-028): re-evaluates over a clone of <paramref name="ruleset"/>
    /// whose global ignore list excludes the triage-tagged (<c>[ack]</c>/<c>[suppress]</c>) entries —
    /// so acknowledged + suppressed findings reappear in the TABLE (visible + reversible) while plain
    /// ignore entries still suppress as ever. Pure: never mutates the borrowed snapshot/ruleset.</summary>
    private static RuleReport ComputeWouldBeReport(DirectorySnapshot snapshot, Ruleset ruleset)
    {
        var baseIgnore = ruleset.Ignore.Where(e => TriageEntry.KindOf(e) is null).ToList();
        // No triage entries => the would-be report IS the live report; skip the extra Evaluate.
        if (baseIgnore.Count == ruleset.Ignore.Count)
        {
            return RuleEngine.Evaluate(snapshot, ruleset);
        }

        var untriaged = ruleset with { Ignore = baseIgnore };
        return RuleEngine.Evaluate(snapshot, untriaged);
    }

    /// <summary>The categories pane (WP5c): one row per rule class that produced findings, in the
    /// canonical <see cref="Ruleset.EnumerateRules"/> order (nesting → naming rules in file order →
    /// circular → empty-group), so the list never reshuffles by dictionary order. Each row IS the
    /// rule-class filter facet (WP-C/#180) — clicking it runs <see cref="ToggleCategory"/>; its
    /// <see cref="AuditCategoryRow.IsActive"/> mirrors the <c>_ruleClassFilter</c> set. Rebuilt in
    /// place on every <see cref="ApplyRuleset"/>.</summary>
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

    /// <summary>The ONE finding whose detail pane is shown (WP5f / #160): the findings ListBox's
    /// <c>SelectedItem</c> — the single ACTIVE row. This is DISTINCT from the multi-select triage
    /// checkboxes (<see cref="AuditFindingRowModel.IsSelected"/>): selecting a row for detail must
    /// NOT toggle any checkbox, and toggling a checkbox must not change this. <c>null</c> = nothing
    /// selected (the detail pane shows its empty-state hint). The view binds the ListBox
    /// <c>SelectedItem</c> two-way to this; <see cref="OnSelectedFindingChanged"/> re-projects
    /// <see cref="Detail"/>.</summary>
    [ObservableProperty]
    private AuditFindingRowModel? _selectedFinding;

    /// <summary>The read-only detail projection (header / what / why / how + the copy-only snippet) of
    /// <see cref="SelectedFinding"/>, or <c>null</c> when nothing is selected (WP5f / #160). Re-derived
    /// purely from the selected row's <see cref="AuditFindingRowModel.Violation"/> + resolved name — no
    /// AD, no arbitrary attributes; the snippet is INERT TEXT (never executed).</summary>
    [ObservableProperty]
    private AuditFindingDetail? _detail;

    /// <summary>The transient "Copied" affordance (WP5f / #160): the view sets it via
    /// <see cref="MarkSnippetCopied"/> after it writes the snippet to the CLIPBOARD (the only side
    /// effect — never an execution), and clears it when the active finding changes. Drives the Copy
    /// button's "Copied" caption swap. View-driven so the VM stays clipboard-free.</summary>
    [ObservableProperty]
    private bool _snippetCopied;

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

    /// <summary>True when a finding is selected for detail — gates the detail pane against its
    /// empty-state hint (WP5f / #160).</summary>
    public bool HasDetail => Detail is not null;

    /// <summary>Re-projects <see cref="Detail"/> when the active (detail) row changes (WP5f / #160).
    /// Pure: derives the detail from the selected row's borrowed <see cref="AuditFindingRowModel.Violation"/>
    /// — no AD, no provider, no mutation. Independent of the triage checkboxes. Also clears the transient
    /// "Copied" affordance so it never lingers onto the next finding.</summary>
    partial void OnSelectedFindingChanged(AuditFindingRowModel? value)
    {
        SnippetCopied = false;
        Detail = value is null
            ? null
            : AuditFindingDetail.From(value.Violation, value.ObjectName, value.RuleClass);
    }

    /// <summary>Re-gates the detail-pane visibility whenever the projection swaps.</summary>
    partial void OnDetailChanged(AuditFindingDetail? value) => OnPropertyChanged(nameof(HasDetail));

    /// <summary>The view calls this AFTER it has written the snippet to the clipboard (WP5f / #160):
    /// flips the transient "Copied" affordance. The VM never touches the clipboard itself and NEVER
    /// executes the snippet — copying is a pure clipboard write the view owns.</summary>
    public void MarkSnippetCopied() => SnippetCopied = true;

    /// <summary>A header click re-filters + re-orders the live <see cref="Findings"/> collection in place.</summary>
    partial void OnSortColumnChanged(AuditSortColumn value) => ApplyView();

    /// <summary>A direction flip re-filters + re-orders the live <see cref="Findings"/> collection in place.</summary>
    partial void OnSortDescendingChanged(bool value) => ApplyView();

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
                // IsActive mirrors the rule-class filter set so the active affordance survives a
                // summary rebuild (exactly like the filter chips do — WP-C/#180).
                Categories.Add(new AuditCategoryRow(rule.Id, rule.DisplayName, count, severity)
                {
                    IsActive = _ruleClassFilter.Contains(rule.Id),
                });
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
        // Detach the OLD master rows (the subscription lives on _allRows, not the visible view — WP1).
        foreach (var row in _allRows)
        {
            row.PropertyChanged -= OnFindingPropertyChanged;
        }

        // Rule id -> human class label (canonical EnumerateRules order); naming rules carry their id.
        var ruleClassById = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var rule in _ruleset.EnumerateRules())
        {
            ruleClassById[rule.Id] = rule.DisplayName;
        }

        _allRows.Clear();
        var reportOrder = 0;
        foreach (var violation in _wouldBeReport.Violations)
        {
            var name = SubjectNameResolver.Resolve(_snapshot, violation.PrimaryDn);
            var ruleClass = ruleClassById.TryGetValue(violation.RuleId, out var label) ? label : violation.RuleId;
            // Per-row status (ADR-028): match the finding's ESCAPED primary DN against the LIVE
            // ruleset's tagged ignore entries — a hit means a tagged entry covers it (ack/suppress).
            var escapedDn = TriageEntry.Escape(violation.PrimaryDn);
            var status = TriageEntry.StatusFor(_ruleset.Ignore, escapedDn) switch
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
            row.PropertyChanged += OnFindingPropertyChanged;
            _allRows.Add(row);
        }

        // A re-thread rebuilds the rows, so the prior detail selection no longer points at a live row;
        // clear it (the detail pane returns to its empty state). WP5f / #160.
        SelectedFinding = null;
        // Rebuild the chips AFTER _allRows (severity/status counts derive from it); this preserves the
        // severity/status sets and prunes the rule-class set to surviving rule ids. The rule-class
        // filter is driven by the categories pane (WP-C/#180), not a separate chip group.
        RebuildFilterChips();
        ApplyView();
    }

    /// <summary>Rebuilds the severity + status chip collections (WP1) from the freshly-built
    /// <see cref="_allRows"/> (both are fixed domains, counts over <see cref="_allRows"/>; their filter
    /// sets are PRESERVED). Also prunes the rule-class filter set to the rule ids still present so a
    /// stale id (after a re-thread dropped a class) never silently filters everything out — that set is
    /// now driven by the categories pane (WP-C/#180), so there is no rule-class chip collection. Each
    /// chip's <see cref="AuditFilterChip.IsActive"/> is set from the (preserved/pruned) sets.</summary>
    private void RebuildFilterChips()
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
    /// (<see cref="SortColumn"/>/<see cref="SortDescending"/>, <see cref="AuditSortColumn.None"/> = the
    /// canonical <see cref="RuleReport.Violations"/> report order, with the report-order index as every
    /// key's deterministic tie-break). Rows filtered OUT are deselected (a no-op on a pure sort — the
    /// visible set is unchanged — so selection survives sorting but never lingers on a hidden row).
    /// Rebuilds by in-place Clear/Add (the bound collection reference never changes).</summary>
    private void ApplyView()
    {
        // FILTER: a row passes iff it satisfies every NON-EMPTY axis (empty axis => no constraint).
        var filtered = _allRows.Where(r =>
            (_severityFilter.Count == 0 || _severityFilter.Contains(r.Severity))
            && (_statusFilter.Count == 0 || _statusFilter.Contains(r.Status))
            && (_ruleClassFilter.Count == 0 || _ruleClassFilter.Contains(r.RuleId)));

        // SORT: r.ReportOrder is the canonical report-projection rank — the None ordering and the
        // deterministic tie-break for every other column, independent of current row order.
        IEnumerable<AuditFindingRowModel> ordered = SortColumn switch
        {
            AuditSortColumn.Severity => SortDescending
                ? filtered.OrderByDescending(r => (int)r.Severity).ThenBy(r => r.ReportOrder)
                : filtered.OrderBy(r => (int)r.Severity).ThenBy(r => r.ReportOrder),
            AuditSortColumn.ObjectName => SortDescending
                ? filtered.OrderByDescending(r => r.ObjectName, StringComparer.OrdinalIgnoreCase).ThenBy(r => r.ReportOrder)
                : filtered.OrderBy(r => r.ObjectName, StringComparer.OrdinalIgnoreCase).ThenBy(r => r.ReportOrder),
            AuditSortColumn.RuleClass => SortDescending
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

        SelectedCount = Findings.Count(r => r.IsSelected);
        OnPropertyChanged(nameof(HasFindings));
        OnPropertyChanged(nameof(AllSelected));
        OnPropertyChanged(nameof(VisibleCount));
        OnPropertyChanged(nameof(TotalCount));
        OnPropertyChanged(nameof(IsFiltered));
        OnPropertyChanged(nameof(FilterSummary));
        OnPropertyChanged(nameof(HasNoMatches));
        OnPropertyChanged(nameof(IsAllClear));
    }

    /// <summary>Toggles one filter chip (WP1): flips its <see cref="AuditFilterChip.IsActive"/>, adds/
    /// removes its <see cref="AuditFilterChip.Key"/> in the set selected by its
    /// <see cref="AuditFilterChip.Axis"/>, and rebuilds the visible view. The chips multi-select within
    /// AND across axes (each axis is independent + fail-open).</summary>
    [RelayCommand]
    private void ToggleFilter(AuditFilterChip chip)
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

        ApplyView();
    }

    /// <summary>Toggles the rule-class filter from a categories-pane row (WP-C/#180): flips the row's
    /// <see cref="AuditCategoryRow.IsActive"/>, adds/removes its <see cref="AuditCategoryRow.RuleId"/>
    /// in <c>_ruleClassFilter</c>, and rebuilds the visible view. The rule-class axis is the single
    /// source of truth here — the category rows ARE the rule-class filter facet (the separate strip
    /// chip group was removed as a duplicate). Multi-select within the axis; ANDs with severity/status.</summary>
    [RelayCommand]
    private void ToggleCategory(AuditCategoryRow row)
    {
        row.IsActive = !row.IsActive;
        ToggleKey(_ruleClassFilter, row.RuleId, row.IsActive);
        ApplyView();
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
    [RelayCommand]
    private void ClearFilters()
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

        ApplyView();
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
            SortColumn = column; // Triggers ApplyView via OnSortColumnChanged.
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

    /// <summary>Acknowledge every SELECTED open finding (WP5e / ADR-028): emits one
    /// <c>[ack]</c> triage request per selected Open row through the shell seam (the gate appends the
    /// tagged ignore entries → RulesetApplied re-threads → the rows re-project as Acknowledged + the
    /// findings drop from the live health report). Already-triaged selected rows are skipped (no-op).</summary>
    [RelayCommand]
    private void AcknowledgeSelected() => TriageSelected(TriageKind.Acknowledge);

    /// <summary>Suppress every SELECTED open finding (WP5e / ADR-028): the <c>[suppress]</c>
    /// twin of <see cref="AcknowledgeSelected"/> — equal engine strength, different note tag.</summary>
    [RelayCommand]
    private void SuppressSelected() => TriageSelected(TriageKind.Suppress);

    /// <summary>Reverse triage on every SELECTED triaged finding (WP5e / ADR-028): emits one
    /// Un-triage request per selected Acknowledged/Suppressed row (the gate removes the matching
    /// tagged ignore entry → the finding reappears as Open + re-enters the live report). Open rows
    /// are skipped.</summary>
    [RelayCommand]
    private void UntriageSelected()
    {
        var requests = Findings
            .Where(r => r.IsSelected && r.IsTriaged)
            .Select(r => new TriageRequest(TriageEntry.Escape(r.PrimaryDn), r.RuleId, TriageKind.Untriage, null))
            .ToList();
        Submit(requests);
    }

    /// <summary>Acknowledge a single open finding (WP5e): the per-row affordance equivalent of
    /// <see cref="AcknowledgeSelected"/> for <paramref name="row"/>.</summary>
    [RelayCommand]
    private void AcknowledgeRow(AuditFindingRowModel row) => TriageRows([row], TriageKind.Acknowledge);

    /// <summary>Suppress a single open finding (WP5e): the per-row equivalent of
    /// <see cref="SuppressSelected"/> for <paramref name="row"/>.</summary>
    [RelayCommand]
    private void SuppressRow(AuditFindingRowModel row) => TriageRows([row], TriageKind.Suppress);

    /// <summary>Reverse triage on a single triaged finding (WP5e): the per-row equivalent of
    /// <see cref="UntriageSelected"/> for <paramref name="row"/>.</summary>
    [RelayCommand]
    private void UntriageRow(AuditFindingRowModel row)
    {
        if (row.IsTriaged)
        {
            Submit([new TriageRequest(TriageEntry.Escape(row.PrimaryDn), row.RuleId, TriageKind.Untriage, null)]);
        }
    }

    /// <summary>Builds an Ack/Suppress batch over the SELECTED Open rows and submits it.</summary>
    private void TriageSelected(TriageKind kind) =>
        TriageRows(Findings.Where(r => r.IsSelected).ToList(), kind);

    /// <summary>Builds an Ack/Suppress batch over the OPEN rows in <paramref name="rows"/> (triaged
    /// rows are skipped — re-tagging an already-ignored finding is a no-op) and submits it through
    /// the shell seam. The DN is glob-escaped so a single-object ignore stays exact.</summary>
    private void TriageRows(IReadOnlyList<AuditFindingRowModel> rows, TriageKind kind)
    {
        var requests = rows
            .Where(r => !r.IsTriaged)
            .Select(r => new TriageRequest(TriageEntry.Escape(r.PrimaryDn), r.RuleId, kind, null))
            .ToList();
        Submit(requests);
    }

    /// <summary>Hands a non-empty triage batch to the shell seam (WP5e). The shell owns the gate; a
    /// null seam (un-armed / headless) or empty batch is a no-op — never a parallel write path.</summary>
    private void Submit(IReadOnlyList<TriageRequest> requests)
    {
        if (requests.Count > 0)
        {
            _triage?.Invoke(requests);
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
        // WP5e / ADR-028: the table shows the WOULD-BE findings (triage-tagged ignores removed) so
        // triaged rows stay listed + reversible; the LIVE report drives health. Recompute both, then
        // Summary's setter re-projects the table (RebuildFindings reads _wouldBeReport + status).
        _wouldBeReport = ComputeWouldBeReport(_snapshot, _ruleset);
        Summary = AuditSummary.Compute(_report, _snapshot, _ruleset);
    }

    /// <summary>
    /// Exports the LIVE post-suppression report (<see cref="_report"/> — the same findings the
    /// health score + sidebar show; ack'd/suppressed findings are intentionally absent, NOT the
    /// would-be table) as RFC-4180 CSV to a user-picked path (WP2 / ADR-013 §2/§5/§6). Re-guards in
    /// the body (a stale-armed Execute ignores CanExecute), picks via the seam, and on a non-null
    /// pick writes the pure-Core <see cref="ViolationReportExporter.ToCsv"/> output (UTF-8, no BOM)
    /// to ONLY that path. Read-only toward AD: the only write target is the picked local file; the
    /// name closure reads the borrowed snapshot only (never a provider call).
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanExportReport))]
    private async Task ExportReportCsvAsync()
    {
        if (IsDisposed || _exportDialogs is null)
        {
            return;
        }

        var path = await _exportDialogs.PickSavePathAsync(ExportKind.Csv, _cts.Token);
        if (path is null || IsDisposed)
        {
            return;
        }

        var csv = ViolationReportExporter.ToCsv(_report, ResolveName);
        await WriteUtf8Async(path, csv, _cts.Token);
    }

    /// <summary>Exports the LIVE post-suppression report as a self-contained HTML file to a
    /// user-picked path (WP2 / ADR-013 §2/§5/§6). Same gate, re-guard, pick and write-once discipline
    /// as <see cref="ExportReportCsvAsync"/>; the HTML carries a <see cref="ReportHeader"/> from
    /// the audit root identity + connection summary + the current wall clock
    /// (<see cref="BuildReportHeader"/>).</summary>
    [RelayCommand(CanExecute = nameof(CanExportReport))]
    private async Task ExportReportHtmlAsync()
    {
        if (IsDisposed || _exportDialogs is null)
        {
            return;
        }

        var path = await _exportDialogs.PickSavePathAsync(ExportKind.Html, _cts.Token);
        if (path is null || IsDisposed)
        {
            return;
        }

        var html = ViolationReportExporter.ToHtml(_report, ResolveName, BuildReportHeader());
        await WriteUtf8Async(path, html, _cts.Token);
    }

    /// <summary>Armed iff the export seam is installed and the step is live (WP2 / ADR-013 §6). The
    /// borrowed snapshot is non-null by ctor contract and there is no loading state, so the seam +
    /// not-disposed are the only gates — pre-install the commands are inert.</summary>
    private bool CanExportReport() => !IsDisposed && _exportDialogs is not null;

    /// <summary>The name-resolution closure handed to the exporter — identical to the findings table
    /// + sidebar: an in-snapshot object resolves to its <c>Name</c>, an absent DN falls back to the
    /// DN itself (never a provider call, so export stays read-only toward AD). Core stays App-free
    /// (ADR-013 §2): it takes this delegate, never the snapshot.</summary>
    private string ResolveName(string dn) => SubjectNameResolver.Resolve(_snapshot, dn);

    /// <summary>Builds the HTML report header from the audit's identity (ADR-013 §2): root DN + the
    /// snapshot-resolved root name + the shell-threaded connection summary (an empty one falls back to
    /// a snapshot-object-count line, so an un-threaded audit still produces an honest header), with
    /// <see cref="DateTimeOffset.Now"/> as the injected generation timestamp.</summary>
    private ReportHeader BuildReportHeader()
    {
        var rootName = SubjectNameResolver.Resolve(_snapshot, RootDn);
        var summary = string.IsNullOrEmpty(_connectionSummary)
            ? $"{_snapshot.Objects.Count} objects loaded"
            : _connectionSummary;
        return new ReportHeader(RootDn, rootName, summary, DateTimeOffset.Now);
    }

    /// <summary>Writes <paramref name="content"/> to <paramref name="path"/> as UTF-8 WITHOUT a BOM —
    /// the exact bytes the CSV/HTML exporter pinned tests expect (mirrors
    /// <see cref="WorkspaceViewModel.WriteUtf8Async"/>).</summary>
    private static Task WriteUtf8Async(string path, string content, CancellationToken cancellationToken) =>
        File.WriteAllTextAsync(path, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), cancellationToken);

    /// <summary>Back to the Ist workspace (mirrors <see cref="GapViewModel.BackCommand"/>):
    /// invokes the shell-supplied callback, which restores the SAME workspace instance and
    /// disposes this audit step.</summary>
    [RelayCommand]
    private void Back() => _onBack.Invoke();

    /// <summary>"Show in graph" (WP5 v1): returning to the workspace IS the graph view, so it
    /// routes through the same Back callback. A richer in-graph projection is a later sub-slice.</summary>
    [RelayCommand]
    private void ShowInGraph() => _onBack.Invoke();

    /// <summary>Dispose: flips <see cref="IsDisposed"/> and cancels+disposes <see cref="_cts"/> so an
    /// export save-picker / write still in flight at teardown is cancelled and can never write after
    /// dispose (WP2 / ADR-013). Idempotent; NEVER disposes nor mutates the borrowed Ist snapshot /
    /// report / ruleset (the audit owns no renderer — there is nothing else to tear down).</summary>
    public void Dispose()
    {
        if (IsDisposed)
        {
            return;
        }

        IsDisposed = true;
        _cts.Cancel();
        _cts.Dispose();
    }
}
