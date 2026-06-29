using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;

using Avalonia.Headless.XUnit;
using Avalonia.Threading;

using GroupWeaver.App.Settings;
using GroupWeaver.App.Startup;
using GroupWeaver.App.ViewModels;
using GroupWeaver.App.Views;
using GroupWeaver.Core.Model;
using GroupWeaver.Core.Providers;
using GroupWeaver.Core.Rules;
using GroupWeaver.Providers;

using Xunit;

namespace GroupWeaver.App.Tests;

/// <summary>
/// Pins the WP5c (#154) health-band + categories projections on the
/// <see cref="AuditViewModel"/> — the dashboard ring + categories pane the audit step binds.
/// Sister to <see cref="AuditNavigationTests"/> (the WP5b summary/recompute pins): this fixture
/// covers the NEW WP5c projections only — <see cref="AuditViewModel.RingFraction"/>,
/// <see cref="AuditViewModel.HealthAutomationName"/>, the <see cref="AuditViewModel.UncheckedPresent"/>
/// / <see cref="AuditViewModel.Info"/> passthroughs, and the
/// <see cref="AuditViewModel.Categories"/> / <see cref="AuditViewModel.HasCategories"/> pane
/// (canonical rule order, finding-bearing classes only, rebuilt on <see cref="AuditViewModel.ApplyRuleset"/>).
///
/// <para><b>Two layers of fixture, exactly as the WP5b tests use.</b> The scalar projections (#1-#3)
/// and the ApplyRuleset rebuild (#5) are UNIT-level over a directly-constructed
/// <see cref="AuditViewModel"/> on the SAME hand-built loaded scope the WP5b tests pin
/// (<see cref="LoadedScopeWithFindings"/>) — expectations are computed from
/// <see cref="AuditSummary.Compute"/> over the same inputs, never hardcoded, so the assertions
/// track the engine. The rich categories ordering (#4) drives the REAL <see cref="DemoProvider"/>
/// scope through the shell (the AP 3.2 19-finding baseline: nesting + naming-gg/dl + circular +
/// empty-group, the only scope that carries ALL FOUR canonical rule classes incl. the seeded
/// GG_Circle_A↔GG_Circle_B cycle, so the canonical-order pin is exercised end to end).</para>
///
/// <para><b>Test-isolation seam (#124 lesson):</b> the shell-driven case injects a temp-dir
/// <see cref="UiStateStore"/> so it never reads/writes real <c>%APPDATA%</c> (a persisted
/// <c>RailCollapsed:true</c> on this box would otherwise starve view realization).</para>
///
/// <para>Compares PROJECTIONS, never record/collection identity (rule-engine.md /
/// data-model.md): category rows are compared as (RuleId, DisplayName, Count, MaxSeverity) tuples.</para>
/// </summary>
public sealed class AuditHealthBandTests
{
    private static readonly WebView2RuntimeStatus Present = new(IsInstalled: true, Version: "test");

    // === (1) RingFraction == Score / 100.0 =====================================================

    /// <summary>
    /// <see cref="AuditViewModel.RingFraction"/> is exactly <see cref="AuditViewModel.Score"/> / 100.0
    /// — the conic-gradient hard stop. Pinned across the whole range incl. the 0 (Poor floor) and 100
    /// (Excellent / clean) endpoints, with the VM's Score driving the ratio so the two can never drift.
    /// </summary>
    [Theory]
    [InlineData(0, 0.0)]
    [InlineData(25, 0.25)]
    [InlineData(55, 0.55)]
    [InlineData(100, 1.0)]
    public void RingFraction_IsScoreOverHundred(int score, double expectedFraction)
    {
        using var audit = AuditWithScore(score);

        Assert.Equal(score, audit.Score); // the fixture really yields the targeted score
        Assert.Equal(expectedFraction, audit.RingFraction, precision: 10);
        Assert.Equal(audit.Score / 100.0, audit.RingFraction, precision: 10); // the literal contract
    }

    // === (2) HealthAutomationName exact text ====================================================

    /// <summary>
    /// <see cref="AuditViewModel.HealthAutomationName"/> is the exact WCAG 1.1.1/4.1.2 screen-reader
    /// label "Directory health {Score} of 100, {Band}" — the decorative ring's redundant text channel.
    /// Pinned for a KNOWN summary (the hand-built findings scope) whose Score/Band come from the engine,
    /// so the literal text tracks the computed values rather than a hardcoded score.
    /// </summary>
    [Fact]
    public void HealthAutomationName_IsTheExactScoreAndBandLabel()
    {
        var (snapshot, ruleset) = LoadedScopeWithFindings();
        var report = RuleEngine.Evaluate(snapshot, ruleset);
        var expected = AuditSummary.Compute(report, snapshot, ruleset);
        using var audit = new AuditViewModel(snapshot, report, ruleset, RootDn, onBack: () => { });

        // ADR-030 (#188): this fixture trips a nesting Error (a DL with a direct User member), so it is
        // Critical > 0 and the band gates to "Action required". The spoken name now LEADS with that
        // verdict + the Critical count (the band is the headline, the diluted score secondary), not the
        // score+band phrasing — derived from expected.Critical so it tracks the engine, never hardcoded.
        Assert.True(expected.Critical > 0, "the findings fixture must surface a Critical (nesting Error)");
        Assert.Equal($"Directory health: action required, {expected.Critical} critical", audit.HealthAutomationName);

        // And a fully-pinned literal for an exact-score fixture so the score+band format string itself
        // is nailed down (not just the interpolation): a clean scope is Critical == 0 / Score 100 /
        // "Excellent" => the score+band phrasing (the non-Critical arm).
        using var clean = AuditWithScore(100);
        Assert.Equal("Directory health 100 of 100, Excellent", clean.HealthAutomationName);
    }

    // === (3) UncheckedPresent + Info passthrough ===============================================

    /// <summary>
    /// <see cref="AuditViewModel.UncheckedPresent"/> and <see cref="AuditViewModel.Info"/> are pure
    /// passthroughs of the borrowed <see cref="AuditSummary"/>. Pinned on a scope whose default-ruleset
    /// evaluation yields Info findings (the empty groups) — so Info > 0 is a real assertion, not a
    /// trivially-true 0 == 0 — and again on a scope with a known-but-unloaded parent so
    /// UncheckedPresent flips true via the report frontier.
    /// </summary>
    [Fact]
    public void UncheckedPresent_And_Info_MatchTheSummary()
    {
        var (snapshot, ruleset) = LoadedScopeWithFindings();
        var report = RuleEngine.Evaluate(snapshot, ruleset);
        var expected = AuditSummary.Compute(report, snapshot, ruleset);
        using var audit = new AuditViewModel(snapshot, report, ruleset, RootDn, onBack: () => { });

        Assert.Equal(expected.Info, audit.Info);
        Assert.Equal(expected.UncheckedPresent, audit.UncheckedPresent);

        // Anti-vacuous: this fixture's empty groups are real Info findings.
        Assert.True(audit.Info > 0, "the findings fixture must surface Info-severity findings (empty groups)");
        // The fully-loaded scope has no unexpanded areas => the passthrough is honestly false here.
        Assert.False(audit.UncheckedPresent);

        // A scope with a KNOWN-but-unloaded group parent flips UncheckedPresent true (the report
        // frontier is non-empty), proving the passthrough tracks the summary in BOTH states.
        var (unchecked_, uncheckedRuleset) = ScopeWithUncheckedParent();
        var uncheckedReport = RuleEngine.Evaluate(unchecked_, uncheckedRuleset);
        var uncheckedExpected = AuditSummary.Compute(uncheckedReport, unchecked_, uncheckedRuleset);
        using var uncheckedAudit = new AuditViewModel(unchecked_, uncheckedReport, uncheckedRuleset, RootDn, onBack: () => { });

        Assert.True(uncheckedExpected.UncheckedPresent, "the unloaded-parent fixture must put a DN in the frontier");
        Assert.Equal(uncheckedExpected.UncheckedPresent, uncheckedAudit.UncheckedPresent);
        Assert.True(uncheckedAudit.UncheckedPresent);
    }

    // === (4) Categories: canonical order, finding-bearing only, HasCategories ===================

    /// <summary>
    /// Over the REAL demo scope (the AP 3.2 19-finding baseline), <see cref="AuditViewModel.Categories"/>
    /// is exactly the finding-bearing rule classes in CANONICAL <see cref="Ruleset.EnumerateRules"/>
    /// order — nesting → naming-gg → naming-dl → circular → empty-group — each with the right
    /// DisplayName, Count (from <see cref="AuditSummary.ByRuleClass"/>) and MaxSeverity (the max over
    /// that class's findings). The CLEAN naming-ug class (zero findings) must be ABSENT, and
    /// <see cref="AuditViewModel.HasCategories"/> is true.
    ///
    /// <para>Built by mirroring <see cref="ShellViewModel.OnAudit"/> (the live wiring): a fresh
    /// <see cref="AuditViewModel"/> over the demo workspace's loaded Ist snapshot + report + the
    /// default ruleset. The expected rows are derived from the SAME report/summary, never hardcoded,
    /// so they cannot drift from the engine; only the ORDER and the absent-clean-class invariants
    /// are literal.</para>
    /// </summary>
    [AvaloniaFact(Timeout = 60_000)]
    public async Task Categories_DemoScope_AreFindingBearingClassesInCanonicalOrder()
    {
        var (window, shell, workspace) = await DriveDemoWorkspaceAsync();

        var snapshot = workspace.Snapshot!;
        var ruleset = RulesetLoader.LoadDefault();
        var report = workspace.Report;
        var summary = AuditSummary.Compute(report, snapshot, ruleset);

        using var audit = new AuditViewModel(snapshot, report, ruleset, workspace.RootDn, onBack: () => { });

        Assert.True(audit.HasCategories, "the demo scope produces findings => HasCategories");

        // Expected = the canonical EnumerateRules order, intersected with finding-bearing classes,
        // each row's Count from ByRuleClass and MaxSeverity from the report's findings. Derived from
        // the same report the VM consumes (never hardcoded) — only the order/intersection is the pin.
        var expectedRows = ExpectedCategoryRows(ruleset, report, summary);

        Assert.Equal(expectedRows, RowProjection(audit.Categories));

        // The canonical order is concretely the demo baseline's class sequence (the literal pin):
        // nesting → naming-gg → naming-dl → circular → empty-group, naming-ug ABSENT (clean class).
        Assert.Equal(
            new[]
            {
                (RuleIds.Nesting, "Nesting matrix", RuleSeverity.Error),
                ("naming-gg", "Naming: naming-gg", RuleSeverity.Warning),
                ("naming-dl", "Naming: naming-dl", RuleSeverity.Warning),
                (RuleIds.Circular, "Circular nesting", RuleSeverity.Error),
                (RuleIds.EmptyGroup, "Empty groups", RuleSeverity.Info),
            },
            audit.Categories.Select(r => (r.RuleId, r.DisplayName, r.MaxSeverity)).ToArray());

        // The clean naming-ug class produced no finding => it is not a row.
        Assert.DoesNotContain("naming-ug", audit.Categories.Select(r => r.RuleId));

        shell.Dispose();
        window.Close();
    }

    /// <summary>A report with NO findings yields an EMPTY <see cref="AuditViewModel.Categories"/> pane
    /// and <see cref="AuditViewModel.HasCategories"/> false — the all-clear scope shows no category
    /// rows even though every rule block is enabled.</summary>
    [Fact]
    public void Categories_CleanReport_IsEmpty_AndHasCategoriesFalse()
    {
        // Three loaded GG groups, default (rules-enabled) ruleset, but a hand-built clean report.
        var snapshot = LoadedGroups(3);
        var ruleset = RulesetLoader.LoadDefault();
        var report = new RuleReport(Array.Empty<RuleViolation>(), Array.Empty<string>());

        using var audit = new AuditViewModel(snapshot, report, ruleset, RootDn, onBack: () => { });

        Assert.False(audit.HasCategories);
        Assert.Empty(audit.Categories);
    }

    // === (5) Categories rebuilt on ApplyRuleset (+ change-notification) =========================

    /// <summary>
    /// <see cref="AuditViewModel.ApplyRuleset"/> with the nesting rule disabled REBUILDS
    /// <see cref="AuditViewModel.Categories"/> in place: the nesting row drops, the surviving rows'
    /// counts/order still match <see cref="AuditSummary.ByRuleClass"/> over the flipped ruleset, and
    /// the rebuild change-notifies <see cref="AuditViewModel.HasCategories"/>. The fixture carries a
    /// nesting Error, so dropping it is a real, observable change (one fewer row).
    /// </summary>
    [Fact]
    public void ApplyRuleset_RebuildsCategories_DropsTheNestingRow_AndNotifies()
    {
        var (snapshot, ruleset) = LoadedScopeWithFindings();
        var report = RuleEngine.Evaluate(snapshot, ruleset);
        using var audit = new AuditViewModel(snapshot, report, ruleset, RootDn, onBack: () => { });

        // Pre-condition: the default ruleset surfaces a nesting row on this fixture.
        Assert.Contains(RuleIds.Nesting, audit.Categories.Select(r => r.RuleId));
        var rowsBefore = audit.Categories.Count;

        var notified = new HashSet<string>(StringComparer.Ordinal);
        void OnChanged(object? _, PropertyChangedEventArgs e)
        {
            if (e.PropertyName is { } name)
            {
                notified.Add(name);
            }
        }

        audit.PropertyChanged += OnChanged;

        var defaults = RulesetLoader.LoadDefault();
        var nestingOff = ruleset with { Nesting = defaults.Nesting with { Enabled = false } };
        var expected = AuditSummary.Compute(RuleEngine.Evaluate(snapshot, nestingOff), snapshot, nestingOff);

        audit.ApplyRuleset(nestingOff);

        audit.PropertyChanged -= OnChanged;

        // The nesting row is gone (its rule is disabled => no nesting findings => not in ByRuleClass).
        Assert.DoesNotContain(RuleIds.Nesting, audit.Categories.Select(r => r.RuleId));
        Assert.Equal(rowsBefore - 1, audit.Categories.Count);

        // The surviving rows still match ByRuleClass over the flipped ruleset, in canonical order,
        // compared as projections (never collection identity).
        Assert.Equal(
            ExpectedCategoryRows(nestingOff, RuleEngine.Evaluate(snapshot, nestingOff), expected),
            RowProjection(audit.Categories));

        // The rebuild re-raised HasCategories (the binding gate stays live).
        Assert.Contains(nameof(AuditViewModel.HasCategories), notified);
    }

    // === Helpers ===============================================================================

    private const string RootDn = "OU=Lab,DC=stub,DC=lab";

    /// <summary>The category rows as (RuleId, DisplayName, Count, MaxSeverity) tuples — the PROJECTION
    /// the assertions compare (never record/collection identity; rule-engine.md).</summary>
    private static (string, string, int, RuleSeverity)[] RowProjection(IEnumerable<AuditCategoryRow> rows) =>
        rows.Select(r => (r.RuleId, r.DisplayName, r.Count, r.MaxSeverity)).ToArray();

    /// <summary>The EXPECTED category rows derived from the SAME report/summary the VM consumes: the
    /// canonical <see cref="Ruleset.EnumerateRules"/> order, intersected with finding-bearing classes
    /// (<see cref="AuditSummary.ByRuleClass"/>), each row's DisplayName from EnumerateRules, Count from
    /// ByRuleClass and MaxSeverity from the report's findings. Mirrors <see cref="AuditViewModel"/>'s
    /// own RebuildCategories so the test pins the CONTRACT (order + intersection + fields), not a
    /// hardcoded count.</summary>
    private static (string, string, int, RuleSeverity)[] ExpectedCategoryRows(
        Ruleset ruleset, RuleReport report, AuditSummary summary)
    {
        var maxByRuleId = new Dictionary<string, RuleSeverity>(StringComparer.OrdinalIgnoreCase);
        foreach (var v in report.Violations)
        {
            if (!maxByRuleId.TryGetValue(v.RuleId, out var s) || v.Severity > s)
            {
                maxByRuleId[v.RuleId] = v.Severity;
            }
        }

        return ruleset.EnumerateRules()
            .Where(rule => summary.ByRuleClass.TryGetValue(rule.Id, out var c) && c > 0)
            .Select(rule => (
                rule.Id,
                rule.DisplayName,
                summary.ByRuleClass[rule.Id],
                maxByRuleId.TryGetValue(rule.Id, out var max) ? max : RuleSeverity.Info))
            .ToArray();
    }

    /// <summary>An <see cref="AuditViewModel"/> whose summary Score is exactly <paramref name="score"/>.
    /// Built over a loaded-groups snapshot of 100 subjects plus a hand-built warnings-only report so
    /// the score maps 1:1 (penalty == warnings over CS=100, raw == 100-warnings) — the same exact-score
    /// idiom AuditSummaryTests' band-threshold theory uses; the score is engine-computed (not forced).</summary>
    private static AuditViewModel AuditWithScore(int score)
    {
        var snapshot = LoadedGroups(100);
        var ruleset = RulesetLoader.LoadDefault();
        var warnings = Enumerable.Range(0, 100 - score)
            .Select(i => new RuleViolation
            {
                RuleId = "naming-gg",
                Severity = RuleSeverity.Warning,
                Dns = new[] { $"CN=GG_{i:D4},OU=Lab,DC=stub,DC=lab" },
                Message = "synthetic",
            })
            .ToArray<RuleViolation>();
        var report = new RuleReport(warnings, Array.Empty<string>());
        return new AuditViewModel(snapshot, report, ruleset, RootDn, onBack: () => { });
    }

    /// <summary><paramref name="count"/> loaded (empty) GG groups — CheckedSubjects == count under
    /// any loaded-group-rule-enabled ruleset (mirrors AuditSummaryTests.LoadedGroups).</summary>
    private static DirectorySnapshot LoadedGroups(int count)
    {
        var snapshot = new DirectorySnapshot();
        for (var i = 0; i < count; i++)
        {
            var dn = $"CN=GG_{i:D4},OU=Lab,DC=stub,DC=lab";
            snapshot.AddObject(new AdObject { Dn = dn, Kind = AdObjectKind.GlobalGroup, Name = dn });
            snapshot.SetMembers(dn, Array.Empty<string>());
        }

        return snapshot;
    }

    /// <summary>The WP5b findings fixture: a fully-LOADED scope that trips the default ruleset's
    /// nesting (a DL with a direct User member) + naming (a badly-named GG) + empty-group rules, so
    /// Compute yields a real Critical/Warning/Info mix. Identical structure to
    /// <see cref="AuditNavigationTests"/>'s helper (re-stated here so the two fixtures stay
    /// independent). Returns the snapshot + the default ruleset.</summary>
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

        snapshot.SetMembers(dlOk, new[] { ggMember });
        snapshot.SetMembers(ggMember, Array.Empty<string>());
        snapshot.SetMembers(dlBad, new[] { userDn });
        snapshot.SetMembers(ggBadName, Array.Empty<string>());
        snapshot.SetMembers(ggEmpty, Array.Empty<string>());

        return (snapshot, RulesetLoader.LoadDefault());
    }

    /// <summary>A scope with a KNOWN-but-unloaded group parent — its DN lands in the report frontier
    /// (UncheckedDns), so AuditSummary.UncheckedPresent is true. One loaded-empty GG (so there is a
    /// checked subject) plus one GG added-but-never-loaded.</summary>
    private static (DirectorySnapshot Snapshot, Ruleset Ruleset) ScopeWithUncheckedParent()
    {
        const string loaded = "CN=GG_Loaded,OU=Lab,DC=stub,DC=lab";
        const string unloaded = "CN=GG_Unloaded,OU=Lab,DC=stub,DC=lab";

        var snapshot = new DirectorySnapshot();
        snapshot.AddObject(Group(loaded, AdObjectKind.GlobalGroup));
        snapshot.AddObject(Group(unloaded, AdObjectKind.GlobalGroup));
        snapshot.SetMembers(loaded, Array.Empty<string>());
        // unloaded: deliberately NOT loaded => the tri-state's unchecked arm.

        return (snapshot, RulesetLoader.LoadDefault());
    }

    private static AdObject Group(string dn, AdObjectKind kind) => new()
    {
        Dn = dn,
        Kind = kind,
        Name = dn.Split(',')[0]["CN=".Length..],
    };

    /// <summary>Connect (demo) → pick the demo root OU → load, returning the shown window, the real
    /// shell, and the settled demo workspace (the demo root OU scope carries the AP 3.2 baseline incl.
    /// the GG_Circle_A↔GG_Circle_B cycle). Mirrors AuditNavigationTests' drive idiom; injects a
    /// temp-dir UiStateStore (the #124 isolation seam — never touches real %APPDATA%).</summary>
    private static async Task<(MainWindow Window, ShellViewModel Shell, WorkspaceViewModel Workspace)> DriveDemoWorkspaceAsync()
    {
        var uiStateBase = System.IO.Directory
            .CreateTempSubdirectory("groupweaver-audit-band-uistate-").FullName;
        var shell = new ShellViewModel(
            _ => new DemoProvider(),
            new StartupOptions(Demo: false),
            Present,
            graphRendererFactory: null,
            ruleset: null,
            locator: null,
            uiStateStore: new UiStateStore(uiStateBase));

        var window = new MainWindow { DataContext = shell, Width = 1280, Height = 720 };
        window.Show();
        Dispatcher.UIThread.RunJobs();

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

        Assert.NotNull(workspace.Snapshot);
        return (window, shell, workspace);
    }
}
