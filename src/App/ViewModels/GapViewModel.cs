using System.Collections.ObjectModel;
using System.Text;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using GroupWeaver.App.Export;
using GroupWeaver.App.Graph;
using GroupWeaver.Core.Diff;
using GroupWeaver.Core.Export;
using GroupWeaver.Core.Graph;
using GroupWeaver.Core.Model;
using GroupWeaver.Core.Plan;

namespace GroupWeaver.App.ViewModels;

/// <summary>
/// Gap-analysis step (ADR-015 Slice 7, #66): a sibling shell step the live
/// <see cref="WorkspaceViewModel"/> switches into (like <see cref="PlanViewModel"/>) to compare the
/// proposed <see cref="PlanModel"/> against the borrowed live "Ist" <see cref="DirectorySnapshot"/>.
/// It is shaped like <see cref="PlanViewModel"/> — own renderer (the WebView2 gate +
/// <c>NodeClicked → SelectedDn</c> seam), null-renderer-safe compute pipeline, <see cref="BackCommand"/>
/// and a jump command, ADR-014 dispose discipline — but SIMPLER in one respect: it shows the
/// Ist-vs-Plan DIFF, never rule severity. It never calls <c>RuleEngine.Evaluate</c> nor computes a
/// below-map, and its renderer push carries a <see cref="SnapshotDiff"/>, not a <c>RuleReport</c>.
///
/// <para><b>The compute pipeline.</b> <see cref="RefreshAsync"/> projects the plan
/// (<see cref="PlanProjection.ToSnapshot"/>), computes the <see cref="SnapshotDiff"/>, builds the
/// render UNION (<see cref="SnapshotDiff.BuildUnion"/>) and its graph
/// (<c>GraphBuilder.Build(union, RootDn)</c>), the <see cref="GapSummary"/>, and the
/// <see cref="GapReport"/>, then — only when a renderer exists — pushes the union graph + the diff
/// through <see cref="IGraphRenderer.ShowDiffGraphAsync"/> (wholesale destroy+fit). It is
/// NULL-RENDERER-SAFE (computes everything, skips the push) so it runs headless, mirroring
/// <see cref="PlanViewModel.RevalidateAsync"/>.</para>
///
/// <para><b>Borrowed Ist is read-only.</b> The Ist snapshot is borrowed — the diff/union never
/// mutate it (ADR-005 append-only) and <see cref="Dispose"/> never disposes nor mutates it nor the
/// borrowed plan; the shell owns their lifetime. Contract pinned by
/// <c>tests/GroupWeaver.App.Tests/GapModeTests.cs</c>.</para>
/// </summary>
public sealed partial class GapViewModel : ObservableObject, IDisposable
{
    private readonly CancellationTokenSource _cts = new();
    private readonly DirectorySnapshot _ist;
    private readonly PlanModel _plan;
    private readonly Action? _onBack;

    /// <summary>The export save-dialog seam (mirrors <see cref="WorkspaceViewModel"/>): supplied by
    /// <c>MainWindow.WireExport</c> from the workspace window's <see cref="Avalonia.Controls.TopLevel"/>
    /// once attached (via <see cref="UseExportFileDialogs"/>); <c>null</c> in tests until wired, so the
    /// export commands stay disarmed (<see cref="CanExportDiff"/>).</summary>
    private IExportFileDialogs? _exportDialogs;

    /// <summary>The synthesized "what changed" report the Gap sidebar binds; recomputed on every
    /// <see cref="RefreshAsync"/>. <see cref="OnReportChanged"/> re-projects <see cref="GapRows"/>
    /// and re-evaluates <see cref="HasFindings"/>. Starts at the empty singleton.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasFindings))]
    [NotifyCanExecuteChangedFor(nameof(ExportDiffCsvCommand))]
    [NotifyCanExecuteChangedFor(nameof(ExportDiffHtmlCommand))]
    private GapReport _report = GapReport.Empty;

    /// <summary>DN of the last tapped gap node — the selection seam (carried verbatim; DN strings
    /// are never canonicalized, data-model rule). The setter flips the matching row's highlight in
    /// <see cref="OnSelectedDnChanged"/>.</summary>
    [ObservableProperty]
    private string? _selectedDn;

    public GapViewModel(
        DirectorySnapshot ist,
        PlanModel plan,
        string rootDn,
        Func<IGraphRenderer>? graphRendererFactory = null,
        bool webView2Missing = false,
        Action? onBack = null)
    {
        ArgumentNullException.ThrowIfNull(ist);
        ArgumentNullException.ThrowIfNull(plan);

        _ist = ist; // BORROWED — read-only, never disposed, never mutated.
        _plan = plan;
        RootDn = rootDn;
        _onBack = onBack;
        WebView2Missing = webView2Missing;

        // Mirror PlanViewModel: re-check the runtime probe BEFORE building a renderer, so a missing
        // WebView2 Runtime never even invokes the factory. The Gap step owns its OWN renderer
        // instance — never the workspace's (the two steps render independently).
        if (graphRendererFactory is not null && !webView2Missing)
        {
            GraphRenderer = graphRendererFactory();
            GraphRenderer.NodeClicked += (_, e) => SelectedDn = e.Dn;
        }
    }

    /// <summary>The base OU the gap graph is rooted at (the Ist/Plan scope root).</summary>
    public string RootDn { get; }

    /// <summary>Handed through from the shell (mirrors <see cref="PlanViewModel"/>): a missing
    /// runtime vetoes the renderer factory; the view keeps its placeholder.</summary>
    public bool WebView2Missing { get; }

    /// <summary>The Gap step's OWN renderer; <c>null</c> when the factory is null or the WebView2
    /// Runtime is missing — <see cref="RefreshAsync"/> then skips the push.</summary>
    public IGraphRenderer? GraphRenderer { get; }

    /// <summary>The window-scoped graph-surface coordinator (#122 / ADR-025), pushed in by
    /// <c>MainWindow</c> via <see cref="UseGraphSurfaceCoordinator"/> (mirroring the workspace/plan
    /// export seam). The view uses it to MOUNT the live graph surface (preserving a parked
    /// viewport); <c>null</c> headless / off a window — the view then keeps today's direct
    /// GraphHost mount.</summary>
    public IGraphSurfaceCoordinator? GraphSurfaceCoordinator { get; private set; }

    /// <summary>Installs the window-scoped graph-surface coordinator (#122 / ADR-025): pushed in by
    /// <c>MainWindow</c> through the <c>CurrentStep</c> watcher. The view's mount path reads
    /// <see cref="GraphSurfaceCoordinator"/>; idempotent — the last writer wins.</summary>
    public void UseGraphSurfaceCoordinator(IGraphSurfaceCoordinator coordinator) =>
        GraphSurfaceCoordinator = coordinator;

    /// <summary>The render UNION (<see cref="SnapshotDiff.BuildUnion"/>) — set in
    /// <see cref="RefreshAsync"/> BEFORE <see cref="Report"/> so <see cref="OnReportChanged"/>
    /// resolves subject names against it (it carries both Ist + Plan names, Ist-wins).
    /// <c>null</c> until the first refresh.</summary>
    public DirectorySnapshot? Snapshot { get; private set; }

    /// <summary>The last built union graph model; <c>null</c> until the first refresh.</summary>
    public GraphModel? Graph { get; private set; }

    /// <summary>The last computed Ist-vs-Plan diff; <c>null</c> until the first refresh.</summary>
    public SnapshotDiff? Diff { get; private set; }

    /// <summary>The last per-status diff summary; <c>null</c> until the first refresh.</summary>
    public GapSummary? Summary { get; private set; }

    /// <summary>True once <see cref="Dispose"/> ran — the dispose-discipline observability the
    /// shell teardown pins read.</summary>
    public bool IsDisposed { get; private set; }

    /// <summary>The gap findings the sidebar binds, in canonical report order;
    /// <see cref="OnReportChanged"/> projects <see cref="Report"/>. Subject names resolve against
    /// the union (absent DN falls back to the DN itself).</summary>
    public ObservableCollection<GapRowModel> GapRows { get; } = [];

    /// <summary>True when the gap report has at least one finding.</summary>
    public bool HasFindings => Report.Findings.Count > 0;

    /// <summary>True when the diff has ≥1 known-but-unloaded Ist parent (the honest
    /// "N Ist areas unexpanded" banner). False before the first refresh (no <see cref="Diff"/>).</summary>
    public bool HasUncheckedAreas => Diff is { } d && d.UncheckedParents.Count > 0;

    /// <summary>Back to the Ist workspace (ADR-014): invokes the shell-supplied callback, which
    /// restores the SAME workspace instance — never a reload, never a dispose.</summary>
    [RelayCommand]
    private void Back() => _onBack?.Invoke();

    /// <summary>
    /// The gap compute pipeline (mirrors <see cref="PlanViewModel.RevalidateAsync"/>): project the
    /// plan → diff → build union (+ <see cref="Snapshot"/>) → build graph → summary → report → push.
    /// NULL-RENDERER-SAFE: it computes <see cref="Diff"/>/<see cref="Graph"/>/<see cref="Snapshot"/>/
    /// <see cref="Summary"/>/<see cref="Report"/> and simply skips the renderer push when there is
    /// none, so it runs headless. NO <c>RuleEngine.Evaluate</c>, NO below-map — the gap view shows
    /// the diff, not severity. Uses <see cref="IGraphRenderer.ShowDiffGraphAsync"/> (wholesale
    /// destroy+fit): a gap render is its own seam carrying the diff, never a replace-in-place update.
    /// </summary>
    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        var planSnapshot = PlanProjection.ToSnapshot(_plan);
        Diff = SnapshotDiff.Compute(_ist, planSnapshot);

        // Snapshot (the union) BEFORE Report: OnReportChanged resolves subject names against the
        // union (both Ist + Plan names, Ist-wins) — same ordering invariant as the plan/workspace
        // load.
        var union = SnapshotDiff.BuildUnion(_ist, planSnapshot);
        Snapshot = union;
        Graph = GraphBuilder.Build(union, RootDn);
        Summary = GapSummary.From(Diff);

        // HasUncheckedAreas depends on Diff (now set); notify so a bound banner repaints.
        OnPropertyChanged(nameof(HasUncheckedAreas));

        Report = GapReport.Build(Diff, _ist, planSnapshot);

        if (GraphRenderer is { } renderer)
        {
            await renderer.ShowDiffGraphAsync(Graph, Diff, cancellationToken);
        }
    }

    /// <summary>
    /// <see cref="GapViewModel.JumpToCommand"/> over a row: sets <see cref="SelectedDn"/> to the
    /// row's <c>PrimaryDn</c> (driving the highlight) and frames the anchor on the graph via
    /// <see cref="IGraphRenderer.FocusAsync"/> with exactly <c>[row.PrimaryDn]</c> — the gap jump,
    /// mirroring the workspace/plan jump. Null-safe (a null row, or a disposed VM, is a no-op).
    /// </summary>
    [RelayCommand]
    private async Task JumpTo(GapRowModel? row)
    {
        if (row is null || IsDisposed)
        {
            return;
        }

        SelectedDn = row.PrimaryDn;
        if (GraphRenderer is { } renderer)
        {
            await renderer.FocusAsync([row.PrimaryDn], _cts.Token);
        }
    }

    /// <summary>Installs the real export save-picker seam (mirrors
    /// <see cref="WorkspaceViewModel.UseExportFileDialogs"/>): the production <c>MainWindow</c> calls
    /// this from its own <see cref="Avalonia.Controls.TopLevel"/> (<c>StorageProviderExportFileDialogs</c>)
    /// once attached, so the gap-export commands reach the OS picker. Headless tests inject a fake here.
    /// Re-arms both export commands (the gate includes <c>_exportDialogs is not null</c>); idempotent —
    /// the last writer wins.</summary>
    public void UseExportFileDialogs(IExportFileDialogs dialogs)
    {
        _exportDialogs = dialogs;
        ExportDiffCsvCommand.NotifyCanExecuteChanged();
        ExportDiffHtmlCommand.NotifyCanExecuteChanged();
    }

    /// <summary>
    /// Exports the current gap <see cref="Report"/> + <see cref="Summary"/> as RFC-4180 CSV to a
    /// user-picked path (mirrors <see cref="WorkspaceViewModel.ExportReportCsvAsync"/>). Re-guards in
    /// the body (a stale-armed Execute ignores CanExecute), picks via the seam, and on a non-null pick
    /// writes the pure-Core <see cref="GapReportExporter.ToCsv"/> output (UTF-8, no BOM) to ONLY that
    /// path — a cancelled pick is a no-op. Read-only toward AD: the only write target is the picked
    /// local file; the directory is never touched (the name closure reads the union snapshot only).
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanExportDiff))]
    private async Task ExportDiffCsvAsync()
    {
        if (IsDisposed || _exportDialogs is null || ReferenceEquals(Report, GapReport.Empty) || Summary is null)
        {
            return;
        }

        var path = await _exportDialogs.PickSavePathAsync(ExportKind.DiffCsv, _cts.Token);
        if (path is null)
        {
            return;
        }

        var csv = GapReportExporter.ToCsv(Report, Summary, ResolveSubjectName);
        await WriteUtf8Async(path, csv, _cts.Token);
    }

    /// <summary>
    /// Exports the current gap <see cref="Report"/> + <see cref="Summary"/> as a self-contained HTML
    /// file to a user-picked path (mirrors <see cref="WorkspaceViewModel.ExportReportHtmlAsync"/>). Same
    /// re-guard, pick and write-once discipline as <see cref="ExportDiffCsvAsync"/>; the HTML carries a
    /// <see cref="DiffReportHeader"/> built from the root identity + the current wall clock
    /// (<see cref="BuildDiffReportHeader"/>).
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanExportDiff))]
    private async Task ExportDiffHtmlAsync()
    {
        if (IsDisposed || _exportDialogs is null || ReferenceEquals(Report, GapReport.Empty) || Summary is null)
        {
            return;
        }

        var path = await _exportDialogs.PickSavePathAsync(ExportKind.DiffHtml, _cts.Token);
        if (path is null)
        {
            return;
        }

        var html = GapReportExporter.ToHtml(Report, Summary, ResolveSubjectName, BuildDiffReportHeader());
        await WriteUtf8Async(path, html, _cts.Token);
    }

    /// <summary>Armed iff the export seam is installed, the VM is not disposed, and a non-empty gap
    /// report has been computed (<c>Report</c> is no longer the <see cref="GapReport.Empty"/> sentinel —
    /// the same "a refresh has produced a diff" signal <see cref="HasFindings"/> tracks). Pre-refresh
    /// the commands are inert.</summary>
    private bool CanExportDiff() =>
        !IsDisposed && _exportDialogs is not null && !ReferenceEquals(Report, GapReport.Empty);

    /// <summary>The name-resolution closure handed to the exporter — mirrors
    /// <see cref="OnReportChanged"/> exactly: an in-union object resolves to its <c>Name</c>, an absent
    /// DN falls back to the DN itself (never a provider call, so export stays read-only toward AD). Core
    /// stays App-free: it takes this delegate, never the snapshot.</summary>
    private string ResolveSubjectName(string dn) =>
        Snapshot is not null && Snapshot.TryGetObject(dn, out var obj) ? obj.Name : dn;

    /// <summary>Builds the gap HTML report header: the root DN, the root's union-resolved name, a
    /// deterministic gap-source summary, and <see cref="DateTimeOffset.Now"/> as the INJECTED generation
    /// timestamp (the exporter keeps no ambient clock — keeping it deterministic for tests).</summary>
    private DiffReportHeader BuildDiffReportHeader() =>
        new(RootDn, ResolveSubjectName(RootDn), "Gap analysis: plan compared against the current structure", DateTimeOffset.Now);

    /// <summary>Writes <paramref name="content"/> to <paramref name="path"/> as UTF-8 WITHOUT a BOM —
    /// the exact bytes the CSV/HTML exporter pinned tests expect (mirrors
    /// <see cref="WorkspaceViewModel.WriteUtf8Async"/>).</summary>
    private static Task WriteUtf8Async(string path, string content, CancellationToken cancellationToken) =>
        File.WriteAllTextAsync(path, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), cancellationToken);

    /// <summary>Re-projects <see cref="Report"/>'s findings into <see cref="GapRows"/>: canonical
    /// report order, subject name resolved against the union <see cref="Snapshot"/> (absent DN
    /// falls back to the anchor DN). The union resolves both Ist + Plan names (Ist-wins).</summary>
    partial void OnReportChanged(GapReport value)
    {
        GapRows.Clear();
        foreach (var finding in value.Findings)
        {
            var anchor = finding.Dns[0];
            var subject = Snapshot is not null && Snapshot.TryGetObject(anchor, out var obj)
                ? obj.Name
                : anchor;
            GapRows.Add(new GapRowModel(finding.Kind, finding.Message, subject, anchor));
        }

        // Re-apply the selection-sync highlight over the fresh rows (a persisting selection keeps
        // lighting its anchor across a re-Refresh).
        HighlightActiveRows();
    }

    /// <summary>Selection sync (mirrors <see cref="PlanViewModel.OnSelectedDnChanged"/>): flips
    /// <see cref="GapRowModel.IsActive"/> on every <see cref="GapRows"/> row whose <c>PrimaryDn</c>
    /// matches the new selection (under <c>Dn.Comparer</c>, never ordinal).</summary>
    partial void OnSelectedDnChanged(string? value) => HighlightActiveRows();

    /// <summary>Re-applies the sidebar highlight over the current rows (both
    /// <see cref="OnReportChanged"/> and the selection setter call it), so a selection that
    /// persists across a re-Refresh keeps lighting its anchor.</summary>
    private void HighlightActiveRows()
    {
        foreach (var row in GapRows)
        {
            row.IsActive = SelectedDn is not null && Dn.Comparer.Equals(row.PrimaryDn, SelectedDn);
        }
    }

    /// <summary>Cancels any in-flight refresh render, then disposes the renderer (#122 — tears down
    /// its WebView, retiring the ADR-024 never-disposed leak); idempotent. The CTS is cancelled FIRST
    /// so the renderer's own-cancellation guards see an already-cancelled command token. The shell
    /// disposes this step at teardown — and now also when Gap's Back abandons it (#122 Slice 5).
    /// NEVER disposes nor mutates the borrowed Ist snapshot or plan (ADR-015 D3 / ADR-005
    /// append-only) — the shell owns their lifetime.</summary>
    public void Dispose()
    {
        if (IsDisposed)
        {
            return;
        }

        IsDisposed = true;
        _cts.Cancel();
        _cts.Dispose();
        GraphRenderer?.Dispose();
    }
}
