# ADR-013: Export pipeline (violation report + graph image)

**Status:** Accepted · **Date:** 2026-06-13
**Decides:** PLANNING.md AP 4.1 (v0.2 export) + issue #56 · **Phase:** 4

## Context

AP 4.1 (issue #56, v0.2) adds export. Two surfaces: a violation report (CSV +
a self-contained HTML report) of the current `RuleReport`, and a graph image.
Source tokens (DN, name, message) are untrusted directory/file content
(`.claude/rules/rule-engine.md`, the #45 plain-text rule), so any text output
must escape them, never inject. The product is read-only toward AD — export
writes only local files the user picks via a save dialog.

## Decision

1. **Report-first, PNG-only, SVG deferred.** Report export is pure Core,
   fully TDD-testable, shipped first (the higher-value self-contained half).
   The graph image uses `cy.png()`, which is on the already-vendored Cytoscape
   3.34.0 core — zero new dependencies. SVG is deferred to its own work package
   to avoid vendoring `cytoscape-svg`+`canvas2svg` across both web-bundle
   integrity lists (`tools/pack-release.ps1`, `.github/workflows/ci.yml`) and
   the THIRD-PARTY-NOTICES upstream-SHA provenance discipline (#52).

2. **Core exporter takes a `ResolveName` delegate + a `ReportHeader` record**
   (`src/Core/Export/ViolationReportExporter.cs`, `ToCsv`/`ToHtml` → `string`).
   Core stays App-free; the VM passes a snapshot-name closure mirroring
   `OnReportChanged` (`dn => Snapshot.TryGetObject(dn, out var o) ? o.Name : dn`).
   Output is deterministic (timestamp injected via `ReportHeader.GeneratedAt`),
   iterates `report.Violations` in canonical order verbatim, and appends
   `UncheckedDns` as a separate section.

3. **#45 escaping.** CSV: a formula-injection guard (leading `= + - @` / TAB /
   CR / LF → prefix `'`) applied BEFORE RFC-4180 quoting (so the `'` lands
   inside the quotes), `CultureInfo.InvariantCulture` throughout. HTML:
   `System.Net.WebUtility.HtmlEncode` of every untrusted token, tokens emitted
   ONLY in element text content (`<td>`/`<li>`), never in any attribute (so
   `HtmlEncode` leaving `'` literal is safe), severity colors via class-keyed
   CSS (palette `#D13438`/`#F7A30B`/`#4FA3E3`), no raw interpolation, no
   external CSS/JS/font/image refs (a self-contained file that opens offline).

4. **Wire contract (graph image).** Outbound `{type:'exportPng',scale,full,bg}`
   (defaults `full:false` = viewport, `scale:2`, `bg:'#1b1f27'`); inbound
   `{type:'pngExported',data:<base64>,width,height}`. `IGraphRenderer.ExportPngAsync
   → Task<byte[]?>`, null on timeout/error, mirroring `FocusAsync` single-flight
   + 60 s bounded-wait + the RendererError-and-return-normally policy. A valid
   `pngExported` MUST parse to `PngExportedMessage` (an `UnknownMessage` would
   trip `RendererError`). The `graph.js` handler sits inside the `try` before
   `default`, with a `cy === null` jsError guard, so the Playwright zero-jsError
   audit still passes.

5. **Read-only invariant.** Export writes ONLY the path returned by the user's
   `SaveFilePicker`; the AD provider is never touched. A new `IExportFileDialogs`
   seam (`src/App/Export/`) — not the jsonc-hardcoded `IRulesetFileDialogs`.

6. **Report-export gate = `Snapshot is not null`** (not `HasViolations`): the
   unexpanded-areas appendix is a real exportable artifact even in the
   all-clear-but-unchecked state.

## Consequences

- v0.2 ships report value (CSV + HTML) even if the PNG round-trip slips; the two
  halves are independently mergeable, sharing only the file-dialog seam.
- `full:false` exports the viewport (what you see); large-graph base64 inflation
  (~33%, one un-chunked `WebMessageReceived` message) is bounded by the
  viewport-only default, a capped scale, and the 60 s wait.
- No new vendored blob — integrity lists and THIRD-PARTY-NOTICES untouched.
- SVG export becomes a future issue when feedback asks for it.

## Rejected alternatives

SVG via vendored `cytoscape-svg` (provenance cost across two integrity lists —
deferred); overloading `IRulesetFileDialogs` (jsonc-hardcoded, wrong window);
exporting from the VM `Violations` projection (drops `RuleId` and the full
`Dns`); a synchronous `cy.png` return (the bridge has no sync path); `full:true`
default (multi-MB raster on large lazily-expanded graphs); gating report export
on `HasViolations` (would hide the unexpanded-areas appendix).
