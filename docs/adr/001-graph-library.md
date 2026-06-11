# ADR-001: Graph library — Cytoscape.js in the official Avalonia WebView

**Status:** Proposed (flips to Accepted at PR merge) · **Date:** 2026-06-11
**Decides:** PLANNING.md O3 / E5 (open graph-library choice) · **Phase:** 0, AP 0.3

## Context

GroupWeaver renders AD structures as an interactive graph (concentric, "AD in the
center"; drag/zoom/lazy-expand; node colors per type) inside an Avalonia desktop
app. The spike target is 5,000 nodes without freezing, including a bidirectional
event roundtrip (node click → .NET detail panel; .NET → JS expand). Constraints:
read-only product, portable .zip distribution with WebView2 Evergreen Runtime as a
documented prerequisite (O4), lab/server boxes may lack a GPU, and Core/Providers
must stay UI-free.

Key facts (verified June 2026):

- Avalonia's official WebView went MIT/open-source in March 2026 (formerly the paid
  "Accelerate" control). `Avalonia.Controls.WebView` **11.4.0** is the
  Avalonia-11-compatible line; 11.3.x package versions are deprecated commercial
  builds — pin exactly. `NativeWebView` provides Navigate/NavigateToString,
  `InvokeScript(string):Task<string?>`, `WebMessageReceived` (JS calls the injected
  global `invokeCSharpAction`), `WebResourceRequested`, `EnvironmentRequested`, and
  an escape hatch `TryGetPlatformHandle()` → `IWindowsWebView2PlatformHandle` → raw
  `ICoreWebView2` COM (PostWebMessageAsJson, SetVirtualHostNameToFolderMapping).
- Community WebView wrappers are dead ends: WebView.Avalonia stale since 2023;
  WebView2.Avalonia preview-only, never stable; WebViewControl-Avalonia is CEF and
  bundles full Chromium — wrong for a portable .zip on a WebView2 baseline.
- Cytoscape.js handles 5k nodes on its canvas renderer with known perf levers. Its
  WebGL renderer crashes rather than falls back where WebGL2 is unavailable
  (GPU-less lab/server boxes) — canvas is the safe default.
- Native .NET graph controls collapse at this scale or are dormant (see
  Alternatives); the only credible native path is MSAGL layout + a hand-rolled
  Skia renderer — multi-week effort with permanent ownership of interaction code.

## Decision

**Cytoscape.js, pinned 3.34.0 and vendored** (THIRD-PARTY-NOTICES per AP 1.1),
rendered in the **official Avalonia WebView (`Avalonia.Controls.WebView` 11.4.0,
MIT; WebView2 backend on Windows)**.

Implementation guardrails:

1. **Canvas renderer only** for v0.1; do not enable Cytoscape's WebGL path (hard
   crash without WebGL2, no fallback).
2. **Perf levers at 5k nodes:** preset layout with .NET-precomputed radial
   positions (BFS depth = ring, AD in the center), haystack edges, no arrowheads
   at overview zoom, `min-zoomed-font-size` labels, `pixelRatio: 1`,
   `hideEdgesOnViewport`, mutations inside `cy.batch`.
3. **Renderer behind a thin interface:** graph JSON in, click/expand events out.
   GraphBuilder/RuleEngine never reference the WebView; the MSAGL fallback stays
   swappable without touching Core/Providers.
4. **Data transfer:** bulk dataset via chunked `InvokeScript` batches — measured
   cheap in the spike (~600 KB in 17 chunks, 126–168 ms). `WebResourceRequested`
   in 11.4.0 exposes only the request (no response-providing API), so fetch-based
   serving is not possible; COM `PostWebMessageAsJson`/`SetVirtualHostNameToFolderMapping`
   via the platform-handle escape hatch remains the escalation if profiling demands.
5. **Airspace:** the WebView is a native child HWND — Avalonia cannot draw over
   the graph region. UI design places the detail panel, dialogs, and overlays
   *beside* the graph, never above it.
6. **Headless verification:** `NativeWebView` does not render under
   Avalonia.Headless. The graph layer is verified by loading the same web bundle
   under Playwright/headless Chromium (a `bridge.js` seam shims
   `invokeCSharpAction`); native chrome via Avalonia.Headless — the two-part
   verification already mandated by CLAUDE.md and PLANNING.md §9.
7. WebView2 Evergreen Runtime remains a documented system requirement (O4) with a
   startup check + download link (AP 2.1).

## Consequences

- Mature, MIT-licensed graph interaction (drag, zoom, lazy-expand, styling) for
  free; UI effort goes into AD semantics, not rendering plumbing.
- Two-runtime architecture: a JS bundle and a .NET↔JS bridge become permanent
  parts of the app; debugging spans both sides.
- Vendored, pinned Cytoscape (3.34.0) and pinned WebView package (exactly 11.4.0)
  avoid supply-chain drift; upgrades are deliberate, reviewed events.
- Graph-layer UI tests run in a browser, not in the app — the bridge seam is the
  contract that keeps this honest.
- Exit strategy if the WebView path fails (perf, bridge instability): MSAGL layout
  engine (`AutomaticGraphLayout` 1.1.12, netstandard2.0; `MdsLayoutSettings` at
  5k) + custom Avalonia `ICustomDrawOperation`/SkiaSharp rendering with quadtree
  hit-testing. Multi-week effort; guardrail 3 caps the blast radius.
- Second contingency, hosting-only: hand-rolled `NativeControlHost` +
  `Microsoft.Web.WebView2` SDK (bounds sync, focus, DPI, reparenting all manual)
  — feasible, but buys nothing while the official control is MIT.

## Alternatives considered

- **AvaloniaGraphControl (native):** per-node Avalonia controls collapse around
  hundreds of nodes; feature-frozen. Rejected.
- **GraphShape (native):** WPF-only, dormant since 2021. Rejected.
- **MSAGL + custom Skia renderer (native):** only credible native option, but
  MSAGL unreleased since 2021 and we would own the entire interaction layer;
  Microsoft's own MSAGL team went browser/WebGL (msagljs + deck.gl) for large
  interactive graphs. Kept as documented exit strategy, not the primary path.
- **Community WebView wrappers:** WebView.Avalonia (stale 2023), WebView2.Avalonia
  (preview-only), WebViewControl-Avalonia (CEF, ships full Chromium). Rejected.
- **Hand-rolled NativeControlHost + WebView2 SDK:** feasible but redundant since
  the official control went MIT. Recorded as second contingency only.
- **Sigma.js (O3 candidate):** WebGL-first — conflicts with the GPU-less
  lab/server baseline; Cytoscape's canvas renderer fits better. Rejected.

## Spike evidence

**AP 0.1** (`spikes/GraphSpike`, self-driving Avalonia app, all criteria PASS in
the Avalonia-hosted WebView — no standalone-browser numbers): 5,000 nodes +
6,499 edges; dataset (~600 KB JSON, 17 InvokeScript chunks) transferred in
126–168 ms, transfer + parse + cy init + first render ~1.6 s; node click → .NET
detail panel in 8–9 ms; .NET → JS expand (+25 children) confirmed in 20–26 ms.
Pan/zoom FPS, measured in two environments on the same box (the GPU driver was
installed mid-spike — both full result files are preserved in the spike dir):

| Interaction path | Software rendering (no GPU driver) | Intel UHD 620 |
|---|---|---|
| Human-gesture (DOM drag + wheel) | avg 37.5 / min 4.6 | avg 58.7 / min 12.0 |
| Programmatic `cy.viewport()` loop (worst case) | avg 4.9 / min 1.9 | avg 16.6 / min 4.6 |

The software-rendered run is the relevant floor for the target audience (RDP
sessions, servers, GPU-less VMs) — usable there, comfortable with any GPU.
WebGL2 is available with a driver present but stays unused (guardrail 1).

Pitfalls the spike surfaced (binding for Phase 2 implementation):

- The programmatic-loop numbers are NOT user experience: cytoscape's
  `hideEdgesOnViewport`/`textureOnViewport` only engage on input-handler gesture
  flags (verified in 3.34.0 source) — any future perf harness must drive
  DOM-level events, not `cy.zoom()`/`cy.pan()`.
- The exe needs an `app.manifest` with `<supportedOS>` or `NativeControlHost`
  cannot create the child window.
- `invokeCSharpAction` is injected asynchronously — the JS bridge queues
  outbound messages until it appears.
- file:// pages run in an opaque origin, muting `window.onerror` to
  "Script error." — error routing to .NET works, but the product should serve
  the bundle from a non-opaque origin for usable stack traces.

**AP 0.2** (`spikes/LdapSpike`): paged `DirectorySearcher` under Integrated Auth
loads the 194-object lab OU in ~100 ms (paging verified across 4 pages);
load-once-then-walk makes full transitive member resolution of all 40 groups
~1 ms in memory; the seeded circular nesting (A→B→A) terminates and is reported
from both entry points.
