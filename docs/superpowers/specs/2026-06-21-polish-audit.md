# v0.2 Polish Audit — ranked backlog (Phase 0 output)

**Date:** 2026-06-21 · **Spec:** `2026-06-21-v0.2-polish-pass-design.md` · **Phase:** 4
**Method:** rendered every shipped surface (32 native PNGs via `ShellScreenshotTests`, 5
graph PNGs via `verify.mjs`, 3 motion clips + 31 sampled frames via the new
`tools/capture-motion.ps1`), judged against an elevated bar — `frontend-design` lens
(identity), the `[I]` items + motion frames (feel), every empty/error/honesty state
(edge-state UX) — anchored to **WCAG 2.2 AA** (`tools/check-contrast.ps1`) and **Nielsen's
heuristics**. Rendering mode: GPU (Intel UHD 620); motion clips also represent the
software floor.

## Headline

Both layers **pass correctness but lack identity**. The graph reads as the generic
AI-default dark force-graph (#1b1f27 + one accent); the chrome reads as default-Fluent-dark
with a semantic palette bolted on. The encoding system (kind=shape+fill, severity=overlay,
diff=underlay, root=border) is genuinely well-designed but **invisible at working zoom and
completely static** (no hover, no selection feedback, no transitions). The craft is all in
the data model; almost none is in the *feel*.

## Identity-ADR gate — DECIDED

An ADR **is** required, for **one** slice only: the **design-tokens / WCAG re-tone**
(#90). Both audits converged on it independently — it touches the parity-pinned palette, so
it needs the single-source-tokens ADR + C#↔JS re-pin. **Everything else — the graph
signature, all motion, interaction feedback, type scale, layout, copy — is non-palette and
ships WITHOUT the ADR.** The highest-leverage identity and feel wins are therefore *not*
blocked on the palette gate.

## WCAG 2.2 measured data (token vs page bg #1b1f27)

FAIL non-text 3:1 (1.4.11): **DomainLocal #A14000 (2.55), Universal #744DA9 (2.66),
Computer #556070 (2.59)** — node-fill kinds. **White-on-Warning badge 2.06 (FAIL both)** —
concrete legibility bug. FAIL text 4.5:1 but pass non-text: User 3.62, GG 3.08, OU 3.07,
External 3.58, Error 3.35, DiffRemoved 4.23. Comfortable pass: Warning 8.02, Info 6.04,
DiffAdded 5.73, DiffUnchecked 5.08. (Caveat: report measures the flat source token; rendered
nodes are a blue-gray fill + thin colored border, so on-canvas contrast differs — judge in
context.)

## Signature direction (the one bold move) — REVISED 2026-06-21

> **Original premise REFUTED.** The audit assumed the concentric radius mapped to AGDLP
> kind tiers (the "onion"). The #87 design workflow + adversarial critique proved it false:
> radius = **OU-containment depth**, not kind tier (GraphBuilder `GetRingKey` = (depth,
> kindRank), depth primary; ADR-004 D1 verbatim; the same kind appears at many radii).
> Drawing A/G/DL/P tier rings would be a lie the layout doesn't implement — and the honest
> depth-vignette fallback is the weakest, highest-"AI-decoration"-risk identity move and adds
> perceptual load to an already 4-channel scene.

**Revised signature = the encoding language, made legible.** Per UI-design/psychology
(perceptual budget, figure-ground, expert-trust-via-precision, von Restorff memorability),
the one element that is BOTH true and distinctive is GroupWeaver's encoding system. Recast
**#87** into a crafted, information-dense **legend/key** (folds F10): the full 4-channel
visual language (kind shape+color / severity halo / diff underlay) shown with the actual node
visuals as swatches, plus **live per-kind counts** (visibility of system status). True,
task-relevant, memorable — the signature is the encoding, not a background flourish.

## Ranked backlog

### High leverage — filed as issues, the Phase-1 work front
| # | Slice | Dim | Effort | Palette/ADR |
|---|---|---|---|---|
| #87 | Encoding-key signature: live-count legend + full 4-channel key (recast from AGDLP tier shells — premise refuted; folds F10) | Identity | M | no |
| #88 | Graph motion: eased focus-fit + expand enter-anim + in-canvas busy | Motion | M | no |
| #89 | Graph interaction feedback: hover + selection + neighborhood + selective labels | Motion/State | M | no |
| #90 | **ADR** + design tokens: WCAG re-tone (badges, DL/UG/Computer fills, white-on-Warning) | Identity/State | M | **yes** |
| #91 | Type scale & voice: wordmark, hierarchy, monospace DNs | Identity | M | no |
| #92 | Workspace right-column rhythm + findings scroll-clip fix | Identity/State | M | no |
| #93 | Connect step composition + action hierarchy (+ settings footer) | Identity | M | no |

### Medium — recorded here, file/roll-in during Phase 1
- **Graph edge legibility + viewport flicker** (F6/F7): membership vs containment are
  monochrome hairline variants (cobweb at 200 nodes); `hideEdgesOnViewport:true` makes edges
  vanish mid-zoom on small graphs. Differentiate edge value/weight; gate the hide on element
  count (preserve the software floor for large graphs). Effort S–M.
- **Interactive legend** (F10): static `pointer-events:none` key, verbose, omits the
  severity + diff channels. Add live per-kind counts, document all four channels, optional
  click-to-filter. Effort M.
- **Copy & honesty pass** (chrome#5/#7/#10): reword the GraphHost placeholder ("Graph view
  is unavailable in this environment" → end-user voice); add a long-German-DN fixture to
  prove picker truncation keeps the `DC=` tail; nudge gap amber to 4.5:1. Effort S.
- **Severity chip geometry** (chrome#6): give severity chips a different shape (square/diamond)
  from kind badges (rounded rect) so the two systems don't blur. Effort S (rolls into #90).

### Low / guard
- **Diff-at-scale verify** (F11): render a dense (200-node) gap dataset to confirm Added-green
  vs GG-green still reads; tune underlay padding if not. Verify-first, S.
- **Plain-text guard** (chrome#9): the no-control-char rendering (detail attrs, settings
  notes, naming preview, plan names) is correct — **keep it**; any recolor/"prettify" of those
  panels triggers `/security-review`. No change; regression guard.

## Intersections confirmed live
i18n: detail-panel DN wrapping verified; picker long-DN truncation **unproven** (demo DNs too
short) → needs the German-DN fixture (above). Security: plain-text rendering confirmed intact.
Cross-mode: plan/gap views share the badge + honesty patterns (so #90/#91 propagate to all
three). Theme: dark-only confirmed; the token set (#90) should enable a future light theme.

## Recommended Phase-1 order
Cheapest-highest-feel first: **#88 eased focus-fit** (S, transforms the whole tool's feel) →
**#89 selection/hover** → **#87 signature shells** → **#91 type scale** → **#92 layout fix**
→ **#93 connect** → **#90 ADR+tokens** (the WCAG-critical, palette-gated slice; sequence it
once the token single-source is clear, but it carries the only true bug, white-on-Warning, so
don't defer it far). Re-evaluate leverage every 2–3 slices; stop when it drops.
