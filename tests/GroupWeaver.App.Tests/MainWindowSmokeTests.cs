using Avalonia.Headless.XUnit;

using GroupWeaver.App.Views;

using Xunit;

namespace GroupWeaver.App.Tests;

/// <summary>
/// Headless smoke for the Avalonia shell (AP 2.1): the window must construct and show
/// under the headless platform booted by <see cref="TestAppBuilder"/>.
/// </summary>
public sealed class MainWindowSmokeTests
{
    [AvaloniaFact]
    public void MainWindow_ShowsWithExpectedTitle()
    {
        var window = new MainWindow();

        window.Show();

        Assert.True(window.IsVisible, "MainWindow.Show() left the window invisible");
        Assert.Equal("GroupWeaver", window.Title);
    }
}
