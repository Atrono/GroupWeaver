# ADR-018: Graph interaction feedback — selection, hover, neighborhood dim, selective labels

**Status:** Accepted · **Date:** 2026-06-21
**Refines:** ADR-004 (the label-zoom gate) · ADR-010 (severity overlay) · ADR-015 (diff underlay) · ADR-017 (element `opacity` = enter fade) · **Decides:** issue #89 (F3 + F9; reverse sidebar→graph select-sync split to a follow-up) · **Phase:** 4 (v0.2 polish pass)

## Context

The v0.2 polish audit (F3/F9) found the graph gives **zero feedback** on tap or hover — clicking a node updates the native detail panel but the canvas never acknowledges the selection, and at fit zoom every node is an unlabeled dot. The fix must compose with an already-crowded visual-channel map: element `opacity` is now the #88 enter-fade (ADR-017), `overlay-*` is severity (ADR-010), `underlay-*`/`background-opacity` is diff (ADR-015), `border-color` is root/External, and fill/shape is kind. A design workflow's adversarial critique caught two traps before any edit: `background-brighten` is **not a cytoscape property** (only `background-blacken`, negative = brighter), and synthetic headless `emit('tap')` does **not** run cytoscape's native select.

## Decision

1. **Interaction-feedback channel ownership (the durable contract — future cues must claim a still-free channel):**
   - **Neighborhood dim** (`node.gw-dim`): `background-blacken: 0.6` + `text-opacity: 0.15`. `background-blacken` darkens **only the kind fill**, so a dimmed node's severity `overlay-*` halo and diff `underlay-*` stay at **full strength** (an Error halo is never hidden by dimming — same pattern ADR-015 uses for diff='removed' fading the fill while keeping the underlay). It never touches element `opacity`, so the #88 enter-fade is uncorrupted.
   - **Selection** (`node:selected`): white `border-color #FFFFFF` + `border-width 3` + `z-index 10` + `min-zoomed-font-size 0` (forced label). Rides border + z only.
   - **Hover** (`node.gw-hover`): `background-blacken: -0.15` (brightens) + `border-opacity 1`.
   - **Edge dim** (`edge.gw-dim`): `opacity 0.12`, applied transiently only during a live selection, restored on clear.
   - Source order (last wins): `… , node.gw-dim, node.gw-hover, node:selected`. `gw-hover` after `gw-dim` so hovering a dimmed node brightens it (desired feedback on the shared `background-blacken` channel); `:selected` last so the selection border always wins.
2. **Selection is INSTANT** — `addClass`/`removeClass` toggles, never `cy.animate`/collection `.animate`, so it never touches the #88 isolated motion counters (`__gwAnimateCalls`/`__gwEnterAnims`). Only **hover** may carry a short style `transition`, gated on the module-scoped `reduceMotion` boolean (read once at IIFE init); reduced-motion collapses it to an instant flip.
3. **nodeClick contract unchanged.** The bridge `nodeClick` send stays the **unconditional first statement** of the node-tap handler (keyed off `tap`, never off cytoscape `select`/`unselect`). `applySelection(node)` then **explicitly** calls `node.select()` (synthetic `emit('tap')` won't), clears prior `gw-dim`/`gw-hover`, dims `cy.elements()`, and un-dims `node.closedNeighborhood()`. A new core-level background-tap handler (`evt.target === cy`) clears selection + dim. Selection visuals are **graph→VM only**; the reverse **sidebar→graph `:selected` sync is split to a follow-up** (it needs a VM→JS `select` command touching the pinned jump/selection-sync C# tests).
4. **Selective always-labels (F9):** add `min-zoomed-font-size: 0` to the existing `node[?root]` and `node[sev='error']` rules so the root and Error-severity nodes are labeled at fit zoom; the base `node` floor stays `10`. **Only root + Error** (never warning/info) — keeps the overview legible and honors the AP 3.4 max-severity-always-on mandate. This elevates ADR-004's label gate from "hidden until zoomed" to "hidden at fit **except** root and Error."

## Consequences

- The graph gains recognition feedback (selection, hover, neighborhood focus) and an orientable overview (root + Error labeled). All cues use **brightness/border/z**, never hue — composing with severity, diff, and the enter-fade without collision.
- The **channel-ownership map is now documented**; any future graph cue must claim a still-free channel (the critique caught a collision precisely because the map was implicit).
- The **reverse sidebar→graph selection sync** is deferred to its own issue (C# VM change).
- `docs/ui-checklist.md` section A: the label-gate item is elevated to exempt root + Error; new selection/hover/dim items added. `verify.mjs` gains a `SELECTION` constant block + selection/dim/hover/selective-label asserts + a counters-untouched guard. The C# `Graph_HidesLabelsAtFitZoom` substring check is unaffected.
