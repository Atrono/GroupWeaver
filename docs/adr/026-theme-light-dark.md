# ADR-026: Token-resolved light/dark theme — one switch, app-chrome first, graph canvas next

**Status:** Accepted · **Date:** 2026-06-23
**Refines:** ADR-021 (the one-palette-source / light-ready seam) · ADR-022 (`ui-state.json` persistence) · ADR-004/010/015 (the graph palettes + web-bundle parity) · **Decides:** the WP1 work package of the 2026-06 UX redesign · **Phase:** 4 (feedback-driven)

## Context

The imported Claude Design project *"GroupWeaver UX Redesign"* is dark-first **with a
light variant** (Frame 4 == Frame 1 with re-derived surfaces, lines and accent). Its
own thesis: *"theme is one switch — built on theme tokens, so dark ⇄ light is a single
toggle, no divergent screens to maintain."*

GroupWeaver is dark-only today but **light-ready by seam** (ADR-021 D4): `BrandTokens`
already names the page-relative role tokens (`PageBackground`, `OnDarkText`,
`OnLightText`, `NodeLiftRing`). What is missing is (a) the `ThemeVariant` switch, (b)
light values for the *chrome* roles (surfaces, lines, secondary text, card chrome —
today hardcoded translucent-white-over-dark, e.g. `CardBackgroundBrush #14FFFFFF`),
and (c) a themed graph canvas (the WebView bundle hardcodes every hue directly in the
cytoscape style objects and the `#legend`/`#controls` CSS — no CSS-var layer, no theme
message over the wire).

Two hard constraints bind any theme work:
- **WCAG 2.2 AA is non-negotiable here** (ADR-021's whole point). A light theme must
  re-derive, not just invert: low-opacity bright halos (severity overlay, diff
  underlay) that read on `#1b1f27` can fail 1.4.11 (3:1 non-text) on a near-white
  canvas. We must not ship a knowingly-inaccessible light theme.
- **C#↔JS palette parity is hand-mirrored** (`BrandTokens` ⇄ `graph.js` ⇄
  `index.html` ⇄ `verify.mjs` ⇄ `WebBundleTests`). A theme that adds light hues adds a
  second mirror set — review-enforced in lock-step.

## Decision

### D1 — One token model, resolved by `ThemeVariant`; the dark values are unchanged.

Each role token gains a **light** value beside its existing **dark** value; the dark
values stay byte-for-byte as shipped (every ADR-021 WCAG ratio holds). `BrandTokens`
remains the declared source of truth and grows a `…LightHex`/brush beside each role
that differs by theme; theme-invariant roles (the kind fills — all dark enough to read
on a light canvas too — stay single-valued).

The Avalonia side resolves via **`ThemeDictionaries`** (a `ThemeVariantScope`-aware
`ResourceDictionary` keyed `Light`/`Dark`) in `Styles/Tokens.axaml`, layered under
`FluentTheme` (which already flips its own control defaults per variant). The active
variant is set on `Application.Current.RequestedThemeVariant` at startup from persisted
state and on toggle.

### D2 — Ship in two slices; main stays shippable after each.

- **WP1a (this work package): app-chrome theme + toggle + persistence — Avalonia only.**
  Light values for the chrome roles, the few hardcoded dark brushes in `App.axaml` /
  views moved into the theme dictionaries, a toggle in the top command strip, and a
  `Theme` field on `UiState`. The graph **canvas is untouched (stays dark)** in this
  slice. A light app chrome around a dark graph canvas is a coherent, common pattern
  (the canvas is a viewport, not chrome) — not a half-done state.
- **WP1b (next): graph canvas over the wire.** A `theme` bridge message carries the
  resolved canvas/edge/label/legend tokens + the light-canvas hue set; `graph.js`
  rebuilds its cytoscape style and `index.html` chrome from CSS variables;
  `verify.mjs` gains a LIGHT assertion block. Built on WP1a's toggle.

### D3 — Chrome role → value map (WP1a). Light values taken from Frame 4.

| Role (Avalonia resource) | Dark (unchanged) | Light (Frame 4) |
|---|---|---|
| `PageBackgroundBrush` (window/page) | `#1b1f27` | `#ECEEF1` |
| `CardBackgroundBrush` (surface tint) | `#14FFFFFF` | `#0A000000` |
| `CardBorderBrush` (separators/cards) | `#22FFFFFF` | `#1A000000` |
| `SecondaryForegroundBrush` (chrome glyphs) | `#B0B5BD` | `#5A636E` |
| Ghost-button hover wash | `#14FFFFFF` | `#0D000000` |

`FluentTheme` supplies the primary `Foreground`/control backgrounds per variant, so the
opacity-driven Typography hierarchy (white-at-opacity on dark) flips automatically to
dark-ink-at-opacity on light — the type scale needs **no** per-variant change. The
`Button.hyperlink` blue `#0F6CBD` and the WebView2 banner amber both read on either
background and stay as-is.

### D4 — Persistence: `UiState.Theme` (ADR-022 convention).

`UiState` gains `Theme` (`"Dark"` | `"Light"`; default `"Dark"` — dark-first), persisted
to `%APPDATA%\GroupWeaver\ui-state.json` via the existing never-throw `UiStateStore`.
The toggle (an icon button in the top strip, left of Focus) flips
`Application.Current.RequestedThemeVariant` and saves. **Test seam (lab-environment
rule):** every test that constructs a VM/shell reading user-profile state MUST inject a
temp-dir `UiStateStore` — a green CI run can otherwise hide a real-`%APPDATA%` read
(the #124 lesson). The new `Theme` field rides the same injected seam.

### D5 — Light-canvas graph hues (WP1b spec, pinned now so WP1a doesn't foreclose it).

On the light canvas (`#F5F6F8`) the kind FILLS still read (all are dark-on-light ≥ 3:1),
so they are **theme-invariant**. The semi-transparent **severity overlays** and **diff
underlays** are re-derived for the light canvas using Frame 4's deepened hues
(severity red `#D63A4A`, amber `#BD7C00`, green `#1F9D57`; the diff/scope set likewise),
each re-checked ≥ 3:1 (1.4.11) at its painted opacity. Canvas `#F5F6F8`, grid
`rgba(0,0,0,.045)`, edges membership/containment re-toned to dark-on-light keeping the
ADR-005/F6 four-channel separation (lightness + weight + dash + arrow) ≥ 3:1, node
label ink dark with a light outline. The mockup's *circle+ring* node language and its
GG=blue/DL=teal scope recolor are **rejected** (ADR-NNN node-language reconciliation,
WP3): the per-kind shape vocabulary is the colourblind-redundant 1.4.1 channel and is
kept in both themes.

## Consequences

- Dark theme is provably unchanged (values identical) — no re-pin of existing WCAG
  ratios or screenshot tripwires for WP1a beyond the moved brushes.
- A second hand-mirrored palette set (light) lands in WP1b; the reviewer enforces
  lock-step parity (`BrandTokens` light ⇄ `graph.js` ⇄ `index.html` ⇄ `verify.mjs`),
  same review-only invariant as ADR-021.
- After WP1a, toggling Light themes every native screen (Connection, RootPicker,
  Workspace rail/sidebar/detail, Plan, Gap, Settings dialog) while the graph canvas
  stays dark — documented, not a defect; WP1b completes the canvas.
- `UiState` gains a field; the JSON is forward/back compatible (missing ⇒ default
  `"Dark"`, never-throw load). No AD surface touched — read-only product unaffected.
- **Known follow-up:** WP1b light-canvas hue derivation + the `theme` wire message;
  WP2 may additionally retone the *dark* surfaces toward Frame 1's deeper `#0E1013`
  family (out of scope here — WP1a keeps the shipped dark values).
