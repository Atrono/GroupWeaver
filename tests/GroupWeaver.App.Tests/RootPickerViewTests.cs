using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;

using GroupWeaver.App.Settings;
using GroupWeaver.App.Tests.Fakes;
using GroupWeaver.App.ViewModels;
using GroupWeaver.App.Views;
using GroupWeaver.Core.Model;
using GroupWeaver.Core.Providers;

using Xunit;

namespace GroupWeaver.App.Tests;

/// <summary>
/// Headless VIEW-level pins for the Slice-A RootPicker affordances (the code-behind
/// <c>OnCandidateDoubleTapped</c> handler + the <c>Load</c> button's <c>IsDefault</c>):
/// double-tapping a candidate row commits the selection by executing
/// <see cref="RootPickerViewModel.LoadRootCommand"/> — keyboard/pointer parity with the
/// default Load button — and is a NO-OP unless a candidate is selected (the same
/// <c>CanExecute</c> gate the Load button honors). The VM command matrix and the two exits
/// live in <see cref="RootPickerTests"/> (plain Facts); this file only pins the new
/// view-side gesture wiring, so it renders the real <see cref="RootPickerView"/> headless.
///
/// A fresh temp-dir <see cref="UiStateStore"/> is injected into the picker (and so into the
/// <see cref="WorkspaceViewModel"/> the confirm builds): the ctor never reads the real
/// <c>%APPDATA%\GroupWeaver\ui-state.json</c>, so a persisted <c>RailCollapsed:true</c> can
/// never zero realized views locally while CI (fresh box) stays green (lab-environment rule).
/// </summary>
public sealed class RootPickerViewTests
{
    [AvaloniaFact]
    public async Task DoubleTappingASelectedCandidate_ExecutesLoadRootCommand_CommittingTheSelection()
    {
        WorkspaceViewModel? confirmed = null;
        var provider = ConnectedStub(Obj("GG_Sales", "CN=GG_Sales,OU=Groups,DC=stub,DC=lab"));
        var picker = Picker(provider, onConfirmed: ws => confirmed = ws);
        var (window, view, listBox) = await ShowPickerAsync(picker);

        // A candidate is selected => LoadRootCommand can execute (the Load-button gate).
        picker.SelectedCandidate = picker.Candidates[0];
        Dispatcher.UIThread.RunJobs();
        Assert.True(picker.LoadRootCommand.CanExecute(null), "guard: a selection arms the command");

        RaiseDoubleTapped(listBox);
        Dispatcher.UIThread.RunJobs();

        // The handler fired LoadRootCommand: the confirm callback received the workspace built
        // from the chosen root (the load committed). Identity is the chosen candidate's DN.
        Assert.NotNull(confirmed);
        Assert.Equal("CN=GG_Sales,OU=Groups,DC=stub,DC=lab", confirmed!.RootDn);

        window.Close();
    }

    [AvaloniaFact]
    public async Task DoubleTappingWithNoSelection_IsANoOp_NeverExecutesLoadRoot()
    {
        var confirmCalls = 0;
        var provider = ConnectedStub(Obj("GG_Sales", "CN=GG_Sales,OU=Groups,DC=stub,DC=lab"));
        var picker = Picker(provider, onConfirmed: _ => confirmCalls++);
        var (window, view, listBox) = await ShowPickerAsync(picker);

        // Nothing selected => the mandatory-entry-filter gate is closed (same gate the Load
        // button honors). A double-tap on the empty selection must do nothing.
        Assert.Null(picker.SelectedCandidate);
        Assert.False(picker.LoadRootCommand.CanExecute(null), "guard: no selection disarms the command");

        RaiseDoubleTapped(listBox);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(0, confirmCalls);
        // The step did not advance: the picker VM is still the live one (no confirm fired).
        Assert.Null(picker.SelectedCandidate);

        window.Close();
    }

    // --- helpers -------------------------------------------------------------------

    /// <summary>Raise the SAME routed event the XAML <c>DoubleTapped="OnCandidateDoubleTapped"</c>
    /// handler is wired to, directly on the candidate ListBox — exercising the real code-behind
    /// gesture path (the handler ignores the args, reacting only to the VM's CanExecute gate).</summary>
    private static void RaiseDoubleTapped(ListBox listBox) =>
        listBox.RaiseEvent(new TappedEventArgs(Gestures.DoubleTappedEvent, null!));

    /// <summary>Render the real RootPickerView headless in a sized window and return the view +
    /// the candidate ListBox (the control carrying the DoubleTapped handler).</summary>
    private static async Task<(Window Window, RootPickerView View, ListBox ListBox)> ShowPickerAsync(
        RootPickerViewModel picker)
    {
        await picker.LoadCandidates;

        var view = new RootPickerView { DataContext = picker };
        var window = new Window { Content = view, Width = 800, Height = 600 };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var listBox = Assert.Single(view.GetVisualDescendants().OfType<ListBox>());
        return (window, view, listBox);
    }

    private static AdObject Obj(
        string name, string dn, AdObjectKind kind = AdObjectKind.GlobalGroup, string? sam = null) =>
        new() { Dn = dn, Kind = kind, Name = name, SamAccountName = sam };

    private static StubDirectoryProvider ConnectedStub(params AdObject[] candidates) =>
        new(Task.FromResult(new DirectoryConnection("stub directory", 5)))
        {
            RootCandidatesResult = Task.FromResult<IReadOnlyList<AdObject>>(candidates),
        };

    /// <summary>A picker over the stub with a captured confirm callback and a fresh temp-dir
    /// UiStateStore (so the confirm-built WorkspaceViewModel never reads real %APPDATA%).</summary>
    private static RootPickerViewModel Picker(
        StubDirectoryProvider provider, Action<WorkspaceViewModel> onConfirmed) =>
        new(
            provider,
            new DirectoryConnection("stub directory", 5),
            onBack: () => { },
            onConfirmed: onConfirmed,
            uiStateStore: new UiStateStore(
                Directory.CreateTempSubdirectory("groupweaver-rootpickerview-uistate-").FullName));
}
