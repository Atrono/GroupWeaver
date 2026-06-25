using System;
using System.Linq;

using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;

using GroupWeaver.App.Tests.Fakes;
using GroupWeaver.App.ViewModels;
using GroupWeaver.App.Views;
using GroupWeaver.Core.Model;
using GroupWeaver.Core.Rules;

using Xunit;

namespace GroupWeaver.App.Tests.Views;

/// <summary>
/// Pins the WP2 / ADR-013 report-export PAIR at the VIEW layer: the "Export CSV" / "Export HTML"
/// buttons in <see cref="AuditView"/>'s actions row. <see cref="AuditExportTests"/> already pins the
/// VM half (gate, serialize+write, read-only invariant), so this test pins ONLY the rendered-button
/// wiring the VM tests cannot see (sister to <see cref="SidebarExportButtonsViewTests"/> on the
/// workspace side):
/// <list type="bullet">
///   <item><b>Both buttons render with their <c>Content</c></b> ("Export CSV" / "Export HTML"), located
///   by their stable seam <c>Name</c> (<c>ExportCsvButton</c>/<c>ExportHtmlButton</c>) — the
///   template-independent anchor.</item>
///   <item><b>Each is bound to the matching command instance</b> — CSV→<c>ExportReportCsvCommand</c>,
///   HTML→<c>ExportReportHtmlCommand</c>, never cross-wired (<c>Assert.Same</c>).</item>
///   <item><b>Disabled before the seam is installed; armed after</b> — the WP2 gate surfaced in the
///   RENDERED button (<c>IsEffectivelyEnabled</c>, not merely <c>CanExecute</c>). The audit step has no
///   load transition (its borrowed snapshot is non-null by ctor contract), so the pre-install→post-install
///   differential isolates the seam half of the gate (<c>_exportDialogs is not null</c>).</item>
/// </list>
///
/// <para>The audit is built over a cycle-bearing scope (GG_Circle_A↔GG_Circle_B), so the report build
/// that the buttons project MUST terminate over it (the circular-case charter). <see cref="AuditView"/>
/// owns no graph renderer and no <c>UiStateStore</c> seam, so the #124 rail-collapse realization hazard
/// does not apply here — it is a plain table UserControl.</para>
/// </summary>
public sealed class AuditExportButtonsViewTests
{
    private const string RootDn = "OU=Lab,DC=stub,DC=lab";
    private const string CircleADn = "CN=GG_Circle_A,OU=Lab,DC=stub,DC=lab";
    private const string CircleBDn = "CN=GG_Circle_B,OU=Lab,DC=stub,DC=lab";

    [AvaloniaFact]
    public void ExportButtons_RenderWithContent_BoundToTheExportCommands_DisabledUntilSeamInstalled()
    {
        var snapshot = CycleScope();
        var ruleset = RulesetLoader.LoadDefault();
        var report = RuleEngine.Evaluate(snapshot, ruleset); // MUST terminate over the A<->B cycle
        using var audit = new AuditViewModel(snapshot, report, ruleset, RootDn, onBack: () => { });

        var (window, _) = ShowAudit(audit);
        Dispatcher.UIThread.RunJobs();

        var view = Assert.Single(window.GetVisualDescendants().OfType<AuditView>());
        var csv = ExportButton(view, "ExportCsvButton");
        var html = ExportButton(view, "ExportHtmlButton");

        // (1) Both buttons render, visible, with their shipped English Content.
        Assert.True(csv.IsEffectivelyVisible);
        Assert.True(html.IsEffectivelyVisible);
        Assert.Equal("Export CSV", csv.Content);
        Assert.Equal("Export HTML", html.Content);

        // (2) Each is bound to the matching command instance — never cross-wired.
        Assert.Same(audit.ExportReportCsvCommand, csv.Command);
        Assert.Same(audit.ExportReportHtmlCommand, html.Command);

        // (3a) Disabled before the seam is installed: the rendered button reflects the seam gate
        //      (IsEffectivelyEnabled, not just CanExecute).
        Assert.False(
            csv.IsEffectivelyEnabled,
            "Export CSV must be disabled before the export seam is installed (gate = _exportDialogs is not null)");
        Assert.False(
            html.IsEffectivelyEnabled,
            "Export HTML must be disabled before the export seam is installed");

        // Install the seam — the SAME rendered buttons re-arm (the installer notifies CanExecuteChanged).
        audit.UseExportFileDialogs(new FakeExportDialogs());
        Dispatcher.UIThread.RunJobs();

        // (3b) Armed after the seam is installed.
        Assert.True(
            csv.IsEffectivelyEnabled,
            "Export CSV must arm once the export seam is installed");
        Assert.True(
            html.IsEffectivelyEnabled,
            "Export HTML must arm once the export seam is installed");

        window.Close();
    }

    // --- helpers ------------------------------------------------------------------------

    /// <summary>The actions-row export <see cref="Button"/> with the given seam <c>Name</c>
    /// (<c>ExportCsvButton</c>/<c>ExportHtmlButton</c>) — the stable, template-independent anchor.
    /// Asserts exactly one is realized, so a rename or virtualization hiding it fails loudly.</summary>
    private static Button ExportButton(AuditView view, string name) =>
        Assert.Single(view.GetVisualDescendants().OfType<Button>(), b => b.Name == name);

    /// <summary>Root OU + the GG_Circle_A ↔ GG_Circle_B nesting cycle (A→B→A) — the report build the
    /// buttons project MUST terminate over it.</summary>
    private static DirectorySnapshot CycleScope()
    {
        var snapshot = new DirectorySnapshot();
        snapshot.AddObject(new AdObject { Dn = RootDn, Kind = AdObjectKind.OrganizationalUnit, Name = "Lab" });
        snapshot.AddObject(new AdObject { Dn = CircleADn, Kind = AdObjectKind.GlobalGroup, Name = "GG_Circle_A" });
        snapshot.AddObject(new AdObject { Dn = CircleBDn, Kind = AdObjectKind.GlobalGroup, Name = "GG_Circle_B" });
        snapshot.SetMembers(CircleADn, new[] { CircleBDn });
        snapshot.SetMembers(CircleBDn, new[] { CircleADn }); // closes the A→B→A cycle
        return snapshot;
    }

    /// <summary>The audit view in a sized, shown headless window (bindings live, the actions row
    /// realized) — the SidebarExportButtonsViewTests hosting idiom, hosting AuditView directly.</summary>
    private static (Window Window, AuditView View) ShowAudit(AuditViewModel vm)
    {
        var view = new AuditView { DataContext = vm };
        var window = new Window { Content = view, Width = 1280, Height = 720 };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return (window, view);
    }
}
