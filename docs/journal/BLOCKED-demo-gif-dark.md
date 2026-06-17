# BLOCKED — #78 dark-theme demo GIF (recorder click beat)

**Date:** 2026-06-17 (session 20). **Issue:** #78 "Refresh the demo GIF under the
dark theme." **Status:** blocked on the recorder's WebView click injection; #78 stays
OPEN. Invoked the CLAUDE.md stuck rule after 2 distinct fix approaches on a subsystem
the lab rules explicitly flag as fragile (`.claude/rules/lab-environment.md`,
"Windowed-smoke driving" / WebView click injection).

## Symptom

`pwsh tools/record-demo-gif.ps1` fails at **beat 3 (root-node click)**:
`root node click never populated the detail panel` after 4 retries. Everything
before it succeeds — demo-mode chosen via UIA (off camera), root picker filtered to
`DL_FS-Finance_RW`, `Load` invoked, `Chrome_RenderWidgetHostHWND` detected, graph
rendered. The posted `WM_LBUTTONDOWN`/`WM_LBUTTONUP` to the Chromium child does **not**
register as a cytoscape node tap, so the Avalonia detail panel stays
"Click a node to inspect it." Beats 4–5 (lazy-expand, zoom) never run.

## What WORKS (verified from captured frames in `artifacts/ui/gif-frames/`)

- **Dark theme renders correctly.** `frame_001` (root picker) is dark; `probe.png`
  shows the dark graph — rust `DL_FS-Finance_RW` root node, `GG_Finance_Staff` external
  child, findings panel, legend. So the theme-refresh GOAL of #78 is achievable in the
  app; only the scripted animation capture is blocked.
- **Demo-mode-only is honoured** (no real/lab identity in any frame — the recorder
  UIA-clicks "Demo mode" off camera and never invokes "Connect to domain").

## Attempts

1. **Plain re-run** (07:11) — failed identically.
2. **Defeated the documented PrintWindow one-compositor-batch lag.** `frame_003` in run 1
   captured a STALE root-picker frame at the "graph rendered" checkpoint, proving the lag
   (lab-environment.md: "the first capture after a mutation returns the previous frame;
   capture-and-discard then capture"). Added `Capture-Live` (capture twice, discard the
   first) and routed `Save-Frame`/`Save-Probe` through it. After the fix `probe.png` is
   reliably fresh (shows the rendered graph), **but the click still does not select** —
   staleness was real but is NOT the click cause. (This fix is KEPT — it is a correct,
   independent hardening the eventual #78 fix will want.)

## Ruled out

- **DPI coordinate mismatch.** Display is at 100% scale (`LOGPIXELSX=96`); `probe.png` is
  1946×1271 (physical == logical), so `Send-CanvasClick`'s capture→child-client math is
  1:1 and correct. (The lab rule's ">100% DPI" note does not apply on the box's current
  scale.)
- **Theme/render** — confirmed working in dark (above).

## Best hypotheses for the next session

- **Tap interpreted as a drag/pan.** Chromium may synthesize a mousemove between DOWN and
  UP (the recorder's own comment at `Send-CanvasClick` warns of this); the hover-park
  mitigation may be insufficient on the current build. Try `SetForegroundWindow` /
  `WM_MOUSEACTIVATE` before the click, a different DOWN/UP cadence, or — more robustly —
  confirm selection through the cytoscape bridge (`invokeCSharpAction` / a `cy` query)
  instead of pixel-hunting the detail panel.
- **Click lands on the legend overlay, not a node.** The top-left legend has a rust
  "Domain-local group" swatch; `Find-NodeBlob` returns the densest rust blob, which could
  be the legend icon (an HTML overlay above the canvas) rather than the root node. Try
  excluding the legend rect from the canvas blob search, or click the node's geometric
  centre reported by `cy`.
- **Regression window:** this recorder worked for the M2 GIF (session 7, 2026-06-12) and
  broke by/after session 19's Light→Dark theme flip. The styling change may have shifted
  node geometry, overlay z-order, or canvas hit-testing — diff the rendered graph layout
  vs the 2026-06-12 light run.

## Decision

#78's user-facing goal (dark-theme public media) is **already met** by the committed
dark stills in the README hero — `docs/media/graph-explore.png` and `gap-analysis.png`
(session 19). The animated GIF is a nice-to-have and is currently unreferenced by the
README. The stale light `docs/media/m2-explore.gif` is left untouched (unreferenced; a
future PR may drop or replace it once the recorder is fixed). #78 stays OPEN with this
note as the recovery point.
