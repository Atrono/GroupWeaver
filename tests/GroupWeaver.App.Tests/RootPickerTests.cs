using GroupWeaver.App.Tests.Fakes;
using GroupWeaver.App.ViewModels;
using GroupWeaver.Core.Model;
using GroupWeaver.Core.Providers;
using GroupWeaver.Providers;

using Xunit;

namespace GroupWeaver.App.Tests;

/// <summary>
/// Pins the S5 PickRoot step (AP 2.1, ADR-003 D5/D7): the candidate load started by the
/// constructor and kept observable through <see cref="RootPickerViewModel.LoadCandidates"/>,
/// the mandatory entry filter (<c>LoadRootCommand</c> disabled without a selection), the
/// client-side text filter over Name/SamAccountName/Dn, the picker's error policy
/// (<see cref="DirectoryUnavailableException"/> inline WITHOUT the Connect step's demo hint,
/// everything else faults the load task — crash = bug), and both exits: confirm builds the
/// <see cref="WorkspaceViewModel"/>, Back drops the provider for a fresh Connect step.
/// The ViewModel is UI-free — every test here is a plain <see cref="FactAttribute"/>.
/// </summary>
public sealed class RootPickerTests
{
    // --- candidate load ---------------------------------------------------------

    [Fact]
    public async Task Construction_StartsCandidateLoad_IsLoadingUntilReleased_ThenSortedNameThenDn()
    {
        var tcs = new TaskCompletionSource<IReadOnlyList<AdObject>>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var provider = ConnectedStub();
        provider.RootCandidatesResult = tcs.Task;

        var picker = Picker(provider);

        Assert.True(picker.IsLoading, "the load kicked off by the ctor must show as in flight");
        Assert.False(picker.LoadCandidates.IsCompleted);
        Assert.Empty(picker.Candidates);
        Assert.Equal(1, provider.RootCandidatesCalls);

        // Deliberately unsorted, with a Name tie ("Alpha"/"alpha", OrdinalIgnoreCase-equal)
        // that only the Dn tie-breaker can order.
        tcs.SetResult([
            Obj("Zeta", "CN=Zeta,OU=Groups,DC=stub,DC=lab"),
            Obj("alpha", "OU=2,DC=stub,DC=lab", AdObjectKind.OrganizationalUnit),
            Obj("Alpha", "OU=1,DC=stub,DC=lab", AdObjectKind.OrganizationalUnit),
        ]);
        await picker.LoadCandidates;

        Assert.False(picker.IsLoading);
        Assert.Null(picker.ErrorMessage);
        Assert.Equal(
            ["OU=1,DC=stub,DC=lab", "OU=2,DC=stub,DC=lab", "CN=Zeta,OU=Groups,DC=stub,DC=lab"],
            picker.Candidates.Select(c => c.Dn).ToArray());
        Assert.Equal(picker.Candidates, picker.FilteredCandidates);
    }

    /// <summary>
    /// End-to-end VM pin against the REAL embedded demo dataset: keeps the M1 number
    /// (44 root candidates = 4 OUs + 40 groups) alive at ViewModel level. Dataset-level
    /// parity is owned by <c>DemoProviderTests</c>; this proves the picker exposes it.
    /// </summary>
    [Fact]
    public async Task RealDemoProvider_Exposes44Candidates_FourOusAndFortyGroups()
    {
        var provider = new DemoProvider();
        var connection = await provider.ConnectAsync();
        var picker = new RootPickerViewModel(provider, connection, () => { }, _ => { });

        await picker.LoadCandidates;

        Assert.False(picker.IsLoading);
        Assert.Null(picker.ErrorMessage);
        Assert.Equal(44, picker.Candidates.Count);
        Assert.Equal(4, picker.Candidates.Count(c => c.Kind == AdObjectKind.OrganizationalUnit));
        Assert.Equal(40, picker.Candidates.Count(c => IsGroupKind(c.Kind)));
    }

    // --- mandatory entry filter (LoadRootCommand gating) -------------------------

    [Fact]
    public async Task LoadRoot_CanExecute_TracksSelection_AndRaisesCanExecuteChanged()
    {
        var candidate = Obj("GG_Sales", "CN=GG_Sales,OU=Groups,DC=stub,DC=lab");
        var picker = Picker(ConnectedStub(candidate));
        await picker.LoadCandidates;

        Assert.False(
            picker.LoadRootCommand.CanExecute(null),
            "the mandatory entry filter: nothing loads without a selection");

        var canExecuteChanges = 0;
        picker.LoadRootCommand.CanExecuteChanged += (_, _) => canExecuteChanges++;

        picker.SelectedCandidate = picker.Candidates[0];
        Assert.True(picker.LoadRootCommand.CanExecute(null));
        Assert.True(
            canExecuteChanges > 0,
            "selecting must raise CanExecuteChanged — bound buttons only re-query on the event");

        var changesAfterSelect = canExecuteChanges;
        picker.SelectedCandidate = null;
        Assert.False(picker.LoadRootCommand.CanExecute(null));
        Assert.True(
            canExecuteChanges > changesAfterSelect,
            "clearing the selection must raise CanExecuteChanged again");
    }

    // --- text filter --------------------------------------------------------------

    [Fact]
    public async Task Filter_Narrows_OrdinalIgnoreCase_OnNameSamAccountNameAndDn()
    {
        var picker = Picker(ConnectedStub(
            Obj("GG_Sales", "CN=GG_Sales,OU=Groups,DC=stub,DC=lab"), // Name match
            Obj("Marketing", "OU=Marketing,OU=GG_Legacy,DC=stub,DC=lab", AdObjectKind.OrganizationalUnit), // Dn-only match
            Obj("Service Desk", "CN=Service Desk,OU=Groups,DC=stub,DC=lab", sam: "gg_servicedesk"), // Sam-only match
            Obj("DL_Other", "CN=DL_Other,OU=Misc,DC=stub,DC=lab", AdObjectKind.DomainLocalGroup, sam: "DL_Other"))); // no match
        await picker.LoadCandidates;

        var propertyChanges = new List<string?>();
        picker.PropertyChanged += (_, e) => propertyChanges.Add(e.PropertyName);

        picker.Filter = "GG_";
        Assert.Contains(
            nameof(RootPickerViewModel.FilteredCandidates),
            propertyChanges); // the view binds FilteredCandidates; Filter must notify it
        Assert.Equal(
            ["GG_Sales", "Marketing", "Service Desk"],
            picker.FilteredCandidates.Select(c => c.Name).ToArray());

        picker.Filter = "gg_"; // OrdinalIgnoreCase: same three
        Assert.Equal(3, picker.FilteredCandidates.Count);

        picker.Filter = "no-such-candidate";
        Assert.Empty(picker.FilteredCandidates);

        picker.Filter = string.Empty; // clearing restores everything
        Assert.Equal(4, picker.FilteredCandidates.Count);

        picker.Filter = "   "; // whitespace-only is a blank filter, not a match-nothing one
        Assert.Equal(4, picker.FilteredCandidates.Count);

        picker.Filter = null; // null must be safe and show everything
        Assert.Equal(4, picker.FilteredCandidates.Count);
    }

    // --- error policy (ADR-003 D7) --------------------------------------------------

    [Fact]
    public async Task DirectoryUnavailable_DuringLoad_ShowsInlineError_WithoutDemoHint()
    {
        var provider = ConnectedStub();
        provider.RootCandidatesResult = Task.FromException<IReadOnlyList<AdObject>>(
            new DirectoryUnavailableException("directory went away"));

        var picker = Picker(provider);
        await picker.LoadCandidates; // handled inline — the load task itself must not fault

        Assert.True(
            picker.LoadCandidates.IsCompletedSuccessfully,
            "DirectoryUnavailableException is handled inline; the app stays alive");
        // Exactly the exception message: the "try Demo mode" hint belongs to the Connect
        // step's live path only — here a connection already succeeded.
        Assert.Equal("directory went away", picker.ErrorMessage);
        Assert.False(picker.IsLoading);
        Assert.Empty(picker.Candidates);
    }

    [Fact]
    public async Task NonDirectoryUnavailableException_FaultsTheObservableLoadTask()
    {
        var provider = ConnectedStub();
        provider.RootCandidatesResult = Task.FromException<IReadOnlyList<AdObject>>(
            new InvalidOperationException("boom"));

        var picker = Picker(provider);

        // D7: anything but DirectoryUnavailableException is a bug and must stay observable
        // through LoadCandidates (crash = bug), never be swallowed into the error block.
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => picker.LoadCandidates);

        Assert.Equal("boom", ex.Message);
        Assert.Null(picker.ErrorMessage);
        Assert.False(picker.IsLoading, "the finally block must clear IsLoading even on a bug-path fault");
    }

    // --- the two exits: confirm and back ---------------------------------------------

    [Fact]
    public async Task LoadRoot_BuildsWorkspaceFromSelection_AndHandsItToTheShell()
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
        var connect = Assert.IsType<ConnectionViewModel>(shell.CurrentStep);
        await connect.ConnectDemoCommand.ExecuteAsync(null);
        var picker = Assert.IsType<RootPickerViewModel>(shell.CurrentStep);
        await picker.LoadCandidates;

        var chosen = Assert.Single(picker.Candidates, c => c.Name == "GG_Sales");
        picker.SelectedCandidate = chosen;
        picker.LoadRootCommand.Execute(null);

        var workspace = Assert.IsType<WorkspaceViewModel>(shell.CurrentStep);
        Assert.Same(provider, workspace.Provider);
        Assert.Same(chosen, workspace.Root);
        Assert.Same(connection, workspace.Connection);
        Assert.Equal("CN=GG_Sales,OU=Groups,DC=stub,DC=lab", workspace.RootDn);
        Assert.Equal("GG_Sales", workspace.RootName);
        Assert.Equal(
            "connected, 7 groups loaded — stub demo directory",
            workspace.ConnectionSummary); // status-bar line, same shape as the M1 DoD console line
    }

    [Fact]
    public async Task Back_DropsTheProvider_AndReturnsToAFreshConnectStep()
    {
        var provider = ConnectedStub(Obj("GG_Sales", "CN=GG_Sales,OU=Groups,DC=stub,DC=lab"));
        var shell = Shell(provider);
        var originalConnect = Assert.IsType<ConnectionViewModel>(shell.CurrentStep);
        await originalConnect.ConnectDemoCommand.ExecuteAsync(null);
        var picker = Assert.IsType<RootPickerViewModel>(shell.CurrentStep);
        await picker.LoadCandidates;

        picker.BackCommand.Execute(null);

        var freshConnect = Assert.IsType<ConnectionViewModel>(shell.CurrentStep);
        Assert.NotSame(originalConnect, freshConnect); // fresh step, no stale error/in-flight state
        Assert.Null(shell.Provider);
    }

    // --- helpers -------------------------------------------------------------------

    private static AdObject Obj(
        string name, string dn, AdObjectKind kind = AdObjectKind.GlobalGroup, string? sam = null) =>
        new() { Dn = dn, Kind = kind, Name = name, SamAccountName = sam };

    /// <summary>Stub whose connect succeeds and whose candidate load yields <paramref name="candidates"/>.</summary>
    private static StubDirectoryProvider ConnectedStub(params AdObject[] candidates) =>
        new(Task.FromResult(new DirectoryConnection("stub directory", 5)))
        {
            RootCandidatesResult = Task.FromResult<IReadOnlyList<AdObject>>(candidates),
        };

    /// <summary>Standalone picker with no-op exits, for tests that never leave the step.</summary>
    private static RootPickerViewModel Picker(StubDirectoryProvider provider) =>
        new(provider, new DirectoryConnection("stub directory", 5), () => { }, _ => { });

    private static ShellViewModel Shell(StubDirectoryProvider provider) =>
        new(_ => provider, new StartupOptions(Demo: false));

    private static bool IsGroupKind(AdObjectKind kind) =>
        kind is AdObjectKind.GlobalGroup or AdObjectKind.DomainLocalGroup or AdObjectKind.UniversalGroup;
}
