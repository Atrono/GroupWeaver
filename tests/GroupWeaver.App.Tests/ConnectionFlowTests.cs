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

    // --- ADR-031: targeted-connect (the Advanced disclosure) -------------------

    [Fact]
    public async Task TargetedConnect_ValidatedServerAndBaseDn_ReachTheTargetedFactory()
    {
        var expected = new DirectoryConnection("targeted directory", 3);
        var provider = Stub(expected);
        var targetedArgs = new List<(string? Server, string? BaseDn)>();
        var connected = new List<(IDirectoryProvider Provider, bool Demo)>();
        var connect = new ConnectionViewModel(
            _ => throw new InvalidOperationException("the serverless factory must NOT run when targeting"),
            (p, _, demo) => connected.Add((p, demo)),
            (server, baseDn) =>
            {
                targetedArgs.Add((server, baseDn));
                return provider;
            })
        {
            TargetServer = "  dc01.corp.local  ", // surrounding whitespace is trimmed by the validator
            TargetBaseDn = "OU=Groups,DC=corp,DC=local",
        };

        await connect.ConnectCommand.ExecuteAsync(null);

        // The validated, TRIMMED values reach the targeted factory exactly once.
        var args = Assert.Single(targetedArgs);
        Assert.Equal("dc01.corp.local", args.Server);
        Assert.Equal("OU=Groups,DC=corp,DC=local", args.BaseDn);
        Assert.Same(provider, Assert.Single(connected).Provider);
        Assert.False(Assert.Single(connected).Demo, "the live targeted path is never the demo flag");
        Assert.Null(connect.ErrorMessage);
    }

    [Fact]
    public async Task TargetedConnect_OnlyServerEntered_PassesNullBaseDn()
    {
        var provider = Stub(new DirectoryConnection("targeted", 1));
        var targetedArgs = new List<(string? Server, string? BaseDn)>();
        var connect = new ConnectionViewModel(
            _ => throw new InvalidOperationException("the serverless factory must NOT run when one field is set"),
            (_, _, _) => { },
            (server, baseDn) =>
            {
                targetedArgs.Add((server, baseDn));
                return provider;
            })
        {
            TargetServer = "10.0.0.5",
            // base DN left blank => validates to null (read defaultNamingContext at bind)
        };

        await connect.ConnectCommand.ExecuteAsync(null);

        var args = Assert.Single(targetedArgs);
        Assert.Equal("10.0.0.5", args.Server);
        Assert.Null(args.BaseDn);
        Assert.Null(connect.ErrorMessage);
    }

    [Fact]
    public async Task BlankAdvancedFields_TakeTheServerlessDefault_PreservingZeroConfig()
    {
        var expected = new DirectoryConnection("serverless directory", 9);
        var provider = Stub(expected);
        var serverlessArgs = new List<bool>();
        var connected = new List<IDirectoryProvider>();
        var connect = new ConnectionViewModel(
            demo =>
            {
                serverlessArgs.Add(demo);
                return provider;
            },
            (p, _, _) => connected.Add(p),
            (_, _) => throw new InvalidOperationException(
                "the targeted factory must NOT run when both Advanced fields are blank (zero-config default)"));
        // TargetServer / TargetBaseDn left null (the collapsed Advanced default).

        await connect.ConnectCommand.ExecuteAsync(null);

        // The zero-config serverless path: _providerFactory(false), never the targeted factory.
        Assert.False(Assert.Single(serverlessArgs), "blank Advanced fields must request the live serverless provider");
        Assert.Same(provider, Assert.Single(connected));
        Assert.Null(connect.ErrorMessage);
    }

    [Fact]
    public async Task TargetedFactoryNull_WithFieldsEntered_FallsBackToServerless()
    {
        // A pre-ADR-031 call site (no targeted factory wired) must still connect serverlessly
        // even with the fields populated — never NRE on a null targeted factory.
        var provider = Stub(new DirectoryConnection("serverless fallback", 2));
        var serverlessArgs = new List<bool>();
        var connect = new ConnectionViewModel(
            demo =>
            {
                serverlessArgs.Add(demo);
                return provider;
            },
            (_, _, _) => { },
            targetedProviderFactory: null)
        {
            TargetServer = "dc01",
            TargetBaseDn = "DC=corp,DC=local",
        };

        await connect.ConnectCommand.ExecuteAsync(null);

        Assert.False(Assert.Single(serverlessArgs), "no targeted factory ⇒ the live serverless provider is used");
        Assert.Null(connect.ErrorMessage);
    }

    [Theory]
    [InlineData("LDAP://evil/DC=x", null)] // injected scheme/path in the server
    [InlineData("a/b", null)]
    [InlineData("a(b", null)] // filter metacharacter in the server
    [InlineData(null, "notadn")] // garbage base DN
    [InlineData(null, "=novalue")]
    public async Task InvalidAdvancedInput_SurfacesInlineError_AndBuildsNoProvider(
        string? server, string? baseDn)
    {
        var serverlessCalls = 0;
        var targetedCalls = 0;
        var connected = 0;
        var connect = new ConnectionViewModel(
            _ =>
            {
                serverlessCalls++;
                return Stub(new DirectoryConnection("unused", 0));
            },
            (_, _, _) => connected++,
            (_, _) =>
            {
                targetedCalls++;
                return Stub(new DirectoryConnection("unused", 0));
            })
        {
            TargetServer = server,
            TargetBaseDn = baseDn,
        };

        await connect.ConnectCommand.ExecuteAsync(null);

        // ADR-031 D5 / ADR-003 D7: invalid input shows the inline error and aborts BEFORE any
        // provider is built — never reach AdsPath with an injectable host or a garbage base.
        Assert.False(string.IsNullOrWhiteSpace(connect.ErrorMessage), "invalid input must surface an inline error");
        Assert.Equal(0, serverlessCalls);
        Assert.Equal(0, targetedCalls);
        Assert.Equal(0, connected);
        Assert.False(connect.IsConnecting, "a rejected validation must release the in-flight latch");
    }

    [Fact]
    public async Task DemoPath_IgnoresAdvancedTargeting_EvenWithInvalidServer()
    {
        // The demo path never targets: an invalid server in the Advanced fields must not block
        // Demo mode (targeting is a live-path concern only).
        var provider = Stub(new DirectoryConnection("demo", 7));
        var serverlessArgs = new List<bool>();
        var connect = new ConnectionViewModel(
            demo =>
            {
                serverlessArgs.Add(demo);
                return provider;
            },
            (_, _, _) => { },
            (_, _) => throw new InvalidOperationException("demo must never reach the targeted factory"))
        {
            TargetServer = "LDAP://would-be-invalid", // ignored on the demo path
        };

        await connect.ConnectDemoCommand.ExecuteAsync(null);

        Assert.True(Assert.Single(serverlessArgs), "the demo path requests the demo provider regardless of targeting");
        Assert.Null(connect.ErrorMessage);
    }

    [Fact]
    public void TargetLine_NamesTheDirectory_AcrossTheFourTargetingStates()
    {
        var connect = new ConnectionViewModel(_ => Stub(new DirectoryConnection("x", 0)), (_, _, _) => { });
        string ctx = connect.CurrentUserContext;

        // Both blank: the serverless default resolves the FQDN at bind time.
        Assert.Equal($"as {ctx} against the detected domain", connect.TargetLine);

        connect.TargetServer = "dc01.corp.local";
        Assert.Equal($"as {ctx} against dc01.corp.local", connect.TargetLine);

        connect.TargetServer = null;
        connect.TargetBaseDn = "OU=Groups,DC=corp,DC=local";
        Assert.Equal($"as {ctx} against OU=Groups,DC=corp,DC=local", connect.TargetLine);

        connect.TargetServer = "dc01";
        Assert.Equal($"as {ctx} against dc01 — OU=Groups,DC=corp,DC=local", connect.TargetLine);
    }

    [Fact]
    public void TargetProsePlusTargetDn_ComposeTargetLine_AcrossTheFourTargetingStates()
    {
        // #287: the view renders TargetProse + TargetDn as two Runs (the DN half in the
        // mono dn face); their concatenation must stay byte-identical to the pinned
        // TargetLine in every branch, or the split silently rewrites the confirmation text.
        var connect = new ConnectionViewModel(_ => Stub(new DirectoryConnection("x", 0)), (_, _, _) => { });

        // Both blank.
        Assert.Equal(connect.TargetLine, connect.TargetProse + connect.TargetDn);

        // Server only.
        connect.TargetServer = "dc01.corp.local";
        Assert.Equal(connect.TargetLine, connect.TargetProse + connect.TargetDn);

        // Base DN only.
        connect.TargetServer = null;
        connect.TargetBaseDn = "OU=Groups,DC=corp,DC=local";
        Assert.Equal(connect.TargetLine, connect.TargetProse + connect.TargetDn);

        // Both entered.
        connect.TargetServer = "dc01";
        Assert.Equal(connect.TargetLine, connect.TargetProse + connect.TargetDn);
    }

    [Fact]
    public void TargetDn_CarriesTheTrimmedBaseDnOnly_NeverAHostname()
    {
        var connect = new ConnectionViewModel(_ => Stub(new DirectoryConnection("x", 0)), (_, _, _) => { });
        string ctx = connect.CurrentUserContext;

        // Both blank and server-only: no base DN entered ⇒ the mono Run renders nothing —
        // a hostname is prose, never a DN (#287 monospace honesty).
        Assert.Equal(string.Empty, connect.TargetDn);
        connect.TargetServer = "dc01.corp.local";
        Assert.Equal(string.Empty, connect.TargetDn);

        // Base DN only: TargetDn is the TRIMMED base DN; the prose keeps the connective
        // text and ends where the mono Run takes over.
        connect.TargetServer = null;
        connect.TargetBaseDn = "  OU=Groups,DC=corp,DC=local  ";
        Assert.Equal("OU=Groups,DC=corp,DC=local", connect.TargetDn);
        Assert.Equal($"as {ctx} against ", connect.TargetProse);

        // Both entered: the host stays in the prose (ending "{server} — "); TargetDn is
        // still the DN alone.
        connect.TargetServer = "dc01";
        Assert.Equal("OU=Groups,DC=corp,DC=local", connect.TargetDn);
        Assert.Equal($"as {ctx} against dc01 — ", connect.TargetProse);
    }

    [Fact]
    public void ToggleAdvanced_FlipsTheDisclosure_CollapsedByDefault()
    {
        var connect = new ConnectionViewModel(_ => Stub(new DirectoryConnection("x", 0)), (_, _, _) => { });

        Assert.False(connect.IsAdvancedExpanded, "the Advanced disclosure is collapsed by default (zero-config)");

        connect.ToggleAdvancedCommand.Execute(null);
        Assert.True(connect.IsAdvancedExpanded);

        connect.ToggleAdvancedCommand.Execute(null);
        Assert.False(connect.IsAdvancedExpanded);
    }

    [Fact]
    public async Task ShellTargetedFactory_ThreadsThroughToTheConnectStep()
    {
        // The ShellViewModel's optional trailing targetedProviderFactory must reach the Connect
        // step it builds — proving the wire is end-to-end, not just on a bare ConnectionViewModel.
        var provider = Stub(new DirectoryConnection("shell targeted", 4));
        var targetedArgs = new List<(string? Server, string? BaseDn)>();
        var shell = new ShellViewModel(
            _ => throw new InvalidOperationException("the serverless factory must NOT run when targeting"),
            new StartupOptions(Demo: false),
            new WebView2RuntimeStatus(IsInstalled: true, Version: "test"),
            targetedProviderFactory: (server, baseDn) =>
            {
                targetedArgs.Add((server, baseDn));
                return provider;
            });
        var connect = Assert.IsType<ConnectionViewModel>(shell.CurrentStep);
        connect.TargetServer = "dc02.corp.local";

        await connect.ConnectCommand.ExecuteAsync(null);

        var args = Assert.Single(targetedArgs);
        Assert.Equal("dc02.corp.local", args.Server);
        Assert.Null(args.BaseDn);
        Assert.Same(provider, shell.Provider);
        Assert.IsType<RootPickerViewModel>(shell.CurrentStep);
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
