using System.Collections.ObjectModel;
using System.Text;

using Avalonia.Controls;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GroupWeaver.App.Export;
using GroupWeaver.App.Graph;
using GroupWeaver.App.Rules;
using GroupWeaver.App.Settings;
using GroupWeaver.App.Startup;
using GroupWeaver.Core.Export;
using GroupWeaver.Core.Graph;
using GroupWeaver.Core.Model;
using GroupWeaver.Core.Providers;
using GroupWeaver.Core.Rules;

namespace GroupWeaver.App.ViewModels;

/// <summary>
/// Workspace step (AP 2.2 S6): loads the chosen root's scope at construction, builds
/// the drawn graph and hands it to the <see cref="IGraphRenderer"/> seam (ADR-004
/// D5/D6) — the same observable-task pattern as <see cref="ShellViewModel.Initialization"/>,
/// never fire-and-forget. Error policy (ADR-003 D7): <see cref="DirectoryUnavailableException"/>
/// surfaces inline via <see cref="LoadError"/> WITHOUT the Connect step's demo hint
/// (a connection already succeeded); cancellation (<see cref="Dispose"/>) settles
/// <see cref="Initialization"/> quietly; everything else propagates (crash = bug).
/// AP 2.3 adds the lazy-expand pipeline (ADR-005 D3) behind
/// <see cref="IGraphRenderer.NodeExpandRequested"/>, observable via <see cref="Expansion"/>
/// under the same error policy, plus <see cref="RefreshCommand"/> (ADR-005 D4): a
/// FORCED expand of <see cref="SelectedDn"/> through the same pipeline. AP 2.5 (ADR-007)
/// projects the selection into <see cref="DetailPanel"/> — snapshot-only, recomputed on
/// selection change and in each pipeline's <c>finally</c>. Contract pinned by
/// <c>tests/GroupWeaver.App.Tests/WorkspaceLoadTests.cs</c>, <c>WorkspaceExpandTests.cs</c>
/// and <c>WorkspaceDetailTests.cs</c>.
/// </summary>
public sealed partial class WorkspaceViewModel : ObservableObject, IDisposable
{
    private readonly CancellationTokenSource _cts = new();
    private bool _disposed;

    /// <summary>True while the scope load + first render — or an expand pipeline
    /// (AP 2.3; fetch AND focus-only branch alike) — is in flight; the ONE global
    /// busy gate (ADR-005 D3).</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RefreshCommand))]
    [NotifyCanExecuteChangedFor(nameof(JumpCommand))]
    [NotifyCanExecuteChangedFor(nameof(ReloadScopeCommand))]
    [NotifyCanExecuteChangedFor(nameof(ExportReportCsvCommand))]
    [NotifyCanExecuteChangedFor(nameof(ExportReportHtmlCommand))]
    [NotifyCanExecuteChangedFor(nameof(ExportGraphImageCommand))]
    [NotifyCanExecuteChangedFor(nameof(AuditCommand))]
    private bool _isLoading;

    /// <summary>Inline load/renderer error; <c>null</c> hides the error block.</summary>
    [ObservableProperty]
    private string? _loadError;

    /// <summary>DN of the last clicked graph node — the AP 2.5 detail-panel seam.
    /// Carried verbatim (data-model rule: DN strings are never canonicalized).</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RefreshCommand))]
    private string? _selectedDn;

    /// <summary>Status line for the loaded graph: <c>"&lt;n&gt; objects, &lt;m&gt; edges"</c>
    /// over the DRAWN graph (<see cref="Graph"/> node/edge counts, not the snapshot's).</summary>
    [ObservableProperty]
    private string? _graphSummary;

    /// <summary>The AP 2.5 detail-panel projection of <see cref="SelectedDn"/> (ADR-007):
    /// the ONLY thing the panel binds. Recomputed at exactly three points — selection
    /// change plus each pipeline's <c>finally</c>; <c>null</c> while nothing is selected
    /// or no snapshot exists yet. Pinned by <c>WorkspaceDetailTests.cs</c>.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsNodeSelected))]
    private DetailPanelModel? _detailPanel;

    /// <summary>The ADR-022 D3 rail width in pixels, clamped to [300, 520] in
    /// <see cref="OnRailWidthChanged"/> so the graph is never squeezed below a usable width and the
    /// rail never drops below its content minimum. Default 340; seeded from <see cref="UiStateStore"/>
    /// at construction and persisted on change (D4). The rail grid column binds the derived
    /// <see cref="RailColumnWidth"/> (two-way: the GridSplitter writes a px width back through it).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RailColumnWidth))]
    private double _railWidth = 340;

    /// <summary>The ADR-022 D3 rail-collapsed flag: when true the rail column collapses to 0
    /// (<see cref="RailColumnWidth"/> → <c>GridLength(0)</c>) and the splitter hides, leaving only
    /// the seam + ▸ expand chevron beside GraphHost (ADR-001 airspace intact). Toggled by
    /// <see cref="ToggleRail"/> / <c>Ctrl+B</c>, driven by focus mode via
    /// <see cref="SetRailCollapsed"/>; seeded from <see cref="UiStateStore"/> and persisted (D4).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RailColumnWidth))]
    private bool _isRailCollapsed;

    /// <summary>The findings sidebar's share of the rail's findings+detail vertical space
    /// (WP-B / #178), clamped to [0.2, 0.8] in <see cref="OnRailFindingsFractionChanged"/> so
    /// neither section collapses. Default 0.5 (a 1:1 split, up from the old fixed 2:3); seeded
    /// from <see cref="UiStateStore"/> at construction and persisted on change. The two rail rows
    /// bind the derived <see cref="FindingsRowHeight"/>/<see cref="DetailRowHeight"/> star lengths
    /// one-way; the row GridSplitter writes a new fraction back through
    /// <see cref="SetRailFindingsFraction"/> on drag-completed (the jitter-free seam).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FindingsRowHeight))]
    [NotifyPropertyChangedFor(nameof(DetailRowHeight))]
    private double _railFindingsFraction = 0.5;

    /// <summary>The findings sidebar row height as a star <see cref="GridLength"/> (WP-B / #178):
    /// the findings share of the findings+detail vertical split. One-way bound (the row
    /// GridSplitter mutates the live RowDefinitions during a drag and the VM is synced on
    /// drag-completed — see <see cref="SetRailFindingsFraction"/>).</summary>
    public GridLength FindingsRowHeight => new(RailFindingsFraction, GridUnitType.Star);

    /// <summary>The detail/actions row height as a star <see cref="GridLength"/> (WP-B / #178):
    /// the complementary <c>1 - fraction</c> share of the findings+detail vertical split. One-way
    /// bound, the mirror of <see cref="FindingsRowHeight"/>.</summary>
    public GridLength DetailRowHeight => new(1 - RailFindingsFraction, GridUnitType.Star);

    /// <summary>The rail grid column width as a <see cref="GridLength"/> (ADR-022 D3): collapsed ⇒
    /// <c>GridLength(0)</c>, otherwise <c>RailWidth</c> px. Two-way: the GridSplitter writes a
    /// resized px <see cref="GridLength"/> here, which flows into <see cref="RailWidth"/> (re-clamped
    /// + persisted). A collapsed write is ignored — collapse is driven by <see cref="IsRailCollapsed"/>,
    /// not by the splitter (which is hidden then anyway).</summary>
    public GridLength RailColumnWidth
    {
        get => IsRailCollapsed ? new GridLength(0) : new GridLength(RailWidth, GridUnitType.Pixel);
        set
        {
            if (!IsRailCollapsed && value.IsAbsolute)
            {
                RailWidth = value.Value;
            }
        }
    }

    /// <summary>The AP 3.4 rule report (ADR-010 §3) the violations sidebar binds.
    /// <c>RuleEngine.Evaluate</c> runs against the threaded ruleset at the two graph-build
    /// sites — LoadAsync and the ExpandAsync fetch branch — BEFORE the renderer call, and
    /// assigns the real report (which re-projects <see cref="Violations"/> in
    /// <see cref="OnReportChanged"/> and re-evaluates <see cref="HasViolations"/>/
    /// <see cref="HasUncheckedAreas"/>). Starts at <see cref="RuleReport.Empty"/> until
    /// the first load settles.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasViolations))]
    [NotifyPropertyChangedFor(nameof(HasUncheckedAreas))]
    private RuleReport _report = RuleReport.Empty;

    /// <summary>The ruleset every Evaluate runs against — located once in the composition
    /// root and threaded down (ADR-010 §3); a <c>null</c> ctor ruleset resolves to the
    /// embedded default, so every pre-AP-3.4 workspace test stays on the 19-finding
    /// baseline. <see cref="EffectiveRuleset.Errors"/> is carried, not surfaced — AP 3.3
    /// owns the settings UI that shows them. Settable (AP 3.3 / ADR-011 §3): a settings
    /// Apply/Save re-threads it via <see cref="ApplyRulesetAsync"/>, and because
    /// <c>RuleEngine.Evaluate</c> re-reads the field at call time an in-flight pipeline
    /// picks the new ruleset up in its own Evaluate.</summary>
    private Ruleset _ruleset;

    /// <summary>The AP 4.1 export save-dialog seam (ADR-013 §5): supplied by the composition
    /// root from the workspace window's <see cref="Avalonia.Controls.TopLevel"/> once attached
    /// (via <see cref="UseExportFileDialogs"/>, mirroring <c>SettingsViewModel.UseFileDialogs</c>);
    /// <c>null</c> in tests until the export seam is wired, so every existing test stays on the
    /// default and the export commands stay disarmed (<see cref="CanExportReport"/>).</summary>
    private IExportFileDialogs? _exportDialogs;

    /// <summary>The AP 4.2.2 "Design plan" callback (ADR-014): the shell installs it via
    /// <see cref="UseDesignPlanCallback"/> when this workspace becomes the current step, so the
    /// header button can switch into Plan Mode. <c>null</c> until installed — the command then
    /// stays disarmed (<see cref="CanDesignPlan"/>), keeping every pre-4.2.2 test on the default.</summary>
    private Action? _onDesignPlan;

    /// <summary>The ADR-022 D2 "Focus" callback: the shell installs it via
    /// <see cref="UseFocusToggleCallback"/> at <c>OnRootChosen</c> (mirroring
    /// <see cref="UseDesignPlanCallback"/>), so the header button can toggle shell-level focus
    /// mode. <c>null</c> until installed — the command then stays disarmed
    /// (<see cref="CanToggleFocus"/>), so a headless/renderer-less workspace never half-toggles.</summary>
    private Action? _onToggleFocus;

    /// <summary>The WP5 "Audit" callback (#152): the shell installs it via
    /// <see cref="UseAuditCallback"/> at <c>OnRootChosen</c> (mirroring
    /// <see cref="UseDesignPlanCallback"/>), so the header button can switch into the audit step.
    /// <c>null</c> until installed — the command then stays disarmed (<see cref="CanAudit"/>),
    /// keeping every pre-WP5 test on the default.</summary>
    private Action? _onAudit;

    /// <summary>The ADR-022 D4 rail-state store; seeds <see cref="RailWidth"/>/
    /// <see cref="IsRailCollapsed"/> at construction and is written on each change. Best-effort —
    /// load/save never throw. Defaulted so every pre-ADR-022 call site (and test) still compiles.</summary>
    private readonly UiStateStore _uiStateStore;

    /// <summary>Suppresses the seed-time persist: while the ctor applies the loaded
    /// <see cref="UiState"/>, the generated <c>OnRailWidthChanged</c>/<c>OnIsRailCollapsedChanged</c>
    /// hooks must not write the just-read values back. Cleared once construction settles.</summary>
    private readonly bool _seeding;

    public WorkspaceViewModel(
        IDirectoryProvider provider,
        AdObject root,
        DirectoryConnection connection,
        bool webView2Missing = false,
        Func<IGraphRenderer>? graphRendererFactory = null,
        EffectiveRuleset? ruleset = null,
        IExportFileDialogs? exportDialogs = null,
        UiStateStore? uiStateStore = null)
    {
        Provider = provider;
        Root = root;
        Connection = connection;
        WebView2Missing = webView2Missing;
        _exportDialogs = exportDialogs;

        // ADR-022 D4: seed the rail state from the persisted store (never-throw Load → defaults
        // on any failure). _seeding gates the generated change hooks so applying the loaded
        // values does not immediately write them straight back.
        _uiStateStore = uiStateStore ?? new UiStateStore();
        _seeding = true;
        var uiState = _uiStateStore.Load();
        RailWidth = uiState.RailWidth;
        IsRailCollapsed = uiState.RailCollapsed;
        RailFindingsFraction = uiState.RailFindingsFraction;
        _seeding = false;

        // null => the embedded default (the pre-AP-3.4 contract); a located user/default
        // EffectiveRuleset otherwise. Errors carried, surfaced by AP 3.3. // AP 3.3
        _ruleset = (ruleset ?? new EffectiveRuleset(RulesetLoader.LoadDefault(), FromUserFile: false, [])).Ruleset;

        // ADR-004 D5: re-check the runtime probe BEFORE building a renderer — a
        // missing WebView2 Runtime must not even invoke the factory.
        if (graphRendererFactory is not null && !webView2Missing)
        {
            GraphRenderer = graphRendererFactory();
            GraphRenderer.NodeClicked += (_, e) => SelectedDn = e.Dn;
            // Pinned decision: renderer failures reuse the ONE inline error surface
            // instead of growing a parallel property; IsLoading stays untouched.
            GraphRenderer.RendererError += (_, e) => LoadError = $"{e.Source}: {e.Message}";
            // AP 2.3 (ADR-005 D3): the expand decision is made from the SNAPSHOT
            // (kind + load state), never the event's Kind string — only the DN flows.
            GraphRenderer.NodeExpandRequested += (_, e) => OnNodeExpandRequested(e.Dn);
        }

        Initialization = LoadAsync(_cts.Token);
    }

    /// <summary>Provider behind the active connection; the scope is loaded from it.</summary>
    public IDirectoryProvider Provider { get; }

    /// <summary>The root the user picked in the PickRoot step (mandatory entry filter).</summary>
    public AdObject Root { get; }

    /// <summary>Connection summary handed over from the Connect step.</summary>
    public DirectoryConnection Connection { get; }

    /// <summary>DN of the chosen root.</summary>
    public string RootDn => Root.Dn;

    /// <summary>The active ruleset's <see cref="GroupWeaver.Core.Rules.Ruleset.Name"/> — the one
    /// audit-orientation fact surfaced nowhere else in the workspace chrome (root DN lives in the
    /// status bar, demo/live in the top strip). Shown by the no-selection scope-summary card; the
    /// change notification is raised in <see cref="ApplyRulesetAsync"/> so a settings apply updates
    /// it live (ADR-022 D5 reframe, #186).</summary>
    public string RulesetName => _ruleset.Name;

    /// <summary>Display name of the chosen root.</summary>
    public string RootName => Root.Name;

    /// <summary>Status-bar line; same shape as the M1 DoD console line.</summary>
    public string ConnectionSummary =>
        $"connected, {Connection.GroupCount} groups loaded — {Connection.Description}";

    /// <summary>
    /// Handed through from <see cref="ShellViewModel"/> at construction (ADR-003 D3):
    /// switches the GraphHost placeholder to its missing-runtime variant and vetoes
    /// the renderer factory.
    /// </summary>
    public bool WebView2Missing { get; }

    /// <summary>Placeholder hyperlink: open the runtime's download page in the browser.</summary>
    public IRelayCommand OpenWebView2DownloadPageCommand { get; } =
        new RelayCommand(WebView2Runtime.OpenDownloadPage);

    /// <summary>The UNFILTERED projected sidebar rows (#197): every finding in
    /// <see cref="Report"/>, in canonical report order. <see cref="Violations"/> is the
    /// filtered VIEW derived from this in <see cref="ApplyViolationFilter"/>; the per-severity
    /// chip counts (<see cref="SeverityChips"/>) and <see cref="TotalViolationCount"/> are
    /// computed over THIS list so filtering never shrinks either. Rebuilt in
    /// <see cref="OnReportChanged"/>.</summary>
    private readonly List<ViolationRowModel> _allViolations = [];

    /// <summary>The active severity-axis filter (#197). EMPTY = no constraint (fail-open —
    /// all rows shown); non-empty keeps only rows whose <see cref="ViolationRowModel.Severity"/>
    /// is contained. Session-only — reset on each report change (unlike the Audit screen's
    /// persisted filter, #203), because <see cref="SeverityChips"/> is rebuilt each report.</summary>
    private readonly HashSet<RuleSeverity> _severityFilter = [];

    /// <summary>The AP 3.4 violations sidebar rows (ADR-010 §5), in canonical report
    /// order (unshuffled — ADR-009): the FILTERED view of <see cref="_allViolations"/> —
    /// all rows when <see cref="_severityFilter"/> is empty, otherwise only the rows whose
    /// severity is in the set (#197). <see cref="ViolationRowModel.SubjectName"/> is resolved
    /// snapshot-only. Bound by <see cref="Views.ViolationsSidebarView"/>.</summary>
    public ObservableCollection<ViolationRowModel> Violations { get; } = [];

    /// <summary>The severity filter chips (#197) — fixed Error/Warning/Info, in descending
    /// severity order — rebuilt whenever <see cref="Report"/> changes. Each chip's
    /// <see cref="AuditFilterChip.Count"/> is over the UNFILTERED <see cref="_allViolations"/>
    /// and its <see cref="AuditFilterChip.IsActive"/> mirrors <see cref="_severityFilter"/>
    /// membership; clicking a chip runs <see cref="ToggleSeverityFilterCommand"/>. Reuses the
    /// Audit screen's <see cref="AuditFilterChip"/> (axis <see cref="AuditFilterAxis.Severity"/>)
    /// — the same chip type + active affordance. Gated to render only when there are findings.</summary>
    public ObservableCollection<AuditFilterChip> SeverityChips { get; } = [];

    /// <summary>The TOTAL (unfiltered) finding count (#197): <see cref="_allViolations"/>.Count.
    /// The header binds THIS so filtering never makes "Findings (n)" silently shrink — the
    /// per-severity breakdown lives on the chips. Re-notified in <see cref="OnReportChanged"/>.</summary>
    public int TotalViolationCount => _allViolations.Count;

    /// <summary>Drives the sidebar all-clear state: <c>true</c> when the report has at
    /// least one finding. Recomputed on <see cref="Report"/> change.</summary>
    public bool HasViolations => Report.Violations.Count > 0;

    /// <summary>Drives the "unexpanded areas are unchecked" hint: <c>true</c> when the
    /// report's <see cref="RuleReport.UncheckedDns"/> is non-empty (load-state truth,
    /// never ignore-filtered — ADR-009). Recomputed on <see cref="Report"/> change.</summary>
    public bool HasUncheckedAreas => Report.UncheckedDns.Count > 0;

    /// <summary>WP-B (#178): true exactly when a node's detail is projected (a node is selected over
    /// a loaded snapshot — the same signal that arms the detail panel). Drives the Refresh button's
    /// conditional brand accent: filled/primary only when the action is meaningful, plain otherwise.
    /// Recomputed via the <see cref="DetailPanel"/> change notification.</summary>
    public bool IsNodeSelected => DetailPanel is not null;

    /// <summary>
    /// The scope-load-and-render flow kicked off at construction; completes only once
    /// the renderer accepted the graph (its readiness must not lie, ADR-004 D5).
    /// </summary>
    public Task Initialization { get; }

    /// <summary>
    /// The lazy-expand pipeline of the most recent ACCEPTED expand gesture (ADR-005 D3)
    /// — the same observable-task pattern as <see cref="Initialization"/>, never
    /// fire-and-forget. Completed from construction (safe to await before any gesture);
    /// left reference-unchanged when a gesture is dropped by the busy gate.
    /// </summary>
    public Task Expansion { get; private set; } = Task.CompletedTask;

    /// <summary>Renderer built from the ctor factory; <c>null</c> when the factory is
    /// null or the WebView2 Runtime is missing — the view then keeps its placeholder.</summary>
    public IGraphRenderer? GraphRenderer { get; }

    /// <summary>The window-scoped graph-surface coordinator (#122 / ADR-025), pushed in by
    /// <c>MainWindow</c> via <see cref="UseGraphSurfaceCoordinator"/> (mirroring the export seam).
    /// The view uses it to MOUNT the live graph surface (preserving a parked viewport); <c>null</c>
    /// headless / off a window — the view then keeps today's direct GraphHost mount.</summary>
    public IGraphSurfaceCoordinator? GraphSurfaceCoordinator { get; private set; }

    /// <summary>The loaded scope; <c>null</c> until the load completed (AP 2.3/2.5 seam).</summary>
    public DirectorySnapshot? Snapshot { get; private set; }

    /// <summary>The built graph model handed to the renderer; <c>null</c> until built.</summary>
    public GraphModel? Graph { get; private set; }

    /// <summary>ADR-007 D1: selection re-projects the detail panel IMMEDIATELY —
    /// snapshot-only reads, never busy-gated, so the panel stays responsive during any
    /// in-flight pipeline. ADR-010 §5: the same change drives the sidebar selection-sync
    /// highlight — every row anchored at <paramref name="value"/> lights up (multiple
    /// findings on one DN all highlight); the detail panel itself is untouched (severity
    /// is not a whitelist attribute).</summary>
    partial void OnSelectedDnChanged(string? value)
    {
        RecomputeDetailPanel();
        HighlightActiveRows(value);

        // ADR-020 (#96): reverse sidebar->graph selection sync — project the current
        // selection onto the canvas from OUTSIDE a tap. Fire-and-forget (partial void
        // can't be async): SelectAsync is never-throw, so the discarded task can't crash
        // the no-handler path. NOT busy-gated (selection stays responsive mid-pipeline,
        // ADR-007 D1). Goes to the renderer SELECT channel, NEVER FocusAsync: JumpAsync
        // sets SelectedDn (triggering THIS) AND then calls FocusAsync, and the pinned
        // JumpCommand test requires FocusCalls to increment by EXACTLY 1 per jump. A null
        // value clears the canvas (empty DN -> clearSelection JS-side). A tap-driven
        // selection re-issues a redundant select; idempotent, intentionally unguarded.
        if (GraphRenderer is { } renderer)
        {
            _ = renderer.SelectAsync(value ?? string.Empty, _cts.Token);
        }
    }

    /// <summary>Flips <see cref="ViolationRowModel.IsActive"/> on every sidebar row whose
    /// anchor matches <paramref name="selectedDn"/> under <see cref="Dn.Comparer"/>
    /// (ADR-010 §5) — the highlight is by ANCHOR (PrimaryDn), never by attached-DN, so a
    /// member-endpoint selection that is no finding's anchor lights nothing. A
    /// <c>null</c>/no-match selection clears all highlights.</summary>
    private void HighlightActiveRows(string? selectedDn)
    {
        foreach (var row in Violations)
        {
            row.IsActive = selectedDn is not null && Dn.Comparer.Equals(row.PrimaryDn, selectedDn);
        }
    }

    /// <summary>The single projection write: a pure snapshot read through the
    /// <see cref="DetailPanelModel.Build"/> choke point — never calls the provider,
    /// never checks or takes the busy gate (ADR-007 D1).</summary>
    private void RecomputeDetailPanel() =>
        DetailPanel = Snapshot is null ? null : DetailPanelModel.Build(Snapshot, SelectedDn, Report);

    /// <summary>Re-projects <see cref="Report"/>'s findings into the UNFILTERED
    /// <see cref="_allViolations"/> backing list (ADR-010 §5): canonical report order,
    /// unshuffled (ADR-009); the subject name is resolved snapshot-only (raw-External anchors
    /// fall back to the DN — never a provider call). Then rebuilds <see cref="SeverityChips"/>
    /// (counts over the unfiltered list) and re-projects the filtered <see cref="Violations"/>
    /// view via <see cref="ApplyViolationFilter"/>. The all-clear text and the unchecked-areas
    /// hint flip via the generated <see cref="HasViolations"/>/<see cref="HasUncheckedAreas"/>
    /// notifications; <see cref="TotalViolationCount"/> is re-notified for the header. The
    /// selection-sync highlight is re-applied (inside <see cref="ApplyViolationFilter"/>) over
    /// the fresh visible rows so a selection that persists across a re-Evaluate keeps lighting
    /// its anchor. The severity filter is session-only (#197) — a fresh report resets it.</summary>
    partial void OnReportChanged(RuleReport value)
    {
        _allViolations.Clear();
        foreach (var violation in value.Violations)
        {
            var subject = SubjectNameResolver.Resolve(Snapshot, violation.PrimaryDn);
            _allViolations.Add(new ViolationRowModel(
                violation.Severity, violation.Message, subject, violation.PrimaryDn));
        }

        RebuildSeverityChips();
        ApplyViolationFilter();
        OnPropertyChanged(nameof(TotalViolationCount));
    }

    /// <summary>Rebuilds <see cref="SeverityChips"/> in place (#197) — fixed Error/Warning/Info
    /// in descending order, each carrying its count over the UNFILTERED <see cref="_allViolations"/>
    /// and its <see cref="AuditFilterChip.IsActive"/> from the preserved <see cref="_severityFilter"/>.
    /// Reuses the Audit <see cref="AuditFilterChip"/> type (axis <see cref="AuditFilterAxis.Severity"/>,
    /// the boxed <see cref="RuleSeverity"/> as the key + the severity for the glyph).</summary>
    private void RebuildSeverityChips()
    {
        SeverityChips.Clear();
        foreach (var severity in new[] { RuleSeverity.Error, RuleSeverity.Warning, RuleSeverity.Info })
        {
            var count = _allViolations.Count(r => r.Severity == severity);
            SeverityChips.Add(new AuditFilterChip(
                AuditFilterAxis.Severity, severity, SeverityChipLabel(severity), count, severity)
            {
                IsActive = _severityFilter.Contains(severity),
            });
        }
    }

    private static string SeverityChipLabel(RuleSeverity severity) => severity switch
    {
        RuleSeverity.Error => "Errors",
        RuleSeverity.Warning => "Warnings",
        _ => "Info",
    };

    /// <summary>Re-projects the filtered <see cref="Violations"/> view from
    /// <see cref="_allViolations"/> (#197): all rows when <see cref="_severityFilter"/> is empty
    /// (fail-open), otherwise only rows whose severity is in the set. Rebuilt by in-place
    /// Clear/Add (the bound collection reference never changes). Re-applies the selection-sync
    /// highlight over the fresh visible rows.</summary>
    private void ApplyViolationFilter()
    {
        Violations.Clear();
        foreach (var row in _allViolations)
        {
            if (_severityFilter.Count == 0 || _severityFilter.Contains(row.Severity))
            {
                Violations.Add(row);
            }
        }

        HighlightActiveRows(SelectedDn);
    }

    /// <summary>Toggles one severity filter chip (#197): flips its <see cref="AuditFilterChip.IsActive"/>,
    /// adds/removes its boxed <see cref="RuleSeverity"/> key in <see cref="_severityFilter"/>, then
    /// re-projects the filtered <see cref="Violations"/> view. Mirrors <c>AuditViewModel.ToggleFilter</c>
    /// for the severity axis only; multi-select (each active severity is OR'd). Session-only — NOT
    /// persisted (unlike the Audit screen's #203); it resets on the next report change.</summary>
    [RelayCommand]
    private void ToggleSeverityFilter(AuditFilterChip chip)
    {
        chip.IsActive = !chip.IsActive;
        var severity = (RuleSeverity)chip.Key;
        if (chip.IsActive)
        {
            _severityFilter.Add(severity);
        }
        else
        {
            _severityFilter.Remove(severity);
        }

        ApplyViolationFilter();
    }

    /// <summary>The ctor-load entry point (kept by name — <see cref="Initialization"/>
    /// still routes through it); a thin delegate to the shared <see cref="RunScopeLoadAsync"/>
    /// body now reused by <see cref="ReloadScopeAsync"/> (issue #30).</summary>
    private Task LoadAsync(CancellationToken cancellationToken) => RunScopeLoadAsync(cancellationToken);

    /// <summary>
    /// The shared whole-scope load body (issue #30): <c>LoadScopeAsync</c> → fresh
    /// <see cref="DirectorySnapshot"/> → <c>GraphBuilder.Build</c> → re-Evaluate against
    /// the LIVE <c>_ruleset</c> → <see cref="IGraphRenderer.ShowGraphAsync"/> (replace-all,
    /// NEVER <c>UpdateGraphAsync</c> — the topology is wholesale-new, so viewport
    /// preservation is meaningless). The <c>Snapshot =</c> assignment MUST precede
    /// <c>Report =</c>: <see cref="OnReportChanged"/> resolves subject names against the
    /// CURRENT snapshot, so a reorder would name findings off the stale snapshot. Reused by
    /// the ctor load and reload — one composition, one render contract.
    /// </summary>
    private async Task RunScopeLoadAsync(CancellationToken cancellationToken)
    {
        IsLoading = true;
        try
        {
            Snapshot = await Provider.LoadScopeAsync(RootDn, cancellationToken);
            Graph = GraphBuilder.Build(Snapshot, RootDn);

            // AP 3.4 (ADR-010 §3): evaluate the loaded scope BEFORE the renderer call,
            // inside this IsLoading window — Evaluate is pure/sync (ADR-009), no new gate.
            // The report + roll-up below-map ride the renderer seam into the wire fields.
            Report = RuleEngine.Evaluate(Snapshot, _ruleset);
            var below = ComputeBelow(Snapshot, Report);

            if (GraphRenderer is not null)
            {
                await GraphRenderer.ShowGraphAsync(Graph, Report, below, cancellationToken);
            }

            UpdateGraphSummary(Graph);
        }
        catch (DirectoryUnavailableException ex)
        {
            // No demo hint here (unlike the Connect step): a connection already succeeded.
            LoadError = ex.Message;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Our own Dispose cancelled the load — settle Initialization quietly.
        }
        finally
        {
            // ADR-007 D1: every pipeline run re-projects (error paths included) — a
            // selection made during the gated load shows up without a further gesture.
            RecomputeDetailPanel();
            IsLoading = false;
        }
    }

    /// <summary>
    /// Whole-scope reload (issue #30): re-runs <see cref="RunScopeLoadAsync"/> over
    /// <see cref="RootDn"/> — a fresh <see cref="DirectorySnapshot"/> rebuilt from scratch,
    /// so node-Refresh ex-member orphans and out-of-scope lazy expansions vanish by
    /// construction (no pruning code; snapshot stays append-only). Clears
    /// <see cref="SelectedDn"/> (the selected node may not survive the rebuild) and
    /// <see cref="LoadError"/> BEFORE the await — the panel re-projects null and highlights
    /// clear up front. Re-Evaluates against the LIVE <c>_ruleset</c> (honors a settings
    /// Apply since first load), exactly like the ctor load. A stale-armed Execute
    /// (busy/disposed/renderer-less) is dropped, never queued — <c>RelayCommand.Execute</c>
    /// ignores <c>CanExecute</c>, so the guard is re-checked here (same discipline as
    /// <see cref="Refresh"/>).
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanReloadScope))]
    private async Task ReloadScopeAsync()
    {
        if (_disposed || IsLoading || GraphRenderer is null)
        {
            return;
        }

        SelectedDn = null;
        LoadError = null;
        await RunScopeLoadAsync(_cts.Token);
    }

    /// <summary>Armed iff a renderer exists (the AP 2.5 seam allows selection without one —
    /// nothing to reload-render then) and the ONE global busy gate is idle (ADR-005 D3).
    /// Selection-INDEPENDENT — reload always re-loads the root, never the selection — so it
    /// stays armed with nothing selected, unlike <see cref="CanRefresh"/>.</summary>
    private bool CanReloadScope() => !IsLoading && GraphRenderer is not null;

    /// <summary>
    /// Re-threads a settings Apply/Save into this LIVE workspace (AP 3.3 / ADR-011 §3):
    /// swaps the ruleset, then — over the ALREADY-LOADED snapshot — re-Evaluates and
    /// pushes the fresh report through <see cref="IGraphRenderer.UpdateGraphAsync"/>
    /// (replace-in-place, viewport-preserving). NEVER <c>GraphBuilder.Build</c> (a
    /// ruleset-only change leaves topology — and the <see cref="Graph"/> reference —
    /// untouched) and NEVER <c>ShowGraphAsync</c> (destroy + fit would lose the viewport).
    /// The exact <see cref="ExpandAsync"/> post-fetch machinery minus the rebuild;
    /// <see cref="OnReportChanged"/> re-projects the sidebar.
    ///
    /// <para><b>IsLoading-gated</b> (ADR-005 D3): with no snapshot yet, or while a load
    /// is in flight, this ONLY sets the field and returns — no concurrent push. The
    /// in-flight pipeline's own Evaluate re-reads the now-settable <c>_ruleset</c> at call
    /// time, so the new ruleset lands via that pipeline's single render, never a second
    /// update racing it. Settings is modal, so no new gesture starts a load while open.</para>
    /// </summary>
    public async Task ApplyRulesetAsync(Ruleset newRuleset, CancellationToken cancellationToken = default)
    {
        _ruleset = newRuleset;
        OnPropertyChanged(nameof(RulesetName));
        if (Snapshot is null || IsLoading || GraphRenderer is not { } renderer)
        {
            return;
        }

        Report = RuleEngine.Evaluate(Snapshot, _ruleset);
        var below = ComputeBelow(Snapshot, Report);
        await renderer.UpdateGraphAsync(Graph!, Report, below, cancellationToken);
    }

    /// <summary>True once <see cref="Dispose"/> ran — the dispose-discipline observability the
    /// shell teardown pins read (AP 4.2.2): the Ist↔Plan round-trip must never flip this.</summary>
    public bool IsDisposed => _disposed;

    /// <summary>The "Design plan" header button (AP 4.2.2 / ADR-014): switches the shell into
    /// Plan Mode via the installed callback. Armed iff the callback is installed (the shell
    /// installs it when this workspace becomes the current step); a stale-armed Execute with no
    /// callback is a silent no-op.</summary>
    [RelayCommand(CanExecute = nameof(CanDesignPlan))]
    private void DesignPlan() => _onDesignPlan?.Invoke();

    private bool CanDesignPlan() => _onDesignPlan is not null;

    /// <summary>Installs the shell's Plan-Mode switch callback (AP 4.2.2 / ADR-014) and re-arms
    /// <see cref="DesignPlanCommand"/>. Called by the shell when this workspace becomes the
    /// current step; headless tests reach it through the live shell's <c>OnRootChosen</c>.</summary>
    public void UseDesignPlanCallback(Action onDesignPlan)
    {
        _onDesignPlan = onDesignPlan;
        DesignPlanCommand.NotifyCanExecuteChanged();
    }

    /// <summary>The ADR-022 D2 "Focus" header button: toggles shell-level focus mode through the
    /// installed callback. Armed iff the callback is installed (the shell installs it at
    /// <c>OnRootChosen</c>); a stale-armed Execute with no callback is a silent no-op.</summary>
    [RelayCommand(CanExecute = nameof(CanToggleFocus))]
    private void ToggleFocus() => _onToggleFocus?.Invoke();

    private bool CanToggleFocus() => _onToggleFocus is not null;

    /// <summary>Installs the shell's focus-mode toggle callback (ADR-022 D2) and re-arms
    /// <see cref="ToggleFocusCommand"/>. Called by the shell when this workspace becomes the
    /// current step; mirrors <see cref="UseDesignPlanCallback"/>.</summary>
    public void UseFocusToggleCallback(Action toggle)
    {
        _onToggleFocus = toggle;
        ToggleFocusCommand.NotifyCanExecuteChanged();
    }

    /// <summary>The WP5 "Audit" header button (#152): switches the shell into the audit step
    /// via the installed callback. Armed iff the callback is installed AND a load has completed
    /// (<c>Snapshot is not null</c> — the audit summary is computed over the loaded scope); a
    /// stale-armed Execute with no callback is a silent no-op.</summary>
    [RelayCommand(CanExecute = nameof(CanAudit))]
    private void Audit() => _onAudit?.Invoke();

    private bool CanAudit() => _onAudit is not null && Snapshot is not null;

    /// <summary>Installs the shell's audit-step switch callback (WP5 / #152) and re-arms
    /// <see cref="AuditCommand"/>. Called by the shell when this workspace becomes the current
    /// step; mirrors <see cref="UseDesignPlanCallback"/>.</summary>
    public void UseAuditCallback(Action onAudit)
    {
        _onAudit = onAudit;
        AuditCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Toggles the rail collapsed state (ADR-022 D3 — <c>Ctrl+B</c> / the seam chevron).
    /// The generated <see cref="OnIsRailCollapsedChanged"/> hook persists the new value (D4).</summary>
    [RelayCommand]
    private void ToggleRail() => IsRailCollapsed = !IsRailCollapsed;

    /// <summary>Drives the rail collapsed state from shell-level focus mode (ADR-022 D2): focus
    /// on ⇒ collapse, focus off ⇒ expand. Routed through the same <see cref="IsRailCollapsed"/>
    /// setter, so the change persists (D4) like a manual toggle.</summary>
    public void SetRailCollapsed(bool collapsed) => IsRailCollapsed = collapsed;

    /// <summary>ADR-022 D3 clamp: the rail width is held within [300, 520] so the graph keeps a
    /// usable width and the rail never drops below its content minimum. Persists on change (D4).</summary>
    partial void OnRailWidthChanged(double value)
    {
        var clamped = Math.Clamp(value, 300, 520);
        if (clamped != value)
        {
            // Re-entrant write through the same setter; the clamped value re-fires this hook
            // (clamped == value the second time), so it persists exactly once at the clamped value.
            RailWidth = clamped;
            return;
        }

        PersistUiState();
    }

    /// <summary>ADR-022 D4: persist the rail-collapsed change (write-on-change, best-effort).</summary>
    partial void OnIsRailCollapsedChanged(bool value) => PersistUiState();

    /// <summary>WP-B (#178) clamp: the findings share is held within [0.2, 0.8] so neither the
    /// findings sidebar nor the detail stack collapses. Persists on change (write-on-change,
    /// best-effort — mirrors <see cref="OnRailWidthChanged"/>).</summary>
    partial void OnRailFindingsFractionChanged(double value)
    {
        var clamped = Math.Clamp(value, 0.2, 0.8);
        if (clamped != value)
        {
            // Re-entrant write through the same setter; the clamped value re-fires this hook
            // (clamped == value the second time), so it persists exactly once at the clamped value.
            RailFindingsFraction = clamped;
            return;
        }

        PersistUiState();
    }

    /// <summary>WP-B (#178) write-back seam from the row GridSplitter: the view's drag-completed
    /// handler reads the two live rail rows' rendered heights and calls this with the findings
    /// share. Routes through the <see cref="RailFindingsFraction"/> setter, so the value is clamped
    /// to [0.2, 0.8] and persisted exactly like a manual change. A non-finite or zero total (a
    /// not-yet-measured rail) is ignored — the splitter only fires after layout, but guard anyway
    /// so a degenerate read never writes NaN to the store.</summary>
    public void SetRailFindingsFraction(double findingsHeight, double detailHeight)
    {
        var total = findingsHeight + detailHeight;
        if (!double.IsFinite(total) || total <= 0)
        {
            return;
        }

        RailFindingsFraction = findingsHeight / total;
    }

    /// <summary>Writes the current rail state to the persisted store (ADR-022 D4). Best-effort —
    /// <see cref="UiStateStore.Save"/> never throws. Suppressed during ctor seeding so applying
    /// the just-loaded values does not write them straight back.</summary>
    private void PersistUiState()
    {
        if (_seeding)
        {
            return;
        }

        // Preserve the ADR-026 theme field that ShellViewModel owns: read-modify-write the
        // shared store so a rail change never clobbers the persisted theme back to default.
        _uiStateStore.Save(_uiStateStore.Load() with
        {
            RailWidth = RailWidth,
            RailCollapsed = IsRailCollapsed,
            RailFindingsFraction = RailFindingsFraction,
        });
    }

    /// <summary>Installs the real export save-picker seam (AP 4.1 / ADR-013 §5): the
    /// production workspace view calls this from its own <see cref="Avalonia.Controls.TopLevel"/>
    /// (<c>StorageProviderExportFileDialogs</c>) once attached, so the export commands reach the
    /// OS picker. Headless tests inject a fake here. Re-arms the export commands (the gate
    /// includes <c>_exportDialogs is not null</c>). Idempotent — the last writer wins; mirrors
    /// <c>SettingsViewModel.UseFileDialogs</c>.</summary>
    public void UseExportFileDialogs(IExportFileDialogs dialogs)
    {
        _exportDialogs = dialogs;
        ExportReportCsvCommand.NotifyCanExecuteChanged();
        ExportReportHtmlCommand.NotifyCanExecuteChanged();
        ExportGraphImageCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Installs the window-scoped graph-surface coordinator (#122 / ADR-025): pushed in by
    /// <c>MainWindow</c> through the <c>CurrentStep</c> watcher, exactly like
    /// <see cref="UseExportFileDialogs"/>. The view's mount path reads
    /// <see cref="GraphSurfaceCoordinator"/>; idempotent — the last writer wins.</summary>
    public void UseGraphSurfaceCoordinator(IGraphSurfaceCoordinator coordinator) =>
        GraphSurfaceCoordinator = coordinator;

    /// <summary>
    /// Exports the current <see cref="Report"/> as RFC-4180 CSV to a user-picked path
    /// (AP 4.1 / ADR-013 §2/§5/§6). Gate (F2): <c>Snapshot is not null</c> — NOT
    /// <see cref="HasViolations"/> — so an all-clear-but-unexpanded scope still exports its
    /// unchecked-areas appendix. Re-guards in the body (a stale-armed Execute ignores
    /// CanExecute), picks via the seam, and on a non-null pick writes the pure-Core
    /// <see cref="ViolationReportExporter.ToCsv"/> output (UTF-8, no BOM) to ONLY that path —
    /// a cancelled pick is a no-op. Read-only toward AD: the only write target is the picked
    /// local file; the directory is never touched (the name closure reads the snapshot only).
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanExportReport))]
    private async Task ExportReportCsvAsync()
    {
        if (_disposed || IsLoading || Snapshot is null || _exportDialogs is null)
        {
            return;
        }

        var path = await _exportDialogs.PickSavePathAsync(ExportKind.Csv, _cts.Token);
        if (path is null)
        {
            return;
        }

        var csv = ViolationReportExporter.ToCsv(Report, ResolveSubjectName);
        await WriteUtf8Async(path, csv, _cts.Token);
    }

    /// <summary>
    /// Exports the current <see cref="Report"/> as a self-contained HTML file to a user-picked
    /// path (AP 4.1 / ADR-013 §2/§5/§6). Same gate, re-guard, pick and write-once discipline as
    /// <see cref="ExportReportCsvAsync"/>; the HTML carries a <see cref="ReportHeader"/> built
    /// from this workspace's root identity + connection summary + the current wall clock
    /// (<see cref="BuildReportHeader"/>).
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanExportReport))]
    private async Task ExportReportHtmlAsync()
    {
        if (_disposed || IsLoading || Snapshot is null || _exportDialogs is null)
        {
            return;
        }

        var path = await _exportDialogs.PickSavePathAsync(ExportKind.Html, _cts.Token);
        if (path is null)
        {
            return;
        }

        var html = ViolationReportExporter.ToHtml(Report, ResolveSubjectName, BuildReportHeader());
        await WriteUtf8Async(path, html, _cts.Token);
    }

    /// <summary>Armed iff a load has COMPLETED (<c>Snapshot is not null</c> — F2, NOT
    /// <see cref="HasViolations"/>: the unexpanded-areas appendix is exportable all-clear),
    /// the ONE global busy gate is idle, and the export seam is installed. Pre-load the
    /// commands are inert.</summary>
    private bool CanExportReport() => !IsLoading && Snapshot is not null && _exportDialogs is not null;

    /// <summary>
    /// Exports the live graph as a PNG image to a user-picked path (AP 4.1 / ADR-013 §3/§5/§6).
    /// Gate: a renderer exists (it is the byte source — no renderer, nothing to rasterise) and
    /// the export seam is installed. Flow (spec slice 8, RASTERISE-BEFORE-PICK): re-guard, then
    /// <see cref="IGraphRenderer.ExportPngAsync"/>; the never-throw renderer returns <c>null</c>
    /// on timeout/error — a null raster short-circuits BEFORE the picker is ever consulted (write
    /// NOTHING). On a non-null raster, <c>PickSavePathAsync(Png)</c>; a cancelled pick is a no-op.
    /// Otherwise the EXACT image bytes are written to ONLY that picked <c>.png</c> path — image
    /// data only, no transform. Read-only toward AD: the only write target is the picked local
    /// file; the directory is never touched, and the outbound command carries no untrusted tokens.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanExportGraphImage))]
    private async Task ExportGraphImageAsync()
    {
        if (_disposed || IsLoading || GraphRenderer is not { } renderer || _exportDialogs is null)
        {
            return;
        }

        var bytes = await renderer.ExportPngAsync(_cts.Token);
        if (bytes is null)
        {
            return;
        }

        var path = await _exportDialogs.PickSavePathAsync(ExportKind.Png, _cts.Token);
        if (path is null)
        {
            return;
        }

        await File.WriteAllBytesAsync(path, bytes, _cts.Token);
    }

    /// <summary>Armed iff a renderer exists (the byte source — the AP 2.5 seam allows selection
    /// without one, in which case there is nothing to rasterise), the ONE global busy gate is
    /// idle, and the export seam is installed. Snapshot-independent: the live graph is what the
    /// renderer rasterises.</summary>
    private bool CanExportGraphImage() => !IsLoading && GraphRenderer is not null && _exportDialogs is not null;

    /// <summary>The name-resolution closure handed to the exporter — mirrors
    /// <see cref="OnReportChanged"/> exactly: an in-snapshot object resolves to its
    /// <c>Name</c>, an absent DN falls back to the DN itself (never a provider call, so
    /// export stays read-only toward AD). Core stays App-free (ADR-013 §2 / F4): it takes
    /// this delegate, never the snapshot.</summary>
    private string ResolveSubjectName(string dn) => SubjectNameResolver.Resolve(Snapshot, dn);

    /// <summary>Builds the HTML report header from this workspace's identity (ADR-013 §2):
    /// root DN/name + connection summary, with <see cref="DateTimeOffset.Now"/> as the
    /// injected generation timestamp (the exporter keeps no ambient clock).</summary>
    private ReportHeader BuildReportHeader() =>
        new(RootDn, RootName, ConnectionSummary, DateTimeOffset.Now);

    /// <summary>Writes <paramref name="content"/> to <paramref name="path"/> as UTF-8 WITHOUT
    /// a BOM — the exact bytes the CSV/HTML exporter pinned tests expect.</summary>
    private static Task WriteUtf8Async(string path, string content, CancellationToken cancellationToken) =>
        File.WriteAllTextAsync(path, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), cancellationToken);

    private void OnNodeExpandRequested(string dn)
    {
        // ADR-005 D3: ONE global busy gate (IsLoading) — a gesture during the initial
        // scope load or another in-flight expand is silently dropped, never queued, and
        // Expansion stays reference-unchanged (replacing it would lie to observers).
        if (_disposed || IsLoading || Snapshot is null)
        {
            return;
        }

        Expansion = ExpandAsync(dn, forceFetch: false, _cts.Token);
    }

    /// <summary>
    /// Refresh = forced expand of <see cref="SelectedDn"/> (ADR-005 D4): the SAME
    /// pipeline as a dbltap gesture with the IsLoaded cache check BYPASSED — refresh
    /// exists FOR loaded nodes (<see cref="DirectorySnapshot.SetMembers"/> REPLACES,
    /// the refresh semantics of the data-model contract). A stale-armed Execute
    /// (busy/disposed/renderer-less/selection no longer fetchable) is dropped,
    /// never queued.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanRefresh))]
    private void Refresh()
    {
        if (_disposed || IsLoading || Snapshot is null || GraphRenderer is null
            || SelectedDn is not { } dn || !IsFetchable(Snapshot.GetKind(dn)))
        {
            return;
        }

        Expansion = ExpandAsync(dn, forceFetch: true, _cts.Token);
    }

    /// <summary>Armed iff a renderer exists (the AP 2.5 seam allows selection without
    /// one — nothing to update then), a fetchable snapshot kind is selected, and the
    /// ONE global busy gate is idle (ADR-005 D4); never touches the provider.</summary>
    private bool CanRefresh() =>
        !IsLoading
        && GraphRenderer is not null
        && Snapshot is not null
        && SelectedDn is { } dn
        && IsFetchable(Snapshot.GetKind(dn));

    /// <summary>
    /// Jump-to-node (ADR-010 §5): frame + select a finding's anchor DN from a sidebar
    /// row. SELECTS <paramref name="dn"/> (the detail-panel + highlight sync via
    /// <see cref="OnSelectedDnChanged"/>) AND focuses it on the graph
    /// (<c>FocusAsync([dn])</c>; an unknown/raw-External DN is silently skipped by the
    /// graph surface — never an error). A stale-armed Execute (busy/disposed/renderer-less)
    /// is a silent no-op: it neither selects nor focuses, because the busy gate means the
    /// graph state is half-built. RelayCommand.Execute does not consult CanExecute, so the
    /// guard is re-checked HERE — same drop-never-queue discipline as <see cref="Refresh"/>.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanJump))]
    private async Task JumpAsync(string dn)
    {
        if (_disposed || IsLoading || GraphRenderer is not { } renderer)
        {
            return;
        }

        SelectedDn = dn;
        await renderer.FocusAsync([dn], _cts.Token);
    }

    /// <summary>Armed iff the ONE global busy gate is idle and a renderer exists (ADR-010
    /// §5) — a jump frames the graph, so it needs the renderer the AP 2.5 seam may lack;
    /// never touches the provider or the snapshot.</summary>
    private bool CanJump() => !IsLoading && GraphRenderer is not null;

    /// <summary>The fetchable kinds (ADR-005 D3): groups plus External — a frontier
    /// DN missing from the snapshot's Objects resolves to External by contract.</summary>
    private static bool IsFetchable(AdObjectKind kind) => kind
        is AdObjectKind.GlobalGroup
        or AdObjectKind.DomainLocalGroup
        or AdObjectKind.UniversalGroup
        or AdObjectKind.External;

    /// <summary>
    /// The AP 3.4 roll-up below-map (ADR-010 §4): for every LOADED fetchable-kind node,
    /// the count + max severity of the DISTINCT findings among its loaded transitive
    /// descendants. The ONLY place a <see cref="MembershipTraversal.Walk"/> runs for
    /// roll-ups (data-model.md: the one sanctioned walk; JS never walks) — Walk reads
    /// <c>GetMembers</c> per node (<c>null</c> = unexpanded = excluded), so the count is
    /// loaded-only by construction and never fetches. <c>Visited.Skip(1)</c> drops the
    /// node itself (its own halo is the <c>sev</c> field, not the roll-up). The
    /// <c>report.Violations.Count == 0</c> early exit keeps the all-clear scope free.
    /// </summary>
    private static Dictionary<string, (int Count, RuleSeverity Sev)> ComputeBelow(
        DirectorySnapshot snapshot, RuleReport report)
    {
        var map = new Dictionary<string, (int Count, RuleSeverity Sev)>(Dn.Comparer);
        if (report.Violations.Count == 0)
        {
            return map;
        }

        foreach (var obj in snapshot.Objects)
        {
            if (!IsFetchable(obj.Kind) || !snapshot.IsLoaded(obj.Dn))
            {
                continue;
            }

            var below = report.ViolationsAmong(
                MembershipTraversal.Walk(snapshot, obj.Dn).Visited.Skip(1));
            if (below.Count == 0)
            {
                continue;
            }

            map[obj.Dn] = (below.Count, below.Max(v => v.Severity));
        }

        return map;
    }

    /// <summary>Sets <see cref="GraphSummary"/> (object/edge totals) from <paramref name="graph"/> —
    /// the single write site, called from each graph-build path.</summary>
    private void UpdateGraphSummary(GraphModel graph) =>
        GraphSummary = $"{graph.Nodes.Count} objects, {graph.Edges.Count} edges";

    private async Task ExpandAsync(string dn, bool forceFetch, CancellationToken cancellationToken)
    {
        var snapshot = Snapshot!;
        // Both entry points already drop renderer-less calls (gestures only arrive
        // from a built renderer; Refresh re-guards) — defense in depth for the
        // AP 2.5 seam, where a selection can exist without a renderer.
        if (GraphRenderer is not { } renderer)
        {
            return;
        }

        // ADR-005 D3: the ONE global busy gate is held across the WHOLE pipeline —
        // the focus-only branch included, so the entry guards drop overlapping
        // gestures during an in-flight camera move exactly like during a fetch.
        IsLoading = true;
        var busySet = false;
        try
        {
            var fetchable = IsFetchable(snapshot.GetKind(dn));
            var cachedMembers = snapshot.GetMembers(dn);
            // Cache hit ONLY when there are cached members to frame: a non-group dbltap,
            // or a loaded group with ≥1 member. A loaded-but-EMPTY fetchable group falls
            // through to the fetch branch (ADR-010 §3) — re-fetching is the whole point of
            // re-opening an empty group (it may have gained members), there is nothing to
            // focus on, and the re-fetch re-Evaluates so a now-non-empty group sheds its
            // empty-group finding. A forced Refresh always re-fetches (ADR-005 D4).
            if (!fetchable || (!forceFetch && cachedMembers is { Count: > 0 }))
            {
                // A pure camera move over the node plus its cached members — NEVER a
                // fabricated SetMembers (null ≠ empty; the AP 3.2/3.4 checks read exactly
                // this load state).
                IReadOnlyCollection<string> focus =
                    cachedMembers is null ? [dn] : [dn, .. cachedMembers];
                await renderer.FocusAsync(focus, cancellationToken);
                return;
            }

            LoadError = null; // every new attempt clears the inline error (load policy)

            // ADR-019 (#94): paint the in-canvas busy ring for the directory round-trip.
            // FETCH PATH ONLY — the cache-hit/focus-only branch above already returned, so
            // it has no round-trip to mark. Fire-and-forget: SetBusyAsync never takes the
            // renderer single-flight (must not deadlock the UpdateGraphAsync/FocusAsync this
            // pipeline issues) and never records on the focus channel.
            busySet = true;
            await renderer.SetBusyAsync(dn, true, cancellationToken);

            // Transactional fetch (ADR-005 D3): resolve the parent object only when its
            // DN is missing from the snapshot (External frontier node → true kind), then
            // fetch the members; NOTHING is applied until both provider calls came back.
            AdObject? resolved = null;
            if (!snapshot.TryGetObject(dn, out _))
            {
                resolved = await Provider.GetObjectAsync(dn, cancellationToken);
            }

            // A frontier DN may resolve to a NON-group (e.g. User): GetMembersAsync then
            // answers truthfully empty and SetMembers records REAL load state — never
            // fabricated (null-vs-empty contract; AP 3.2 filters by kind, not this).
            var fetched = await Provider.GetMembersAsync(dn, cancellationToken);
            var memberDns = fetched.Select(m => m.Dn).ToList();

            if (resolved is not null)
            {
                snapshot.AddObject(resolved);
            }

            foreach (var member in fetched)
            {
                snapshot.AddObject(member);
            }

            // SetMembers LAST: marks the parent loaded (possibly loaded-and-empty),
            // member DNs in fetch order.
            snapshot.SetMembers(dn, memberDns);

            Graph = GraphBuilder.Build(snapshot, RootDn);

            // AP 3.4 (ADR-010 §3): full re-Evaluate per expand (ADR-009 — no
            // incrementality), BEFORE the renderer call, inside this IsLoading window. The
            // fresh report + below-map ride the replace-in-place update, so the halos
            // re-attach on the live instance (a re-sent wire field, never preserved state).
            Report = RuleEngine.Evaluate(snapshot, _ruleset);
            var below = ComputeBelow(snapshot, Report);

            // Replace-in-place (ADR-005 D1/D2): exactly ONE UpdateGraphAsync — never a
            // second ShowGraphAsync (destroy + fit would lose the viewport).
            await renderer.UpdateGraphAsync(Graph, Report, below, cancellationToken);
            UpdateGraphSummary(Graph);
            await renderer.FocusAsync([dn, .. memberDns], cancellationToken);
        }
        catch (DirectoryUnavailableException ex)
        {
            // Same inline surface as the scope load; snapshot/graph/renderer untouched.
            LoadError = ex.Message;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Our own Dispose cancelled the expand — settle Expansion quietly.
        }
        finally
        {
            // ADR-019 (#94): clear the busy ring iff the fetch path set it. CancellationToken.None
            // so a cancelled/failed expand still clears the ring (the on-call used the real ct).
            if (busySet)
            {
                await renderer.SetBusyAsync(dn, false, CancellationToken.None);
            }

            // ADR-007 D1: re-project the CURRENT selection — an upserted/refreshed
            // selected object updates the open panel; a stale panel is impossible.
            RecomputeDetailPanel();
            IsLoading = false;
        }
    }

    /// <summary>Cancels an in-flight scope load or expand fetch, then disposes the renderer
    /// (#122 — tears down its WebView, retiring the ADR-024 never-disposed leak); idempotent.
    /// The CTS is cancelled FIRST so the renderer's own-cancellation guards see an already-
    /// cancelled command token.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _cts.Cancel();
        _cts.Dispose();
        GraphRenderer?.Dispose();
    }
}
