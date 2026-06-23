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
                // The renderer's single control may still be parented to the GraphHost of a
                // discarded previous view: each step VM owns one renderer, and Back re-enters
                // the SAME VM, so that one control is re-mounted into a fresh view (the steps
                // share one ContentControl shell). Detach it from any stale host before
                // re-parenting, or Avalonia throws on the add.
                if (rendererView.Parent is ContentControl { } staleHost && staleHost != GraphHost)
                {
                    staleHost.Content = null;
                }

                GraphHost.Content = rendererView;
            }
        };

        // Release the shared renderer control when this view leaves the visual tree, so the
        // next view (e.g. Back to Workspace) can mount it without a "already has a visual
        // parent" conflict. The renderer keeps the control alive; only the parenting is freed.
        DetachedFromVisualTree += (_, _) => GraphHost.Content = null;
    }
}
