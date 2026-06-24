# ADR-027: Graph node-language reconciliation — keep per-kind shapes, add a selection accent halo/pulse

**Status:** Accepted · **Date:** 2026-06-24
**Refines:** ADR-004 (the kind palette + per-kind shapes + web-bundle parity) · ADR-010 (severity overlay halo) · ADR-015 (diff underlay) · ADR-018 (#89 selection + neighborhood dim) · ADR-021 (one-palette-source / role tokens) · ADR-026 (light/dark theme + the WP2 brand accent) · **Decides:** the WP3 work package of the 2026-06 UX redesign (#140) · **Phase:** 4 (feedback-driven)

## Context

The imported Claude Design project *"GroupWeaver UX Redesign"* draws its graph nodes with a
**single circle geometry per node**, distinguishing group scope by a coloured **ring hue**
(GG blue, DL teal, …) and stamping a small **per-node corner badge** for audit state
(Error/Warning/Info). Its selected/root node carries an **accent glow ring with a gentle
pulse**. The thesis under the mockup is *"type readable before the label"* — the kind should
be legible at a glance, before you read the node's text.

The shipped app already satisfies that thesis by a **different, stronger** mechanism: a
distinct **shape per kind** (GG triangle / DL diamond / UG pentagon / OU round-rect /
User + External ellipse / Computer rectangle), pinned by `verify.mjs`'s `PALETTE` /
`KIND_BORDER` blocks and `WebBundleTests`. Shape is a **colourblind-redundant** channel
(WCAG 1.4.1 Use of Color) that a hue-only ring is not.

This ADR reconciles the mockup's node language with the shipped one: adopt the mockup's
*spirit* and its **one** genuinely-additive polish (the selection glow), reject the parts
that would either regress accessibility or are physically impossible in the canvas renderer.

Two hard constraints bind the work:
- **The cytoscape canvas renderer has no per-node DOM / pseudo-elements.** Every per-node
  mark must be a cytoscape style channel, and those channels are **all already taken**:
  `background-color` = kind fill, `shape` = kind, `border-*` = selection / root / External,
  `overlay-*` = severity halo (ADR-010) + busy ring (ADR-019), `underlay-*` = diff (ADR-015),
  `background-blacken` / `text-opacity` / `z-index` = interaction feedback (ADR-018). A new
  per-node accent mark has no free channel.
- **C# ⇄ JS palette parity is hand-mirrored** (ADR-021 / ADR-026): `BrandTokens` ⇄ `graph.js`
  THEME/CHROME ⇄ `index.html` ⇄ `verify.mjs` ⇄ `WebBundleTests`, moved in lock-step,
  review-enforced. Any new token joins that mirror set.

## Decision

### D1 — Keep per-kind SHAPES. Reject the mockup's circle+ring node geometry.

The per-kind **shape** vocabulary stays exactly as shipped (ADR-004), in **both** themes. It
is the colourblind-redundant 1.4.1 channel: a red-green-blind reader tells a GG from a DL by
the triangle-vs-diamond silhouette, never by a ring hue. The mockup's circle+ring (every
group a circle, scope = ring colour) collapses the kind signal onto a single hue channel and
is **rejected** — this matches ADR-026 D5, which already pre-rejected the mockup's
circle+ring and its GG=blue / DL=teal scope recolour for the same reason.

We adopt the mockup's *spirit* ("type readable before the label" — already true: the shape
reads before the label, which is gated behind `min-zoomed-font-size`) and only its
**compatible** polish (D3), never its node geometry.

### D2 — The mockup's per-node corner audit-state badge is NOT adopted.

The mockup stamps a small Error/Warning/Info badge in each node's corner. That is a per-node
**DOM pseudo-element**, which the **canvas** renderer does not have (constraint above) — it is
not a feature we declined for taste, it is one the renderer cannot draw without a per-node
overlay element (the software-rendering-floor target forbids N overlay DOM nodes; ADR-004
chose canvas precisely for the 5000-node floor). The app's existing, equivalent audit-state
signalling stays authoritative and is **not a gap**:

- the **severity overlay halo** (ADR-010, the `overlay-*` channel) — a coloured glow behind
  the flagged node, with the colourblind-redundant monotonic padding (7/6/5);
- the **forced Error label** at fit zoom (`min-zoomed-font-size: 0` on `node[sev='error']`,
  ADR-018 D4 / F9) so the worst finding is always named;
- the **roll-up ring** on a loaded group hiding flagged descendants (ADR-010 D4); and
- the **authoritative count** in the violations sidebar (AP 3.4) — the number lives in
  native chrome, never on canvas (canvas cytoscape has no text-on-node-corner primitive).

The halo + forced label + sidebar count together deliver the mockup's *intent* (the node
shows it is flagged; the exact severity/count is one glance away) within the renderer's
constraints. This is documented here as the **rationale**, not a deferral.

### D3 — Selection gains an accent halo + pulse (the one adopted mockup polish).

The mockup's selected/root node has a soft **accent glow ring** that gently **pulses**.
Today selection (ADR-018 #89) is the neighbourhood dim (`applySelection`) plus a white
`node:selected` border (width 3) — channel-safe but visually quiet. The accent glow is the
single piece of the mockup's node language that is genuinely additive and worth adopting.

Because every per-node cytoscape channel is taken (constraint above; an accent `overlay`
would fight the severity halo, an accent `border` would fight the white selection border /
root border / External dashed border), the accent glow needs a **new layer outside
cytoscape's style system**.

**Chosen approach — a single DOM-overlay accent ring element (`#gw-accent-ring`).** One
absolutely-positioned `<div>` in `index.html` (the same overlay family as the existing
`#legend` / `#controls`), `pointer-events: none`, hidden by default. On a selection it is
positioned at the selected node's `renderedPosition`, sized to the node's rendered diameter
plus a glow margin, and **tracked** across `render` / `pan` / `zoom` / `position` cy events so
it follows the node during gestures and lazy-expand re-layouts. It is shown by
`applySelection` and hidden by `clearSelection`. **Exactly ONE element** ever exists
(software-rendering-floor safe — no per-node proliferation). The existing white
`node:selected` border is **kept** (it still composes and wins on the border channel); the
accent ring is a purely **additive glow** drawn on top.

**Why the DOM ring over the cytoscape fallback.** The fallback (a looped `cy.animate` on the
selected element) was rejected for two reasons: (a) it has no free per-node channel to animate
without colliding with severity/diff/border, and (b) `cy.animate` increments the `#88`
isolated-motion counter (`__gwAnimateCalls`) that `verify.mjs` pins at **0** across the entire
selection block (ADR-018 D2: selection is INSTANT class toggles, never `cy.animate`). A DOM
ring with a **CSS** keyframe pulse keeps `cy.animate` untouched, so the existing
instant-selection contract and its motion-counter pins hold unchanged.

**Pulse + reduced motion (ADR-017).** The pulse is a CSS `@keyframes` animation on the ring
element, applied via a class. It is **gated on `reduceMotion`** — graph.js already reads
`window.matchMedia('(prefers-reduced-motion: reduce)')` once at IIFE init (ADR-017 D5); the
ring code reuses that single read. Under `prefers-reduced-motion: reduce` the ring is shown
**static** (no `@keyframes`, no animation class), satisfying the ADR-017 no-animation contract;
otherwise it pulses gently.

**Clearing.** The ring hides on `clearSelection` (background tap, empty/unknown `select`
command) and must also clear when the selected node **vanishes** on a `graphUpdate`
(lazy-expand replaces the element set): the tracking handler reads the live element by id and
hides the ring if it is gone, so a stale ring never floats over empty canvas.

### D4 — Graph accent token (mirrors the WP2 brand accent into the canvas layer).

WP1b themed the canvas (canvas/edge/severity/diff) but **not** an accent — ADR-026 D6's
brand accent landed only in the Avalonia chrome (`BrandTokens.Accent*`). WP3 brings the
**decorative accent** into the graph layer for the selection ring. Per-theme, mirrored from
the WP2 `AccentHex` / `AccentLightHex` decorative values:

| Role | Dark | Light |
|---|---|---|
| graph decorative accent (`THEME.*.accent`, `--gw-accent`) | `#8B7BFF` | `#6A5CFF` |

These are the **same** brand-purple decorative hues as `BrandTokens.AccentHex` /
`AccentLightHex` (ADR-026 D6). To keep the documented C# source-of-truth beside the existing
graph-light mirror group, `BrandTokens` gains `GraphAccentHex` (= `AccentHex` value `#8B7BFF`)
and `GraphAccentLightHex` (= `AccentLightHex` value `#6A5CFF`) constants — the canvas-layer
mirror of the chrome accent, documented as such (not consumed by any Avalonia converter; the
canvas runs in the `file://` WebView). The wire still carries **only** the variant string
(ADR-026 D5): `graph.js` owns the value in `THEME.dark.accent` / `THEME.light.accent`, and
the DOM ring reads it through the `--gw-accent` CSS variable set by `applyChromeVariant`.

**Accessibility framing.** The accent ring is a **large decorative graphical object**
(non-text), redundant with the white `node:selected` border and the neighbourhood dim — it is
not the sole indicator of selection, so it is not gated by a hard WCAG ratio (like the
ADR-026 severity/diff soft cues, which themselves blend < 3:1 by design and are read against
their dark counterparts). The opacity is chosen so the glow **reads clearly on both canvases**
— the brand purple sits well against the dark `#1b1f27` canvas and the light `#F5F6F8` canvas
(a soft outer glow + a semi-opaque ring), giving a visible but unobtrusive selection accent in
each theme.

## Consequences

- **Dark is byte-identical except the additive ring + the new accent token.** No existing
  cytoscape style rule changes; `THEME`/`CHROME` gain one accent entry each, `index.html`
  gains the `--gw-accent` var + the `#gw-accent-ring` element/CSS, and `applySelection` /
  `clearSelection` gain the show/hide + position calls. The first render (currentVariant
  `dark`, nothing selected) is unchanged — the ring starts hidden.
- **A new hand-mirror entry joins the parity set** (`BrandTokens.GraphAccent*Hex` ⇄
  `THEME.*.accent` ⇄ `index.html --gw-accent` / `CHROME.*` ⇄ `verify.mjs` ⇄ `WebBundleTests`),
  moved in lock-step, reviewer-enforced — same review-only invariant as ADR-021/ADR-026.
- **The `#88` isolated-motion counters stay 0 across selection** because the pulse is CSS,
  not `cy.animate` — the existing ADR-018 instant-selection pins in `verify.mjs` are
  unaffected.
- **Reduced motion is honoured** (ADR-017): the ring is static under
  `prefers-reduced-motion: reduce`, animated otherwise — reusing the single init-time
  `reduceMotion` read.
- **Mockup parity is partial by design:** the circle+ring geometry (D1) and the per-node
  corner badge (D2) are rejected/not-adopted with rationale; only the selection glow (D3) is
  taken. Reviewers and future redesign passes should treat D1/D2 as settled, not as gaps.
- **Known follow-up:** none required for v0.x; a future pass could extend the same DOM-ring
  layer to the root node's resting accent (the mockup also glows the root), but WP3 scopes the
  ring to the active selection only.
