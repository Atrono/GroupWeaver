# ADR-004: Graph rendering pipeline — rings, wire contract, renderer seam

**Status:** Accepted · **Date:** 2026-06-12
**Refines:** ADR-001 (Cytoscape in Avalonia WebView) for AP 2.2 · **Phase:** 2

## Context

ADR-001 fixed the stack (vendored Cytoscape.js 3.34.0, canvas renderer only,
`Avalonia.Controls.WebView` exactly 11.4.0, preset layout with .NET-precomputed
positions) but left the pipeline contracts open: ring semantics, the no-overlap
geometry, the JSON wire shape, the renderer interface, the page origin, and how
the graph layer is verified. AP 2.3/2.4/3.4 build on these; they belong on record.

## Decision

1. **Rings = DN-containment depth, refined by kind sub-rings.** Ring key =
   `(relativeDepth, kindRank)`; depth = unescaped-comma RDN components below the
   root DN (escape-aware splitter `DnPath`, all DN comparisons via `Dn.Comparer`);
   kindRank OU=0, DL=1, UG=2, GG=3, Computer=4, User=5; root alone at ring 0.
   Every edge endpoint missing from the snapshot is materialized as an
   `External` node on a dedicated outermost ring — edges are never dropped.
2. **Two drawn edge sets.** Membership (`snapshot.Edges`, read into a local
   **exactly once** per build — pinned perf contract): bezier, arrowhead, drawn
   orientation **member → group** ("is member of", the A→G→DL reading; the
   semantic direction `ParentDn`=group stays in Core, the App wire mapper flips).
   Containment: one dashed, arrowless edge per non-root in-scope node from its
   nearest in-snapshot DN ancestor. An HTML legend overlay explains arrow + colors.
   Bezier + arrowheads stay on at ~200 nodes; haystack/no-arrowheads are 5k-node
   levers (ADR-001), and bezier is required to keep the seeded A↔B cycle legible.
3. **No-overlap geometry, proven.** Preset layout from `GraphBuilder` (Core,
   UI-free). With node diameter D=44, margin m=16, ring gap g=150:
   `r_k = max(r_{k-1}+g, (D+m)/(2·sin(π/n_k)))` (exact chord formula), angles
   uniform with per-ring stagger, deterministic ordering (nearest-ancestor angle,
   then DN). Same-ring chord ≥ D+m, cross-ring ≥ g ⇒ min center distance ≥ ~59.7.
   Pinned twice: xUnit geometry test (model space) and Playwright assert on
   rendered positions (render space).
4. **Wire contract.** Core model (`GraphModel`/`GraphNode`/`GraphEdge`, no JSON
   attributes); App-side `GraphJson` uses **reflection** System.Text.Json,
   camelCase: node `{id: DN verbatim, label, kind: enum name verbatim, x, y}` +
   `root:true` on the root only; edge `{id: m0…/c0…, s, t, rel: 'member'|'contains'}`
   with membership `s:=member, t:=group`. Chunking in .NET (≤500 nodes / ≤1000
   edges per `graphChunk`, trailing `graphCommit`); JS is a dumb accumulator.
   Source-gen STJ only if trimming ever lands (CI publishes untrimmed).
5. **Renderer seam.** `IGraphRenderer { Control? View; ShowGraphAsync(GraphModel);
   events NodeClicked, NodeExpandRequested, RendererError }`. `NodeExpandRequested`
   ships now (issue names click/expand as the interface; the VM ignores it until
   AP 2.3). `CytoscapeGraphRenderer` owns the WebView lifecycle (navigate on first
   attach, ready-TCS with 60 s timeout → `RendererError`, never a hang).
   `WorkspaceViewModel` takes an optional trailing `Func<IGraphRenderer>?` factory —
   `null` (all existing tests) keeps the placeholder; the real factory is passed
   only in the composition root. JS selectors: `cy.getElementById` **only** —
   `cy.$('#'+dn)` silently fails on every comma-containing DN.
6. **file:// origin for v0.1** — byte-identical to the spike setup; Playwright
   verifies the same origin. Opaque-origin error muting is mitigated by
   per-handler try/catch in graph.js + the zero-jsError Playwright assertion.
   Escalation if blind JS errors ever bite (≤3 attempts, stuck rule):
   `TryGetPlatformHandle` → `ICoreWebView2.SetVirtualHostNameToFolderMapping`.
7. **Demo-only graph dumps.** `--demo --dump-graph <path>` is the permanent
   fixture/diagnostic CLI; `--dump-graph` **without** `--demo` exits 64 — live-AD
   structure must never reach artifacts (public-media rule), pinned by process test.

## Consequences

- The Playwright harness (`tests/graph-bundle`, driven by `tools/test-graph-bundle.ps1`
  locally and in CI) verifies the literal shipped bundle against the literal
  GraphBuilder output — palette parity C#/JS is pinned solely there.
- Labels are hidden at fit zoom by design (`min-zoomed-font-size`); checklist A
  directs judgment to the focus screenshot, not the overview.
- Any future switch of membership edges to straight/haystack merges antiparallel
  cycle pairs into one ambiguous line — `graph-cycle.png` is the permanent guard.

## Rejected alternatives

BFS-over-membership rings (not total: orphan users/OU roots unreachable);
JS-side `concentric` layout (violates ADR-001 precomputed-preset guardrail);
parent→member arrows (reads against the AGDLP chain); straight edges (cycle pair
merges); source-gen STJ now (no trimming); virtual-host origin now (speculative
COM interop); `@playwright/test` runner (extra surface for one sequential spec);
xUnit-dump fixture (cross-test ordering); edge ids in Core (wire concern).
