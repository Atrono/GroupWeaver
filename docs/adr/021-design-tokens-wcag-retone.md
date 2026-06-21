# ADR-021: Design tokens + WCAG 2.2 AA re-tone — one palette source, on-badge ink, node border-lift

**Status:** Accepted · **Date:** 2026-06-22
**Refines:** ADR-004 (the kind palette / web-bundle parity) · ADR-010 (the severity palette + overlay) · ADR-015 (the diff palette) · ADR-018 (interaction-feedback parity) · **Decides:** issue #90 · **Phase:** 4 (v0.2 polish pass)

## Context

The v0.2 polish audit measured **two WCAG 2.2 AA failures** against the page background `#1b1f27`, and a structural smell underneath them.

**1.4.3 Contrast (Minimum), 4.5:1 — badge text.** White glyph text reads on the colored severity badges (the violations-sidebar E/W/i squares, the settings severity selector, the matrix deny-override chips). White on the Warning fill `#F7A30B` = **2.06:1** (FAIL) and white on the Info fill `#4FA3E3` = **2.73:1** (FAIL). White on Error `#D13438` = **4.93:1** (PASS). A concrete legibility bug on the amber and light-blue badges.

**1.4.11 Non-text Contrast, 3:1 — node fills.** The graphical-object contrast of three kind node fills against the page bg fails: DomainLocalGroup `#A14000` = **2.55:1**, UniversalGroup `#744DA9` = **2.66:1**, Computer `#556070` = **2.59:1** (all FAIL). The other four kind fills clear 3:1.

**The structural smell.** Every palette hex is duplicated across **five** converters (`AdObjectKindConverters`, `SeverityConverters`, `CellChoiceConverters`, `GapKindConverters`, `NamingPreviewConverter`) plus `graph.js` and `index.html`, with two test tripwires (`verify.mjs`, `WebBundleTests`) re-pinning them by hand. There is no single declared source. The theme is dark-only and must not foreclose a future light theme.

The redundant, non-color channels already in place (the severity E/W/i letter, the kind shape, the diff brightness + line-style) satisfy WCAG 1.4.1 (Use of Color) and must be preserved by any fix.

## Decision

1. **`BrandTokens` (`src/App/Views/BrandTokens.cs`) is THE declared source of truth.** It holds every palette hex as a named `const string XxxHex` AND a matching `ImmutableSolidColorBrush` static, grouped by role (kind fills, severity, diff, cell/naming, role tokens). All five converters reference these tokens instead of re-parsing `Color.Parse("#…")`. Because the graph runs as a `file://` bundle with no runtime share, `graph.js`, `index.html`, `verify.mjs`, and the C# `WebBundleTests` palette/border assertions remain **hand-copied mirrors**, pinned by the existing tripwires — a review-only parity invariant, documented at the top of `BrandTokens`.

2. **Per-severity on-badge text ink via `SeverityConverters.ToTextBrush`.** Error → white (`OnDarkText`, 4.93:1 ✓), Warning → `#1b1f27` (`OnLightText`, the page-bg ink, **8.02:1** ✓), Info → `#1b1f27` (**6.04:1** ✓). The amber and light-blue fills are too light for white text; only the red keeps white. `CellChoiceConverters.ToTextBrush` mirrors it (Allow/Deny keep white — both ≥ 4.5:1 on their own fills; the E/W/i overrides delegate to `SeverityConverters.ToTextBrush`). The naming-preview Violation chip already renders its glyph/caption as the severity HUE TEXT on the dark page bg (a transparent chip, never white-on-fill), so amber `#F7A30B` text = 5.39:1 and it needs **no** change — it was never the white-on-fill bug. **No fill hex changes**; the redundant E/W/i letter and kind shape channels are untouched (1.4.1 preserved).

3. **Node 1.4.11 fix = a 2px `#8A93A3` border-lift on DL / UG / Computer; FILLS unchanged.** The ring reads 5.33:1 vs the page bg, lifting the three failing node objects while the fill hex stays as-is — so the kind-badge white-on-fill text (which the fills already pass for ≥ 4.5:1) and the `PaletteHexes` parity both hold. **Brightening the fills was evaluated and rejected:** a fill bright enough to clear 1.4.11 (3:1 non-text) lands at or below the 1.4.3 edge (4.5:1) for the white kind-badge text, trading one failure for another; the border-lift fixes 1.4.11 with zero fill churn. The root node's white `#E8ECF2` w3 border (appended later) still wins on root; the External dashed `#B0B6BF` border stays distinct.

4. **Dark-only today, light-ready by seam.** The role tokens (`PageBackground` `#1b1f27`, `OnDarkText` `#FFFFFF`, `OnLightText` `#1b1f27`, `NodeLiftRing` `#8A93A3`) name the page-relative roles a future light theme would re-bind. **No `ThemeVariant` switch is added now** — only the seam.

## Consequences

- **C#↔JS palette parity is RE-PINNED BY HAND — the review-only invariant.** `BrandTokens` is the source; `graph.js` / `index.html` / `verify.mjs` / `WebBundleTests` are mirrors. The reviewer confirms every value moves in lock-step on any palette PR; there is no runtime guarantee across the `file://` bundle.
- A **clean future-light-theme seam** exists (the named role tokens) without committing to a theme switch.
- **Known deferral:** the GapView diff-glyph text color is **outside the two audited fails** and is recorded here, not changed. The diff glyph (`GapView.axaml`) renders **white on the diff fill** (Added `#2FAE4E` / Removed `#E0503A` / Unchecked `#8A8F98`) — the same white-on-fill pattern this slice fixed for the severity badges, so it is sub-4.5:1 for the diff hues. It was simply not in the audit's named scope (white-on-Warning/Info + the three node fills); the same per-hue `ToTextBrush` treatment is the follow-up fix.
- **Screenshot parity pins are unaffected:** the `ShellScreenshotTests` derive their brushes from the converters at runtime and assert `Background` (the fill), not `Foreground` (the ink), so the on-badge text re-tone does not perturb them.
- The test-engineer re-pins the tripwires: `verify.mjs` gains a `KIND_BORDER` block (the DL/UG/Computer ring color/width) and `WebBundleTests` gains the matching border assertions; the graph-bundle test may not yet assert the new border until that lands.
