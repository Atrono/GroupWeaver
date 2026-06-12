using Avalonia.Controls;

using GroupWeaver.App.ViewModels;

namespace GroupWeaver.App.Views;

public sealed partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();

        // Window teardown is the one signal that cancels the workspace's in-flight
        // scope load (AP 2.2 S6); the shell disposes its active step.
        Closed += (_, _) => (DataContext as ShellViewModel)?.Dispose();
    }
}
