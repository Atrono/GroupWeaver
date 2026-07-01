using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;

using GroupWeaver.App.Graph;
using GroupWeaver.App.Rules;
using GroupWeaver.App.Settings;
using GroupWeaver.App.Startup;
using GroupWeaver.App.Tests.Fakes;
using GroupWeaver.App.ViewModels;
using GroupWeaver.App.Views;
using GroupWeaver.Core.Graph;
using GroupWeaver.Core.Model;
using GroupWeaver.Core.Providers;
using GroupWeaver.Core.Rules;
using GroupWeaver.Providers;

using Xunit;

namespace GroupWeaver.App.Tests.Settings;

/// <summary>
/// Pins the AP 3.3 / S8 shell integration (ADR-011 §1/§3, the spec's "Final shell
/// integration" + "Final slice plan" S8): the net-new "Settings" affordance, the
/// re-thread of an Apply/Save into the LIVE <see cref="WorkspaceViewModel"/>, and the
/// no-rebuild / viewport-preserving / IsLoading-gated contract of
/// <see cref="WorkspaceViewModel.ApplyRulesetAsync"/>.
///
/// <para><b>Why the production <c>OpenSettings</c> path is NOT awaited here (ADR-011
/// open-risk #3, audit finding #1).</b> Production <c>OpenSettingsAsync</c> ends in
/// <c>await window.ShowDialog(MainWindow)</c> — a modal that only completes when the
/// dialog closes and needs a dispatcher loop the headless theory does not pump; awaiting
/// the command would hang. So this fixture drives the SAME re-thread the dialog path
/// drives, minus the window-show: it builds the settings VM through the shell's own
/// seam (<c>BuildSettingsViewModel()</c> — the seam <c>OpenSettingsAsync</c> uses
/// internally: seed from the shell's cached <see cref="EffectiveRuleset"/> + the injected
/// <see cref="RulesetLocator"/>, subscribe the shell's re-thread to
/// <see cref="SettingsViewModel.RulesetApplied"/>), edits the mirror, and raises the
/// applied event by calling <see cref="SettingsViewModel.Save"/>/<c>Apply</c>. The
/// window-show itself is <c>[I]</c>; all gateable logic lives in the VMs.</para>
///
/// <para><b>The flipped-cell lever (the spec's concrete example, ADR-008 docs §"Not
/// detectable in v1": "GG←GG nesting (GG_IT_Admins←GG_IT_Backup) is allow by default,
/// one cell flip flags it").</b> The default ruleset's GG←GG matrix cell is
/// <see cref="CellChoice.Allow"/>, so the demo's <c>GG_IT_Admins ← GG_IT_Backup</c>
/// membership edge produces NO nesting finding. Flipping that one cell to
/// <see cref="CellChoice.Error"/> in the mirror, then Apply/Save, must re-Evaluate the
/// already-loaded snapshot and surface exactly that edge as a new nesting error —
/// proving the re-thread reaches the live engine. The full demo scope is used (the real
/// <see cref="DemoProvider"/>, the AP 3.2 19-finding baseline authority), carrying the
/// GG_Circle_A ↔ GG_Circle_B cycle: every Evaluate over it must terminate.</para>
///
/// <para><b>RED until S8</b> adds <see cref="ShellViewModel.OpenSettingsCommand"/> + the
/// shell's settings-VM seam + the settable <c>_ruleset</c> + the
/// <c>RulesetLocator locator</c> ctor arg, <see cref="WorkspaceViewModel.ApplyRulesetAsync"/>
/// (+ a settable <c>_ruleset</c>), and the MainWindow top command strip's Settings button.
/// The back-compat pins (new ctor args defaulted) stay green throughout.</para>
/// </summary>
public sealed class SettingsShellIntegrationTests
{
    // --- demo scope (the 19-finding baseline authority + the GG←GG flip lever) ----------

    private const string DemoRootDn = "OU=AGDLP-Demo,DC=weavedemo,DC=example";
    private const string GroupSuffix = ",OU=Groups,OU=AGDLP-Demo,DC=weavedemo,DC=example";

    /// <summary>The GG←GG edge parent (nesting Dns[0] = the finding anchor once flipped).</summary>
    private const string GgItAdminsDn = "CN=GG_IT_Admins" + GroupSuffix;

    /// <summary>The GG←GG edge member (nesting Dns[1]).</summary>
    private const string GgItBackupDn = "CN=GG_IT_Backup" + GroupSuffix;

    // === (a) the Settings affordance is reachable from the shell ========================

    /// <summary>
    /// The shell exposes a reachable <c>OpenSettings</c> command — the net-new top
    /// command strip's affordance (spec "Final shell integration"). It is reachable on
    /// every step (here: the Connect step, the shell's entry state) and armed.
    /// </summary>
    [Fact]
    public void OpenSettingsCommand_IsReachableFromTheShell_OnEveryStep()
    {
        var shell = Shell(new DemoProvider());

        Assert.IsType<ConnectionViewModel>(shell.CurrentStep);
        Assert.True(
            shell.OpenSettingsCommand.CanExecute(null),
            "the Settings command must be reachable from the shell's entry (Connect) step");
    }

    /// <summary>
    /// Discoverability slice (feat/discoverability): the shell exposes a reachable
    /// <see cref="ShellViewModel.OpenKeyboardHelpCommand"/> — the top command strip's "?"
    /// affordance, mirroring <see cref="ShellViewModel.OpenSettingsCommand"/>. Reachable
    /// on every step (here: the Connect entry state) and armed. Like OpenSettings the
    /// production path ends in a headless-hostile <c>ShowDialog</c>, so this pins the
    /// COMMAND exists + <c>CanExecute</c>, never the modal loop (ADR-011 open-risk #3).
    /// </summary>
    [Fact]
    public void OpenKeyboardHelpCommand_IsReachableFromTheShell_OnEveryStep()
    {
        var shell = Shell(new DemoProvider());

        Assert.IsType<ConnectionViewModel>(shell.CurrentStep);
        Assert.True(
            shell.OpenKeyboardHelpCommand.CanExecute(null),
            "the keyboard-help command must be reachable from the shell's entry (Connect) step");
    }

    /// <summary>
    /// The workspace-demo re-judge (spec "ui-checklist additions" row
    /// <c>[S:workspace-demo]</c>): the shell's top command strip renders a "Settings"
    /// button bound to <see cref="ShellViewModel.OpenSettingsCommand"/>, below the
    /// WebView2 banner and above the step content — present and reachable on the live
    /// workspace step. (The airspace/over-GraphHost judgment is the ui-verifier's PNG
    /// job; this pins the button exists, carries the command, and is enabled.)
    /// </summary>
    [AvaloniaFact]
    public async Task MainWindow_TopCommandStrip_ShowsAReachableSettingsButton_OnTheWorkspaceStep()
    {
        var (window, shell, _) = await DriveDemoShellToWorkspaceAsync();

        var settingsButton = Assert.Single(
            window.GetVisualDescendants().OfType<Button>(),
            b => VisibleTexts(b).Any(t => t?.Contains("Settings", StringComparison.OrdinalIgnoreCase) == true));

        Assert.True(settingsButton.IsEffectivelyVisible, "the Settings button must be rendered");
        Assert.True(settingsButton.IsEffectivelyEnabled, "the Settings button must be enabled");
        Assert.Same(shell.OpenSettingsCommand, settingsButton.Command);

        window.Close();
    }

    /// <summary>
    /// Discoverability slice (feat/discoverability): the top command strip renders the new
    /// "?" keyboard-help button (left of Settings), bound to
    /// <see cref="ShellViewModel.OpenKeyboardHelpCommand"/>, present + reachable on the live
    /// workspace step — the exact analogue of the Settings-button pin above (deliberately
    /// extending the strip's button set with the new affordance). Located by its COMMAND
    /// binding (robust against the bare "?" glyph appearing elsewhere), then its glyph is
    /// confirmed. The <c>ShowDialog</c> modal itself is <c>[I]</c> — this pins the button,
    /// its command, and that it is enabled, never the dialog loop.
    /// </summary>
    [AvaloniaFact]
    public async Task MainWindow_TopCommandStrip_ShowsAReachableKeyboardHelpButton_OnTheWorkspaceStep()
    {
        var (window, shell, _) = await DriveDemoShellToWorkspaceAsync();

        var helpButton = Assert.Single(
            window.GetVisualDescendants().OfType<Button>(),
            b => b.Command == shell.OpenKeyboardHelpCommand);

        Assert.True(helpButton.IsEffectivelyVisible, "the keyboard-help button must be rendered");
        Assert.True(helpButton.IsEffectivelyEnabled, "the keyboard-help button must be enabled");
        // The affordance is the quiet "?" glyph (left of Settings).
        Assert.Contains(VisibleTexts(helpButton), t => t == "?");

        window.Close();
    }

    // === (b) a Save in settings re-threads the LIVE workspace (no rebuild) ==============

    /// <summary>
    /// THE S8 contract: a Save in the settings editor re-threads into the live
    /// <see cref="WorkspaceViewModel"/> so its <see cref="WorkspaceViewModel.Report"/>
    /// and <see cref="WorkspaceViewModel.Violations"/> CHANGE under the flipped ruleset —
    /// flipping the GG←GG matrix cell to Error makes the demo's GG_IT_Admins ←
    /// GG_IT_Backup edge a brand-new nesting error finding. The re-thread runs over the
    /// ALREADY-LOADED snapshot: NO <c>GraphBuilder.Build</c> (the
    /// <see cref="WorkspaceViewModel.Graph"/> reference is unchanged) and it rides
    /// <c>UpdateGraphAsync</c> (replace-in-place, viewport-preserving), never
    /// <c>ShowGraphAsync</c>.
    /// </summary>
    [Fact(Timeout = 60_000)]
    public async Task SettingsSave_ReThreadsTheLiveWorkspace_FlippedGgGgCell_AddsTheNestingError_NoRebuild()
    {
        var (shell, workspace, fake) = await DemoShellWithLiveWorkspaceAsync();

        // Baseline: the default ruleset allows GG←GG, so the GG_IT_Admins←GG_IT_Backup
        // edge is NOT a finding (and the load showed the graph exactly once).
        Assert.DoesNotContain(workspace.Report.Violations, IsGgGgNestingError);
        var graphBefore = workspace.Graph;
        Assert.NotNull(graphBefore);
        Assert.Single(fake.ShownGraphs);
        Assert.Empty(fake.UpdatedGraphs);

        // Open settings through the shell's seam (the dialog path, minus ShowDialog),
        // flip the one GG←GG cell to Error, and Save — which re-threads via RulesetApplied.
        var settings = shell.BuildSettingsViewModel();
        settings.Nesting.Cell(AdObjectKind.GlobalGroup, AdObjectKind.GlobalGroup).Choice = CellChoice.Error;
        Assert.True(settings.Save(), "the flipped-cell mirror is valid and must Save");

        // The shell re-threaded the live workspace: the GG←GG edge is now a nesting error,
        // anchored at the parent, with the member as the second endpoint (rule-engine.md
        // nesting Dns invariant = [parent, member]).
        var finding = Assert.Single(workspace.Report.Violations, IsGgGgNestingError);
        Assert.Equal(GgItAdminsDn, finding.Dns[0], Dn.Comparer);
        Assert.Equal(GgItBackupDn, finding.Dns[1], Dn.Comparer);

        // The sidebar re-projected from the fresh report — the new finding is a row.
        Assert.Contains(
            workspace.Violations,
            r => r.Severity == RuleSeverity.Error && Dn.Comparer.Equals(r.PrimaryDn, GgItAdminsDn));

        // NO rebuild: the Graph reference is the very same instance (GraphBuilder.Build
        // was never called — ruleset-only change, ADR-009/ADR-011 §3).
        Assert.Same(graphBefore, workspace.Graph);

        // Viewport-preserving: the fresh report rode a replace-in-place UpdateGraphAsync,
        // NEVER a second ShowGraphAsync (which would destroy + fit, losing the viewport).
        Assert.Single(fake.ShownGraphs); // still exactly the one load-time show
        var updated = Assert.Single(fake.UpdatedGraphs);
        Assert.Same(workspace.Graph, updated);
        var updatedReport = Assert.Single(fake.UpdatedReports);
        Assert.Same(workspace.Report, updatedReport);

        shell.Dispose();
    }

    /// <summary>
    /// An Apply (live-only, no disk write) re-threads exactly like a Save for the live
    /// workspace: same flipped-cell finding, same no-rebuild / UpdateGraphAsync path.
    /// (Apply vs Save differ only in persistence — ADR-011 §3 — which is
    /// <see cref="SettingsValidationTests"/>' domain, not the live-thread's.)
    /// </summary>
    [Fact(Timeout = 60_000)]
    public async Task SettingsApply_ReThreadsTheLiveWorkspace_LikeSave_ButThisFixtureCoversTheLiveHalf()
    {
        var (shell, workspace, fake) = await DemoShellWithLiveWorkspaceAsync();
        Assert.DoesNotContain(workspace.Report.Violations, IsGgGgNestingError);
        var graphBefore = workspace.Graph;

        var settings = shell.BuildSettingsViewModel();
        settings.Nesting.Cell(AdObjectKind.GlobalGroup, AdObjectKind.GlobalGroup).Choice = CellChoice.Error;
        Assert.True(settings.Apply(), "the flipped-cell mirror is valid and must Apply");

        Assert.Contains(workspace.Report.Violations, IsGgGgNestingError);
        Assert.Same(graphBefore, workspace.Graph); // no rebuild
        Assert.Single(fake.UpdatedGraphs); // replace-in-place, not a re-show
        Assert.Single(fake.ShownGraphs);

        shell.Dispose();
    }

    /// <summary>
    /// The shell re-caches the applied ruleset so a workspace created AFTER the apply
    /// (e.g. a future re-pick) inherits it through the existing Shell→RootPicker→Workspace
    /// thread (spec "Re-threading" §"When no live workspace exists"). Applying with NO
    /// live workspace (the shell still on Connect) must not throw and must re-thread the
    /// NEXT workspace: the flipped GG←GG cell shows up in a freshly driven workspace's
    /// report without any further settings interaction.
    /// </summary>
    [Fact(Timeout = 60_000)]
    public async Task SettingsApply_WithNoLiveWorkspace_ReCachesRuleset_NextWorkspaceInheritsIt()
    {
        var fake = new FakeGraphRenderer();
        var shell = Shell(new DemoProvider(), () => fake);

        // No workspace yet — still on Connect. Apply must be a safe re-cache, never a throw.
        var settings = shell.BuildSettingsViewModel();
        settings.Nesting.Cell(AdObjectKind.GlobalGroup, AdObjectKind.GlobalGroup).Choice = CellChoice.Error;
        var ex = Record.Exception(() => { bool applied = settings.Apply(); Assert.True(applied); });
        Assert.Null(ex);

        // Now drive into a workspace: it must inherit the re-cached (flipped) ruleset.
        var workspace = await DriveShellToWorkspaceAsync(shell);

        Assert.Contains(workspace.Report.Violations, IsGgGgNestingError);

        shell.Dispose();
    }

    // === (c) ApplyRulesetAsync — the no-rebuild / viewport / IsLoading contract =========

    /// <summary>
    /// <see cref="WorkspaceViewModel.ApplyRulesetAsync"/> over a settled workspace
    /// re-Evaluates the existing snapshot and pushes the fresh report through
    /// <c>UpdateGraphAsync</c> (replace-in-place) — never <c>GraphBuilder.Build</c>
    /// (Graph reference unchanged), never <c>ShowGraphAsync</c>. The viewport-preserving
    /// path is the ExpandAsync post-fetch machinery minus the topology rebuild.
    /// </summary>
    [Fact(Timeout = 60_000)]
    public async Task ApplyRulesetAsync_ReEvaluates_OverTheSameGraph_ViaUpdateNotShow()
    {
        var (vm, fake) = await DemoWorkspaceAsync();
        Assert.DoesNotContain(vm.Report.Violations, IsGgGgNestingError);
        var graphBefore = vm.Graph;
        var reportBefore = vm.Report;

        await vm.ApplyRulesetAsync(FlippedGgGgRuleset());

        // Re-Evaluated: a NEW report instance carrying the GG←GG nesting error.
        Assert.NotSame(reportBefore, vm.Report);
        Assert.Contains(vm.Report.Violations, IsGgGgNestingError);

        // No rebuild, viewport kept: same Graph instance, exactly one UpdateGraphAsync
        // carrying it + the fresh report, and still only the one load-time ShowGraphAsync.
        Assert.Same(graphBefore, vm.Graph);
        Assert.Single(fake.ShownGraphs);
        var updated = Assert.Single(fake.UpdatedGraphs);
        Assert.Same(vm.Graph, updated);
        Assert.Same(vm.Report, Assert.Single(fake.UpdatedReports));

        vm.Dispose();
    }

    /// <summary>
    /// An Apply DURING a faked in-flight load respects the one global busy gate
    /// (ADR-005 D3): it ONLY sets the field and returns — no concurrent
    /// <c>UpdateGraphAsync</c>, no torn render. Because <c>RuleEngine.Evaluate</c>
    /// re-reads the now-settable <c>_ruleset</c> at call time (rule-engine.md), the
    /// in-flight pipeline's OWN Evaluate (when the load is released) picks up the new
    /// ruleset — so the flipped GG←GG error appears via the load's own ShowGraphAsync,
    /// never a second update racing it.
    /// </summary>
    [Fact(Timeout = 60_000)]
    public async Task ApplyRulesetAsync_DuringInFlightLoad_OnlySetsField_NoConcurrentUpdate_PipelineRereads()
    {
        // Gate the demo scope load so the workspace stays IsLoading while we Apply.
        var demo = new DemoProvider();
        var realSnapshot = await demo.LoadScopeAsync(DemoRootDn);
        var root = await demo.GetObjectAsync(DemoRootDn);
        Assert.NotNull(root);

        var loadGate = new TaskCompletionSource<DirectorySnapshot>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var provider = new StubDirectoryProvider(Task.FromResult(await demo.ConnectAsync()))
        {
            LoadScopeResult = loadGate.Task,
        };
        var fake = new FakeGraphRenderer();
        var vm = new WorkspaceViewModel(
            provider, root!, await demo.ConnectAsync(),
            webView2Missing: false, () => fake);

        Assert.True(vm.IsLoading, "the gated load must hold the busy gate");

        // Apply while the load is in flight: it must ONLY set the field — no Evaluate-and-
        // push of its own (no UpdateGraphAsync), because IsLoading is held.
        await vm.ApplyRulesetAsync(FlippedGgGgRuleset());

        Assert.Empty(fake.UpdatedGraphs); // no concurrent replace-in-place
        Assert.Empty(fake.ShownGraphs); // the load hasn't shown yet either

        // Release the load: the in-flight pipeline's OWN Evaluate re-reads the settable
        // field, so the flipped GG←GG error lands via the load's ShowGraphAsync — exactly
        // one show, no extra update racing it.
        loadGate.SetResult(realSnapshot);
        await vm.Initialization;

        Assert.Single(fake.ShownGraphs);
        Assert.Empty(fake.UpdatedGraphs); // the Apply never spawned its own push
        Assert.Contains(vm.Report.Violations, IsGgGgNestingError);
        var shownReport = Assert.Single(fake.ShownReports);
        Assert.Same(vm.Report, shownReport);

        vm.Dispose();
    }

    /// <summary>
    /// Apply before any snapshot exists (no load has settled) only sets the field and
    /// pushes nothing — a clean no-op on the renderer (Snapshot is null guard).
    /// </summary>
    [Fact]
    public async Task ApplyRulesetAsync_BeforeAnySnapshot_OnlySetsField_PushesNothing()
    {
        var loadGate = new TaskCompletionSource<DirectorySnapshot>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var provider = StubProvider();
        provider.LoadScopeResult = loadGate.Task;
        var fake = new FakeGraphRenderer();
        var vm = Workspace(provider, () => fake);

        Assert.Null(vm.Snapshot);

        await vm.ApplyRulesetAsync(FlippedGgGgRuleset());

        Assert.Empty(fake.ShownGraphs);
        Assert.Empty(fake.UpdatedGraphs);

        loadGate.SetResult(new DirectorySnapshot());
        await vm.Initialization;
        vm.Dispose();
    }

    // === (d) back-compat: existing Shell/Workspace ctor shapes still compile ============

    /// <summary>
    /// The new ctor args (the shell's <c>RulesetLocator locator</c>, the workspace's
    /// settable <c>_ruleset</c>) are OPTIONAL and DEFAULTED, so every pre-S8 Shell/Workspace
    /// test constructs unchanged (spec S8 DoD: "existing Shell/Workspace tests still pass
    /// with the new optional ctor args defaulted"). These constructions mirror the exact
    /// shapes WorkspaceShellTests / WorkspaceLoadTests use — they must still compile and
    /// behave identically.
    /// </summary>
    [Fact]
    public void ExistingCtorShapes_StillCompile_WithTheNewArgsDefaulted()
    {
        // The 3-arg shell shape (WorkspaceShellTests): provider factory + options + webview status.
        using var shell = new ShellViewModel(
            _ => new DemoProvider(),
            new StartupOptions(Demo: false),
            new WebView2RuntimeStatus(IsInstalled: true, Version: "test"));
        Assert.NotNull(shell.CurrentStep);

        // The 4-/5-arg workspace shape (WorkspaceLoadTests): no ruleset arg, no locator.
        var provider = StubProvider();
        using var vm = new WorkspaceViewModel(
            provider,
            new AdObject { Dn = "OU=Lab,DC=stub,DC=lab", Kind = AdObjectKind.OrganizationalUnit, Name = "Lab" },
            new DirectoryConnection("stub directory", 5),
            webView2Missing: false,
            graphRendererFactory: () => new FakeGraphRenderer());
        Assert.NotNull(vm);
    }

    // === helpers ========================================================================

    private static bool IsGgGgNestingError(RuleViolation v) =>
        v.RuleId == RuleIds.Nesting
        && v.Severity == RuleSeverity.Error
        && Dn.Comparer.Equals(v.Dns[0], GgItAdminsDn)
        && v.Dns.Count == 2
        && Dn.Comparer.Equals(v.Dns[1], GgItBackupDn);

    /// <summary>The embedded default with ONLY the GG←GG matrix cell flipped to Error —
    /// the same one-cell lever the settings mirror produces, built straight from the
    /// records for the <see cref="WorkspaceViewModel.ApplyRulesetAsync"/> direct tests
    /// (no mirror round-trip needed to prove the VM contract).</summary>
    private static Ruleset FlippedGgGgRuleset()
    {
        var d = RulesetLoader.LoadDefault();
        var matrix = d.Nesting.Matrix.ToDictionary(
            row => row.Key,
            row => (IReadOnlyDictionary<AdObjectKind, NestingCell>)new Dictionary<AdObjectKind, NestingCell>(row.Value),
            EqualityComparer<AdObjectKind>.Default);

        var ggRow = new Dictionary<AdObjectKind, NestingCell>(matrix[AdObjectKind.GlobalGroup])
        {
            [AdObjectKind.GlobalGroup] = new NestingCell(false, RuleSeverity.Error),
        };
        matrix[AdObjectKind.GlobalGroup] = ggRow;

        return d with { Nesting = d.Nesting with { Matrix = matrix } };
    }

    /// <summary>A shell over a real or stub provider with the WebView2 probe forced
    /// present (never the live registry — that would make the banner machine-dependent)
    /// and a temp-dir <see cref="RulesetLocator"/> seam (never real %APPDATA% from a
    /// test). The locator is the net-new 6th ctor arg S8 adds.</summary>
    private static ShellViewModel Shell(IDirectoryProvider provider) =>
        Shell(provider, graphRendererFactory: null);

    private static ShellViewModel Shell(
        IDirectoryProvider provider, Func<IGraphRenderer>? graphRendererFactory)
    {
        var locator = new RulesetLocator(
            Directory.CreateTempSubdirectory("groupweaver-settings-shell-tests-").FullName);
        return new ShellViewModel(
            _ => provider,
            new StartupOptions(Demo: false),
            new WebView2RuntimeStatus(IsInstalled: true, Version: "test"),
            graphRendererFactory,
            locator.LoadEffective(),
            locator);
    }

    /// <summary>Drives a demo shell through Connect → PickRoot → Workspace to the settled
    /// workspace step, returning the window + shell + the settled workspace VM.</summary>
    private static async Task<(Window Window, ShellViewModel Shell, WorkspaceViewModel Workspace)>
        DriveDemoShellToWorkspaceAsync()
    {
        var fake = new FakeGraphRenderer { View = new Border() };
        var shell = Shell(new DemoProvider(), () => fake);
        var window = new MainWindow { DataContext = shell, Width = 1280, Height = 720 };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var workspace = await DriveShellToWorkspaceAsync(shell);
        Dispatcher.UIThread.RunJobs();
        return (window, shell, workspace);
    }

    /// <summary>Connect → pick the demo root OU → load, awaiting the settled workspace.</summary>
    private static async Task<WorkspaceViewModel> DriveShellToWorkspaceAsync(ShellViewModel shell)
    {
        var connect = Assert.IsType<ConnectionViewModel>(shell.CurrentStep);
        await connect.ConnectDemoCommand.ExecuteAsync(null);
        var picker = Assert.IsType<RootPickerViewModel>(shell.CurrentStep);
        await picker.LoadCandidates;
        picker.SelectedCandidate = picker.Candidates.First(c => Dn.Comparer.Equals(c.Dn, DemoRootDn));
        picker.LoadRootCommand.Execute(null);
        var workspace = Assert.IsType<WorkspaceViewModel>(shell.CurrentStep);
        await workspace.Initialization;
        return workspace;
    }

    /// <summary>A demo shell driven to a live workspace, with the renderer fake returned
    /// for the re-thread assertions (the same fake the workspace pushes through).</summary>
    private static async Task<(ShellViewModel Shell, WorkspaceViewModel Workspace, FakeGraphRenderer Fake)>
        DemoShellWithLiveWorkspaceAsync()
    {
        var fake = new FakeGraphRenderer();
        var shell = Shell(new DemoProvider(), () => fake);
        var workspace = await DriveShellToWorkspaceAsync(shell);
        return (shell, workspace, fake);
    }

    /// <summary>A workspace over the REAL <see cref="DemoProvider"/> rooted at the demo OU
    /// (the full 19-finding scope), Initialization awaited — the same shape
    /// <see cref="WorkspaceViolationsTests"/> uses, for the ApplyRulesetAsync direct tests.</summary>
    private static async Task<(WorkspaceViewModel Vm, FakeGraphRenderer Fake)> DemoWorkspaceAsync()
    {
        var provider = new DemoProvider();
        var root = await provider.GetObjectAsync(DemoRootDn);
        Assert.NotNull(root);
        var fake = new FakeGraphRenderer();
        var vm = new WorkspaceViewModel(
            provider, root!, await provider.ConnectAsync(),
            webView2Missing: false, () => fake);
        await vm.Initialization;
        return (vm, fake);
    }

    private static StubDirectoryProvider StubProvider() =>
        new(Task.FromResult(new DirectoryConnection("stub directory", 5)))
        {
            LoadScopeResult = Task.FromResult(new DirectorySnapshot()),
        };

    private static WorkspaceViewModel Workspace(
        StubDirectoryProvider provider, Func<IGraphRenderer> rendererFactory) =>
        new(
            provider,
            new AdObject { Dn = "OU=Lab,DC=stub,DC=lab", Kind = AdObjectKind.OrganizationalUnit, Name = "Lab" },
            new DirectoryConnection("stub directory", 5),
            webView2Missing: false,
            rendererFactory);

    private static List<string?> VisibleTexts(Visual scope) =>
        scope.GetVisualDescendants()
            .OfType<TextBlock>()
            .Where(t => t.IsEffectivelyVisible)
            .Select(t => t.Text)
            .ToList();
}
