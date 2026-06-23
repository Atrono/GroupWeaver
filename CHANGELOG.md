# Changelog

All notable changes to GroupWeaver are documented here. The format is based on
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and this project
adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [0.3.1] - 2026-06-23

Back-navigation polish. Returning from Plan or Gap mode to the previous graph now
preserves the exact viewport (zoom/pan) instead of reloading and re-fitting. Still
read-only by construction.

### Fixed
- **Viewport-preserving Back** — pressing Back from Plan (to the workspace) or from
  Gap (to Plan) used to reload the graph and re-fit it (a brief flash, viewport
  lost). It now restores the live view at the exact zoom and pan you left: the graph
  surface is kept alive in a hidden, always-attached host and moved back into place
  rather than re-rendered (ADR-025). The re-render path remains as the fallback.
- **Renderer disposal** — a Plan/Gap graph surface is now disposed deterministically
  when abandoned, retiring a long-standing never-disposed-renderer leak; live WebView
  surfaces stay bounded.

## [0.3.0] - 2026-06-23

Third feature release — graph **navigation and presentation**. It makes the graph
view drivable from inside the canvas and adds a distraction-free way to show it,
on top of the v0.2.0 feature set and the v0.2.1 polish pass. Still read-only by
construction: nothing in this release writes to Active Directory.

### Added
- **In-graph controls & find-a-node** — an on-canvas control cluster (Fit, Zoom in /
  out, all-labels toggle) and a **find any node by name or DN** box that frames the
  match, plus keyboard shortcuts scoped to the graph (`Ctrl+F` find, `Ctrl+0` fit,
  `Ctrl+±` zoom). Find mirrors a tap without spoofing the focus protocol (ADR-023).
- **Focus mode & full-screen** — a distraction-free **focus mode** (`F`) that folds
  the chrome away to present the graph edge-to-edge, plus `F11` full-screen, an
  adjustable / collapsible panel rail with per-state persistence, and a scope-summary
  card when the detail panel is empty (ADR-022).

### Fixed
- **Back-navigation crash** — pressing Back from a graph-bearing step (Plan → Back to
  Workspace, Gap → Back to Plan) crashed the app: the shared WebView control was never
  released from the leaving view, so the next view re-parented a control that still had
  a parent (`InvalidOperationException` on the measure pass). Leaving views now release
  the graph surface on detach and the renderer restores the graph on re-entry; every
  render / focus / export path is hardened never to throw onto the UI (ADR-024). The
  Back round-trip currently re-fits the graph (viewport not yet preserved — tracked).
- **Naming-rule preview hardening** — the settings naming-rule live preview now caps a
  pattern's length before constructing its regex (shared with the ruleset loader), so a
  pathologically long author-time pattern can't stall preview compilation.

### Security
- Read-only by construction holds across every change in this release — the graph
  navigation, focus mode, and crash fix are all view-layer; there is no new AD code path.
- The naming-preview pattern-length cap closes a regex-construction stall at author time
  (defense in depth alongside the existing finite match timeout and bounded compile cache).

## [0.2.1] - 2026-06-22

UI polish pass over the v0.2.0 feature set — presentation only. No new features,
no behaviour change, and still read-only by construction: nothing in this release
writes to Active Directory. An audit against declared standards (WCAG 2.2 AA,
Nielsen heuristics, a `frontend-design` identity lens) drove every change.

### Added
- **Graph motion** — focus / jump-to-node now *eases* the camera (no synchronous
  cut) and genuinely-new nodes *fade in* on lazy expand; an in-canvas **busy ring**
  marks the node being expanded for the directory round-trip. Honors
  `prefers-reduced-motion` (ADR-017, ADR-019).
- **Graph interaction feedback** — tapping a node selects it (white border) and
  dims non-neighbors; hover brightens; the root and Error-severity nodes stay
  labeled at fit zoom. A sidebar jump now drives the same on-canvas selection
  (ADR-018, ADR-020).
- **Encoding-key legend** — the static legend is recast as a crafted key with the
  real node shapes as swatches and **live per-kind counts**.
- **Declared visual identity** — a single-source `BrandTokens` palette and a
  declared type scale (`src/App/Styles`): a wordmark treatment, all distinguished
  names in a tabular-monospace face, a primary/secondary action hierarchy on the
  connect and settings screens, and consistent card/spacing rhythm (ADR-021).

### Fixed
- **WCAG 2.2 AA contrast** — Warning/Info severity badge glyphs were white-on-fill
  (2.06:1 / 2.73:1) and now use per-hue dark ink; the DomainLocal / Universal /
  Computer node fills (sub-3:1 against the page background) gain a contrast-lift
  border so the node boundary reads (1.4.11). Every semantic cue keeps its
  redundant non-color channel (1.4.1).
- **Findings list** no longer reads as a clipped render bug — a soft scroll fade
  plus a distinct, carded "unexpanded areas are unchecked" notice; the detail
  panel closes with a terminus rule so a short panel no longer leaves a dead gutter.
- **Plan editor input hardening** — the plan editor now rejects the same unsafe
  characters at author time (adding / renaming a node) that the PowerShell export
  already rejected (control characters, line/paragraph separators, and Unicode
  quotation marks), so an unsafe name is caught as you type rather than only at
  export. The export boundary itself is unchanged.

## [0.2.0] - 2026-06-15

Second feature release. Adds **plan authoring**, **gap analysis** (diff a proposed
plan against the live structure), and **export** on top of the v0.1 read-only
viewer. Still read-only by construction: nothing in this release writes to Active
Directory — the PowerShell plan export emits an inert script for a human to review
and run, never executed by GroupWeaver.

### Added
- **Plan mode** — author a proposed group structure (global / domain-local /
  universal groups, users, and memberships) in a panel-based editor with the
  read-only graph as a live preview. The same rule engine validates the plan as
  you edit (nesting matrix, naming, circular nesting, empty groups). Reached from
  the workspace and returning to the same explore session.
- **Plan PowerShell export** — export a plan as an **inert** `.ps1` script (UTF-8,
  no BOM) for review outside the app. All authored names are contained inside
  single-quoted literals, and characters that could escape a shell literal
  (control characters, line/paragraph separators, and Unicode quotation marks)
  are rejected; GroupWeaver never runs the script and never writes to AD.
- **Gap analysis** — diff a proposed plan against the live ("Ist") structure.
  A union graph paints each object and membership as Added (green), Removed
  (red-orange, faded), or Unchecked (gray) using channels disjoint from kind and
  severity, so kind and rule findings still read through. A "Changes" sidebar
  lists the deltas with a summary line, and an honest "unexpanded areas are not
  compared" banner whenever part of the live structure was never loaded.
- **Export** — export the findings report as **CSV** (formula-injection-guarded)
  or a self-contained **HTML** report (every value HTML-encoded), and export the
  current graph as a **PNG** image. Demo-mode only for any published media.
- **Reload scope** — a "Reload scope" action rebuilds the current scope from a
  fresh load, clearing any orphaned ex-member nodes left by an in-place refresh.

### Changed
- WebView2 host hardened defense-in-depth: DevTools disabled, new-window
  navigation suppressed, navigation locked to the local `file://` bundle.
- Untrusted regular expressions (naming patterns and ignore/exception globs) run
  with a finite match timeout and a bounded compile cache.

### Fixed
- Export save-dialog wiring: the CSV / HTML / image export buttons were inert in
  the shipped v0.1 app (the file-dialog seam was never wired in production); they
  now open a save dialog and re-arm as you switch between explore and plan modes.

### Security
- Read-only by construction holds across all new features, including the plan
  PowerShell export (inert, single-quote-contained, shell-unsafe characters rejected).
- Supply chain: the vendored `cytoscape.min.js` is pinned and verified
  byte-identical to its upstream npm release; all CI workflow actions are pinned
  to commit SHAs and the .NET SDK is pinned via `global.json`.

## [0.1.0] - 2026-06-13

First public release. GroupWeaver is a **read-only** Windows desktop app that
visualizes existing Active Directory structures as an interactive graph and
audits them against the A-G-DL principle and configurable naming conventions.
It never writes to Active Directory, and the actual permission grants (the "P"
of AGDLP — resource ACLs) are permanently out of scope.

### Added
- **Interactive graph view** — AD-centered layout rendering object nesting as an
  interactive graph (WebView2 + vendored Cytoscape.js), with drag, pan,
  pointer-anchored zoom, and lazy expansion of the membership frontier.
- **Live LDAP provider** — read-only connection in the logged-on user's security
  context (Integrated Authentication); no credential handling, no stored secrets.
- **Demo mode** (`--demo`) — full functionality against a bundled fictional
  dataset; no Active Directory required.
- **Entry filter** — pick any base OU or group as the graph root.
- **Detail panel** — object attributes restricted to an explicit attribute
  whitelist (the privacy baseline).
- **Rule engine** — nesting-matrix and naming-convention checks with
  traffic-light badges, plus circular-nesting and empty-group detection;
  cycle-safe transitive membership traversal that always terminates.
- **Configurable rulesets** — a single JSONC rule file replaces the built-in
  strict-AGDLP default outright (no merging); commented examples, including a
  published copy of the default, ship in `examples/rulesets/`.
- **Settings editor** — edit the nesting matrix, naming rules, and ignore list
  with live preview and ruleset import/export.
- **Portable distribution** — self-contained single-file `win-x64` `.zip` (the
  .NET 8 runtime is bundled; no separate install). Integrity via a published
  SHA256 hash and a GitHub build provenance attestation.

### Security
- Read-only by construction: there is no code path that writes to Active
  Directory anywhere in the product.

[0.3.1]: https://github.com/Atrono/GroupWeaver/releases/tag/v0.3.1
[0.3.0]: https://github.com/Atrono/GroupWeaver/releases/tag/v0.3.0
[0.2.1]: https://github.com/Atrono/GroupWeaver/releases/tag/v0.2.1
[0.2.0]: https://github.com/Atrono/GroupWeaver/releases/tag/v0.2
[0.1.0]: https://github.com/Atrono/GroupWeaver/releases/tag/v0.1
