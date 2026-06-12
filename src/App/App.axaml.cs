using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using GroupWeaver.App.Views;

namespace GroupWeaver.App;

public sealed partial class App : Application
{
    /// <summary>
    /// Set by <see cref="Program"/> before the framework starts. The default keeps
    /// harnesses that build the app without going through <c>Main</c> (headless tests,
    /// ADR-003 D6) working; the composition root (ADR-003 D7, later AP 2.1 slice)
    /// consumes it in <see cref="OnFrameworkInitializationCompleted"/>.
    /// </summary>
    public static StartupOptions StartupOptions { get; set; } = new(Demo: false);

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = new MainWindow();
        }

        base.OnFrameworkInitializationCompleted();
    }
}
