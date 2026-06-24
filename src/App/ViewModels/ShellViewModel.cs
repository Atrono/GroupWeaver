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

    /// <summary>The one window-scoped graph-surface coordinator (#122 / ADR-025): built in
    /// <c>MainWindow.OnOpened</c> over the hidden parking <c>Panel</c> and pushed in via
    /// <see cref="UseGraphSurfaceCoordinator"/> (mirroring the <see cref="WorkspaceViewModel"/>
    /// export-dialog seam). The shell uses it to PARK the Back-target step's live graph surface
    /// BEFORE a forward swap reassigns <see cref="CurrentStep"/>, so the WebView2 page + viewport
    /// survive the round-trip. <c>null</c> headless / off a window — the shell then leaves the
    /// step swap exactly as before (the ADR-024 re-render fallback).</summary>
    private IGraphSurfaceCoordinator? _surfaceCoordinator;

    /// <summary>The Plan step currently authored over the active workspace (#122 reclaim): set in
    /// <see cref="OnDesignPlan"/>, cleared when the Plan is abandoned (Back to Workspace) or
    /// superseded (a fresh Design-plan). Kept so <see cref="OnDesignPlan"/> can dispose a stale
    /// predecessor — bounding the live WebViews to ≤ Workspace + current Plan (+ a transient Gap).
    /// <c>null</c> when no plan is live.</summary>
    private PlanViewModel? _currentPlan;

    /// <summary>The workspace the CURRENT audit step backs into (WP5e / ADR-028): set in
    /// <see cref="OnAudit"/>, used by <see cref="OnRulesetApplied"/> to re-thread that PARKED
    /// workspace too when a triage Save fires while the audit is the current step — so the graph
    /// halos + violations rail update even though Audit (a table view) is showing. Cleared when the
    /// audit is abandoned (Back) or superseded. <c>null</c> when no audit is live.</summary>
    private WorkspaceViewModel? _auditBackWorkspace;

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

    /// <summary>ADR-026 D6 (WP2): true while the active connection is the embedded demo directory
    /// (the Connect-screen "Demo mode" button OR the CLI <c>--demo</c>, both routed through
    /// <see cref="ConnectionViewModel"/>'s demo path). The top strip's "DEMO" badge binds its
    /// <c>IsVisible</c> to this. Set in <see cref="OnConnected"/> from the threaded demo flag and
    /// reset in <see cref="OnBackToConnect"/> (Back drops the connection, so the badge clears).</summary>
    [ObservableProperty]
    private bool _isDemoMode;

    /// <summary>ADR-026 D4: the app-chrome theme is Light when true (Dark otherwise). Seeded in the
    /// ctor from the persisted <see cref="UiState.Theme"/> and flipped by <see cref="ToggleTheme"/>;
    /// the top strip's theme button binds its glyph to <see cref="ThemeGlyph"/>. Shell-level because
    /// the toggle is shell chrome reachable on every step (unlike Focus, which is workspace-only).</summary>
    [ObservableProperty]
    private bool _isLightTheme;

    /// <summary>ADR-026 D4: the theme-toggle button's glyph — a sun in light mode (tap to go dark),
    /// a moon in dark mode (tap to go light); change-notified off <see cref="IsLightTheme"/>.</summary>
    public string ThemeGlyph => IsLightTheme ? "☀" : "☾";

    /// <summary>Keeps <see cref="ThemeGlyph"/> in sync whenever <see cref="IsLightTheme"/> flips.</summary>
    partial void OnIsLightThemeChanged(bool value) => OnPropertyChanged(nameof(ThemeGlyph));

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
        // ADR-026 D4: seed the app-chrome theme from the persisted state and apply the resolved
        // ThemeVariant on startup (a no-op when the app is not yet running, e.g. some headless
        // theories — the bound IsLightTheme still reflects the persisted choice).
        _isLightTheme = string.Equals(_uiStateStore.Load().Theme, "Light", StringComparison.OrdinalIgnoreCase);
        ApplyThemeVariant();
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

    /// <summary>Installs the window-scoped graph-surface coordinator (#122 / ADR-025), pushed in by
    /// <c>MainWindow.OnOpened</c> once it owns the parking <c>Panel</c> — mirroring how the export
    /// seam is wired through the same window. With it the shell parks the Back-target surface before
    /// a forward swap (<see cref="OnDesignPlan"/>/<see cref="OnGapAnalysis"/>); without it (headless)
    /// the step swap stays exactly as before. Idempotent — the last writer wins.</summary>
    public void UseGraphSurfaceCoordinator(IGraphSurfaceCoordinator coordinator) =>
        _surfaceCoordinator = coordinator;

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
        else if (CurrentStep is AuditViewModel audit)
        {
            // WP5e / ADR-028: recompute the audit step's live + would-be report + summary + table
            // against the flipped ruleset (sync, pure) AND re-thread the PARKED workspace this audit
            // backs into — so its graph halos + violations rail update even though Audit (a table
            // view) is the current step. The workspace surface is parked, so this is a pure re-Evaluate
            // (no rebuild): ApplyRulesetAsync no-ops the render when no renderer is live and re-pushes
            // on the next mount.
            audit.ApplyRuleset(ruleset);
            if (_auditBackWorkspace is { } parkedWorkspace)
            {
                await parkedWorkspace.ApplyRulesetAsync(ruleset);
            }
        }
    }

    /// <summary>
    /// The audit-triage write path (WP5e / ADR-028): turns a batch of <see cref="TriageRequest"/>s
    /// into global-ignore <see cref="MatchEntry"/> mutations on the CURRENT ruleset and routes the
    /// result through the SINGLE existing <c>SettingsViewModel</c> gate — never a parallel
    /// serialize/validate. Acknowledge/Suppress APPEND a tagged entry (skipping a duplicate already
    /// present); Untriage REMOVES the matching tagged entry (by escaped DN + tag). The gate
    /// (<c>BuildRuleset → Serialize → RulesetLoader.Load → Save</c>) persists atomically to
    /// <c>%APPDATA%\GroupWeaver\ruleset.jsonc</c> and fires <see cref="SettingsViewModel.RulesetApplied"/>
    /// → <see cref="OnRulesetApplied"/> (re-thread audit + the parked workspace). <b>The only write
    /// is the ruleset file + in-memory state — never Active Directory.</b>
    /// </summary>
    public void ApplyTriage(IReadOnlyList<TriageRequest> requests, WorkspaceViewModel workspace)
    {
        if (requests.Count == 0)
        {
            return;
        }

        // Seed the editable mirror from the CURRENT effective ruleset; the seam also subscribes the
        // shell's re-thread to RulesetApplied (so the Save below re-threads exactly as a settings Save).
        var settings = BuildSettingsViewModel();
        foreach (var request in requests)
        {
            switch (request.Kind)
            {
                case TriageKind.Acknowledge:
                case TriageKind.Suppress:
                    // Append the tagged dn-mode ignore entry — unless an equal one is already present
                    // (re-triaging an already-covered finding is a no-op, never a duplicate entry).
                    if (!settings.Ignore.Any(e =>
                            TriageEntry.MatchesFinding(e.Build(), request.Dn, request.Kind)))
                    {
                        var entry = TriageEntry.Build(request);
                        settings.Ignore.Add(MatchEntryEditor.LoadFrom(entry, endpointEditable: false));
                    }

                    break;

                case TriageKind.Untriage:
                    // Remove BOTH tags for this DN — a reversal reopens the finding regardless of how
                    // it was triaged; the row only knows it is triaged, not by which tag.
                    var stale = settings.Ignore
                        .Where(e =>
                            TriageEntry.MatchesFinding(e.Build(), request.Dn, TriageKind.Acknowledge)
                            || TriageEntry.MatchesFinding(e.Build(), request.Dn, TriageKind.Suppress))
                        .ToList();
                    foreach (var editor in stale)
                    {
                        settings.Ignore.Remove(editor);
                    }

                    break;
            }
        }

        // The single gate: persist + fire RulesetApplied (→ re-thread). A gate refusal writes nothing
        // and surfaces errors on the throwaway settings VM (the audit batch is well-formed dn entries,
        // so a refusal would mean a pre-existing invalid mirror — left honest, never force-written).
        settings.Save();
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

    /// <summary>ADR-026 D4: flips the app-chrome theme live and persists it. Toggles
    /// <see cref="IsLightTheme"/>, applies the resolved <c>ThemeVariant</c> to the running app
    /// (so every native screen re-themes via the DynamicResource brushes), and writes
    /// <see cref="UiState.Theme"/> through the shared store (read-modify-write so the rail state
    /// the workspace owns is preserved). Reachable on every step (the top strip's theme button).</summary>
    [RelayCommand]
    private void ToggleTheme()
    {
        IsLightTheme = !IsLightTheme;
        ApplyThemeVariant();
        // ADR-026 WP1b: re-theme the graph canvas(es) live. The WebView2 bundle does not observe
        // RequestedThemeVariant — it must be pushed the variant explicitly. Re-theme EVERY tracked
        // graph-bearing step (not just CurrentStep): a parked surface (Workspace/Plan behind a Plan/
        // Gap step) survives its page across the round-trip, so it must re-tone now or it would
        // return in the stale theme (a re-mount does NOT re-render). Fire-and-forget, never-throw.
        ApplyCanvasTheme();
        // Preserve the ADR-022 rail fields the workspace owns: read-modify-write the shared store.
        _uiStateStore.Save(_uiStateStore.Load() with { Theme = IsLightTheme ? "Light" : "Dark" });
    }

    /// <summary>ADR-026 WP1b: pushes the current <see cref="IsLightTheme"/> variant to every tracked
    /// graph-bearing step's renderer (Workspace/Plan/Gap). Fire-and-forget (the renderer's
    /// <see cref="IGraphRenderer.SetThemeAsync"/> is itself never-throw and no-ops before its bundle
    /// is ready — a not-yet-rendered renderer converges anyway because each render prepends the
    /// current theme). Covers parked surfaces too, so returning to a Back-target finds it themed.</summary>
    private void ApplyCanvasTheme()
    {
        foreach (var step in _disposableSteps)
        {
            if (RendererOf(step) is { } renderer)
            {
                _ = renderer.SetThemeAsync(IsLightTheme);
            }
        }
    }

    /// <summary>The graph renderer a tracked step owns, or <c>null</c> for a graph-less step (or a
    /// step whose renderer was never built — null factory / missing WebView2).</summary>
    private static IGraphRenderer? RendererOf(IDisposable step) => step switch
    {
        WorkspaceViewModel workspace => workspace.GraphRenderer,
        PlanViewModel plan => plan.GraphRenderer,
        GapViewModel gap => gap.GraphRenderer,
        _ => null,
    };

    /// <summary>Applies <see cref="IsLightTheme"/> to <c>Application.Current.RequestedThemeVariant</c>
    /// (Light ⇒ <see cref="Avalonia.Styling.ThemeVariant.Light"/>, else Dark). A no-op under two
    /// guards: (1) no app is running (some headless theories), and (2) the caller is NOT on the
    /// Avalonia UI thread — <c>RequestedThemeVariant</c> is a UI-thread-affine setter that throws
    /// "Call from invalid thread" off-thread. In production both the ctor (during
    /// <c>OnFrameworkInitializationCompleted</c>) and <c>ToggleTheme</c> run on the UI thread, so the
    /// apply still happens; off-thread (tests, any non-UI caller) it safely skips the global setter
    /// while the bound <see cref="IsLightTheme"/> still reflects the persisted choice. Synchronous
    /// skip — never dispatched (that would reorder startup).</summary>
    private void ApplyThemeVariant()
    {
        if (Application.Current is { } app && Avalonia.Threading.Dispatcher.UIThread.CheckAccess())
        {
            app.RequestedThemeVariant = IsLightTheme
                ? Avalonia.Styling.ThemeVariant.Light
                : Avalonia.Styling.ThemeVariant.Dark;
        }
    }

    /// <summary>The desktop main window, the modal owner for <see cref="OpenSettingsAsync"/>;
    /// <c>null</c> off the classic-desktop lifetime (headless theory) — the seam then
    /// falls back to a non-modal show, but tests never reach the show at all.</summary>
    private static Avalonia.Controls.Window? GetMainWindow() =>
        (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow;

    private void OnConnected(IDirectoryProvider provider, DirectoryConnection connection, bool isDemo)
    {
        Provider = provider;
        IsDemoMode = isDemo;
        CurrentStep = new RootPickerViewModel(
            provider, connection, OnBackToConnect, OnRootChosen, WebView2Missing,
            _graphRendererFactory, _ruleset, _uiStateStore);
    }

    /// <summary>The picker's Back: drop the provider, start over on a fresh Connect step.</summary>
    private void OnBackToConnect()
    {
        Provider = null;
        IsDemoMode = false;
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
        // Arm the workspace "Audit" button (WP5 / #152) — same install idiom as Design-plan; the
        // snapshot-null gate lives in OnAudit (and the command's own CanAudit).
        workspace.UseAuditCallback(() => OnAudit(workspace));
        Track(workspace);
        CurrentStep = workspace;
    }

    /// <summary>
    /// The Ist→Plan switch (AP 4.2.2 / ADR-014): makes <see cref="CurrentStep"/> a fresh
    /// <see cref="PlanViewModel"/> seeded EMPTY at <paramref name="current"/>'s root DN (the
    /// empty-start default), carrying the live ruleset and the same renderer factory (the plan
    /// builds its OWN renderer instance). Back returns the SAME <paramref name="current"/>
    /// instance — never disposed on the switch, so the Ist load, viewport and selection survive;
    /// the abandoned Plan is disposed + untracked on Back (#122 reclaim) so its WebView is freed (a
    /// fresh Plan is built on re-entry). The workspace is tracked and disposed at shell teardown.
    /// </summary>
    public void OnDesignPlan(WorkspaceViewModel current)
    {
        // #122 reclaim: a previously-authored Plan for this workspace (if Back-to-Workspace did not
        // already dispose it) is superseded by this fresh one — dispose + untrack it so its WebView
        // is freed and the live count stays bounded. Idempotent if already gone.
        if (_currentPlan is { } stale)
        {
            DisposeAndUntrack(stale);
            _currentPlan = null;
        }

        var effective = _ruleset ?? new EffectiveRuleset(RulesetLoader.LoadDefault(), FromUserFile: false, []);
        PlanViewModel? plan = null;
        plan = new PlanViewModel(
            current.RootDn,
            effective,
            _graphRendererFactory,
            WebView2Missing,
            // Back to Workspace abandons the Plan (#122): a fresh PlanViewModel is always made on
            // re-entry, so dispose + untrack this one to free its WebView. The workspace surface was
            // parked below before this swap, so its re-mount preserves the viewport.
            onBackToExplore: () =>
            {
                CurrentStep = current;
                if (plan is not null)
                {
                    DisposeAndUntrack(plan);
                }

                _currentPlan = null;
            });
        // Arm the plan's "Gap analysis" button (ADR-015 / #66) — exactly like the workspace's
        // Design-plan callback is installed in OnRootChosen, so the PlanView button is live once
        // Plan mode is reached. The BaseOuDn == RootDn + snapshot-null gate lives in OnGapAnalysis.
        plan.UseGapAnalysisCallback(() => OnGapAnalysis(plan, current));
        Track(plan);
        _currentPlan = plan;

        // #122 (ADR-025): PARK the workspace surface we will Back INTO — SYNCHRONOUSLY, BEFORE the
        // CurrentStep reassignment below detaches the leaving workspace view. This ordering is the
        // single load-bearing invariant: if the swap detached first, the view's detach guard would
        // release (un-root) the live surface and reproduce the negative-control page-death.
        ParkSurface(current.GraphRenderer);
        CurrentStep = plan;
    }

    /// <summary>
    /// The Plan→Gap switch (ADR-015 / #66): makes <see cref="CurrentStep"/> a fresh
    /// <see cref="GapViewModel"/> that diffs the borrowed live Ist (<paramref name="workspace"/>'s
    /// <see cref="WorkspaceViewModel.Snapshot"/>) against <paramref name="plan"/>'s
    /// <see cref="PlanViewModel.Plan"/>, at the workspace root, carrying the same renderer factory
    /// (the gap builds its OWN renderer instance). Back returns the SAME <paramref name="plan"/>
    /// instance — never disposed on the switch, so the authored model survives; the abandoned Gap
    /// is disposed + untracked on Back (#122 reclaim) so its WebView is freed (a fresh Gap is built
    /// on re-entry). Survivors are disposed at shell teardown.
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

        GapViewModel? gap = null;
        gap = new GapViewModel(
            ist,
            plan.Plan,
            workspace.RootDn,
            _graphRendererFactory,
            WebView2Missing,
            // Back to Plan abandons the Gap (#122): a fresh GapViewModel is always made on re-entry,
            // so dispose + untrack this one to free its WebView. The plan surface was parked below
            // before this swap, so its re-mount preserves the viewport.
            onBack: () =>
            {
                CurrentStep = plan;
                if (gap is not null)
                {
                    DisposeAndUntrack(gap);
                }
            });
        Track(gap);

        // #122 (ADR-025): PARK the plan surface we will Back INTO — SYNCHRONOUSLY, BEFORE the
        // CurrentStep reassignment detaches the leaving plan view (same load-bearing ordering as
        // OnDesignPlan). The workspace surface is already parked from the Design-plan hop, so the
        // lot now legitimately holds Workspace + Plan at once.
        ParkSurface(plan.GraphRenderer);
        CurrentStep = gap;

        // Fire-and-forget compute + push (it awaits renderer-ready internally, so the GapView mount
        // race is handled) — mirrors the workspace/plan observable-compute hand-off.
        _ = gap.RefreshAsync();
    }

    /// <summary>
    /// The Workspace→Audit switch (WP5 / #152): makes <see cref="CurrentStep"/> a fresh
    /// <see cref="AuditViewModel"/> that rolls up <paramref name="current"/>'s already-loaded Ist
    /// <see cref="WorkspaceViewModel.Snapshot"/> + <see cref="WorkspaceViewModel.Report"/> against
    /// the effective ruleset, at the workspace root. Back returns the SAME <paramref name="current"/>
    /// instance — never disposed on the switch, so the Ist load, viewport and selection survive; the
    /// abandoned Audit is disposed + untracked on Back. The Audit step owns NO renderer of its own
    /// (table view, v1) — there is nothing to build and no airspace conflict.
    ///
    /// <para>GATE: a no-op unless the workspace has a loaded Ist (the summary is meaningful only
    /// against a loaded snapshot) — mirrors <see cref="OnGapAnalysis"/>'s snapshot-null arm.</para>
    /// </summary>
    public void OnAudit(WorkspaceViewModel current)
    {
        if (current.Snapshot is not { } ist)
        {
            return;
        }

        // The Ruleset behind the live evaluation (kept in sync by OnRulesetApplied); a null cached
        // ruleset resolves to the embedded default — same resolution OnDesignPlan uses.
        var effective = _ruleset ?? new EffectiveRuleset(RulesetLoader.LoadDefault(), FromUserFile: false, []);

        AuditViewModel? audit = null;
        audit = new AuditViewModel(
            ist,
            current.Report,
            effective.Ruleset,
            current.RootDn,
            // Back to Workspace abandons the Audit: dispose + untrack this one (it owns no renderer,
            // so this only flips IsDisposed and drops it from the tracked set). The workspace surface
            // was parked below before this swap, so its re-mount preserves the viewport.
            onBack: () =>
            {
                CurrentStep = current;
                _auditBackWorkspace = null;
                if (audit is not null)
                {
                    DisposeAndUntrack(audit);
                }
            });
        // The workspace this audit backs into — re-threaded alongside the audit step when a triage
        // Save fires (OnRulesetApplied), so the parked graph + rail update too (ADR-028).
        _auditBackWorkspace = current;
        // Arm the audit triage seam (WP5e / ADR-028) — the same install idiom as the Design-plan
        // callback. The shell OWNS the single write path: it routes every triage batch through
        // BuildSettingsViewModel()'s gate (BuildRuleset → Serialize → RulesetLoader.Load → Save),
        // which fires RulesetApplied → OnRulesetApplied (re-thread audit + parked workspace). Writes
        // hit ONLY %APPDATA%\GroupWeaver\ruleset.jsonc + in-memory state — never AD.
        audit.UseTriageCallback(requests => ApplyTriage(requests, current));
        Track(audit);

        // #122 (ADR-025): PARK the workspace surface we will Back INTO — SYNCHRONOUSLY, BEFORE the
        // CurrentStep reassignment detaches the leaving workspace view (the same load-bearing
        // ordering as OnDesignPlan/OnGapAnalysis). The Audit step has no surface of its own, so it
        // never parks anything — Back must restore the live workspace viewport.
        ParkSurface(current.GraphRenderer);
        CurrentStep = audit;
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

    /// <summary>Parks a step's live graph surface in the hidden parking lot (#122 / ADR-025) so it
    /// stays rooted (page + viewport alive) across the forward swap. A no-op when no coordinator is
    /// wired (headless) or the step has no renderer surface (null factory / missing WebView2). MUST
    /// be called BEFORE the <see cref="CurrentStep"/> reassignment that detaches the leaving view —
    /// the single load-bearing ordering invariant.</summary>
    private void ParkSurface(IGraphRenderer? renderer)
    {
        if (_surfaceCoordinator is { } coordinator && renderer?.View is { } view)
        {
            coordinator.Park(view);
        }
    }

    /// <summary>Disposes a step and drops it from the teardown set (#122 reclaim): the renderer
    /// Dispose (Slice 1) frees the WebView, so abandoned Gap/superseded-Plan surfaces do not
    /// accumulate. Idempotent — a step's <c>Dispose</c> is idempotent and a not-tracked step is
    /// silently skipped on removal.</summary>
    private void DisposeAndUntrack(IDisposable step)
    {
        _disposableSteps.Remove(step);
        step.Dispose();
    }

    /// <summary>
    /// Disposes the surviving disposable steps (AP 4.2.2 dispose discipline): the workspace and any
    /// still-live plan/gap step. Abandoned plan/gap steps were already disposed + untracked on Back
    /// (#122 reclaim), so this tears down only what is still tracked. Each step's <c>Dispose</c> is
    /// idempotent (cancels its in-flight load/render, then disposes its renderer). Idempotent overall.
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
