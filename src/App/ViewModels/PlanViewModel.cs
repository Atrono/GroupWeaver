using System.Collections.ObjectModel;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
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

    /// <summary>The AP 3.4 rule report the violations list binds; re-Evaluated on every
    /// <see cref="RevalidateAsync"/> and <see cref="ApplyRulesetAsync"/>.
    /// <see cref="OnReportChanged"/> re-projects <see cref="Violations"/> and re-evaluates
    /// <see cref="HasViolations"/>/<see cref="HasUncheckedAreas"/>. Starts empty.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasViolations))]
    [NotifyPropertyChangedFor(nameof(HasUncheckedAreas))]
    private RuleReport _report = RuleReport.Empty;

    /// <summary>DN of the last tapped plan node — the selection seam (carried verbatim;
    /// DN strings are never canonicalized, data-model rule).</summary>
    [ObservableProperty]
    private string? _selectedDn;

    public PlanViewModel(
        string baseOuDn,
        EffectiveRuleset ruleset,
        Func<IGraphRenderer>? graphRendererFactory = null,
        bool webView2Missing = false,
        Action? onBackToExplore = null)
    {
        ArgumentNullException.ThrowIfNull(ruleset);

        Plan = new PlanModel(baseOuDn);
        _ruleset = ruleset.Ruleset;
        _onBackToExplore = onBackToExplore;
        WebView2Missing = webView2Missing;

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

    /// <summary>Back to the Ist workspace (ADR-014): invokes the shell-supplied callback,
    /// which restores the SAME workspace instance — never a reload, never a dispose.</summary>
    [RelayCommand]
    private void Back() => _onBackToExplore?.Invoke();

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
        Violations.Clear();
        foreach (var violation in value.Violations)
        {
            var subject = Snapshot is not null && Snapshot.TryGetObject(violation.PrimaryDn, out var obj)
                ? obj!.Name
                : violation.PrimaryDn;
            Violations.Add(new ViolationRowModel(
                violation.Severity, violation.Message, subject, violation.PrimaryDn));
        }
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

    /// <summary>Cancels any in-flight revalidate render; idempotent. The shell disposes
    /// this step at teardown (never on the Ist↔Plan switch — the step you leave survives).</summary>
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
