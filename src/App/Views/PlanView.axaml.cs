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
            if (DataContext is not PlanViewModel { GraphRenderer.View: { } rendererView } vm)
            {
                return;
            }

            // #122 (ADR-025): MOUNT through the coordinator when wired (atomic reparent, preserves
            // a parked-and-alive viewport); else keep today's direct mount with the stale-parent
            // guard. See WorkspaceView for the full rationale.
            if (vm.GraphSurfaceCoordinator is { } coordinator)
            {
                coordinator.Mount(rendererView, GraphHost);
            }
            else
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

        // Release the shared renderer control on leave — but ONLY IF THE SURFACE IS STILL OUR CHILD
        // (#122 / ADR-025): a shell-parked surface was already moved out by the coordinator, so this
        // is a no-op for it; an abandoned / no-coordinator surface is still our child -> released
        // (ADR-024 D1 fallback preserved). See WorkspaceView for the full rationale.
        DetachedFromVisualTree += (_, _) =>
        {
            if (GraphHost.Content is Control content
                && DataContext is PlanViewModel { GraphRenderer.View: { } view }
                && ReferenceEquals(content, view))
            {
                GraphHost.Content = null;
            }
        };
    }
}
