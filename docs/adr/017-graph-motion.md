# ADR-017: Graph-layer motion — new-node enter fade + eased focus-fit

**Status:** Accepted · **Date:** 2026-06-21
**Refines:** ADR-005 (which explicitly deferred "animation concerns") · ADR-004 D1/D5 (render pipeline) · ADR-010 D1 (severity overlay) · **Decides:** issue #88 (F1 + F2; F12 split to a follow-up) · **Phase:** 4 (v0.2 polish pass)

## Context

ADR-005 deferred animation. The v0.2 polish audit (`docs/superpowers/specs/2026-06-21-polish-audit.md`) found the two highest-leverage feel gaps both live here: lazy-expand **snaps** (`updateGraph` does `cy.batch` remove-all/add-all, new nodes appear instantly) and focus/jump-to-node **cuts** the camera (`focusOn` calls synchronous `cy.fit`). The interaction that *is* the auditing workflow has zero motion. This ADR reintroduces the animation ADR-005 deferred, scoped narrowly so the replace-in-place contract is untouched.

A design workflow (5 parallel code/contract reads → synthesis → adversarial critique) grounded the change and corrected three traps: cytoscape **element `opacity` composites the whole node including its overlay/underlay layers**; ADR-005's full-layout-recompute means **survivors already move on every expand**; and a fire-and-forget busy signal (F12) needs an `IGraphRenderer` interface change and would contend the shared single-flight. F12 is therefore split out; this ADR is F1 + F2.

## Decision

1. **Eased focus-fit (F2) is the only camera animation.** `focusOn` animates the viewport: `cy.stop(); cy.animate({ fit: { eles: col, padding: 80 } }, { duration: 280, easing: 'ease-out-cubic', complete: confirmFocus })`. The `focused` bridge confirmation moves from `cy.one('render', …)` (fires on the first frame, before the ease settles) to the animation **`complete`** callback — still exactly once per command, ~280 ms vs. the 60 s `BridgeTimeout`, so C# `FocusAsync` is unaffected (no C# change). `cy.stop()` coalesces a focus arriving mid-ease so a superseded animation never strands its `complete`.

2. **New-node enter fade (F1).** `updateGraph` captures the **pre-removal** live node-id set, runs the unchanged `cy.batch` remove-all/add-all, then fades **only genuinely-new** nodes (`incoming id ∉ pre-removal set`) via the element **`opacity`** channel 0→1 (240 ms, ease-out-cubic). Survivors are replaced instantly (no tween), exactly as today. No `cy.fit`/`pan`/`zoom`, no position tween. `sendLoaded` stays on the **first** post-batch render so the `loaded` counts/viewport asserts read the settled set independent of the tween.

3. **Element-opacity semantics — accepted.** Because element `opacity` composites a node *with* its own overlay (severity halo) and underlay (diff) layers, a node that is **both new and flagged/diffed** fades in *together with* its halo — correct, that halo belongs to that node. The invariant is therefore "the enter tween never touches overlay/underlay opacity **on survivors**"; survivor halos/underlays are never animated. Tests sample **survivor** ids only for the overlay/underlay-0 checks (never a new node mid-tween).

4. **Survivor-reposition jump — accepted, pre-existing.** ADR-005's global-ring relayout gives survivors new preset positions every expand; they snap there instantly today. F1 does **not** change this and does **not** animate positions (a position/layout tween is O(V+E)/frame and judders on the software-rendering floor — out of scope). Smoothing the survivor jump (position-stable layout or eased reposition) is a possible future ADR-005 revision, not part of #88.

5. **Reduced-motion mandate.** `window.matchMedia('(prefers-reduced-motion: reduce)').matches`, read once at IIFE init, degrades **both** behaviors to the instant pre-slice paths (synchronous `cy.fit` + `cy.one('render', confirmFocus)` for focus; full-opacity add for update) — no `cy.animate`, no opacity tween. The empty/un-pannable collection also takes the instant path (guarded **before** the reduced-motion branch) so every focus posts `focused` exactly once. The Playwright `emulateMedia({ reducedMotion: 'reduce' })` probe pins it. No bridge-flag channel (it would be an untested code path).

6. **Perf floor.** Only the element-opacity fade (F1, bounded to the revealed cluster — tens of nodes) and the single viewport matrix (F2) animate per frame; never layout/position. `textureOnViewport`/`hideEdgesOnViewport` stay ON (they engage only on the camera ease — momentary blur + dropped edges during the focus beat are the optimization working, not a regression); `motionBlur` stays OFF. Perf claims state the rendering mode (GPU vs software, `lab-environment.md`).

## Consequences

- The core auditing interaction gains motion; focus reads as navigation, not teleport. The **survivor-reposition jump remains** (documented, pre-existing).
- **F12 (in-canvas busy ring) is split to a follow-up:** it needs `IGraphRenderer.SetBusyAsync` as a default no-op method (so `FakeGraphRenderer` and every impl compile), dispatched **without** `EnterSingleFlight`/`_commandInFlight` (else it deadlocks the in-flight expand), recording on its own channel (never `FocusCalls`).
- `docs/ui-checklist.md` section A lazy-expand + drag/zoom items are elevated with the motion expectation; `verify.mjs` gains isolated camera (`__gwAnimateCalls`) and enter (`__gwEnterAnims`) counters plus a standalone reduced-motion probe. All ADR-005 pins (1e-9 viewport/position, counts, dropped-edge, severity-survival) stay green.
