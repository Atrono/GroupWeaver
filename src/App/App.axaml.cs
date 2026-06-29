using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using GroupWeaver.App.Graph;
using GroupWeaver.App.Rules;
using GroupWeaver.App.Settings;
using GroupWeaver.App.Startup;
using GroupWeaver.App.ViewModels;
using GroupWeaver.App.Views;
using GroupWeaver.Core.Providers;
using GroupWeaver.Providers;

namespace GroupWeaver.App;

public sealed partial class App : Application
{
    /// <summary>
    /// Set by <see cref="Program"/> before the framework starts. The default keeps
    /// harnesses that build the app without going through <c>Main</c> (headless tests,
    /// ADR-003 D6) working; the composition root consumes it in
    /// <see cref="OnFrameworkInitializationCompleted"/>.
    /// </summary>
    public static StartupOptions StartupOptions { get; set; } = new(Demo: false);

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    /// <summary>
    /// Manual composition root (ADR-003 D7): the object graph is built by hand; the
    /// provider factory is the single seam tests substitute. LdapProvider is
    /// windows-only, which this project's TFM (net8.0-windows) guarantees statically.
    /// </summary>
    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // The ruleset every workspace Evaluate runs against (ADR-010 §3) — located
            // ONCE here (ADR-008 whole-file precedence). EffectiveRuleset.Errors are
            // carried, surfaced by AP 3.3's settings UI; the locator never throws. The
            // same locator instance is handed to the shell so a settings Save persists to
            // its UserRulesetPath (AP 3.3 / ADR-011 §1).
            var locator = new RulesetLocator();
            // ADR-022 D4: the one rail-state store, threaded down the same Shell→RootPicker→
            // Workspace path as the locator so each workspace seeds + persists its rail state.
            var uiStateStore = new UiStateStore();
            var shell = new ShellViewModel(
                static demo => demo ? new DemoProvider() : (IDirectoryProvider)new LdapProvider(),
                StartupOptions,
                // Probed ONCE here (ADR-003 D3); missing = persistent banner, never a blocker.
                WebView2Runtime.Probe(),
                // The ONLY place the real renderer is wired (ADR-004 D5). Headless tests
                // never reach this: they construct VMs directly (null factory or fakes).
                static () => new CytoscapeGraphRenderer(),
                locator.LoadEffective(),
                locator,
                uiStateStore,
                // ADR-031 D1: the targeted live-provider builder behind the Connect card's Advanced
                // disclosure — still integrated auth, no credentials. Inputs are validated by
                // ConnectionTarget before reaching this; blank fields never call it (serverless default).
                static (server, baseDn) => new LdapProvider(server, baseDn));
            desktop.MainWindow = new MainWindow { DataContext = shell };
        }

        base.OnFrameworkInitializationCompleted();
    }
}
