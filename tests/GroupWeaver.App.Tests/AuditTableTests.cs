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
using GroupWeaver.Core.Rules;
using GroupWeaver.Providers;

using Xunit;

namespace GroupWeaver.App.Tests;

/// <summary>
/// Pins the WP5d (#156) audit findings table on the <see cref="AuditViewModel"/> — the
/// 1:1 row projection over <see cref="RuleReport.Violations"/>, the snapshot-only name
/// resolution (parity with the sidebar via the shared <see cref="SubjectNameResolver"/>),
/// the deterministic header sort (incl. the re-sort-stability / rank-on-row fix), the
/// multi-select roll-ups, the <see cref="AuditViewModel.ApplyRuleset"/> rebuild (selection
/// dropped, stale row subscriptions detached), and the "show in graph" → Back toggle.
///
/// <para>Sister to <see cref="AuditNavigationTests"/> (summary/recompute) and
/// <see cref="AuditHealthBandTests"/> (health band + categories): this fixture covers the
/// NEW WP5d table projections only. Most pins are UNIT-level over a directly-constructed
/// <see cref="AuditViewModel"/> on the SAME hand-built loaded fixtures the WP5b/WP5c tests
/// use; one pin drives the REAL <see cref="DemoProvider"/> scope (the AP 3.2 19-finding
/// baseline) through the shell so default order == the engine's canonical report order is
/// proven end to end, incl. the seeded GG_Circle_A↔GG_Circle_B cycle.</para>
///
/// <para><b>Test-isolation seam (#124 lesson):</b> the shell-driven case injects a temp-dir
/// <see cref="UiStateStore"/> so it never reads/writes real <c>%APPDATA%</c>.</para>
///
/// <para>Compares PROJECTIONS, never record/collection identity (rule-engine.md /
/// data-model.md): rows are compared as their value tuples and the report-order sequence.</para>
/// </summary>
public sealed class AuditTableTests
{
    private static readonly WebView2RuntimeStatus Present = new(IsInstalled: true, Version: "test");

    private const string RootDn = "OU=Lab,DC=stub,DC=lab";

    // === (1) Row projection: 1:1 with Report.Violations, field-by-field, Status=Open ============

    /// <summary>
    /// <see cref="AuditViewModel.Findings"/> is one row per <see cref="RuleReport.Violations"/> entry
    /// (same count), and each row's <see cref="AuditFindingRowModel.Severity"/>/<c>Message</c>/
    /// <c>PrimaryDn</c>/<c>RuleClass</c>/<c>ReportOrder</c> matches the corresponding violation, with
    /// <c>Status</c> a fixed <see cref="TriageStatus.Open"/> (no triage entries in this fixture).
    /// <c>RuleClass</c> is the <see cref="Ruleset.EnumerateRules"/>
    /// DisplayName for that finding's rule id. The default <see cref="AuditSortColumn.None"/> sort keeps
    /// the rows in canonical report order so the positional match is honest.
    /// </summary>
    [Fact]
    public void Findings_AreOneRowPerViolation_FieldByField_StatusOpen() // status is TriageStatus.Open
    {
        var (snapshot, ruleset) = LoadedScopeWithFindings();
        var report = RuleEngine.Evaluate(snapshot, ruleset);
        using var audit = new AuditViewModel(snapshot, report, ruleset, RootDn, onBack: () => { });

        Assert.Equal(report.Violations.Count, audit.Findings.Count);
        Assert.True(audit.Findings.Count > 0, "the fixture must produce findings (else this pin is vacuous)");
        Assert.True(audit.HasFindings);

        // The rule id -> DisplayName map (the same EnumerateRules source the VM uses for RuleClass).
        var displayNameById = ruleset.EnumerateRules().ToDictionary(
            r => r.Id, r => r.DisplayName, StringComparer.OrdinalIgnoreCase);

        // Field-by-field, positionally (default None sort == report order): Severity/Message/PrimaryDn/
        // ReportOrder/RuleClass match the report violation; Status is the fixed "Open".
        for (var i = 0; i < report.Violations.Count; i++)
        {
            var v = report.Violations[i];
            var row = audit.Findings[i];

            Assert.Equal(v.Severity, row.Severity);
            Assert.Equal(v.Message, row.Message);
            Assert.Equal(v.PrimaryDn, row.PrimaryDn);
            Assert.Equal(i, row.ReportOrder);
            Assert.Equal(displayNameById[v.RuleId], row.RuleClass);
            Assert.Equal(TriageStatus.Open, row.Status);
        }
    }

    /// <summary>
    /// <see cref="AuditFindingRowModel.ObjectName"/> resolves the anchor DN via the SNAPSHOT (a loaded
    /// subject → its <c>Name</c>) and falls back to the raw DN for an anchor that is absent from the
    /// snapshot. The loaded branch is proven over the engine-produced fixture (every finding's PrimaryDn
    /// resolves to its Name).
    ///
    /// <para><b>WP5e contract note:</b> the findings table is now projected from the engine's WOULD-BE
    /// report (<see cref="RuleEngine.Evaluate"/> over the snapshot + base ignore — ADR-028), NOT from a
    /// report the caller hands the VM, so the table can no longer be driven with a hand-built synthetic
    /// finding (no real rule emits an absent PrimaryDn — a nesting parent is always loaded). The
    /// DN-fallback branch is therefore pinned on the EXACT resolver the VM's projection calls,
    /// <see cref="SubjectNameResolver.Resolve"/> (see <c>AuditViewModel.RebuildFindings</c>), which is the
    /// load-bearing production code path; the parity test below proves the table's <c>ObjectName</c> is
    /// that same call for every row.</para>
    /// </summary>
    [Fact]
    public void ObjectName_ResolvesLoadedSubjectName_AndFallsBackToDnForAbsentAnchor()
    {
        // --- Loaded branch: engine-produced findings, every anchor resolves to its Name ---
        var (snapshot, ruleset) = LoadedScopeWithFindings();
        var report = RuleEngine.Evaluate(snapshot, ruleset);
        using var audit = new AuditViewModel(snapshot, report, ruleset, RootDn, onBack: () => { });

        foreach (var row in audit.Findings)
        {
            var expected = snapshot.TryGetObject(row.PrimaryDn, out var obj) ? obj!.Name : row.PrimaryDn;
            Assert.Equal(expected, row.ObjectName);
        }

        // Anti-vacuous: at least one row resolved to a real loaded Name distinct from its DN.
        Assert.Contains(
            audit.Findings,
            r => snapshot.TryGetObject(r.PrimaryDn, out _) && !string.Equals(r.ObjectName, r.PrimaryDn, StringComparison.Ordinal));

        // --- DN-fallback branch: pinned on the resolver the VM projection calls (RebuildFindings →
        //     SubjectNameResolver.Resolve). A loaded subject resolves to its Name; an absent anchor
        //     falls back to the raw DN — exactly what an absent-PrimaryDn row would show in the table. ---
        const string loadedDn = "CN=GG_Real,OU=Lab,DC=stub,DC=lab";
        const string absentDn = "CN=ghost,DC=elsewhere,DC=lab"; // deliberately NOT in Objects
        var fallbackScope = new DirectorySnapshot();
        fallbackScope.AddObject(new AdObject { Dn = loadedDn, Kind = AdObjectKind.GlobalGroup, Name = "GG_Real" });

        Assert.Equal("GG_Real", SubjectNameResolver.Resolve(fallbackScope, loadedDn)); // loaded => Name
        Assert.Equal(absentDn, SubjectNameResolver.Resolve(fallbackScope, absentDn));   // absent => the DN itself
    }

    /// <summary>
    /// <see cref="SubjectNameResolver.Resolve"/> is the single shared resolution the audit table AND the
    /// sidebar route through: the same (snapshot, dn) input yields the same output. Pinned over a scope
    /// with a loaded subject (→ Name) and an absent anchor (→ DN), plus the null-snapshot fallback —
    /// the audit table's <c>ObjectName</c> equals what the sidebar's <c>SubjectName</c> would resolve to.
    /// </summary>
    [Fact]
    public void SubjectNameResolver_AuditTable_HasParityWithSidebarResolution()
    {
        var (snapshot, ruleset) = LoadedScopeWithFindings();
        var report = RuleEngine.Evaluate(snapshot, ruleset);
        using var audit = new AuditViewModel(snapshot, report, ruleset, RootDn, onBack: () => { });

        // The table's ObjectName is, for every row, exactly Resolve(snapshot, PrimaryDn) — the SAME
        // call the WorkspaceViewModel's OnReportChanged feeds into the sidebar ViolationRowModel.
        foreach (var row in audit.Findings)
        {
            Assert.Equal(SubjectNameResolver.Resolve(snapshot, row.PrimaryDn), row.ObjectName);
        }

        // Direct parity assertions on the shared resolver (loaded subject, absent anchor, null snapshot).
        const string loaded = "CN=GG_Loaded_Parity,OU=Lab,DC=stub,DC=lab";
        var parityScope = new DirectorySnapshot();
        parityScope.AddObject(new AdObject { Dn = loaded, Kind = AdObjectKind.GlobalGroup, Name = "GG_Loaded_Parity" });
        const string absent = "CN=Vanished,DC=elsewhere,DC=lab";

        Assert.Equal("GG_Loaded_Parity", SubjectNameResolver.Resolve(parityScope, loaded));
        Assert.Equal(absent, SubjectNameResolver.Resolve(parityScope, absent)); // absent => DN
        Assert.Equal(loaded, SubjectNameResolver.Resolve(null, loaded));        // null snapshot => DN
    }

    // === (2) Default order == report / canonical order (SortColumn None) ========================

    /// <summary>
    /// With no explicit sort (<see cref="AuditSortColumn.None"/>, the default), <see cref="AuditViewModel.Findings"/>
    /// is in canonical report order — the rows' <c>ReportOrder</c> is 0,1,2,... and the <c>PrimaryDn</c>
    /// sequence matches <see cref="RuleReport.Violations"/> exactly. Proven over the REAL demo scope (the
    /// AP 3.2 19-finding baseline incl. the GG_Circle cycle) so the engine's order is exercised end to end.
    /// </summary>
    [AvaloniaFact(Timeout = 60_000)]
    public async Task DefaultOrder_IsReportOrder_OverTheDemoBaseline()
    {
        var (window, shell, workspace) = await DriveDemoWorkspaceAsync();

        var snapshot = workspace.Snapshot!;
        var ruleset = RulesetLoader.LoadDefault();
        var report = workspace.Report;

        using var audit = new AuditViewModel(snapshot, report, ruleset, workspace.RootDn, onBack: () => { });

        Assert.Equal(AuditSortColumn.None, audit.SortColumn); // the default

        // Rows are in report order: ReportOrder is the dense ascending 0..n-1, and the projected
        // (Severity, Message, PrimaryDn) sequence matches the report's violations 1:1.
        Assert.Equal(
            Enumerable.Range(0, report.Violations.Count).ToArray(),
            audit.Findings.Select(r => r.ReportOrder).ToArray());
        Assert.Equal(
            report.Violations.Select(v => (v.Severity, v.Message, v.PrimaryDn)).ToArray(),
            audit.Findings.Select(r => (r.Severity, r.Message, r.PrimaryDn)).ToArray());

        shell.Dispose();
        window.Close();
    }

    // === (3) Sort: severity desc default, object/rule asc->desc, re-sort stability ==============

    /// <summary>
    /// Sorting by severity (the <see cref="AuditViewModel.SortCommand"/> default direction is descending)
    /// floats Error rows to the top, then Warning, then Info — with the canonical report order as the
    /// tie-break inside each severity block. A repeat click flips to ascending (Info first).
    /// </summary>
    [Fact]
    public void Sort_BySeverity_DefaultsDescending_ErrorsFirst_ReportOrderTieBreak()
    {
        var (snapshot, ruleset) = LoadedScopeWithFindings();
        var report = RuleEngine.Evaluate(snapshot, ruleset);
        using var audit = new AuditViewModel(snapshot, report, ruleset, RootDn, onBack: () => { });

        // Pre-condition: the fixture carries a real mix of severities (else the sort pin is vacuous).
        var severities = audit.Findings.Select(r => r.Severity).Distinct().ToList();
        Assert.True(severities.Count > 1, "the fixture must carry more than one severity for a meaningful sort");

        audit.SortCommand.Execute(AuditSortColumn.Severity);

        Assert.Equal(AuditSortColumn.Severity, audit.SortColumn);
        Assert.True(audit.SortDescending, "severity defaults to descending (worst first)");

        // Descending by (int)Severity (Error=2 > Warning=1 > Info=0), report-order tie-break within block.
        Assert.Equal(
            audit.Findings.Select(r => r.ReportOrder).ToArray(),
            audit.Findings
                .OrderByDescending(r => (int)r.Severity).ThenBy(r => r.ReportOrder)
                .Select(r => r.ReportOrder).ToArray());
        Assert.Equal(RuleSeverity.Error, audit.Findings[0].Severity); // worst is on top

        // Repeat click flips direction to ascending (Info first).
        audit.SortCommand.Execute(AuditSortColumn.Severity);
        Assert.Equal(AuditSortColumn.Severity, audit.SortColumn);
        Assert.False(audit.SortDescending);
        Assert.Equal(
            audit.Findings.Select(r => r.ReportOrder).ToArray(),
            audit.Findings
                .OrderBy(r => (int)r.Severity).ThenBy(r => r.ReportOrder)
                .Select(r => r.ReportOrder).ToArray());
        Assert.Equal(RuleSeverity.Info, audit.Findings[0].Severity);
    }

    /// <summary>
    /// Sorting by Object name (or Rule class) is ascending on first click, descending on a repeat click,
    /// OrdinalIgnoreCase, with the report-order index as the deterministic tie-break.
    /// </summary>
    [Fact]
    public void Sort_ByObjectAndRule_AscThenDesc_OrdinalIgnoreCase_ReportOrderTieBreak()
    {
        var (snapshot, ruleset) = LoadedScopeWithFindings();
        var report = RuleEngine.Evaluate(snapshot, ruleset);
        using var audit = new AuditViewModel(snapshot, report, ruleset, RootDn, onBack: () => { });

        // --- ObjectName: first click ascending ---
        audit.SortCommand.Execute(AuditSortColumn.ObjectName);
        Assert.Equal(AuditSortColumn.ObjectName, audit.SortColumn);
        Assert.False(audit.SortDescending, "a new (non-severity) column defaults to ascending");
        Assert.Equal(
            audit.Findings.Select(r => r.ReportOrder).ToArray(),
            audit.Findings
                .OrderBy(r => r.ObjectName, StringComparer.OrdinalIgnoreCase).ThenBy(r => r.ReportOrder)
                .Select(r => r.ReportOrder).ToArray());

        // --- ObjectName: repeat click descending ---
        audit.SortCommand.Execute(AuditSortColumn.ObjectName);
        Assert.True(audit.SortDescending);
        Assert.Equal(
            audit.Findings.Select(r => r.ReportOrder).ToArray(),
            audit.Findings
                .OrderByDescending(r => r.ObjectName, StringComparer.OrdinalIgnoreCase).ThenBy(r => r.ReportOrder)
                .Select(r => r.ReportOrder).ToArray());

        // --- RuleClass: switch to a new column => ascending again ---
        audit.SortCommand.Execute(AuditSortColumn.RuleClass);
        Assert.Equal(AuditSortColumn.RuleClass, audit.SortColumn);
        Assert.False(audit.SortDescending);
        Assert.Equal(
            audit.Findings.Select(r => r.ReportOrder).ToArray(),
            audit.Findings
                .OrderBy(r => r.RuleClass, StringComparer.OrdinalIgnoreCase).ThenBy(r => r.ReportOrder)
                .Select(r => r.ReportOrder).ToArray());
    }

    /// <summary>
    /// Re-sort stability (the rank-on-row fix): sort by one column, then a DIFFERENT column, then BACK
    /// — the result is correct each time. Because <c>ReportOrder</c> is a stable rank captured at
    /// projection (independent of the live row order), re-sorting an already-reordered collection never
    /// corrupts the ordering. Drives the demo baseline so the tie-break is genuinely exercised.
    /// </summary>
    [AvaloniaFact(Timeout = 60_000)]
    public async Task Sort_ReSortAfterDifferentColumn_StaysCorrect_OverTheDemoBaseline()
    {
        var (window, shell, workspace) = await DriveDemoWorkspaceAsync();
        var snapshot = workspace.Snapshot!;
        var ruleset = RulesetLoader.LoadDefault();
        var report = workspace.Report;
        using var audit = new AuditViewModel(snapshot, report, ruleset, workspace.RootDn, onBack: () => { });

        // The expected orderings, computed once from the IMMUTABLE projection (snapshot the rows now —
        // ReportOrder/ObjectName/Severity never change), so the expectations cannot drift with the live
        // collection's current order.
        var rows = audit.Findings.ToList();
        var byObjectAsc = rows
            .OrderBy(r => r.ObjectName, StringComparer.OrdinalIgnoreCase).ThenBy(r => r.ReportOrder)
            .Select(r => r.ReportOrder).ToArray();
        var bySeverityDesc = rows
            .OrderByDescending(r => (int)r.Severity).ThenBy(r => r.ReportOrder)
            .Select(r => r.ReportOrder).ToArray();

        // 1) Sort by Object (asc).
        audit.SortCommand.Execute(AuditSortColumn.ObjectName);
        Assert.Equal(byObjectAsc, audit.Findings.Select(r => r.ReportOrder).ToArray());

        // 2) Sort by Severity (desc) — a DIFFERENT column over the now-reordered collection.
        audit.SortCommand.Execute(AuditSortColumn.Severity);
        Assert.Equal(bySeverityDesc, audit.Findings.Select(r => r.ReportOrder).ToArray());

        // 3) Back to Object (asc) — the SECOND differing sort must NOT corrupt order (the rank-on-row
        //    fix): the result is identical to step 1, byte for byte.
        audit.SortCommand.Execute(AuditSortColumn.ObjectName);
        Assert.Equal(byObjectAsc, audit.Findings.Select(r => r.ReportOrder).ToArray());

        shell.Dispose();
        window.Close();
    }

    // === (4) Multi-select: count/HasSelection, ToggleSelectAll, AllSelected, ClearSelection =====

    /// <summary>
    /// Toggling a row's <see cref="AuditFindingRowModel.IsSelected"/> re-tallies
    /// <see cref="AuditViewModel.SelectedCount"/>/<see cref="AuditViewModel.HasSelection"/>;
    /// <see cref="AuditViewModel.ToggleSelectAllCommand"/> selects all rows then (a second invoke) clears
    /// them; <see cref="AuditViewModel.AllSelected"/> reflects the state; and
    /// <see cref="AuditViewModel.ClearSelectionCommand"/> zeroes the selection.
    /// </summary>
    [Fact]
    public void MultiSelect_TogglesTally_SelectAllRoundTrips_ClearZeroes()
    {
        var (snapshot, ruleset) = LoadedScopeWithFindings();
        var report = RuleEngine.Evaluate(snapshot, ruleset);
        using var audit = new AuditViewModel(snapshot, report, ruleset, RootDn, onBack: () => { });
        var total = audit.Findings.Count;
        Assert.True(total >= 2, "need at least two rows to exercise partial selection");

        // Initial state: nothing selected.
        Assert.Equal(0, audit.SelectedCount);
        Assert.False(audit.HasSelection);
        Assert.False(audit.AllSelected);

        // Toggle one row: count == 1, HasSelection true, AllSelected false (partial).
        audit.Findings[0].IsSelected = true;
        Assert.Equal(1, audit.SelectedCount);
        Assert.True(audit.HasSelection);
        Assert.False(audit.AllSelected);

        // Toggle a second row, then deselect the first: count tracks each change.
        audit.Findings[1].IsSelected = true;
        Assert.Equal(2, audit.SelectedCount);
        audit.Findings[0].IsSelected = false;
        Assert.Equal(1, audit.SelectedCount);
        audit.Findings[1].IsSelected = false;
        Assert.Equal(0, audit.SelectedCount);
        Assert.False(audit.HasSelection);

        // ToggleSelectAll: selects every row => AllSelected true, count == total.
        audit.ToggleSelectAllCommand.Execute(null);
        Assert.Equal(total, audit.SelectedCount);
        Assert.True(audit.AllSelected);
        Assert.True(audit.HasSelection);
        Assert.All(audit.Findings, r => Assert.True(r.IsSelected));

        // ToggleSelectAll again: clears every row.
        audit.ToggleSelectAllCommand.Execute(null);
        Assert.Equal(0, audit.SelectedCount);
        Assert.False(audit.AllSelected);
        Assert.All(audit.Findings, r => Assert.False(r.IsSelected));

        // ClearSelection zeroes a non-empty selection.
        audit.Findings[0].IsSelected = true;
        Assert.Equal(1, audit.SelectedCount);
        audit.ClearSelectionCommand.Execute(null);
        Assert.Equal(0, audit.SelectedCount);
        Assert.False(audit.HasSelection);
        Assert.All(audit.Findings, r => Assert.False(r.IsSelected));
    }

    // === (5) ApplyRuleset rebuilds Findings, drops selection, no double-count from stale subs ===

    /// <summary>
    /// <see cref="AuditViewModel.ApplyRuleset"/> (with a flipped finding set) REBUILDS
    /// <see cref="AuditViewModel.Findings"/> in place, DROPS any prior selection
    /// (<see cref="AuditViewModel.SelectedCount"/> == 0), and the OLD rows' <c>PropertyChanged</c>
    /// subscriptions are detached so they don't double-count: toggling a NEW row tallies 1, not 2.
    /// The nesting rule is disabled so the finding set genuinely changes (fewer rows), making the
    /// rebuild observable rather than a no-op.
    /// </summary>
    [Fact]
    public void ApplyRuleset_RebuildsFindings_DropsSelection_AndDetachesStaleSubscriptions()
    {
        var (snapshot, ruleset) = LoadedScopeWithFindings();
        var report = RuleEngine.Evaluate(snapshot, ruleset);
        using var audit = new AuditViewModel(snapshot, report, ruleset, RootDn, onBack: () => { });

        var countBefore = audit.Findings.Count;
        Assert.True(countBefore > 0);

        // Select every row BEFORE the re-thread, and capture the old row instances.
        audit.ToggleSelectAllCommand.Execute(null);
        Assert.Equal(countBefore, audit.SelectedCount);
        var oldRows = audit.Findings.ToList();

        // Flip the ruleset: disable nesting (drops the nesting Error finding => one fewer row).
        var defaults = RulesetLoader.LoadDefault();
        var nestingOff = ruleset with { Nesting = defaults.Nesting with { Enabled = false } };
        var expectedAfter = RuleEngine.Evaluate(snapshot, nestingOff);

        audit.ApplyRuleset(nestingOff);

        // Rebuilt 1:1 with the flipped report, selection dropped to zero.
        Assert.Equal(expectedAfter.Violations.Count, audit.Findings.Count);
        Assert.Equal(countBefore - 1, audit.Findings.Count); // observable change (nesting row gone)
        Assert.Equal(0, audit.SelectedCount);
        Assert.False(audit.HasSelection);
        Assert.All(audit.Findings, r => Assert.False(r.IsSelected));

        // The new rows are different instances (a genuine rebuild, not a mutate-in-place).
        Assert.DoesNotContain(audit.Findings, r => oldRows.Contains(r));

        // The stale subscriptions are detached: toggling an OLD (now-orphaned) row must NOT bump the
        // count — the VM no longer listens to it.
        oldRows[0].IsSelected = true;
        Assert.Equal(0, audit.SelectedCount);

        // Toggling a NEW row tallies EXACTLY 1 (not 2 — no double-subscription leak).
        audit.Findings[0].IsSelected = true;
        Assert.Equal(1, audit.SelectedCount);
    }

    // === (6) ShowInGraphCommand invokes the Back callback =======================================

    /// <summary>
    /// <see cref="AuditViewModel.ShowInGraphCommand"/> ("Show in graph") routes through the same Back
    /// callback (returning to the workspace IS the graph view, WP5 v1). Pinned by a callback counter.
    /// </summary>
    [Fact]
    public void ShowInGraphCommand_InvokesTheBackCallback()
    {
        var (snapshot, ruleset) = LoadedScopeWithFindings();
        var report = RuleEngine.Evaluate(snapshot, ruleset);
        var backCalls = 0;
        using var audit = new AuditViewModel(snapshot, report, ruleset, RootDn, onBack: () => backCalls++);

        audit.ShowInGraphCommand.Execute(null);
        Assert.Equal(1, backCalls);

        // Idempotent-shaped: a second invoke fires it again (it is a plain pass-through).
        audit.ShowInGraphCommand.Execute(null);
        Assert.Equal(2, backCalls);
    }

    // === Helpers ===============================================================================

    /// <summary>The WP5b/WP5c findings fixture (re-stated so the fixtures stay independent): a
    /// fully-LOADED scope that trips the default ruleset's nesting (a DL with a direct User member) +
    /// naming (a badly-named GG) + empty-group rules — a real Error/Warning/Info mix. Returns the
    /// snapshot + the default ruleset.</summary>
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

    private static AdObject Group(string dn, AdObjectKind kind) => new()
    {
        Dn = dn,
        Kind = kind,
        Name = dn.Split(',')[0]["CN=".Length..],
    };

    /// <summary>Connect (demo) → pick the demo root OU → load, returning the shown window, the real
    /// shell, and the settled demo workspace (the demo root OU scope carries the AP 3.2 baseline incl.
    /// the GG_Circle_A↔GG_Circle_B cycle). Mirrors AuditHealthBandTests' drive idiom; injects a
    /// temp-dir UiStateStore (the #124 isolation seam — never touches real %APPDATA%).</summary>
    private static async Task<(MainWindow Window, ShellViewModel Shell, WorkspaceViewModel Workspace)> DriveDemoWorkspaceAsync()
    {
        var uiStateBase = System.IO.Directory
            .CreateTempSubdirectory("groupweaver-audit-table-uistate-").FullName;
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
