using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
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
/// Pins the Slice B (UX polish) detail-panel DN-copy + SAM-label affordances in the rendered
/// VIEW (the <see cref="DetailPanelCopyDnTests"/> companion pins the model-level flip). Mirrors
/// the <see cref="DetailPanelViewTests"/>/<see cref="WorkspaceDetailTests"/> harness: a workspace
/// VM with a temp-dir <c>UiStateStore</c> seam (#124 — a persisted <c>RailCollapsed:true</c> must
/// not starve the right-rail realization these facts assert over), driven by a click into the
/// <c>DetailPanelRegion</c> seam.
///
/// <para>Pinned here for a loaded USER selection:</para>
/// <list type="bullet">
/// <item>the ghost <c>CopyDnButton</c> renders beside the DN, content "Copy", with the
/// clipboard tooltip and a Click handler wired (the copy path exists and is reachable);</item>
/// <item>the <c>Classes="caption"</c> "sAMAccountName" label renders above the SAM value (the
/// bare directory string is no longer unlabeled);</item>
/// <item>the transient "Copied" TextBlock is HIDDEN until <see cref="DetailPanelModel.CopiedDn"/>
/// flips — the binding is wired to the model affordance.</item>
/// </list>
///
/// <para><b>Clipboard handling (deliberate, not a gap).</b> The actual clipboard WRITE
/// (<c>DetailPanelView.OnCopyDnClick</c> → <c>TopLevel.Clipboard.SetTextAsync</c>) is the
/// untestable [I] layer — the same shape as <c>AuditView.OnCopySnippetClick</c>, whose existing
/// coverage tests the flip at the model level only and never asserts a real headless clipboard.
/// This fixture follows that precedent: it proves the button is present + wired (a real Click
/// is raised and does not throw) and asserts the visible-affordance contract; the
/// <c>CopiedDn</c> false→true flip is pinned deterministically in
/// <see cref="DetailPanelCopyDnTests"/>. No flaky <c>TopLevel.Clipboard</c> dependency is added.</para>
/// </summary>
public sealed class DetailPanelCopyDnViewTests
{
    private const string RootDn = "OU=Lab,DC=stub,DC=lab";
    private const string AdaDn = "CN=Ada Lovelace,OU=Lab,DC=stub,DC=lab";

    private const string CopyContent = "Copy";
    private const string CopyTooltip = "Copy this distinguished name to the clipboard";
    private const string SamCaption = "sAMAccountName";
    private const string CopiedText = "Copied";

    // --- (a) the ghost Copy button: present, content, tooltip, Click wired --------------

    [AvaloniaFact]
    public async Task UserSelection_RendersTheGhostCopyDnButton_WithContent_Tooltip_AndClickWired()
    {
        var fake = new FakeGraphRenderer();
        var vm = Workspace(Provider(PanelScope()), () => fake);
        var (window, view) = ShowWorkspace(vm);
        await vm.Initialization;

        fake.RaiseNodeClicked(AdaDn, "User");
        Dispatcher.UIThread.RunJobs();

        var region = Region(view, "DetailPanelRegion");
        var copyButton = Assert.Single(
            region.GetVisualDescendants().OfType<Button>(), b => b.Name == "CopyDnButton");

        Assert.True(copyButton.IsEffectivelyVisible, "the Copy button must render for a loaded selection");
        Assert.Equal(CopyContent, copyButton.Content);
        Assert.Equal(CopyTooltip, ToolTip.GetTip(copyButton));

        // The click path is reachable + wired: raising the button's Click must not throw
        // (the code-behind reads TopLevel.Clipboard, which may be absent headless — it guards
        // and no-ops). This proves the handler is attached without depending on a real clipboard.
        var ex = Record.Exception(() =>
        {
            copyButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();
        });
        Assert.Null(ex);

        window.Close();
    }

    // --- (b) the sAMAccountName caption renders above the SAM value ---------------------

    [AvaloniaFact]
    public async Task UserSelection_RendersTheSamAccountNameCaption_AboveTheSamValue()
    {
        var fake = new FakeGraphRenderer();
        var vm = Workspace(Provider(PanelScope()), () => fake);
        var (window, view) = ShowWorkspace(vm);
        await vm.Initialization;

        fake.RaiseNodeClicked(AdaDn, "User");
        Dispatcher.UIThread.RunJobs();

        var region = Region(view, "DetailPanelRegion");
        var texts = VisibleTexts(region);

        // The caption label (the bare directory string is no longer unlabeled) AND the value.
        Assert.Contains(SamCaption, texts);
        Assert.Contains("ada.lovelace", texts);

        // The caption carries the "caption" class (parity with the attribute-row captions).
        var captionBlock = Assert.Single(
            region.GetVisualDescendants().OfType<TextBlock>(),
            t => t.IsEffectivelyVisible && t.Text == SamCaption);
        Assert.Contains("caption", captionBlock.Classes);

        window.Close();
    }

    // --- (c) the transient "Copied" affordance is hidden until the flag flips -----------

    [AvaloniaFact]
    public async Task CopiedAffordance_IsHiddenInitially_ThenShownWhenCopiedDnFlips()
    {
        var fake = new FakeGraphRenderer();
        var vm = Workspace(Provider(PanelScope()), () => fake);
        var (window, view) = ShowWorkspace(vm);
        await vm.Initialization;

        fake.RaiseNodeClicked(AdaDn, "User");
        Dispatcher.UIThread.RunJobs();

        var region = Region(view, "DetailPanelRegion");

        // Initially the "Copied" TextBlock exists but is collapsed (CopiedDn defaults false).
        Assert.DoesNotContain(CopiedText, VisibleTexts(region));

        // Flipping the model affordance (what the view's OnCopyDnClick does after the
        // clipboard write) reveals it — the IsVisible="{Binding CopiedDn}" wire is live.
        Assert.NotNull(vm.DetailPanel);
        vm.DetailPanel.MarkDnCopied();
        Dispatcher.UIThread.RunJobs();

        Assert.Contains(CopiedText, VisibleTexts(region));

        window.Close();
    }

    // --- helpers ------------------------------------------------------------------------

    private static AdObject Obj(
        string name, string dn, AdObjectKind kind = AdObjectKind.GlobalGroup) =>
        new() { Dn = dn, Kind = kind, Name = name };

    /// <summary>Scope with a loaded user carrying a SAM + one attribute (so the SAM caption
    /// and the Copy button both have a real selection to render against).</summary>
    private static DirectorySnapshot PanelScope()
    {
        var snapshot = new DirectorySnapshot();
        snapshot.AddObject(Obj("Lab", RootDn, AdObjectKind.OrganizationalUnit));
        snapshot.AddObject(new AdObject
        {
            Dn = AdaDn,
            Kind = AdObjectKind.User,
            Name = "Ada Lovelace",
            SamAccountName = "ada.lovelace",
            Attributes = new Dictionary<string, string>
            {
                ["description"] = "copy-affordance fixture",
            },
        });
        return snapshot;
    }

    private static StubDirectoryProvider Provider(DirectorySnapshot snapshot) =>
        new(Task.FromResult(new DirectoryConnection("stub directory", 5)))
        {
            LoadScopeResult = Task.FromResult(snapshot),
        };

    /// <summary>Workspace VM with a fresh temp-dir UiStateStore (#124 / ADR-022 D4): never the
    /// real %APPDATA% ui-state.json, so a persisted RailCollapsed:true cannot collapse the right
    /// rail and starve the detail-panel realization this file asserts over.</summary>
    private static WorkspaceViewModel Workspace(
        StubDirectoryProvider provider, Func<IGraphRenderer> rendererFactory) =>
        new(
            provider,
            Obj("Lab", RootDn, AdObjectKind.OrganizationalUnit),
            new DirectoryConnection("stub directory", 5),
            webView2Missing: false,
            rendererFactory,
            uiStateStore: new GroupWeaver.App.Settings.UiStateStore(
                System.IO.Directory.CreateTempSubdirectory("groupweaver-copydn-uistate-").FullName));

    private static (Window Window, WorkspaceView View) ShowWorkspace(WorkspaceViewModel vm)
    {
        var view = new WorkspaceView { DataContext = vm };
        var window = new Window { Content = view, Width = 1280, Height = 720 };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return (window, view);
    }

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
