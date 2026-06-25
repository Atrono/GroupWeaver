---
name: record-demo-media
description: How to refresh the public README demo media after a UI change - the two static graph PNGs (from verify.mjs, copied+renamed) and the demo-mode GIF (from the live windowed recorder), with the colour/timing/DPI gotchas. Use when regenerating docs/media/*.png or m2-explore.gif, re-recording the demo GIF, or refreshing marketing screenshots. Demo mode only.
---

# Refreshing the README demo media

The README references exactly THREE artifacts, produced by TWO different
pipelines. There is NO "refresh all media" script — know which pipeline owns
each. **Demo data only**: lab-AD content (and the connect card, which renders the
live operator identity) must never reach published media.

| README artifact | Pipeline | Source frame |
|---|---|---|
| `docs/media/graph-explore.png` | `verify.mjs` (headless Chromium, [[headless-uitest]] part A) | `artifacts/ui/graph-overview.png` |
| `docs/media/gap-analysis.png` | `verify.mjs` (diff tripwire frame) | `artifacts/ui/graph-diff.png` |
| `docs/media/m2-explore.gif` | `tools/record-demo-gif.ps1` (live windowed app) | its own frames |

The two PNGs are pure-browser renders of the literal `src/App/web` bundle — the
Avalonia app is never on screen for them. Only the GIF drives the running app.

## A. The two static PNGs (copy + rename — the undocumented step)

1. `pwsh tools/test-graph-bundle.ps1` — green. (If you changed UI code, delete
   `src/App/bin/Release/.../GroupWeaver.App.exe` first so it rebuilds; the script
   skips the build when the exe exists.) A red `verify.mjs` = a palette/contrast
   hex drifted between C# tokens and `graph.js` — **fix the drift, don't
   regenerate around a failing assert.**
2. Copy + rename into git-tracked `docs/media/` (`artifacts/` is gitignored, so
   nothing lands automatically — this mapping lives only here and in #108):
   ```
   Copy-Item artifacts/ui/graph-overview.png docs/media/graph-explore.png -Force
   Copy-Item artifacts/ui/graph-diff.png     docs/media/gap-analysis.png  -Force
   ```
   Straight copy at 1600×1000 — there is no crop/resize/PNG-optimization step.
   (The hero is the OVERVIEW frame, not `graph-focus`, since WP-A/ADR-029 — the
   overview-zoom edge-fade turns that frame into a clean kind-shaped constellation
   instead of a hairball, the stronger first impression. `graph-focus` is still the
   zoomed-in, full-edge frame for the `[S:graph-focus]` checklist items.)
3. Read both PNGs; judge vs `docs/ui-checklist.md` (`[S:graph-overview]`/
   `[S:graph-diff]`). If the UI change altered the visual language (a kind colour,
   the diff palette, the overview edge-fade), update the README alt-text too
   (lines 12/18).

## B. The demo GIF (`tools/record-demo-gif.ps1`)

After a UI change, rebuild Debug first (`dotnet build src/App -c Debug` — the
recorder uses the Debug exe and only builds when missing; a stale build is why a
frame can come out light-themed). Then `pwsh tools/record-demo-gif.ps1`.

It launches with NO args, UIA-clicks "Demo mode" **off camera** (capture starts at
beat 2 — never the connect card), drives the 5 beats (root pick → click root →
double-click the External frontier to lazy-expand → wheel-zoom), then ffmpeg
assembles `frame_%03d.png` at **2 fps** (storytelling end-states, not real motion)
with `palettegen`/`paletteuse` into `docs/media/m2-explore.gif` (~960px wide).
Commit the GIF deliberately (it is a tracked binary).

## Gotchas (load-bearing; most are change-fragile)

- **`PW_RENDERFULLCONTENT` (0x2) or the WebView2 child captures BLACK.** Plus
  `SetThreadDpiAwarenessContext(-4)` BEFORE `GetWindowRect` or the >100%-DPI box
  crops the right edge. Both already in `capture-window.ps1` / the shared lib.
- **PrintWindow lags one compositor batch** — the first capture after any mutation
  returns the PREVIOUS frame. `Capture-Live` captures twice, keeps the second. No
  sleeps. (Same root cause as [[headless-uitest]]'s capture-and-discard.)
- **Pixel-hunt uses RENDERED (blended) colours, not the source palette.** On the
  dark canvas a DL root rust `0xA14000` renders `(136,94,69)`; the unresolved
  External frontier renders BLUE `(49,85,115)`; the detail-panel badge keeps the
  EXACT palette `(0xA1,0x40,0x00)`. These constants are hard-coded near the top of
  `record-demo-gif.ps1` **and `capture-motion.ps1`** — if your change moves node
  fills/borders or the dark-theme blend, re-derive them from a probe capture or
  the recorder times out at beat 3/4 (it retries but cannot self-heal a shift).
- **Pan the graph clear of the legend before pixel-hunting** — the larger #87
  legend occludes the 2-node-scope rust root (`Send-CanvasDrag`); don't shrink the
  legend.
- **Runs under Windows PowerShell 5.1** (UIAutomationClient is a .NET-Framework GAC
  assembly pwsh/.NET 8 can't load). Invoke with `pwsh`; the script relaunches
  itself under `powershell.exe`. Once the `Chrome_RenderWidgetHostHWND` child
  exists, UIA on the window returns only Chromium — drive the canvas with posted
  `WM_*` only (see [[lab-environment]] windowed-smoke / cytoscape-wheel).
- **`ffmpeg` must be on PATH** (`tools/bootstrap.ps1` choco-installs it; the script
  self-heals PATH after a same-session install).

## Not README media

`tools/capture-motion.ps1` records real-time interaction *feel* (~15–30 fps mp4
into gitignored `artifacts/ui/motion/`, labelled GPU-vs-software per
[[lab-environment]]) — review evidence for the `[I]` ui-checklist items, NOT a
README artifact. Use it only when judging motion smoothness.
