using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GroupWeaver.App.Graph;
using GroupWeaver.App.Rules;
using GroupWeaver.App.Settings;
using GroupWeaver.App.Startup;
using GroupWeaver.App.Views;
using GroupWeaver.Core.Model;
using GroupWeaver.Core.Providers;
using GroupWeaver.Core.Rules;

namespace GroupWeaver.App.ViewModels;

/// <summary>
/// Owns the shell's step state machine (ADR-003 D5): Connect → PickRoot → Workspace.
/// <see cref="CurrentStep"/> holds the active step's content object; <c>MainWindow</c>
/// maps its runtime type to a view via DataTemplates. Steps hand control back through
/// callbacks: Connect succeeds into the root picker, the picker either confirms into a
/// workspace or backs out to a fresh Connect step.
/// </summary>
public sealed partial class ShellViewModel : ObservableObject, IDisposable
{
    private readonly Func<bool, IDirectoryProvider> _providerFactory;
    private readonly Func<IGraphRenderer>? _graphRendererFactory;
    private readonly RulesetLocator _locator;

    /// <summary>The ADR-022 D4 rail-state store, threaded down the Shell→RootPicker→Workspace
    /// path exactly as <see cref="_locator"/> is; each workspace seeds + persists its rail state
    /// through it. Defaulted (pre-ADR-022 call sites/tests get the real <c>%APPDATA%</c> layout).</summary>
    private readonly UiStateStore _uiStateStore;

    /// <summary>Every disposable step the shell has created and must dispose at teardown
    /// (AP 4.2.2 dispose discipline): the Ist↔Plan switch keeps BOTH the workspace and the
    /// plan step alive — it never disposes the step it leaves, so teardown is the only place
    /// they are torn down. Tracked here because <see cref="CurrentStep"/> holds only the
    /// active one. De-duplicated by reference (the same step re-entered is tracked once).</summary>
    private readonly List<IDisposable> _disposableSteps = [];

    /// <summary>The ruleset the app is currently running on (ADR-010 §3); settable
    /// (AP 3.3 / ADR-011 §3) so a settings Apply/Save re-caches it as the new effective
    /// ruleset — the next workspace inherits it through the existing Shell→RootPicker→
    /// Workspace thread, and a live workspace is re-threaded via
    /// <see cref="WorkspaceViewModel.ApplyRulesetAsync"/>.</summary>
    private EffectiveRuleset? _ruleset;

    /// <summary>Active step content; the window's DataTemplates switch on its type.</summary>
    [ObservableProperty]
    private object _currentStep;

    /// <summary>ADR-022 addendum: true only while the workspace step is current. The top strip's
    /// "Focus" button binds <c>IsVisible</c> to this so it appears on the workspace step alone —
    /// the Connect/RootPicker strips stay byte-identical. Recomputed (change-notified) on every
    /// <see cref="CurrentStep"/> change via <see cref="OnCurrentStepChanged"/>.</summary>
    public bool IsWorkspaceStep => CurrentStep is WorkspaceViewModel;

    /// <summary>Raises <see cref="IsWorkspaceStep"/>'s change notification whenever the active step
    /// changes (the generated setter already raises <c>CurrentStep</c>) — keeps the Focus button's
    /// visibility binding in sync as steps switch (ADR-022 addendum).</summary>
    partial void OnCurrentStepChanged(object value) => OnPropertyChanged(nameof(IsWorkspaceStep));

    /// <summary>ADR-022 D2: focus (presentation) mode. The top command strip binds
    /// <c>IsVisible="{Binding !IsFocusMode}"</c> so focus mode hides it (the WebView2-missing
    /// banner stays visible). Shell-level because the strip is shell chrome; propagated to the
    /// active workspace via the <see cref="CurrentStep"/>-dispatch seam in
    /// <see cref="ToggleFocusMode"/>/<see cref="ExitFocusMode"/>.</summary>
    [ObservableProperty]
    private bool _isFocusMode;

    public ShellViewModel(
        Func<bool, IDirectoryProvider> providerFactory,
        StartupOptions startupOptions,
        WebView2RuntimeStatus? webView2Runtime = null,
        Func<IGraphRenderer>? graphRendererFactory = null,
        EffectiveRuleset? ruleset = null,
        RulesetLocator? locator = null,
        UiStateStore? uiStateStore = null)
    {
        _providerFactory = providerFactory;
        _graphRendererFactory = graphRendererFactory;
        _ruleset = ruleset;
        // ADR-022 D4: defaulted like the locator — the composition root passes the one store,
        // pre-ADR-022 tests omit it and get the real %APPDATA% layout.
        _uiStateStore = uiStateStore ?? new UiStateStore();
        // Defaulted (AP 3.3 / ADR-011 §1): App.axaml.cs passes the one composition-root
        // locator; pre-S8 tests omit it and get the real %APPDATA% layout. Settings
        // Save persists to its UserRulesetPath; the headless tests inject a temp-dir seam.
        _locator = locator ?? new RulesetLocator();

        // Default = real probe, so harnesses constructing the shell directly behave like
        // the app; S8's headless tests pass an explicit status to force the missing state.
        var runtime = webView2Runtime ?? WebView2Runtime.Probe();
        WebView2Missing = !runtime.IsInstalled;
        WebView2Version = runtime.Version;

        var connect = new ConnectionViewModel(providerFactory, OnConnected);
        _currentStep = connect;

        // --demo auto-connects without user input; storing the Task keeps the startup
        // work observable (headless tests await Initialization, no fire-and-forget).
        Initialization = startupOptions.Demo
            ? connect.ConnectDemoCommand.ExecuteAsync(null)
            : Task.CompletedTask;
    }

    /// <summary>
    /// The <c>--demo</c> startup auto-connect, or a completed task when none runs.
    /// Never faults on an unreachable directory — the Connect step shows that inline.
    /// </summary>
    public Task Initialization { get; }

    /// <summary>
    /// Provider behind the active connection; set when the Connect step succeeds,
    /// dropped when the picker backs out to the Connect step.
    /// </summary>
    public IDirectoryProvider? Provider { get; private set; }

    /// <summary>
    /// True when the startup probe found no WebView2 Runtime (ADR-003 D3). Drives the
    /// persistent shell banner; a missing runtime never blocks — only the AP 2.2 graph
    /// view needs it. Fixed at construction (installing mid-session needs a restart).
    /// </summary>
    public bool WebView2Missing { get; }

    /// <summary>Detected runtime version (<c>pv</c>); <c>null</c> when missing.</summary>
    public string? WebView2Version { get; }

    /// <summary>Banner hyperlink: open the runtime's download page in the browser.</summary>
    [RelayCommand]
    private void OpenWebView2DownloadPage() => WebView2Runtime.OpenDownloadPage();

    /// <summary>
    /// The top command strip's "⚙ Settings" affordance (AP 3.3 / ADR-011 §1): builds the
    /// settings VM (the <see cref="BuildSettingsViewModel"/> seam already subscribes the
    /// shell's re-thread to <see cref="SettingsViewModel.RulesetApplied"/>) and shows it
    /// as a modal <see cref="SettingsWindow"/> over the main window. Reachable on every
    /// step. <c>ShowDialog</c> is the production-only path (headless-hostile — ADR-011
    /// open-risk #3): tests drive the SAME re-thread through <see cref="BuildSettingsViewModel"/>
    /// + <c>SettingsViewModel.Save</c>/<c>Apply</c>, never this command.
    /// </summary>
    [RelayCommand]
    private async Task OpenSettingsAsync()
    {
        var vm = BuildSettingsViewModel();
        var window = new SettingsWindow { DataContext = vm };
        if (GetMainWindow() is { } owner)
        {
            await window.ShowDialog(owner);
        }
        else
        {
            window.Show();
        }
    }

    /// <summary>
    /// The shell's settings-VM seam (AP 3.3 / ADR-011 §1/§3): seeds a
    /// <see cref="SettingsViewModel"/> from the cached <see cref="EffectiveRuleset"/>
    /// (rejected-file errors carried, finally surfaced) and the injected
    /// <see cref="RulesetLocator"/>, then subscribes the shell's re-thread to its
    /// <see cref="SettingsViewModel.RulesetApplied"/> event. On Apply/Save the shell
    /// re-caches <c>_ruleset</c> as a clean <c>new EffectiveRuleset(saved, true, [])</c>
    /// and, if a live workspace is the current step, re-threads it via
    /// <see cref="WorkspaceViewModel.ApplyRulesetAsync"/> — no rebuild, viewport kept.
    /// This is the seam <see cref="OpenSettingsAsync"/> uses internally and the seam the
    /// headless integration tests drive directly (the <c>ShowDialog</c> path is <c>[I]</c>).
    /// </summary>
    public SettingsViewModel BuildSettingsViewModel()
    {
        var effective = _ruleset ?? new EffectiveRuleset(RulesetLoader.LoadDefault(), FromUserFile: false, []);
        var vm = SettingsViewModel.Open(effective, _locator);
        vm.RulesetApplied += OnRulesetApplied;
        return vm;
    }

    private async void OnRulesetApplied(Ruleset ruleset)
    {
        _ruleset = new EffectiveRuleset(ruleset, FromUserFile: true, []);
        // Re-thread the LIVE step (AP 3.3 / ADR-011 §3, extended for Plan Mode in AP 4.2.2):
        // both the workspace and a plan step re-Evaluate against the flipped ruleset.
        if (CurrentStep is WorkspaceViewModel workspace)
        {
            await workspace.ApplyRulesetAsync(ruleset);
        }
        else if (CurrentStep is PlanViewModel plan)
        {
            await plan.ApplyRulesetAsync(ruleset);
        }
    }

    /// <summary>ADR-022 D2: flips focus mode and propagates the new state to the active workspace
    /// step via the existing <see cref="CurrentStep"/>-dispatch seam (exactly as
    /// <see cref="OnRulesetApplied"/> re-threads the live step). Non-workspace steps simply lose
    /// the top strip (harmless). The workspace "Focus" button reaches this through the callback the
    /// shell installs at <see cref="OnRootChosen"/>; the view also binds <c>Esc</c>/<c>F11</c>.</summary>
    [RelayCommand]
    private void ToggleFocusMode()
    {
        IsFocusMode = !IsFocusMode;
        if (CurrentStep is WorkspaceViewModel workspace)
        {
            workspace.SetRailCollapsed(IsFocusMode);
        }
    }

    /// <summary>ADR-022 D2: leaves focus mode unconditionally (the <c>Esc</c> exit affordance,
    /// alongside full-screen exit in the view). A no-op when focus mode is already off; otherwise
    /// re-expands the active workspace rail through the same <see cref="CurrentStep"/>-dispatch seam.</summary>
    [RelayCommand]
    private void ExitFocusMode()
    {
        if (!IsFocusMode)
        {
            return;
        }

        IsFocusMode = false;
        if (CurrentStep is WorkspaceViewModel workspace)
        {
            workspace.SetRailCollapsed(false);
        }
    }

    /// <summary>The desktop main window, the modal owner for <see cref="OpenSettingsAsync"/>;
    /// <c>null</c> off the classic-desktop lifetime (headless theory) — the seam then
    /// falls back to a non-modal show, but tests never reach the show at all.</summary>
    private static Avalonia.Controls.Window? GetMainWindow() =>
        (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow;

    private void OnConnected(IDirectoryProvider provider, DirectoryConnection connection)
    {
        Provider = provider;
        CurrentStep = new RootPickerViewModel(
            provider, connection, OnBackToConnect, OnRootChosen, WebView2Missing,
            _graphRendererFactory, _ruleset, _uiStateStore);
    }

    /// <summary>The picker's Back: drop the provider, start over on a fresh Connect step.</summary>
    private void OnBackToConnect()
    {
        Provider = null;
        CurrentStep = new ConnectionViewModel(_providerFactory, OnConnected);
    }

    private void OnRootChosen(WorkspaceViewModel workspace)
    {
        // Install the Plan-Mode switch callback (AP 4.2.2 / ADR-014) and track the workspace
        // as a disposable step so teardown disposes it even after a switch into Plan Mode.
        workspace.UseDesignPlanCallback(() => OnDesignPlan(workspace));
        // Arm the workspace "Focus" button (ADR-022 D2) — dead until armed, exactly like the
        // Design-plan callback, so a renderer-less/headless workspace never half-toggles.
        workspace.UseFocusToggleCallback(() => ToggleFocusMode());
        Track(workspace);
        CurrentStep = workspace;
    }

    /// <summary>
    /// The Ist→Plan switch (AP 4.2.2 / ADR-014): makes <see cref="CurrentStep"/> a fresh
    /// <see cref="PlanViewModel"/> seeded EMPTY at <paramref name="current"/>'s root DN (the
    /// empty-start default), carrying the live ruleset and the same renderer factory (the plan
    /// builds its OWN renderer instance). Back returns the SAME <paramref name="current"/>
    /// instance — never disposed on the switch, so the Ist load, viewport and selection survive.
    /// Both steps are tracked and disposed only at shell teardown.
    /// </summary>
    public void OnDesignPlan(WorkspaceViewModel current)
    {
        var effective = _ruleset ?? new EffectiveRuleset(RulesetLoader.LoadDefault(), FromUserFile: false, []);
        var plan = new PlanViewModel(
            current.RootDn,
            effective,
            _graphRendererFactory,
            WebView2Missing,
            onBackToExplore: () => CurrentStep = current);
        // Arm the plan's "Gap analysis" button (ADR-015 / #66) — exactly like the workspace's
        // Design-plan callback is installed in OnRootChosen, so the PlanView button is live once
        // Plan mode is reached. The BaseOuDn == RootDn + snapshot-null gate lives in OnGapAnalysis.
        plan.UseGapAnalysisCallback(() => OnGapAnalysis(plan, current));
        Track(plan);
        CurrentStep = plan;
    }

    /// <summary>
    /// The Plan→Gap switch (ADR-015 / #66): makes <see cref="CurrentStep"/> a fresh
    /// <see cref="GapViewModel"/> that diffs the borrowed live Ist (<paramref name="workspace"/>'s
    /// <see cref="WorkspaceViewModel.Snapshot"/>) against <paramref name="plan"/>'s
    /// <see cref="PlanViewModel.Plan"/>, at the workspace root, carrying the same renderer factory
    /// (the gap builds its OWN renderer instance). Back returns the SAME <paramref name="plan"/>
    /// instance — never disposed on the switch, so the authored model survives. The gap step is
    /// tracked and disposed only at shell teardown (Workspace + Plan + Gap each once).
    ///
    /// <para>GATE (ADR-015 D7): a no-op unless the workspace has a loaded Ist whose root equals the
    /// plan's base OU. <c>BaseOuDn == RootDn</c> holds by construction (OnDesignPlan seeds the
    /// plan's base OU = the workspace root); the snapshot-null arm is the honest no-op for the
    /// pre-load edge case (a gap is meaningful only against a loaded Ist).</para>
    /// </summary>
    public void OnGapAnalysis(PlanViewModel plan, WorkspaceViewModel workspace)
    {
        if (workspace.Snapshot is not { } ist
            || !Dn.Comparer.Equals(plan.Plan.BaseOuDn, workspace.RootDn))
        {
            return;
        }

        var gap = new GapViewModel(
            ist,
            plan.Plan,
            workspace.RootDn,
            _graphRendererFactory,
            WebView2Missing,
            onBack: () => CurrentStep = plan);
        Track(gap);
        CurrentStep = gap;

        // Fire-and-forget compute + push (it awaits renderer-ready internally, so the GapView mount
        // race is handled) — mirrors the workspace/plan observable-compute hand-off.
        _ = gap.RefreshAsync();
    }

    /// <summary>Records a created step for teardown disposal (AP 4.2.2 dispose discipline),
    /// de-duplicated by reference: re-entering the same workspace via Back tracks it once.</summary>
    private void Track(IDisposable step)
    {
        if (!_disposableSteps.Contains(step))
        {
            _disposableSteps.Add(step);
        }
    }

    /// <summary>
    /// Disposes EVERY disposable step the shell created (AP 4.2.2 dispose discipline): both the
    /// workspace and any plan step — the Ist↔Plan switch never disposes the step it leaves, so
    /// teardown is the sole tear-down point. Each step's <c>Dispose</c> is idempotent (cancels
    /// its in-flight load/render). Idempotent overall.
    /// </summary>
    public void Dispose()
    {
        foreach (var step in _disposableSteps)
        {
            step.Dispose();
        }

        _disposableSteps.Clear();
    }
}
