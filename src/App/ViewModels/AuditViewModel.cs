using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using GroupWeaver.Core.Model;
using GroupWeaver.Core.Rules;

namespace GroupWeaver.App.ViewModels;

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
    }

    /// <summary>The base OU the audit was run over (the workspace root); part of the title.</summary>
    public string RootDn { get; }

    /// <summary>The audit step header line.</summary>
    public string Title => $"Audit · {RootDn}";

    /// <summary>The 0-100 health score (<see cref="AuditSummary.Score"/>).</summary>
    public int Score => Summary.Score;

    /// <summary>The qualitative band for <see cref="Score"/> (<see cref="AuditSummary.Band"/>).</summary>
    public string Band => Summary.Band;

    /// <summary>Error-severity finding count (<see cref="AuditSummary.Critical"/>).</summary>
    public int Critical => Summary.Critical;

    /// <summary>Warning-severity finding count (<see cref="AuditSummary.Warnings"/>).</summary>
    public int Warnings => Summary.Warnings;

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
        OnPropertyChanged(nameof(Critical));
        OnPropertyChanged(nameof(Warnings));
        OnPropertyChanged(nameof(Passing));
        OnPropertyChanged(nameof(RuleClasses));
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
