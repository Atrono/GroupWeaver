# ADR-024: Graph-surface lifecycle across shell step swaps (back-navigation crash)

**Status:** Accepted · **Date:** 2026-06-23
**Refines:** ADR-001 (the graph is a native child HWND — airspace guardrail 5) · ADR-003 (D5 shell step state machine — one `ContentControl`, one DataTemplate per step) · ADR-004 (D5/D6 the `IGraphRenderer` seam, lazy `View`, navigate-on-attach) · **Triggered by:** ADR-014 (Plan Mode) + ADR-015 (Gap analysis), which first introduced step↔step swapping · **Fixes:** #120 · **Phase:** 4 (feedback-driven)

## Context

A user reported the app **crashes when pressing Back on some screens**. Reproduced deterministically (`tools/smoke-back-nav.ps1`): pressing Back from a graph-bearing step (Plan → Back to Workspace; Gap → Back to Plan) throws, while RootPicker → Back to Connect (no graph) is fine — hence "*some* screens."

Root cause: the shell renders one step through a single `ContentControl` (`MainWindow.axaml`). Workspace/Plan/Gap each own a `CytoscapeGraphRenderer` whose **single** `NativeWebView` is mounted into that step view's `GraphHost` (`*View.axaml.cs` `DataContextChanged`: `GraphHost.Content = renderer.View`). The OLD view was discarded on a swap but **never released** the control, so on Back the SAME `WorkspaceViewModel` materialised a NEW view whose `GraphHost.Content = renderer.View` re-added a control that still had the old `GraphHost` as its parent:

```
System.InvalidOperationException: The control NativeWebView already has a visual parent
ContentPresenter (… GraphHost) while trying to add it as a child of ContentPresenter (… GraphHost).
   at Avalonia.Controls.Presenters.ContentPresenter.UpdateChild(…)   // thrown from the measure pass after the swap
```

This is a **pure Avalonia visual-tree conflict**, not a WebView2 fault — so it is reproducible under Avalonia.Headless with any real `Control` as the renderer's `View`. There is a **secondary** WebView2 issue: `NativeWebView` is a `NativeControlHost`, which **destroys** its native child on detach and **recreates** it blank on re-attach; the renderer's `NavigateOnce` navigated only on the first attach (`_navigated` guard), so even once the crash is fixed the graph would come back **blank**.

Why the suite missed it: all back-nav tests are VM-level against a `FakeGraphRenderer` whose `View` is `null` (so the real views never mount anything and never re-parent); headless Avalonia cannot host a real `NativeWebView` (ADR-001 guardrail 6); the one tier with a real WebView — the windowed `--demo` smoke — only ever drove the forward path, never Back into a graph step.

## Decision

1. **Views release the shared control on leave (the crash fix).** Each of `WorkspaceView`/`PlanView`/`GapView` clears `GraphHost.Content = null` on `DetachedFromVisualTree`, so a discarded view frees the singleton control when it leaves the tree; by the time Back materialises the next view, the control is parentless and mounts cleanly. The mount also defensively detaches the control from any stale `ContentControl` host before assigning (belt-and-suspenders). The renderer keeps the control object alive — only the parenting is freed.

2. **The renderer recovers the recreated-blank control on re-attach (the graph-returns fix).** `CytoscapeGraphRenderer` caches the chunk commands of the last successful base render (`ShowGraph`/`UpdateGraph`/`ShowDiffGraph` — not transient focus/select/busy). On a re-attach (already navigated once → the native child was destroyed and recreated blank) it re-navigates `index.html` and replays the cached chunks once the page is ready (`ReNavigateAndReplayAsync`). The ready gate (`_ready`) is reset to a fresh, uncompleted source **immediately before** the re-navigate — a detach-time-only reset proved unreliable in the smoke (a stale-completed `_ready` let `InvokeScript` run "before any page was loaded"). Cost: Back **re-renders** the graph (a brief reload, viewport re-fit) rather than preserving the live viewport — accepted for a crash fix; seamless viewport preservation is a possible later enhancement (see Alternatives).

3. **The renderer never throws onto an awaited/async-void caller (unconditional safety net).** The base-render/focus/export command paths wrap their dispatch+confirm so any non-own-cancellation `InvokeScript` fault becomes a `RendererError` + normal return — extending ADR-004 D5's "degraded renderer is an inline error, never a crash" from the timeout path to the fault path. The single-flight guard (ADR-005 D3) and the 60 s bounded-wait policy are untouched (the catch sits inside the single-flight `try`, never around it).

## Consequences

- **Crash gone; graph returns on Back** (re-fit, brief reload — viewport not preserved). Verified end-to-end by `tools/smoke-back-nav.ps1` (Plan and Gap round-trips: process stays alive + the graph re-renders).
- **The regression is now CI-testable.** Because the crash is an Avalonia parent conflict (not WebView2-specific), a headless test with a fake renderer that returns a real `Control` (opt-in `FakeGraphRenderer.WithRealSurface()`, default behaviour unchanged) drives the live shell + real views through Back and the Gap round-trip and asserts no throw + correct re-parenting — RED before this change, GREEN after. The WebView2-only re-render is covered by the windowed smoke (DoD step 2). VM never-throw pins cover the faulted-renderer paths.
- **Invariant for future steps:** any future step that hosts the graph surface MUST release it on detach (D1) — the shared native control can have exactly one parent. Documented at the mount/detach sites.
- **No new leak class.** Renderers are still never disposed (pre-existing — the native control already lived until process exit); this change does not extend that.
- **Airspace + graph layout untouched** (ADR-001/004/017/018/020): container-level lifecycle only.

## Alternatives considered

- **`NativeWebView.BeginReparenting()` to preserve the live page across the swap** (no reload, viewport kept). Rejected for this fix: it requires opening the scope BEFORE the detach (shell coordination) and holding it across an arbitrarily long detach gap (the user dwelling in another step) — undocumented/unvalidated across-turn behaviour — and it still needs D1's parent release. Re-render-on-reattach is self-contained and robust. Kept as the candidate for a future viewport-preserving enhancement.
- **Keep all step views mounted, toggle visibility** (so the native child never detaches). Rejected: a larger restructure of the DataTemplate-driven step machine for a crash fix; revisit only if viewport preservation becomes a priority.
