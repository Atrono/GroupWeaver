using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;

using GroupWeaver.App.Graph;

using Xunit;

namespace GroupWeaver.App.Tests.Graph;

/// <summary>
/// Direct unit pin of <see cref="GraphSurfaceCoordinator"/> (#122 / ADR-025) — the park/mount
/// reparent primitive, isolated from the shell/views. A real <see cref="Border"/> surface, a real
/// parking <see cref="Panel"/>, and a real <see cref="ContentControl"/> GraphHost, all inside a
/// SHOWN headless window so each hop is followed by a genuine layout pass (the existing
/// <c>BackNavigationStepSwapTests.ForceLayoutPass</c> capture-and-discard + RunJobs pattern).
///
/// <para>Avalonia.Headless cannot host a real <c>NativeWebView</c>, so the surface is a
/// <see cref="Border"/> — the coordinator's <c>Reparent</c> takes the plain-swap branch (the spike
/// proved it equivalent to <c>BeginReparenting</c> for a control with no live page). These tests
/// pin the parent transitions and the <c>wasAliveParked</c> verdict; the WebView2-specific atomic
/// reparent (same child HWND) is the windowed smoke's job.</para>
/// </summary>
public sealed class GraphSurfaceCoordinatorTests
{
    /// <summary>
    /// The full park→mount round-trip: the surface starts under the GraphHost, <c>Park</c> moves it
    /// into the parking lot, <c>Mount</c> moves it back into the GraphHost — and because it WAS in
    /// the lot, <c>Mount</c> reports <c>wasAliveParked == true</c>. The surface's <c>Parent</c> is
    /// asserted at each hop (it followed the move and never went un-rooted: it leaves an attached
    /// host and lands in the always-attached lot, then back).
    /// </summary>
    [AvaloniaFact]
    public void ParkThenMount_MovesTheSurfaceAndReportsParkedAlive()
    {
        var (window, parkingLot, host, surface) = BuildShownTree();
        var coordinator = new GraphSurfaceCoordinator(parkingLot);

        // Seeded under the host (the "currently mounted" start state).
        host.Content = surface;
        ForceLayoutPass(window);
        Assert.Same(host, surface.Parent);

        // Park: GraphHost → parking lot. The host's Content is cleared, the surface joins the lot.
        coordinator.Park(surface);
        ForceLayoutPass(window);
        Assert.Same(parkingLot, surface.Parent);
        Assert.Contains(surface, parkingLot.Children);
        Assert.Null(host.Content);

        // Mount back: parking lot → GraphHost, reporting parked-and-alive (it came from the lot).
        var wasAliveParked = coordinator.Mount(surface, host);
        ForceLayoutPass(window);
        Assert.True(wasAliveParked, "a surface mounted FROM the parking lot is parked-and-alive");
        Assert.Same(host, surface.Parent);
        Assert.Same(surface, host.Content);
        Assert.DoesNotContain(surface, parkingLot.Children);

        window.Close();
    }

    /// <summary>
    /// A fresh, never-parked surface mounted straight into the GraphHost reports
    /// <c>wasAliveParked == false</c> — the caller must then fall back to the ADR-024 re-render. The
    /// surface still ends up under the host (the move happens regardless of the verdict).
    /// </summary>
    [AvaloniaFact]
    public void Mount_AFreshNeverParkedSurface_ReportsNotParked()
    {
        var (window, parkingLot, host, surface) = BuildShownTree();
        var coordinator = new GraphSurfaceCoordinator(parkingLot);

        // The surface has no parent at all (never mounted, never parked).
        Assert.Null(surface.Parent);

        var wasAliveParked = coordinator.Mount(surface, host);
        ForceLayoutPass(window);

        Assert.False(wasAliveParked, "a never-parked surface is NOT parked-and-alive (re-render fallback)");
        Assert.Same(host, surface.Parent);
        Assert.Same(surface, host.Content);

        window.Close();
    }

    /// <summary>
    /// A surface already mounted in a host (NOT in the parking lot), re-mounted into a SECOND host,
    /// reports <c>wasAliveParked == false</c> — it was reclaimed from a stale host, not the lot — and
    /// the move still leaves it under the new host (the stale host is cleared first, so no
    /// double-parent throw on the layout pass). This is the "reclaimed from elsewhere" arm.
    /// </summary>
    [AvaloniaFact]
    public void Mount_FromAStaleHostNotTheLot_ReportsNotParked_AndReParentsCleanly()
    {
        var (window, parkingLot, host, surface) = BuildShownTree();
        // A second mounted host, sibling to the first in the same fill Panel.
        var secondHost = new ContentControl { Name = "GraphHost" };
        ((Panel)host.Parent!).Children.Add(secondHost);
        var coordinator = new GraphSurfaceCoordinator(parkingLot);

        host.Content = surface;
        ForceLayoutPass(window);
        Assert.Same(host, surface.Parent);

        // Re-mount into the second host straight from the first (a stale host, not the lot).
        var wasAliveParked = coordinator.Mount(surface, secondHost);
        var crash = Record.Exception(() => ForceLayoutPass(window));

        Assert.Null(crash); // the stale host was cleared first → no "already has a visual parent" throw
        Assert.False(wasAliveParked, "reclaimed from a stale host is NOT parked-and-alive");
        Assert.Same(secondHost, surface.Parent);
        Assert.Same(surface, secondHost.Content);
        Assert.Null(host.Content);

        window.Close();
    }

    /// <summary>
    /// <c>Park</c> twice in a row is idempotent: the second call sees the surface already in the lot
    /// and is a no-op (no duplicate child, no throw). The reflexive guard the shell relies on when it
    /// parks a surface that was already parked on the prior hop (Workspace stays parked across
    /// Design-plan then Gap).
    /// </summary>
    [AvaloniaFact]
    public void Park_WhenAlreadyParked_IsANoOp()
    {
        var (window, parkingLot, host, surface) = BuildShownTree();
        var coordinator = new GraphSurfaceCoordinator(parkingLot);

        host.Content = surface;
        ForceLayoutPass(window);

        coordinator.Park(surface);
        ForceLayoutPass(window);
        Assert.Single(parkingLot.Children);

        coordinator.Park(surface); // already parked → no-op
        ForceLayoutPass(window);
        Assert.Single(parkingLot.Children);
        Assert.Same(parkingLot, surface.Parent);

        window.Close();
    }

    /// <summary>
    /// Mounting a surface that is ALREADY the GraphHost's content reports <c>true</c> and leaves it
    /// in place (the idempotent "already where it belongs" arm the returning view hits when its own
    /// DataContextChanged mount already ran). No throw, no duplicate move.
    /// </summary>
    [AvaloniaFact]
    public void Mount_WhenAlreadyInThatHost_ReportsTrue_AndIsANoOp()
    {
        var (window, parkingLot, host, surface) = BuildShownTree();
        var coordinator = new GraphSurfaceCoordinator(parkingLot);

        host.Content = surface;
        ForceLayoutPass(window);

        var wasAliveParked = coordinator.Mount(surface, host);
        ForceLayoutPass(window);

        Assert.True(wasAliveParked, "already in the target host counts as alive-in-place (skip re-render)");
        Assert.Same(surface, host.Content);
        Assert.Same(host, surface.Parent);

        window.Close();
    }

    // === helpers ===============================================================================

    /// <summary>A SHOWN headless window holding a fill Panel with a GraphHost
    /// <see cref="ContentControl"/> and a sibling parking <see cref="Panel"/>, plus a free
    /// <see cref="Border"/> surface — the minimal real tree the coordinator reparents within (mirrors
    /// MainWindow's "step ContentControl over a sibling ParkingLot Panel" shape). Shown so reparents
    /// are followed by a real layout pass.</summary>
    private static (Window Window, Panel ParkingLot, ContentControl Host, Border Surface) BuildShownTree()
    {
        var host = new ContentControl { Name = "GraphHost" };
        var parkingLot = new Panel { Name = "ParkingLot", IsVisible = false, Width = 0, Height = 0 };
        var fill = new Panel();
        fill.Children.Add(host);
        fill.Children.Add(parkingLot);

        var window = new Window { Content = fill, Width = 400, Height = 400 };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        return (window, parkingLot, host, new Border());
    }

    /// <summary>Forces the headless compositor through a real layout/render pass (capture-and-discard
    /// + pump) — identical to <c>BackNavigationStepSwapTests.ForceLayoutPass</c>, the pass where a
    /// double-parent reparent would throw from <c>ContentPresenter.UpdateChild</c> during measure.</summary>
    private static void ForceLayoutPass(Window window)
    {
        Dispatcher.UIThread.RunJobs();
        window.CaptureRenderedFrame()?.Dispose();
        Dispatcher.UIThread.RunJobs();
    }
}
