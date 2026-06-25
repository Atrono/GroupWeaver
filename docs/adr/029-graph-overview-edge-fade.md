# ADR-029: Graph overview edge-fade — fade plain edges at fit zoom, restore on zoom-in

**Status:** Accepted · **Date:** 2026-06-25
**Refines:** ADR-004 (preset concentric layout + the bezier edge language) · ADR-010/015 (severity overlay / diff underlay channels) · ADR-017/018 (#88 isolated-motion counters, instant class toggles) · ADR-023 (web-layer in-graph controls) · **Decides:** the v0.4.2 senior-review polish WP-A (#176) · **Phase:** 4 (feedback-driven)

## Context

The default fit/overview render of an explore graph is a dense, full-opacity **edge
mesh** — at the demo scale (196 nodes / 334 edges) a legible-but-busy cobweb, and on a
real directory a true hairball. At overview zoom no *individual* edge is traceable
anyway (they cross, overlap, and converge on the high-degree rings), so the mesh at that
zoom is **pure visual noise**: it buries the one thing the overview *should* communicate
— the shaped-node constellation (which kinds sit where, which clusters are dense, where
the flagged nodes are). This view is also the product's **first impression** and the
README hero image (`docs/media/graph-explore.png`).

The shipped app already ships strong *opt-in* mitigations — Issues-only filter (#142),
Find / command palette (ADR-023), neighbourhood dim on selection (ADR-018), the minimap
(#146) — but the **default first paint** receives none of them. The weakest frame in the
product is the one every new user and every README reader sees first.

Two hard constraints bind any fix:
- **The software-rendering floor (ADR-001).** The target audience runs on RDP / server /
  GPU-less VMs; the 5000-node/6499-edge spike (`spikes/GraphSpike/RESULTS-software-
  rendering.md`) is the perf bar. A per-frame restyle of every edge is off the table.
- **The #88 isolated-motion contract (ADR-017/018).** Selection and its kin are *instant
  class toggles*, never `cy.animate`; `verify.mjs` pins the `__gwAnimateCalls` /
  `__gwEnterAnims` counters at **0** across those paths. Any new resting-state treatment
  must not animate.

## Decision

### D1 — Fade plain edges at overview zoom; restore them on zoom-in.

When an explore graph sits at/near its fit zoom, every edge fades to a faint wash
(`opacity 0.15`); as the user zooms in to inspect, edges return to full opacity. The
**node** layer (kind fill + shape, severity halo, accent ring, labels) is untouched —
only edges fade, so the constellation reads while the cobweb recedes. The faded wash
keeps just enough edge presence to suggest structure (you can still see *that* the centre
is densely connected) without asserting traceable individual links you cannot follow at
that zoom anyway.

### D2 — Binary toggle with hysteresis, never a per-frame opacity ramp.

A continuous opacity-vs-zoom interpolation (prettier in principle) is **rejected**: it
requires restyling every edge on every zoom frame, which breaks the software-rendering
floor at the 5–6k-edge spike. Instead the fade is a single CSS-class toggle
(`edge.gw-edge-faded { opacity: 0.15 }`) flipped on a **threshold crossing** only:

- `fitZoom` is captured once per full build, right after `cy.fit()` — the
  **size-independent overview baseline** (so the rule auto-scales to any graph; no magic
  absolute zoom).
- Edges fade while `cy.zoom() <= fitZoom * EDGE_FADE_FACTOR` (`1.6`, tunable). The factor
  is small so edges restore quickly once the user starts zooming in to inspect.
- **Hysteresis:** the per-frame `zoom` handler (`onZoomFade → updateEdgeFade(false)`)
  costs one read + one compare; the `cy.batch` `addClass`/`removeClass` runs **only** when
  the boolean state actually flips. The per-build init and every `graphUpdate` re-assert
  with `updateEdgeFade(true)` so freshly-added edges (lazy expand) match the current
  state.

The toggle carries **no transition**, so it is instant and the #88 motion counters stay
0 — the fade is a state change, not an animation, and needs no reduced-motion branch
(ADR-017 is about `cy.animate` / opacity tweens, neither of which this uses).

### D3 — The gap/diff view is excluded wholesale.

In a gap build the Added / Removed / Unchecked edges **are** the signal, never noise — so
fade is off entirely whenever the graph carries any diff-tagged edge (`isDiffGraph`,
computed once per build via `cy.edges('[?diff]')`). This is checked as an O(1) module
boolean inside `updateEdgeFade`, never as a per-frame selector scan. Style-channel
composition is belt-and-braces here: the `edge.gw-edge-faded` rule is placed in
`buildStyle()` **before** the `edge[diff=…]` rules and the `edge.gw-dim` rule, so even if
the class were ever present on a diffed or selection-dimmed edge those later rules win the
opacity channel (diff keeps its pinned opacity; a dimmed edge keeps `0.12`). The fade
therefore governs only an **undiffed, undimmed** edge at overview zoom.

### D4 — No new themed token; no parity-mirror entry.

`EDGE_FADE_FACTOR` and the `0.15` wash are **behaviour constants in `graph.js` only**
(like `EDGE_HIDE_THRESHOLD`), theme-invariant — they are not colours, so they do **not**
join the hand-mirrored `BrandTokens` ⇄ `THEME`/`CHROME` ⇄ `index.html` ⇄ `verify.mjs`
palette parity set (unlike ADR-027's accent token). They are pinned only by the
`verify.mjs` behavioural assertion (D5).

### D5 — Pinned in verify.mjs; a new ui-checklist criterion.

`tests/graph-bundle/verify.mjs` pins the behaviour: at the overview frame a plain
membership edge and a contains edge render `opacity ≈ 0.15`; after zooming in past
`fitZoom * EDGE_FADE_FACTOR` the same edges render `opacity 1`; and on the diff frame no
edge carries `gw-edge-faded` while the diff edges keep their pinned diff opacities. The
overview frame (`graph-overview.png`) — the source the README hero is copied from — now
renders the faded constellation. `docs/ui-checklist.md` gains a matching `[P]` criterion.

## Consequences

- **The explore hero stops being a hairball.** `docs/media/graph-explore.png` is
  re-rendered from the new overview frame: a clean shaped-node constellation with faint
  structural edges, not a full-opacity cobweb. The first impression now sells the product.
- **Dark/light parity is unaffected** — fade is an opacity-only behaviour, identical in
  both themes; no theme table changes.
- **One pre-existing pin moves:** any `verify.mjs` assertion that pinned a plain edge's
  opacity to `1` *at the overview frame* now expects the faded value at overview and full
  opacity zoomed-in (the value is unchanged in graph.js' resting `buildStyle`; only the
  rendered overview state changed). This is a deliberate, reviewed test update, not a
  weakening.
- **Selection at overview zoom** shares the faded edge treatment (the neighbourhood edges
  are faint too); the node-level emphasis (un-dimmed neighbour nodes, accent ring, white
  border) still carries the selection, and the user zooms in to trace links. Accepted for
  v0.4.2; a future pass could exempt the selected neighbourhood from fade if review finds
  it wanting — it would be an additive `updateEdgeFade` branch, not a contract change.
- **No motion-counter impact** (#88): the fade is an instant class toggle, so the
  ADR-017/018 `__gwAnimateCalls` pins hold unchanged.
