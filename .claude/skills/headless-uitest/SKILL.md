---
name: headless-uitest
description: How to render and verify GroupWeaver UI headlessly - the graph browser bundle via Playwright/headless Chromium and the native Avalonia chrome via Avalonia.Headless, with screenshots judged against docs/ui-checklist.md. Use for any UI change verification (DoD step 2).
---

# Headless UI verification

Two-part procedure (CLAUDE.md DoD step 2; PLANNING.md §9). **Demo-mode data
only** — lab-AD content must never appear in artifacts that could go public.

## A. Graph layer (browser bundle)

One command, identical locally and in CI:

```
pwsh tools/test-graph-bundle.ps1
```

Pipeline: build `src/App` (Release) if needed → dump the demo graph fixture
(`GroupWeaver.App --demo --dump-graph artifacts/graph-fixtures/demo-graph.json`;
`--dump-graph` **without** `--demo` exits 64 by design — live AD never reaches
artifacts) → `npm ci` in `tests/graph-bundle` → `npx playwright install
chromium` → `node verify.mjs <fixture> artifacts/ui`.

`verify.mjs` loads the LITERAL shipped `src/App/web` bundle on its production
file:// origin and feeds the fixture through the chunked bridge protocol
(≥ 3 `graphChunk` dispatches + `graphCommit`). It pins:

- rendered node/edge counts == fixture counts, with a ≥ 190-node anti-vacuous floor
- preset positions honored exactly (5 sampled DNs incl. a comma-containing DN;
  `cy.getElementById` only — selector strings silently fail on comma DNs)
- O(n²) pairwise min center distance ≥ 44 (the D=44 no-overlap floor, ADR-004 D3)
- C#/JS palette parity for every kind present (≥ 6 of the 7 kinds exercised) —
  the ONLY place palette parity is pinned
- click + dbltap roundtrips byte-identical on a comma DN
- ZERO `jsError` messages across the whole run

Screenshots: `artifacts/ui/graph-overview.png`, `graph-focus.png`,
`graph-cycle.png` (1600×1000).

## B. Native chrome (Avalonia)

Runs as part of `pwsh tools/build.ps1` (or directly: `dotnet test
tests/GroupWeaver.App.Tests`). The fixture is
`tests/GroupWeaver.App.Tests/Screenshots/ShellScreenshotTests.cs`: every shipped
shell state through the REAL pipeline — real DemoProvider, real views, real Skia
rasterization on Avalonia.Headless (`UseHeadlessDrawing = false`) — written to
`artifacts/ui/<view>-<W>x<H>.png` at both 1280×720 and 1920×1080:
`connection-idle`, `connection-error`, `rootpicker-demo`, `rootpicker-demo-tail`,
`workspace-demo`, `workspace-webview2-missing` — 12 PNGs per run.

**Capture-and-discard rule:** the headless compositor renders one committed
batch per render-timer tick, so the first `CaptureRenderedFrame()` after a state
mutation returns the PREVIOUS frame. Capture-and-discard, then capture — no
sleeps, no retries.

**Limit:** `NativeWebView` does not render under Avalonia.Headless — the
workspace PNGs show the GraphHost placeholder. The graph surface itself is part
A's job; live-mount evidence comes from the windowed `--demo` smoke
(`tools/capture-window.ps1`, DPI-aware PrintWindow → `workspace-live-graph.png`).

## Judging

Read every PNG; evaluate against the matching section of
`docs/ui-checklist.md` (section A for `graph-*.png` and
`workspace-live-graph.png`, section B for the shell matrix), honoring each
item's evidence tags. Fix and re-render until pass.
