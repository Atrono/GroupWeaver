using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

using GroupWeaver.App.Rules;
using GroupWeaver.App.Settings;
using GroupWeaver.App.ViewModels;
using GroupWeaver.Core.Model;
using GroupWeaver.Core.Rules;

using Xunit;

namespace GroupWeaver.App.Tests;

/// <summary>
/// Pins the "persist view state" wiring on <see cref="AuditViewModel"/>: the optional
/// <see cref="AuditViewModel.UseUiStateStore"/> seam RESTORES the persisted Audit filters + sort on
/// install (from <see cref="UiStateStore.Load"/>) and PERSISTS every filter toggle / clear / sort
/// change back through the store (read-modify-write, best-effort). Mirrors the
/// <see cref="WorkspaceRailStateTests"/> temp-dir <see cref="UiStateStore"/> seam idiom (the #124
/// isolation lesson — never touches real <c>%APPDATA%</c>) and the <see cref="AuditFilterTests"/>
/// directly-constructed-VM unit pattern over the same hand-built loaded scope.
///
/// <para>The VM itself reads NO user-profile state except through the injected store, so these are
/// plain <see cref="FactAttribute"/>s — no headless UI. They cover: the restore round-trip (chips +
/// sort + table reflect the seeded state), persist-on-change (toggle / clear / sort write the five
/// audit fields AND leave the rail/theme fields intact), unparseable-name tolerance (a bad sort
/// column / severity / status is dropped, never throws), stale rule-id self-heal (an id absent from
/// the current report does not survive into an everything-hiding filter), and the un-wired no-op (an
/// audit without the seam neither restores nor persists, and writes no file).</para>
///
/// <para>Compares PROJECTIONS, never record/collection identity (rule-engine.md / data-model.md):
/// the persisted name lists are compared as sets / sequences, chips by their
/// (severity/label, IsActive) projections, and the table by its severity / rule-id membership.</para>
/// </summary>
public sealed class AuditViewStatePersistenceTests
{
    private const string RootDn = "OU=Lab,DC=stub,DC=lab";

    // === (2) Restore round-trip: a seeded store repopulates filters + sort on install ===============

    /// <summary>
    /// With a temp-dir <see cref="UiStateStore"/> seeded (severity=[Error], status=[Open] — the only
    /// status the untriaged fixture carries — a valid rule-class id, sort=Severity/desc), a freshly-built
    /// <see cref="AuditViewModel"/> + <see cref="AuditViewModel.UseUiStateStore"/> RESTORES that state: the
    /// matching severity chip + status chip + category row read active, <see cref="AuditViewModel.SortColumn"/>/
    /// <see cref="AuditViewModel.SortDescending"/> are set, the view is marked filtered, and the table
    /// reflects the AND'd filter (here Error severity AND the empty-group class is contradictory, so the
    /// restore lands on the "no matches" state — proving every restored axis was actually applied, not just
    /// the active flags). A concordant restore is covered separately below.
    /// </summary>
    [Fact]
    public void UseUiStateStore_RestoresSeveritySortAndActiveChips_FromSeededStore()
    {
        using var dir = new TempDir();

        // Seed the store as a prior session would have: an Error-severity filter, the Open status, the
        // empty-group rule class, sorted by Severity descending.
        new UiStateStore(dir.Path).Save(UiState.Default with
        {
            AuditSeverityFilter = new[] { "Error" },
            AuditStatusFilter = new[] { "Open" },
            AuditRuleClassFilter = new[] { RuleIds.EmptyGroup },
            AuditSortColumn = "Severity",
            AuditSortDescending = true,
        });

        var (snapshot, ruleset) = LoadedScopeWithFindings();
        var report = RuleEngine.Evaluate(snapshot, ruleset);
        using var audit = new AuditViewModel(snapshot, report, ruleset, RootDn, onBack: () => { });

        // Install the seam: this triggers the restore.
        audit.UseUiStateStore(new UiStateStore(dir.Path));

        // Sort restored.
        Assert.Equal(AuditSortColumn.Severity, audit.SortColumn);
        Assert.True(audit.SortDescending);

        // The restored chips/category read active (their IsActive mirrors the restored filter sets).
        Assert.True(audit.SeverityChips.Single(c => c.Severity == RuleSeverity.Error).IsActive);
        Assert.True(audit.StatusChips.Single(c => c.Label == "Open").IsActive);
        Assert.True(audit.Categories.Single(
            c => string.Equals(c.RuleId, RuleIds.EmptyGroup, StringComparison.OrdinalIgnoreCase)).IsActive);

        // The view is filtered (the restore actually applied the sets, not just flipped IsActive flags):
        // Error severity AND the empty-group class is a contradictory AND (empty-group findings are Info),
        // so the restore lands on the no-matches state — every restored axis was genuinely applied.
        Assert.True(audit.IsFiltered);
        Assert.True(audit.HasNoMatches);
        Assert.False(audit.IsAllClear);
    }

    /// <summary>
    /// A CONCORDANT restore (the empty-group rule class AND the Info severity — the two axes agree, since
    /// every empty-group finding is Info) repopulates a NON-empty filtered table: exactly the empty-group
    /// rows are visible, and the canonical "Showing N of M" summary reflects it. Proves the restore applies
    /// the persisted sets to the actual table view, not only to the chip active flags.
    /// </summary>
    [Fact]
    public void UseUiStateStore_ConcordantRestore_FiltersTheTableToTheRestoredSets()
    {
        using var dir = new TempDir();
        new UiStateStore(dir.Path).Save(UiState.Default with
        {
            AuditSeverityFilter = new[] { "Info" },
            AuditRuleClassFilter = new[] { RuleIds.EmptyGroup },
        });

        var (snapshot, ruleset) = LoadedScopeWithFindings();
        var report = RuleEngine.Evaluate(snapshot, ruleset);
        using var audit = new AuditViewModel(snapshot, report, ruleset, RootDn, onBack: () => { });
        var total = audit.TotalCount;
        var emptyGroupCount = audit.Categories.Single(
            c => string.Equals(c.RuleId, RuleIds.EmptyGroup, StringComparison.OrdinalIgnoreCase)).Count;
        Assert.True(emptyGroupCount > 0 && emptyGroupCount < total, "need a strict subset for a meaningful filter");

        audit.UseUiStateStore(new UiStateStore(dir.Path));

        Assert.True(audit.IsFiltered);
        Assert.False(audit.HasNoMatches);
        Assert.Equal(emptyGroupCount, audit.VisibleCount);
        Assert.All(audit.Findings, r => Assert.Equal(RuleSeverity.Info, r.Severity));
        Assert.All(audit.Findings, r => Assert.Equal(RuleIds.EmptyGroup, r.RuleId, StringComparer.OrdinalIgnoreCase));
        Assert.Equal($"Showing {emptyGroupCount} of {total}", audit.FilterSummary);
    }

    /// <summary>
    /// The restore does NOT write the just-loaded values straight back (the <c>_restoring</c> guard): a
    /// store seeded with a non-default audit state, then installed, is left BYTE-IDENTICAL after install —
    /// no persist-on-restore echo (mirrors the workspace ctor-seeding _seeding gate).
    /// </summary>
    [Fact]
    public void UseUiStateStore_Restore_DoesNotImmediatelyRewriteTheStore()
    {
        using var dir = new TempDir();
        var seeded = UiState.Default with
        {
            AuditSeverityFilter = new[] { "Warning" },
            AuditSortColumn = "ObjectName",
            AuditSortDescending = true,
            // A non-default rail field too, to prove the restore round-trips the WHOLE record unchanged.
            RailWidth = 401,
        };
        new UiStateStore(dir.Path).Save(seeded);

        var (snapshot, ruleset) = LoadedScopeWithFindings();
        var report = RuleEngine.Evaluate(snapshot, ruleset);
        using var audit = new AuditViewModel(snapshot, report, ruleset, RootDn, onBack: () => { });
        audit.UseUiStateStore(new UiStateStore(dir.Path));

        // The persisted state is untouched after the restore (no echo write of the loaded values).
        var after = new UiStateStore(dir.Path).Load();
        Assert.Equal(new[] { "Warning" }, after.AuditSeverityFilter);
        Assert.Equal("ObjectName", after.AuditSortColumn);
        Assert.True(after.AuditSortDescending);
        Assert.Equal(401, after.RailWidth);
    }

    // === (3) Persist-on-change: toggle / clear / sort write the audit fields, keep rail/theme =======

    /// <summary>
    /// With the seam installed, <see cref="AuditViewModel.ToggleFilterCommand"/> WRITES the toggled
    /// severity to the store's <see cref="UiState.AuditSeverityFilter"/> (reload the UiState to confirm),
    /// and toggling it OFF clears it back to empty. The write is a read-modify-write of only the audit
    /// fields.
    /// </summary>
    [Fact]
    public void ToggleFilter_WithSeam_PersistsTheSeverityFilter_AndTogglingOffClearsIt()
    {
        using var dir = new TempDir();
        var (snapshot, ruleset) = LoadedScopeWithFindings();
        var report = RuleEngine.Evaluate(snapshot, ruleset);
        using var audit = new AuditViewModel(snapshot, report, ruleset, RootDn, onBack: () => { });
        audit.UseUiStateStore(new UiStateStore(dir.Path));

        var errorChip = audit.SeverityChips.Single(c => c.Severity == RuleSeverity.Error);

        // Toggle ON: the store now records the Error severity.
        audit.ToggleFilterCommand.Execute(errorChip);
        Assert.Equal(new[] { "Error" }, new UiStateStore(dir.Path).Load().AuditSeverityFilter);

        // Toggle OFF: the store records an empty severity filter again.
        audit.ToggleFilterCommand.Execute(errorChip);
        Assert.Empty(new UiStateStore(dir.Path).Load().AuditSeverityFilter);
    }

    /// <summary>
    /// A category-row toggle (the rule-class axis — WP-C/#180) and a status-chip toggle both PERSIST their
    /// axis to the store. Asserts the rule-class id and status name lists round-trip via a reload (compared
    /// OrdinalIgnoreCase for the rule-class axis, which is case-insensitively keyed).
    /// </summary>
    [Fact]
    public void ToggleCategoryAndStatus_WithSeam_PersistTheRuleClassAndStatusFilters()
    {
        using var dir = new TempDir();
        var (snapshot, ruleset) = LoadedScopeWithFindings();
        var report = RuleEngine.Evaluate(snapshot, ruleset);
        using var audit = new AuditViewModel(snapshot, report, ruleset, RootDn, onBack: () => { });
        audit.UseUiStateStore(new UiStateStore(dir.Path));

        var emptyGroupRow = audit.Categories.Single(
            c => string.Equals(c.RuleId, RuleIds.EmptyGroup, StringComparison.OrdinalIgnoreCase));
        var openChip = audit.StatusChips.Single(c => c.Label == "Open");

        audit.ToggleCategoryCommand.Execute(emptyGroupRow);
        audit.ToggleFilterCommand.Execute(openChip);

        var loaded = new UiStateStore(dir.Path).Load();
        Assert.Equal(
            new[] { RuleIds.EmptyGroup },
            loaded.AuditRuleClassFilter.ToArray(),
            StringComparer.OrdinalIgnoreCase);
        Assert.Equal(new[] { "Open" }, loaded.AuditStatusFilter);
    }

    /// <summary>
    /// A sort change (<see cref="AuditViewModel.SortCommand"/>) PERSISTS the column + direction: sorting by
    /// Severity records "Severity"/descending (severity defaults to descending), and clicking the same
    /// column again flips the persisted direction. Drives the public Sort command so the
    /// <see cref="AuditViewModel"/>'s OnSortColumnChanged/OnSortDescendingChanged persist hooks fire.
    /// </summary>
    [Fact]
    public void Sort_WithSeam_PersistsTheSortColumnAndDirection()
    {
        using var dir = new TempDir();
        var (snapshot, ruleset) = LoadedScopeWithFindings();
        var report = RuleEngine.Evaluate(snapshot, ruleset);
        using var audit = new AuditViewModel(snapshot, report, ruleset, RootDn, onBack: () => { });
        audit.UseUiStateStore(new UiStateStore(dir.Path));

        // Sort by Severity (defaults to descending so the worst sort to the top).
        audit.SortCommand.Execute(AuditSortColumn.Severity);
        var afterFirst = new UiStateStore(dir.Path).Load();
        Assert.Equal("Severity", afterFirst.AuditSortColumn);
        Assert.True(afterFirst.AuditSortDescending);

        // Click the same column again: the direction flips, and the flip persists.
        audit.SortCommand.Execute(AuditSortColumn.Severity);
        var afterSecond = new UiStateStore(dir.Path).Load();
        Assert.Equal("Severity", afterSecond.AuditSortColumn);
        Assert.False(afterSecond.AuditSortDescending);
    }

    /// <summary>
    /// <see cref="AuditViewModel.ClearFiltersCommand"/> PERSISTS the cleared state: after filters are
    /// applied + persisted, ClearFilters writes empty filter lists back to the store.
    /// </summary>
    [Fact]
    public void ClearFilters_WithSeam_PersistsTheClearedState()
    {
        using var dir = new TempDir();
        var (snapshot, ruleset) = LoadedScopeWithFindings();
        var report = RuleEngine.Evaluate(snapshot, ruleset);
        using var audit = new AuditViewModel(snapshot, report, ruleset, RootDn, onBack: () => { });
        audit.UseUiStateStore(new UiStateStore(dir.Path));

        // Apply one facet on each axis, then confirm the store recorded them.
        audit.ToggleFilterCommand.Execute(audit.SeverityChips.Single(c => c.Severity == RuleSeverity.Error));
        audit.ToggleFilterCommand.Execute(audit.StatusChips.Single(c => c.Label == "Open"));
        audit.ToggleCategoryCommand.Execute(audit.Categories[0]);
        var beforeClear = new UiStateStore(dir.Path).Load();
        Assert.NotEmpty(beforeClear.AuditSeverityFilter);
        Assert.NotEmpty(beforeClear.AuditStatusFilter);
        Assert.NotEmpty(beforeClear.AuditRuleClassFilter);

        audit.ClearFiltersCommand.Execute(null);

        var afterClear = new UiStateStore(dir.Path).Load();
        Assert.Empty(afterClear.AuditSeverityFilter);
        Assert.Empty(afterClear.AuditStatusFilter);
        Assert.Empty(afterClear.AuditRuleClassFilter);
    }

    /// <summary>
    /// A filter persist is a read-MODIFY-write that touches ONLY the five audit fields: seed a non-default
    /// <see cref="UiState.RailWidth"/> + <see cref="UiState.Theme"/> (the rail/theme own those), install the
    /// seam, toggle a filter, and assert the rail/theme fields SURVIVE in the persisted state (the audit VM
    /// never clobbers another owner's fields — the PersistView read-modify-write contract).
    /// </summary>
    [Fact]
    public void Persist_DoesNotClobberRailOrThemeFields()
    {
        using var dir = new TempDir();

        // A prior rail/theme session persisted non-default RailWidth + Theme (+ collapsed) — the fields
        // the rail/theme writers own. The audit's PersistView must never overwrite them.
        new UiStateStore(dir.Path).Save(new UiState(455, true) { Theme = "Light", RailFindingsFraction = 0.4 });

        var (snapshot, ruleset) = LoadedScopeWithFindings();
        var report = RuleEngine.Evaluate(snapshot, ruleset);
        using var audit = new AuditViewModel(snapshot, report, ruleset, RootDn, onBack: () => { });
        audit.UseUiStateStore(new UiStateStore(dir.Path));

        audit.ToggleFilterCommand.Execute(audit.SeverityChips.Single(c => c.Severity == RuleSeverity.Error));

        var loaded = new UiStateStore(dir.Path).Load();
        // The audit field landed …
        Assert.Equal(new[] { "Error" }, loaded.AuditSeverityFilter);
        // … and the rail/theme fields survived the audit-only write.
        Assert.Equal(455, loaded.RailWidth);
        Assert.True(loaded.RailCollapsed);
        Assert.Equal("Light", loaded.Theme);
        Assert.Equal(0.4, loaded.RailFindingsFraction);
    }

    // === (4) Unparseable tolerance: bad names / sort column are dropped, never throw ================

    /// <summary>
    /// A persisted <see cref="UiState.AuditSortColumn"/> that no longer parses ("Bogus") restores to
    /// <see cref="AuditSortColumn.None"/> (the canonical order), and a bogus severity / status name is
    /// dropped from the restored filter — the restore NEVER throws (the never-throw load contract). A VALID
    /// name alongside a bogus one in the same list survives, proving only the bad entry is skipped.
    /// </summary>
    [Fact]
    public void UseUiStateStore_UnparseableNamesAndSortColumn_AreDroppedNotThrown()
    {
        using var dir = new TempDir();
        new UiStateStore(dir.Path).Save(UiState.Default with
        {
            // One valid + one bogus severity name; one bogus status name; a bogus sort column.
            AuditSeverityFilter = new[] { "Error", "Bogus" },
            AuditStatusFilter = new[] { "NotAStatus" },
            AuditSortColumn = "Bogus",
            AuditSortDescending = true,
        });

        var (snapshot, ruleset) = LoadedScopeWithFindings();
        var report = RuleEngine.Evaluate(snapshot, ruleset);
        using var audit = new AuditViewModel(snapshot, report, ruleset, RootDn, onBack: () => { });

        // The restore must not throw on any of the bad values.
        var exception = Record.Exception(() => audit.UseUiStateStore(new UiStateStore(dir.Path)));
        Assert.Null(exception);

        // The bogus sort column fell back to None.
        Assert.Equal(AuditSortColumn.None, audit.SortColumn);

        // The valid Error severity survived; the bogus one was dropped (only the Error chip is active).
        Assert.True(audit.SeverityChips.Single(c => c.Severity == RuleSeverity.Error).IsActive);
        Assert.False(audit.SeverityChips.Single(c => c.Severity == RuleSeverity.Warning).IsActive);
        Assert.False(audit.SeverityChips.Single(c => c.Severity == RuleSeverity.Info).IsActive);

        // The bogus status name was dropped entirely — no status chip is active.
        Assert.All(audit.StatusChips, c => Assert.False(c.IsActive));

        // The applied view is constrained to Error rows only (the valid axis is genuinely in effect).
        Assert.True(audit.IsFiltered);
        Assert.All(audit.Findings, r => Assert.Equal(RuleSeverity.Error, r.Severity));
    }

    // === (5) Stale rule-id self-heal: an id absent from the report is pruned, not all-hiding =========

    /// <summary>
    /// A persisted rule-class id that is ABSENT from the current report self-heals: it does NOT survive
    /// into an everything-hiding filter. Seeds the store with the NESTING rule id but evaluates a ruleset
    /// with nesting DISABLED (so no nesting finding exists), then restores. The stale id is pruned by the
    /// <see cref="AuditViewModel"/>'s RebuildFilterChips prune, so the view is NOT filtered down to nothing
    /// (a surviving stale id would AND every row out, leaving an empty table) — the full list shows and the
    /// rule-class axis carries no active constraint.
    /// </summary>
    [Fact]
    public void UseUiStateStore_StaleRuleClassId_AbsentFromReport_IsPruned_NotAllHiding()
    {
        using var dir = new TempDir();

        // Seed a rule-class filter pinned on NESTING.
        new UiStateStore(dir.Path).Save(UiState.Default with
        {
            AuditRuleClassFilter = new[] { RuleIds.Nesting },
        });

        // Evaluate over a ruleset with NESTING DISABLED — so the report carries no nesting finding and the
        // nesting rule id is stale relative to this report.
        var (snapshot, baseRuleset) = LoadedScopeWithFindings();
        var defaults = RulesetLoader.LoadDefault();
        var nestingOff = baseRuleset with { Nesting = defaults.Nesting with { Enabled = false } };
        var report = RuleEngine.Evaluate(snapshot, nestingOff);
        using var audit = new AuditViewModel(snapshot, report, nestingOff, RootDn, onBack: () => { });

        // Pre-condition: there ARE findings (warning + info), just none from nesting.
        Assert.True(audit.TotalCount > 0, "the fixture must still produce non-nesting findings");
        Assert.DoesNotContain(
            audit.Categories,
            c => string.Equals(c.RuleId, RuleIds.Nesting, StringComparison.OrdinalIgnoreCase));

        audit.UseUiStateStore(new UiStateStore(dir.Path));

        // The stale nesting id was PRUNED — the rule-class axis imposes no constraint, so the full list
        // shows (a surviving stale id would have AND'd every row out => HasNoMatches / empty table).
        Assert.False(audit.IsFiltered);
        Assert.False(audit.HasNoMatches);
        Assert.Equal(audit.TotalCount, audit.VisibleCount);
        Assert.NotEmpty(audit.Findings);
        Assert.DoesNotContain(
            audit.Categories,
            c => string.Equals(c.RuleId, RuleIds.Nesting, StringComparison.OrdinalIgnoreCase) && c.IsActive);
    }

    // === (6) Un-wired no-op: no seam => no restore, no persist, no file =============================

    /// <summary>
    /// An <see cref="AuditViewModel"/> WITHOUT <see cref="AuditViewModel.UseUiStateStore"/> neither
    /// restores nor persists: it starts at the default (no filter / canonical order) regardless of any
    /// on-disk state, and a filter toggle / sort change writes NO file (the existing test ctors stay valid
    /// — the un-wired audit behaves exactly as before). Even when a populated ui-state.json exists on the
    /// SAME path a future store would use, the un-wired VM ignores it entirely.
    /// </summary>
    [Fact]
    public void NoSeam_NeitherRestoresNorPersists_AndWritesNoFile()
    {
        using var dir = new TempDir();

        // A populated ui-state.json sits at the canonical path — but no seam is installed, so the VM must
        // ignore it (no restore) and never write to it (no persist).
        var probeStore = new UiStateStore(dir.Path);
        probeStore.Save(UiState.Default with
        {
            AuditSeverityFilter = new[] { "Error" },
            AuditSortColumn = "Severity",
            AuditSortDescending = true,
        });
        var beforeBytes = File.ReadAllBytes(probeStore.StatePath);

        var (snapshot, ruleset) = LoadedScopeWithFindings();
        var report = RuleEngine.Evaluate(snapshot, ruleset);
        using var audit = new AuditViewModel(snapshot, report, ruleset, RootDn, onBack: () => { });

        // No restore: the VM starts at the defaults despite the populated file.
        Assert.Equal(AuditSortColumn.None, audit.SortColumn);
        Assert.False(audit.SortDescending);
        Assert.False(audit.IsFiltered);
        Assert.All(audit.SeverityChips, c => Assert.False(c.IsActive));

        // No persist: toggling a filter + changing the sort must not touch the on-disk file.
        audit.ToggleFilterCommand.Execute(audit.SeverityChips.Single(c => c.Severity == RuleSeverity.Warning));
        audit.SortCommand.Execute(AuditSortColumn.ObjectName);
        audit.ClearFiltersCommand.Execute(null);

        // The file is byte-identical to what the probe wrote — the un-wired VM never persisted.
        Assert.Equal(beforeBytes, File.ReadAllBytes(probeStore.StatePath));
    }

    [Fact]
    public void NoSeam_WithNoFileOnDisk_NeverCreatesOne()
    {
        using var dir = new TempDir();
        var probeStore = new UiStateStore(dir.Path);
        Assert.False(File.Exists(probeStore.StatePath));

        var (snapshot, ruleset) = LoadedScopeWithFindings();
        var report = RuleEngine.Evaluate(snapshot, ruleset);
        using var audit = new AuditViewModel(snapshot, report, ruleset, RootDn, onBack: () => { });

        // Exercise every persist-trigger with no seam installed.
        audit.ToggleFilterCommand.Execute(audit.SeverityChips.Single(c => c.Severity == RuleSeverity.Error));
        audit.ToggleCategoryCommand.Execute(audit.Categories[0]);
        audit.SortCommand.Execute(AuditSortColumn.Severity);
        audit.ClearFiltersCommand.Execute(null);

        // No file was ever created — the un-wired audit is fully inert toward persistence.
        Assert.False(File.Exists(probeStore.StatePath));
    }

    // === Helpers ===================================================================================

    /// <summary>The shared findings fixture (re-stated, matching <see cref="AuditFilterTests"/>): a
    /// fully-LOADED scope tripping the default ruleset's nesting (a DL with a direct User member) + naming
    /// (a badly-named GG) + empty-group rules — a real Error/Warning/Info mix across &gt;1 rule class.
    /// Returns the snapshot + the default ruleset.</summary>
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

    private sealed class TempDir : IDisposable
    {
        public string Path { get; } =
            Directory.CreateTempSubdirectory("groupweaver-audit-viewstate-tests-").FullName;

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch (IOException)
            {
                // Best-effort cleanup; never fail a test over temp-dir teardown.
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}
