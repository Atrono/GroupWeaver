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

### D5 — Light-canvas graph hues (WP1b, SHIPPED).

The wire carries **only the variant string** (`{type:'theme', variant:'dark'|'light'}`);
`graph.js` owns the dark+light token tables (mirrored in `BrandTokens` as the documented
C# source — the `Graph*LightHex` group — and pinned by `verify.mjs`'s LIGHT block +
`WebBundleTests`). The light canvas is `#F5F6F8`.

**Kind FILLS are theme-INVARIANT** (all clear 3:1 on the light canvas: User 4.22, GG 4.96,
DL 5.98, UG 5.75, OU 4.98, Computer 5.90, External 4.26), so they are NOT re-toned and the
dark 1.4.11 **border-lift** (DL/UG/Computer) is **dropped on light** (`nodeLiftWidth = 0`) —
unneeded since the three fills already clear 3:1 on light. The mockup's *circle+ring* node
language and its GG=blue/DL=teal scope recolor are **rejected** (WP3 node-language
reconciliation): the per-kind **shape** vocabulary is the colourblind-redundant 1.4.1 channel,
kept in both themes.

**Structural objects** (≥ 3:1 non-text, 1.4.11; text ≥ 4.5:1, 1.4.3 — all vs canvas `#F5F6F8`):

| Role | Dark (unchanged) | Light | Light ratio |
|---|---|---|---|
| canvas / body bg | `#1b1f27` | `#F5F6F8` | — |
| node label ink (text) | `#E8ECF2` | `#1C2127` | **14.98:1** |
| node label outline | `#1b1f27` | `#F5F6F8` | — |
| membership edge (solid, w1.6, arrow) | `#8E9BB4` | `#5A6473` | **5.54:1** |
| containment edge (dashed, w1) | `#6B788F` | `#3A424E` | **9.39:1** |
| root node border (w3) | `#E8ECF2` | `#1C2127` | **14.98:1** |
| External dashed border | `#B0B6BF` | `#6B7480` | **4.38:1** |
| node:selected border (w3) | `#FFFFFF` | `#1C2127` | **14.98:1** |

The F6 four-channel membership-vs-containment separation (lightness + weight + dash + arrow)
holds: light membership `#5A6473` stays lighter than containment `#3A424E`.

**Severity halos + diff underlays** are soft semi-transparent emphasis cues (redundant with
the sidebar E/W/i letter + node shape). Like the **dark** halos — which themselves blend
*below* 3:1 over their bg (error 1.57, warning 2.65, info 2.07; diff added 2.42, removed 1.96,
unchecked 1.75) — the light cues cannot reach 3:1 as a translucent ring. The bar is therefore
**read at or above the dark counterpart's blended ratio**, achieved via deepened Frame-4 hues +
raised opacities. Effective blended ratio vs `#F5F6F8`:

| Cue | Dark hue @opacity (blended) | Light hue @opacity | Light blended |
|---|---|---|---|
| severity error halo | `#D13438` @0.45 (1.57) | `#D63A4A` @0.70 | **2.84:1** |
| severity warning halo | `#F7A30B` @0.45 (2.65) | `#BD7C00` @0.75 | **2.34:1** |
| severity info halo | `#4FA3E3` @0.40 (2.07) | `#2F6FE0` @0.70 | **2.68:1** |
| roll-up ring (max-sev, fainter) | `· @0.30` | `· @0.50` | err 2.07 / amber 1.84 / info 1.97 |
| busy ring | `#4FA3E3` @0.35 | `#2F6FE0` @0.55 | **2.12:1** |
| diff added underlay | `#2FAE4E` @0.5 (2.42) | `#1F9D57` @0.70 | **2.23:1** |
| diff removed underlay | `#E0503A` @0.5 (1.96) | `#D63A4A` @0.70 | **2.84:1** |
| diff unchecked underlay | `#8A8F98` @0.35 (1.75) | `#5A6473` @0.50 | **2.08:1** |

The diff **edge lines** are near-opaque (structural directed signals), so they clear ~3:1:
added `#1F9D57` @0.95 = **3.05:1**, removed `#D63A4A` @0.85 = **3.51:1**, unchecked
`#5A6473` @0.60 = 2.49:1 (the dotted unchecked line is the faintest, redundant with its
dotted line-style). The removed-node kind-fill fade (`background-opacity 0.45`, the
colourblind brightness channel) is theme-invariant.

**Live toggle:** `ShellViewModel.ToggleTheme` (WP1a) additionally calls
`IGraphRenderer.SetThemeAsync(isLightTheme)` on every tracked graph-bearing step (Workspace/
Plan/Gap, including parked surfaces) — fire-and-forget, never-throw, no single-flight. Each
renderer ALSO prepends the current variant to its render/replay pipeline, so a freshly-rendered
or re-attached graph matches the live theme with no flash. The export-PNG background still
defaults to the dark `#1b1f27` (ADR-013) — a themed export bg is a WP-follow-up, not WP1b.

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
