using System.ComponentModel;

using GroupWeaver.App.Settings;
using GroupWeaver.App.Startup;
using GroupWeaver.App.Tests.Fakes;
using GroupWeaver.App.ViewModels;
using GroupWeaver.Core.Model;
using GroupWeaver.Core.Providers;

using Xunit;

namespace GroupWeaver.App.Tests;

/// <summary>
/// Pins the ADR-026 D6 (WP2) demo-mode flag on <see cref="ShellViewModel"/>: the honest
/// source behind the MainWindow top-strip "DEMO" badge (<c>IsVisible="{Binding IsDemoMode}"</c>).
/// Covers the false default (no connection yet), the demo path setting
/// <see cref="ShellViewModel.IsDemoMode"/> true while the live path leaves it false,
/// Back-to-Connect resetting it, and the property change-notifying (so the badge binding updates).
///
/// <para>The demo flag is driven through the REAL seam: the <see cref="ConnectionViewModel"/>
/// <c>ConnectDemoCommand</c>/<c>ConnectCommand</c> the shell builds (which calls
/// <c>ConnectCoreAsync(demo)</c> → the 3-arg <c>OnConnected(provider, connection, isDemo)</c>),
/// with a <see cref="StubDirectoryProvider"/> as the fake provider — mirroring
/// <see cref="ConnectionFlowTests"/>/<see cref="ShellThemeTests"/>. The shell ctor now reads
/// <c>ui-state.json</c> (theme seed), so EVERY test injects a
/// <see cref="Directory.CreateTempSubdirectory(string)"/>-backed <see cref="UiStateStore"/> per
/// the #124 lesson — nothing touches real <c>%APPDATA%</c>. Assertions are over the UI-free VM
/// flag, so plain <see cref="FactAttribute"/>.</para>
/// </summary>
public sealed class ShellDemoModeTests
{
    private const string RootDn = "OU=Lab,DC=stub,DC=lab";

    // --- default: false before any connection -------------------------------------------

    [Fact]
    public void Default_BeforeAnyConnection_IsDemoModeFalse()
    {
        using var dir = new TempDir();

        // A fresh shell sits on the Connect step with no connection yet: the badge source must
        // be false so the DEMO badge stays hidden on the Connect screen.
        using var shell = NewConnectShell(dir.Path, Stub());

        Assert.False(shell.IsDemoMode);
    }

    // --- demo path sets the flag; live path leaves it false ------------------------------

    [Fact]
    public async Task DemoConnect_SetsIsDemoModeTrue()
    {
        using var dir = new TempDir();
        using var shell = NewConnectShell(dir.Path, Stub());

        var connect = Assert.IsType<ConnectionViewModel>(shell.CurrentStep);
        await connect.ConnectDemoCommand.ExecuteAsync(null);

        // The demo button routes through ConnectCoreAsync(demo:true) → OnConnected(.., isDemo:true).
        Assert.True(shell.IsDemoMode);
        Assert.IsType<RootPickerViewModel>(shell.CurrentStep);
    }

    [Fact]
    public async Task LiveConnect_LeavesIsDemoModeFalse()
    {
        using var dir = new TempDir();
        using var shell = NewConnectShell(dir.Path, Stub());

        var connect = Assert.IsType<ConnectionViewModel>(shell.CurrentStep);
        await connect.ConnectCommand.ExecuteAsync(null);

        // The live button routes through ConnectCoreAsync(demo:false) → OnConnected(.., isDemo:false):
        // the flag must stay false even though the connect succeeded and advanced the step.
        Assert.False(shell.IsDemoMode);
        Assert.IsType<RootPickerViewModel>(shell.CurrentStep);
    }

    // --- Back-to-Connect resets the flag ------------------------------------------------

    [Fact]
    public async Task BackToConnect_ResetsIsDemoModeToFalse()
    {
        using var dir = new TempDir();
        using var shell = NewConnectShell(dir.Path, Stub());

        // Demo-connect (flag true), then Back out of the root picker (OnBackToConnect drops the
        // connection and clears the flag — the badge must not survive onto the fresh Connect step).
        var connect = Assert.IsType<ConnectionViewModel>(shell.CurrentStep);
        await connect.ConnectDemoCommand.ExecuteAsync(null);
        Assert.True(shell.IsDemoMode);

        var picker = Assert.IsType<RootPickerViewModel>(shell.CurrentStep);
        picker.BackCommand.Execute(null);

        Assert.IsType<ConnectionViewModel>(shell.CurrentStep);
        Assert.False(shell.IsDemoMode);
    }

    // --- IsDemoMode raises PropertyChanged (the badge binding stays live) ----------------

    [Fact]
    public async Task DemoConnect_ChangeNotifiesIsDemoMode()
    {
        using var dir = new TempDir();
        using var shell = NewConnectShell(dir.Path, Stub());

        // Record the explicit-name notification — the live proof the [ObservableProperty] setter
        // raises it (the DEMO badge's IsVisible binding would silently stick to hidden otherwise).
        var demoModeNotified = false;
        void OnShellChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(ShellViewModel.IsDemoMode))
            {
                demoModeNotified = true;
            }
        }

        shell.PropertyChanged += OnShellChanged;

        var connect = Assert.IsType<ConnectionViewModel>(shell.CurrentStep);
        await connect.ConnectDemoCommand.ExecuteAsync(null);

        Assert.True(
            demoModeNotified,
            "the demo connect must raise PropertyChanged for IsDemoMode (proves the "
            + "[ObservableProperty] setter notifies — the DEMO badge's IsVisible binding stays live)");

        shell.PropertyChanged -= OnShellChanged;
    }

    // --- helpers ------------------------------------------------------------------------

    /// <summary>Stub whose connect succeeds and whose root-candidate load returns one OU, so the
    /// demo/live connect advances cleanly to a settled <see cref="RootPickerViewModel"/>.</summary>
    private static StubDirectoryProvider Stub() =>
        new(Task.FromResult(new DirectoryConnection("stub directory", 0)))
        {
            RootCandidatesResult = Task.FromResult<IReadOnlyList<AdObject>>([
                new AdObject { Dn = RootDn, Kind = AdObjectKind.OrganizationalUnit, Name = "Lab" },
            ]),
            LoadScopeResult = Task.FromResult(new DirectorySnapshot()),
        };

    /// <summary>A fresh shell on the Connect step (no --demo, no advance) over
    /// <paramref name="provider"/>, persisting to <paramref name="baseDir"/> (never real
    /// <c>%APPDATA%</c>, the #124 seam), with an explicit WebView2-present status (the ctor default
    /// probes the live registry — per-machine flakiness a VM test must not inherit).</summary>
    private static ShellViewModel NewConnectShell(string baseDir, StubDirectoryProvider provider) =>
        new(
            _ => provider,
            new StartupOptions(Demo: false),
            new WebView2RuntimeStatus(IsInstalled: true, Version: "test"),
            graphRendererFactory: null,
            ruleset: null,
            locator: null,
            uiStateStore: new UiStateStore(baseDir));

    private sealed class TempDir : IDisposable
    {
        public string Path { get; } =
            Directory.CreateTempSubdirectory("groupweaver-shell-demo-mode-tests-").FullName;

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch (IOException)
            {
                // Best-effort cleanup; never fail a test over temp-dir teardown.
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}
