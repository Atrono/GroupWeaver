using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

using GroupWeaver.App.Export;
using GroupWeaver.App.Tests.Fakes;
using GroupWeaver.App.ViewModels;
using GroupWeaver.Core.Export;
using GroupWeaver.Core.Model;
using GroupWeaver.Core.Rules;

using Xunit;

namespace GroupWeaver.App.Tests;

/// <summary>
/// Pins the WP2 / ADR-013 audit-screen report export — the <c>ExportReportCsvCommand</c> and
/// <c>ExportReportHtmlCommand</c> just added to <see cref="AuditViewModel"/>. Sister to
/// <see cref="WorkspaceExportTests"/> (the workspace half): the exporter itself is byte-pinned by
/// <c>tests/GroupWeaver.Tests/Export/ViolationReport{Csv,Html}Tests.cs</c>; these tests pin ONLY the
/// audit VM's seam/gate/serialize-write wiring around it.
///
/// <para>Binding contract (ADR-013 §2/§5/§6 + the WP2 spec):</para>
/// <list type="bullet">
///   <item><b>Gate = seam-installed.</b> <c>CanExportReport</c> / <c>ExportReportCsvCommand.CanExecute</c>
///   is FALSE before <see cref="AuditViewModel.UseExportFileDialogs"/> (the borrowed snapshot is never
///   null by ctor contract and there is no loading state, so the seam + not-disposed are the only
///   gates), TRUE after the seam is installed.</item>
///   <item><b>The LIVE report (not the would-be table) is exported (keystone).</b> A triaged finding
///   is absent from the live <c>_report</c> but still listed in the would-be findings table; the CSV
///   must NOT carry it — proving the export serializes <c>_report</c>, not <c>_wouldBeReport</c>.</item>
///   <item><b>HTML header carries the threaded connection summary</b> (verbatim when constructed with
///   one; the <c>"{N} objects loaded"</c> snapshot fallback when constructed with <c>""</c>), plus the
///   root DN + the snapshot-resolved root name.</item>
///   <item><b>A cancelled pick (path == null) is a no-op</b> — nothing written, no throw.</item>
///   <item><b>Read-only invariant (keystone):</b> the ONLY path written is the dialog-returned one; the
///   borrowed snapshot/report/ruleset are never mutated.</item>
///   <item><b>Cancel-on-Dispose:</b> a stale-armed Execute after <see cref="AuditViewModel.Dispose"/>
///   writes nothing (the re-guard / cancelled <c>_cts</c>); <see cref="AuditViewModel.Dispose"/> is
///   idempotent (the new <c>_cts</c> disposal is guarded).</item>
/// </list>
///
/// <para>Fixtures mirror <see cref="AuditTriageTests"/>/<see cref="AuditTableTests"/>: a directly-built
/// <see cref="AuditViewModel"/> over a hand-built LOADED scope tripping the default ruleset's nesting +
/// naming + empty-group rules, plus a cycle-bearing scope so the report build (membership walk) MUST
/// terminate over the GG_Circle_A↔GG_Circle_B nesting cycle (the circular-case charter). Compares
/// PROJECTIONS, never record identity.</para>
/// </summary>
public sealed class AuditExportTests
{
    private const string RootDn = "OU=Lab,DC=stub,DC=lab";

    // The badly-named GG carries a naming Warning (and an empty-group Info) — the triage subject we
    // suppress to prove the LIVE report (not the would-be table) is exported.
    private const string GgBadNameDn = "CN=NotAConventionName,OU=Lab,DC=stub,DC=lab";

    // === (1) Gate: disarmed before the seam, armed after =================================

    [Fact]
    public void Gate_CanExportReport_IsFalseBeforeSeam_TrueAfterInstall()
    {
        var (snapshot, ruleset) = LoadedScopeWithFindings();
        var report = RuleEngine.Evaluate(snapshot, ruleset);
        using var audit = new AuditViewModel(snapshot, report, ruleset, RootDn, onBack: () => { });

        // Pre-install: the export seam is dead, so both report-export commands are disarmed (the
        // borrowed snapshot is non-null by ctor contract — only the seam half of the gate is missing).
        Assert.False(
            audit.ExportReportCsvCommand.CanExecute(null),
            "CSV export must be disarmed before UseExportFileDialogs installs the seam");
        Assert.False(
            audit.ExportReportHtmlCommand.CanExecute(null),
            "HTML export must be disarmed before UseExportFileDialogs installs the seam");

        // Install the seam — both commands re-arm (the installer notifies CanExecuteChanged).
        audit.UseExportFileDialogs(new FakeExportDialogs());

        Assert.True(audit.ExportReportCsvCommand.CanExecute(null), "CSV export must arm once the seam is installed");
        Assert.True(audit.ExportReportHtmlCommand.CanExecute(null), "HTML export must arm once the seam is installed");
    }

    // === (2) CSV writes the LIVE report, NOT the would-be table (keystone) ===============

    [Fact(Timeout = 60_000)]
    public async Task ExportReportCsv_WritesExporterOutput_OfTheLiveReport_ToThePickedPath()
    {
        var (snapshot, ruleset) = LoadedScopeWithFindings();
        var report = RuleEngine.Evaluate(snapshot, ruleset);
        using var audit = new AuditViewModel(snapshot, report, ruleset, RootDn, onBack: () => { });
        using var temp = new TempFile("csv");
        audit.UseExportFileDialogs(new FakeExportDialogs().SavePathFor(ExportKind.Csv, temp.Path));

        await audit.ExportReportCsvCommand.ExecuteAsync(null);

        // The exporter is the byte-authority (pinned by ViolationReportCsvTests); the VM must write
        // EXACTLY ToCsv(<the LIVE report>, resolveName) for its borrowed report + name closure.
        var expected = ViolationReportExporter.ToCsv(report, ResolveNameOf(snapshot));
        Assert.True(File.Exists(temp.Path), "the CSV command must write the picked file");
        Assert.Equal(expected, ReadAllUtf8(temp.Path));

        // #329 defect 2 (end-to-end): the CSV FILE starts with the UTF-8 BOM bytes EF BB BF (the
        // exporter's in-string U+FEFF through AuditExportService's BOM-less UTF-8 writer), so an
        // Excel double-click decodes UTF-8 — the German-AD Excel-correctness contract.
        var head = File.ReadAllBytes(temp.Path);
        Assert.True(
            head.Length >= 3 && head[0] == 0xEF && head[1] == 0xBB && head[2] == 0xBF,
            "the CSV export must start with the UTF-8 BOM (EF BB BF)");
    }

    [Fact(Timeout = 60_000)]
    public async Task ExportReportCsv_ExportsLiveReport_NotTheWouldBeTable_TriagedFindingAbsent()
    {
        // The keystone distinguishing pin: triage the naming finding (a tagged [suppress] ignore
        // entry), so it DROPS from the live _report but STAYS LISTED in the would-be findings table.
        // The exported CSV must reflect the LIVE report — the suppressed finding's DN/message ABSENT.
        var (snapshot, baseRuleset) = LoadedScopeWithFindings();

        // Build the live ruleset with a [suppress]-tagged ignore entry covering the naming finding's DN
        // (the exact shape AuditTriageTests' status-detection unit test uses, via the triage seam grammar).
        var taggedEntry = TriageEntry.Build(
            new TriageRequest(TriageEntry.Escape(GgBadNameDn), "naming-gg", TriageKind.Suppress, null));
        var liveRuleset = baseRuleset with { Ignore = baseRuleset.Ignore.Append(taggedEntry).ToList() };
        var liveReport = RuleEngine.Evaluate(snapshot, liveRuleset);

        using var audit = new AuditViewModel(snapshot, liveReport, liveRuleset, RootDn, onBack: () => { });

        // Precondition A: the naming finding is GONE from the live report (suppressed for real)…
        Assert.DoesNotContain(
            liveReport.Violations,
            v => Dn.Comparer.Equals(v.PrimaryDn, GgBadNameDn) && v.Severity == RuleSeverity.Warning);
        // Precondition B: …yet the would-be table STILL LISTS the triaged row (visible + reversible) —
        // so an export of the table (the wrong source) WOULD carry it. This makes the absence below
        // load-bearing: only exporting the live report excludes it.
        Assert.Contains(
            audit.Findings,
            r => r.Severity == RuleSeverity.Warning && Dn.Comparer.Equals(r.PrimaryDn, GgBadNameDn));

        using var temp = new TempFile("csv");
        audit.UseExportFileDialogs(new FakeExportDialogs().SavePathFor(ExportKind.Csv, temp.Path));

        await audit.ExportReportCsvCommand.ExecuteAsync(null);

        var written = ReadAllUtf8(temp.Path);

        // The file equals the LIVE report's CSV (the authority)…
        Assert.Equal(ViolationReportExporter.ToCsv(liveReport, ResolveNameOf(snapshot)), written);
        // …and the suppressed finding's DN does NOT appear anywhere in it (it is the would-be-only row).
        Assert.DoesNotContain(GgBadNameDn, written);
        // The naming finding's canonical message is also absent (belt-and-braces against a DN-format quirk).
        var suppressedNamingMessage = audit.Findings
            .First(r => r.Severity == RuleSeverity.Warning && Dn.Comparer.Equals(r.PrimaryDn, GgBadNameDn))
            .Message;
        Assert.DoesNotContain(suppressedNamingMessage, written);
    }

    // === (3) HTML header: threaded connection summary, fallback, root identity ===========

    [Fact(Timeout = 60_000)]
    public async Task ExportReportHtml_CarriesThreadedConnectionSummary_AndRootIdentity()
    {
        const string connectionSummary = "LDAP localhost:389 (agdlp.lab)";
        var (snapshot, ruleset) = LoadedScopeWithFindings();
        var report = RuleEngine.Evaluate(snapshot, ruleset);
        using var audit = new AuditViewModel(
            snapshot, report, ruleset, RootDn, onBack: () => { }, connectionSummary: connectionSummary);
        using var temp = new TempFile("html");
        audit.UseExportFileDialogs(new FakeExportDialogs().SavePathFor(ExportKind.Html, temp.Path));

        await audit.ExportReportHtmlCommand.ExecuteAsync(null);

        Assert.True(File.Exists(temp.Path), "the HTML command must write the picked file");
        var actual = ReadAllUtf8(temp.Path);

        // The threaded connection summary reached the header verbatim (HTML-escaped element text).
        Assert.Contains(WebUtilEncode(connectionSummary), actual);
        // The root DN + the snapshot-resolved root name both appear (the header identity).
        var rootName = SubjectNameResolver.Resolve(snapshot, RootDn);
        Assert.Contains(WebUtilEncode(RootDn), actual);
        Assert.Contains(WebUtilEncode(rootName), actual);

        // Byte-equality against the exporter for the VM's identity + a placeholder timestamp (the only
        // field that legitimately differs run-to-run is GeneratedAt, normalised away). ADR-030 D3 (#188):
        // the VM threads the ruleset name + triaged count + unchecked count into the header, so the
        // expected header carries the SAME three values (ruleset Name, this scope's TriagedCount, the
        // live report's unchecked count) — byte-equality tracks the implementation, never a constant.
        var expected = ViolationReportExporter.ToHtml(
            report,
            ResolveNameOf(snapshot),
            new ReportHeader(
                RootDn,
                rootName,
                connectionSummary,
                default,
                RulesetName: ruleset.Name,
                TriagedCount: audit.TriagedCount,
                UncheckedCount: report.UncheckedDns.Count));
        Assert.Equal(NormaliseGeneratedRow(expected), NormaliseGeneratedRow(actual));
    }

    [Fact(Timeout = 60_000)]
    public async Task ExportReportHtml_EmptyConnectionSummary_FallsBackToObjectCount()
    {
        // Constructed with the default "" connection summary (e.g. the 5-arg test ctors): the header
        // must fall back to the snapshot-object-count line, never an empty summary.
        var (snapshot, ruleset) = LoadedScopeWithFindings();
        var report = RuleEngine.Evaluate(snapshot, ruleset);
        using var audit = new AuditViewModel(snapshot, report, ruleset, RootDn, onBack: () => { });
        using var temp = new TempFile("html");
        audit.UseExportFileDialogs(new FakeExportDialogs().SavePathFor(ExportKind.Html, temp.Path));

        await audit.ExportReportHtmlCommand.ExecuteAsync(null);

        var actual = ReadAllUtf8(temp.Path);
        var fallback = $"{snapshot.Objects.Count} objects loaded";
        Assert.Contains(WebUtilEncode(fallback), actual);

        // ADR-030 D3 (#188): the VM now threads the active ruleset name, the triaged count and the live
        // report's unchecked count into the header (BuildReportHeader), so the exporter renders the three
        // honesty meta rows. Build the expected header with the SAME values the VM passes — the ruleset's
        // Name, this scope's TriagedCount (0 here — no triage entries) and the live report's unchecked
        // count — so byte-equality still tracks the implementation, never a hardcoded constant.
        var rootName = SubjectNameResolver.Resolve(snapshot, RootDn);
        var expected = ViolationReportExporter.ToHtml(
            report,
            ResolveNameOf(snapshot),
            new ReportHeader(
                RootDn,
                rootName,
                fallback,
                default,
                RulesetName: ruleset.Name,
                TriagedCount: audit.TriagedCount,
                UncheckedCount: report.UncheckedDns.Count));
        Assert.Equal(NormaliseGeneratedRow(expected), NormaliseGeneratedRow(actual));
    }

    // === (4) Cancelled pick = no write ===================================================

    [Fact(Timeout = 60_000)]
    public async Task ExportReportCsv_CancelledPick_WritesNothing_NoThrow()
    {
        var (snapshot, ruleset) = LoadedScopeWithFindings();
        var report = RuleEngine.Evaluate(snapshot, ruleset);
        using var audit = new AuditViewModel(snapshot, report, ruleset, RootDn, onBack: () => { });
        using var temp = new TempFile("csv");
        // A cancelled save dialog returns null for the CSV kind.
        var dialogs = new FakeExportDialogs().SavePathFor(ExportKind.Csv, null);
        audit.UseExportFileDialogs(dialogs);

        await audit.ExportReportCsvCommand.ExecuteAsync(null);

        // The picker WAS consulted (the gate let the command run — there is a snapshot)…
        Assert.Contains(ExportKind.Csv, dialogs.RequestedKinds);
        // …but a null pick is a no-op: nothing was written anywhere.
        Assert.Empty(temp.WrittenFiles());
    }

    [Fact(Timeout = 60_000)]
    public async Task ExportReportHtml_CancelledPick_WritesNothing_NoThrow()
    {
        var (snapshot, ruleset) = LoadedScopeWithFindings();
        var report = RuleEngine.Evaluate(snapshot, ruleset);
        using var audit = new AuditViewModel(snapshot, report, ruleset, RootDn, onBack: () => { });
        using var temp = new TempFile("html");
        var dialogs = new FakeExportDialogs().SavePathFor(ExportKind.Html, null);
        audit.UseExportFileDialogs(dialogs);

        await audit.ExportReportHtmlCommand.ExecuteAsync(null);

        Assert.Contains(ExportKind.Html, dialogs.RequestedKinds);
        Assert.Empty(temp.WrittenFiles());
    }

    // === (5) Read-only invariant: only the picked path written; inputs unmutated =========

    [Fact(Timeout = 60_000)]
    public async Task ExportReportCsv_WritesOnlyTheDialogReturnedPath_AndNeverMutatesTheBorrowedInputs()
    {
        var (snapshot, ruleset) = LoadedScopeWithFindings();
        var report = RuleEngine.Evaluate(snapshot, ruleset);

        // Capture the borrowed inputs' observable state BEFORE export (read-only invariant).
        var objectCountBefore = snapshot.Objects.Count;
        var ignoreCountBefore = ruleset.Ignore.Count;
        var violationsBefore = report.Violations
            .Select(v => (v.RuleId, v.Severity, v.PrimaryDn, v.Message))
            .ToArray();

        using var audit = new AuditViewModel(snapshot, report, ruleset, RootDn, onBack: () => { });
        using var temp = new TempFile("csv");
        var dialogs = new FakeExportDialogs().SavePathFor(ExportKind.Csv, temp.Path);
        audit.UseExportFileDialogs(dialogs);

        await audit.ExportReportCsvCommand.ExecuteAsync(null);

        // The picker was consulted for the CSV kind, and the ONE write target is exactly the path it
        // returned — export wrote ONLY there, nothing else under the isolation directory.
        Assert.Contains(ExportKind.Csv, dialogs.RequestedKinds);
        Assert.True(File.Exists(temp.Path));
        var written = temp.WrittenFiles();
        Assert.True(
            written.SequenceEqual(new[] { temp.Path }, StringComparer.OrdinalIgnoreCase),
            $"export must write ONLY the picked path; saw: {string.Join(", ", written)}");

        // The borrowed snapshot/report/ruleset were never mutated (the audit borrows them read-only).
        Assert.Equal(objectCountBefore, snapshot.Objects.Count);
        Assert.Equal(ignoreCountBefore, ruleset.Ignore.Count);
        Assert.Equal(
            violationsBefore,
            report.Violations.Select(v => (v.RuleId, v.Severity, v.PrimaryDn, v.Message)).ToArray());
    }

    [Fact(Timeout = 60_000)]
    public async Task ExportReportHtml_WritesOnlyTheDialogReturnedPath_NeverElsewhere()
    {
        var (snapshot, ruleset) = LoadedScopeWithFindings();
        var report = RuleEngine.Evaluate(snapshot, ruleset);
        using var audit = new AuditViewModel(snapshot, report, ruleset, RootDn, onBack: () => { });
        using var temp = new TempFile("html");
        var dialogs = new FakeExportDialogs().SavePathFor(ExportKind.Html, temp.Path);
        audit.UseExportFileDialogs(dialogs);

        await audit.ExportReportHtmlCommand.ExecuteAsync(null);

        Assert.Contains(ExportKind.Html, dialogs.RequestedKinds);
        Assert.True(File.Exists(temp.Path));
        var written = temp.WrittenFiles();
        Assert.True(
            written.SequenceEqual(new[] { temp.Path }, StringComparer.OrdinalIgnoreCase),
            $"export must write ONLY the picked path; saw: {string.Join(", ", written)}");
    }

    // === circular-case charter: the report build must TERMINATE over the A<->B cycle =====

    [Fact(Timeout = 60_000)]
    public async Task ExportReportCsv_OverACyclicScope_Terminates_AndWritesTheLiveReport()
    {
        // The GG_Circle_A <-> GG_Circle_B nesting cycle: constructing the AuditViewModel runs
        // RuleEngine.Evaluate (membership walk) which MUST terminate over it; the export then writes the
        // resulting live report. This is the test-engineer circular-case charter for traversal code.
        var snapshot = CycleScope();
        var ruleset = RulesetLoader.LoadDefault();
        var report = RuleEngine.Evaluate(snapshot, ruleset);
        using var audit = new AuditViewModel(snapshot, report, ruleset, RootDn, onBack: () => { });
        using var temp = new TempFile("csv");
        audit.UseExportFileDialogs(new FakeExportDialogs().SavePathFor(ExportKind.Csv, temp.Path));

        await audit.ExportReportCsvCommand.ExecuteAsync(null);

        // The cyclic scope yields a circular finding, and the written CSV equals the live report's CSV.
        Assert.Contains(report.Violations, v => v.RuleId == RuleIds.Circular);
        Assert.Equal(ViolationReportExporter.ToCsv(report, ResolveNameOf(snapshot)), ReadAllUtf8(temp.Path));
    }

    // === (6) Cancel-on-Dispose: a post-Dispose resolve writes nothing ====================

    [Fact(Timeout = 60_000)]
    public async Task ExportReportCsv_PickResolvesAfterDispose_WritesNothing()
    {
        // Construct the race the workspace test models: the picker is held in flight, Dispose runs
        // (cancelling _cts), THEN the pick resolves to a real path. The re-guard / cancelled token must
        // drop the write — nothing reaches the file after dispose.
        var (snapshot, ruleset) = LoadedScopeWithFindings();
        var report = RuleEngine.Evaluate(snapshot, ruleset);
        var audit = new AuditViewModel(snapshot, report, ruleset, RootDn, onBack: () => { });
        using var temp = new TempFile("csv");

        var pickGate = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        audit.UseExportFileDialogs(new GatedExportDialogs(pickGate.Task));

        var export = audit.ExportReportCsvCommand.ExecuteAsync(null);

        // Tear down while the pick is still pending, THEN resolve it to a real path.
        audit.Dispose();
        pickGate.SetResult(temp.Path);

        // The cancel-on-Dispose contract (ADR-013): a save-picker open during teardown "can never write
        // after dispose". The clean contract is a NO-OP — the post-await re-guard (IsDisposed) drops the
        // write before it ever touches the (now-disposed) _cts. It must NOT surface an exception from the
        // export task: throwing ObjectDisposedException out of an [RelayCommand] handler is a teardown-race
        // bug, not a graceful no-op.
        var ex = await Record.ExceptionAsync(() => export);
        Assert.Null(ex);

        // …and nothing was written after dispose.
        Assert.Empty(temp.WrittenFiles());
    }

    [Fact]
    public void Gate_AfterDispose_CanExportReportIsFalse_AndExecuteIsInert()
    {
        var (snapshot, ruleset) = LoadedScopeWithFindings();
        var report = RuleEngine.Evaluate(snapshot, ruleset);
        var audit = new AuditViewModel(snapshot, report, ruleset, RootDn, onBack: () => { });
        audit.UseExportFileDialogs(new FakeExportDialogs());
        Assert.True(audit.ExportReportCsvCommand.CanExecute(null), "armed pre-dispose");

        audit.Dispose();

        Assert.False(
            audit.ExportReportCsvCommand.CanExecute(null),
            "export must disarm once disposed (IsDisposed gates CanExportReport)");
        Assert.False(audit.ExportReportHtmlCommand.CanExecute(null), "export must disarm once disposed");
    }

    // === (7) Dispose idempotency: the guarded _cts disposal never throws =================

    [Fact]
    public void Dispose_CalledTwice_DoesNotThrow_AndStaysDisposed()
    {
        var (snapshot, ruleset) = LoadedScopeWithFindings();
        var report = RuleEngine.Evaluate(snapshot, ruleset);
        var audit = new AuditViewModel(snapshot, report, ruleset, RootDn, onBack: () => { });

        audit.Dispose();
        Assert.True(audit.IsDisposed);

        // The second Dispose is guarded (IsDisposed short-circuits before re-cancelling/-disposing _cts),
        // so it must be a no-op — never an ObjectDisposedException from a double _cts.Dispose().
        var ex = Record.Exception(() => audit.Dispose());
        Assert.Null(ex);
        Assert.True(audit.IsDisposed);
    }

    // === helpers ========================================================================

    /// <summary>The name-resolution closure the audit VM passes to the exporter — the shared
    /// snapshot-only <see cref="SubjectNameResolver"/> (an in-snapshot object resolves to its
    /// <c>Name</c>, an absent DN falls back to the DN itself; never a provider call).</summary>
    private static ViolationReportExporter.ResolveName ResolveNameOf(DirectorySnapshot snapshot) =>
        dn => SubjectNameResolver.Resolve(snapshot, dn);

    private static string WebUtilEncode(string value) => System.Net.WebUtility.HtmlEncode(value);

    /// <summary>Replaces the single "Generated" metadata row's value with a placeholder so two
    /// renderings that differ ONLY in the injected timestamp compare equal (mirrors
    /// <see cref="WorkspaceExportTests"/>).</summary>
    private static string NormaliseGeneratedRow(string html) =>
        Regex.Replace(
            html,
            "<tr><th[^>]*>Generated</th><td>.*?</td></tr>",
            "<tr><th>Generated</th><td>TS</td></tr>",
            RegexOptions.Singleline);

    /// <summary>Decodes the file's raw bytes WITHOUT BOM detection — a leading U+FEFF stays in
    /// the returned string, so string equality against the exporter output (whose CSV contract
    /// carries the in-string BOM, #329) compares the true bytes. (<c>File.ReadAllText</c> would
    /// silently strip a file BOM.)</summary>
    private static string ReadAllUtf8(string path) =>
        Encoding.UTF8.GetString(File.ReadAllBytes(path));

    /// <summary>The WP5 findings fixture (mirrors <see cref="AuditTriageTests"/>/<see cref="AuditTableTests"/>):
    /// a fully-LOADED scope tripping the default ruleset's nesting (a DL with a direct User member) +
    /// naming (a badly-named GG) + empty-group rules — a real Error/Warning/Info mix. Returns the
    /// snapshot + the default ruleset.</summary>
    private static (DirectorySnapshot Snapshot, Ruleset Ruleset) LoadedScopeWithFindings()
    {
        const string dlOk = "CN=DL_FileShare_RW,OU=Lab,DC=stub,DC=lab";
        const string ggMember = "CN=GG_FileShare_Members,OU=Lab,DC=stub,DC=lab";
        const string dlBad = "CN=DL_DirectUser_RW,OU=Lab,DC=stub,DC=lab";
        const string userDn = "CN=alice,OU=Lab,DC=stub,DC=lab";
        const string ggEmpty = "CN=GG_Empty_Team,OU=Lab,DC=stub,DC=lab";

        var snapshot = new DirectorySnapshot();
        snapshot.AddObject(new AdObject { Dn = RootDn, Kind = AdObjectKind.OrganizationalUnit, Name = "Lab" });
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

    /// <summary>Root OU + the GG_Circle_A <-> GG_Circle_B nesting cycle (A->B->A) — the circular-case
    /// charter scope: the report build's membership walk MUST terminate over it.</summary>
    private static DirectorySnapshot CycleScope()
    {
        const string circleA = "CN=GG_Circle_A,OU=Lab,DC=stub,DC=lab";
        const string circleB = "CN=GG_Circle_B,OU=Lab,DC=stub,DC=lab";
        var snapshot = new DirectorySnapshot();
        snapshot.AddObject(new AdObject { Dn = RootDn, Kind = AdObjectKind.OrganizationalUnit, Name = "Lab" });
        snapshot.AddObject(Group(circleA, AdObjectKind.GlobalGroup));
        snapshot.AddObject(Group(circleB, AdObjectKind.GlobalGroup));
        snapshot.SetMembers(circleA, new[] { circleB });
        snapshot.SetMembers(circleB, new[] { circleA }); // closes the A->B->A cycle
        return snapshot;
    }

    private static AdObject Group(string dn, AdObjectKind kind) => new()
    {
        Dn = dn,
        Kind = kind,
        Name = dn.Split(',')[0]["CN=".Length..],
    };

    /// <summary>A temp file under its OWN per-instance isolation directory so the read-only invariant can
    /// be pinned by scanning that directory for stray writes (mirrors <see cref="WorkspaceExportTests"/>).
    /// The PATH is computed but the file is NOT created — a no-op/cancelled export must leave it absent.</summary>
    private sealed class TempFile : IDisposable
    {
        private readonly string _dir;

        public TempFile(string extension)
        {
            _dir = Directory.CreateTempSubdirectory("groupweaver-audit-export-tests-").FullName;
            Path = System.IO.Path.Combine(_dir, $"report.{extension}");
        }

        public string Path { get; }

        public string[] WrittenFiles() =>
            Directory.Exists(_dir)
                ? Directory.GetFiles(_dir, "*", SearchOption.AllDirectories)
                : [];

        public void Dispose()
        {
            try
            {
                Directory.Delete(_dir, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    /// <summary>An export-dialog seam whose <see cref="PickSavePathAsync"/> resolves only when the
    /// supplied task completes — lets a test hold the pick in flight, Dispose the VM, THEN resolve the
    /// path to model the post-dispose race (cancel-on-Dispose). The CancellationToken is observed so a
    /// cancelled _cts can also fault the await; either way the post-dispose re-guard drops the write.</summary>
    private sealed class GatedExportDialogs : IExportFileDialogs
    {
        private readonly Task<string?> _pick;

        public GatedExportDialogs(Task<string?> pick) => _pick = pick;

        public async Task<string?> PickSavePathAsync(ExportKind kind, CancellationToken ct = default)
        {
            // Honour cancellation if the token trips first (Dispose cancels _cts); otherwise return the
            // gated pick. Both arms leave the post-await re-guard to drop the write after dispose.
            using var registration = ct.Register(() => { });
            return await _pick;
        }
    }
}
