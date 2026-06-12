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
/// Headless view-level pins for the S5 PickRoot and Workspace steps (AP 2.1, ADR-003
/// D5/D6): the DataTemplate switch materializes the right view per step, the workspace
/// exposes the named <c>GraphHost</c> / <c>DetailPanelRegion</c> ContentControls (the
/// AP 2.2 / AP 2.5 seams — renaming them breaks the contract), the status bar binds the
/// connection summary and root DN, and the picker's virtualizing ListBox actually
/// materializes item containers once the window has a real size to measure against.
/// </summary>
public sealed class WorkspaceShellTests
{
    [AvaloniaFact]
    public async Task WorkspaceStep_ExposesGraphHostAndDetailPanelSeams_AndStatusBarText()
    {
        var connection = new DirectoryConnection("stub demo directory", 7);
        var provider = new StubDirectoryProvider(Task.FromResult(connection))
        {
            RootCandidatesResult = Task.FromResult<IReadOnlyList<AdObject>>([
                Obj("GG_Sales", "CN=GG_Sales,OU=Groups,DC=stub,DC=lab"),
                Obj("Users", "OU=Users,DC=stub,DC=lab", AdObjectKind.OrganizationalUnit),
            ]),
        };
        var shell = Shell(provider);
        var window = new MainWindow { DataContext = shell };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        // Drive the full step chain: Connect → PickRoot → Workspace.
        var connect = Assert.IsType<ConnectionViewModel>(shell.CurrentStep);
        await connect.ConnectDemoCommand.ExecuteAsync(null);
        var picker = Assert.IsType<RootPickerViewModel>(shell.CurrentStep);
        await picker.LoadCandidates;
        picker.SelectedCandidate = picker.Candidates[0];
        picker.LoadRootCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();

        var workspace = Assert.IsType<WorkspaceViewModel>(shell.CurrentStep);

        // The picker view is gone; exactly one workspace view replaced it (D5 switch).
        Assert.Empty(window.GetVisualDescendants().OfType<RootPickerView>());
        var workspaceView = Assert.Single(window.GetVisualDescendants().OfType<WorkspaceView>());

        // The two named regions are THE seams AP 2.2 (graph WebView) and AP 2.5 (detail
        // panel) mount into — this is the contract pin on their names.
        var contentControls = workspaceView.GetVisualDescendants().OfType<ContentControl>().ToList();
        Assert.Single(contentControls, c => c.Name == "GraphHost");
        Assert.Single(contentControls, c => c.Name == "DetailPanelRegion");

        // Status bar: connection summary + root DN, bound from the workspace VM.
        var texts = workspaceView.GetVisualDescendants()
            .OfType<TextBlock>()
            .Select(t => t.Text)
            .ToList();
        Assert.Contains("connected, 7 groups loaded — stub demo directory", texts);
        Assert.Equal(workspace.ConnectionSummary, texts.Single(t => t?.StartsWith("connected, ", StringComparison.Ordinal) == true));
        Assert.Contains($"root: {workspace.RootDn}", texts);

        window.Close();
    }

    [AvaloniaFact]
    public async Task PickerStep_VirtualizingListBox_MaterializesCandidateItems()
    {
        var provider = new StubDirectoryProvider(
            Task.FromResult(new DirectoryConnection("stub demo directory", 7)))
        {
            RootCandidatesResult = Task.FromResult<IReadOnlyList<AdObject>>([
                Obj("GG_Sales", "CN=GG_Sales,OU=Groups,DC=stub,DC=lab"),
                Obj("DL_FS-Sales_RW", "CN=DL_FS-Sales_RW,OU=Groups,DC=stub,DC=lab", AdObjectKind.DomainLocalGroup),
                Obj("Users", "OU=Users,DC=stub,DC=lab", AdObjectKind.OrganizationalUnit),
            ]),
        };
        var shell = Shell(provider);

        // A virtualizing panel realizes zero containers in a zero-sized viewport: give the
        // window an explicit size BEFORE showing so the ListBox measures against real space.
        var window = new MainWindow { DataContext = shell, Width = 1280, Height = 720 };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var connect = Assert.IsType<ConnectionViewModel>(shell.CurrentStep);
        await connect.ConnectDemoCommand.ExecuteAsync(null);
        var picker = Assert.IsType<RootPickerViewModel>(shell.CurrentStep);
        await picker.LoadCandidates;
        Dispatcher.UIThread.RunJobs();

        var pickerView = Assert.Single(window.GetVisualDescendants().OfType<RootPickerView>());
        var listBox = Assert.Single(pickerView.GetVisualDescendants().OfType<ListBox>());
        Assert.Equal(3, listBox.ItemCount);

        // Virtualization smoke: all three fit the viewport, so all three containers must
        // be realized with their candidate names rendered.
        var itemTexts = listBox.GetVisualDescendants()
            .OfType<ListBoxItem>()
            .Select(item => string.Join(
                "|",
                item.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text)))
            .ToList();
        Assert.Equal(3, itemTexts.Count);
        Assert.Contains(itemTexts, t => t.Contains("GG_Sales", StringComparison.Ordinal));
        Assert.Contains(itemTexts, t => t.Contains("DL_FS-Sales_RW", StringComparison.Ordinal));
        Assert.Contains(itemTexts, t => t.Contains("Users", StringComparison.Ordinal));

        window.Close();
    }

    // --- helpers -------------------------------------------------------------------

    private static AdObject Obj(
        string name, string dn, AdObjectKind kind = AdObjectKind.GlobalGroup) =>
        new() { Dn = dn, Kind = kind, Name = name };

    /// <summary>
    /// Explicit WebView2 status: the ctor default falls back to
    /// <see cref="WebView2Runtime.Probe"/>, which reads the LIVE registry — per-machine
    /// flakiness (and a machine-dependent banner in the visual tree) these view-level
    /// pins must never inherit.
    /// </summary>
    private static ShellViewModel Shell(StubDirectoryProvider provider) =>
        new(
            _ => provider,
            new StartupOptions(Demo: false),
            new WebView2RuntimeStatus(IsInstalled: true, Version: "test"));
}
