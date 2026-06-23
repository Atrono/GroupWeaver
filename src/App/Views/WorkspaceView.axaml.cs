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
            if (DataContext is not WorkspaceViewModel { GraphRenderer.View: { } rendererView } vm)
            {
                return;
            }

            // #122 (ADR-025): when the window's coordinator is wired, MOUNT through it — a single
            // atomic reparent that preserves a parked-and-alive surface's live page + viewport
            // (wasAliveParked == true). When false, the surface was reclaimed from elsewhere and
            // the renderer's own re-attach (OnAttached -> ReNavigateAndReplayAsync) restores the
            // graph exactly as today (the ADR-024 fallback). With NO coordinator (headless / no
            // window) keep today's direct mount with the stale-parent guard.
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

        // Release the shared renderer control when this view leaves the visual tree, so the next
        // view (e.g. Back to Workspace) can mount it without a "already has a visual parent"
        // conflict (ADR-024 D1). RELEASE ONLY IF THE SURFACE IS STILL OUR CHILD (#122 / ADR-025):
        // when the shell PARKED this surface before the swap, the coordinator already moved it out,
        // so GraphHost.Content is no longer it -> no-op -> the parked live control is NOT torn down.
        // An abandoned / no-coordinator surface is still our child -> released (fallback preserved).
        DetachedFromVisualTree += (_, _) =>
        {
            if (GraphHost.Content is Control content
                && DataContext is WorkspaceViewModel { GraphRenderer.View: { } view }
                && ReferenceEquals(content, view))
            {
                GraphHost.Content = null;
            }
        };
    }
}
