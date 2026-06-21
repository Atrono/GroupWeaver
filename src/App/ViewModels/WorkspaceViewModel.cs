using System.Collections.ObjectModel;
using System.Text;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GroupWeaver.App.Export;
using GroupWeaver.App.Graph;
using GroupWeaver.App.Rules;
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
    private DetailPanelModel? _detailPanel;

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

    public WorkspaceViewModel(
        IDirectoryProvider provider,
        AdObject root,
        DirectoryConnection connection,
        bool webView2Missing = false,
        Func<IGraphRenderer>? graphRendererFactory = null,
        EffectiveRuleset? ruleset = null,
        IExportFileDialogs? exportDialogs = null)
    {
        Provider = provider;
        Root = root;
        Connection = connection;
        WebView2Missing = webView2Missing;
        _exportDialogs = exportDialogs;

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

    /// <summary>The AP 3.4 violations sidebar rows (ADR-010 §5), in canonical report
    /// order (unshuffled — ADR-009): <see cref="OnReportChanged"/> projects
    /// <see cref="Report"/>'s <see cref="RuleReport.Violations"/> into it, with
    /// <see cref="ViolationRowModel.SubjectName"/> resolved snapshot-only. Bound by
    /// <see cref="Views.ViolationsSidebarView"/>.</summary>
    public ObservableCollection<ViolationRowModel> Violations { get; } = [];

    /// <summary>Drives the sidebar all-clear state: <c>true</c> when the report has at
    /// least one finding. Recomputed on <see cref="Report"/> change.</summary>
    public bool HasViolations => Report.Violations.Count > 0;

    /// <summary>Drives the "unexpanded areas are unchecked" hint: <c>true</c> when the
    /// report's <see cref="RuleReport.UncheckedDns"/> is non-empty (load-state truth,
    /// never ignore-filtered — ADR-009). Recomputed on <see cref="Report"/> change.</summary>
    public bool HasUncheckedAreas => Report.UncheckedDns.Count > 0;

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
        DetailPanel = Snapshot is null ? null : DetailPanelModel.Build(Snapshot, SelectedDn);

    /// <summary>Re-projects <see cref="Report"/>'s findings into <see cref="Violations"/>
    /// (ADR-010 §5): canonical report order, unshuffled (ADR-009); the subject name is
    /// resolved snapshot-only (raw-External anchors fall back to the DN — never a provider
    /// call). The all-clear text and the unchecked-areas hint flip via the generated
    /// <see cref="HasViolations"/>/<see cref="HasUncheckedAreas"/> notifications. The
    /// selection-sync highlight is re-applied over the fresh rows so a selection that
    /// persists across a re-Evaluate keeps lighting its anchor.</summary>
    partial void OnReportChanged(RuleReport value)
    {
        Violations.Clear();
        foreach (var violation in value.Violations)
        {
            var subject = Snapshot is not null && Snapshot.TryGetObject(violation.PrimaryDn, out var obj)
                ? obj!.Name
                : violation.PrimaryDn;
            Violations.Add(new ViolationRowModel(
                violation.Severity, violation.Message, subject, violation.PrimaryDn));
        }

        HighlightActiveRows(SelectedDn);
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

            GraphSummary = $"{Graph.Nodes.Count} objects, {Graph.Edges.Count} edges";
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
    private string ResolveSubjectName(string dn) =>
        Snapshot is not null && Snapshot.TryGetObject(dn, out var obj) ? obj!.Name : dn;

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
            GraphSummary = $"{Graph.Nodes.Count} objects, {Graph.Edges.Count} edges";
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

    /// <summary>Cancels an in-flight scope load or expand fetch; idempotent.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _cts.Cancel();
        _cts.Dispose();
    }
}
