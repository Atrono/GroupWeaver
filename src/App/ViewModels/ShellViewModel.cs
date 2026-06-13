using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GroupWeaver.App.Graph;
using GroupWeaver.App.Rules;
using GroupWeaver.App.Settings;
using GroupWeaver.App.Startup;
using GroupWeaver.App.Views;
using GroupWeaver.Core.Providers;
using GroupWeaver.Core.Rules;

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
    private readonly RulesetLocator _locator;

    /// <summary>The ruleset the app is currently running on (ADR-010 §3); settable
    /// (AP 3.3 / ADR-011 §3) so a settings Apply/Save re-caches it as the new effective
    /// ruleset — the next workspace inherits it through the existing Shell→RootPicker→
    /// Workspace thread, and a live workspace is re-threaded via
    /// <see cref="WorkspaceViewModel.ApplyRulesetAsync"/>.</summary>
    private EffectiveRuleset? _ruleset;

    /// <summary>Active step content; the window's DataTemplates switch on its type.</summary>
    [ObservableProperty]
    private object _currentStep;

    public ShellViewModel(
        Func<bool, IDirectoryProvider> providerFactory,
        StartupOptions startupOptions,
        WebView2RuntimeStatus? webView2Runtime = null,
        Func<IGraphRenderer>? graphRendererFactory = null,
        EffectiveRuleset? ruleset = null,
        RulesetLocator? locator = null)
    {
        _providerFactory = providerFactory;
        _graphRendererFactory = graphRendererFactory;
        _ruleset = ruleset;
        // Defaulted (AP 3.3 / ADR-011 §1): App.axaml.cs passes the one composition-root
        // locator; pre-S8 tests omit it and get the real %APPDATA% layout. Settings
        // Save persists to its UserRulesetPath; the headless tests inject a temp-dir seam.
        _locator = locator ?? new RulesetLocator();

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

    /// <summary>
    /// The top command strip's "⚙ Settings" affordance (AP 3.3 / ADR-011 §1): builds the
    /// settings VM (the <see cref="BuildSettingsViewModel"/> seam already subscribes the
    /// shell's re-thread to <see cref="SettingsViewModel.RulesetApplied"/>) and shows it
    /// as a modal <see cref="SettingsWindow"/> over the main window. Reachable on every
    /// step. <c>ShowDialog</c> is the production-only path (headless-hostile — ADR-011
    /// open-risk #3): tests drive the SAME re-thread through <see cref="BuildSettingsViewModel"/>
    /// + <c>SettingsViewModel.Save</c>/<c>Apply</c>, never this command.
    /// </summary>
    [RelayCommand]
    private async Task OpenSettingsAsync()
    {
        var vm = BuildSettingsViewModel();
        var window = new SettingsWindow { DataContext = vm };
        if (GetMainWindow() is { } owner)
        {
            await window.ShowDialog(owner);
        }
        else
        {
            window.Show();
        }
    }

    /// <summary>
    /// The shell's settings-VM seam (AP 3.3 / ADR-011 §1/§3): seeds a
    /// <see cref="SettingsViewModel"/> from the cached <see cref="EffectiveRuleset"/>
    /// (rejected-file errors carried, finally surfaced) and the injected
    /// <see cref="RulesetLocator"/>, then subscribes the shell's re-thread to its
    /// <see cref="SettingsViewModel.RulesetApplied"/> event. On Apply/Save the shell
    /// re-caches <c>_ruleset</c> as a clean <c>new EffectiveRuleset(saved, true, [])</c>
    /// and, if a live workspace is the current step, re-threads it via
    /// <see cref="WorkspaceViewModel.ApplyRulesetAsync"/> — no rebuild, viewport kept.
    /// This is the seam <see cref="OpenSettingsAsync"/> uses internally and the seam the
    /// headless integration tests drive directly (the <c>ShowDialog</c> path is <c>[I]</c>).
    /// </summary>
    public SettingsViewModel BuildSettingsViewModel()
    {
        var effective = _ruleset ?? new EffectiveRuleset(RulesetLoader.LoadDefault(), FromUserFile: false, []);
        var vm = SettingsViewModel.Open(effective, _locator);
        vm.RulesetApplied += OnRulesetApplied;
        return vm;
    }

    private async void OnRulesetApplied(Ruleset ruleset)
    {
        _ruleset = new EffectiveRuleset(ruleset, FromUserFile: true, []);
        if (CurrentStep is WorkspaceViewModel workspace)
        {
            await workspace.ApplyRulesetAsync(ruleset);
        }
    }

    /// <summary>The desktop main window, the modal owner for <see cref="OpenSettingsAsync"/>;
    /// <c>null</c> off the classic-desktop lifetime (headless theory) — the seam then
    /// falls back to a non-modal show, but tests never reach the show at all.</summary>
    private static Avalonia.Controls.Window? GetMainWindow() =>
        (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow;

    private void OnConnected(IDirectoryProvider provider, DirectoryConnection connection)
    {
        Provider = provider;
        CurrentStep = new RootPickerViewModel(
            provider, connection, OnBackToConnect, OnRootChosen, WebView2Missing,
            _graphRendererFactory, _ruleset);
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
