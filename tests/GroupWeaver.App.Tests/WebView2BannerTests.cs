using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;

using GroupWeaver.App.Startup;
using GroupWeaver.App.Tests.Fakes;
using GroupWeaver.App.ViewModels;
using GroupWeaver.App.Views;
using GroupWeaver.Core.Model;
using GroupWeaver.Core.Providers;

using Xunit;

namespace GroupWeaver.App.Tests;

/// <summary>
/// Pins the missing-WebView2-Runtime UX (AP 2.1 S7, ADR-003 D3): the persistent shell
/// banner is visible on EVERY step while the probe reported missing — and never when it
/// reported present — and the workspace's GraphHost placeholder switches between its
/// normal "unavailable in this environment" variant and the missing-runtime variant
/// (since AP 2.2 the placeholder only appears when no renderer factory is wired,
/// i.e. headless tests or a missing runtime). The status is
/// forced through <see cref="ShellViewModel"/>'s optional ctor parameter, so no test
/// here depends on what is actually installed on the box. The texts are matched
/// verbatim: the banner is one combined TextBlock, the placeholder splits headline and
/// body — exact equality is what tells them apart in the shared visual tree. (The normal
/// placeholder headline is the Slice-A "the graph preview will appear here" copy.)
/// </summary>
public sealed class WebView2BannerTests
{
    private static readonly WebView2RuntimeStatus Missing = new(IsInstalled: false, Version: null);
    private static readonly WebView2RuntimeStatus Present = new(IsInstalled: true, Version: "x");

    private const string BannerText =
        "The Microsoft Edge WebView2 Runtime was not found. GroupWeaver needs it to display the graph (everything else works). Download:";

    private const string PlaceholderMissingHeadline =
        "The Microsoft Edge WebView2 Runtime was not found.";

    private const string PlaceholderNormalHeadline = "The graph preview will appear here.";

    private const string DownloadLinkText = "developer.microsoft.com/microsoft-edge/webview2";

    // --- the banner across the step chain (view level) ---------------------------------

    [AvaloniaFact]
    public async Task ForcedMissing_BannerStaysVisible_AcrossConnectPickerAndWorkspaceSteps()
    {
        var (window, shell) = ShowShell(Missing);

        // Connect step: banner above the step content, with its download link.
        Assert.IsType<ConnectionViewModel>(shell.CurrentStep);
        Assert.True(IsBannerEffectivelyVisible(window), "banner must show on the Connect step");
        Assert.Contains(
            window.GetVisualDescendants().OfType<TextBlock>(),
            t => t.Text == DownloadLinkText && t.IsEffectivelyVisible);

        // PickRoot step: the step content switched, the banner did not.
        var picker = await ConnectIntoPickerAsync(shell);
        Dispatcher.UIThread.RunJobs();
        Assert.Single(window.GetVisualDescendants().OfType<RootPickerView>());
        Assert.True(IsBannerEffectivelyVisible(window), "banner must survive into the PickRoot step");

        // Workspace step: banner still there, docked above the workspace layout.
        ConfirmIntoWorkspace(shell, picker);
        Dispatcher.UIThread.RunJobs();
        Assert.Single(window.GetVisualDescendants().OfType<WorkspaceView>());
        Assert.True(IsBannerEffectivelyVisible(window), "banner must survive into the Workspace step");

        window.Close();
    }

    [AvaloniaFact]
    public async Task ForcedPresent_BannerIsNotVisible_OnAnyStep()
    {
        var (window, shell) = ShowShell(Present);

        Assert.False(IsBannerEffectivelyVisible(window), "no banner on Connect while the runtime is present");

        var picker = await ConnectIntoPickerAsync(shell);
        Dispatcher.UIThread.RunJobs();
        Assert.False(IsBannerEffectivelyVisible(window), "no banner on PickRoot while the runtime is present");

        ConfirmIntoWorkspace(shell, picker);
        Dispatcher.UIThread.RunJobs();
        Assert.False(IsBannerEffectivelyVisible(window), "no banner on Workspace while the runtime is present");

        window.Close();
    }

    // --- the GraphHost placeholder variants (view level) --------------------------------

    [AvaloniaFact]
    public async Task ForcedMissing_GraphHostPlaceholder_ShowsTheMissingRuntimeVariant()
    {
        var (window, shell) = ShowShell(Missing);
        ConfirmIntoWorkspace(shell, await ConnectIntoPickerAsync(shell));
        Dispatcher.UIThread.RunJobs();

        var graphHost = GraphHost(window);
        var texts = VisibleTexts(graphHost);

        Assert.Contains(PlaceholderMissingHeadline, texts);
        Assert.Contains(DownloadLinkText, texts);
        Assert.DoesNotContain(PlaceholderNormalHeadline, texts);

        window.Close();
    }

    [AvaloniaFact]
    public async Task ForcedPresent_GraphHostPlaceholder_ShowsTheNormalAp22Variant_WithTheRootDn()
    {
        var (window, shell) = ShowShell(Present);
        ConfirmIntoWorkspace(shell, await ConnectIntoPickerAsync(shell));
        Dispatcher.UIThread.RunJobs();

        var workspace = Assert.IsType<WorkspaceViewModel>(shell.CurrentStep);
        var graphHost = GraphHost(window);
        var texts = VisibleTexts(graphHost);

        Assert.Contains(PlaceholderNormalHeadline, texts);
        Assert.Contains(workspace.RootDn, texts);
        Assert.DoesNotContain(PlaceholderMissingHeadline, texts);

        window.Close();
    }

    // --- flag propagation (ViewModel level) ----------------------------------------------

    [Fact]
    public async Task MissingFlag_PropagatesShellThroughPickerIntoWorkspace()
    {
        var shell = Shell(Missing);
        Assert.True(shell.WebView2Missing);
        Assert.Null(shell.WebView2Version);

        // The picker holds the flag privately; the workspace it builds is the only
        // public window onto the hand-off — confirm and read it there.
        var workspace = ConfirmIntoWorkspace(shell, await ConnectIntoPickerAsync(shell));
        Assert.True(
            workspace.WebView2Missing,
            "the probe result must reach the workspace through the picker hand-off");
    }

    [Fact]
    public async Task PresentFlag_PropagatesShellThroughPickerIntoWorkspace_WithTheVersion()
    {
        var shell = Shell(Present);
        Assert.False(shell.WebView2Missing);
        Assert.Equal("x", shell.WebView2Version);

        var workspace = ConfirmIntoWorkspace(shell, await ConnectIntoPickerAsync(shell));
        Assert.False(workspace.WebView2Missing);
    }

    // --- helpers --------------------------------------------------------------------------

    /// <summary>
    /// The banner is the only place rendering <see cref="BannerText"/> as ONE TextBlock
    /// (the workspace placeholder splits headline and body), so exact text equality
    /// identifies it on every step without pinning names or tree shape.
    /// </summary>
    private static bool IsBannerEffectivelyVisible(MainWindow window) =>
        window.GetVisualDescendants()
            .OfType<TextBlock>()
            .Any(t => t.Text == BannerText && t.IsEffectivelyVisible);

    private static ContentControl GraphHost(MainWindow window)
    {
        var workspaceView = Assert.Single(window.GetVisualDescendants().OfType<WorkspaceView>());
        return Assert.Single(
            workspaceView.GetVisualDescendants().OfType<ContentControl>(),
            c => c.Name == "GraphHost");
    }

    private static List<string?> VisibleTexts(ContentControl scope) =>
        scope.GetVisualDescendants()
            .OfType<TextBlock>()
            .Where(t => t.IsEffectivelyVisible)
            .Select(t => t.Text)
            .ToList();

    private static ShellViewModel Shell(WebView2RuntimeStatus status)
    {
        var provider = new StubDirectoryProvider(
            Task.FromResult(new DirectoryConnection("stub demo directory", 7)))
        {
            RootCandidatesResult = Task.FromResult<IReadOnlyList<AdObject>>([
                new AdObject
                {
                    Dn = "CN=GG_Sales,OU=Groups,DC=stub,DC=lab",
                    Kind = AdObjectKind.GlobalGroup,
                    Name = "GG_Sales",
                },
            ]),
        };
        return new ShellViewModel(_ => provider, new StartupOptions(Demo: false), status);
    }

    private static (MainWindow Window, ShellViewModel Shell) ShowShell(WebView2RuntimeStatus status)
    {
        var shell = Shell(status);
        var window = new MainWindow { DataContext = shell, Width = 1280, Height = 720 };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return (window, shell);
    }

    private static async Task<RootPickerViewModel> ConnectIntoPickerAsync(ShellViewModel shell)
    {
        var connect = Assert.IsType<ConnectionViewModel>(shell.CurrentStep);
        await connect.ConnectDemoCommand.ExecuteAsync(null);
        var picker = Assert.IsType<RootPickerViewModel>(shell.CurrentStep);
        await picker.LoadCandidates;
        return picker;
    }

    private static WorkspaceViewModel ConfirmIntoWorkspace(
        ShellViewModel shell, RootPickerViewModel picker)
    {
        picker.SelectedCandidate = picker.Candidates[0];
        picker.LoadRootCommand.Execute(null);
        return Assert.IsType<WorkspaceViewModel>(shell.CurrentStep);
    }
}
