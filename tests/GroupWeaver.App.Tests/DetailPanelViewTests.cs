using System.Globalization;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;

using GroupWeaver.App.Graph;
using GroupWeaver.App.Tests.Fakes;
using GroupWeaver.App.ViewModels;
using GroupWeaver.App.Views;
using GroupWeaver.Core.Model;
using GroupWeaver.Core.Providers;

using Xunit;

namespace GroupWeaver.App.Tests;

/// <summary>
/// Pins the AP 2.5 S3 detail-panel VIEW inside the <c>DetailPanelRegion</c> seam
/// (ADR-007): the region's teaser/raw-DN placeholders give way to a DetailPanelView
/// whose <c>x:DataType</c> is the <see cref="DetailPanelModel"/> projection — these
/// facts only ever look at rendered text/geometry through the region, never at a
/// domain object. Pinned here: the no-selection placeholder; header name + kind badge
/// label (in PARITY with <see cref="AdObjectKindConverters.ToBadgeLabel"/> — derived,
/// never hardcoded); attribute rows with label AND value as their own text elements
/// (values wrap, ADR-007 D4); the NotLoaded resolve hint for frontier clicks (D3);
/// the airspace pin (panel content stays inside the right detail column, never over
/// GraphHost — ADR-001 guardrail 5); and the DN rendered VERBATIM in a text-SELECTABLE
/// control (D4: the panel is the full-value surface). The VM-level projection contract
/// lives in <c>WorkspaceDetailTests</c>; these are the Avalonia.Headless view facts.
/// </summary>
public sealed class DetailPanelViewTests
{
    private const string RootDn = "OU=Lab,DC=stub,DC=lab";
    private const string SalesDn = "CN=GG_Sales,OU=Lab,DC=stub,DC=lab";
    private const string EdgeGroupDn = "CN=GG_Edge,OU=Lab,DC=stub,DC=lab";

    // Escaped-comma DN on purpose: DN strings flow verbatim (data-model rule — never
    // canonicalized), so the backslash escape must survive into the rendered text.
    private const string EscapedDn = "CN=x\\, y,OU=Lab,DC=stub,DC=lab";

    /// <summary>A member-edge endpoint that is NOT in Snapshot.Objects (frontier).</summary>
    private const string FrontierDn = "CN=Frontier,DC=elsewhere,DC=lab";

    /// <summary>The AP 2.5 no-selection placeholder (shipped UI strings are English).</summary>
    private const string Placeholder = "Click a node to inspect it.";

    /// <summary>The pre-AP-2.5 teaser this slice REPLACES.</summary>
    private const string Teaser = "Detail panel arrives in AP 2.5";

    /// <summary>ADR-022 D3 re-baseline: the rail moved <c>*,300</c> ⇒ <c>*, Auto, {rail}</c> with
    /// a 340px default rail and a 14px <c>Auto</c> seam (GridSplitter + ◂/▸ chevron) BESIDE
    /// GraphHost. GraphHost (col 0) therefore ends 354px (340 rail + 14 seam) from the right
    /// edge — that boundary is the airspace line (ADR-001 guardrail 5): all detail content must
    /// sit at or right of it, never over the graph. Derived from the production layout, not a
    /// magic number; a panel that ever strayed over GraphHost would render left of it and fail.</summary>
    private const double RailLeftEdgeFromRight = 340 + 14;

    // --- (a) no selection: the placeholder, the teaser is gone -------------------------

    [AvaloniaFact]
    public async Task NoSelection_ShowsTheClickToInspectPlaceholder_ReplacingTheTeaser()
    {
        var vm = Workspace(Provider(PanelScope()), () => new FakeGraphRenderer());
        var (window, view) = ShowWorkspace(vm);
        await vm.Initialization;
        Dispatcher.UIThread.RunJobs();

        Assert.Null(vm.SelectedDn);
        var texts = VisibleTexts(Region(view, "DetailPanelRegion"));
        Assert.Contains(Placeholder, texts);
        Assert.DoesNotContain(Teaser, texts); // AP 2.5 replaces the teaser placeholder

        window.Close();
    }

    // --- (b) group click: header name, kind badge label (converter parity), rows -------

    [AvaloniaFact]
    public async Task GroupClick_ShowsName_KindBadgeLabel_AndAttributeRowsWithValues()
    {
        var fake = new FakeGraphRenderer();
        var vm = Workspace(Provider(PanelScope()), () => fake);
        var (window, view) = ShowWorkspace(vm);
        await vm.Initialization;

        fake.RaiseNodeClicked(SalesDn, "GlobalGroup");
        Dispatcher.UIThread.RunJobs();

        var region = Region(view, "DetailPanelRegion");
        var texts = VisibleTexts(region);

        // Header: the object NAME as its own text element (not buried in the DN).
        Assert.Contains("GG_Sales", texts);

        // Kind badge label in PARITY with the one converter palette (root picker, graph,
        // panel must never diverge): the expectation is DERIVED, never hardcoded.
        Assert.Contains(BadgeLabelFor(AdObjectKind.GlobalGroup), texts);

        // Attribute rows: label AND value each as their own text element — the
        // projection's Rows verbatim, values included (ADR-007 D2/D4).
        Assert.Contains("description", texts);
        Assert.Contains("sales group", texts);
        Assert.Contains("whenCreated", texts);
        Assert.Contains("2024-02-02T08:30:00Z", texts);
        Assert.Contains("groupType", texts);
        Assert.Contains("-2147483646", texts);

        // Values wrap (ADR-007 D4: the panel is the full-value surface, never truncates).
        var valueBlock = Assert.Single(
            region.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == "sales group");
        Assert.Equal(TextWrapping.Wrap, valueBlock.TextWrapping);

        // A selection replaces BOTH placeholders.
        Assert.DoesNotContain(Placeholder, texts);
        Assert.DoesNotContain(Teaser, texts);

        window.Close();
    }

    // --- (c) frontier click: the NotLoaded resolve hint (ADR-007 D3) -------------------

    [AvaloniaFact]
    public async Task FrontierClick_ShowsTheNotLoadedResolveHint()
    {
        var fake = new FakeGraphRenderer();
        var vm = Workspace(Provider(PanelScope()), () => fake);
        var (window, view) = ShowWorkspace(vm);
        await vm.Initialization;

        fake.RaiseNodeClicked(FrontierDn, "External");
        Dispatcher.UIThread.RunJobs();

        var region = Region(view, "DetailPanelRegion");
        var texts = VisibleTexts(region);

        // The hint's wording is the implementer's; pinned essentials: it states the
        // object is not loaded and names the Refresh affordance (exact button casing).
        Assert.Contains(
            region.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible),
            t => t.Text?.Contains("not loaded", StringComparison.OrdinalIgnoreCase) == true
                && t.Text.Contains("Refresh", StringComparison.Ordinal));

        // The header still carries the DN verbatim in EVERY state (ADR-007 D3) …
        Assert.Contains(FrontierDn, texts);
        // … and honesty means NO fabricated rows for a never-fetched frontier DN.
        Assert.DoesNotContain("description", texts);
        Assert.DoesNotContain(Placeholder, texts);

        window.Close();
    }

    // --- (d) airspace: panel content stays inside grid column 1 ------------------------

    [AvaloniaFact]
    public async Task PanelContent_StaysInsideTheDetailColumn_NeverOverGraphHost()
    {
        var fake = new FakeGraphRenderer();
        var vm = Workspace(Provider(PanelScope()), () => fake);
        var (window, view) = ShowWorkspace(vm);
        await vm.Initialization;

        fake.RaiseNodeClicked(SalesDn, "GlobalGroup");
        Dispatcher.UIThread.RunJobs();

        var region = Region(view, "DetailPanelRegion");
        Assert.Contains("GG_Sales", VisibleTexts(region)); // the panel actually rendered

        // The airspace pin (ADR-001 guardrail 5, the WorkspaceLoadTests pattern): the
        // region AND every rendered text of the panel live in the right 300px detail
        // column — nothing may ever stray over GraphHost's native-HWND territory.
        var regionTop = region.TranslatePoint(new Point(0, 0), view);
        Assert.NotNull(regionTop);
        // ADR-022 D3: the rail is now 340px + a 14px Auto seam, so the detail column sits
        // right of GraphHost's (Width-354) right edge — never over the graph (ADR-001 #5).
        Assert.True(
            regionTop.Value.X >= view.Bounds.Width - RailLeftEdgeFromRight,
            $"the DetailPanelRegion belongs in the right detail column (was at X={regionTop.Value.X})");

        foreach (var text in region.GetVisualDescendants()
            .OfType<TextBlock>().Where(t => t.IsEffectivelyVisible))
        {
            var topLeft = text.TranslatePoint(new Point(0, 0), view);
            Assert.NotNull(topLeft);
            Assert.True(
                topLeft.Value.X >= view.Bounds.Width - RailLeftEdgeFromRight,
                $"'{text.Text}' strayed left of the detail column (X={topLeft.Value.X})");
            Assert.True(
                topLeft.Value.X + text.Bounds.Width <= view.Bounds.Width + 0.5,
                $"'{text.Text}' overflows the right edge (X={topLeft.Value.X}, W={text.Bounds.Width})");
        }

        // And none of the panel's content leaks into GraphHost itself.
        Assert.DoesNotContain("GG_Sales", VisibleTexts(Region(view, "GraphHost")));

        window.Close();
    }

    // --- (e) the DN: rendered verbatim AND text-selectable (ADR-007 D4) ----------------

    [AvaloniaFact]
    public async Task DnValue_RendersVerbatim_InASelectableTextControl()
    {
        var fake = new FakeGraphRenderer();
        var vm = Workspace(Provider(PanelScope()), () => fake);
        var (window, view) = ShowWorkspace(vm);
        await vm.Initialization;

        fake.RaiseNodeClicked(EscapedDn, "User");
        Dispatcher.UIThread.RunJobs();

        // The DN as its OWN element, character-for-character (ordinal — the escaped
        // comma survives untouched; no label prefix, no truncation: it must be copyable).
        var region = Region(view, "DetailPanelRegion");
        var dnBlocks = region.GetVisualDescendants().OfType<TextBlock>()
            .Where(t => t.IsEffectivelyVisible && t.Text == EscapedDn)
            .ToList();
        Assert.NotEmpty(dnBlocks);

        // Selectability, pragmatically: the DN element is a SelectableTextBlock (the
        // TextBlock subclass with a real selection surface), and selecting all of it
        // yields the DN verbatim — the copy path cannot mangle what selection returns.
        var selectable = Assert.Single(dnBlocks.OfType<SelectableTextBlock>());
        selectable.SelectAll();
        Assert.Equal(EscapedDn, selectable.SelectedText);

        window.Close();
    }

    // --- helpers ------------------------------------------------------------------------

    /// <summary>The parity oracle: the badge label THE converter produces for
    /// <paramref name="kind"/> — the panel must agree with the root picker/graph palette.</summary>
    private static string BadgeLabelFor(AdObjectKind kind) =>
        Assert.IsType<string>(AdObjectKindConverters.ToBadgeLabel.Convert(
            kind, typeof(string), null, CultureInfo.InvariantCulture));

    private static AdObject Obj(
        string name, string dn, AdObjectKind kind = AdObjectKind.GlobalGroup) =>
        new() { Dn = dn, Kind = kind, Name = name };

    /// <summary>
    /// The S3 fixture scope: root OU; GG_Sales (group display set, scrambled insertion
    /// order); an escaped-comma-DN user; GG_Edge LOADED with [GG_Sales, FrontierDn] so
    /// the frontier DN is a real member-edge endpoint outside Objects.
    /// </summary>
    private static DirectorySnapshot PanelScope()
    {
        var snapshot = new DirectorySnapshot();
        snapshot.AddObject(Obj("Lab", RootDn, AdObjectKind.OrganizationalUnit));
        snapshot.AddObject(new AdObject
        {
            Dn = SalesDn,
            Kind = AdObjectKind.GlobalGroup,
            Name = "GG_Sales",
            SamAccountName = "GG_Sales",
            Attributes = new Dictionary<string, string>
            {
                // Scrambled on purpose — display order is the whitelist's, not the dict's.
                ["groupType"] = "-2147483646",
                ["description"] = "sales group",
                ["whenCreated"] = "2024-02-02T08:30:00Z",
            },
        });
        snapshot.AddObject(new AdObject
        {
            Dn = EscapedDn,
            Kind = AdObjectKind.User,
            Name = "x, y",
            SamAccountName = "x.y",
            Attributes = new Dictionary<string, string>
            {
                ["description"] = "escaped-comma DN fixture",
            },
        });
        snapshot.AddObject(Obj("GG_Edge", EdgeGroupDn));
        snapshot.SetMembers(EdgeGroupDn, [SalesDn, FrontierDn]);
        return snapshot;
    }

    /// <summary>Stub whose scope load yields <paramref name="snapshot"/>.</summary>
    private static StubDirectoryProvider Provider(DirectorySnapshot snapshot) =>
        new(Task.FromResult(new DirectoryConnection("stub directory", 5)))
        {
            LoadScopeResult = Task.FromResult(snapshot),
        };

    /// <summary>Workspace VM rooted at <see cref="RootDn"/> (AP 2.2 S6 ctor shape). Fresh
    /// temp-dir UiStateStore (#124 / ADR-022 D4): never touches the real %APPDATA%
    /// ui-state.json, so a persisted RailCollapsed:true cannot collapse the right rail and
    /// starve the detail-panel realization this file asserts over.</summary>
    private static WorkspaceViewModel Workspace(
        StubDirectoryProvider provider, Func<IGraphRenderer> rendererFactory) =>
        new(
            provider,
            Obj("Lab", RootDn, AdObjectKind.OrganizationalUnit),
            new DirectoryConnection("stub directory", 5),
            webView2Missing: false,
            rendererFactory,
            uiStateStore: new GroupWeaver.App.Settings.UiStateStore(
                System.IO.Directory.CreateTempSubdirectory("groupweaver-detailpanel-uistate-").FullName));

    /// <summary>Workspace view in a sized, shown headless window (bindings live).</summary>
    private static (Window Window, WorkspaceView View) ShowWorkspace(WorkspaceViewModel vm)
    {
        var view = new WorkspaceView { DataContext = vm };
        var window = new Window { Content = view, Width = 1280, Height = 720 };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return (window, view);
    }

    /// <summary>One of the two named seam regions (GraphHost / DetailPanelRegion).</summary>
    private static ContentControl Region(WorkspaceView view, string name) =>
        Assert.Single(
            view.GetVisualDescendants().OfType<ContentControl>(), c => c.Name == name);

    private static List<string?> VisibleTexts(Visual scope) =>
        scope.GetVisualDescendants()
            .OfType<TextBlock>()
            .Where(t => t.IsEffectivelyVisible)
            .Select(t => t.Text)
            .ToList();
}
