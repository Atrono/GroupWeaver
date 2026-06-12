# UI checklist — GroupWeaver

Judged by the `ui-verifier` agent on every UI change (CLAUDE.md DoD step 2).
Two parts: (A) graph layer via Playwright/headless Chromium, (B) native chrome
via Avalonia.Headless. Screenshots go to `artifacts/ui/` (gitignored).
Section A is still a stub until the graph ships (AP 2.2); section B covers the
shipped AP 2.1 shell, with future panels parked under "Future".

## A. Graph layer (browser bundle)

- [ ] Node types visually distinct (color/shape): User, GG, DL, UG, OU, Computer, extern/unresolvable
- [ ] Nesting edges legible, direction unambiguous
- [ ] Concentric layout sane: root centered, no node overlap at 200 demo nodes
- [ ] Drag/zoom/lazy-expand respond; expanded vs. collapsed state distinguishable
- [ ] Severity colors (red/yellow/info) and roll-up badge ("⚠ n below") readable at default zoom
- [ ] Label contrast sufficient on every node color (no dark-on-dark)

## B. Native chrome (Avalonia)

Screenshot fixture: `tests/GroupWeaver.App.Tests/Screenshots/ShellScreenshotTests.cs`
renders every shipped shell state via real Skia (real DemoProvider, real views) to
`artifacts/ui/<view>-<W>x<H>.png` at **both** 1280×720 and 1920×1080:
`connection-idle`, `connection-error`, `rootpicker-demo`, `workspace-demo`,
`workspace-webview2-missing` — 10 PNGs per run.

Evidence tags: **[S:name]** = judge from the `name-*.png` pair; **[I]** = interactive
or transient — cannot be evidenced by a static frame; covered by headless tests
(`WebView2BannerTests`, `ConnectionFlowTests`, `RootPickerTests`, `WorkspaceShellTests`)
and spot-checked by hand when the step changes.

### Connect step

- [ ] Both connect paths reachable: "Connect to domain" and "Demo mode" buttons present, enabled, clearly separated [S:connection-idle]
- [ ] Integrated-auth context line shows `DOMAIN\user` under the live button; no credential fields anywhere on the step [S:connection-idle]
- [ ] Demo hint line under the demo button explains "no domain needed" [S:connection-idle]
- [ ] Inline error block: red, wraps without clipping, legible on the theme background; live-path message carries the try-Demo-mode hint on its own line [S:connection-error]
- [ ] Error block hidden again once the message clears [I]
- [ ] Busy state: indeterminate progress bar while a connect is in flight; both buttons disabled until it resolves [I]

### Root picker

- [ ] Kind badges (OU/GG/DL/UG) visually distinguishable at a glance; badge label readable on every badge color (no dark-on-dark) [S:rootpicker-demo]
- [ ] Candidate rows: name prominent, DN below it dimmed but legible; long DNs ellipsize instead of overflowing the row [S:rootpicker-demo]
- [ ] Mandatory selection gating — selected state: one row visibly highlighted and the Load button enabled [S:rootpicker-demo]
- [ ] Mandatory selection gating — unselected state: Load visibly disabled until a candidate is picked [I]
- [ ] Filter box narrows the list live (name/SAM/DN, case-insensitive); clearing restores the full list [I]
- [ ] Virtualization at scale: thousands of candidates scroll smoothly without realizing every container [I]
- [ ] Back returns to a fresh Connect step (no stale error/in-flight state) [I]

### Workspace

- [ ] GraphHost seam placeholder centered in the graph region: "Graph view arrives in AP 2.2" plus the chosen root DN [S:workspace-demo]
- [ ] DetailPanelRegion sits BESIDE GraphHost in its own right-hand column with a visible separator — never overlapping the graph region (ADR-001 airspace) [S:workspace-demo]
- [ ] Status bar below the graph region: connection summary ("connected, n groups loaded — …") left, "root: <DN>" right, single dimmed line, no clipping [S:workspace-demo]
- [ ] Nothing floats, pops up, or layers over GraphHost; anything modal is its own Window [I — design rule, re-check on every workspace change]

### WebView2 runtime (missing-runtime UX)

- [ ] Banner docked above the step content when the probe reports missing: amber tint, wrapped text, never a dialog, never blocks [S:workspace-webview2-missing]
- [ ] Banner present on ALL steps (Connect, PickRoot, Workspace) while missing [S:workspace-webview2-missing shows Workspace; Connect/PickRoot pinned by WebView2BannerTests — spot-check on banner changes]
- [ ] Download link present and recognizable as a link (underlined, link color) in BOTH the banner and the placeholder [S:workspace-webview2-missing]
- [ ] GraphHost placeholder switches to the missing-runtime variant (headline + explanation + link) instead of the AP 2.2 teaser [S:workspace-webview2-missing]
- [ ] No banner anywhere when the runtime is present [S:workspace-demo]
- [ ] Missing runtime never crashes or blocks any step; banner is informational only [I — pinned by WebView2BannerTests]

### Global

- [ ] No clipped or overlapping controls at 1280×720 and 1920×1080 [S: every pair — compare both sizes of each view]
- [ ] Dimmed/secondary text (DNs, hints, status bar) still legible at both sizes [S: every pair]
- [ ] Centered layouts (Connect card, picker column, placeholders) stay centered, not stretched or stuck to a corner, at 1920×1080 [S: every -1920x1080]

### Future (not shipped yet — judge when the owning AP lands)

- [ ] Detail panel (AP 2.5): whitelist attributes only; long DNs truncated with full value available
- [ ] Settings/rule editor (Phase 3): live preview updates; import/export present
- [ ] Violation sidebar (AP 3.4): list with jump-to-node; "unexpanded areas are unchecked" notice visible
