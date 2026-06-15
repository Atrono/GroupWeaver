# Changelog

All notable changes to GroupWeaver are documented here. The format is based on
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and this project
adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

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

[0.2.0]: https://github.com/Atrono/GroupWeaver/releases/tag/v0.2
[0.1.0]: https://github.com/Atrono/GroupWeaver/releases/tag/v0.1
