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
/// <c>Dn.Comparer</c> — but the reviewer found the sidebar XAML never binds that flag to
/// any visible property, so a selected row looked identical to a cold one (a half-built
/// feature). This test closes that gap: it drives the real <see cref="WorkspaceViewModel"/>
/// behind the real view through a headless window, selects a finding's anchor, and asserts
/// the corresponding row CONTAINER actually repaints.
///
/// THE PINNED CONTRACT (the implementer binds to exactly this):
///   • Visual property: the row template's root <see cref="Button"/> (the flat command
///     button that is each ListBox row — it already binds <c>CommandParameter</c> =
///     <see cref="ViolationRowModel.PrimaryDn"/>, the stable anchor to locate it by) and
///     its <see cref="Button.Background"/>.
///   • Active (<c>IsActive == true</c>): <c>Button.Background</c> is a solid brush of the
///     pinned highlight color <see cref="HighlightHex"/> (#330F6CBD — 20%-alpha of the app
///     accent #0F6CBD, a subtle selection band behind the dark row text).
///   • Inactive (<c>IsActive == false</c>): <c>Button.Background</c> stays
///     <see cref="Colors.Transparent"/> (the row template's current literal default).
/// Against current <c>src</c> there is NO IsActive binding, so EVERY row keeps the literal
/// <c>Background="Transparent"</c> — the selected row never differs from a cold one and this
/// test FAILS (the active row's background is Transparent, not the highlight). When the
/// implementer binds <c>Button.Background</c> to <c>IsActive</c> via a brush converter, it
/// passes. Change the pinned hex ONLY by editing this constant AND the XAML together in one
/// reviewed PR (the data-model.md "change only with a reviewed PR" discipline).
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

    /// <summary>The pinned selection-highlight brush color (ADR-010 §5): 20%-alpha of the
    /// app accent #0F6CBD. The active row's <c>Button.Background</c> must equal this; the
    /// implementer binds the SAME color.</summary>
    private const string HighlightHex = "#330F6CBD";

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
        var activeRow = RowButtonFor(sidebar, GroupADn);
        var coldRow = RowButtonFor(sidebar, GroupBDn);

        var activeBrush = Assert.IsAssignableFrom<ISolidColorBrush>(activeRow.Background);
        var coldBrush = Assert.IsAssignableFrom<ISolidColorBrush>(coldRow.Background);

        // (1) the selected row's container repaints to the pinned highlight color …
        Assert.Equal(Color.Parse(HighlightHex), activeBrush.Color);
        // (2) … the cold row stays the template's transparent default …
        Assert.Equal(Colors.Transparent, coldBrush.Color);
        // (3) … so a selected row is visibly distinct from a cold one (the whole point:
        //     against current src both are Transparent and this differential fails).
        Assert.NotEqual(coldBrush.Color, activeBrush.Color);

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

    /// <summary>The row template's command <see cref="Button"/> whose <c>CommandParameter</c>
    /// (= <see cref="ViolationRowModel.PrimaryDn"/>) matches <paramref name="primaryDn"/> — the
    /// stable, template-independent way to address a specific realized row. Asserts the row is
    /// realized (a single match), so virtualization hiding it fails loudly, not as a null deref.</summary>
    private static Button RowButtonFor(ViolationsSidebarView sidebar, string primaryDn) =>
        Assert.Single(
            sidebar.GetVisualDescendants().OfType<Button>(),
            b => b.IsEffectivelyVisible && b.CommandParameter as string == primaryDn);

    /// <summary>Stub whose scope load yields <paramref name="snapshot"/>.</summary>
    private static StubDirectoryProvider Provider(DirectorySnapshot snapshot) =>
        new(Task.FromResult(new DirectoryConnection("stub directory", 5)))
        {
            LoadScopeResult = Task.FromResult(snapshot),
        };

    /// <summary>Workspace VM rooted at <see cref="RootDn"/> (AP 2.2 S6 ctor shape), null
    /// ruleset => the embedded default (the 19-finding-baseline contract; here it yields the
    /// two empty-group findings of the stub scope).</summary>
    private static WorkspaceViewModel Workspace(
        StubDirectoryProvider provider, Func<IGraphRenderer> rendererFactory) =>
        new(
            provider,
            Obj("Lab", RootDn, AdObjectKind.OrganizationalUnit),
            new DirectoryConnection("stub directory", 5),
            webView2Missing: false,
            rendererFactory);

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
