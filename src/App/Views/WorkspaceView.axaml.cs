using Avalonia.Controls;

using GroupWeaver.App.ViewModels;

namespace GroupWeaver.App.Views;

public sealed partial class WorkspaceView : UserControl
{
    public WorkspaceView()
    {
        InitializeComponent();

        // Mount the renderer's surface into the reserved GraphHost region (AP 2.2 S6,
        // ADR-004 D5). Without a renderer view (null factory, missing WebView2 Runtime,
        // headless fakes) the XAML placeholder stays in place.
        DataContextChanged += (_, _) =>
        {
            if (DataContext is WorkspaceViewModel { GraphRenderer.View: { } rendererView })
            {
                GraphHost.Content = rendererView;
            }
        };
    }
}
