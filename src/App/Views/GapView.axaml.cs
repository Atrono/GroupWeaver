using Avalonia.Controls;

using GroupWeaver.App.ViewModels;

namespace GroupWeaver.App.Views;

public sealed partial class GapView : UserControl
{
    public GapView()
    {
        InitializeComponent();

        // Mount the gap renderer's surface into the reserved GraphHost region (mirrors PlanView /
        // WorkspaceView, ADR-001 guardrail 5). Without a renderer view (null factory, missing
        // WebView2 Runtime, headless fakes) the XAML placeholder stays in place.
        DataContextChanged += (_, _) =>
        {
            if (DataContext is GapViewModel { GraphRenderer.View: { } rendererView })
            {
                GraphHost.Content = rendererView;
            }
        };
    }
}
