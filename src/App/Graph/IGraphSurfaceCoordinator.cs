using Avalonia.Controls;

namespace GroupWeaver.App.Graph;

/// <summary>
/// Moves a step's live graph surface (the renderer's single <see cref="IGraphRenderer.View"/>
/// control) between its step-view <c>GraphHost</c> and a hidden, always-attached "parking lot"
/// host, as ONE synchronous reparent hop that NEVER un-roots the control (#122 / ADR-025). The
/// ParkSpike GO finding: a continuously-rooted <c>NativeWebView</c> keeps its live WebView2 page
/// + cytoscape viewport (and the same native child HWND) across a park→dwell→unpark cycle, while
/// an un-root loses them — so the shell parks the Back-target surface BEFORE the DataTemplate swap
/// detaches the leaving view, and the returning view re-mounts the SAME live control.
///
/// <para>The shell (<see cref="ViewModels.ShellViewModel"/>) parks; the step views
/// (Workspace/Plan/Gap) mount. One coordinator per window, built in <c>MainWindow.OnOpened</c>
/// over the XAML parking <c>Panel</c> and pushed into the shell + the graph-bearing step VMs
/// exactly as <c>IExportFileDialogs</c> is wired (no view→shell→MainWindow back-channel).
/// UI-thread-only by contract.</para>
/// </summary>
public interface IGraphSurfaceCoordinator
{
    /// <summary>Moves <paramref name="view"/> out of its current host (a <see cref="ContentControl"/>
    /// → <c>Content = null</c>, or a <see cref="Panel"/> → <c>Children.Remove</c>) INTO the parking
    /// lot, as ONE synchronous hop — the control stays rooted the whole time (it leaves an attached
    /// host and lands in the always-attached parking <see cref="Panel"/>). A no-op if it is already
    /// parked. The shell calls this for the Back-target surface BEFORE reassigning
    /// <c>CurrentStep</c>.</summary>
    void Park(Control view);

    /// <summary>Moves <paramref name="view"/> from wherever it lives (the parking lot, a stale host,
    /// or nowhere) INTO <paramref name="graphHost"/>'s <c>Content</c>, as ONE synchronous hop.
    /// Returns <c>true</c> iff the view was ALREADY parked-and-alive — i.e. the live page survived
    /// and the caller can SKIP the ADR-024 re-render fallback (the returning view shows the
    /// preserved viewport); <c>false</c> when it had to be reclaimed from elsewhere (then the
    /// renderer's own re-attach replay restores the graph as before).</summary>
    bool Mount(Control view, ContentControl graphHost);
}
