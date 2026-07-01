using System.Collections.ObjectModel;
using System.Reflection;
using System.Text;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GroupWeaver.App.Export;
using GroupWeaver.App.Graph;
using GroupWeaver.App.Rules;
using GroupWeaver.Core.Graph;
using GroupWeaver.Core.Model;
using GroupWeaver.Core.Plan;
using GroupWeaver.Core.Rules;

namespace GroupWeaver.App.ViewModels;

/// <summary>
/// Plan Mode step (AP 4.2.2 / ADR-014): a sibling shell step the live
/// <see cref="WorkspaceViewModel"/> switches into and back out of. It authors a PROPOSED
/// AGUDLP structure into a mutable <see cref="PlanModel"/> (this slice ships it EMPTY —
/// editing commands and seed-from-Ist are AP 4.2.3) and live-validates it by reusing the
/// UNCHANGED engine and builder via <see cref="PlanProjection"/>: project →
/// <c>GraphBuilder.Build</c> → <c>RuleEngine.Evaluate</c> → roll-up → render. The pipeline
/// mirrors <see cref="WorkspaceViewModel.ApplyRulesetAsync"/> but is null-renderer-safe
/// (it computes the report and graph, then skips the renderer push when there is none) so
/// it runs headless.
///
/// <para>It owns its OWN <see cref="IGraphRenderer"/> instance (never the workspace's —
/// the two steps render independently), threads selection through
/// <c>NodeClicked → SelectedDn</c> verbatim, and projects <see cref="Report"/> into
/// <see cref="Violations"/> exactly as the workspace does. <see cref="BackCommand"/>
/// invokes the supplied callback to return to the SAME (never-disposed) workspace instance
/// — the shell holds and disposes both steps at teardown. Contract pinned by
/// <c>tests/GroupWeaver.App.Tests/PlanModeTests.cs</c>.</para>
/// </summary>
public sealed partial class PlanViewModel : ObservableObject, IDisposable
{
    private readonly CancellationTokenSource _cts = new();
    private readonly Action? _onBackToExplore;
    private Ruleset _ruleset;

    /// <summary>The ADR-015 (#66) "Gap analysis" callback: the shell installs it via
    /// <see cref="UseGapAnalysisCallback"/> when it creates the plan step, so the header button can
    /// switch into Gap mode. <c>null</c> until installed — <see cref="GapAnalysisCommand"/> then
    /// stays disarmed (<see cref="CanGapAnalysis"/>), keeping every pre-Slice-8 test on the default.
    /// Mirrors <see cref="WorkspaceViewModel"/>'s <c>_onDesignPlan</c> exactly; the
    /// <c>BaseOuDn == RootDn</c> + snapshot-null gate lives in the shell seam, not here.</summary>
    private Action? _onGapAnalysis;

    /// <summary>The injected, deterministic generation clock (AP 4.2.4 / ADR-014): the plan
    /// export builds its <see cref="PlanScriptHeader.GeneratedAt"/> from this, never the wall
    /// clock — so two exports of the same plan are byte-identical. Defaults to
    /// <c>() =&gt; DateTimeOffset.Now</c>; a test injects a fixed instant.</summary>
    private readonly Func<DateTimeOffset> _clock;

    /// <summary>The injected tool version stamped into the export header (AP 4.2.4 / ADR-014):
    /// the assembly informational version by default (Program.cs pattern, "unknown" fallback),
    /// a fixed string in tests — so the exported <c>.ps1</c> is reproducible.</summary>
    private readonly string _toolVersion;

    /// <summary>The AP 4.1 export save-dialog seam (ADR-013 §5): supplied by the production
    /// <c>MainWindow</c> from the window's <see cref="Avalonia.Controls.TopLevel"/> once attached
    /// (via <see cref="UseExportFileDialogs"/>, mirroring <see cref="WorkspaceViewModel"/>);
    /// <c>null</c> until wired, so <see cref="ExportPlanScriptCommand"/> stays disarmed
    /// (<see cref="CanExportPlanScript"/>).</summary>
    private IExportFileDialogs? _exportDialogs;

    /// <summary>The AP 3.4 rule report the violations list binds; re-Evaluated on every
    /// <see cref="RevalidateAsync"/> and <see cref="ApplyRulesetAsync"/>.
    /// <see cref="OnReportChanged"/> re-projects <see cref="Violations"/> and re-evaluates
    /// <see cref="HasViolations"/>/<see cref="HasUncheckedAreas"/>. Starts empty.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasViolations))]
    [NotifyPropertyChangedFor(nameof(HasUncheckedAreas))]
    private RuleReport _report = RuleReport.Empty;

    /// <summary>DN of the last tapped plan node — the selection seam (carried verbatim;
    /// DN strings are never canonicalized, data-model rule). The setter re-resolves
    /// <see cref="SelectedNodeRow"/> and the sidebar highlight in <see cref="OnSelectedDnChanged"/>.</summary>
    [ObservableProperty]
    private string? _selectedDn;

    /// <summary>The add-object name box (AP 4.2.3). The DN is formed from it on Add;
    /// a blank/whitespace name disarms <see cref="AddObjectCommand"/>.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AddObjectCommand))]
    private string _newObjectName = "";

    /// <summary>The add-object kind combo selection. <see cref="NewObjectIsUser"/> derives the
    /// SAM box visibility from it; kept across a successful add (repeat-add the same kind).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NewObjectIsUser))]
    private PlanCreatableKind _newObjectKind = PlanCreatableKind.GlobalGroup;

    /// <summary>The add-object SAM box (only meaningful for <see cref="PlanCreatableKind.User"/>;
    /// a group never receives it — the add command passes null for a group).</summary>
    [ObservableProperty]
    private string _newObjectSam = "";

    /// <summary>The inline edit error: the last <c>PlanConflictException.Message</c> a rejected
    /// mutation surfaced; CLEARED (null) on every successful mutation. The one error surface.</summary>
    [ObservableProperty]
    private string? _editError;

    /// <summary>The rename box (pre-filled from the selected row's Name); blank disarms
    /// <see cref="RenameSelectedCommand"/>.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RenameSelectedCommand))]
    private string _renameText = "";

    /// <summary>The Objects-list selection (AP 4.2.3): changing it drives
    /// <see cref="SelectedDn"/> + <see cref="RenameText"/> + the command guards in
    /// <see cref="OnSelectedNodeRowChanged"/>.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelectedNode))]
    [NotifyCanExecuteChangedFor(nameof(RemoveSelectedCommand))]
    [NotifyCanExecuteChangedFor(nameof(RenameSelectedCommand))]
    private PlanNodeRowModel? _selectedNodeRow;

    /// <summary>The membership-form PARENT combo selection (a group, from
    /// <see cref="GroupNodes"/>). Notifies <see cref="AddMemberCommand"/> CanExecute.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AddMemberCommand))]
    private PlanNodeRowModel? _memberParentRow;

    /// <summary>The membership-form CHILD combo selection (any node, from
    /// <see cref="Nodes"/>). Notifies <see cref="AddMemberCommand"/> CanExecute.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AddMemberCommand))]
    private PlanNodeRowModel? _memberChildRow;

    /// <summary>Re-entrancy guard for the <see cref="SelectedDn"/>↔<see cref="SelectedNodeRow"/>
    /// round-trip (it terminates naturally — SetProperty short-circuits on equal DN strings —
    /// but the guard makes the two setters one-directional per edit, never racing).</summary>
    private bool _syncingSelection;

    public PlanViewModel(
        string baseOuDn,
        EffectiveRuleset ruleset,
        Func<IGraphRenderer>? graphRendererFactory = null,
        bool webView2Missing = false,
        Action? onBackToExplore = null,
        IExportFileDialogs? exportDialogs = null,
        Func<DateTimeOffset>? clock = null,
        string? toolVersion = null)
    {
        ArgumentNullException.ThrowIfNull(ruleset);

        Plan = new PlanModel(baseOuDn);
        _ruleset = ruleset.Ruleset;
        _onBackToExplore = onBackToExplore;
        WebView2Missing = webView2Missing;

        // AP 4.2.4 deterministic-export inputs (ADR-014): the header is built from the injected
        // clock + tool version so the .ps1 is reproducible (a test pins exact bytes). The version
        // defaults to the assembly informational version (Program.cs pattern, "unknown" fallback).
        _exportDialogs = exportDialogs;
        _clock = clock ?? (() => DateTimeOffset.Now);
        _toolVersion = toolVersion
            ?? Assembly.GetExecutingAssembly()
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
                .InformationalVersion
            ?? "unknown";

        // Mirror the workspace: re-check the runtime probe BEFORE building a renderer, so a
        // missing WebView2 Runtime never even invokes the factory. The plan owns its OWN
        // renderer instance — never the workspace's (the two steps render independently).
        if (graphRendererFactory is not null && !webView2Missing)
        {
            GraphRenderer = graphRendererFactory();
            GraphRenderer.NodeClicked += (_, e) => SelectedDn = e.Dn;
        }
    }

    /// <summary>The mutable authoring store (ADR-014). Starts EMPTY in this slice.</summary>
    public PlanModel Plan { get; }

    /// <summary>The base OU every authored object is formed under (the plan root).</summary>
    public string BaseOuDn => Plan.BaseOuDn;

    /// <summary>Handed through from the shell (mirrors <see cref="WorkspaceViewModel"/>):
    /// a missing runtime vetoes the renderer factory; the view keeps its placeholder.</summary>
    public bool WebView2Missing { get; }

    /// <summary>The plan's OWN renderer; <c>null</c> when the factory is null or the
    /// WebView2 Runtime is missing — <see cref="RevalidateAsync"/> then skips the push.</summary>
    public IGraphRenderer? GraphRenderer { get; }

    /// <summary>The window-scoped graph-surface coordinator (#122 / ADR-025), pushed in by
    /// <c>MainWindow</c> via <see cref="UseGraphSurfaceCoordinator"/> (mirroring the export seam).
    /// The view uses it to MOUNT the live graph surface (preserving a parked viewport); <c>null</c>
    /// headless / off a window — the view then keeps today's direct GraphHost mount.</summary>
    public IGraphSurfaceCoordinator? GraphSurfaceCoordinator { get; private set; }

    /// <summary>The last projected snapshot; <c>null</c> until the first revalidate.</summary>
    public DirectorySnapshot? Snapshot { get; private set; }

    /// <summary>The last built graph model; <c>null</c> until the first revalidate.</summary>
    public GraphModel? Graph { get; private set; }

    /// <summary>True once <see cref="Dispose"/> ran — the dispose-discipline observability
    /// the shell teardown pins read (AP 4.2.2).</summary>
    public bool IsDisposed { get; private set; }

    /// <summary>The violations list the plan sidebar binds, in canonical report order
    /// (unshuffled — ADR-009); <see cref="OnReportChanged"/> projects <see cref="Report"/>.
    /// Subject names resolve snapshot-only (raw-External anchors fall back to the DN).</summary>
    public ObservableCollection<ViolationRowModel> Violations { get; } = [];

    /// <summary>True when the report has at least one finding.</summary>
    public bool HasViolations => Report.Violations.Count > 0;

    /// <summary>Always false in Plan Mode: a plan is fully authored, so the projection
    /// leaves no unexpanded (null-member) group and <see cref="RuleReport.UncheckedDns"/>
    /// is empty by construction. Exposed for sidebar parity with the workspace.</summary>
    public bool HasUncheckedAreas => Report.UncheckedDns.Count > 0;

    /// <summary>The four <see cref="PlanCreatableKind"/> values the add-object kind combo
    /// binds (AP 4.2.3) — the VM-exposed static items source (no <c>ObjectDataProvider</c>).</summary>
    public IReadOnlyList<PlanCreatableKind> CreatableKinds { get; } =
    [
        PlanCreatableKind.User,
        PlanCreatableKind.GlobalGroup,
        PlanCreatableKind.DomainLocalGroup,
        PlanCreatableKind.UniversalGroup,
    ];

    /// <summary>Every authored node, ordered by Name (OrdinalIgnoreCase, then Dn for stability)
    /// — bound by the Objects list AND the membership CHILD combo. Rebuilt by
    /// <see cref="RefreshAuthoredCollections"/> after every mutation.</summary>
    public ObservableCollection<PlanNodeRowModel> Nodes { get; } = [];

    /// <summary>The group-kind subset of <see cref="Nodes"/> (<c>PlanKindMap.IsGroup</c>), same
    /// ordering — bound by the membership PARENT combo (only a group can have members).</summary>
    public ObservableCollection<PlanNodeRowModel> GroupNodes { get; } = [];

    /// <summary>Every authored edge as a "parent ← child" row (names resolved via
    /// <c>Plan.TryGetNode</c>, DN fallback), ordered by parent Name then child Name — bound by
    /// the Memberships list. Rebuilt by <see cref="RefreshAuthoredCollections"/>.</summary>
    public ObservableCollection<PlanEdgeRowModel> Memberships { get; } = [];

    /// <summary>True while the SAM box is meaningful — derived from <see cref="NewObjectKind"/>
    /// (only <see cref="PlanCreatableKind.User"/>). Drives the SAM box visibility.</summary>
    public bool NewObjectIsUser => NewObjectKind == PlanCreatableKind.User;

    /// <summary>True while a node row is selected — the selected-node action block visibility
    /// and the Remove/Rename command guards.</summary>
    public bool HasSelectedNode => SelectedNodeRow is not null;

    /// <summary>Back to the Ist workspace (ADR-014): invokes the shell-supplied callback,
    /// which restores the SAME workspace instance — never a reload, never a dispose.</summary>
    [RelayCommand]
    private void Back() => _onBackToExplore?.Invoke();

    /// <summary>
    /// Deliberately starts the plan over (#122 keep-alive): clears the authored content
    /// (<see cref="PlanModel.Clear"/> — nodes + edges, <see cref="BaseOuDn"/> kept), resets the
    /// selection/error/form state, rebuilds the authored collections, then re-runs the SAME
    /// live-validation loop every mutating command uses. The plan instance and its renderer
    /// survive — Back keeps the plan alive, so this is the one way to empty it on purpose.
    /// No AD-write code path: it only mutates the in-memory authoring model.
    /// </summary>
    [RelayCommand]
    private async Task NewPlanAsync()
    {
        Plan.Clear();
        EditError = null;
        SelectedNodeRow = null;
        SelectedDn = null;
        MemberParentRow = null;
        MemberChildRow = null;
        RefreshAuthoredCollections();
        await RevalidateAsync(_cts.Token);
    }

    /// <summary>The "Gap analysis" header button (ADR-015 / #66): switches the shell into Gap mode
    /// via the installed callback. Armed iff the callback is installed (the shell installs it when
    /// it creates the plan step); a stale-armed Execute with no callback is a silent no-op. Mirrors
    /// <see cref="WorkspaceViewModel"/>'s <c>DesignPlan</c> exactly.</summary>
    [RelayCommand(CanExecute = nameof(CanGapAnalysis))]
    private void GapAnalysis() => _onGapAnalysis?.Invoke();

    private bool CanGapAnalysis() => _onGapAnalysis is not null;

    /// <summary>Installs the shell's Gap-mode switch callback (ADR-015 / #66) and re-arms
    /// <see cref="GapAnalysisCommand"/>. Called by the shell when it creates the plan step (in
    /// <c>OnDesignPlan</c>); headless tests reach it through the live shell's <c>OnDesignPlan</c>.
    /// Mirrors <see cref="WorkspaceViewModel.UseDesignPlanCallback"/>.</summary>
    public void UseGapAnalysisCallback(Action onGapAnalysis)
    {
        _onGapAnalysis = onGapAnalysis;
        GapAnalysisCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Installs the real export save-picker seam (AP 4.2.4 / ADR-014; mirrors
    /// <see cref="WorkspaceViewModel.UseExportFileDialogs"/>): the production <c>MainWindow</c>
    /// calls this from its own <see cref="Avalonia.Controls.TopLevel"/>
    /// (<c>StorageProviderExportFileDialogs</c>) once attached, so the export command reaches the
    /// OS picker. Headless tests inject a fake here. Re-arms <see cref="ExportPlanScriptCommand"/>
    /// (its gate includes <c>_exportDialogs is not null</c>); idempotent — the last writer wins.</summary>
    public void UseExportFileDialogs(IExportFileDialogs dialogs)
    {
        _exportDialogs = dialogs;
        ExportPlanScriptCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Installs the window-scoped graph-surface coordinator (#122 / ADR-025): pushed in by
    /// <c>MainWindow</c> through the <c>CurrentStep</c> watcher, exactly like
    /// <see cref="UseExportFileDialogs"/>. The view's mount path reads
    /// <see cref="GraphSurfaceCoordinator"/>; idempotent — the last writer wins.</summary>
    public void UseGraphSurfaceCoordinator(IGraphSurfaceCoordinator coordinator) =>
        GraphSurfaceCoordinator = coordinator;

    /// <summary>
    /// Exports the authored plan as an inert PowerShell script to a user-picked path
    /// (AP 4.2.4 / ADR-014; mirrors the workspace CSV/HTML discipline). NO AD-write code path:
    /// the FROZEN pure-Core <see cref="PlanScriptExporter.ToPowerShell"/> produces INERT string
    /// text the app never runs; the only write target is the picked local <c>.ps1</c> file (the
    /// directory is never touched). Re-guards in the body (a stale-armed Execute ignores
    /// CanExecute), picks via the seam, and on a non-null pick builds a
    /// <see cref="PlanScriptHeader"/> from <see cref="BaseOuDn"/> + the INJECTED tool version +
    /// the INJECTED clock instant, then writes the exporter output (UTF-8, NO BOM) to ONLY that
    /// path. A control-char token surfaces into <see cref="EditError"/> instead of throwing
    /// (defense-in-depth — <c>PlanModel.AddNode</c> already blocks them at author time).
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanExportPlanScript))]
    private async Task ExportPlanScriptAsync()
    {
        if (IsDisposed || _exportDialogs is null || Plan.Nodes.Count == 0)
        {
            return;
        }

        var path = await _exportDialogs.PickSavePathAsync(ExportKind.Ps1, _cts.Token);
        if (path is null)
        {
            return;
        }

        var header = new PlanScriptHeader(BaseOuDn, _toolVersion, _clock());
        string script;
        try
        {
            script = PlanScriptExporter.ToPowerShell(Plan, header);
        }
        catch (PlanScriptException ex)
        {
            EditError = ex.Message;
            return;
        }

        EditError = null;
        await File.WriteAllTextAsync(
            path, script, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), _cts.Token);
    }

    /// <summary>Armed iff the export seam is installed AND the plan has ≥1 node (a control-char
    /// token can never reach here — <c>AddNode</c> blocks it). Re-gated from
    /// <see cref="RefreshAuthoredCollections"/> (every add/remove) and
    /// <see cref="UseExportFileDialogs"/>.</summary>
    private bool CanExportPlanScript() =>
        !IsDisposed && _exportDialogs is not null && Plan.Nodes.Count > 0;

    // ===== AP 4.2.3 editor commands: form -> model mutation -> collections -> revalidate =====
    // Each mutating command catches PlanConflictException into EditError (a value, never a
    // throw to the user), rebuilds the authored collections, then runs the EXISTING live-
    // validation loop with the VM's own token. No code path writes to AD — a plan is an
    // in-memory authoring model projected to a snapshot for the unchanged engine.

    /// <summary>Authors a node from the add-object form (AP 4.2.3). SAM only for a User
    /// (the form derives <see cref="NewObjectIsUser"/> from the kind). A conflict surfaces
    /// into <see cref="EditError"/> and the form is RETAINED so the user can fix it; a success
    /// clears the error + the name/SAM boxes (the kind stays, for repeat adds).</summary>
    [RelayCommand(CanExecute = nameof(CanAddObject))]
    private async Task AddObjectAsync()
    {
        try
        {
            Plan.AddNode(
                NewObjectKind,
                NewObjectName.Trim(),
                NewObjectIsUser && !string.IsNullOrWhiteSpace(NewObjectSam) ? NewObjectSam.Trim() : null);
        }
        catch (PlanConflictException ex)
        {
            EditError = ex.Message;
            return;
        }

        EditError = null;
        NewObjectName = "";
        NewObjectSam = "";
        RefreshAuthoredCollections();
        await RevalidateAsync(_cts.Token);
    }

    private bool CanAddObject() => !string.IsNullOrWhiteSpace(NewObjectName);

    /// <summary>Removes the selected node and cascades its incident edges (the model's
    /// RemoveNode cascade), clears the selection, and revalidates.</summary>
    [RelayCommand(CanExecute = nameof(HasSelectedNode))]
    private async Task RemoveSelectedAsync()
    {
        Plan.RemoveNode(SelectedNodeRow!.Dn);
        EditError = null;
        SelectedNodeRow = null;
        SelectedDn = null;
        RefreshAuthoredCollections();
        await RevalidateAsync(_cts.Token);
    }

    /// <summary>Renames the selected node (replace-by-DN; the DN is identity). The selection
    /// FOLLOWS the rename (SelectedDn = the new DN, SelectedNodeRow re-resolved, RenameText
    /// tracks the new name); incident memberships keep their endpoints (rewritten). A conflict
    /// (unknown DN or rename onto an existing different DN) surfaces into
    /// <see cref="EditError"/> with no change.</summary>
    [RelayCommand(CanExecute = nameof(CanRenameSelected))]
    private async Task RenameSelectedAsync()
    {
        var dn = SelectedNodeRow!.Dn;
        var newName = RenameText.Trim();
        try
        {
            Plan.RenameNode(dn, newName);
        }
        catch (PlanConflictException ex)
        {
            EditError = ex.Message;
            return;
        }

        EditError = null;
        RefreshAuthoredCollections();
        // The new DN is the rename's identity; selecting it re-resolves SelectedNodeRow to the
        // renamed row and seeds RenameText with the new name (OnSelectedDnChanged).
        SelectedDn = Plan.FormDn(newName);
        await RevalidateAsync(_cts.Token);
    }

    private bool CanRenameSelected() => HasSelectedNode && !string.IsNullOrWhiteSpace(RenameText);

    /// <summary>Authors a membership from the form (AP 4.2.3): the parent (a group) lists the
    /// child. Idempotent (no error if the edge already existed — the model returns false). A
    /// conflict (unknown endpoint / non-group parent — guarded against by CanExecute) surfaces
    /// into <see cref="EditError"/>. A self-membership and cycles are AUTHORABLE (ADR-014) — the
    /// model does not reject them; the engine reports them as findings.</summary>
    [RelayCommand(CanExecute = nameof(CanAddMember))]
    private async Task AddMemberAsync()
    {
        try
        {
            Plan.AddEdge(MemberParentRow!.Dn, MemberChildRow!.Dn);
        }
        catch (PlanConflictException ex)
        {
            EditError = ex.Message;
            return;
        }

        EditError = null;
        RefreshAuthoredCollections();
        await RevalidateAsync(_cts.Token);
    }

    /// <summary>Armed iff both endpoints are selected AND the parent row is a group (a user can
    /// never have members — the model would throw, so the command guards via CanExecute).</summary>
    private bool CanAddMember() =>
        MemberParentRow is not null
        && MemberChildRow is not null
        && PlanKindMap.IsGroup(MemberParentRow.Kind);

    /// <summary>Removes a membership row (the per-row Remove the memberships list binds) and
    /// revalidates.</summary>
    [RelayCommand]
    private async Task RemoveMemberAsync(PlanEdgeRowModel row)
    {
        Plan.RemoveEdge(row.ParentDn, row.ChildDn);
        EditError = null;
        RefreshAuthoredCollections();
        await RevalidateAsync(_cts.Token);
    }

    /// <summary>
    /// Rebuilds <see cref="Nodes"/>/<see cref="GroupNodes"/>/<see cref="Memberships"/> from the
    /// model after a mutation, then re-resolves <see cref="SelectedNodeRow"/>/
    /// <see cref="MemberParentRow"/>/<see cref="MemberChildRow"/> to the NEW row instances
    /// matching their current DN under <c>Dn.Comparer</c> (so a rebuild never drops a live
    /// selection; a vanished DN resets that reference to null). The combos/list bind THESE
    /// instances, so selection must use them. Does NOT call <see cref="RevalidateAsync"/> — each
    /// command runs that separately (it is async).
    /// </summary>
    private void RefreshAuthoredCollections()
    {
        var ordered = Plan.Nodes
            .OrderBy(n => n.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(n => n.Dn, Dn.Comparer)
            .Select(n => new PlanNodeRowModel(n.Dn, n.Name, n.Kind))
            .ToList();

        Nodes.Clear();
        GroupNodes.Clear();
        foreach (var row in ordered)
        {
            Nodes.Add(row);
            if (PlanKindMap.IsGroup(row.Kind))
            {
                GroupNodes.Add(row);
            }
        }

        var edges = Plan.Edges
            .Select(e => new PlanEdgeRowModel(
                e.ParentDn, NameOf(e.ParentDn), e.ChildDn, NameOf(e.ChildDn)))
            .OrderBy(e => e.ParentName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(e => e.ChildName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        Memberships.Clear();
        foreach (var edge in edges)
        {
            Memberships.Add(edge);
        }

        // Re-resolve the three live row references to the fresh instances (or null if gone).
        // SelectedNodeRow is re-resolved through its own setter so SelectedDn/RenameText follow.
        SelectedNodeRow = Resolve(Nodes, SelectedNodeRow?.Dn);
        MemberParentRow = Resolve(GroupNodes, MemberParentRow?.Dn);
        MemberChildRow = Resolve(Nodes, MemberChildRow?.Dn);

        // Re-gate plan export across the zero-node boundary (AP 4.2.4): adding the first object
        // arms it, removing the last disarms it (the dialogs half is set by UseExportFileDialogs).
        ExportPlanScriptCommand.NotifyCanExecuteChanged();
    }

    /// <summary>The fresh <paramref name="rows"/> instance whose Dn matches <paramref name="dn"/>
    /// under <c>Dn.Comparer</c>, or null (no match / null DN).</summary>
    private static PlanNodeRowModel? Resolve(IEnumerable<PlanNodeRowModel> rows, string? dn) =>
        dn is null ? null : rows.FirstOrDefault(r => Dn.Comparer.Equals(r.Dn, dn));

    /// <summary>The display name of an authored DN (the edge-row resolver), falling back to the
    /// DN itself if somehow absent — never a provider call.</summary>
    private string NameOf(string dn) => Plan.TryGetNode(dn, out var node) ? node.Name : dn;

    /// <summary>
    /// Selection sync (mirrors <see cref="WorkspaceViewModel.OnSelectedDnChanged"/>): re-resolves
    /// <see cref="SelectedNodeRow"/> to the <see cref="Nodes"/> row whose Dn matches the new
    /// selection, and flips <see cref="ViolationRowModel.IsActive"/> on every <see cref="Violations"/>
    /// row whose <c>PrimaryDn</c> matches (by ANCHOR, never by member-endpoint). The
    /// <see cref="_syncingSelection"/> guard keeps the SelectedDn↔SelectedNodeRow round-trip
    /// one-directional per edit.
    /// </summary>
    partial void OnSelectedDnChanged(string? value)
    {
        if (!_syncingSelection)
        {
            _syncingSelection = true;
            try
            {
                SelectedNodeRow = Resolve(Nodes, value);
            }
            finally
            {
                _syncingSelection = false;
            }
        }

        HighlightActiveRows();
    }

    /// <summary>The Objects-list selection drives the rest of the selection state (AP 4.2.3):
    /// <see cref="SelectedDn"/> = the row's Dn and <see cref="RenameText"/> = the row's Name (so
    /// the rename box pre-fills). Guarded by <see cref="_syncingSelection"/> so the round-trip
    /// from <see cref="OnSelectedDnChanged"/> does not re-enter.</summary>
    partial void OnSelectedNodeRowChanged(PlanNodeRowModel? value)
    {
        RenameText = value?.Name ?? "";
        if (_syncingSelection)
        {
            return;
        }

        _syncingSelection = true;
        try
        {
            SelectedDn = value?.Dn;
        }
        finally
        {
            _syncingSelection = false;
        }
    }

    /// <summary>Re-applies the sidebar highlight over the current rows (the
    /// <see cref="OnReportChanged"/> re-projection and the selection setters both call it), so a
    /// selection that persists across a re-Evaluate keeps lighting its anchor (mirrors
    /// <see cref="WorkspaceViewModel"/>'s <c>HighlightActiveRows</c>).</summary>
    private void HighlightActiveRows()
    {
        foreach (var row in Violations)
        {
            row.IsActive = SelectedDn is not null && Dn.Comparer.Equals(row.PrimaryDn, SelectedDn);
        }
    }

    /// <summary>
    /// Jump-to-graph for a plan finding row (mirrors <see cref="GapViewModel.JumpToCommand"/> and
    /// the workspace jump): sets <see cref="SelectedDn"/> to the row's <c>PrimaryDn</c> (which drives
    /// the <see cref="HighlightActiveRows"/> selection highlight via <see cref="OnSelectedDnChanged"/>)
    /// and frames the anchor on the plan's own graph via <see cref="IGraphRenderer.FocusAsync"/> with
    /// exactly <c>[row.PrimaryDn]</c>. Null-safe (a null row, or a disposed VM, is a no-op).
    /// </summary>
    [RelayCommand]
    private async Task JumpToFinding(ViolationRowModel? row)
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

    /// <summary>
    /// The live-validate inner loop (mirrors <see cref="WorkspaceViewModel.ApplyRulesetAsync"/>):
    /// project the plan → <c>GraphBuilder.Build</c> → <c>RuleEngine.Evaluate</c> against the
    /// live <see cref="_ruleset"/> → roll-up below-map → render. NULL-RENDERER-SAFE: it
    /// computes <see cref="Snapshot"/>/<see cref="Graph"/>/<see cref="Report"/> and simply
    /// skips the renderer push when there is none, so it runs headless (the same contract
    /// that lets the workspace's <c>ApplyRulesetAsync</c> run renderer-less). Uses
    /// <c>ShowGraphAsync</c> (replace-all): a plan edit is a wholesale topology change.
    /// </summary>
    public async Task RevalidateAsync(CancellationToken cancellationToken = default)
    {
        var snapshot = PlanProjection.ToSnapshot(Plan);
        Snapshot = snapshot;
        Graph = GraphBuilder.Build(snapshot, BaseOuDn);

        // Snapshot before Report: OnReportChanged resolves subject names against the
        // CURRENT snapshot (same ordering invariant as the workspace load).
        Report = RuleEngine.Evaluate(snapshot, _ruleset);
        var below = ComputeBelow(snapshot, Report);

        if (GraphRenderer is { } renderer)
        {
            await renderer.ShowGraphAsync(Graph, Report, below, cancellationToken);
        }
    }

    /// <summary>
    /// Re-threads a settings Apply/Save into the live plan (AP 3.3 / ADR-011 §3 parity): swaps
    /// the ruleset, then re-Evaluates the ALREADY-projected plan and pushes the fresh report
    /// through <see cref="IGraphRenderer.UpdateGraphAsync"/> (replace-in-place — a ruleset-only
    /// change leaves the topology and the <see cref="Graph"/> reference untouched). Null-renderer
    /// safe: with no snapshot yet, or no renderer, it only re-Evaluates the field and returns.
    /// The seam <c>ShellViewModel.OnRulesetApplied</c> calls for a live plan step.
    /// </summary>
    public async Task ApplyRulesetAsync(Ruleset newRuleset, CancellationToken cancellationToken = default)
    {
        _ruleset = newRuleset;
        if (Snapshot is not { } snapshot)
        {
            return;
        }

        Report = RuleEngine.Evaluate(snapshot, _ruleset);
        var below = ComputeBelow(snapshot, Report);
        if (GraphRenderer is { } renderer)
        {
            await renderer.UpdateGraphAsync(Graph!, Report, below, cancellationToken);
        }
    }

    /// <summary>Re-projects <see cref="Report"/>'s findings into <see cref="Violations"/>
    /// (ADR-010 §5): canonical report order, subject name resolved snapshot-only (absent DN
    /// falls back to the DN itself). Mirrors <see cref="WorkspaceViewModel.OnReportChanged"/>.</summary>
    partial void OnReportChanged(RuleReport value)
    {
        // Rule id -> human class label (canonical EnumerateRules order) — the same resolution the
        // workspace uses, so the per-rule-class "Why?" copy on the shared sidebar template matches the
        // Audit screen by construction (#198). The remediation snippet AuditFindingDetail.From also
        // builds is discarded — the sidebar surfaces rationale only.
        var ruleClassById = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var rule in _ruleset.EnumerateRules())
        {
            ruleClassById[rule.Id] = rule.DisplayName;
        }

        Violations.Clear();
        foreach (var violation in value.Violations)
        {
            var subject = Snapshot is not null && Snapshot.TryGetObject(violation.PrimaryDn, out var obj)
                ? obj!.Name
                : violation.PrimaryDn;
            var ruleClassLabel = ruleClassById.TryGetValue(violation.RuleId, out var label)
                ? label
                : violation.RuleId;
            var detail = AuditFindingDetail.From(violation, subject, ruleClassLabel);
            Violations.Add(new ViolationRowModel(
                violation.Severity, violation.Message, subject, violation.PrimaryDn,
                detail.WhyItMatters, detail.HowToFix));
        }

        // Re-apply the selection-sync highlight over the fresh rows (a persisting selection
        // keeps lighting its anchor across a re-Evaluate) — mirrors the workspace.
        HighlightActiveRows();
    }

    /// <summary>The AP 3.4 roll-up below-map — a COPY of
    /// <see cref="WorkspaceViewModel"/>'s <c>private static</c> helper (it is not reusable
    /// across the type). For every LOADED fetchable-kind node, the count + max severity of
    /// the distinct findings among its loaded transitive descendants; the one sanctioned
    /// <see cref="MembershipTraversal.Walk"/> (data-model.md). In a plan every group is
    /// loaded (the projection <c>SetMembers</c> them all), so the map covers every group.</summary>
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

    /// <summary>The fetchable kinds (ADR-005 D3): groups plus External — copied alongside
    /// <see cref="ComputeBelow"/> for the same reason (the workspace's is private).</summary>
    private static bool IsFetchable(AdObjectKind kind) => kind
        is AdObjectKind.GlobalGroup
        or AdObjectKind.DomainLocalGroup
        or AdObjectKind.UniversalGroup
        or AdObjectKind.External;

    /// <summary>Cancels any in-flight revalidate render, then disposes the renderer (#122 — tears
    /// down its WebView, retiring the ADR-024 never-disposed leak); idempotent. The CTS is cancelled
    /// FIRST so the renderer's own-cancellation guards see an already-cancelled command token. The
    /// shell disposes this step at teardown — and now also on the abandon paths (Back to Workspace
    /// abandons the Plan), so the bound live WebViews stay ≤ Workspace + current Plan (#122 Slice 5).</summary>
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
