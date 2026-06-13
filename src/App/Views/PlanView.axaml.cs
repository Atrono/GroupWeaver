using Avalonia.Controls;

using GroupWeaver.App.ViewModels;

namespace GroupWeaver.App.Views;

public sealed partial class PlanView : UserControl
{
    public PlanView()
    {
        InitializeComponent();

        // Mount the plan renderer's surface into the reserved GraphHost region (mirrors
        // WorkspaceView, ADR-001 guardrail 5). Without a renderer view (null factory, missing
        // WebView2 Runtime, headless fakes) the XAML placeholder stays in place.
        DataContextChanged += (_, _) =>
        {
            if (DataContext is PlanViewModel { GraphRenderer.View: { } rendererView })
            {
                GraphHost.Content = rendererView;
            }
        };
    }
}
