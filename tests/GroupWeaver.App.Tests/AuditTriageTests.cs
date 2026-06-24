using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

using Avalonia.Headless.XUnit;
using Avalonia.Threading;

using GroupWeaver.App.Rules;
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
/// Pins the WP5e (#158 / ADR-028) audit-triage behavior end to end: the shell's
/// <see cref="ShellViewModel.ApplyTriage"/> write path routed through the single
/// <c>SettingsViewModel</c> gate, the <see cref="TriageEntry"/> tag grammar + DN escaping, the
/// <see cref="AuditViewModel"/> would-be table (triaged rows stay listed) vs. the LIVE report/health
/// (triaged findings drop), and the Untriage reversal.
///
/// <para><b>Acknowledge and Suppress are equal engine strength</b> (ADR-028): both append an ordinary
/// dn-mode global-ignore entry, so both drop the finding from the LIVE <see cref="RuleEngine.Evaluate"/>
/// report and raise health; the ONLY difference is the entry note tag (<c>[ack]</c> vs
/// <c>[suppress]</c>), a human annotation of intent. The would-be table reads each row's
/// <see cref="TriageStatus"/> from the LIVE ruleset's tagged entries, so a triaged row stays listed
/// (visible + reversible) while a PLAIN (untagged) ignore entry removes the finding from the table
/// entirely.</para>
///
/// <para><b>No AD write</b> (the non-negotiable read-only invariant): the triage write path touches
/// ONLY the ruleset file (via the gate's atomic temp+move to the injected
/// <see cref="RulesetLocator.UserRulesetPath"/>) plus in-memory state — never Active Directory. The
/// shell-gate tests prove the write lands exactly there and the gate is the sole writer.</para>
///
/// <para><b>Test-isolation seam (lab-environment.md / the #124 lesson):</b> every shell-driven case
/// injects a temp-dir <see cref="UiStateStore"/> AND a temp-dir <see cref="RulesetLocator"/>, so
/// nothing ever reads/writes real <c>%APPDATA%</c> (a persisted <c>RailCollapsed:true</c> on this box
/// would otherwise starve view realization; a real <c>ruleset.jsonc</c> would pollute the dev box).</para>
///
/// <para>Compares PROJECTIONS, never record/collection identity (rule-engine.md / data-model.md):
/// findings are compared by their (RuleId, Severity, PrimaryDn) projection.</para>
/// </summary>
public sealed class AuditTriageTests
{
    private static readonly WebView2RuntimeStatus Present = new(IsInstalled: true, Version: "test");

    private const string RootDn = "OU=Lab,DC=stub,DC=lab";

    // The badly-named GG carries BOTH a naming Warning and an empty-group Info finding (it is the
    // empty, non-conventional GG in LoadedScopeWithFindings). Its DN is the triage subject we drive.
    private const string GgBadNameDn = "CN=NotAConventionName,OU=Lab,DC=stub,DC=lab";

    // === (1) Suppress a finding: appended tagged entry, finding drops from live report, score rises,
    //         row stays listed as Suppressed in the would-be table ===================================

    /// <summary>
    /// Suppressing a selected finding routes through the shell gate, APPENDS a <c>[suppress]</c>-tagged
    /// dn-mode ignore entry for the finding's escaped DN, DROPS the finding from the LIVE report (so the
    /// audit health Score RISES), and the row STAYS LISTED in the would-be table with
    /// <see cref="TriageStatus.Suppressed"/>. Acknowledge is the equal-strength twin (covered in #3).
    /// </summary>
    [AvaloniaFact(Timeout = 60_000)]
    public async Task SuppressSelected_AppendsTaggedEntry_DropsFindingFromLiveReport_RaisesScore_RowStaysListedSuppressed()
    {
        var (window, shell, _, audit) = await DriveToArmedAuditAsync();

        // Baseline: the badly-named GG is a LIVE naming finding; capture the pre-triage score.
        var scoreBefore = audit.Score;
        Assert.Contains(audit.Findings, r => IsNamingFindingFor(r, GgBadNameDn));
        Assert.All(audit.Findings, r => Assert.Equal(TriageStatus.Open, r.Status));

        // Select the naming row and Suppress it through the bulk command (the shell gate appends the
        // tagged entry, re-threads audit + parked workspace via RulesetApplied).
        var namingRow = audit.Findings.First(r => IsNamingFindingFor(r, GgBadNameDn));
        namingRow.IsSelected = true;
        audit.SuppressSelectedCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();

        // The would-be table still LISTS the row (visible + reversible) but it now reads Suppressed.
        var afterRow = audit.Findings.First(r => IsNamingFindingFor(r, GgBadNameDn));
        Assert.Equal(TriageStatus.Suppressed, afterRow.Status);
        Assert.True(afterRow.IsTriaged);
        Assert.Equal("Suppressed", afterRow.StatusLabel);

        // The LIVE health report DROPPED the finding => the score strictly rose (fewer findings). The
        // audit's Summary is computed from the LIVE report, so a higher score IS the live-report drop.
        Assert.True(audit.Score > scoreBefore, "suppressing a finding must raise the live health score");

        shell.Dispose();
        window.Close();
    }

    // === (2) Reversal: Untriage removes the entry, finding reopens (Open + back in the live report) ==

    /// <summary>
    /// Un-triage REMOVES the matching tagged entry (by escaped DN + tag) so the finding REAPPEARS as
    /// <see cref="TriageStatus.Open"/> and RE-ENTERS the live report (score falls back). Drives a full
    /// Suppress → Untriage round-trip and asserts the score returns to the pre-triage value.
    /// </summary>
    [AvaloniaFact(Timeout = 60_000)]
    public async Task UntriageSelected_RemovesTheEntry_FindingReopensOpen_AndReEntersTheLiveReport()
    {
        var (window, shell, workspace, audit) = await DriveToArmedAuditAsync();

        var scoreClean = audit.Score;
        var namingRow = audit.Findings.First(r => IsNamingFindingFor(r, GgBadNameDn));

        // Suppress first.
        namingRow.IsSelected = true;
        audit.SuppressSelectedCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        var suppressedRow = audit.Findings.First(r => IsNamingFindingFor(r, GgBadNameDn));
        Assert.Equal(TriageStatus.Suppressed, suppressedRow.Status);
        var scoreSuppressed = audit.Score;
        Assert.True(scoreSuppressed > scoreClean);

        // Reverse it: select the (now triaged) row and Untriage.
        suppressedRow.IsSelected = true;
        audit.UntriageSelectedCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();

        // The finding reopened: row is Open again, and the score fell back to the pre-triage value.
        var reopenedRow = audit.Findings.First(r => IsNamingFindingFor(r, GgBadNameDn));
        Assert.Equal(TriageStatus.Open, reopenedRow.Status);
        Assert.False(reopenedRow.IsTriaged);
        Assert.Equal(scoreClean, audit.Score);

        // The finding is back in the PARKED workspace's LIVE report too — the shell re-threads the
        // parked workspace alongside the audit step (ADR-028: graph halos + violations rail update even
        // though Audit is showing). That re-thread is async (ApplyRulesetAsync), so pump until it settles.
        await PumpUntilAsync(() =>
            workspace.Report.Violations.Any(
                v => Dn.Comparer.Equals(v.Dns[0], GgBadNameDn) && v.Severity == RuleSeverity.Warning));
        Assert.Contains(
            workspace.Report.Violations,
            v => Dn.Comparer.Equals(v.Dns[0], GgBadNameDn) && v.Severity == RuleSeverity.Warning);

        shell.Dispose();
        window.Close();
    }

    // === (3) Ack and Suppress are equal engine strength (both drop the finding) ======================

    /// <summary>
    /// Acknowledge and Suppress are EQUAL engine strength (ADR-028): BOTH drop the finding from the
    /// LIVE report and raise the score identically; the ONLY observable difference is the row's
    /// <see cref="TriageStatus"/> (Acknowledged vs Suppressed) — driven by the entry note tag. Drives
    /// Acknowledge over a fresh audit and asserts the same score lift Suppress produced, with the row
    /// reading Acknowledged.
    /// </summary>
    [AvaloniaFact(Timeout = 60_000)]
    public async Task AcknowledgeAndSuppress_AreEqualEngineStrength_OnlyTheStatusTagDiffers()
    {
        // Run Suppress in one audit and Acknowledge in another, over the SAME hand-built scope, and
        // assert the score lift is identical (equal engine strength) while the status differs.
        var (winS, shellS, _, auditS) = await DriveToArmedAuditAsync();
        var cleanScore = auditS.Score;
        var suppRow = auditS.Findings.First(r => IsNamingFindingFor(r, GgBadNameDn));
        suppRow.IsSelected = true;
        auditS.SuppressSelectedCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        var suppressedScore = auditS.Score;
        Assert.Equal(TriageStatus.Suppressed, auditS.Findings.First(r => IsNamingFindingFor(r, GgBadNameDn)).Status);
        shellS.Dispose();
        winS.Close();

        var (winA, shellA, _, auditA) = await DriveToArmedAuditAsync();
        Assert.Equal(cleanScore, auditA.Score); // same clean baseline (same scope)
        var ackRow = auditA.Findings.First(r => IsNamingFindingFor(r, GgBadNameDn));
        ackRow.IsSelected = true;
        auditA.AcknowledgeSelectedCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        var acknowledgedScore = auditA.Score;
        Assert.Equal(TriageStatus.Acknowledged, auditA.Findings.First(r => IsNamingFindingFor(r, GgBadNameDn)).Status);

        // Equal engine strength: the SAME finding dropped, the SAME score lift, only the tag differs.
        Assert.Equal(suppressedScore, acknowledgedScore);
        Assert.True(acknowledgedScore > cleanScore, "acknowledging a finding must also drop it from the live report");

        shellA.Dispose();
        winA.Close();
    }

    // === (4) DN escaping: a DN with a literal '*' round-trips so the suppress entry covers ITS finding =

    /// <summary>
    /// <see cref="TriageEntry.Escape"/> on a DN containing a literal glob metacharacter (<c>*</c>)
    /// produces a stored <see cref="MatchEntry.Dn"/> whose <see cref="MatchEntry.MatchesDn"/> STILL
    /// matches that exact DN — so a triage entry built for a pathological-DN finding genuinely covers
    /// its OWN object (the round-trip the triage write path relies on). A plain DN is returned
    /// unchanged and matches itself but not a sibling.
    ///
    /// <para><b>Dialect limitation, pinned honestly:</b> the ADR-008 glob dialect has NO literal-escape
    /// for <c>*</c>/<c>?</c> (only <c>*</c>=any-run and <c>?</c>=one-char are special), so it is
    /// IMPOSSIBLE to express a glob that matches a <c>*</c>-bearing DN and NOTHING else — the escape is
    /// a documented fail-safe ("DNs containing <c>*</c>/<c>?</c> are pathological", TriageEntry.cs). We
    /// therefore pin the load-bearing property (the entry covers its own finding's DN) and do NOT assert
    /// the impossible "matches ONLY that DN"; real AD DNs never contain a bare <c>*</c>/<c>?</c>.</para>
    /// </summary>
    [Fact]
    public void Escape_OnDnWithLiteralStar_RoundTripsViaMatchEntry_CoveringTheExactDn()
    {
        // Plain DN: unchanged, matches itself, does NOT match a sibling.
        const string plain = "CN=GG_Plain,OU=Lab,DC=stub,DC=lab";
        Assert.Equal(plain, TriageEntry.Escape(plain));
        var plainEntry = TriageEntry.Build(new TriageRequest(TriageEntry.Escape(plain), RuleIds.EmptyGroup, TriageKind.Suppress, null));
        Assert.True(plainEntry.MatchesDn(plain), "the plain entry must cover its own DN");
        Assert.False(plainEntry.MatchesDn("CN=GG_Other,OU=Lab,DC=stub,DC=lab"), "a plain DN entry must not cover a sibling");

        // Pathological DN with a literal '*': the escaped glob STILL covers the exact DN (the round-trip
        // the write path needs). The stored Dn is the escaped spelling, not the raw DN.
        const string starDn = "CN=GG_a*b,OU=Lab,DC=stub,DC=lab";
        var escaped = TriageEntry.Escape(starDn);
        Assert.NotEqual(starDn, escaped); // a '*' DN IS rewritten (the fail-safe prefix)
        var starEntry = TriageEntry.Build(new TriageRequest(escaped, RuleIds.EmptyGroup, TriageKind.Suppress, null));
        Assert.Equal(escaped, starEntry.Dn); // the entry stores the escaped spelling verbatim
        Assert.True(
            starEntry.MatchesDn(starDn),
            "the escaped entry for a '*'-bearing DN must still cover that exact DN via GlobMatcher");
    }

    // === (5) Status detection: tagged => triaged + listed; plain ignore => removed from would-be table =

    /// <summary>
    /// A would-be finding whose LIVE ruleset carries a <c>[suppress]</c>-tagged ignore entry for its
    /// ESCAPED DN reads <see cref="TriageStatus.Suppressed"/> AND stays in the would-be table; a PLAIN
    /// (untagged) ignore entry for the same DN removes the finding from the would-be table ENTIRELY
    /// (it is a genuine, non-reversible ignore, not a triage). Unit-level over a directly-constructed
    /// <see cref="AuditViewModel"/> so the projection logic is pinned without driving the whole shell.
    /// </summary>
    [Fact]
    public void Status_TaggedEntryListsRowSuppressed_PlainIgnoreRemovesItFromTheWouldBeTable()
    {
        var (snapshot, baseRuleset) = LoadedScopeWithFindings();
        var escaped = TriageEntry.Escape(GgBadNameDn);

        // --- Tagged [suppress] entry: row stays listed, reads Suppressed ---
        // The rule id rides along only for the note; matching is by escaped DN + tag (naming ids are
        // user-chosen, e.g. the default's "naming-gg" — there is no RuleIds.Naming constant).
        var taggedEntry = TriageEntry.Build(new TriageRequest(escaped, "naming-gg", TriageKind.Suppress, null));
        var taggedRuleset = baseRuleset with { Ignore = baseRuleset.Ignore.Append(taggedEntry).ToList() };
        var taggedLiveReport = RuleEngine.Evaluate(snapshot, taggedRuleset);
        using var taggedAudit = new AuditViewModel(snapshot, taggedLiveReport, taggedRuleset, RootDn, onBack: () => { });

        var taggedRow = taggedAudit.Findings.FirstOrDefault(r => IsNamingFindingFor(r, GgBadNameDn));
        Assert.NotNull(taggedRow); // the would-be table keeps the triaged row listed
        Assert.Equal(TriageStatus.Suppressed, taggedRow!.Status);
        // And it is genuinely OUT of the live report (suppressed for real).
        Assert.DoesNotContain(
            taggedLiveReport.Violations,
            v => Dn.Comparer.Equals(v.Dns[0], GgBadNameDn) && v.Severity == RuleSeverity.Warning);

        // --- Plain (untagged) ignore entry for the SAME DN: finding vanishes from the would-be table ---
        var plainEntry = new MatchEntry { Dn = escaped, Note = "a plain operator ignore, not triage" };
        var plainRuleset = baseRuleset with { Ignore = baseRuleset.Ignore.Append(plainEntry).ToList() };
        var plainLiveReport = RuleEngine.Evaluate(snapshot, plainRuleset);
        using var plainAudit = new AuditViewModel(snapshot, plainLiveReport, plainRuleset, RootDn, onBack: () => { });

        // A plain ignore is NOT a triage: the would-be report (base ignore = entries WITHOUT a tag)
        // still contains the plain entry, so the naming finding is removed from the table entirely.
        Assert.DoesNotContain(plainAudit.Findings, r => IsNamingFindingFor(r, GgBadNameDn));
        // No row => no Suppressed status to read; the finding is genuinely gone, not merely muted.
        Assert.DoesNotContain(
            plainLiveReport.Violations,
            v => Dn.Comparer.Equals(v.Dns[0], GgBadNameDn) && v.Severity == RuleSeverity.Warning);
    }

    // === (6) No-AD-write: the triage write path touches ONLY the ruleset file + in-memory state ======

    /// <summary>
    /// The triage write path is the read-only product's sanctioned NON-AD writer (CLAUDE.md): a
    /// Suppress writes ONLY the injected <see cref="RulesetLocator.UserRulesetPath"/> ruleset file (the
    /// gate's atomic temp+move) plus in-memory state — never Active Directory. Proven by (a) the file
    /// did NOT exist before, EXISTS after the Suppress, and parses back to a ruleset carrying the
    /// tagged entry (the gate is the sole writer), and (b) the temp-dir locator base is the ONLY thing
    /// the write touched (the shell holds a stub provider that exposes no mutation API at all — the
    /// product has no <c>Set-AD</c>/<c>CommitChanges</c> seam by construction).
    /// </summary>
    [AvaloniaFact(Timeout = 60_000)]
    public async Task ApplyTriage_WritesOnlyTheRulesetFile_NeverAd()
    {
        var rulesetBase = Directory.CreateTempSubdirectory("groupweaver-triage-noadwrite-").FullName;
        var locator = new RulesetLocator(rulesetBase);

        // Pre-condition: the user ruleset file does NOT exist yet (a clean box / first triage).
        Assert.False(File.Exists(locator.UserRulesetPath), "the ruleset file must not exist before the first triage");

        var (window, shell, _, audit) = await DriveToArmedAuditAsync(locator);

        var namingRow = audit.Findings.First(r => IsNamingFindingFor(r, GgBadNameDn));
        namingRow.IsSelected = true;
        audit.SuppressSelectedCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();

        // (a) The ONLY persistent side effect is the ruleset file, written via the gate, now carrying
        //     the tagged ignore entry — the SettingsViewModel gate is the sole writer (no AD path).
        Assert.True(File.Exists(locator.UserRulesetPath), "the Suppress must have written the ruleset file via the gate");
        var reloaded = RulesetLoader.Load(File.ReadAllText(locator.UserRulesetPath));
        Assert.True(reloaded.Success, "the gate must have written a re-parseable ruleset");
        Assert.Contains(
            reloaded.Ruleset.Ignore,
            e => TriageEntry.KindOf(e) == TriageKind.Suppress
                 && string.Equals(e.Dn, TriageEntry.Escape(GgBadNameDn), StringComparison.Ordinal));

        // (b) The write landed in the injected temp-dir base and NOWHERE else under it but the one file
        //     (the gate writes a single ruleset.jsonc; an atomic temp sibling, if any, is cleaned up).
        //     This is the structural proof there is no second (AD or otherwise) write path.
        var written = Directory.EnumerateFiles(rulesetBase, "*", SearchOption.AllDirectories).ToList();
        Assert.Equal(locator.UserRulesetPath, Assert.Single(written));

        shell.Dispose();
        window.Close();
    }

    // === Helpers ===============================================================================

    /// <summary>Pumps the Avalonia dispatcher until <paramref name="condition"/> holds or a bounded
    /// number of yields elapses — the deterministic way to await the shell's <c>async void</c>
    /// <c>OnRulesetApplied</c> re-thread of the PARKED workspace (its <c>ApplyRulesetAsync</c>
    /// continuation runs on the UI thread). No wall-clock sleeps; each iteration yields then runs the
    /// queued UI jobs, so the continuation makes progress every pass.</summary>
    private static async Task PumpUntilAsync(Func<bool> condition)
    {
        for (var i = 0; i < 200 && !condition(); i++)
        {
            await Task.Yield();
            Dispatcher.UIThread.RunJobs();
        }
    }

    /// <summary>True when <paramref name="row"/> is the naming Warning finding anchored at
    /// <paramref name="dn"/> (the badly-named GG). Compared by the (severity, anchor) PROJECTION.</summary>
    private static bool IsNamingFindingFor(AuditFindingRowModel row, string dn) =>
        row.Severity == RuleSeverity.Warning && Dn.Comparer.Equals(row.PrimaryDn, dn);

    /// <summary>The WP5b/WP5c/WP5d findings fixture (re-stated so the fixtures stay independent): a
    /// fully-LOADED scope tripping the default ruleset's nesting (a DL with a direct User member) +
    /// naming (a badly-named GG) + empty-group rules — a real Error/Warning/Info mix. Returns the
    /// snapshot + the default ruleset. Matches <see cref="AuditNavigationTests"/>/<see cref="AuditTableTests"/>.</summary>
    private static (DirectorySnapshot Snapshot, Ruleset Ruleset) LoadedScopeWithFindings()
    {
        const string dlOk = "CN=DL_FileShare_RW,OU=Lab,DC=stub,DC=lab";
        const string ggMember = "CN=GG_FileShare_Members,OU=Lab,DC=stub,DC=lab";
        const string dlBad = "CN=DL_DirectUser_RW,OU=Lab,DC=stub,DC=lab";
        const string userDn = "CN=alice,OU=Lab,DC=stub,DC=lab";
        const string ggEmpty = "CN=GG_Empty_Team,OU=Lab,DC=stub,DC=lab";

        var snapshot = new DirectorySnapshot();
        snapshot.AddObject(Group(dlOk, AdObjectKind.DomainLocalGroup));
        snapshot.AddObject(Group(ggMember, AdObjectKind.GlobalGroup));
        snapshot.AddObject(Group(dlBad, AdObjectKind.DomainLocalGroup));
        snapshot.AddObject(new AdObject { Dn = userDn, Kind = AdObjectKind.User, Name = "alice" });
        snapshot.AddObject(Group(GgBadNameDn, AdObjectKind.GlobalGroup));
        snapshot.AddObject(Group(ggEmpty, AdObjectKind.GlobalGroup));

        snapshot.SetMembers(dlOk, new[] { ggMember });
        snapshot.SetMembers(ggMember, Array.Empty<string>());
        snapshot.SetMembers(dlBad, new[] { userDn });
        snapshot.SetMembers(GgBadNameDn, Array.Empty<string>());
        snapshot.SetMembers(ggEmpty, Array.Empty<string>());

        return (snapshot, RulesetLoader.LoadDefault());
    }

    private static AdObject Group(string dn, AdObjectKind kind) => new()
    {
        Dn = dn,
        Kind = kind,
        Name = dn.Split(',')[0]["CN=".Length..],
    };

    /// <summary>
    /// Builds a REAL shell over a stub provider that loads the <see cref="LoadedScopeWithFindings"/>
    /// snapshot, drives it to a live workspace, then into the Audit step (OnAudit arms the triage seam
    /// → ApplyTriage → the gate). Returns the window, shell, the parked workspace, and the armed audit.
    /// Injects temp-dir <see cref="UiStateStore"/> + <see cref="RulesetLocator"/> seams (lab-environment.md
    /// / the #124 lesson) so nothing touches real <c>%APPDATA%</c>.
    /// </summary>
    private static Task<(MainWindow Window, ShellViewModel Shell, WorkspaceViewModel Workspace, AuditViewModel Audit)>
        DriveToArmedAuditAsync() =>
        DriveToArmedAuditAsync(
            new RulesetLocator(Directory.CreateTempSubdirectory("groupweaver-triage-ruleset-").FullName));

    private static async Task<(MainWindow Window, ShellViewModel Shell, WorkspaceViewModel Workspace, AuditViewModel Audit)>
        DriveToArmedAuditAsync(RulesetLocator locator)
    {
        var (snapshot, _) = LoadedScopeWithFindings();
        var rootObject = new AdObject { Dn = RootDn, Kind = AdObjectKind.OrganizationalUnit, Name = "Lab" };
        var provider = new Fakes.StubDirectoryProvider(Task.FromResult(new DirectoryConnection("stub directory", 0)))
        {
            RootCandidatesResult = Task.FromResult<IReadOnlyList<AdObject>>([rootObject]),
            LoadScopeResult = Task.FromResult(snapshot),
        };

        var uiStateBase = Directory.CreateTempSubdirectory("groupweaver-triage-uistate-").FullName;
        var shell = new ShellViewModel(
            _ => provider,
            new StartupOptions(Demo: false),
            Present,
            graphRendererFactory: null,
            ruleset: locator.LoadEffective(),
            locator: locator,
            uiStateStore: new UiStateStore(uiStateBase));

        var window = new MainWindow { DataContext = shell, Width = 1280, Height = 720 };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var connect = Assert.IsType<ConnectionViewModel>(shell.CurrentStep);
        await connect.ConnectDemoCommand.ExecuteAsync(null);
        var picker = Assert.IsType<RootPickerViewModel>(shell.CurrentStep);
        await picker.LoadCandidates;
        Dispatcher.UIThread.RunJobs();
        picker.SelectedCandidate = picker.Candidates[0];
        picker.LoadRootCommand.Execute(null);
        var workspace = Assert.IsType<WorkspaceViewModel>(shell.CurrentStep);
        await workspace.Initialization;
        Dispatcher.UIThread.RunJobs();
        Assert.NotNull(workspace.Snapshot);

        // Into the Audit step: OnAudit arms the triage seam (audit.UseTriageCallback → ApplyTriage).
        shell.OnAudit(workspace);
        var audit = Assert.IsType<AuditViewModel>(shell.CurrentStep);
        return (window, shell, workspace, audit);
    }
}
