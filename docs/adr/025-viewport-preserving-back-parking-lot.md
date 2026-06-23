# ADR-025: Viewport-preserving Back navigation (parking-lot host)

**Status:** Accepted · **Date:** 2026-06-23
**Refines:** ADR-024 (re-render-on-reattach is **demoted to the fallback**) · ADR-001 (the graph is a native child HWND — airspace guardrail 5: a hidden-but-attached HWND paints over nothing) · ADR-003 (D5 shell step machine — one `ContentControl`, one DataTemplate per step) · ADR-004 (D5/D6 the `IGraphRenderer` seam) · **Fixes:** #122 · **Phase:** 4 (feedback-driven)

## Context

ADR-024 fixed the Back-navigation crash by releasing the shared `NativeWebView` on the leaving view's `DetachedFromVisualTree` and recovering the recreated-blank control by re-navigate+replay. That is correct but **re-fits** the graph on every Back (a brief reload, viewport lost) — `NativeControlHost` destroys the WebView2 child on detach and rebuilds it blank on re-attach (#122).

A throwaway windowed spike (`spikes/ParkSpike/`, `RESULTS-parking.md`) settled the open question (#122 risk #1): a `NativeWebView` keeps its **live page + cytoscape viewport (zoom/pan) AND the same `Chrome_RenderWidgetHostHWND`** across a park→dwell→unpark, *provided it is never un-rooted* — moved only between two attached parents. The negative control (un-root for the dwell, the ADR-024 `GraphHost.Content = null`) destroyed+recreated the child blank and lost all state, proving the measurement. Two further findings: `IsVisible=false` **hides the native HWND without detaching it** (airspace is satisfiable by hiding, not removing — risk #3), and survival comes from **continuous rootedness**, not `BeginReparenting` per se (the prior `BeginReparenting`-only attempt failed precisely because the crash fix un-roots — #122 background).

## Decision

1. **Never un-root the graph surface; park it.** A single always-attached, hidden (`IsVisible=false`, zero-size) `ParkingLot` `Panel` lives in `MainWindow` as a sibling of the step `ContentControl` (a `Panel`, so Workspace **and** the current Plan can be parked at once). A leaving step's surface is moved into it instead of being released.

2. **A window-scoped `IGraphSurfaceCoordinator` performs the moves.** `Park(view)` and `Mount(view, graphHost)` each do **one synchronous reparent** (detach-from-old + attach-to-new). For a `NativeWebView` the pair is wrapped in `BeginReparenting(true)` — the atomic native reparent, never a transient un-root; any other control (a headless test surface) takes the plain swap (spike-proven equivalent for a page-less control). `Mount` returns `wasAliveParked` so a parked-and-alive remount **skips** the re-render. The coordinator is built in `MainWindow.OnOpened` over the `ParkingLot` panel and pushed into the shell and the graph step VMs via the **same MainWindow→VM seam as `IExportFileDialogs`** — no view→shell→MainWindow back-channel (MVVM-clean).

3. **The load-bearing ordering invariant.** `ShellViewModel` parks the step it will Back *into* (`OnDesignPlan` parks Workspace; `OnGapAnalysis` parks Plan) **synchronously, immediately before `CurrentStep` is reassigned** — i.e. before the leaving view detaches. The views' `DetachedFromVisualTree` releases `GraphHost.Content` **only if the surface is still their child** (`ReferenceEquals`): a parked surface was already moved out → the detach is a no-op → the live control is not torn down. Reverse the order and the detach guard would un-root the live surface and reproduce the spike's negative-control page-death.

4. **Renderer `IDisposable` + bounded surfaces.** `IGraphRenderer` is now `IDisposable`; `CytoscapeGraphRenderer.Dispose` cancels in-flight work, unsubscribes its handlers, releases the native control, and refuses to recreate it afterward — retiring the pre-existing never-disposed-renderer leak (ADR-024 Consequences), now mandatory because abandoned surfaces must free their WebView. The shell reclaims abandoned surfaces (Gap on Back, a superseded Plan) via `DisposeAndUntrack`, so at most **Workspace + the current Plan** are parked at once.

5. **Re-render-on-reattach (ADR-024 D2) is demoted to the fallback** for any non-parked surface: no coordinator (headless / no window), a reclaimed surface re-entered fresh, or the WebView2-missing path. Defense in depth — the crash fix and its recovery are fully retained.

## Consequences

- **Back preserves the exact viewport** (zoom/pan), same WebView2 child HWND, no reload flash, for Plan→Back→Workspace and Gap→Back→Plan. The ADR-024 crash fix and its re-render recovery remain intact as the fallback.
- **The never-disposed-renderer leak is retired**; live WebViews are bounded (≤ Workspace + current Plan parked, plus the displayed one).
- **Airspace untouched** (ADR-001 guardrail 5): parked HWNDs are hidden, never drawn over chrome.
- **The ordering invariant is the regression surface.** Pinned headlessly (`Mount` returns `wasAliveParked == true` on a parked Back; same surface instance re-mounted; bounded parked count; abandoned renderers disposed) and in the windowed `tools/smoke-back-nav.ps1` (same `Chrome_RenderWidgetHostHWND` + same `cy.zoom()` across Back — the only tier with a real WebView2).
- **Headless tests** use a real `Border` surface: the coordinator's `BeginReparenting` branch is skipped (it is `NativeWebView`-only) and the plain-swap path runs, which the spike proved equivalent for a page-less control — so the parking/reclaim logic is CI-testable without a real WebView2.

## Alternatives considered

- **`BeginReparenting()` across an un-rooted hold** (the prior attempt, #122 background). Rejected: the page dies during the dwell — `BeginReparenting` is a synchronous same-frame reparent, not an across-step hold. Parking keeps the control continuously rooted, which is the actual requirement.
- **Plain reparent without `BeginReparenting`.** The spike showed it also preserves the page for an attached→attached move, but `BeginReparenting(true)` is kept as the safer atomic move (no transient un-root window between detach and attach, even within one synchronous block).
- **Keep all step views mounted, toggle visibility** (ADR-024 alternative). Rejected again: a larger restructure of the DataTemplate-driven step machine; the parking lot achieves continuous rootedness for just the graph surface with far less change.
