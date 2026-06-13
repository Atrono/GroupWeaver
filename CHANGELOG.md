# Changelog

All notable changes to GroupWeaver are documented here. The format is based on
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and this project
adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

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

[0.1.0]: https://github.com/Atrono/GroupWeaver/releases/tag/v0.1
