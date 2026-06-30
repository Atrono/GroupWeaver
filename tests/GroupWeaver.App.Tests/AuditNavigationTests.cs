using System;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;

using Avalonia.Headless.XUnit;
using Avalonia.Threading;

using GroupWeaver.App.Settings;
using GroupWeaver.App.Startup;
using GroupWeaver.App.Tests.Fakes;
using GroupWeaver.App.ViewModels;
using GroupWeaver.App.Views;
using GroupWeaver.Core.Model;
using GroupWeaver.Core.Providers;
using GroupWeaver.Core.Rules;
using GroupWeaver.Providers;

using Xunit;

namespace GroupWeaver.App.Tests;

/// <summary>
/// Pins the WP5b (#152) Audit-step navigation seam on branch <c>feat/audit-step</c>: the
/// Workspace↔Audit round-trip through the REAL <see cref="ShellViewModel"/> (mirroring
/// <see cref="ParkingLotBackNavigationTests"/>/<see cref="BackNavigationStepSwapTests"/>), the
/// snapshot-null gate + <see cref="WorkspaceViewModel.AuditCommand"/> CanExecute re-arming, and the
/// <see cref="AuditViewModel"/> summary projection + <see cref="AuditViewModel.ApplyRuleset"/>
/// recompute.
///
/// <para><b>Test-isolation seam (lab-environment / the #124 lesson).</b> The
/// <see cref="ShellViewModel"/> ctor READS (and the workspace WRITES) <c>ui-state.json</c>, so the
/// integration helper injects a <see cref="System.IO.Directory.CreateTempSubdirectory(string)"/>
/// -backed <see cref="UiStateStore"/> — nothing ever touches real <c>%APPDATA%</c> (a
/// <c>RailCollapsed:true</c> persisted on this box would otherwise collapse the right rail to width
/// 0 and starve view realization).</para>
///
/// <para>The integration tests drive the SAME demo root OU scope the back-nav tests use (it carries
/// the seeded GG_Circle_A↔GG_Circle_B cycle), and assert on the documented contracts: reference
/// equality of <see cref="ShellViewModel.CurrentStep"/> on Back, the audit step's
/// <see cref="AuditViewModel.IsDisposed"/>/untracked state, and no double-dispose at shell teardown.
/// The summary/recompute pins (#3/#4) are UNIT-level over a directly-constructed
/// <see cref="AuditViewModel"/> (instruction #5 — cleaner than threading a settings re-apply through
/// the whole shell): a hand-built loaded snapshot + report + ruleset, comparing the VM's projected
/// props to <see cref="AuditSummary.Compute"/> over the same inputs (never hardcoding the demo's
/// Score 55 — the value is computed from the same inputs the VM uses, so the test cannot drift from
/// the engine).</para>
/// </summary>
public sealed class AuditNavigationTests
{
    /// <summary>WebView2 forced present (never the live registry) so the shell behaves machine-
    /// independently; the Audit step owns no renderer, but the workspace/plan path still probes it.</summary>
    private static readonly WebView2RuntimeStatus Present = new(IsInstalled: true, Version: "test");

    // === (1) Workspace → Audit → Back: SAME workspace instance, audit disposed + untracked =====

    /// <summary>
    /// Workspace→Audit→Back returns the SAME workspace instance (reference equality on
    /// <see cref="ShellViewModel.CurrentStep"/>) — the live Ist load/viewport survive — and the
    /// abandoned audit step is disposed (<see cref="AuditViewModel.IsDisposed"/>) AND untracked, so
    /// the shell's teardown does not double-dispose it. Proven by calling <see cref="ShellViewModel.Dispose"/>
    /// after Back and asserting it does not throw and the audit stays disposed (idempotent).
    /// </summary>
    [AvaloniaFact(Timeout = 60_000)]
    public async Task WorkspaceAuditBack_ReturnsTheSameWorkspaceInstance_AndDisposesUntracksTheAudit()
    {
        var (window, shell) = ShowShell();
        var workspace = await DriveToWorkspaceAsync(shell);
        Assert.NotNull(workspace.Snapshot); // OnAudit gates on a loaded Ist

        // Into Audit: a fresh AuditViewModel becomes the current step (no renderer, table view).
        shell.OnAudit(workspace);
        var audit = Assert.IsType<AuditViewModel>(shell.CurrentStep);
        Assert.False(audit.IsDisposed); // alive while current

        // Back: the SAME workspace instance is restored, and the audit step is disposed.
        audit.BackCommand.Execute(null);
        Assert.Same(workspace, Assert.IsType<WorkspaceViewModel>(shell.CurrentStep));
        Assert.True(audit.IsDisposed, "Back from Audit must dispose the abandoned audit step");

        // Untracked: the shell's teardown must NOT double-dispose it (and must not throw). The audit
        // stays disposed (idempotent). The workspace survivor is disposed by teardown — not asserted
        // here (the round-trip identity + audit reclaim is the pin).
        var teardown = Record.Exception(shell.Dispose);
        Assert.Null(teardown);
        Assert.True(audit.IsDisposed);

        window.Close();
    }

    // === (2) OnAudit gate + CanAudit re-arming ==================================================

    /// <summary>
    /// Pre-load gate: with <see cref="WorkspaceViewModel.Snapshot"/> null (a workspace whose scope
    /// load is still IN FLIGHT), invoking the audit path is a no-op — <see cref="ShellViewModel.CurrentStep"/>
    /// stays the workspace — and <see cref="WorkspaceViewModel.AuditCommand"/>'s CanExecute is false.
    /// Once the load settles, CanExecute flips true (the <c>_isLoading</c> NotifyCanExecuteChangedFor
    /// re-arm plus the snapshot-non-null gate) and OnAudit opens.
    ///
    /// <para>The DemoProvider load settles synchronously inside <c>LoadRootCommand.Execute</c>, so a
    /// real pre-load window can only be observed by GATING the load: a <see cref="StubDirectoryProvider"/>
    /// whose <c>LoadScopeOverride</c> returns a <see cref="TaskCompletionSource{TResult}"/>-driven task
    /// that stays in flight until this test releases it with the loaded fixture snapshot (the same
    /// build idiom <c>ShellThemeTests</c>/the workspace dispose tests use). This is the faithful
    /// pre-load state — not a synthetic one.</para>
    /// </summary>
    [AvaloniaFact(Timeout = 60_000)]
    public async Task OnAudit_PreLoadSnapshotNull_IsANoOp_AndCanAuditFlipsAfterLoad()
    {
        // A gated load: it stays in flight until we complete the TCS with the fixture snapshot.
        var (snapshot, _) = LoadedScopeWithFindings();
        var loadGate = new TaskCompletionSource<DirectorySnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        var provider = new StubDirectoryProvider(Task.FromResult(new DirectoryConnection("stub directory", 0)))
        {
            RootCandidatesResult = Task.FromResult<IReadOnlyList<AdObject>>([
                new AdObject { Dn = RootDn, Kind = AdObjectKind.OrganizationalUnit, Name = "Lab" },
            ]),
            LoadScopeOverride = _ => loadGate.Task,
        };

        var (window, shell) = ShowShell(provider);

        var connect = Assert.IsType<ConnectionViewModel>(shell.CurrentStep);
        await connect.ConnectDemoCommand.ExecuteAsync(null);
        var picker = Assert.IsType<RootPickerViewModel>(shell.CurrentStep);
        await picker.LoadCandidates;
        Dispatcher.UIThread.RunJobs();
        picker.SelectedCandidate = picker.Candidates[0];
        picker.LoadRootCommand.Execute(null);
        var workspace = Assert.IsType<WorkspaceViewModel>(shell.CurrentStep);
        Dispatcher.UIThread.RunJobs();

        // Pre-load (the load is still gated): the audit command is armed-by-callback but gated CLOSED
        // on the null snapshot, and OnAudit is the honest no-op (no AuditViewModel built).
        Assert.Null(workspace.Snapshot);
        Assert.False(workspace.AuditCommand.CanExecute(null));
        shell.OnAudit(workspace);
        Assert.Same(workspace, shell.CurrentStep);

        // Release the load: the snapshot lands and CanAudit flips true.
        loadGate.SetResult(snapshot);
        await workspace.Initialization;
        Dispatcher.UIThread.RunJobs();
        Assert.NotNull(workspace.Snapshot);
        Assert.True(
            workspace.AuditCommand.CanExecute(null),
            "AuditCommand.CanExecute must flip true once the load settles (snapshot non-null, not loading)");

        // And the gate now opens: OnAudit produces the audit step.
        shell.OnAudit(workspace);
        Assert.IsType<AuditViewModel>(shell.CurrentStep);

        shell.Dispose();
        window.Close();
    }

    // === (2b) Off-UI-thread construction does not throw (the ApplyThemeVariant CheckAccess guard) =

    /// <summary>
    /// Pins the WP5b threading fix on <see cref="ShellViewModel"/>: constructing the shell OFF the
    /// Avalonia UI thread (here on a thread-pool thread via <see cref="Task.Run(System.Func{ShellViewModel})"/>)
    /// while <see cref="Application.Current"/> IS set must NOT throw. The ctor unconditionally calls
    /// <c>ApplyThemeVariant()</c>, whose <c>Application.Current.RequestedThemeVariant</c> setter is
    /// UI-thread-affine and previously threw <see cref="System.InvalidOperationException"/> ("Call
    /// from invalid thread") off-thread; the added <c>Dispatcher.UIThread.CheckAccess()</c> guard
    /// (ShellViewModel.cs ~line 339) skips the global setter when not on the UI thread.
    ///
    /// <para>The bound <see cref="ShellViewModel.ThemeChoice"/> still reflects the PERSISTED choice
    /// (seeded Light here) even though the off-thread global variant apply was skipped — the seam keeps
    /// VM state honest without touching UI-thread-affine app state.</para>
    ///
    /// <para><b>Why <see cref="AvaloniaFactAttribute"/>:</b> it sets <see cref="Application.Current"/>
    /// (the precondition that made the setter throw). A plain <see cref="FactAttribute"/> leaves
    /// <see cref="Application.Current"/> null, so the first guard short-circuits and the bug never
    /// reproduces. Inside this body we ARE on the UI thread, so <see cref="Task.Run(System.Func{ShellViewModel})"/>
    /// moves construction to a thread-pool thread where <c>Dispatcher.UIThread.CheckAccess()</c> is
    /// false — exactly the failing precondition. Hermetic: its own temp-dir <see cref="UiStateStore"/>,
    /// no shared state.</para>
    /// </summary>
    [AvaloniaFact(Timeout = 60_000)]
    public async Task Ctor_OffUiThread_DoesNotThrow_AndHonorsPersistedLightTheme()
    {
        // Seed a previously-persisted Light theme through a fresh temp-dir store BEFORE constructing
        // the shell (the Ctor_SeedsThemeChoice_FromPersistedLight idiom) — never touches %APPDATA%.
        var uiStateBase = System.IO.Directory
            .CreateTempSubdirectory("groupweaver-audit-offthread-uistate-").FullName;
        new UiStateStore(uiStateBase).Save(UiState.Default with { Theme = "Light" });

        // Construct the shell on a thread-pool thread (CheckAccess() == false there), capturing any
        // exception. The SAME provider/WebView2-present/null-factory construction ShowShell uses.
        ShellViewModel? shell = null;
        var ex = await Record.ExceptionAsync(async () => shell = await Task.Run(() => new ShellViewModel(
            _ => new DemoProvider(),
            new StartupOptions(Demo: false),
            Present,
            graphRendererFactory: null,
            ruleset: null,
            locator: null,
            uiStateStore: new UiStateStore(uiStateBase))));

        // (a) Off-thread construction does NOT throw (the CheckAccess guard skipped the affine setter).
        Assert.Null(ex);

        // (b) The persisted Light choice is still honored despite the skipped global variant apply.
        Assert.NotNull(shell);
        Assert.Equal(
            AppThemeChoice.Light,
            shell!.ThemeChoice);

        shell.Dispose();
    }

    // === (3) Summary projection equals AuditSummary.Compute over the same inputs ================

    /// <summary>
    /// The <see cref="AuditViewModel"/>'s <see cref="AuditViewModel.Summary"/> and its projected
    /// scalar props (<see cref="AuditViewModel.Score"/>/<see cref="AuditViewModel.Band"/>/
    /// <see cref="AuditViewModel.Critical"/>/<see cref="AuditViewModel.Warnings"/>/
    /// <see cref="AuditViewModel.Passing"/>/<see cref="AuditViewModel.RuleClasses"/>) equal
    /// <see cref="AuditSummary.Compute"/> over the SAME snapshot+report+ruleset. Unit-level
    /// (instruction #5): a hand-built loaded scope evaluated with the default ruleset — the expected
    /// summary is computed from the same inputs, never hardcoded, so the assertion tracks the engine.
    /// </summary>
    [Fact]
    public void AuditViewModel_Summary_EqualsComputeOverTheSameInputs()
    {
        var (snapshot, ruleset) = LoadedScopeWithFindings();
        var report = RuleEngine.Evaluate(snapshot, ruleset);
        var expected = AuditSummary.Compute(report, snapshot, ruleset);

        using var audit = new AuditViewModel(snapshot, report, ruleset, RootDn, onBack: () => { });

        // The whole record (scalars by value; ByRuleClass compared as a sorted projection so we never
        // rely on dictionary identity — rule-engine.md "compare PROJECTIONS").
        Assert.Equal(expected with { ByRuleClass = audit.Summary.ByRuleClass }, audit.Summary);
        Assert.Equal(SortedByRuleClass(expected), SortedByRuleClass(audit.Summary));

        // The projected props mirror the record exactly.
        Assert.Equal(expected.Score, audit.Score);
        Assert.Equal(expected.Band, audit.Band);
        Assert.Equal(expected.Critical, audit.Critical);
        Assert.Equal(expected.Warnings, audit.Warnings);
        Assert.Equal(expected.Passing, audit.Passing);
        Assert.Equal(expected.RuleClasses, audit.RuleClasses);

        // Anti-vacuous: this fixture actually has findings (otherwise "equals Compute" is trivially
        // satisfied by a clean board) and a meaningful score band.
        Assert.True(audit.Critical + audit.Warnings > 0, "fixture must carry real findings");
    }

    // === (4) ApplyRuleset recompute + change-notification =======================================

    /// <summary>
    /// <see cref="AuditViewModel.ApplyRuleset"/> with a flipped ruleset (the nesting rule disabled)
    /// re-Evaluates over the BORROWED Ist and updates <see cref="AuditViewModel.Summary"/> to match
    /// <see cref="AuditSummary.Compute"/> over the new ruleset — and the Summary change notifies the
    /// projected props (the <c>OnSummaryChanged</c> re-projection). The fixture's findings include
    /// nesting errors, so disabling nesting strictly RAISES the score (fewer Critical findings) — a
    /// real, observable recompute, not a no-op.
    /// </summary>
    [Fact]
    public void AuditViewModel_ApplyRuleset_Recomputes_AndChangeNotifiesProjectedProps()
    {
        var (snapshot, ruleset) = LoadedScopeWithFindings();
        var report = RuleEngine.Evaluate(snapshot, ruleset);
        using var audit = new AuditViewModel(snapshot, report, ruleset, RootDn, onBack: () => { });

        var before = audit.Summary;
        Assert.True(before.Critical > 0, "the default ruleset must surface Critical findings on this fixture");

        // Record the change-notifications the OnSummaryChanged hook must re-raise for the bound props.
        var notified = new System.Collections.Generic.HashSet<string>(StringComparer.Ordinal);
        void OnChanged(object? _, PropertyChangedEventArgs e)
        {
            if (e.PropertyName is { } name)
            {
                notified.Add(name);
            }
        }

        audit.PropertyChanged += OnChanged;

        // Flip the ruleset: disable the nesting rule. This drops the nesting Error findings, so the
        // recomputed summary differs (strictly higher score / fewer Critical).
        var defaults = RulesetLoader.LoadDefault();
        var nestingOff = ruleset with { Nesting = defaults.Nesting with { Enabled = false } };
        var expected = AuditSummary.Compute(RuleEngine.Evaluate(snapshot, nestingOff), snapshot, nestingOff);

        audit.ApplyRuleset(nestingOff);

        audit.PropertyChanged -= OnChanged;

        // The recompute matches Compute over the flipped ruleset (re-evaluated, not a stale copy).
        Assert.Equal(expected with { ByRuleClass = audit.Summary.ByRuleClass }, audit.Summary);
        Assert.Equal(SortedByRuleClass(expected), SortedByRuleClass(audit.Summary));
        Assert.Equal(expected.Score, audit.Score);
        Assert.Equal(expected.Critical, audit.Critical);
        Assert.Equal(expected.RuleClasses, audit.RuleClasses);

        // The recompute was observable, not a no-op: disabling nesting removed Critical findings and
        // raised the score.
        Assert.True(audit.Critical < before.Critical, "disabling nesting must drop Critical findings");
        Assert.True(audit.Score > before.Score, "fewer findings must raise the score");
        Assert.Equal(before.RuleClasses - 1, audit.RuleClasses); // one fewer enabled rule block

        // The Summary record AND every projected prop change-notified (the binding stays live).
        Assert.Contains(nameof(AuditViewModel.Summary), notified);
        Assert.Contains(nameof(AuditViewModel.Score), notified);
        Assert.Contains(nameof(AuditViewModel.Band), notified);
        Assert.Contains(nameof(AuditViewModel.Critical), notified);
        Assert.Contains(nameof(AuditViewModel.Warnings), notified);
        Assert.Contains(nameof(AuditViewModel.Passing), notified);
        Assert.Contains(nameof(AuditViewModel.RuleClasses), notified);
    }

    // === Helpers ===============================================================================

    private const string RootDn = "OU=Lab,DC=stub,DC=lab";

    /// <summary>The ByRuleClass map as a sorted (ruleId, count) projection — the comparison contract
    /// (rule-engine.md: compare PROJECTIONS, never dictionary identity).</summary>
    private static (string, int)[] SortedByRuleClass(AuditSummary summary) =>
        summary.ByRuleClass
            .Select(kvp => (kvp.Key, kvp.Value))
            .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    /// <summary>
    /// A small, fully-LOADED scope that deliberately trips the default ruleset's nesting + naming +
    /// empty-group rules, so <see cref="AuditSummary.Compute"/> yields a non-trivial mix of
    /// Critical/Warnings/Info — enough to make "equals Compute" and "ApplyRuleset changes the result"
    /// real assertions. Every group is loaded (members set), so it counts toward CheckedSubjects (no
    /// tri-state unchecked noise). Returns the snapshot + the default ruleset.
    ///
    /// <para>The structure: a DL containing a GG (AGDLP-legal — no finding), a DL containing a USER
    /// directly (an AGDLP nesting Error), a badly-named GG (a naming Warning on naming-gg), and an
    /// empty GG (an empty-group Info). Findings are produced by the ENGINE, not hand-built, so the
    /// fixture stays honest against rule changes.</para>
    /// </summary>
    private static (DirectorySnapshot Snapshot, Ruleset Ruleset) LoadedScopeWithFindings()
    {
        const string dlOk = "CN=DL_FileShare_RW,OU=Lab,DC=stub,DC=lab";
        const string ggMember = "CN=GG_FileShare_Members,OU=Lab,DC=stub,DC=lab";
        const string dlBad = "CN=DL_DirectUser_RW,OU=Lab,DC=stub,DC=lab";
        const string userDn = "CN=alice,OU=Lab,DC=stub,DC=lab";
        const string ggBadName = "CN=NotAConventionName,OU=Lab,DC=stub,DC=lab";
        const string ggEmpty = "CN=GG_Empty_Team,OU=Lab,DC=stub,DC=lab";

        var snapshot = new DirectorySnapshot();
        snapshot.AddObject(Group(dlOk, AdObjectKind.DomainLocalGroup));
        snapshot.AddObject(Group(ggMember, AdObjectKind.GlobalGroup));
        snapshot.AddObject(Group(dlBad, AdObjectKind.DomainLocalGroup));
        snapshot.AddObject(new AdObject { Dn = userDn, Kind = AdObjectKind.User, Name = "alice" });
        snapshot.AddObject(Group(ggBadName, AdObjectKind.GlobalGroup));
        snapshot.AddObject(Group(ggEmpty, AdObjectKind.GlobalGroup));

        // DL_Ok -> GG (AGDLP-legal). GG itself loaded-empty.
        snapshot.SetMembers(dlOk, new[] { ggMember });
        snapshot.SetMembers(ggMember, Array.Empty<string>());
        // DL_Bad -> User directly (AGDLP nesting Error: a DL with a user member).
        snapshot.SetMembers(dlBad, new[] { userDn });
        // The badly-named GG, loaded-empty (also an empty-group Info, plus a naming Warning).
        snapshot.SetMembers(ggBadName, Array.Empty<string>());
        // An empty GG (empty-group Info).
        snapshot.SetMembers(ggEmpty, Array.Empty<string>());

        return (snapshot, RulesetLoader.LoadDefault());
    }

    private static AdObject Group(string dn, AdObjectKind kind) => new()
    {
        Dn = dn,
        Kind = kind,
        Name = dn.Split(',')[0]["CN=".Length..],
    };

    /// <summary>A shown <see cref="MainWindow"/> + real <see cref="ShellViewModel"/> over a real
    /// <see cref="DemoProvider"/>, WebView2 forced present, with a fresh temp-dir
    /// <see cref="UiStateStore"/> (the #124 isolation seam — never touches real %APPDATA%). The Audit
    /// step owns no renderer, so the default null renderer factory suffices. Mirrors the back-nav
    /// tests' shell-construction idiom.</summary>
    private static (MainWindow Window, ShellViewModel Shell) ShowShell() => ShowShell(provider: null);

    private static (MainWindow Window, ShellViewModel Shell) ShowShell(IDirectoryProvider? provider)
    {
        var uiStateBase = System.IO.Directory
            .CreateTempSubdirectory("groupweaver-audit-uistate-").FullName;
        var shell = new ShellViewModel(
            _ => provider ?? new DemoProvider(),
            new StartupOptions(Demo: false),
            Present,
            graphRendererFactory: null,
            ruleset: null,
            locator: null,
            uiStateStore: new UiStateStore(uiStateBase));

        var window = new MainWindow { DataContext = shell, Width = 1280, Height = 720 };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return (window, shell);
    }

    /// <summary>Connect (demo) → pick the demo root OU → load, awaiting the settled workspace (the
    /// demo root OU scope carries the seeded GG_Circle_A↔GG_Circle_B cycle). Mirrors the back-nav
    /// tests' DriveToWorkspaceAsync.</summary>
    private static async Task<WorkspaceViewModel> DriveToWorkspaceAsync(ShellViewModel shell)
    {
        var connect = Assert.IsType<ConnectionViewModel>(shell.CurrentStep);
        await connect.ConnectDemoCommand.ExecuteAsync(null);
        var picker = Assert.IsType<RootPickerViewModel>(shell.CurrentStep);
        await picker.LoadCandidates;
        Dispatcher.UIThread.RunJobs();

        picker.SelectedCandidate = picker.Candidates.First(c => c.Kind == AdObjectKind.OrganizationalUnit);
        picker.LoadRootCommand.Execute(null);
        var workspace = Assert.IsType<WorkspaceViewModel>(shell.CurrentStep);
        await workspace.Initialization;
        Dispatcher.UIThread.RunJobs();
        return workspace;
    }
}
