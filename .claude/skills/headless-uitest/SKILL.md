---
name: headless-uitest
description: How to render and verify GroupWeaver UI headlessly - the graph browser bundle via Playwright/headless Chromium and the native Avalonia chrome via Avalonia.Headless, with screenshots judged against docs/ui-checklist.md. Use for any UI change verification (DoD step 2).
---

# Headless UI verification

> Stub - flesh out during Phase 2 when the views exist (CLAUDE.md bootstrap step 4).

Two-part procedure (CLAUDE.md DoD step 2; PLANNING.md §9):

## A. Graph layer (browser bundle)
The graph view is a vendored Cytoscape.js bundle hosted in a WebView2 (pending
ADR-001). Verify the SAME bundle the app ships:
1. Serve/open the bundle standalone with demo-mode data (200-node dataset).
2. Drive it with Playwright + headless Chromium (`npx playwright ...`).
3. Screenshot to `artifacts/ui/graph-*.png`.

## B. Native chrome (Avalonia)
Panels, dialogs, settings via `Avalonia.Headless` test host:
1. Instantiate the view with demo-mode view models.
2. Render to `artifacts/ui/native-*.png`.

## Judging
Read every PNG; evaluate against the matching section of
`docs/ui-checklist.md`. Fix and re-render until pass. Demo-mode data only -
lab-AD content must never appear in artifacts that could go public.

TODO Phase 2: concrete commands, test-host bootstrap code, viewport matrix.
