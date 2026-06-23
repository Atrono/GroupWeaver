using Avalonia.Controls;

namespace GroupWeaver.App.Graph;

/// <summary>
/// The one window-scoped <see cref="IGraphSurfaceCoordinator"/> (#122 / ADR-025): reparents a
/// step's live graph surface between its <c>GraphHost</c> and the always-attached, hidden parking
/// <see cref="Panel"/> handed in by <c>MainWindow.OnOpened</c>. Each hop is a SINGLE synchronous
/// reparent — for a <see cref="NativeWebView"/> the detach+attach pair is wrapped in
/// <c>BeginReparenting(true)</c> (the atomic native reparent: NEVER a transient un-root between
/// the detach and the attach, the load-bearing point the ParkSpike proved); a non-WebView control
/// (a test surface) takes the plain swap (the spike proved it equivalent for a control with no
/// page). UI-thread-only by contract.
/// </summary>
public sealed class GraphSurfaceCoordinator : IGraphSurfaceCoordinator
{
    /// <summary>The hidden, always-attached parking host (the XAML <c>ParkingLot</c> Panel). A
    /// Panel so Workspace + the current Plan surface can be parked at once.</summary>
    private readonly Panel _parkingLot;

    public GraphSurfaceCoordinator(Panel parkingLot)
    {
        ArgumentNullException.ThrowIfNull(parkingLot);
        _parkingLot = parkingLot;
    }

    /// <inheritdoc />
    public void Park(Control view)
    {
        ArgumentNullException.ThrowIfNull(view);

        // Already parked → nothing to move (the shell may park a surface that was parked on the
        // prior hop, e.g. Workspace stays parked across Design-plan then Gap).
        if (ReferenceEquals(view.Parent, _parkingLot))
        {
            return;
        }

        Reparent(view, () =>
        {
            DetachFromCurrentHost(view);
            _parkingLot.Children.Add(view);
        });
    }

    /// <inheritdoc />
    public bool Mount(Control view, ContentControl graphHost)
    {
        ArgumentNullException.ThrowIfNull(view);
        ArgumentNullException.ThrowIfNull(graphHost);

        // Already where it belongs → no move, but it WAS parked-and-alive only if it came from the
        // parking lot; sitting in the right GraphHost already is the same "no re-render needed" case.
        if (ReferenceEquals(graphHost.Content, view))
        {
            return true;
        }

        // Parked-and-alive iff the live surface is currently held in the parking lot: the page
        // survived, so the caller skips the ADR-024 re-render fallback.
        var wasAliveParked = ReferenceEquals(view.Parent, _parkingLot);

        Reparent(view, () =>
        {
            DetachFromCurrentHost(view);
            graphHost.Content = view;
        });

        return wasAliveParked;
    }

    /// <summary>Runs <paramref name="move"/> as the atomic reparent: a <see cref="NativeWebView"/>
    /// inside <c>BeginReparenting(true)</c> (no transient un-root between detach and attach); any
    /// other control takes the plain move. The <c>true</c> argument
    /// (<c>yieldOnLayoutBeforeExiting</c>) lets the control settle its native reparent on the next
    /// layout pass before the scope exits — the exact usage the spike validated.</summary>
    private static void Reparent(Control view, Action move)
    {
        if (view is NativeWebView webView)
        {
            using (webView.BeginReparenting(true))
            {
                move();
            }
        }
        else
        {
            move();
        }
    }

    /// <summary>Removes <paramref name="view"/> from whatever host currently parents it — a
    /// <see cref="ContentControl"/> (a GraphHost / the step ContentControl) by clearing
    /// <c>Content</c>, a <see cref="Panel"/> (the parking lot, or a stale Panel host) by removing
    /// the child — so the subsequent add lands cleanly. The control is still rooted at this instant
    /// only inside a <c>BeginReparenting</c> scope; the immediate re-add keeps it continuously
    /// rooted for the plain path too.</summary>
    private static void DetachFromCurrentHost(Control view)
    {
        switch (view.Parent)
        {
            case ContentControl host when ReferenceEquals(host.Content, view):
                host.Content = null;
                break;
            case Panel panel:
                panel.Children.Remove(view);
                break;
        }
    }
}
