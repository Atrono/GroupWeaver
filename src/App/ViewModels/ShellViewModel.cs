using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GroupWeaver.App.Graph;
using GroupWeaver.App.Startup;
using GroupWeaver.Core.Providers;

namespace GroupWeaver.App.ViewModels;

/// <summary>
/// Owns the shell's step state machine (ADR-003 D5): Connect → PickRoot → Workspace.
/// <see cref="CurrentStep"/> holds the active step's content object; <c>MainWindow</c>
/// maps its runtime type to a view via DataTemplates. Steps hand control back through
/// callbacks: Connect succeeds into the root picker, the picker either confirms into a
/// workspace or backs out to a fresh Connect step.
/// </summary>
public sealed partial class ShellViewModel : ObservableObject, IDisposable
{
    private readonly Func<bool, IDirectoryProvider> _providerFactory;
    private readonly Func<IGraphRenderer>? _graphRendererFactory;

    /// <summary>Active step content; the window's DataTemplates switch on its type.</summary>
    [ObservableProperty]
    private object _currentStep;

    public ShellViewModel(
        Func<bool, IDirectoryProvider> providerFactory,
        StartupOptions startupOptions,
        WebView2RuntimeStatus? webView2Runtime = null,
        Func<IGraphRenderer>? graphRendererFactory = null)
    {
        _providerFactory = providerFactory;
        _graphRendererFactory = graphRendererFactory;

        // Default = real probe, so harnesses constructing the shell directly behave like
        // the app; S8's headless tests pass an explicit status to force the missing state.
        var runtime = webView2Runtime ?? WebView2Runtime.Probe();
        WebView2Missing = !runtime.IsInstalled;
        WebView2Version = runtime.Version;

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

    /// <summary>
    /// True when the startup probe found no WebView2 Runtime (ADR-003 D3). Drives the
    /// persistent shell banner; a missing runtime never blocks — only the AP 2.2 graph
    /// view needs it. Fixed at construction (installing mid-session needs a restart).
    /// </summary>
    public bool WebView2Missing { get; }

    /// <summary>Detected runtime version (<c>pv</c>); <c>null</c> when missing.</summary>
    public string? WebView2Version { get; }

    /// <summary>Banner hyperlink: open the runtime's download page in the browser.</summary>
    [RelayCommand]
    private void OpenWebView2DownloadPage() => WebView2Runtime.OpenDownloadPage();

    private void OnConnected(IDirectoryProvider provider, DirectoryConnection connection)
    {
        Provider = provider;
        CurrentStep = new RootPickerViewModel(
            provider, connection, OnBackToConnect, OnRootChosen, WebView2Missing,
            _graphRendererFactory);
    }

    /// <summary>The picker's Back: drop the provider, start over on a fresh Connect step.</summary>
    private void OnBackToConnect()
    {
        Provider = null;
        CurrentStep = new ConnectionViewModel(_providerFactory, OnConnected);
    }

    private void OnRootChosen(WorkspaceViewModel workspace) => CurrentStep = workspace;

    /// <summary>
    /// Disposes the active step (cancels the workspace's in-flight scope load,
    /// AP 2.2 S6). The workspace is the only disposable step and is terminal in the
    /// step machine — no transition ever swaps a disposable step away mid-session.
    /// </summary>
    public void Dispose() => (CurrentStep as IDisposable)?.Dispose();
}
