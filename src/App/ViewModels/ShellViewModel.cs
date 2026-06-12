using CommunityToolkit.Mvvm.ComponentModel;
using GroupWeaver.Core.Providers;

namespace GroupWeaver.App.ViewModels;

/// <summary>
/// Owns the shell's step state machine (ADR-003 D5): Connect → PickRoot → Workspace.
/// <see cref="CurrentStep"/> holds the active step's content object; <c>MainWindow</c>
/// maps its runtime type to a view via DataTemplates. Steps hand control back through
/// callbacks: Connect succeeds into the root picker, the picker either confirms into a
/// workspace or backs out to a fresh Connect step.
/// </summary>
public sealed partial class ShellViewModel : ObservableObject
{
    private readonly Func<bool, IDirectoryProvider> _providerFactory;

    /// <summary>Active step content; the window's DataTemplates switch on its type.</summary>
    [ObservableProperty]
    private object _currentStep;

    public ShellViewModel(
        Func<bool, IDirectoryProvider> providerFactory, StartupOptions startupOptions)
    {
        _providerFactory = providerFactory;
        var connect = new ConnectionViewModel(providerFactory, OnConnected);
        _currentStep = connect;

        // --demo auto-connects without user input; storing the Task keeps the startup
        // work observable (headless tests await Initialization, no fire-and-forget).
        Initialization = startupOptions.Demo
            ? connect.ConnectDemoCommand.ExecuteAsync(null)
            : Task.CompletedTask;
    }

    /// <summary>
    /// The <c>--demo</c> startup auto-connect, or a completed task when none runs.
    /// Never faults on an unreachable directory — the Connect step shows that inline.
    /// </summary>
    public Task Initialization { get; }

    /// <summary>
    /// Provider behind the active connection; set when the Connect step succeeds,
    /// dropped when the picker backs out to the Connect step.
    /// </summary>
    public IDirectoryProvider? Provider { get; private set; }

    private void OnConnected(IDirectoryProvider provider, DirectoryConnection connection)
    {
        Provider = provider;
        CurrentStep = new RootPickerViewModel(provider, connection, OnBackToConnect, OnRootChosen);
    }

    /// <summary>The picker's Back: drop the provider, start over on a fresh Connect step.</summary>
    private void OnBackToConnect()
    {
        Provider = null;
        CurrentStep = new ConnectionViewModel(_providerFactory, OnConnected);
    }

    private void OnRootChosen(WorkspaceViewModel workspace) => CurrentStep = workspace;
}
