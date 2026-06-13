using System;
using System.Linq;
using System.Threading.Tasks;

using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;

using GroupWeaver.App.Export;
using GroupWeaver.App.Graph;
using GroupWeaver.App.Tests.Fakes;
using GroupWeaver.App.ViewModels;
using GroupWeaver.App.Views;
using GroupWeaver.Core.Model;
using GroupWeaver.Core.Providers;

using Xunit;

namespace GroupWeaver.App.Tests.Views;

/// <summary>
/// Pins the AP 4.1 / ADR-013 §6 report-export PAIR at the VIEW layer: the two
/// "Export CSV" / "Export HTML" buttons S4 added to <see cref="ViolationsSidebarView"/>'s
/// header row. <see cref="WorkspaceExportTests"/> already pins the VM half — the commands'
/// gate, serialize+write logic and the read-only invariant — so this test pins ONLY the
/// rendered-button wiring the VM tests cannot see:
/// <list type="bullet">
///   <item><b>Both buttons render with their <c>Content</c></b> ("Export CSV" / "Export HTML"),
///   located by their stable seam <c>Name</c> (<c>ExportCsvButton</c>/<c>ExportHtmlButton</c>) —
///   the template-independent anchor (the RefreshButton/ReloadScopeButton idiom).</item>
///   <item><b>Each is bound to the matching command instance</b> — CSV→<c>ExportReportCsvCommand</c>,
///   HTML→<c>ExportReportHtmlCommand</c>, never cross-wired (<c>Assert.Same</c>).</item>
///   <item><b>Disabled when <c>Snapshot is null</c>; armed after a load completes</b> — the F2
///   gate surfaced in the RENDERED button (<c>IsEffectivelyEnabled</c>, not merely
///   <c>CanExecute</c>). The export seam is injected BEFORE showing and held constant, so the
///   pre-load→post-load differential isolates the <c>Snapshot is not null</c> half of the gate
///   (not the <c>_exportDialogs is not null</c> half — that stays satisfied throughout).</item>
/// </list>
///
/// <para>The load is held in flight to observe the pre-load disabled state, then released to
/// observe the armed state, over a STUB scope that includes the GG_Circle_A ↔ GG_Circle_B
/// nesting cycle — the report build that arms the buttons walks membership and MUST terminate
/// over it (the test-engineer circular-case charter).</para>
/// </summary>
public sealed class SidebarExportButtonsViewTests
{
    private const string RootDn = "OU=Lab,DC=stub,DC=lab";
    private const string CircleADn = "CN=GG_Circle_A,OU=Lab,DC=stub,DC=lab";
    private const string CircleBDn = "CN=GG_Circle_B,OU=Lab,DC=stub,DC=lab";

    [AvaloniaFact]
    public async Task ExportButtons_RenderWithContent_BoundToTheExportCommands_DisabledUntilSnapshotLoads()
    {
        var fake = new FakeGraphRenderer();

        // Hold the scope load in flight so Snapshot stays null — the pre-load state.
        var loadGate = new TaskCompletionSource<DirectorySnapshot>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var provider = Provider();
        provider.LoadScopeResult = loadGate.Task;

        var vm = Workspace(provider, () => fake);

        // Install the export seam BEFORE showing and hold it constant across both states, so
        // the only thing flipping the buttons enabled/disabled is the Snapshot gate (F2), not
        // the seam half of CanExportReport (_exportDialogs is not null).
        vm.UseExportFileDialogs(new FakeExportDialogs());

        var (window, _) = ShowWorkspace(vm);
        Dispatcher.UIThread.RunJobs();

        // Pre-load: a load is in flight, no snapshot yet.
        Assert.True(vm.IsLoading);
        Assert.Null(vm.Snapshot);

        var sidebar = Assert.Single(window.GetVisualDescendants().OfType<ViolationsSidebarView>());
        var csv = ExportButton(sidebar, "ExportCsvButton");
        var html = ExportButton(sidebar, "ExportHtmlButton");

        // (1) Both buttons render, visible, with their shipped English Content.
        Assert.True(csv.IsEffectivelyVisible);
        Assert.True(html.IsEffectivelyVisible);
        Assert.Equal("Export CSV", csv.Content);
        Assert.Equal("Export HTML", html.Content);

        // (2) Each is bound to the matching command instance — never cross-wired.
        Assert.Same(vm.ExportReportCsvCommand, csv.Command);
        Assert.Same(vm.ExportReportHtmlCommand, html.Command);

        // (3a) Disabled pre-load: the rendered button reflects the Snapshot-is-null gate
        //      (IsEffectivelyEnabled, not just CanExecute) — the buttons self-disable so the
        //      pre-load state shows them greyed (no per-button IsEnabled binding needed).
        Assert.False(
            csv.IsEffectivelyEnabled,
            "Export CSV must be disabled before a load completes (gate = Snapshot is not null, F2)");
        Assert.False(
            html.IsEffectivelyEnabled,
            "Export HTML must be disabled before a load completes (gate = Snapshot is not null, F2)");

        // Release the load — Snapshot is now non-null, the report builds (terminating over the
        // A↔B cycle), and the export commands re-arm.
        loadGate.SetResult(CycleScope());
        await vm.Initialization;
        Dispatcher.UIThread.RunJobs();

        Assert.False(vm.IsLoading);
        Assert.NotNull(vm.Snapshot);

        // (3b) Armed after the load: the SAME rendered buttons are now enabled. The seam was
        //      installed all along, so this transition isolates the Snapshot gate.
        Assert.True(
            csv.IsEffectivelyEnabled,
            "Export CSV must arm once a load completes (Snapshot is not null, seam installed)");
        Assert.True(
            html.IsEffectivelyEnabled,
            "Export HTML must arm once a load completes (Snapshot is not null, seam installed)");

        window.Close();
    }

    // --- helpers ------------------------------------------------------------------------

    private static AdObject Obj(
        string name, string dn, AdObjectKind kind = AdObjectKind.GlobalGroup) =>
        new() { Dn = dn, Kind = kind, Name = name, SamAccountName = name };

    /// <summary>The header export <see cref="Button"/> with the given seam <c>Name</c>
    /// (<c>ExportCsvButton</c>/<c>ExportHtmlButton</c>) — the stable, template-independent
    /// anchor (the RefreshButton/ReloadScopeButton idiom). Asserts exactly one is realized, so
    /// a rename or virtualization hiding it fails loudly, not as a null deref.</summary>
    private static Button ExportButton(ViolationsSidebarView sidebar, string name) =>
        Assert.Single(sidebar.GetVisualDescendants().OfType<Button>(), b => b.Name == name);

    /// <summary>Root OU + the GG_Circle_A ↔ GG_Circle_B nesting cycle (A→B→A). A real loaded
    /// scope whose report build the armed buttons depend on — and whose membership walk MUST
    /// terminate over the cycle (the circular-case charter).</summary>
    private static DirectorySnapshot CycleScope()
    {
        var snapshot = new DirectorySnapshot();
        snapshot.AddObject(Obj("Lab", RootDn, AdObjectKind.OrganizationalUnit));
        snapshot.AddObject(Obj("GG_Circle_A", CircleADn));
        snapshot.AddObject(Obj("GG_Circle_B", CircleBDn));
        snapshot.SetMembers(CircleADn, [CircleBDn]);
        snapshot.SetMembers(CircleBDn, [CircleADn]); // closes the A→B→A cycle
        return snapshot;
    }

    /// <summary>Stub whose scope load yields <paramref name="snapshot"/> (default: empty); the
    /// in-flight gate is set on <c>LoadScopeResult</c> by the test for the pre-load state.</summary>
    private static StubDirectoryProvider Provider(DirectorySnapshot? snapshot = null) =>
        new(Task.FromResult(new DirectoryConnection("stub directory", 5)))
        {
            LoadScopeResult = Task.FromResult(snapshot ?? new DirectorySnapshot()),
        };

    /// <summary>Workspace VM rooted at <see cref="RootDn"/> (the S6 ctor shape), null ruleset
    /// => the embedded default.</summary>
    private static WorkspaceViewModel Workspace(
        StubDirectoryProvider provider, Func<IGraphRenderer> rendererFactory) =>
        new(
            provider,
            Obj("Lab", RootDn, AdObjectKind.OrganizationalUnit),
            new DirectoryConnection("stub directory", 5),
            webView2Missing: false,
            rendererFactory);

    /// <summary>The full workspace view in a sized, shown headless window (bindings live, the
    /// sidebar realized) — the ViolationsSidebarViewTests/DetailPanelViewTests hosting idiom.</summary>
    private static (Window Window, WorkspaceView View) ShowWorkspace(WorkspaceViewModel vm)
    {
        var view = new WorkspaceView { DataContext = vm };
        var window = new Window { Content = view, Width = 1280, Height = 720 };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return (window, view);
    }
}
