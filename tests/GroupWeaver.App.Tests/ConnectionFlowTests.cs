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
/// Pins the shell-navigation + Connect step (AP 2.1, ADR-003 D5/D7):
/// step switching via <see cref="ShellViewModel.CurrentStep"/>, the <c>--demo</c>
/// auto-connect observable through <see cref="ShellViewModel.Initialization"/>, and the
/// Connect step's error policy — <see cref="DirectoryUnavailableException"/> surfaces
/// inline (demo hint on the live path only), everything else crashes (crash = bug).
/// The ViewModels are UI-free, so most tests are plain <see cref="FactAttribute"/>;
/// only the view-level tests boot the headless visual tree.
/// </summary>
/// <remarks>
/// S6 history note: S3 deliberately parked the raw <see cref="DirectoryConnection"/> as the
/// post-connect <see cref="ShellViewModel.CurrentStep"/> placeholder, and the S4 tests pinned
/// that. S5 replaced the placeholder with <see cref="RootPickerViewModel"/> exactly as
/// planned, so the three step-advance assertions here were updated deliberately — the
/// connection is now private to the picker, observable only through the
/// <see cref="WorkspaceViewModel"/> its confirm path builds (see
/// <see cref="ConfirmAndGetCarriedConnection"/>). No behavior regressed; the placeholder was
/// a documented temporary.
/// </remarks>
public sealed class ConnectionFlowTests
{
    // --- ViewModel level: shell construction & startup ------------------------

    [Fact]
    public void FreshShell_StartsOnConnectStep_WithoutAutoConnect()
    {
        var provider = Stub(new DirectoryConnection("unused", 0));
        var (shell, factoryArgs) = CreateShell(provider, demo: false);

        Assert.IsType<ConnectionViewModel>(shell.CurrentStep);
        Assert.True(
            shell.Initialization.IsCompletedSuccessfully,
            "without --demo, Initialization must be an already-completed task");
        Assert.Empty(factoryArgs);
        Assert.Equal(0, provider.ConnectCalls);
        Assert.Null(shell.Provider);
    }

    [Fact]
    public async Task DemoStartup_AutoConnects_AndAdvancesWithoutUserInput()
    {
        var expected = new DirectoryConnection("stub demo directory", 7);
        var provider = Stub(expected);
        var (shell, factoryArgs) = CreateShell(provider, demo: true);

        await shell.Initialization;

        // S5: a successful connect now advances to the PickRoot step (was: the raw
        // DirectoryConnection placeholder pinned by S4 — see the class remarks).
        var picker = Assert.IsType<RootPickerViewModel>(shell.CurrentStep);
        await picker.LoadCandidates;
        Assert.Same(provider, shell.Provider);
        Assert.True(Assert.Single(factoryArgs), "--demo auto-connect must request the demo provider");
        Assert.Equal(1, provider.ConnectCalls);
        Assert.Same(expected, ConfirmAndGetCarriedConnection(shell, picker));
    }

    // --- ViewModel level: the two connect commands -----------------------------

    [Fact]
    public async Task DemoCommand_AdvancesCurrentStep_ToTheRootPicker()
    {
        var expected = new DirectoryConnection("stub demo directory", 7);
        var provider = Stub(expected);
        var (shell, factoryArgs) = CreateShell(provider, demo: false);
        var connect = Assert.IsType<ConnectionViewModel>(shell.CurrentStep);

        await connect.ConnectDemoCommand.ExecuteAsync(null);

        // S5: the step after Connect is the root picker (S4 pinned Assert.Same(expected,
        // CurrentStep) against the documented placeholder — see the class remarks).
        var picker = Assert.IsType<RootPickerViewModel>(shell.CurrentStep);
        await picker.LoadCandidates;
        Assert.Same(provider, shell.Provider);
        Assert.True(Assert.Single(factoryArgs), "demo command must request the demo provider");
        Assert.Null(connect.ErrorMessage);
        Assert.Same(expected, ConfirmAndGetCarriedConnection(shell, picker));
    }

    [Fact]
    public async Task LiveConnectFailure_ShowsErrorWithDemoHint_AndStaysOnConnectStep()
    {
        var provider = Failing(new DirectoryUnavailableException("no domain controller answered"));
        var (shell, factoryArgs) = CreateShell(provider, demo: false);
        var connect = Assert.IsType<ConnectionViewModel>(shell.CurrentStep);

        // Must complete without throwing - DirectoryUnavailableException is handled inline (D7).
        await connect.ConnectCommand.ExecuteAsync(null);

        var error = connect.ErrorMessage;
        Assert.NotNull(error);
        Assert.Contains("no domain controller answered", error, StringComparison.Ordinal);
        Assert.Contains("Demo mode", error, StringComparison.Ordinal);
        Assert.Same(connect, shell.CurrentStep);
        Assert.Null(shell.Provider);
        Assert.False(connect.IsConnecting);
        Assert.True(connect.ConnectCommand.CanExecute(null), "a failed attempt must allow retry");
        Assert.True(connect.ConnectDemoCommand.CanExecute(null), "a failed attempt must allow the demo path");
        Assert.False(Assert.Single(factoryArgs), "live command must request the live provider");
    }

    [Fact]
    public async Task DemoConnectFailure_ShowsErrorWithoutTheLiveOnlyDemoHint()
    {
        var provider = Failing(new DirectoryUnavailableException("demo data corrupt"));
        var (shell, _) = CreateShell(provider, demo: false);
        var connect = Assert.IsType<ConnectionViewModel>(shell.CurrentStep);

        await connect.ConnectDemoCommand.ExecuteAsync(null);

        // Exactly the exception message: the "try Demo mode" hint is appended on the
        // live path only - pointing at demo mode when demo mode just failed is absurd.
        Assert.Equal("demo data corrupt", connect.ErrorMessage);
        Assert.Same(connect, shell.CurrentStep);
        Assert.False(connect.IsConnecting);
    }

    [Fact]
    public async Task NonDirectoryUnavailableException_PropagatesOutOfTheCommand()
    {
        var provider = Failing(new InvalidOperationException("boom"));
        var (shell, _) = CreateShell(provider, demo: false);
        var connect = Assert.IsType<ConnectionViewModel>(shell.CurrentStep);

        // D7: anything but DirectoryUnavailableException is a bug and must crash, never
        // be swallowed into the inline error block.
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => connect.ConnectCommand.ExecuteAsync(null));

        Assert.Equal("boom", ex.Message);
        Assert.Null(connect.ErrorMessage);
        Assert.False(connect.IsConnecting);
        Assert.Same(connect, shell.CurrentStep);
    }

    [Fact]
    public async Task ConnectInFlight_DisablesBothCommands_UntilReleased()
    {
        var tcs = new TaskCompletionSource<DirectoryConnection>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var provider = new StubDirectoryProvider(tcs.Task);
        var (shell, _) = CreateShell(provider, demo: false);
        var connect = Assert.IsType<ConnectionViewModel>(shell.CurrentStep);

        // CanExecute(null) evaluates live; the bound buttons only re-query on
        // CanExecuteChanged, so NotifyCanExecuteChangedFor(IsConnecting) needs its own pin.
        // The OTHER command matters: the executing one re-raises by itself anyway.
        var demoCanExecuteChanges = 0;
        connect.ConnectDemoCommand.CanExecuteChanged += (_, _) => demoCanExecuteChanges++;

        var execution = connect.ConnectCommand.ExecuteAsync(null);

        Assert.False(execution.IsCompleted);
        Assert.True(connect.IsConnecting);
        Assert.False(connect.ConnectCommand.CanExecute(null));
        Assert.False(connect.ConnectDemoCommand.CanExecute(null));
        Assert.True(
            demoCanExecuteChanges > 0,
            "starting a connect must raise CanExecuteChanged on the sibling command");
        Assert.Same(connect, shell.CurrentStep);

        var changesWhileInFlight = demoCanExecuteChanges;
        var released = new DirectoryConnection("released", 1);
        tcs.SetResult(released);
        await execution;

        Assert.False(connect.IsConnecting);
        Assert.True(
            demoCanExecuteChanges > changesWhileInFlight,
            "finishing a connect must raise CanExecuteChanged on the sibling command");
        Assert.True(connect.ConnectCommand.CanExecute(null));
        Assert.True(connect.ConnectDemoCommand.CanExecute(null));

        // S5: the released connect lands on the picker step (was: the DirectoryConnection
        // placeholder — see the class remarks) and carries the released connection.
        var picker = Assert.IsType<RootPickerViewModel>(shell.CurrentStep);
        await picker.LoadCandidates;
        Assert.Same(released, ConfirmAndGetCarriedConnection(shell, picker));
    }

    // --- View level: headless visual tree --------------------------------------

    [AvaloniaFact]
    public void MainWindow_RendersConnectionView_AndTogglesErrorVisibility()
    {
        var provider = Stub(new DirectoryConnection("unused", 0));
        var (shell, _) = CreateShell(provider, demo: false);
        var window = new MainWindow { DataContext = shell };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var view = Assert.Single(window.GetVisualDescendants().OfType<ConnectionView>());
        var connect = Assert.IsType<ConnectionViewModel>(shell.CurrentStep);

        const string sentinel = "headless sentinel error";
        connect.ErrorMessage = sentinel;
        Dispatcher.UIThread.RunJobs();
        var errorBlock = Assert.Single(
            view.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == sentinel);
        Assert.True(errorBlock.IsVisible, "the error TextBlock must show while ErrorMessage is set");

        connect.ErrorMessage = null;
        Dispatcher.UIThread.RunJobs();
        Assert.False(errorBlock.IsVisible, "the error TextBlock must hide when ErrorMessage clears");

        window.Close();
    }

    [AvaloniaFact]
    public async Task MainWindow_SwapsConnectionViewOut_WhenTheStepAdvances()
    {
        var provider = Stub(new DirectoryConnection("stub demo directory", 7));
        var (shell, _) = CreateShell(provider, demo: false);
        var window = new MainWindow { DataContext = shell };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        var connect = Assert.IsType<ConnectionViewModel>(shell.CurrentStep);

        await connect.ConnectDemoCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();

        // The DataTemplate switch (D5) replaces the Connect view with the root picker.
        Assert.Empty(window.GetVisualDescendants().OfType<ConnectionView>());
        Assert.Single(window.GetVisualDescendants().OfType<RootPickerView>());

        window.Close();
    }

    // --- helpers ----------------------------------------------------------------

    /// <summary>
    /// Reads the <see cref="DirectoryConnection"/> a picker carries by driving its confirm
    /// path: S5 made the connection private to the picker, so the only public window onto it
    /// is the <see cref="WorkspaceViewModel"/> it builds. Advances the shell to the
    /// Workspace step as a side effect — call it last.
    /// </summary>
    private static DirectoryConnection ConfirmAndGetCarriedConnection(
        ShellViewModel shell, RootPickerViewModel picker)
    {
        picker.SelectedCandidate = new AdObject
        {
            Dn = "OU=Sentinel,DC=stub,DC=lab",
            Kind = AdObjectKind.OrganizationalUnit,
            Name = "Sentinel",
        };
        picker.LoadRootCommand.Execute(null);
        return Assert.IsType<WorkspaceViewModel>(shell.CurrentStep).Connection;
    }

    /// <summary>Stub whose connect succeeds with <paramref name="connection"/>.</summary>
    private static StubDirectoryProvider Stub(DirectoryConnection connection) =>
        new(Task.FromResult(connection));

    /// <summary>Stub whose connect faults with <paramref name="exception"/>.</summary>
    private static StubDirectoryProvider Failing(Exception exception) =>
        new(Task.FromException<DirectoryConnection>(exception));

    /// <summary>
    /// Builds a shell around <paramref name="provider"/>; <c>FactoryArgs</c> records every
    /// demo/live bool the factory receives, so tests pin which path was requested.
    /// The explicit WebView2 status matters: the ctor default falls back to
    /// <see cref="WebView2Runtime.Probe"/>, which reads the LIVE registry — per-machine
    /// flakiness a connection-flow test must never inherit.
    /// </summary>
    private static (ShellViewModel Shell, List<bool> FactoryArgs) CreateShell(
        StubDirectoryProvider provider, bool demo)
    {
        var factoryArgs = new List<bool>();
        var shell = new ShellViewModel(
            d =>
            {
                factoryArgs.Add(d);
                return provider;
            },
            new StartupOptions(Demo: demo),
            new WebView2RuntimeStatus(IsInstalled: true, Version: "test"));
        return (shell, factoryArgs);
    }
}
