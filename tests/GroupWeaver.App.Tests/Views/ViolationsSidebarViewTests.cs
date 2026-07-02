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

namespace GroupWeaver.App.Tests.Views;

/// <summary>
/// Pins the AP 3.4 selection-sync HIGHLIGHT at the VIEW layer (ADR-010 §5): the
/// <see cref="ViolationsSidebarView"/> must render the active-row state, not merely
/// compute it. <see cref="WorkspaceViolationsTests"/> already pins the VM half — that
/// <see cref="WorkspaceViewModel.SelectedDn"/> flips <see cref="ViolationRowModel.IsActive"/>
/// on every row whose <see cref="ViolationRowModel.PrimaryDn"/> matches under
/// <c>Dn.Comparer</c>. This test closes the view gap: it drives the real
/// <see cref="WorkspaceViewModel"/> behind the real view through a headless window, selects a
/// finding's anchor, and asserts the corresponding row CONTAINER actually repaints.
///
/// <para>RE-PINNED for #198 (the "Why it matters" drill-path surface): the findings-row template
/// was restructured from a single command <see cref="Button"/> into an OUTER
/// <see cref="Grid"/> that now OWNS the selection-highlight background, with the jump
/// <see cref="Button"/> spanning underneath and a subtle sibling "Why?" <see cref="Button"/>
/// (its <see cref="FlyoutBase">flyout</see> reveals the rationale). The original intent is
/// preserved verbatim — the row still jumps on click via a Button carrying
/// <c>CommandParameter = <see cref="ViolationRowModel.PrimaryDn"/></c>, the active row is
/// highlighted, and the cold row stays transparent — but the highlight moved from the row
/// Button's <see cref="Button.Background"/> to the outer <see cref="Panel.Background"/>. This
/// file now addresses that outer Grid (not the Button) for the background assertion, and
/// additionally pins that the new "Why?" affordance is present per row and is a SEPARATE
/// control from the jump Button (so opening its flyout can never fire the jump).</para>
///
/// THE PINNED CONTRACT:
///   • The row's OUTER <see cref="Grid"/> owns the highlight; it contains the jump
///     <see cref="Button"/> (bound <c>CommandParameter</c> = <see cref="ViolationRowModel.PrimaryDn"/>,
///     the stable anchor to locate the row by) and, top-right, the sibling "Why?" Button.
///   • Active (<c>IsActive == true</c>): the outer Grid's <see cref="Panel.Background"/> is a
///     solid brush of the pinned per-theme highlight color — <see cref="HighlightHex"/> (#298B7BFF)
///     under the app-default DARK theme, <see cref="HighlightLightHex"/> (#1F6A5CFF) under LIGHT —
///     the ADR-026 D6 brand-accent-soft selection band, a subtle purple wash behind the row text.
///   • Inactive (<c>IsActive == false</c>): the outer Grid's background stays
///     <see cref="Colors.Transparent"/> (the active-band-host cold base).
///   • #227: the band RE-TONES LIVE on a theme flip (it is theme-resolved, never a fixed-dark
///     brush) — <see cref="ActiveRowBand_ReTonesLive_WhenTheThemeFlipsToLight"/>.
/// Change the pinned hexes ONLY by editing these constants AND src/App/Styles/Tokens.axaml
/// (both theme variants) together in one reviewed PR (the data-model.md "change only with a
/// reviewed PR" discipline).
/// </summary>
public sealed class ViolationsSidebarViewTests
{
    private const string RootDn = "OU=Lab,DC=stub,DC=lab";

    // Two well-named (naming-gg: GG_<Token>_<Token>, two PascalCase tokens), loaded-and-EMPTY
    // global groups => EXACTLY two empty-group Info findings under the default ruleset (no
    // naming warning to inflate the count), each anchored at its own DN. Exactly two sidebar
    // rows => both are realized (no virtualization to hide either), so one is the
    // selected/active row and the other is the cold comparison row.
    private const string GroupADn = "CN=GG_Sales_Staff,OU=Lab,DC=stub,DC=lab";
    private const string GroupBDn = "CN=GG_Sales_Admin,OU=Lab,DC=stub,DC=lab";

    /// <summary>The pinned DARK-theme selection-highlight color (the headless test app boots the
    /// real <c>App</c>, whose default theme is Dark). DELIBERATELY MOVED (a reviewed contract
    /// change, not drift — #225 Lever 4): the active-row band was the old Fluent-blue #330F6CBD
    /// (20%-alpha of the legacy app accent #0F6CBD); it is now the ADR-026 D6 brand accent-soft
    /// #298B7BFF (= <c>BrandTokens.AccentSoftHex</c>), the shared translucent-accent
    /// selection/focus role. #227 THEME-RESOLVES the band: the active row carries the
    /// <c>active-band</c> style class (<c>Classes.active-band ← IsActive</c>) and the shared
    /// App.axaml <c>active-band</c> style paints it from <c>{DynamicResource AccentSoftBrush}</c>,
    /// whose Tokens.axaml ThemeDictionaries resolve dark #298B7BFF / light
    /// <see cref="HighlightLightHex"/>. Change the hex ONLY by editing this constant AND
    /// src/App/Styles/Tokens.axaml (both variants) together in one reviewed PR (the
    /// data-model.md "change only with a reviewed PR" discipline).</summary>
    private const string HighlightHex = "#298B7BFF"; // BrandTokens.AccentSoftHex (ADR-026 D6, #225 Lever 4)

    /// <summary>The pinned LIGHT-theme selection-highlight color (#227): what the SAME active
    /// row's band must re-tone to when the theme flips to Light —
    /// <c>BrandTokens.AccentSoftLightHex</c>, the light arm of the Tokens.axaml
    /// <c>AccentSoftBrush</c> ThemeDictionaries. Same change discipline as
    /// <see cref="HighlightHex"/>: this constant AND Tokens.axaml (both variants), one
    /// reviewed PR.</summary>
    private const string HighlightLightHex = "#1F6A5CFF"; // BrandTokens.AccentSoftLightHex (#227)

    /// <summary>Guard: the pinned <see cref="HighlightHex"/>/<see cref="HighlightLightHex"/> ARE
    /// the brand accent-soft tokens (#225 Lever 4; light arm #227). This keeps the deliberate
    /// values from silently drifting away from <see cref="BrandTokens.AccentSoftHex"/> /
    /// <see cref="BrandTokens.AccentSoftLightHex"/> — the whole point of the move was to route
    /// the active-row band through that shared ADR-026 accent role — while still pinning the
    /// literal hexes above so a wrong token AND a wrong hex can't cancel out.</summary>
    [Fact]
    public void HighlightHexes_AreTheBrandAccentSoftTokens()
    {
        Assert.Equal(BrandTokens.AccentSoftHex, HighlightHex);
        Assert.Equal(Color.Parse(BrandTokens.AccentSoftHex), Color.Parse(HighlightHex));
        Assert.Equal(BrandTokens.AccentSoftLightHex, HighlightLightHex);
        Assert.Equal(Color.Parse(BrandTokens.AccentSoftLightHex), Color.Parse(HighlightLightHex));
    }

    [AvaloniaFact]
    public async Task SelectingAFindingsAnchor_PaintsThatRowsBackground_TheHighlight_LeavingOtherRowsTransparent()
    {
        var fake = new FakeGraphRenderer();
        var vm = Workspace(Provider(TwoEmptyGroupsScope()), () => fake);
        var (window, _) = ShowWorkspace(vm);
        await vm.Initialization;
        Dispatcher.UIThread.RunJobs();

        // The sidebar populated: exactly the two empty-group rows, one per group anchor.
        Assert.Equal(2, vm.Violations.Count);
        Assert.Contains(vm.Violations, r => Dn.Comparer.Equals(r.PrimaryDn, GroupADn));
        Assert.Contains(vm.Violations, r => Dn.Comparer.Equals(r.PrimaryDn, GroupBDn));

        // Select GG_Alpha's finding anchor — the VM flips IsActive on its row (the VM
        // half is pinned by WorkspaceViolationsTests; here we look at the VIEW).
        vm.SelectedDn = GroupADn;
        Dispatcher.UIThread.RunJobs();

        // The VM did flip the model flag — guard so a red here is unambiguously the
        // missing BINDING, never an unflipped model.
        Assert.True(
            vm.Violations.Single(r => Dn.Comparer.Equals(r.PrimaryDn, GroupADn)).IsActive,
            "the VM must mark the selected row active (the WorkspaceViolationsTests contract)");
        Assert.False(
            vm.Violations.Single(r => Dn.Comparer.Equals(r.PrimaryDn, GroupBDn)).IsActive,
            "the non-selected row must stay inactive");

        var sidebar = Assert.Single(window.GetVisualDescendants().OfType<ViolationsSidebarView>());
        // #198: the highlight moved to the row's OUTER Grid (the Grid that OWNS the jump Button).
        var activeRow = RowGridFor(sidebar, GroupADn);
        var coldRow = RowGridFor(sidebar, GroupBDn);

        var activeBrush = Assert.IsAssignableFrom<ISolidColorBrush>(activeRow.Background);
        var coldBrush = Assert.IsAssignableFrom<ISolidColorBrush>(coldRow.Background);

        // (1) the selected row's container repaints to the pinned highlight color …
        Assert.Equal(Color.Parse(HighlightHex), activeBrush.Color);
        // (2) … the cold row stays the template's transparent default …
        Assert.Equal(Colors.Transparent, coldBrush.Color);
        // (3) … so a selected row is visibly distinct from a cold one (the whole point:
        //     against a no-binding template both are Transparent and this differential fails).
        Assert.NotEqual(coldBrush.Color, activeBrush.Color);

        window.Close();
    }

    /// <summary>
    /// #227 (theme-resolve the active-row band): the band must RE-TONE LIVE when the theme flips
    /// to Light — the SAME active row's outer-Grid background re-resolves from the pinned dark
    /// <see cref="HighlightHex"/> (#298B7BFF) to the pinned light <see cref="HighlightLightHex"/>
    /// (#1F6A5CFF), while a cold row stays <see cref="Colors.Transparent"/> in both themes. A
    /// fixed-dark band (the #227 defect: a hard-coded dark brush fed to the row background) fails
    /// exactly here: after the flip the band still reads #298B7BFF.
    ///
    /// <para>The flip is WINDOW-SCOPED (<see cref="TopLevel.RequestedThemeVariant"/> on the
    /// test's own window): probed to re-resolve App.axaml style-setter DynamicResources under
    /// Avalonia.Headless (via the existing <c>ListBoxItem:selected</c> →
    /// <c>{DynamicResource AccentSoftBrush}</c> style), and — unlike the
    /// <c>Application.Current.RequestedThemeVariant</c> flip <c>ThemeVariantScreenshotTests</c>
    /// uses — it touches no shared global app state (the <c>ShellThemeTests</c> flakiness
    /// warning), so no restore step can be forgotten: the scope dies with the window.</para>
    /// </summary>
    [AvaloniaFact]
    public async Task ActiveRowBand_ReTonesLive_WhenTheThemeFlipsToLight()
    {
        var fake = new FakeGraphRenderer();
        var vm = Workspace(Provider(TwoEmptyGroupsScope()), () => fake);
        var (window, _) = ShowWorkspace(vm);
        await vm.Initialization;
        Dispatcher.UIThread.RunJobs();

        // Select GG_Sales_Staff's anchor so its row is the active one (the VM half is pinned
        // by WorkspaceViolationsTests; the dark-theme paint by the test above).
        vm.SelectedDn = GroupADn;
        Dispatcher.UIThread.RunJobs();

        var sidebar = Assert.Single(window.GetVisualDescendants().OfType<ViolationsSidebarView>());
        var activeRow = RowGridFor(sidebar, GroupADn);
        var coldRow = RowGridFor(sidebar, GroupBDn);

        // Baseline under the app-default DARK theme: the pinned dark band.
        var darkBrush = Assert.IsAssignableFrom<ISolidColorBrush>(activeRow.Background);
        Assert.Equal(Color.Parse(HighlightHex), darkBrush.Color);

        // Flip THIS WINDOW's theme scope to Light and let the dispatcher re-resolve.
        window.RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Light;
        Dispatcher.UIThread.RunJobs();

        // The SAME row's band re-toned to the pinned LIGHT accent-soft value …
        var lightBrush = Assert.IsAssignableFrom<ISolidColorBrush>(activeRow.Background);
        Assert.Equal(Color.Parse(HighlightLightHex), lightBrush.Color);
        // … and the cold row is STILL transparent (the theme flip must not paint cold rows).
        var coldBrush = Assert.IsAssignableFrom<ISolidColorBrush>(coldRow.Background);
        Assert.Equal(Colors.Transparent, coldBrush.Color);

        window.Close();
    }

    /// <summary>
    /// #198 (the drill-path rationale surface): each findings row carries a "Why?" affordance that is
    /// a SEPARATE control from the jump Button — so opening its flyout can never fire JumpCommand — yet
    /// shares the row's outer Grid (and thus its selection-highlight). This pins the restructured row's
    /// two-control shape at the view layer: the jump Button (carrying the anchor CommandParameter) and
    /// the sibling "Why?" Button both live under the row's one outer Grid, and the "Why?" Button does
    /// NOT carry the jump command's PrimaryDn parameter (it opens a flyout, it does not jump).
    /// </summary>
    [AvaloniaFact]
    public async Task EachRow_HasAWhyButton_SeparateFromTheJumpButton_UnderTheSharedRowGrid()
    {
        var fake = new FakeGraphRenderer();
        var vm = Workspace(Provider(TwoEmptyGroupsScope()), () => fake);
        var (window, _) = ShowWorkspace(vm);
        await vm.Initialization;
        Dispatcher.UIThread.RunJobs();

        var sidebar = Assert.Single(window.GetVisualDescendants().OfType<ViolationsSidebarView>());

        foreach (var anchor in new[] { GroupADn, GroupBDn })
        {
            var rowGrid = RowGridFor(sidebar, anchor);

            // The jump Button (the anchor-carrying command button) lives under this outer Grid.
            var jumpButton = Assert.Single(
                rowGrid.GetVisualDescendants().OfType<Button>(),
                b => b.CommandParameter as string == anchor);

            // The "Why?" affordance is a DISTINCT, effectively-visible Button under the SAME outer
            // Grid — never the jump Button — and it does NOT carry the jump's PrimaryDn parameter
            // (so an opened flyout can't be mistaken for a jump). Located by its "Why?" content.
            var whyButton = Assert.Single(
                rowGrid.GetVisualDescendants().OfType<Button>(),
                b => b.IsEffectivelyVisible && (b.Content as string) == "Why?");
            Assert.NotSame(jumpButton, whyButton);
            Assert.NotEqual(anchor, whyButton.CommandParameter as string);
        }

        window.Close();
    }

    // --- helpers ------------------------------------------------------------------------

    private static AdObject Obj(
        string name, string dn, AdObjectKind kind = AdObjectKind.GlobalGroup) =>
        new() { Dn = dn, Kind = kind, Name = name, SamAccountName = name };

    /// <summary>Root OU + two well-named, loaded-and-EMPTY global groups (each a deliberate
    /// empty-group Info finding under the default ruleset; the names pass naming-gg, so the
    /// ONLY findings are the two empty-group infos) — exactly two sidebar rows.</summary>
    private static DirectorySnapshot TwoEmptyGroupsScope()
    {
        var snapshot = new DirectorySnapshot();
        snapshot.AddObject(Obj("Lab", RootDn, AdObjectKind.OrganizationalUnit));
        snapshot.AddObject(Obj("GG_Sales_Staff", GroupADn));
        snapshot.AddObject(Obj("GG_Sales_Admin", GroupBDn));
        snapshot.SetMembers(GroupADn, []); // loaded-and-empty => empty-group finding
        snapshot.SetMembers(GroupBDn, []); // loaded-and-empty => empty-group finding
        return snapshot;
    }

    /// <summary>The row template's OUTER <see cref="Grid"/> for the row whose jump Button carries
    /// <c>CommandParameter</c> (= <see cref="ViolationRowModel.PrimaryDn"/>) equal to
    /// <paramref name="primaryDn"/> — the #198 restructure moved the selection-highlight background
    /// onto this outer Grid (it OWNS the jump Button + the sibling "Why?" Button). Located by first
    /// finding the realized jump Button (the stable, template-independent anchor handle) then walking
    /// up to its nearest <see cref="Grid"/> ancestor: the Button's own content Grid is a DESCENDANT,
    /// so the nearest ancestor Grid is the row's outer container. Asserts the row is realized (a single
    /// jump-Button match), so virtualization hiding it fails loudly, not as a null deref.</summary>
    private static Grid RowGridFor(ViolationsSidebarView sidebar, string primaryDn)
    {
        var jumpButton = Assert.Single(
            sidebar.GetVisualDescendants().OfType<Button>(),
            b => b.IsEffectivelyVisible && b.CommandParameter as string == primaryDn);
        return jumpButton.GetVisualAncestors().OfType<Grid>().First();
    }

    /// <summary>Stub whose scope load yields <paramref name="snapshot"/>.</summary>
    private static StubDirectoryProvider Provider(DirectorySnapshot snapshot) =>
        new(Task.FromResult(new DirectoryConnection("stub directory", 5)))
        {
            LoadScopeResult = Task.FromResult(snapshot),
        };

    /// <summary>Workspace VM rooted at <see cref="RootDn"/> (AP 2.2 S6 ctor shape), null
    /// ruleset => the embedded default (the 19-finding-baseline contract; here it yields the
    /// two empty-group findings of the stub scope). Fresh temp-dir UiStateStore (#124 /
    /// ADR-022 D4): never touches the real %APPDATA% ui-state.json, so a persisted
    /// RailCollapsed:true cannot collapse the right rail and starve the sidebar realization
    /// this file asserts over.</summary>
    private static WorkspaceViewModel Workspace(
        StubDirectoryProvider provider, Func<IGraphRenderer> rendererFactory) =>
        new(
            provider,
            Obj("Lab", RootDn, AdObjectKind.OrganizationalUnit),
            new DirectoryConnection("stub directory", 5),
            webView2Missing: false,
            rendererFactory,
            uiStateStore: new GroupWeaver.App.Settings.UiStateStore(
                System.IO.Directory.CreateTempSubdirectory("groupweaver-violsidebar-uistate-").FullName));

    /// <summary>The full workspace view in a sized, shown headless window (bindings live,
    /// the sidebar realized) — the DetailPanelViewTests hosting idiom.</summary>
    private static (Window Window, WorkspaceView View) ShowWorkspace(WorkspaceViewModel vm)
    {
        var view = new WorkspaceView { DataContext = vm };
        var window = new Window { Content = view, Width = 1280, Height = 720 };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return (window, view);
    }
}
