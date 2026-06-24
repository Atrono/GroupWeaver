using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;

using GroupWeaver.App.Graph;
using GroupWeaver.App.Settings;
using GroupWeaver.App.Tests.Fakes;
using GroupWeaver.App.ViewModels;
using GroupWeaver.App.Views;
using GroupWeaver.Core.Model;
using GroupWeaver.Providers;

using Xunit;

namespace GroupWeaver.App.Tests;

/// <summary>
/// Pins the WP4 (#148) detail-panel VIEW additions inside the <c>DetailPanelRegion</c> seam:
/// the audit-chip <c>ItemsControl</c> (one chip per finding-bearing class, collapsing when the
/// chip list is empty) and the privacy-baseline note ("Showing {N} whitelisted attributes …",
/// N = <c>Rows.Count</c>, gated to <c>Rows.Count &gt; 0</c>). The chip TEXT and the note TEXT
/// live only in axaml, so these are the view-realization facts that pin the binding source and
/// the visibility gate; the structured projection is pinned in <c>DetailPanelAuditChipTests</c>
/// and the converter output in <c>SeverityConvertersTests</c>.
///
/// Rendered over the REAL DemoProvider (so the workspace holds a genuine RuleReport — the
/// 19-finding default-ruleset baseline) with a FRESH temp-dir <see cref="UiStateStore"/>
/// injected (#124 / ADR-022 D4): never touches the real %APPDATA% ui-state.json, so a
/// persisted RailCollapsed:true cannot collapse the right rail to width 0 and starve the
/// detail-panel realization these facts assert over.
/// </summary>
public sealed class DetailPanelChipViewTests
{
    private const string DemoRootDn = "OU=AGDLP-Demo,DC=weavedemo,DC=example";
    private const string GroupSuffix = ",OU=Groups,OU=AGDLP-Demo,DC=weavedemo,DC=example";

    // A flagged DN (DL<-DL nesting Error + empty-group Info) and a clean, loaded DN.
    private const string DlNestedRoDn = "CN=DL_Nested_RO" + GroupSuffix;
    private const string CleanGroupDn = "CN=GG_Sales_Staff" + GroupSuffix;

    // A frontier endpoint: a member-edge target outside Objects (no report findings, no rows).
    private const string DomainAdminsDn = "CN=Domain Admins,CN=Users,DC=weavedemo,DC=example";

    // --- flagged DN: the audit chips render with their converter labels -----------------

    [AvaloniaFact]
    public async Task FlaggedDnClick_RealizesItsAuditChips_WithTheCountedLabels()
    {
        var (window, view, fake) = await ShowDemoWorkspaceAsync();

        fake.RaiseNodeClicked(DlNestedRoDn, "DomainLocalGroup");
        Dispatcher.UIThread.RunJobs();

        var texts = VisibleTexts(Region(view, "DetailPanelRegion"));

        // The two finding-bearing classes' chips, each rendered through ChipToLabel ("Label N").
        Assert.Contains("Nesting 1", texts);
        Assert.Contains("Empty group 1", texts);
        // The pass chip must NOT appear for a flagged DN.
        Assert.DoesNotContain("No findings", texts);

        window.Close();
    }

    // --- clean DN: the single green pass chip renders -----------------------------------

    [AvaloniaFact]
    public async Task CleanDnClick_RealizesTheSingleNoFindingsPassChip()
    {
        var (window, view, fake) = await ShowDemoWorkspaceAsync();

        fake.RaiseNodeClicked(CleanGroupDn, "GlobalGroup");
        Dispatcher.UIThread.RunJobs();

        Assert.Contains("No findings", VisibleTexts(Region(view, "DetailPanelRegion")));

        window.Close();
    }

    // --- privacy note: count = Rows.Count, gated to Rows.Count > 0 ----------------------

    [AvaloniaFact]
    public async Task LoadedDnWithAttributes_RealizesThePrivacyNote_WithTheRowsCount()
    {
        var (window, view, fake) = await ShowDemoWorkspaceAsync();

        fake.RaiseNodeClicked(CleanGroupDn, "GlobalGroup");
        Dispatcher.UIThread.RunJobs();

        var model = Assert.IsType<WorkspaceViewModel>(view.DataContext).DetailPanel;
        Assert.NotNull(model);
        Assert.True(model.Rows.Count > 0); // guard: this DN actually has whitelisted attributes

        // The note's binding source IS Rows.Count: the realized note text carries exactly that
        // number and the privacy-baseline wording (StringFormat lives in axaml, so this pins it).
        var region = Region(view, "DetailPanelRegion");
        Assert.Contains(
            region.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible),
            t => t.Text is { } s
                && s.Contains($"Showing {model.Rows.Count} whitelisted attributes", StringComparison.Ordinal)
                && s.Contains("hidden by the privacy baseline", StringComparison.Ordinal));

        window.Close();
    }

    [AvaloniaFact]
    public async Task FrontierDnWithNoRows_OmitsThePrivacyNote_AndShowsNoChips()
    {
        var (window, view, fake) = await ShowDemoWorkspaceAsync();

        // A frontier DN (Domain Admins is a raw member edge target, outside Objects): NotLoaded,
        // zero rows -> the note is gated OFF (Rows.Count > 0 false) and there are no chips.
        fake.RaiseNodeClicked(DomainAdminsDn, "External");
        Dispatcher.UIThread.RunJobs();

        var model = Assert.IsType<WorkspaceViewModel>(view.DataContext).DetailPanel;
        Assert.NotNull(model);
        Assert.Equal(DetailPanelState.NotLoaded, model.State);
        Assert.Empty(model.Rows);

        var texts = VisibleTexts(Region(view, "DetailPanelRegion"));
        Assert.DoesNotContain(texts, t => t is { } s
            && s.Contains("whitelisted attributes", StringComparison.Ordinal));

        // A frontier DN (absent from Objects) takes Build's early-return branch: AuditChips is
        // EMPTY even with a report present (no pass chip - the "No findings" pass chip is only for
        // KNOWN-but-clean objects; a never-fetched frontier DN makes no audit claim at all). The
        // chip ItemsControl therefore collapses entirely.
        Assert.Empty(model.AuditChips);
        Assert.DoesNotContain("No findings", texts);
        Assert.DoesNotContain("Nesting 1", texts);

        window.Close();
    }

    // --- helpers ------------------------------------------------------------------------

    /// <summary>A workspace over the REAL DemoProvider rooted at the demo OU (the full
    /// 19-finding scope under the embedded default ruleset), rendered in a sized headless
    /// window with a fresh temp-dir UiStateStore (#124).</summary>
    private static async Task<(Window Window, WorkspaceView View, FakeGraphRenderer Fake)> ShowDemoWorkspaceAsync()
    {
        var provider = new DemoProvider();
        var root = await provider.GetObjectAsync(DemoRootDn);
        Assert.NotNull(root);
        var fake = new FakeGraphRenderer();
        var vm = new WorkspaceViewModel(
            provider,
            root!,
            await provider.ConnectAsync(),
            webView2Missing: false,
            () => fake,
            uiStateStore: new UiStateStore(
                Directory.CreateTempSubdirectory("groupweaver-chipview-uistate-").FullName));

        var view = new WorkspaceView { DataContext = vm };
        var window = new Window { Content = view, Width = 1280, Height = 720 };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        await vm.Initialization;
        Dispatcher.UIThread.RunJobs();
        return (window, view, fake);
    }

    private static ContentControl Region(WorkspaceView view, string name) =>
        Assert.Single(view.GetVisualDescendants().OfType<ContentControl>(), c => c.Name == name);

    private static List<string?> VisibleTexts(Visual scope) =>
        scope.GetVisualDescendants()
            .OfType<TextBlock>()
            .Where(t => t.IsEffectivelyVisible)
            .Select(t => t.Text)
            .ToList();
}
