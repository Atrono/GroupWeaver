# ADR-035: Graph-overlay accessibility completion — command palette / minimap / issues-only filter become judged, AT-legible surfaces

**Status:** Accepted · **Date:** 2026-07-01
**Decides:** issue #223 (Lever 3 of the 2026-07-01 fit-audit — formalize three shipped graph-layer overlays and complete their accessibility)
**Phase:** 4 (feedback-driven)
**Builds on:** ADR-023 (graph controls/find), docs/ui-checklist.md §A

## Context

Three interactive graph-layer overlays shipped after ADR-023 but were never given
their own `docs/ui-checklist.md` §A rows, so the `ui-verifier` never judged them and
their accessibility was never audited:

- **The Ctrl+K command palette** (`#find-input` as a `role=combobox`, a `#palette-results`
  `role=listbox`, node matches + quick actions; `src/App/web/graph.js` `buildPaletteItems`
  / `renderPalette` / the `#find-input` keydown handler) — shipped as WP3c (#144).
- **The minimap** (`#minimap` + `#minimap-viewport`, a `cy.png` thumbnail with a live
  viewport rect and click/drag-to-pan; `refreshMinimap` / `updateMinimapViewport` /
  `minimapPanTo`) — shipped as WP3d (#146).
- **The "Issues only" filter** (`#issues-btn`, hides every node without a finding or a
  flagged-descendant roll-up; `applyIssuesFilter` / `syncIssuesButton`) — shipped as
  WP3b (#142).

They function, but ADR-023's own consequences only promised §A rows for the *original*
cluster (Find / Fit / Zoom / Labels). These three are judged nowhere, and the palette —
the headline keyboard surface — completes only PART of the ARIA 1.2 combobox pattern:
`#find-input` carries `role=combobox` / `aria-expanded` / `aria-controls`, and each
option `<li>` carries `role=option` + `aria-selected`, but the option rows have **no
stable ids** and the combobox never sets **`aria-activedescendant`**, so a screen reader
following the input's focus is never told which option the up/down keys have highlighted.
Two more gaps: the "No match" / "No issues" status changes are silent to assistive tech
(no live region), and the light-theme "No match" text (`--gw-no-match: #BD7C00` on the
near-white composited `#controls` surface) reads only ~3.44:1 — under the WCAG 1.4.3
4.5:1 text floor the rest of the v0.2 bar meets.

The persona this closes for is the **point-in-time auditor working entirely by keyboard
and/or assistive technology** — the same read-only auditor the whole product serves, but
one who never touches the mouse. The command palette is that person's primary way to
navigate a 200-node graph; a combobox that does not announce its active option, and a
status that changes silently, leave them without the orientation a sighted mouse user
gets for free. This ADR is markup + CSS only; no behaviour, no wire, no C# changes.

## Decision

### D1 — Adopt the three overlays as checklist-judged §A surfaces.

The command palette, the minimap, and the issues-only filter each gain explicit
`docs/ui-checklist.md` §A rows in the section's existing row + evidence-tag format
(`[S:name]` / `[P]` / `[T:Class]` / `[I]`), so the `ui-verifier` judges them on every
graph change exactly like the ADR-023 cluster and the diff cues. The rows reuse the
existing `graph-controls.png` fixture for the palette open state and the issues-only
button; the minimap is judged from `graph-overview.png` (bottom-left, non-empty graph).
No new behaviour — this decision only makes the already-shipped surfaces auditable.

### D2 — Complete the ARIA 1.2 combobox pattern on the palette.

Each rendered option `<li>` in `#palette-results` gets a **stable id** of the form
`palette-opt-<i>` (its zero-based render index), set in `renderPalette` where the `<li>`
is already built. The `#find-input` combobox sets **`aria-activedescendant`** to the id
of the currently-highlighted option (`palette-opt-<paletteIndex>`) on open, on every
up/down move (`movePaletteHighlight`), and after each input rebuild (`refreshPalette`),
and **clears the attribute** (removes it) on close (`closePalette`), on Esc, and whenever
the highlighted index is `-1` (no results / no match). This is additive to the existing
`role=combobox` / `aria-expanded` / `aria-controls` on the input and the `role=option` /
`aria-selected` on the rows — the missing link that lets an AT user follow the highlight
without moving DOM focus off the input (the "focus stays on the input" combobox model).

### D3 — Announce status changes through one polite live region.

The "No match" affordance (`#find-no-match`) and the "No issues" state (the `#issues-btn`
label swap in `syncIssuesButton`) are status changes an AT user cannot see. Introduce a
single visually-hidden `role=status` / `aria-live=polite` region in `index.html` whose
text content is written when those states change ("No match", "No issues", and a cleared
empty string when they resolve). `role=status` (implicit `aria-live=polite`) is chosen
so the announcement waits for a pause in the user's typing rather than interrupting it.
The existing visible `#find-no-match` text and the `#issues-btn` label stay exactly as
they are (they are the sighted channel); the live region is the parallel AT channel.

### D4 — Clear WCAG 1.4.3 on the light-theme "No match" text.

The `.no-match` selector reads its colour from `--gw-no-match`, whose light value
(`CHROME.light['--gw-no-match'] = #BD7C00`, mirrored on `:root` in index.html) reaches
only ~3.44:1 on the composited light `#controls` surface — under the 4.5:1 text floor.
Retone the light "No match" ink to a value that clears 4.5:1 on that surface (a deepened
amber-brown, e.g. `#8A5A00`, verified by `tools/check-contrast.ps1`), applied via the
`--gw-no-match` light token in the `CHROME.light` table (graph.js, the source of truth).
The dark value is unchanged (it already passes; the "No match" prose is text, so 4.5:1
governs the light case). The retone stays a chrome-only var (no C# BrandTokens consumer).

## Where the code lives

- `src/App/web/index.html`
  - `#controls > input#find-input` — the `role=combobox` input that gains the
    `aria-activedescendant` writes (D2).
  - `#palette-results` (`role=listbox`) — the `<ul>` whose `<li>` option rows gain
    `palette-opt-<i>` ids (D2).
  - a NEW visually-hidden `<div role="status" aria-live="polite">` (D3) — sibling of the
    `#controls` cluster, off-screen (a standard sr-only clip rule in the `<style>` block).
  - `.no-match { color: var(--gw-no-match); }` and the `--gw-no-match` light token (D4).
- `src/App/web/graph.js`
  - `renderPalette()` — sets each `<li>` id `palette-opt-<i>` and mirrors the active id
    onto the input via `aria-activedescendant` (D2).
  - `movePaletteHighlight(delta)` / `refreshPalette()` — update `aria-activedescendant`
    on nav / rebuild (D2).
  - `openPalette()` / `closePalette()` — set / clear `aria-activedescendant` with
    `aria-expanded` (D2).
  - `controlFind(noMatchEl)` and `syncIssuesButton()` — also write the D3 live-region
    text ("No match" / "No issues" / cleared).
  - `CHROME.light['--gw-no-match']` — the retoned light token (D4).
- `tools/check-contrast.ps1` — the verification path for D4 (add the light `.no-match`
  fg/bg pair; the deterministic report proves >= 4.5:1).

## Security-review note

Presentation only — this is a **read-only product** and this change is markup + CSS +
DOM-attribute writes exclusively. **No directory-write path** is added or touched; **no
new LDAP query, file-format, or deserialization surface**; no provider call, no
persistence, nothing crosses the bridge (the palette, minimap, and issues filter are all
canvas-local per ADR-023 — no bridge command, no C# change). `aria-activedescendant` and
the live-region text are set from ids/labels the bundle already computes; no untrusted
token is introduced. The `security-review-groupweaver` threat model is unaffected.

## Rejected alternatives

- **Replace the palette input+listbox with a native `<select>`.** Rejected: a `<select>`
  cannot host the two-line node/action rows (label + dim hint), cannot show quick
  actions interleaved with node matches, and does not fit the "focus stays on the input
  while typing filters" model ADR-023 D5 established. The ARIA combobox pattern is the
  correct control; it only needed `aria-activedescendant` to be complete.
- **`aria-live=assertive` for the status region (D3).** Rejected: assertive interrupts
  the user mid-keystroke on every "No match" while typing a query — exactly the wrong
  time. `role=status` / polite waits for a typing pause, which matches how a sighted user
  perceives the affordance appearing quietly.
- **Move DOM focus onto the highlighted `<li>` instead of `aria-activedescendant`.**
  Rejected: it fights ADR-023's model where the input keeps focus so typing keeps
  filtering; roving focus would blur the input on every up/down. `aria-activedescendant`
  is the pattern designed for exactly this.
- **Darken the shared `--gw-no-match` token in BOTH themes (D4).** Rejected: the dark
  value already passes and is parity-pinned; only the LIGHT text case fails, so only the
  light token moves — keeping the dark render byte-identical.

## Consequences

- **`tests/graph-bundle/verify.mjs`:** the WP3c palette-markup block gains asserts that
  each open option `<li>` has an id `palette-opt-<i>` and that `#find-input`'s
  `aria-activedescendant` equals the active option's id on open / after up/down and is
  absent when closed or on no-match; a new assert covers the D3 `role=status` region's
  text on "No match" / "No issues" and its clear. The existing
  combobox/`aria-expanded`/`aria-controls`/`[hidden]` asserts are unchanged.
- **`WebBundleTests` (C#):** may gain a structural assert that `index.html` ships the
  `role=status` live region and that `.no-match` binds `--gw-no-match`. The existing
  `Index_ReferencesBridgeBeforeGraph` substring gate is untouched — see the tripwire note.
- **Contrast baseline:** `tools/check-contrast.ps1` adds the light `.no-match` text pair;
  the D4 retone must show PASS at 4.5:1 there, so the "No match" ink can't silently drift.
- **`docs/ui-checklist.md` §A:** new rows (D1). No existing row moves.
- **index.html-comment substring tripwire (session-32 rule):** the implementer must NOT
  write the literal bundle-script *filenames* inside any `index.html` **comment** —
  `WebBundleTests.Index_ReferencesBridgeBeforeGraph` is a plain `IndexOf` substring scan,
  so an earlier comment mention of the bundle script name would fail the load-order gate
  even with the script tags correctly ordered. Paraphrase ("the bundle script").
