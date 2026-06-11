# GraphSpike results (AP 0.1, Phase 0)

Run: 2026-06-11 23:49:34 (local). Overall: **PASS**.

## Machine context

- OS: Microsoft Windows 10.0.20348 (X64)
- CPU: 8 logical processors; GPU: Intel UHD Graphics 620, driver 31.0.101.2141 (installed 2026-06-11 mid-spike — the first run had no driver; its software-rendered numbers are preserved in RESULTS-software-rendering.md)
- .NET: .NET 8.0.27
- Stack: Avalonia 11.3.17 + Avalonia.Controls.WebView 11.4.0 (WebView2 backend) + cytoscape 3.34.0 (canvas renderer, preset layout, haystack edges, no arrows, pixelRatio 1, min-zoomed-font-size 12, hideEdgesOnViewport true, textureOnViewport measured both off and on)
- WebView UA: Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/149.0.0.0 Safari/537.36 Edg/149.0.0.0

## Measurements (verbatim run output)

```
Dataset: 5000 nodes, 6499 edges, payload 609,387 bytes in 17 InvokeScript chunks
Transfer (chunks only): 168 ms
Transfer + parse + cy init + first render (.NET wall clock): 1573 ms
  JS-side: chunk span 107 ms, element build 5 ms, cy init 861 ms, first render 1399 ms
Rendered: 5000 nodes / 6499 edges (expected 5000 / 6499)
WebGL2 available in WebView2 (not used; canvas renderer only): yes
Bridge ping/pong roundtrip (5x): avg 6.2 ms, min 0.5 ms, max 28.5 ms
Pan/zoom FPS (textureOnViewport:false) over 3016 ms (50 frames): avg 16.6, min 4.6 (worst frame 217 ms)
Pan/zoom FPS (textureOnViewport:true) over 3015 ms (36 frames): avg 11.9, min 4.6 (worst frame 216 ms)
Pan/zoom FPS (human-gesture path: DOM drag + wheel, textureOnViewport:true) over 3016 ms (177 frames): avg 58.7, min 12.0 (worst frame 83 ms)
Node click (synthetic tap on n42) -> .NET received in 8.9 ms; detail panel updated
Expand n42 (+25 children): confirmed in 20.3 ms, node count now 5025 (expected 5025), edges 6524, JS add 18.2 ms
JS error capture: deliberate window.onerror arrived in .NET log (message muted to 'Script error.' by file:// opaque-origin policy); total JS errors this run: 1

PASS  1. Render 5,000 nodes + 6,499 edges in Avalonia-hosted WebView
PASS  2. Pan/zoom usable (avg FPS >= 10 in at least one measured interaction path)
PASS  3a. JS -> .NET node click + detail panel + latency
PASS  3b. .NET -> JS expand command + confirmation (5,025 nodes)
PASS  4. Dataset transfer + load + initial render measured
PASS  5. JS console/window.onerror routed into .NET log
OVERALL: PASS (total spike runtime 13.4 s)
```

## Environment comparison (GPU driver installed mid-spike)

The GPU driver landed between the first full run and this one, giving both ends of
the spectrum on identical hardware (full baseline: RESULTS-software-rendering.md):

| Metric | Software rendering (no driver) | Intel UHD 620 (driver) |
|---|---|---|
| WebGL2 available | not measured (driver absent; Chromium had no GL) | yes (unused — canvas only) |
| FPS, programmatic `cy.viewport()` loop | avg 4.9 / min 1.9 | avg 16.6 / min 4.6 |
| FPS, human-gesture path (DOM drag + wheel) | avg 37.5 / min 4.6 | avg 58.7 / min 12.0 |
| Transfer + parse + cy init + first render | 1,557 ms | 1,573 ms |
| Click → .NET / expand roundtrip | 8.3 ms / 25.6 ms | 8.9 ms / 20.3 ms |

Both environments pass all criteria; the software-rendered run is the relevant
worst case for the target audience (RDP sessions, servers, VMs without GPU).

## Findings

- `WebResourceRequested` in Avalonia.Controls.WebView 11.4.0 exposes only the request (`Uri`, `Method`, `Headers` - headers mutable via `TrySet`/`TryRemove`); there is **no response-providing API**, so serving `graph.json` via interception is unworkable. Fallback used: chunked `InvokeScript` batches (500 nodes / 1,000 edges per call) - see numbers above.
- Navigation: file:// URL of the on-disk bundle; NavigationCompleted IsSuccess=True. `invokeCSharpAction` is injected by the host; the bridge queues outbound messages until it appears (needed in practice - see bridge.js).
- Avalonia's `NativeControlHost` (which hosts the WebView2 child HWND) throws `Unable to create child window for native control host` unless the exe carries an app.manifest with a `<supportedOS>` Windows 10 entry - added `app.manifest` to fix.
- file:// pages put every script in an opaque origin, so `window.onerror` details are muted to `Script error.` - the error still reaches .NET through the bridge, but messages are useless. The product should serve its bundle from a non-opaque origin (e.g. NavigateToString with baseUri, a custom scheme, or a loopback listener) to get real stack traces.
- **Benchmarking pitfall (key ADR-001 input):** cytoscape's `hideEdgesOnViewport`/`textureOnViewport` only engage while its *input handlers* set gesture flags (`pinching || hoverData.dragging || swipePanning || data.wheelZooming`; hideEdges also accepts `cy.animated()`) - verified in the 3.34.0 source. A programmatic `cy.zoom()`/`cy.pan()`/`cy.viewport()` loop therefore measures the *full-redraw* cost and bypasses both optimizations, which is why textureOnViewport made no difference in the synthetic loop. FPS was measured three ways: (a) `cy.viewport()` loop with textureOnViewport:false, (b) same loop with textureOnViewport:true (expected ~equal to (a) per the above), (c) synthetic DOM mouse-drag + wheel events on the container - the literal human input path, where both optimizations engage. The PASS/FAIL verdict for 'interaction stays usable' is based on the best measured path; all numbers reported.
- Cosmetic: Chromium logs `Failed to unregister class Chrome_WidgetWin_0` during WebView2 teardown at shutdown; harmless, after results are written.
- JS errors captured during the run (1):
  - [window.onerror] Script error. :0:0
