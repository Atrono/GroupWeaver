using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GroupWeaver.App.Audit;
using GroupWeaver.App.Diagnostics;
using GroupWeaver.App.Graph;
using GroupWeaver.App.Rules;
using GroupWeaver.App.Settings;
using GroupWeaver.App.Startup;
using GroupWeaver.App.Views;
using GroupWeaver.Core.Model;
using GroupWeaver.Core.Providers;
using GroupWeaver.Core.Rules;
using Microsoft.Extensions.Logging;

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

    /// <summary>ADR-031 D1: the targeted live-provider builder — <c>(server, baseDn) =></c> a
    /// provider bound to that DC/domain under integrated auth. Threaded to every
    /// <see cref="ConnectionViewModel"/> so the Connect card's Advanced disclosure can target a
    /// specific domain/DC; <c>null</c> (the default / pre-ADR-031 call sites + tests) keeps the
    /// serverless-only behavior.</summary>
    private readonly Func<string?, string?, IDirectoryProvider>? _targetedProviderFactory;

    private readonly Func<IGraphRenderer>? _graphRendererFactory;
    private readonly RulesetLocator _locator;

    /// <summary>The ADR-022 D4 rail-state store, threaded down the Shell→RootPicker→Workspace
    /// path exactly as <see cref="_locator"/> is; each workspace seeds + persists its rail state
    /// through it. Defaulted (pre-ADR-022 call sites/tests get the real <c>%APPDATA%</c> layout).</summary>
    private readonly UiStateStore _uiStateStore;

    /// <summary>The ADR-032 (#190) audit run-history store, installed into each Audit step so its
    /// Save-run + Compare commands persist/list runs under <c>%APPDATA%\GroupWeaver\runs\</c>.
    /// Defaulted (pre-ADR-032 call sites/tests get the real <c>%APPDATA%</c> layout); a headless test
    /// injects a temp-dir-backed store. The ONLY writes are run JSON files — never AD.</summary>
    private readonly AuditRunStore _auditRunStore;

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

    /// <summary>The Plan step authored over the active workspace (#122 keep-alive): set in
    /// <see cref="OnDesignPlan"/> and KEPT ALIVE across Back to Workspace (mirroring the workspace's
    /// own never-disposed-on-Back lifecycle) — Back parks its surface but does NOT dispose it, so the
    /// next Design-plan re-enters this SAME instance with its authored content + graph preview intact.
    /// Disposed + replaced only when superseded by a fresh plan bound to a DIFFERENT base OU (a Reload-
    /// scope / new RootChosen changes the root) or at shell teardown. <c>null</c> until the first
    /// Design-plan. Bounds the live WebViews to ≤ Workspace + current Plan (+ a transient Gap).</summary>
    private PlanViewModel? _currentPlan;

    /// <summary>The workspace the CURRENT audit step backs into (WP5e / ADR-028): set in
    /// <see cref="OnAudit"/>, used by <see cref="OnRulesetApplied"/> to re-thread that PARKED
    /// workspace too when a triage Save fires while the audit is the current step — so the graph
    /// halos + violations rail update even though Audit (a table view) is showing. Cleared when the
    /// audit is abandoned (Back) or superseded. <c>null</c> when no audit is live.</summary>
    private WorkspaceViewModel? _auditBackWorkspace;

    /// <summary>The <c>App.Shell</c> logger (ADR-037 D5): <c>StepChanged</c> — the E2E timeline
    /// backbone — plus <c>WebView2Missing</c>. Defaulted to <see cref="AppLog.Factory"/> (a no-op
    /// NullLogger in headless tests, the installed sink in production).</summary>
    private readonly ILogger _log;

    /// <summary>What caused the in-flight <see cref="CurrentStep"/> assignment — set immediately
    /// before each assignment site, consumed (and reset) by the <c>StepChanged</c> log in
    /// <see cref="OnCurrentStepChanged(object, object)"/>. An untagged assignment (tests setting
    /// <see cref="CurrentStep"/> directly) logs <c>"direct"</c>.</summary>
    private string? _stepTrigger;

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

    /// <summary>ADR-038 WP6 (#245): the <c>--e2e</c> channel's step-change tap, raised from the
    /// SAME choke point <see cref="OnCurrentStepChanged(object?, object)"/> logs
    /// <c>StepChanged</c> from — never a second hook onto <see cref="CurrentStep"/>. The
    /// composition root's <c>Automation.E2eChannel</c> subscribes this (when <c>--e2e</c> is
    /// set) to mirror the log line onto its stdout trace; <c>null</c> when nothing is
    /// subscribed (headless tests, no <c>--e2e</c>) so every pre-WP6 caller is unaffected.</summary>
    public event EventHandler<StepChangedEventArgs>? StepChanged;

    /// <summary>Logs <c>StepChanged{from,to,trigger}</c> (ADR-037 D5 — the E2E timeline backbone)
    /// at the single choke point every step swap passes through, THEN raises
    /// <see cref="StepChanged"/> with the identical (from, to, trigger) triple (ADR-038 WP6) —
    /// the log call is never duplicated. Step names only, never subject data; the ctor's initial
    /// Connect step sets the backing field directly and is NOT logged/raised (the banner already
    /// marks startup).</summary>
    partial void OnCurrentStepChanged(object? oldValue, object newValue)
    {
        var trigger = _stepTrigger ?? "direct";
        _stepTrigger = null;
        var from = StepName(oldValue);
        var to = StepName(newValue);
        _log.LogInformation(
            new EventId(0, "StepChanged"), "StepChanged {from} {to} {trigger}", from, to, trigger);
        StepChanged?.Invoke(this, new StepChangedEventArgs(from, to, trigger));
    }

    /// <summary>The stable step name for <c>StepChanged</c> (ADR-003 D5 step machine's
    /// vocabulary): Connect / PickRoot / Workspace / Plan / Gap / Audit.</summary>
    private static string StepName(object? step) => step switch
    {
        ConnectionViewModel => "Connect",
        RootPickerViewModel => "PickRoot",
        WorkspaceViewModel => "Workspace",
        PlanViewModel => "Plan",
        GapViewModel => "Gap",
        AuditViewModel => "Audit",
        null => "None",
        _ => step.GetType().Name,
    };

    /// <summary>The CURRENT step's stable name (ADR-038 WP6, #245): the <c>--e2e</c> channel's
    /// <c>state</c> command reply reuses this SAME vocabulary rather than re-deriving it.</summary>
    public string CurrentStepName => StepName(CurrentStep);

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

    /// <summary>ADR-026 D4 (extended — System/auto): the app-chrome theme choice — Dark / Light /
    /// System. Seeded in the ctor from the persisted <see cref="UiState.Theme"/> (enum name) and
    /// cycled by <see cref="ToggleTheme"/>; the top strip's theme button binds its glyph to
    /// <see cref="ThemeGlyph"/> and its tooltip to <see cref="ThemeTooltip"/>. Shell-level because
    /// the toggle is shell chrome reachable on every step (unlike Focus, which is workspace-only).
    /// <see cref="System"/> follows the OS preference (resolved via <see cref="_platformThemeProvider"/>).</summary>
    [ObservableProperty]
    private AppThemeChoice _themeChoice;

    /// <summary>The OS light/dark-preference seam (ADR-026 extension): production reads
    /// <c>PlatformSettings</c>, tests inject a fake. Used to RESOLVE <see cref="AppThemeChoice.System"/>
    /// to a concrete variant and to fire a live re-apply on an OS switch. Defaulted to the production
    /// impl, exactly like <see cref="_uiStateStore"/>.</summary>
    private readonly IPlatformThemeProvider _platformThemeProvider;

    /// <summary>True only while subscribed to <see cref="IPlatformThemeProvider.OsPreferenceChanged"/>
    /// — i.e. while <see cref="ThemeChoice"/> is <see cref="AppThemeChoice.System"/>. Tracked so the
    /// subscription is added/removed exactly once as the choice enters/leaves System and torn down on
    /// <see cref="Dispose"/>.</summary>
    private bool _subscribedToOsPreference;

    /// <summary>The resolved app-chrome variant: System resolves through the OS preference, Dark/Light
    /// pass through. True ⇒ Light. The single source of truth for both the applied
    /// <c>RequestedThemeVariant</c> and the WebView canvas sync.</summary>
    private bool ResolvedIsLight => ThemeChoice switch
    {
        AppThemeChoice.Light => true,
        AppThemeChoice.Dark => false,
        _ => _platformThemeProvider.GetOsPreference() == Avalonia.Styling.ThemeVariant.Light,
    };

    /// <summary>ADR-026 D4: the theme-toggle button's glyph — a sun for Light, a moon for Dark, a
    /// half-filled circle for System (all from the same symbol family that already renders in the
    /// app font); change-notified off <see cref="ThemeChoice"/>.</summary>
    public string ThemeGlyph => ThemeChoice switch
    {
        AppThemeChoice.Light => "☀",
        AppThemeChoice.System => "◐",
        _ => "☾",
    };

    /// <summary>ADR-026 D4: the theme-toggle button's tooltip — names the current state and the next
    /// one the toggle will cycle to (Dark → Light → System → Dark); change-notified off
    /// <see cref="ThemeChoice"/>.</summary>
    public string ThemeTooltip => ThemeChoice switch
    {
        AppThemeChoice.Light => "Theme: Light — tap for System",
        AppThemeChoice.System => "Theme: System — tap for Dark",
        _ => "Theme: Dark — tap for Light",
    };

    /// <summary>Keeps <see cref="ThemeGlyph"/>/<see cref="ThemeTooltip"/> in sync whenever
    /// <see cref="ThemeChoice"/> changes, and (un)subscribes to the OS-preference event so the live
    /// follow is active exactly while the choice is System.</summary>
    partial void OnThemeChoiceChanged(AppThemeChoice value)
    {
        OnPropertyChanged(nameof(ThemeGlyph));
        OnPropertyChanged(nameof(ThemeTooltip));
        UpdateOsPreferenceSubscription();
    }

    public ShellViewModel(
        Func<bool, IDirectoryProvider> providerFactory,
        StartupOptions startupOptions,
        WebView2RuntimeStatus? webView2Runtime = null,
        Func<IGraphRenderer>? graphRendererFactory = null,
        EffectiveRuleset? ruleset = null,
        RulesetLocator? locator = null,
        UiStateStore? uiStateStore = null,
        Func<string?, string?, IDirectoryProvider>? targetedProviderFactory = null,
        AuditRunStore? auditRunStore = null,
        IPlatformThemeProvider? platformThemeProvider = null,
        ILoggerFactory? loggerFactory = null)
    {
        // ADR-037 WP1: defaulted like the stores — AppLog.Factory is the installed sink in
        // production and NullLoggerFactory in headless tests (which run exactly as before).
        var logFactory = loggerFactory ?? AppLog.Factory;
        _log = logFactory.CreateLogger("App.Shell");
        _providerFactory = providerFactory;
        _targetedProviderFactory = targetedProviderFactory;
        _graphRendererFactory = graphRendererFactory;
        _ruleset = ruleset;
        // ADR-022 D4: defaulted like the locator — the composition root passes the one store,
        // pre-ADR-022 tests omit it and get the real %APPDATA% layout.
        _uiStateStore = uiStateStore ?? new UiStateStore();
        // ADR-032 (#190): defaulted like the other stores — the real %APPDATA%\GroupWeaver\runs\
        // layout in production, a temp-dir seam in headless tests. Carries the Store.AuditRuns
        // logger (ADR-037 D5: AuditRunSkipped replaces the old Debug.WriteLine trio).
        _auditRunStore = auditRunStore ?? new AuditRunStore(logFactory.CreateLogger("Store.AuditRuns"));
        // ADR-026 extension (System/auto): the OS-preference seam, defaulted to the production impl
        // (reads PlatformSettings) exactly like the stores above — tests inject a fake.
        _platformThemeProvider = platformThemeProvider ?? new DefaultPlatformThemeProvider();
        // ADR-026 D4: seed the app-chrome theme choice from the persisted state (enum name; an
        // unparseable / legacy / missing name falls back to Dark — dark-first) and apply the
        // resolved ThemeVariant on startup (a no-op when the app is not yet running, e.g. some
        // headless theories — the bound ThemeChoice still reflects the persisted choice). Set the
        // backing field directly so OnThemeChoiceChanged's OS-subscription arm runs ONCE, below.
        _themeChoice = ParseThemeChoice(_uiStateStore.Load().Theme);
        ApplyThemeVariant();
        // Subscribe to OS-preference changes iff the seeded choice is System (the OnThemeChoiceChanged
        // hook does not fire for the backing-field seed). Idempotent — guarded by _subscribedToOsPreference.
        UpdateOsPreferenceSubscription();
        // Defaulted (AP 3.3 / ADR-011 §1): App.axaml.cs passes the one composition-root
        // locator; pre-S8 tests omit it and get the real %APPDATA% layout. Settings
        // Save persists to its UserRulesetPath; the headless tests inject a temp-dir seam.
        _locator = locator ?? new RulesetLocator();

        // Default = real probe, so harnesses constructing the shell directly behave like
        // the app; S8's headless tests pass an explicit status to force the missing state.
        var runtime = webView2Runtime ?? WebView2Runtime.Probe();
        WebView2Missing = !runtime.IsInstalled;
        WebView2Version = runtime.Version;
        if (WebView2Missing)
        {
            // ADR-037 D5: the persistent banner state, now also machine-readable evidence.
            _log.LogWarning(new EventId(0, "WebView2Missing"), "WebView2Missing");
        }

        var connect = new ConnectionViewModel(providerFactory, OnConnected, _targetedProviderFactory);
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
    /// The top command strip's "?" affordance: shows the keyboard/gesture cheat sheet as a
    /// modal <see cref="KeyboardHelpWindow"/> over the main window — its own top-level Window
    /// (never layered over the workspace GraphHost, ADR-001 airspace guardrail 5), mirroring
    /// <see cref="OpenSettingsAsync"/>. Static content, so there is no VM to build.
    /// <c>ShowDialog</c> is the production-only path (headless-hostile); the non-modal show is
    /// the off-desktop-lifetime fallback.
    /// </summary>
    [RelayCommand]
    private async Task OpenKeyboardHelpAsync()
    {
        var window = new KeyboardHelpWindow();
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

    /// <summary>ADR-026 D4 (extended — System/auto): cycles the app-chrome theme choice live and
    /// persists it. The cycle is Dark → Light → System → Dark; setting <see cref="ThemeChoice"/>
    /// re-raises the glyph/tooltip and (un)subscribes the OS-preference follow via
    /// <see cref="OnThemeChoiceChanged"/>. Applies the RESOLVED <c>ThemeVariant</c> to the running
    /// app (so every native screen re-themes via the DynamicResource brushes), re-tones the graph
    /// canvas(es), and writes the enum name to <see cref="UiState.Theme"/> through the shared store
    /// (read-modify-write so the rail state the workspace owns is preserved). Reachable on every
    /// step (the top strip's theme button).</summary>
    [RelayCommand]
    private void ToggleTheme()
    {
        ThemeChoice = ThemeChoice switch
        {
            AppThemeChoice.Dark => AppThemeChoice.Light,
            AppThemeChoice.Light => AppThemeChoice.System,
            _ => AppThemeChoice.Dark,
        };
        ApplyThemeVariant();
        // ADR-026 WP1b: re-theme the graph canvas(es) live. The WebView2 bundle does not observe
        // RequestedThemeVariant — it must be pushed the variant explicitly. Re-theme EVERY tracked
        // graph-bearing step (not just CurrentStep): a parked surface (Workspace/Plan behind a Plan/
        // Gap step) survives its page across the round-trip, so it must re-tone now or it would
        // return in the stale theme (a re-mount does NOT re-render). Fire-and-forget, never-throw.
        ApplyCanvasTheme();
        // Preserve the ADR-022 rail fields the workspace owns: read-modify-write the shared store.
        _uiStateStore.Save(_uiStateStore.Load() with { Theme = ThemeChoice.ToString() });
    }

    /// <summary>ADR-026 WP1b: pushes the RESOLVED light/dark variant (<see cref="ResolvedIsLight"/> —
    /// System syncs the OS-resolved variant) to every tracked graph-bearing step's renderer
    /// (Workspace/Plan/Gap). Fire-and-forget (the renderer's <see cref="IGraphRenderer.SetThemeAsync"/>
    /// is itself never-throw and no-ops before its bundle is ready — a not-yet-rendered renderer
    /// converges anyway because each render prepends the current theme). Covers parked surfaces too,
    /// so returning to a Back-target finds it themed.</summary>
    private void ApplyCanvasTheme()
    {
        var isLight = ResolvedIsLight;
        foreach (var step in _disposableSteps)
        {
            if (RendererOf(step) is { } renderer)
            {
                _ = renderer.SetThemeAsync(isLight);
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

    /// <summary>Applies the RESOLVED variant (<see cref="ResolvedIsLight"/> — Dark/Light pass
    /// through, System reads <see cref="IPlatformThemeProvider.GetOsPreference"/>) to
    /// <c>Application.Current.RequestedThemeVariant</c> (Light ⇒
    /// <see cref="Avalonia.Styling.ThemeVariant.Light"/>, else Dark). A no-op under two guards:
    /// (1) no app is running (some headless theories), and (2) the caller is NOT on the Avalonia UI
    /// thread — <c>RequestedThemeVariant</c> is a UI-thread-affine setter that throws "Call from
    /// invalid thread" off-thread. In production both the ctor (during
    /// <c>OnFrameworkInitializationCompleted</c>), <c>ToggleTheme</c>, and the OS-change handler run
    /// on the UI thread, so the apply still happens; off-thread (tests, any non-UI caller) it safely
    /// skips the global setter while the bound <see cref="ThemeChoice"/> still reflects the persisted
    /// choice. Synchronous skip — never dispatched (that would reorder startup).</summary>
    private void ApplyThemeVariant()
    {
        if (Application.Current is { } app && Avalonia.Threading.Dispatcher.UIThread.CheckAccess())
        {
            app.RequestedThemeVariant = ResolvedIsLight
                ? Avalonia.Styling.ThemeVariant.Light
                : Avalonia.Styling.ThemeVariant.Dark;
        }
    }

    /// <summary>Parses a persisted <see cref="UiState.Theme"/> name into an <see cref="AppThemeChoice"/>,
    /// case-insensitively. An unparseable / legacy / empty name falls back to
    /// <see cref="AppThemeChoice.Dark"/> (dark-first — the ADR-026 D4 never-throw load contract); the
    /// original <c>"Light"</c> still round-trips.</summary>
    private static AppThemeChoice ParseThemeChoice(string? persisted) =>
        Enum.TryParse<AppThemeChoice>(persisted, ignoreCase: true, out var choice)
            ? choice
            : AppThemeChoice.Dark;

    /// <summary>(Un)subscribes to <see cref="IPlatformThemeProvider.OsPreferenceChanged"/> so the live
    /// OS follow is active exactly while <see cref="ThemeChoice"/> is <see cref="AppThemeChoice.System"/>.
    /// Idempotent (guarded by <see cref="_subscribedToOsPreference"/>) — called from the ctor seed, the
    /// <see cref="OnThemeChoiceChanged"/> hook, and <see cref="Dispose"/>.</summary>
    private void UpdateOsPreferenceSubscription()
    {
        var wantSystem = ThemeChoice == AppThemeChoice.System;
        if (wantSystem && !_subscribedToOsPreference)
        {
            _platformThemeProvider.OsPreferenceChanged += OnOsPreferenceChanged;
            _subscribedToOsPreference = true;
        }
        else if (!wantSystem && _subscribedToOsPreference)
        {
            _platformThemeProvider.OsPreferenceChanged -= OnOsPreferenceChanged;
            _subscribedToOsPreference = false;
        }
    }

    /// <summary>The OS flipped its light/dark preference while the choice is System: re-apply the
    /// resolved variant to the native chrome and re-tone the graph canvas(es). UI-thread-guarded
    /// inside <see cref="ApplyThemeVariant"/>; <see cref="ApplyCanvasTheme"/> is fire-and-forget /
    /// never-throw, so the OS event handler never throws back into the platform.</summary>
    private void OnOsPreferenceChanged(object? sender, EventArgs e)
    {
        ApplyThemeVariant();
        ApplyCanvasTheme();
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
        _stepTrigger = "connected";
        CurrentStep = new RootPickerViewModel(
            provider, connection, OnBackToConnect, OnRootChosen, WebView2Missing,
            _graphRendererFactory, _ruleset, _uiStateStore);
    }

    /// <summary>The picker's Back: drop the provider, start over on a fresh Connect step.</summary>
    private void OnBackToConnect()
    {
        Provider = null;
        IsDemoMode = false;
        _stepTrigger = "backToConnect";
        CurrentStep = new ConnectionViewModel(_providerFactory, OnConnected, _targetedProviderFactory);
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
        _stepTrigger = "rootChosen";
        CurrentStep = workspace;
    }

    /// <summary>
    /// The Ist→Plan switch (AP 4.2.2 / ADR-014; #122 keep-alive). KEEP-ALIVE: the Plan step mirrors
    /// the workspace's never-disposed-on-Back lifecycle — Back parks the plan surface and returns the
    /// SAME (never-disposed) <paramref name="current"/> workspace instance, and re-entering Plan
    /// re-enters the SAME (never-disposed) <see cref="_currentPlan"/> instance with its authored
    /// content + graph preview intact. Both the workspace and the plan are tracked and disposed only
    /// at shell teardown. A fresh empty plan is built ONLY when there is none alive (first entry, or
    /// after a base-OU change — a plan is bound to its base OU). On re-entry the ruleset is re-threaded
    /// (mirroring <see cref="OnRulesetApplied"/>) so a settings flip while the plan was parked is
    /// honored. The plan carries the same renderer factory (it builds its OWN renderer instance).
    /// </summary>
    public void OnDesignPlan(WorkspaceViewModel current)
    {
        var effective = _ruleset ?? new EffectiveRuleset(RulesetLoader.LoadDefault(), FromUserFile: false, []);

        // KEEP-ALIVE re-entry: a live plan whose base OU still matches this workspace's root is
        // re-entered as-is (authored content + parked graph preview survive). Only discard + rebuild
        // when there is none, it was disposed, or its base OU no longer matches (a plan is bound to
        // its base OU — a Reload-scope / new RootChosen changes the root and must reset the plan).
        if (_currentPlan is { IsDisposed: false } alive
            && Dn.Comparer.Equals(alive.Plan.BaseOuDn, current.RootDn))
        {
            // Re-thread the ruleset in case a settings Apply/Save flipped it while the plan was
            // parked (mirrors OnRulesetApplied's live-plan arm) — fire-and-forget, never-throw.
            _ = alive.ApplyRulesetAsync(effective.Ruleset);

            // PARK the workspace surface we Back INTO — SYNCHRONOUSLY, BEFORE the swap detaches the
            // leaving workspace view (the load-bearing ordering invariant; see ParkSurface).
            ParkSurface(current.GraphRenderer);
            _stepTrigger = "designPlan";
            CurrentStep = alive;
            return;
        }

        // No live plan (first entry) or a stale one bound to a different base OU: dispose + untrack
        // the stale instance (frees its WebView) and build a fresh empty plan at this root.
        if (_currentPlan is { } stale)
        {
            DisposeAndUntrack(stale);
            _currentPlan = null;
        }

        PlanViewModel? plan = null;
        plan = new PlanViewModel(
            current.RootDn,
            effective,
            _graphRendererFactory,
            WebView2Missing,
            // Back to Workspace KEEPS the Plan alive (#122 keep-alive): it is NOT disposed/untracked
            // here — it stays tracked and is re-entered with its content intact on the next Design-plan
            // (disposed only at shell teardown, exactly like the workspace). Back only PARKS the plan
            // surface (so its graph preview survives the round-trip) then restores the workspace.
            onBackToExplore: () =>
            {
                // PARK the plan surface we will Back AWAY FROM — SYNCHRONOUSLY, BEFORE the swap
                // detaches the leaving plan view (the symmetric counterpart of the forward park: the
                // plan's live page + viewport survive into the re-entry instead of the detach guard
                // releasing it). Mirror of how leaving the workspace parks the workspace surface.
                ParkSurface(plan?.GraphRenderer);
                _stepTrigger = "backToExplore";
                CurrentStep = current;
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
        _stepTrigger = "designPlan";
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
                _stepTrigger = "backToPlan";
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
        _stepTrigger = "gapAnalysis";
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
                _stepTrigger = "backToWorkspace";
                CurrentStep = current;
                _auditBackWorkspace = null;
                if (audit is not null)
                {
                    DisposeAndUntrack(audit);
                }
            },
            // WP2 / ADR-013 §2: the real connection summary for the exported HTML report header
            // (same line the status bar + the workspace HTML export use).
            connectionSummary: current.ConnectionSummary);
        // The workspace this audit backs into — re-threaded alongside the audit step when a triage
        // Save fires (OnRulesetApplied), so the parked graph + rail update too (ADR-028).
        _auditBackWorkspace = current;
        // Arm the audit triage seam (WP5e / ADR-028) — the same install idiom as the Design-plan
        // callback. The shell OWNS the single write path: it routes every triage batch through
        // BuildSettingsViewModel()'s gate (BuildRuleset → Serialize → RulesetLoader.Load → Save),
        // which fires RulesetApplied → OnRulesetApplied (re-thread audit + parked workspace). Writes
        // hit ONLY %APPDATA%\GroupWeaver\ruleset.jsonc + in-memory state — never AD.
        audit.UseTriageCallback(requests => ApplyTriage(requests, current));
        // ADR-032 (#190): arm the run-history seam so the audit's Save-run + Compare commands persist /
        // list runs under %APPDATA%\GroupWeaver\runs\ (read-only toward AD — the only writes are run JSON).
        audit.UseRunStore(_auditRunStore);
        // WP "persist view state": arm the UI-preference seam with the shell's SHARED UiStateStore so the
        // audit RESTORES its persisted filters + sort (the VM is built fresh each step-open) and persists
        // every later change. Read-only toward AD — the only write is %APPDATA%\GroupWeaver\ui-state.json.
        audit.UseUiStateStore(_uiStateStore);
        Track(audit);

        // #122 (ADR-025): PARK the workspace surface we will Back INTO — SYNCHRONOUSLY, BEFORE the
        // CurrentStep reassignment detaches the leaving workspace view (the same load-bearing
        // ordering as OnDesignPlan/OnGapAnalysis). The Audit step has no surface of its own, so it
        // never parks anything — Back must restore the live workspace viewport.
        ParkSurface(current.GraphRenderer);
        _stepTrigger = "audit";
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
        // ADR-026 extension: drop the OS-preference subscription if the choice was System (no-op
        // otherwise) — so the shell never outlives its hook on the platform event.
        if (_subscribedToOsPreference)
        {
            _platformThemeProvider.OsPreferenceChanged -= OnOsPreferenceChanged;
            _subscribedToOsPreference = false;
        }

        foreach (var step in _disposableSteps)
        {
            step.Dispose();
        }

        _disposableSteps.Clear();
    }
}

/// <summary>ADR-038 WP6 (#245) payload for <see cref="ShellViewModel.StepChanged"/> — the SAME
/// (from, to, trigger) triple the <c>StepChanged</c> log line carries, never a richer object
/// (step names only, no subject data).</summary>
public sealed record StepChangedEventArgs(string From, string To, string Trigger);
