using System;
using System.Linq;
using System.Threading.Tasks;

using Avalonia.Headless.XUnit;
using Avalonia.Threading;

using GroupWeaver.App.Graph;
using GroupWeaver.App.Startup;
using GroupWeaver.App.Tests.Fakes;
using GroupWeaver.App.ViewModels;
using GroupWeaver.Core.Model;
using GroupWeaver.Core.Providers;
using GroupWeaver.Providers;

using Microsoft.Extensions.Logging;

using Xunit;

namespace GroupWeaver.App.Tests;

/// <summary>
/// The VM half of "a renderer fault must never crash" (the companion to the visual-tree regression in
/// <see cref="BackNavigationStepSwapTests"/>). The production <see cref="IGraphRenderer"/> is never-throw
/// by contract — a degraded renderer (ready timeout, JS error, …) raises
/// <see cref="FakeGraphRenderer.RaiseRendererError"/> and returns NORMALLY; the workspace reuses the ONE
/// inline <see cref="WorkspaceViewModel.LoadError"/> surface for it (ctor wiring) and leaves
/// <see cref="WorkspaceViewModel.IsLoading"/> alone. These tests pin that contract on the steps the Back
/// round-trip re-renders, and additionally pin that a hypothetically FAULTED renderer task never escapes
/// the fire-and-forget / step-switch paths (the genuinely dangerous ones — a throw there crashes the app
/// out-of-band). Where a fault rides an OBSERVABLE task (Initialization / a directly-awaited Refresh) it
/// is surfaced through that task, not swallowed — documented here so the boundary is explicit.
///
/// <para>The <see cref="FakeGraphRenderer"/> already exposes per-call injectable results
/// (<c>ShowGraphResult</c>/<c>UpdateGraphResult</c>/<c>FocusResult</c>/<c>SelectResult</c>/
/// <c>ShowDiffGraphResult</c>) and the <c>RaiseRendererError</c> event — these tests use those existing
/// seams and do NOT touch the default-fake behavior.</para>
/// </summary>
public sealed class RendererFaultNeverCrashesTests
{
    private const string RootDn = "OU=Lab,DC=stub,DC=lab";
    private static readonly WebView2RuntimeStatus Present = new(IsInstalled: true, Version: "test");

    // === Workspace: the production never-throw fault channel (RendererError -> LoadError) ========

    /// <summary>
    /// A renderer that signals failure the production way — <c>RaiseRendererError</c> AFTER a clean load
    /// — surfaces inline as <see cref="WorkspaceViewModel.LoadError"/> (the ctor wires
    /// <c>RendererError += LoadError = "{source}: {message}"</c>) and leaves
    /// <see cref="WorkspaceViewModel.IsLoading"/> untouched. No exception escapes. This is the real
    /// degraded-renderer path; the faulted-task pins below cover contract VIOLATIONS.
    /// </summary>
    [AvaloniaFact]
    public async Task Workspace_RendererErrorEvent_SurfacesAsLoadError_NeverCrashes()
    {
        var fake = new FakeGraphRenderer();
        var vm = Workspace(EmptyProvider(), () => fake);
        await vm.Initialization;

        Assert.Null(vm.LoadError);
        var ex = Record.Exception(() => fake.RaiseRendererError("cytoscape", "kaboom"));

        Assert.Null(ex); // raising the event never throws back into the caller
        Assert.Equal("cytoscape: kaboom", vm.LoadError);
        Assert.False(vm.IsLoading); // a renderer error must not strand the busy gate
    }

    /// <summary>
    /// The fire-and-forget select path: the <see cref="WorkspaceViewModel.SelectedDn"/> setter issues
    /// <c>_ = renderer.SelectAsync(...)</c> (a DISCARDED task — selection must stay responsive). With
    /// <see cref="FakeGraphRenderer.SelectResult"/> faulted, the setter must NOT throw and pumping the
    /// dispatcher must not crash — the fault dies on the discarded task, never on the property/command
    /// path. This is the precise "never crash the async-void/RelayCommand path" the fix protects.
    /// </summary>
    [AvaloniaFact]
    public async Task Workspace_FaultedSelectTask_NeverEscapesTheSelectedDnSetter()
    {
        var fake = new FakeGraphRenderer
        {
            SelectResult = Task.FromException(new InvalidOperationException("boom-select")),
        };
        var vm = Workspace(EmptyProvider(), () => fake);
        await vm.Initialization;

        var setterEx = Record.Exception(() => vm.SelectedDn = "CN=anything,OU=Lab,DC=stub,DC=lab");
        Assert.Null(setterEx);

        // Pump the dispatcher: a fire-and-forget fault must not surface as a dispatcher crash either.
        var pumpEx = Record.Exception(() => Dispatcher.UIThread.RunJobs());
        Assert.Null(pumpEx);
        Assert.Equal("CN=anything,OU=Lab,DC=stub,DC=lab", vm.SelectedDn);
    }

    /// <summary>
    /// A faulted <see cref="FakeGraphRenderer.UpdateGraphResult"/> on the LIVE re-thread path
    /// (<see cref="WorkspaceViewModel.ApplyRulesetAsync"/>, the seam a settings Apply drives and the
    /// shell calls on the live step) is observable on the AWAITED task — never an out-of-band crash. The
    /// caller awaits this seam (<c>ShellViewModel.OnRulesetApplied</c> awaits it), so the fault is a
    /// normal awaitable exception, not a process-killer. Pins that the await boundary is honored.
    /// </summary>
    [AvaloniaFact]
    public async Task Workspace_FaultedUpdateTask_OnApplyRuleset_IsObservableOnTheAwaitedTask()
    {
        var fake = new FakeGraphRenderer
        {
            UpdateGraphResult = Task.FromException(new InvalidOperationException("boom-update")),
        };
        var vm = Workspace(NonEmptyProvider(), () => fake);
        await vm.Initialization;
        Assert.NotNull(vm.Snapshot); // ApplyRulesetAsync only pushes with a loaded snapshot + renderer

        // Awaiting the seam surfaces the renderer fault as a normal exception (not a crash); the test
        // simply proves it is contained to the awaited task and does not escape elsewhere.
        var ex = await Record.ExceptionAsync(
            async () => await vm.ApplyRulesetAsync(GroupWeaver.Core.Rules.RulesetLoader.LoadDefault()));
        Assert.IsType<InvalidOperationException>(ex);
        Assert.Equal("boom-update", ex!.Message);
    }

    // === Plan / Gap: the step-switch + fire-and-forget refresh survive a renderer fault =========

    /// <summary>
    /// Switching into Gap fires-and-forgets <c>gap.RefreshAsync()</c> inside
    /// <see cref="ShellViewModel.OnGapAnalysis"/> (it awaits renderer-ready internally, so the mount race
    /// is handled). With the gap renderer's <see cref="FakeGraphRenderer.ShowDiffGraphResult"/> faulted,
    /// the SWITCH itself must not throw — the fault rides the discarded task, never the synchronous
    /// shell-switch path. Pumping the dispatcher afterwards must not crash either. The current step is the
    /// gap regardless (the switch completed). Awaiting the gap's <c>RefreshAsync</c> directly DOES surface
    /// the fault — pinned so the observable-vs-discarded boundary is explicit.
    /// </summary>
    [AvaloniaFact(Timeout = 60_000)]
    public async Task Gap_FaultedDiffTask_NeverCrashesTheShellSwitch_ButIsObservableWhenAwaited()
    {
        FakeGraphRenderer Factory() => new()
        {
            ShowDiffGraphResult = Task.FromException(new InvalidOperationException("boom-diff")),
            ShowGraphResult = Task.CompletedTask, // workspace/plan render cleanly
        };
        var shell = new ShellViewModel(
            _ => new DemoProvider(),
            new StartupOptions(Demo: false),
            Present,
            graphRendererFactory: Factory,
            ruleset: null,
            locator: null,
            uiStateStore: null);

        var workspace = await DriveDemoWorkspaceAsync(shell);
        shell.OnDesignPlan(workspace);
        var plan = Assert.IsType<PlanViewModel>(shell.CurrentStep);

        // The fire-and-forget refresh fault must not escape the synchronous switch.
        var switchEx = Record.Exception(() => shell.OnGapAnalysis(plan, workspace));
        Assert.Null(switchEx);
        var gap = Assert.IsType<GapViewModel>(shell.CurrentStep);

        var pumpEx = Record.Exception(() => Dispatcher.UIThread.RunJobs());
        Assert.Null(pumpEx);

        // Awaited directly, the same compute path surfaces the fault on the task (observable, not lost).
        var awaitedEx = await Record.ExceptionAsync(async () => await gap.RefreshAsync());
        Assert.IsType<InvalidOperationException>(awaitedEx);

        shell.Dispose();
    }

    /// <summary>
    /// Back from Gap and from Plan (<see cref="GapViewModel.BackCommand"/> /
    /// <see cref="PlanViewModel.BackCommand"/>) are plain <c>RelayCommand</c>s that only swap
    /// <see cref="ShellViewModel.CurrentStep"/> — they must NEVER touch a renderer, so even with every
    /// renderer result faulted, executing Back never throws. Pins that the Back command path is
    /// renderer-independent (the re-render on re-attach is the view's job, exercised in
    /// <see cref="BackNavigationStepSwapTests"/>, not the command's).
    /// </summary>
    [AvaloniaFact(Timeout = 60_000)]
    public async Task BackCommands_NeverTouchTheRenderer_SoAFaultedRendererCannotCrashThem()
    {
        FakeGraphRenderer Factory() => new()
        {
            ShowGraphResult = Task.FromException(new InvalidOperationException("boom-show")),
            UpdateGraphResult = Task.FromException(new InvalidOperationException("boom-update")),
            ShowDiffGraphResult = Task.FromException(new InvalidOperationException("boom-diff")),
            FocusResult = Task.FromException(new InvalidOperationException("boom-focus")),
        };
        var shell = new ShellViewModel(
            _ => new DemoProvider(),
            new StartupOptions(Demo: false),
            Present,
            graphRendererFactory: Factory,
            ruleset: null,
            locator: null,
            uiStateStore: null);

        // The workspace ctor-load pushes ShowGraphAsync (faulted) — observable on Initialization, never
        // an out-of-band crash; swallow it to reach the Back commands under test.
        var workspace = await DriveDemoWorkspaceAsync(shell, swallowInitFault: true);
        shell.OnDesignPlan(workspace);
        var plan = Assert.IsType<PlanViewModel>(shell.CurrentStep);
        shell.OnGapAnalysis(plan, workspace);
        var gap = Assert.IsType<GapViewModel>(shell.CurrentStep);

        // Back from Gap -> Plan, and Back from Plan -> Workspace: neither command may throw.
        var backFromGap = Record.Exception(() => gap.BackCommand.Execute(null));
        Assert.Null(backFromGap);
        Assert.Same(plan, shell.CurrentStep);

        var backFromPlan = Record.Exception(() => plan.BackCommand.Execute(null));
        Assert.Null(backFromPlan);
        Assert.Same(workspace, shell.CurrentStep);

        shell.Dispose();
    }

    // === Logger resilience (ADR-037 WP2, #241): the renderer's new log call sites ==============

    /// <summary>
    /// Wires an <see cref="ILoggerFactory"/> whose <see cref="ILogger.Log{TState}"/> ALWAYS throws
    /// into the REAL <see cref="CytoscapeGraphRenderer"/> (never <see cref="FakeGraphRenderer"/>,
    /// which has no logging surface at all) and drives the inbound bridge-message router
    /// (the internal <c>HandleMessage</c> test seam, ADR-037 WP2 — reachable without a live WebView
    /// per its own doc comment). A jsError message is used so the driven path both logs
    /// (<c>JsErrorReported</c>) AND raises <see cref="IGraphRenderer.RendererError"/>.
    ///
    /// <para><b>Result, pinned:</b> the call site does NOT wrap its <see cref="ILogger"/> calls —
    /// a misbehaving logger's exception propagates verbatim, verified empirically (WP2
    /// test-engineer finding). This mirrors the codebase's established single-point-of-defense
    /// architecture (ADR-037 D3): "never-throw" is a property OF <c>FileLogSink</c> itself (every
    /// one of its public methods is try/catch-wrapped), not a defensive wrapper every call site
    /// must repeat — production only ever installs <c>FileLogSink</c> or
    /// <c>NullLoggerFactory.Instance</c> (<see cref="GroupWeaver.App.Diagnostics.AppLog"/>'s
    /// default), both contractually never-throw, so this scenario cannot occur in practice. This
    /// test PINS the current, deliberately-chosen behavior rather than asserting an aspirational
    /// "always swallowed" contract that would fail against the shipped architecture — reported to
    /// the orchestrator/reviewer to explicitly RATIFY (accept the sink-scoped boundary, as pinned
    /// here) or HARDEN (wrap the call sites) as a conscious decision, not a silent gap.</para>
    /// </summary>
    [Fact]
    public void ThrowingLogger_PropagatesFromHandleMessage_TheSinkIsTheOnlyGuaranteedNeverThrowPoint()
    {
        var renderer = new CytoscapeGraphRenderer(new ThrowingLoggerFactory());

        var ex = Record.Exception(
            () => renderer.HandleMessage("""{"type":"jsError","source":"s","message":"m"}"""));

        Assert.IsType<InvalidOperationException>(ex);
        Assert.Equal(ThrowingLoggerFactory.FailureMessage, ex!.Message);
    }

    /// <summary>
    /// The single-flight guard's <c>SingleFlightViolation</c> log runs BEFORE its own intentional
    /// <see cref="InvalidOperationException"/> (log-then-throw, ADR-037 D5). With a throwing
    /// logger, the LOGGING failure is what escapes — MASKING the intended re-entrancy exception
    /// (same exception TYPE, but the wrong message: a caller pattern-matching on the "while
    /// another renderer call is in flight" text would misdiagnose a logger bug as a re-entrancy
    /// bug). Pinned as the sharper half of the same finding above.
    /// </summary>
    [Fact]
    public void ThrowingLogger_PropagatesFromEnterSingleFlight_MaskingItsOwnIntentionalException()
    {
        var renderer = new CytoscapeGraphRenderer(new ThrowingLoggerFactory());
        renderer.EnterSingleFlight("first"); // no log yet — succeeds

        var ex = Record.Exception(() => renderer.EnterSingleFlight("second"));

        Assert.IsType<InvalidOperationException>(ex);
        // The LOGGER's exception, not EnterSingleFlight's own ("... while another renderer call
        // is in flight ..." never appears) — the masking this finding is about.
        Assert.Equal(ThrowingLoggerFactory.FailureMessage, ex!.Message);
    }

    /// <summary>An <see cref="ILoggerFactory"/>/<see cref="ILogger"/> pair whose every
    /// <see cref="ILogger.Log{TState}"/> call throws — an adversarial stand-in for "the installed
    /// logger violates the never-throw contract" (never true for the shipped
    /// <c>FileLogSink</c>/<c>NullLoggerFactory</c>, ADR-037 D3).</summary>
    private sealed class ThrowingLoggerFactory : ILoggerFactory
    {
        public const string FailureMessage =
            "test-injected logger failure (never happens with FileLogSink/NullLoggerFactory)";

        public ILogger CreateLogger(string categoryName) => new ThrowingLogger();

        public void AddProvider(ILoggerProvider provider)
        {
        }

        public void Dispose()
        {
        }

        private sealed class ThrowingLogger : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter) =>
                throw new InvalidOperationException(FailureMessage);
        }
    }

    // === helpers =============================================================================

    private static AdObject Obj(string name, string dn, AdObjectKind kind = AdObjectKind.GlobalGroup) =>
        new() { Dn = dn, Kind = kind, Name = name };

    /// <summary>A stub whose scope load yields an empty snapshot (a loaded, renderer-pushed scope).</summary>
    private static StubDirectoryProvider EmptyProvider() =>
        new(Task.FromResult(new DirectoryConnection("stub directory", 0)))
        {
            LoadScopeResult = Task.FromResult(new DirectorySnapshot()),
        };

    /// <summary>A stub whose scope load yields a non-empty snapshot, so
    /// <see cref="WorkspaceViewModel.ApplyRulesetAsync"/> actually reaches the renderer push.</summary>
    private static StubDirectoryProvider NonEmptyProvider()
    {
        var snapshot = new DirectorySnapshot();
        snapshot.AddObject(Obj("Lab", RootDn, AdObjectKind.OrganizationalUnit));
        snapshot.AddObject(Obj("GG_Sales", "CN=GG_Sales,OU=Lab,DC=stub,DC=lab"));
        snapshot.SetMembers(RootDn, ["CN=GG_Sales,OU=Lab,DC=stub,DC=lab"]);
        return new StubDirectoryProvider(Task.FromResult(new DirectoryConnection("stub directory", 1)))
        {
            LoadScopeResult = Task.FromResult(snapshot),
        };
    }

    private static WorkspaceViewModel Workspace(
        StubDirectoryProvider provider, Func<IGraphRenderer> rendererFactory) =>
        new(
            provider,
            Obj("Lab", RootDn, AdObjectKind.OrganizationalUnit),
            new DirectoryConnection("stub directory", 0),
            webView2Missing: false,
            graphRendererFactory: rendererFactory);

    /// <summary>Connect (demo) → pick the demo root OU → load, settling the workspace.
    /// <paramref name="swallowInitFault"/> swallows a faulted ctor-load push (when the injected
    /// ShowGraphResult is faulted) so a test can proceed to the steps it actually exercises.</summary>
    private static async Task<WorkspaceViewModel> DriveDemoWorkspaceAsync(
        ShellViewModel shell, bool swallowInitFault = false)
    {
        var connect = Assert.IsType<ConnectionViewModel>(shell.CurrentStep);
        await connect.ConnectDemoCommand.ExecuteAsync(null);
        var picker = Assert.IsType<RootPickerViewModel>(shell.CurrentStep);
        await picker.LoadCandidates;
        picker.SelectedCandidate = picker.Candidates.First(c => c.Kind == AdObjectKind.OrganizationalUnit);
        picker.LoadRootCommand.Execute(null);
        var workspace = Assert.IsType<WorkspaceViewModel>(shell.CurrentStep);
        if (swallowInitFault)
        {
            await Record.ExceptionAsync(async () => await workspace.Initialization);
        }
        else
        {
            await workspace.Initialization;
        }

        return workspace;
    }
}
