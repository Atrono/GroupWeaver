using System.Collections.ObjectModel;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using GroupWeaver.App.Audit;
using GroupWeaver.App.Export;
using GroupWeaver.App.Settings;
using GroupWeaver.Core.Audit;
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

    /// <summary>The findings-table + categories-pane + filter-chips collaborator (#297): owns the WP1/
    /// WP5c/WP5d master list, filter axes, sort/filter projection and per-row selection bookkeeping.
    /// Sort state, <see cref="Summary"/> and the reports/snapshot stay VM-owned and are passed in per
    /// call. See <see cref="AuditFindingsView"/>.</summary>
    private readonly AuditFindingsView _findingsView = new();

    /// <summary>The Ack/Suppress/Untriage batch-building + shell-seam collaborator (#299): owns the
    /// WP5e triage callback seam and the request-building logic the triage commands delegate to. See
    /// <see cref="AuditTriageCoordinator"/>.</summary>
    private readonly AuditTriageCoordinator _triageCoordinator = new();

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

    /// <summary>The connection summary threaded from the shell (WP2 / ADR-013 §2) for the HTML
    /// report header. Empty when un-threaded (e.g. the 5-arg test ctors); a non-empty value is used
    /// verbatim, an empty one falls back to a snapshot-derived line (<see cref="BuildReportHeader"/>).</summary>
    private readonly string _connectionSummary;

    /// <summary>The export save-picker seam + cancel-on-teardown mechanics (WP2 / ADR-013 §5, extracted
    /// to <see cref="Export.AuditExportService"/>), installed by the shell via
    /// <see cref="UseExportFileDialogs"/> once a <see cref="Avalonia.Controls.TopLevel"/> exists. Dead
    /// (the Export CSV/HTML commands are disarmed) until installed — a headless / un-wired audit never
    /// opens a picker. Gates <see cref="CanExportReport"/>; the installer re-arms both commands (the
    /// plain-field + manual-notify idiom of <see cref="WorkspaceViewModel.UseExportFileDialogs"/>).</summary>
    private readonly AuditExportService _exportService = new();

    /// <summary>The run-history store seam (ADR-032 D2 / #190), installed by the shell via
    /// <see cref="UseRunStore"/> once the audit step is current. Dead (the Save-run + Compare commands
    /// are disarmed) until installed — a headless / un-wired audit never writes a run nor reads the
    /// runs directory. The ONLY write target is <c>%APPDATA%\GroupWeaver\runs\</c> — never AD.</summary>
    private AuditRunStore? _runStore;

    /// <summary>The UI-preference store seam (WP "persist view state"), installed by the shell via
    /// <see cref="UseUiStateStore"/> when the audit step is entered. Dead (no restore, no persist) until
    /// installed — a headless / un-wired audit behaves exactly as before (empty filters, canonical
    /// order). The ONLY write target is <c>%APPDATA%\GroupWeaver\ui-state.json</c> — never AD; mirrors
    /// the rail's <see cref="WorkspaceViewModel.PersistUiState"/> read-modify-write best-effort idiom.</summary>
    private UiStateStore? _uiStateStore;

    /// <summary>Suppresses the persist-on-change hooks while <see cref="UseUiStateStore"/> applies the
    /// just-loaded values (mirrors <see cref="WorkspaceViewModel"/>'s ctor-seeding guard): restoring the
    /// persisted filters/sort must not write them straight back. Cleared once restore completes.</summary>
    private bool _restoring;

    /// <summary>The injected, deterministic run-stamp clock (ADR-032 / #190): the saved
    /// <see cref="AuditRun.Timestamp"/> comes from this, so Core never reads an ambient clock and a
    /// test pins the instant. Defaults to <c>() =&gt; DateTimeOffset.Now</c>.</summary>
    private readonly Func<DateTimeOffset> _clock;

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
        string connectionSummary = "",
        Func<DateTimeOffset>? clock = null)
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
        // The injected, deterministic run-stamp clock (ADR-032 / #190): the saved AuditRun's Timestamp
        // comes from this, never an ambient clock read inside Core. Defaults to the wall clock; a test
        // injects a fixed instant. Core stays clock-free — the App supplies the instant.
        _clock = clock ?? (() => DateTimeOffset.Now);

        _findingsView.ViewChanged += OnFindingsViewChanged;
        _findingsView.CategoriesChanged += () => OnPropertyChanged(nameof(HasCategories));
        _findingsView.SelectionChanged += () => SelectedCount = _findingsView.SelectedCount;

        _summary = AuditSummary.Compute(report, snapshot, ruleset);
        RebuildCategories();
        RebuildFindings();
    }

    /// <summary>Arms the shell's triage seam (WP5e / ADR-028): the install idiom mirrors the
    /// workspace Design-plan/Audit callbacks (<c>OnRootChosen</c>). Until called the Ack/Suppress/
    /// Un-triage commands are inert no-ops, so a renderer-less / headless audit never half-acts.
    /// Idempotent — the last writer wins.</summary>
    public void UseTriageCallback(Action<IReadOnlyList<TriageRequest>> triage) =>
        _triageCoordinator.UseTriageCallback(triage);

    /// <summary>Installs the real export save-picker seam (WP2 / ADR-013 §5; mirrors
    /// <see cref="WorkspaceViewModel.UseExportFileDialogs"/>): the production <c>MainWindow</c>
    /// calls this from its own <see cref="Avalonia.Controls.TopLevel"/>
    /// (<c>StorageProviderExportFileDialogs</c>) once the audit step is current, so the export
    /// commands reach the OS picker. Headless tests inject a fake here. Re-arms both export commands
    /// (their gate includes <c>_exportDialogs is not null</c>); idempotent — the last writer wins.</summary>
    public void UseExportFileDialogs(IExportFileDialogs dialogs)
    {
        _exportService.UseDialogs(dialogs);
        ExportReportCsvCommand.NotifyCanExecuteChanged();
        ExportReportHtmlCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Installs the audit run-history store seam (ADR-032 D2 / #190; mirrors
    /// <see cref="UseExportFileDialogs"/>): the production shell injects the real
    /// <c>%APPDATA%\GroupWeaver\runs\</c>-backed <see cref="AuditRunStore"/>; a headless test injects a
    /// temp-dir-backed one. Dead (the Save-run + Compare commands disarmed) until installed. Re-arms
    /// both commands and re-projects <see cref="CanCompare"/>; idempotent — the last writer wins.</summary>
    public void UseRunStore(AuditRunStore runStore)
    {
        _runStore = runStore;
        SaveRunCommand.NotifyCanExecuteChanged();
        CompareToPreviousRunCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(CanCompare));
    }

    /// <summary>Installs the UI-preference store seam (WP "persist view state"; mirrors
    /// <see cref="UseExportFileDialogs"/>/<see cref="UseRunStore"/>): the production shell injects its
    /// shared <see cref="UiStateStore"/> when the audit step is entered. On install it RESTORES the
    /// persisted Audit filters + sort from <see cref="UiStateStore.Load"/> (the VM is built fresh each
    /// time the step opens, so this is the only restore hook), then re-projects the view so the chips +
    /// table reflect it. Thereafter every filter toggle / clear / sort PERSISTS through the store
    /// (read-modify-write, best-effort — <see cref="UiStateStore.Save"/> never throws). A name that no
    /// longer parses (a renamed enum / removed value) is IGNORED — the never-throw load contract; a stale
    /// rule id self-heals via the <see cref="RebuildFilterChips"/> prune. Idempotent: the last writer
    /// wins, and re-installing re-restores (harmless — the same values reapply). Until called the audit
    /// neither restores nor persists (headless / un-wired = today's behaviour).</summary>
    public void UseUiStateStore(UiStateStore store)
    {
        _uiStateStore = store;
        RestoreView(store.Load());
    }

    /// <summary>Applies the persisted Audit filters + sort to the live sets / sort props, then
    /// re-projects (rebuild chips so their <see cref="AuditFilterChip.IsActive"/> mirrors the restored
    /// sets, then <see cref="ApplyView"/> so the table + categories reflect them). Names that don't
    /// parse are skipped (never throws); the stale rule-id prune in <see cref="RebuildFilterChips"/>
    /// self-heals an obsolete class id. Guarded by <see cref="_restoring"/> so the persist-on-change
    /// hooks don't write the just-loaded values straight back.</summary>
    private void RestoreView(UiState state)
    {
        _restoring = true;
        try
        {
            _findingsView.RestoreFilters(
                ParseAll<RuleSeverity>(state.AuditSeverityFilter),
                ParseAll<TriageStatus>(state.AuditStatusFilter),
                state.AuditRuleClassFilter);

            SortColumn = Enum.TryParse<AuditSortColumn>(state.AuditSortColumn, ignoreCase: true, out var column)
                ? column
                : AuditSortColumn.None;
            SortDescending = state.AuditSortDescending;
        }
        finally
        {
            _restoring = false;
        }

        // Re-project so the chips/categories active state + the table order reflect the restored values.
        // RebuildFilterChips also prunes a stale rule-class id to the surviving ids (self-healing).
        _findingsView.RebuildFilterChips();
        RebuildCategories();
        ApplyView();
    }

    /// <summary>Parses every name in <paramref name="names"/> as a <typeparamref name="T"/>, skipping
    /// anything that doesn't parse (never throws — a renamed enum / removed value is silently dropped,
    /// the same never-throw load contract as the rest of <see cref="RestoreView"/>).</summary>
    private static List<T> ParseAll<T>(IEnumerable<string> names)
        where T : struct, Enum
    {
        var results = new List<T>();
        foreach (var name in names)
        {
            if (Enum.TryParse<T>(name, ignoreCase: true, out var value))
            {
                results.Add(value);
            }
        }

        return results;
    }

    /// <summary>Writes the current Audit filters + sort to the shared UI-state store (read-modify-write,
    /// best-effort — <see cref="UiStateStore.Save"/> never throws). Preserves every field the rail /
    /// theme own (load then mutate only the Audit fields), exactly like
    /// <see cref="WorkspaceViewModel.PersistUiState"/>. A no-op when no store is installed (headless /
    /// un-wired) or while <see cref="_restoring"/> the just-loaded values.</summary>
    private void PersistView()
    {
        if (_uiStateStore is null || _restoring)
        {
            return;
        }

        _uiStateStore.Save(_uiStateStore.Load() with
        {
            AuditSeverityFilter = _findingsView.SeverityFilter.Select(s => s.ToString()).ToList(),
            AuditStatusFilter = _findingsView.StatusFilter.Select(s => s.ToString()).ToList(),
            AuditRuleClassFilter = _findingsView.RuleClassFilter.ToList(),
            AuditSortColumn = SortColumn.ToString(),
            AuditSortDescending = SortDescending,
        });
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
    public ObservableCollection<AuditCategoryRow> Categories => _findingsView.Categories;

    /// <summary>True when at least one rule class produced a finding — gates the categories pane
    /// against an empty list (an all-clear scope shows no category rows).</summary>
    public bool HasCategories => _findingsView.HasCategories;

    /// <summary>The findings table (WP5d): one <see cref="AuditFindingRowModel"/> per
    /// <see cref="RuleReport.Violations"/> entry, projected with snapshot-resolved names. Ordered
    /// by <see cref="SortColumn"/>/<see cref="SortDescending"/> (default = canonical report order).
    /// Rebuilt in place on every <see cref="ApplyRuleset"/> and re-ordered on every header sort.</summary>
    public ObservableCollection<AuditFindingRowModel> Findings => _findingsView.Findings;

    /// <summary>True when there is at least one finding — gates the table against the all-clear
    /// empty state (mirrors <see cref="HasCategories"/>).</summary>
    public bool HasFindings => _findingsView.HasFindings;

    /// <summary>The severity filter chips (WP1) — fixed Error/Warning/Info, in descending severity
    /// order. Each chip's <see cref="AuditFilterChip.Count"/> is computed over the master list; the
    /// <see cref="AuditFilterChip.Severity"/> drives the colored glyph. Rebuilt in place after the
    /// master list is built.</summary>
    public ObservableCollection<AuditFilterChip> SeverityChips => _findingsView.SeverityChips;

    /// <summary>The triage-status filter chips (WP1) — fixed Open/Acknowledged/Suppressed.</summary>
    public ObservableCollection<AuditFilterChip> StatusChips => _findingsView.StatusChips;

    /// <summary>The count of currently-visible (filtered) findings (= <see cref="Findings"/>.Count).</summary>
    public int VisibleCount => _findingsView.VisibleCount;

    /// <summary>The total count of findings before filtering.</summary>
    public int TotalCount => _findingsView.TotalCount;

    /// <summary>True when any axis has an active constraint — gates the "Clear filters" button + the
    /// "Showing N of M" wording.</summary>
    public bool IsFiltered => _findingsView.IsFiltered;

    /// <summary>The filter-strip summary line: "Showing {VisibleCount} of {TotalCount}" while filtered,
    /// else "{TotalCount} findings".</summary>
    public string FilterSummary => _findingsView.FilterSummary;

    /// <summary>True when findings exist but the active filters hide them all — gates the dedicated
    /// "no matches" empty state, DISTINCT from the all-clear empty state.</summary>
    public bool HasNoMatches => _findingsView.HasNoMatches;

    /// <summary>True when the scope produced NO findings at all (the master list is empty) — gates the
    /// all-clear "No findings to list." text apart from the filtered <see cref="HasNoMatches"/> state.</summary>
    public bool IsAllClear => _findingsView.IsAllClear;

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

    /// <summary>The run-drift comparison the compare section binds (ADR-032 D4 / #190): the four
    /// buckets + honesty/hash banners of the live findings against the most recent prior saved run for
    /// <see cref="RootDn"/>. <c>null</c> until <see cref="CompareToPreviousRun"/> runs (or when no prior
    /// run exists — gated by <see cref="HasComparison"/>). A pure projection of
    /// <see cref="AuditRunDiff.Compute"/>; never persisted, never touches AD.</summary>
    [ObservableProperty]
    private AuditRunComparison? _comparison;

    /// <summary>The transient "Saved" affordance after a successful <see cref="SaveRun"/>: the view shows
    /// the written file confirmation. Cleared whenever the report changes (a re-thread invalidates it).</summary>
    [ObservableProperty]
    private bool _runSaved;

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

    /// <summary>The screen-reader label for the health ring (WCAG 1.1.1 / 4.1.2): restates the gated
    /// verdict so the decorative ring is not the sole channel. ADR-030 (#188): when a live Error gates
    /// the band to "Action required" it leads with that verdict + the Critical count (the band is the
    /// headline, the diluted score is secondary); otherwise the score+band phrasing.</summary>
    public string HealthAutomationName => Critical > 0
        ? $"Directory health: action required, {Critical} critical"
        : $"Directory health {Score} of 100, {Band}";

    /// <summary>True when unexpanded areas remain unchecked (<see cref="AuditSummary.UncheckedPresent"/>):
    /// gates the honest "the score covers checked objects only" caveat so the score never implies a
    /// clean bill over unexpanded subtrees.</summary>
    public bool UncheckedPresent => Summary.UncheckedPresent;

    /// <summary>The number of findings excluded from the LIVE health report by triage (ADR-030 D2 /
    /// #188): the would-be report (triage-tagged ignores removed) minus the live report. Computed
    /// App-side from the two reports the VM already holds — <see cref="AuditSummary.Compute"/> stays
    /// pure over the live report and never learns the triage tag grammar (the ADR-028 boundary).
    /// Never negative (the would-be set is a superset of the live set). Clamped belt-and-braces.</summary>
    public int TriagedCount => Math.Max(0, _wouldBeReport.Violations.Count - _report.Violations.Count);

    /// <summary>True when at least one finding is triaged (acknowledged/suppressed) out of the live
    /// report — gates the D2 triage caveat beside the band (parallel to <see cref="UncheckedPresent"/>).</summary>
    public bool HasTriaged => TriagedCount > 0;

    /// <summary>The D2 triage-caveat sentence (ADR-030 / #188), pluralized in the VM so "1 finding" reads
    /// grammatically where a raw <c>StringFormat</c> would emit "1 findings". Only shown when
    /// <see cref="HasTriaged"/>; the rest of the sentence is identical for any count.</summary>
    public string TriageCaveatText =>
        $"{TriagedCount} {(TriagedCount == 1 ? "finding" : "findings")} acknowledged/suppressed — excluded from this score.";

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
        OnPropertyChanged(nameof(TriagedCount));
        OnPropertyChanged(nameof(HasTriaged));
        OnPropertyChanged(nameof(TriageCaveatText));
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

    /// <summary>True once a run-history store is installed (ADR-032 / #190) — gates the Save-run +
    /// Compare affordances against a headless / un-wired audit (where the store seam is null).</summary>
    public bool CanCompare => _runStore is not null;

    /// <summary>True when a run comparison has been computed — gates the compare section against its
    /// empty/hint state (ADR-032 D4).</summary>
    public bool HasComparison => Comparison is not null;

    /// <summary>Re-gates the compare section + re-projects the no-comparison hint when the projection
    /// swaps (ADR-032 D4).</summary>
    partial void OnComparisonChanged(AuditRunComparison? value) => OnPropertyChanged(nameof(HasComparison));

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

    /// <summary>A header click re-filters + re-orders the live <see cref="Findings"/> collection in place
    /// and persists the sort (best-effort — a no-op while restoring / un-wired).</summary>
    partial void OnSortColumnChanged(AuditSortColumn value)
    {
        ApplyView();
        PersistView();
    }

    /// <summary>A direction flip re-filters + re-orders the live <see cref="Findings"/> collection in place
    /// and persists the sort (best-effort — a no-op while restoring / un-wired).</summary>
    partial void OnSortDescendingChanged(bool value)
    {
        ApplyView();
        PersistView();
    }

    /// <summary>Repopulates <see cref="Categories"/> in place from the current <see cref="Summary"/> +
    /// report — delegates to <see cref="_findingsView"/> (<see cref="AuditFindingsView.RebuildCategories"/>).</summary>
    private void RebuildCategories() => _findingsView.RebuildCategories(_report, _ruleset, Summary);

    /// <summary>Repopulates <see cref="Findings"/> in place from the current would-be report —
    /// delegates to <see cref="_findingsView"/> (<see cref="AuditFindingsView.RebuildFindings"/>), then
    /// applies the cross-cluster resets a re-thread must ALSO invalidate: the detail-pane selection
    /// (WP5f / #160) and the run-compare projection + "saved" affordance (ADR-032 D4) — none of these
    /// belong to the findings-view collaborator.</summary>
    private void RebuildFindings()
    {
        _findingsView.RebuildFindings(_wouldBeReport, _ruleset, _snapshot, SortColumn, SortDescending);

        // A re-thread rebuilds the rows, so the prior detail selection no longer points at a live row;
        // clear it (the detail pane returns to its empty state). WP5f / #160.
        SelectedFinding = null;
        // A re-thread also invalidates a stale run comparison + the "saved" affordance (the live
        // findings just changed) — clear both so neither lingers over a new evaluation (ADR-032 D4).
        Comparison = null;
        RunSaved = false;
    }

    /// <summary>Re-filters + re-sorts <see cref="Findings"/> in place — delegates to
    /// <see cref="_findingsView"/> (<see cref="AuditFindingsView.ApplyView"/>).</summary>
    private void ApplyView() => _findingsView.ApplyView(SortColumn, SortDescending);

    /// <summary>Re-projects the bound props <see cref="AuditFindingsView.ApplyView"/> can affect,
    /// fired via <see cref="AuditFindingsView.ViewChanged"/>.</summary>
    private void OnFindingsViewChanged()
    {
        OnPropertyChanged(nameof(HasFindings));
        OnPropertyChanged(nameof(AllSelected));
        OnPropertyChanged(nameof(VisibleCount));
        OnPropertyChanged(nameof(TotalCount));
        OnPropertyChanged(nameof(IsFiltered));
        OnPropertyChanged(nameof(FilterSummary));
        OnPropertyChanged(nameof(HasNoMatches));
        OnPropertyChanged(nameof(IsAllClear));
    }

    /// <summary>Toggles one filter chip (WP1) — delegates to <see cref="_findingsView"/>
    /// (<see cref="AuditFindingsView.ToggleFilter"/>), then persists (best-effort, no-op while
    /// restoring / un-wired).</summary>
    [RelayCommand]
    private void ToggleFilter(AuditFilterChip chip)
    {
        _findingsView.ToggleFilter(chip, SortColumn, SortDescending);
        PersistView();
    }

    /// <summary>Toggles the rule-class filter from a categories-pane row (WP-C/#180) — delegates to
    /// <see cref="_findingsView"/> (<see cref="AuditFindingsView.ToggleCategory"/>), then persists.</summary>
    [RelayCommand]
    private void ToggleCategory(AuditCategoryRow row)
    {
        _findingsView.ToggleCategory(row, SortColumn, SortDescending);
        PersistView();
    }

    /// <summary>Clears every filter axis (WP1) — delegates to <see cref="_findingsView"/>
    /// (<see cref="AuditFindingsView.ClearFilters"/>), then persists. The "Clear filters" affordance.</summary>
    [RelayCommand]
    private void ClearFilters()
    {
        _findingsView.ClearFilters(SortColumn, SortDescending);
        PersistView();
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

    /// <summary>The select-all / select-none header checkbox (WP5d) — delegates to
    /// <see cref="_findingsView"/> (<see cref="AuditFindingsView.ToggleSelectAll"/>).</summary>
    [RelayCommand]
    private void ToggleSelectAll() => _findingsView.ToggleSelectAll();

    /// <summary>The selection bar's "Clear" (WP5d) — delegates to <see cref="_findingsView"/>
    /// (<see cref="AuditFindingsView.ClearSelection"/>). No Ack/Suppress action here — that is WP5e.</summary>
    [RelayCommand]
    private void ClearSelection() => _findingsView.ClearSelection();

    /// <summary>Acknowledge every SELECTED open finding (WP5e / ADR-028): emits one
    /// <c>[ack]</c> triage request per selected Open row through the shell seam (the gate appends the
    /// tagged ignore entries → RulesetApplied re-threads → the rows re-project as Acknowledged + the
    /// findings drop from the live health report). Already-triaged selected rows are skipped (no-op).</summary>
    [RelayCommand]
    private void AcknowledgeSelected() => _triageCoordinator.AcknowledgeSelected(Findings);

    /// <summary>Suppress every SELECTED open finding (WP5e / ADR-028): the <c>[suppress]</c>
    /// twin of <see cref="AcknowledgeSelected"/> — equal engine strength, different note tag.</summary>
    [RelayCommand]
    private void SuppressSelected() => _triageCoordinator.SuppressSelected(Findings);

    /// <summary>Reverse triage on every SELECTED triaged finding (WP5e / ADR-028): emits one
    /// Un-triage request per selected Acknowledged/Suppressed row (the gate removes the matching
    /// tagged ignore entry → the finding reappears as Open + re-enters the live report). Open rows
    /// are skipped.</summary>
    [RelayCommand]
    private void UntriageSelected() => _triageCoordinator.UntriageSelected(Findings);

    /// <summary>Acknowledge a single open finding (WP5e): the per-row affordance equivalent of
    /// <see cref="AcknowledgeSelected"/> for <paramref name="row"/>.</summary>
    [RelayCommand]
    private void AcknowledgeRow(AuditFindingRowModel row) => _triageCoordinator.AcknowledgeRow(row);

    /// <summary>Suppress a single open finding (WP5e): the per-row equivalent of
    /// <see cref="SuppressSelected"/> for <paramref name="row"/>.</summary>
    [RelayCommand]
    private void SuppressRow(AuditFindingRowModel row) => _triageCoordinator.SuppressRow(row);

    /// <summary>Reverse triage on a single triaged finding (WP5e): the per-row equivalent of
    /// <see cref="UntriageSelected"/> for <paramref name="row"/>.</summary>
    [RelayCommand]
    private void UntriageRow(AuditFindingRowModel row) => _triageCoordinator.UntriageRow(row);

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
    /// would-be table) as RFC-4180 CSV (WP2 / ADR-013 §2/§5/§6). The gate/re-guard/pick/write-once
    /// mechanics live in <see cref="AuditExportService.ExportCsvAsync"/>; this method supplies only
    /// the VM-owned report and name-resolution closure (<see cref="ResolveName"/>, which reads the
    /// borrowed snapshot only, never a provider call).
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanExportReport))]
    private Task ExportReportCsvAsync() => _exportService.ExportCsvAsync(_report, ResolveName);

    /// <summary>Exports the LIVE post-suppression report as a self-contained HTML file
    /// (WP2 / ADR-013 §2/§5/§6). Same delegation as <see cref="ExportReportCsvAsync"/>; also supplies
    /// a <see cref="ReportHeader"/> built from the audit root identity + connection summary + the
    /// current wall clock (<see cref="BuildReportHeader"/>).</summary>
    [RelayCommand(CanExecute = nameof(CanExportReport))]
    private Task ExportReportHtmlAsync() => _exportService.ExportHtmlAsync(_report, ResolveName, BuildReportHeader());

    /// <summary>Armed iff the export seam is installed and the step is live (WP2 / ADR-013 §6). The
    /// borrowed snapshot is non-null by ctor contract and there is no loading state, so the seam +
    /// not-disposed are the only gates — pre-install the commands are inert.</summary>
    private bool CanExportReport() => !IsDisposed && _exportService.IsArmed;

    /// <summary>"Save audit run" (ADR-032 D4 / #190): builds an <see cref="AuditRun"/> from the LIVE
    /// post-suppression report (<see cref="_report"/> — the same findings the health score + sidebar
    /// show), the live <see cref="Summary"/>, the active ruleset + its content hash, the root + the
    /// connection summary, and the INJECTED <see cref="_clock"/> timestamp, then persists it via the
    /// store. Read-only toward AD: the ONLY write is the run JSON under
    /// <c>%APPDATA%\GroupWeaver\runs\</c>. A store I/O failure is swallowed (a failed save just leaves
    /// <see cref="RunSaved"/> false) so a full disk can never crash the audit.</summary>
    [RelayCommand(CanExecute = nameof(CanUseRunStore))]
    private void SaveRun()
    {
        if (IsDisposed || _runStore is null)
        {
            return;
        }

        try
        {
            _runStore.Save(BuildCurrentRun());
            RunSaved = true;
        }
        catch (Exception ex) when (
            ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            // Saving is a deliberate act but non-critical to the live audit: a torn / failed write can
            // never destroy a prior run (atomic store) and must not crash the screen — just no-op.
            RunSaved = false;
        }
    }

    /// <summary>"Compare to previous run" (ADR-032 D4 / #190): diffs the LIVE findings (as a freshly
    /// built <see cref="AuditRun"/>) against the most recent prior SAVED run for the same
    /// <see cref="RootDn"/>, via the pure <see cref="AuditRunDiff.Compute"/>, and projects the four
    /// buckets + honesty/hash banners into <see cref="Comparison"/>. With no prior run the projection is
    /// the empty "no previous run" state. Read-only: lists the runs directory + computes a pure diff —
    /// never a provider call, never an AD touch.</summary>
    [RelayCommand(CanExecute = nameof(CanUseRunStore))]
    private void CompareToPreviousRun()
    {
        if (IsDisposed || _runStore is null)
        {
            return;
        }

        var current = BuildCurrentRun();
        var previous = _runStore.MostRecentFor(RootDn);
        Comparison = previous is null
            ? AuditRunComparison.NoPreviousRun(RootDn)
            : AuditRunComparison.From(AuditRunDiff.Compute(previous, current), previous, current);
    }

    /// <summary>Armed iff the run-history store is installed and the step is live (ADR-032 / #190).</summary>
    private bool CanUseRunStore() => !IsDisposed && _runStore is not null;

    /// <summary>Builds the <see cref="AuditRun"/> for the CURRENT live audit state (ADR-032 D1): the live
    /// post-suppression report's findings in canonical order, the live <see cref="Summary"/>, the active
    /// ruleset's name + content hash, the root + connection summary, the unchecked DNs, and the injected
    /// timestamp. Pure over the borrowed state — never mutates the snapshot/report/ruleset.</summary>
    private AuditRun BuildCurrentRun()
    {
        var findings = _report.Violations.Select(AuditRun.ToFinding).ToList();
        return new AuditRun(
            AuditRun.CurrentSchemaVersion,
            _clock(),
            RootDn,
            string.IsNullOrEmpty(_connectionSummary) ? $"{_snapshot.Objects.Count} objects loaded" : _connectionSummary,
            _ruleset.Name,
            AuditRun.ComputeRulesetHash(_ruleset),
            Summary,
            findings,
            _report.UncheckedDns.ToList());
    }

    /// <summary>The name-resolution closure handed to the exporter — identical to the findings table
    /// + sidebar: an in-snapshot object resolves to its <c>Name</c>, an absent DN falls back to the
    /// DN itself (never a provider call, so export stays read-only toward AD). Core stays App-free
    /// (ADR-013 §2): it takes this delegate, never the snapshot.</summary>
    private string ResolveName(string dn) => SubjectNameResolver.Resolve(_snapshot, dn);

    /// <summary>Builds the HTML report header from the audit's identity (ADR-013 §2): root DN + the
    /// snapshot-resolved root name + the shell-threaded connection summary (an empty one falls back to
    /// a snapshot-object-count line, so an un-threaded audit still produces an honest header), with
    /// <see cref="DateTimeOffset.Now"/> as the injected generation timestamp. ADR-030 D3 (#188): also
    /// carries the active ruleset name, the triaged count (<see cref="TriagedCount"/>) and the live
    /// report's unchecked count, so a bare export is self-describing and can never present a clean bill
    /// that omits the suppressions or the unexpanded scope.</summary>
    private ReportHeader BuildReportHeader()
    {
        var rootName = SubjectNameResolver.Resolve(_snapshot, RootDn);
        var summary = string.IsNullOrEmpty(_connectionSummary)
            ? $"{_snapshot.Objects.Count} objects loaded"
            : _connectionSummary;
        return new ReportHeader(
            RootDn,
            rootName,
            summary,
            DateTimeOffset.Now,
            RulesetName: _ruleset.Name,
            TriagedCount: TriagedCount,
            UncheckedCount: _report.UncheckedDns.Count);
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

    /// <summary>Dispose: flips <see cref="IsDisposed"/> and disposes <see cref="_exportService"/> (which
    /// cancels its own <see cref="CancellationTokenSource"/>) so an export save-picker / write still in
    /// flight at teardown is cancelled and can never write after dispose (WP2 / ADR-013). Idempotent;
    /// NEVER disposes nor mutates the borrowed Ist snapshot / report / ruleset (the audit owns no
    /// renderer — there is nothing else to tear down).</summary>
    public void Dispose()
    {
        if (IsDisposed)
        {
            return;
        }

        IsDisposed = true;
        _exportService.Dispose();
    }
}
