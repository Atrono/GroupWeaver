using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

using GroupWeaver.App.Export;
using GroupWeaver.App.Graph;
using GroupWeaver.App.Tests.Fakes;
using GroupWeaver.App.ViewModels;
using GroupWeaver.Core.Export;
using GroupWeaver.Core.Model;
using GroupWeaver.Core.Providers;
using GroupWeaver.Core.Rules;
using GroupWeaver.Providers;

using Xunit;

namespace GroupWeaver.App.Tests;

/// <summary>
/// Pins the AP 4.1 / ADR-013 slice-3 VM export commands —
/// <c>ExportReportCsvCommand</c> and <c>ExportReportHtmlCommand</c> on
/// <see cref="WorkspaceViewModel"/> — the App-side seam between the loaded
/// <see cref="WorkspaceViewModel.Report"/> and the pure Core
/// <see cref="ViolationReportExporter"/>. The exporter itself is pinned byte-for-byte by
/// <c>tests/GroupWeaver.Tests/Export/ViolationReportCsvTests.cs</c> and
/// <c>ViolationReportHtmlTests.cs</c>; these tests pin only the VM wiring around it.
///
/// <para>Binding contract (ADR-013 §2/§5/§6, the spec "Final file-dialog seam" + audit
/// findings F2/F4):</para>
/// <list type="bullet">
///   <item><b>Executing a command writes the exporter output to the picked path.</b> The
///   bytes read back from the <see cref="FakeExportDialogs"/>-returned path equal
///   <c>ViolationReportExporter.To{Csv,Html}(vm.Report, resolveName, header)</c>, where
///   <c>resolveName</c> mirrors <see cref="WorkspaceViewModel"/>'s <c>OnReportChanged</c>
///   closure (<c>dn =&gt; Snapshot.TryGetObject(dn, out var o) ? o.Name : dn</c>) and the
///   HTML <see cref="ReportHeader"/> carries the VM's root identity + connection summary.
///   (F4: Core is App-type-free — it takes the delegate + header, never the snapshot.)</item>
///   <item><b>A cancelled pick (path == null) is a no-op</b> — nothing is written anywhere.</item>
///   <item><b>The gate is <c>Snapshot is not null</c>, NOT <c>HasViolations</c></b> (F2):
///   disabled/inert before a load completes; an all-clear-but-unexpanded snapshot STILL
///   exports — the unexpanded-areas appendix is a real exportable artifact.</item>
///   <item><b>Read-only invariant (keystone):</b> the ONLY path written is the one the
///   dialog returned — export never touches AD and never writes anywhere else.</item>
/// </list>
///
/// <para>Content is pinned against the REAL <see cref="DemoProvider"/> demo scope (the
/// first-class offline test bed; the same 19-finding baseline AP 3.2/3.4 pin), so the
/// export is checked against the authoritative dataset — including the GG_Circle_A ↔
/// GG_Circle_B cycle the load must terminate over. RED until slice 3 adds the two commands
/// + the snapshot-name closure + the <see cref="ReportHeader"/> construction.</para>
/// </summary>
public sealed class WorkspaceExportTests
{
    private const string DemoRootDn = "OU=AGDLP-Demo,DC=weavedemo,DC=example";

    private const string StubRootDn = "OU=Lab,DC=stub,DC=lab";

    // === CSV: executing the command writes exactly the exporter's bytes ==================

    [Fact(Timeout = 60_000)]
    public async Task ExportReportCsv_WritesExporterOutput_ToThePickedPath()
    {
        var vm = await DemoWorkspaceAsync();
        using var temp = new TempFile("csv");
        var dialogs = new FakeExportDialogs().SavePathFor(ExportKind.Csv, temp.Path);
        SetDialogs(vm, dialogs);

        await vm.ExportReportCsvCommand.ExecuteAsync(null);

        // The exporter is the byte-authority (pinned by ViolationReportCsvTests); the VM
        // must write EXACTLY ToCsv(Report, resolveName) for its own report + name closure.
        var expected = ViolationReportExporter.ToCsv(vm.Report, ResolveNameOf(vm));
        Assert.True(File.Exists(temp.Path), "the CSV command must write the picked file");
        Assert.Equal(expected, ReadAllUtf8(temp.Path));

        vm.Dispose();
    }

    [Fact(Timeout = 60_000)]
    public async Task ExportReportCsv_CancelledPick_WritesNothing()
    {
        var vm = await DemoWorkspaceAsync();
        using var temp = new TempFile("csv");
        // A cancelled save dialog returns null for the CSV kind.
        var dialogs = new FakeExportDialogs().SavePathFor(ExportKind.Csv, null);
        SetDialogs(vm, dialogs);

        await vm.ExportReportCsvCommand.ExecuteAsync(null);

        // The picker WAS consulted (the gate let the command run — there is a snapshot)…
        Assert.Contains(ExportKind.Csv, dialogs.RequestedKinds);
        // …but a null pick is a no-op: nothing was written anywhere.
        Assert.Empty(temp.WrittenFiles());

        vm.Dispose();
    }

    // === HTML: executing the command writes exactly the exporter's bytes ================

    [Fact(Timeout = 60_000)]
    public async Task ExportReportHtml_WritesExporterOutput_WithTheVmHeader_ToThePickedPath()
    {
        var vm = await DemoWorkspaceAsync();
        using var temp = new TempFile("html");
        var dialogs = new FakeExportDialogs().SavePathFor(ExportKind.Html, temp.Path);
        SetDialogs(vm, dialogs);

        await vm.ExportReportHtmlCommand.ExecuteAsync(null);

        Assert.True(File.Exists(temp.Path), "the HTML command must write the picked file");
        var actual = ReadAllUtf8(temp.Path);

        // The VM builds the header from its own identity (root DN/name + connection summary)
        // and injects DateTimeOffset.Now for GeneratedAt. The timestamp is the ONLY field
        // that legitimately differs run-to-run; everything else must be byte-identical to
        // ToHtml(Report, resolveName, header). Re-render with the VM's identity and a
        // placeholder timestamp, then compare both with the single Generated row normalised.
        var expected = ViolationReportExporter.ToHtml(
            vm.Report,
            ResolveNameOf(vm),
            new ReportHeader(vm.RootDn, vm.RootName, vm.ConnectionSummary, default));

        Assert.Equal(NormaliseGeneratedRow(expected), NormaliseGeneratedRow(actual));

        // And the VM-built header identity actually reached the file (escaped element text).
        Assert.Contains(WebUtilEncode(vm.RootName), actual);
        Assert.Contains(WebUtilEncode(vm.RootDn), actual);
        Assert.Contains(WebUtilEncode(vm.ConnectionSummary), actual);

        vm.Dispose();
    }

    [Fact(Timeout = 60_000)]
    public async Task ExportReportHtml_CancelledPick_WritesNothing()
    {
        var vm = await DemoWorkspaceAsync();
        using var temp = new TempFile("html");
        var dialogs = new FakeExportDialogs().SavePathFor(ExportKind.Html, null);
        SetDialogs(vm, dialogs);

        await vm.ExportReportHtmlCommand.ExecuteAsync(null);

        Assert.Contains(ExportKind.Html, dialogs.RequestedKinds);
        Assert.Empty(temp.WrittenFiles());

        vm.Dispose();
    }

    // === gate = Snapshot is not null (NOT HasViolations) =================================

    [Fact]
    public async Task ExportCommands_BeforeAnyLoad_AreInert_NoPick_NoWrite()
    {
        // The load is held in flight, so Snapshot stays null — the pre-load state. With no
        // snapshot the export commands must be inert: they neither consult the picker nor
        // write a file (the gate is Snapshot is not null — a load must have COMPLETED).
        var loadGate = new TaskCompletionSource<DirectorySnapshot>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var provider = StubProvider();
        provider.LoadScopeResult = loadGate.Task;
        var fake = new FakeGraphRenderer();
        using var csv = new TempFile("csv");
        using var html = new TempFile("html");
        var dialogs = new FakeExportDialogs()
            .SavePathFor(ExportKind.Csv, csv.Path)
            .SavePathFor(ExportKind.Html, html.Path);
        var vm = Workspace(provider, () => fake, dialogs);

        Assert.True(vm.IsLoading);
        Assert.Null(vm.Snapshot);

        // The export commands are disarmed pre-load (the generated CanExecute follows the
        // Snapshot gate)…
        Assert.False(vm.ExportReportCsvCommand.CanExecute(null), "CSV export is disarmed before a load completes");
        Assert.False(vm.ExportReportHtmlCommand.CanExecute(null), "HTML export is disarmed before a load completes");

        // …and a stale-armed Execute (RelayCommand.Execute ignores CanExecute) is still a
        // silent no-op: no picker call, no file.
        await vm.ExportReportCsvCommand.ExecuteAsync(null);
        await vm.ExportReportHtmlCommand.ExecuteAsync(null);

        Assert.Empty(dialogs.RequestedKinds);
        Assert.False(File.Exists(csv.Path));
        Assert.False(File.Exists(html.Path));

        loadGate.SetResult(new DirectorySnapshot());
        await vm.Initialization;
        vm.Dispose();
    }

    [Fact]
    public async Task ExportReportCsv_AllClearButUnchecked_StillExportsTheAppendix()
    {
        // F2: the gate is Snapshot is not null, NOT HasViolations. A loaded scope with ZERO
        // findings but a non-empty unchecked frontier is a real exportable artifact — the
        // command must still run and the written CSV must carry the UncheckedDns appendix.
        var snapshot = AllClearButUncheckedScope();
        var provider = StubProvider();
        provider.LoadScopeResult = Task.FromResult(snapshot);
        var fake = new FakeGraphRenderer();
        using var temp = new TempFile("csv");
        var dialogs = new FakeExportDialogs().SavePathFor(ExportKind.Csv, temp.Path);
        var vm = Workspace(provider, () => fake, dialogs);
        await vm.Initialization;

        // Precondition: genuinely all-clear (no violations) but with unchecked areas — the
        // exact state that gating on HasViolations would wrongly disable.
        Assert.False(vm.HasViolations);
        Assert.True(vm.HasUncheckedAreas);
        Assert.True(
            vm.ExportReportCsvCommand.CanExecute(null),
            "export must stay armed in the all-clear-but-unchecked state (gate = Snapshot, not HasViolations)");

        await vm.ExportReportCsvCommand.ExecuteAsync(null);

        var written = ReadAllUtf8(temp.Path);
        // The file equals the exporter output for this report…
        Assert.Equal(ViolationReportExporter.ToCsv(vm.Report, ResolveNameOf(vm)), written);
        // …and the appendix is present despite there being zero violations.
        Assert.Contains("\r\nUncheckedDns\r\n", written);
        foreach (var dn in vm.Report.UncheckedDns)
        {
            Assert.Contains(dn, written);
        }

        vm.Dispose();
    }

    // === keystone read-only invariant: written path == dialog-returned path =============

    [Fact(Timeout = 60_000)]
    public async Task ExportReportCsv_WritesOnlyTheDialogReturnedPath_NeverTouchesAd()
    {
        // Over a STUB provider whose GetObject/GetMembers THROW unless explicitly injected
        // (none here): if export touched AD this faults. The loaded scope carries a real
        // finding so the CSV has data rows to serialise.
        var snapshot = LoadedScopeWithAFinding();
        var provider = StubProvider();
        provider.LoadScopeResult = Task.FromResult(snapshot);
        var fake = new FakeGraphRenderer();
        using var temp = new TempFile("csv");
        var dialogs = new FakeExportDialogs().SavePathFor(ExportKind.Csv, temp.Path);
        var vm = Workspace(provider, () => fake, dialogs);
        await vm.Initialization;
        Assert.True(vm.HasViolations, "the keystone scope must carry a finding so the CSV has rows");

        await vm.ExportReportCsvCommand.ExecuteAsync(null);

        // The picker was consulted for the CSV kind, and the ONE write target is exactly
        // the path it returned — export wrote ONLY there, nothing else under the isolation
        // directory (no derived AD/elsewhere path).
        Assert.Contains(ExportKind.Csv, dialogs.RequestedKinds);
        Assert.True(File.Exists(temp.Path));
        Assert.Equal(ViolationReportExporter.ToCsv(vm.Report, ResolveNameOf(vm)), ReadAllUtf8(temp.Path));
        var written = temp.WrittenFiles();
        Assert.True(
            written.SequenceEqual(new[] { temp.Path }, StringComparer.OrdinalIgnoreCase),
            $"export must write ONLY the picked path; saw: {string.Join(", ", written)}");

        // Read-only toward AD: export serialises the already-loaded report and NEVER queries
        // the directory — the stub recorded zero object/member fetches (any would have thrown).
        Assert.Equal(0, provider.GetObjectCalls);
        Assert.Equal(0, provider.GetMembersCalls);

        vm.Dispose();
    }

    [Fact(Timeout = 60_000)]
    public async Task ExportReportHtml_WritesOnlyTheDialogReturnedPath_NeverElsewhere()
    {
        var vm = await DemoWorkspaceAsync();
        using var temp = new TempFile("html");
        var dialogs = new FakeExportDialogs().SavePathFor(ExportKind.Html, temp.Path);
        SetDialogs(vm, dialogs);

        await vm.ExportReportHtmlCommand.ExecuteAsync(null);

        Assert.Contains(ExportKind.Html, dialogs.RequestedKinds);
        Assert.True(File.Exists(temp.Path));
        var written = temp.WrittenFiles();
        Assert.True(
            written.SequenceEqual(new[] { temp.Path }, StringComparer.OrdinalIgnoreCase),
            $"export must write ONLY the picked path; saw: {string.Join(", ", written)}");

        vm.Dispose();
    }

    // === helpers ========================================================================

    /// <summary>The name-resolution closure the VM passes to the exporter — mirrors
    /// <c>WorkspaceViewModel.OnReportChanged</c> exactly: an in-snapshot object resolves to
    /// its <c>Name</c>, an absent DN falls back to the DN itself (never a provider call).</summary>
    private static ViolationReportExporter.ResolveName ResolveNameOf(WorkspaceViewModel vm) =>
        dn => vm.Snapshot is not null && vm.Snapshot.TryGetObject(dn, out var o) ? o!.Name : dn;

    private static string WebUtilEncode(string value) => System.Net.WebUtility.HtmlEncode(value);

    /// <summary>Replaces the single "Generated" metadata row's value with a placeholder so
    /// two renderings that differ ONLY in the injected timestamp compare equal. Pins that
    /// every other byte of the VM-written HTML matches the exporter output.</summary>
    private static string NormaliseGeneratedRow(string html) =>
        Regex.Replace(
            html,
            "<tr><th>Generated</th><td>.*?</td></tr>",
            "<tr><th>Generated</th><td>TS</td></tr>",
            RegexOptions.Singleline);

    private static string ReadAllUtf8(string path) =>
        File.ReadAllText(path, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

    /// <summary>Sets the export dialog seam on an already-constructed VM via the slice-3
    /// wiring point. Slice 0 added the ctor param; slice 3 must let a test inject the seam
    /// after construction (the composition root attaches the real adapter once the workspace
    /// window exists). RED until that seam exists.</summary>
    private static void SetDialogs(WorkspaceViewModel vm, IExportFileDialogs dialogs) =>
        vm.UseExportFileDialogs(dialogs);

    private static AdObject Obj(string name, string dn, AdObjectKind kind = AdObjectKind.GlobalGroup) =>
        new() { Dn = dn, Kind = kind, Name = name, SamAccountName = name };

    /// <summary>A workspace over the REAL <see cref="DemoProvider"/> rooted at the demo OU
    /// (the full 19-finding scope, cycle included), Initialization awaited — the
    /// authoritative offline test bed.</summary>
    private static async Task<WorkspaceViewModel> DemoWorkspaceAsync()
    {
        var provider = new DemoProvider();
        var root = await provider.GetObjectAsync(DemoRootDn);
        Assert.NotNull(root);
        var fake = new FakeGraphRenderer();
        var vm = new WorkspaceViewModel(
            provider, root!, await provider.ConnectAsync(),
            webView2Missing: false, () => fake);
        await vm.Initialization;
        return vm;
    }

    /// <summary>A loaded, all-clear scope (a well-named non-empty GG, zero findings) that
    /// STILL carries an unchecked frontier: an unexpanded group member DN absent from the
    /// loaded objects surfaces in <c>UncheckedDns</c>. The F2 lever — exportable despite
    /// HasViolations being false.</summary>
    private static DirectorySnapshot AllClearButUncheckedScope()
    {
        const string parentDn = "CN=GG_Sales_Staff,OU=Lab,DC=stub,DC=lab";
        const string unexpandedChildDn = "CN=GG_Sub_Unexpanded,OU=Lab,DC=stub,DC=lab";
        var snapshot = new DirectorySnapshot();
        snapshot.AddObject(Obj("Lab", StubRootDn, AdObjectKind.OrganizationalUnit));
        snapshot.AddObject(Obj("GG_Sales_Staff", parentDn));
        // A fetchable child that is a member but never expanded => it lands in the frontier
        // (load-state truth) yet the parent is non-empty so no empty-group finding fires.
        snapshot.AddObject(Obj("GG_Sub_Unexpanded", unexpandedChildDn));
        snapshot.SetMembers(parentDn, [unexpandedChildDn]); // loaded, non-empty (no empty-group)
        // unexpandedChildDn is added but never SetMembers => unexpanded => UncheckedDns.
        return snapshot;
    }

    /// <summary>A loaded scope that carries exactly one finding under the default ruleset:
    /// a well-named GG that is LOADED-and-empty (an empty-group info). Gives the keystone
    /// CSV a real data row without any unchecked frontier noise.</summary>
    private static DirectorySnapshot LoadedScopeWithAFinding()
    {
        const string emptyGroupDn = "CN=GG_Sales_Staff,OU=Lab,DC=stub,DC=lab";
        var snapshot = new DirectorySnapshot();
        snapshot.AddObject(Obj("Lab", StubRootDn, AdObjectKind.OrganizationalUnit));
        snapshot.AddObject(Obj("GG_Sales_Staff", emptyGroupDn));
        snapshot.SetMembers(emptyGroupDn, []); // loaded-and-empty => one empty-group finding
        return snapshot;
    }

    private static StubDirectoryProvider StubProvider() =>
        new(Task.FromResult(new DirectoryConnection("stub directory", 5)))
        {
            LoadScopeResult = Task.FromResult(new DirectorySnapshot()),
        };

    private static WorkspaceViewModel Workspace(
        StubDirectoryProvider provider, Func<IGraphRenderer> rendererFactory, IExportFileDialogs dialogs) =>
        new(
            provider,
            Obj("Lab", StubRootDn, AdObjectKind.OrganizationalUnit),
            new DirectoryConnection("stub directory", 5),
            webView2Missing: false,
            rendererFactory,
            ruleset: null,
            exportDialogs: dialogs);

    /// <summary>A temp file under its OWN per-instance isolation directory so the read-only
    /// invariant can be pinned by scanning that directory for stray writes
    /// (<see cref="WrittenFiles"/>) without cross-test interference. The file PATH is computed
    /// but the file is NOT created — a no-op/cancelled export must leave it absent, and the
    /// directory then scans empty.</summary>
    private sealed class TempFile : IDisposable
    {
        private readonly string _dir;

        public TempFile(string extension)
        {
            _dir = Directory.CreateTempSubdirectory("groupweaver-workspace-export-tests-").FullName;
            Path = System.IO.Path.Combine(_dir, $"report.{extension}");
        }

        public string Path { get; }

        /// <summary>Every file that currently exists under THIS file's isolation directory —
        /// used to assert export wrote ONLY the picked path and nothing else.</summary>
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
}
