using System;
using System.Linq;
using System.Threading.Tasks;

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
using GroupWeaver.Core.Providers;

using Xunit;

namespace GroupWeaver.App.Tests.Views;

/// <summary>
/// Pins WP3 (#152) — the PROMOTED workspace <c>AuditButton</c> — at the rendered-view layer.
/// <see cref="AuditNavigationTests"/> already pins the VM/navigation half (the snapshot-null gate,
/// CanExecute re-arming, the Workspace↔Audit round-trip); this test pins ONLY the WP3 prominence
/// refactor the VM tests cannot see, so the icon refactor cannot silently regress the seam:
/// <list type="bullet">
///   <item><b>The button survives the refactor</b> — still located by its stable seam <c>Name</c>
///   (<c>AuditButton</c>), still bound to <see cref="WorkspaceViewModel.AuditCommand"/>
///   (<c>Assert.Same</c>, never cross-wired).</item>
///   <item><b>It carries the new <c>accent-outline</c> style class</b> (App.axaml) — the promotion
///   to the headline-verdict tier.</item>
///   <item><b>The "Audit" label is still REACHABLE</b> — the content is now a horizontal
///   StackPanel of a leading vector <c>Path</c> glyph + a <c>TextBlock Text="Audit"</c> (previously
///   a bare <c>Content="Audit"</c> string), so the label moved into a child TextBlock. Asserting the
///   child TextBlock guards against the icon refactor hiding the label.</item>
///   <item><b>It is still the LAST child of the action <see cref="WrapPanel"/></b> — the documented
///   "placed LAST so it never disturbs the pinned Reload-scope→Refresh adjacency" invariant.</item>
/// </list>
///
/// <para><b>Test-isolation seam (lab-environment / the #124 lesson).</b> A fresh
/// <see cref="System.IO.Directory.CreateTempSubdirectory(string)"/>-backed <see cref="UiStateStore"/>
/// is injected into the workspace VM, so a <c>RailCollapsed:true</c> persisted in real
/// <c>%APPDATA%</c> (from interactive use / the demo-GIF recorder) cannot collapse the right rail to
/// width 0 and starve realization of the rail's action WrapPanel — without it the AuditButton would
/// have Bounds 0x0 / zero realized children and the assertions would see an empty tree.</para>
///
/// <para>The fixture scope includes the GG_Circle_A ↔ GG_Circle_B nesting cycle (A→B→A): the load
/// that realizes the rail walks membership and MUST terminate over it (the test-engineer
/// circular-case charter).</para>
/// </summary>
public sealed class AuditButtonProminenceViewTests
{
    private const string RootDn = "OU=Lab,DC=stub,DC=lab";
    private const string CircleADn = "CN=GG_Circle_A,OU=Lab,DC=stub,DC=lab";
    private const string CircleBDn = "CN=GG_Circle_B,OU=Lab,DC=stub,DC=lab";

    [AvaloniaFact]
    public async Task AuditButton_IsAccentOutline_BoundToAuditCommand_WithReachableLabel_AndLastInActionRow()
    {
        var fake = new FakeGraphRenderer();
        var vm = Workspace(Provider(CycleScope()), () => fake);
        var (window, view) = ShowWorkspace(vm);
        await vm.Initialization;
        Dispatcher.UIThread.RunJobs();

        // The named seam button (the RefreshButton/ReloadScopeButton idiom) — exactly one realized,
        // so a rename or a virtualization hiding it fails loudly, not as a null deref.
        var audit = Assert.Single(
            view.GetVisualDescendants().OfType<Button>(), b => b.Name == "AuditButton");
        Assert.True(audit.IsEffectivelyVisible);

        // (1) The WP3 promotion: it carries the accent-outline style class (App.axaml).
        Assert.Contains(
            "accent-outline",
            audit.Classes);

        // (2) Still bound to AuditCommand — never cross-wired to another action's command.
        Assert.Same(vm.AuditCommand, audit.Command);

        // (3) The "Audit" label is still reachable as a child TextBlock (the content is now a
        //     StackPanel of a vector glyph + the label, no longer a bare Content="Audit" string).
        //     This guards against the icon refactor hiding the label.
        var label = Assert.Single(
            audit.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == "Audit");
        Assert.True(label.IsEffectivelyVisible, "the Audit label TextBlock must stay visible");

        // (4) Still the LAST child of the action WrapPanel (the documented "placed LAST so it never
        //     disturbs the pinned Reload-scope→Refresh adjacency" invariant). The WrapPanel is the
        //     one that hosts the action buttons (it contains the AuditButton).
        var actionRow = view.GetVisualDescendants()
            .OfType<WrapPanel>()
            .Single(w => w.Children.OfType<Button>().Any(b => b.Name == "AuditButton"));
        Assert.Same(audit, actionRow.Children[^1]);

        window.Close();
    }

    // --- helpers ------------------------------------------------------------------------

    private static AdObject Obj(
        string name, string dn, AdObjectKind kind = AdObjectKind.GlobalGroup) =>
        new() { Dn = dn, Kind = kind, Name = name };

    /// <summary>Root OU + the GG_Circle_A ↔ GG_Circle_B nesting cycle (A→B→A): a real loaded scope
    /// whose membership walk MUST terminate over the cycle (the circular-case charter).</summary>
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

    /// <summary>Stub whose scope load yields <paramref name="snapshot"/> (default: empty).</summary>
    private static StubDirectoryProvider Provider(DirectorySnapshot? snapshot = null) =>
        new(Task.FromResult(new DirectoryConnection("stub directory", 5)))
        {
            LoadScopeResult = Task.FromResult(snapshot ?? new DirectorySnapshot()),
        };

    /// <summary>Workspace VM rooted at <see cref="RootDn"/> (the S6 ctor shape), null ruleset =>
    /// the embedded default. Fresh temp-dir UiStateStore (#124 / ADR-022 D4): never touches the
    /// real %APPDATA% ui-state.json, so a persisted RailCollapsed:true cannot collapse the right
    /// rail and starve the action-row realization this file asserts over.</summary>
    private static WorkspaceViewModel Workspace(
        StubDirectoryProvider provider, Func<IGraphRenderer> rendererFactory) =>
        new(
            provider,
            Obj("Lab", RootDn, AdObjectKind.OrganizationalUnit),
            new DirectoryConnection("stub directory", 5),
            webView2Missing: false,
            rendererFactory,
            uiStateStore: new UiStateStore(
                System.IO.Directory.CreateTempSubdirectory("groupweaver-auditbtn-uistate-").FullName));

    /// <summary>The full workspace view in a sized, shown headless window (bindings live, the rail
    /// realized) — the WorkspaceLoadTests/SidebarExportButtonsViewTests hosting idiom.</summary>
    private static (Window Window, WorkspaceView View) ShowWorkspace(WorkspaceViewModel vm)
    {
        var view = new WorkspaceView { DataContext = vm };
        var window = new Window { Content = view, Width = 1280, Height = 720 };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return (window, view);
    }
}
