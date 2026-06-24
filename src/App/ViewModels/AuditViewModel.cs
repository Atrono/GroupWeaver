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
    }

    /// <summary>The categories pane (WP5c): one row per rule class that produced findings, in the
    /// canonical <see cref="Ruleset.EnumerateRules"/> order (nesting → naming rules in file order →
    /// circular → empty-group), so the list never reshuffles by dictionary order. Read-only for now;
    /// click-to-filter is WP5d/e. Rebuilt in place on every <see cref="ApplyRuleset"/>.</summary>
    public ObservableCollection<AuditCategoryRow> Categories { get; } = [];

    /// <summary>True when at least one rule class produced a finding — gates the categories pane
    /// against an empty list (an all-clear scope shows no category rows).</summary>
    public bool HasCategories => Categories.Count > 0;

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
    }

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
