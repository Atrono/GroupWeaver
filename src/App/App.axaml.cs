using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
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
            var shell = new ShellViewModel(
                static demo => demo ? new DemoProvider() : (IDirectoryProvider)new LdapProvider(),
                StartupOptions);
            desktop.MainWindow = new MainWindow { DataContext = shell };
        }

        base.OnFrameworkInitializationCompleted();
    }
}
