# UI checklist — GroupWeaver

Judged by the `ui-verifier` agent on every UI change (CLAUDE.md DoD step 2).
Two parts: (A) graph layer via Playwright/headless Chromium, (B) native chrome
via Avalonia.Headless. Screenshots go to `artifacts/ui/` (gitignored).

## A. Graph layer (browser bundle)

Screenshot fixture: `tools/test-graph-bundle.ps1` drives `tests/graph-bundle/verify.mjs`
(Playwright, headless Chromium, 1600×1000) against the LITERAL shipped `src/App/web`
bundle fed the literal GraphBuilder demo dump — it writes `graph-overview.png`,
`graph-focus.png`, `graph-cycle.png` and `graph-expanded.png`.
`workspace-live-graph.png` is the windowed smoke capture of the real app (`--demo`,
DPI-aware PrintWindow via `tools/capture-window.ps1`) — re-take it whenever the
renderer/mount path changes.

Evidence tags: **[S:name]** = judge from `artifacts/ui/<name>.png`; **[P]** = pinned by
a `tests/graph-bundle/verify.mjs` assertion; **[T:Class]** = pinned by the named xUnit
test; **[I]** = interactive/manual — cannot be evidenced by a static frame.

- [ ] Node types visually distinct — all 7 kinds differ by BOTH color and shape: User, GG, DL, UG, OU, Computer, External/unresolvable [S:graph-focus] [P palette parity C#↔JS for every kind]
- [ ] Nesting edges legible, direction unambiguous: membership = bezier with arrowhead drawn member → group (the legend's "is member of" reading); containment = dashed, arrowless [S:graph-focus] [S:graph-cycle — the seeded antiparallel A↔B pair shows as two separated curves with both arrowheads visible, never one merged line]
- [ ] Concentric layout sane: root centered, no node overlap at ~200 demo nodes [S:graph-overview] [T:GraphBuilderGeometryTests] [P rendered pairwise min center distance ≥ 44 + loaded counts match the fixture]
- [ ] Label contrast sufficient: labels sit BELOW the nodes on the dark page background with a dark outline, never on the node fill [S:graph-focus] (labels are hidden at fit zoom BY DESIGN via `min-zoomed-font-size` — judge graph-focus, never the overview)
- [ ] Drag/zoom respond [I — manual spot-check on the windowed `--demo` run; done for AP 2.2 via posted mouse input + frame diffs]
- [ ] Live workspace mount: the real app's WebView shows the rendered graph (in-page legend top-left, no airspace violation) and the status row carries the graph summary [S:workspace-live-graph]
- [ ] Lazy-expand responds: dbltap round-trips `nodeExpand` — including on a node the update itself added (handlers are bound on the cy core and survive) — and the result lands replace-in-place on the LIVE instance: viewport untouched (no fit), post-update `loaded` counts match the mutated set, the dropped membership edge gone (ADR-005 D1) [P graphUpdate phase] [S:graph-expanded]
- [ ] Expanded vs. collapsed state distinguishable by kind resolution (ADR-005 D5): unexpanded frontier nodes render External (dashed gray); expansion restyles them to their true kind color/shape — no extra badge to judge [S:graph-expanded — the post-update node shows its true kind at label zoom] [T:WorkspaceExpandTests — External frontier resolved via GetObjectAsync]

### Future (owned later — judge when the owning AP lands)

- [ ] Severity colors (red/yellow/info) and roll-up badge ("⚠ n below") readable at default zoom → AP 3.4

## B. Native chrome (Avalonia)

Screenshot fixture: `tests/GroupWeaver.App.Tests/Screenshots/ShellScreenshotTests.cs`
renders every shipped shell state via real Skia (real DemoProvider, real views) to
`artifacts/ui/<view>-<W>x<H>.png` at **both** 1280×720 and 1920×1080:
`connection-idle`, `connection-error`, `rootpicker-demo`, `rootpicker-demo-tail`,
`workspace-demo`, `workspace-webview2-missing`, `workspace-detail`,
`workspace-detail-frontier` — 16 PNGs per run.

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

- [ ] GraphHost placeholder ("Graph view is unavailable in this environment." plus the chosen root DN) appears ONLY when no renderer is wired — headless tests / null factory; a missing WebView2 Runtime shows the missing-runtime variant instead, and the real app mounts the WebView (section A, workspace-live-graph) [S:workspace-demo]
- [ ] DetailPanelRegion sits BESIDE GraphHost in its own right-hand column with a visible separator — never overlapping the graph region (ADR-001 airspace) [S:workspace-demo]
- [ ] Status bar below the graph region: connection summary ("connected, n groups loaded — …") left, "root: <DN>" right, single dimmed line, no clipping [S:workspace-demo]
- [ ] Status bar shows the drawn-graph summary ("<n> objects, <m> edges"), dimmed, between the connection summary and the root DN, only once the load completed [S:workspace-demo] [S:workspace-live-graph]
- [ ] Refresh button tops the right detail column — above the DetailPanelRegion seam, native chrome, never over GraphHost (ADR-005 D4); label "Refresh" (shipped UI strings are English), tooltip present [S:workspace-demo — disabled there: nothing selected]
- [ ] Refresh enablement: armed iff the selection is a fetchable kind (GG/DL/UG/External frontier — loaded or not; refresh is a FORCED re-fetch) and nothing is loading; disarmed for users/computers/OUs, with no selection, and while any load/expand is in flight [I — pinned by WorkspaceLoadTests (button wiring) and WorkspaceExpandTests (command matrix)]
- [ ] Nothing floats, pops up, or layers over GraphHost; anything modal is its own Window [I — design rule, re-check on every workspace change]

### Detail panel

`workspace-detail` stages a selected demo user at the user display set's 5-row
maximum in the exact live-LDAP attribute shape; `workspace-detail-frontier`
selects a never-fetched member of a group-rooted scope (honest NotLoaded).

- [ ] Attribute rows show whitelist attribute NAMES only, in `AttributeWhitelist.FetchProperties` declaration order — the user frame reads exactly description, whenCreated, department, title, primaryGroupID, nothing else; dimmed label above each value, both legible [S:workspace-detail]
- [ ] Header kind badge: label readable on the badge color, palette parity with the root-picker badges and the graph node colors (same kind = same color everywhere); name prominent beside it, SAM + DN dimmed below [S:workspace-detail]
- [ ] Long DN WRAPS fully across lines and is text-selectable — never truncated/ellipsized (ADR-007 D4: the panel is the full-value surface, unlike the picker rows), DN rendered verbatim incl. escape sequences [I — pinned by DetailPanelViewTests (SelectableTextBlock, ordinal verbatim match); wrap spot-checked on the workspace-detail pair]
- [ ] Load-state honesty (ADR-007 D3) — no selection: "Click a node to inspect it." placeholder [S:workspace-demo]; not loaded: External badge, DN verbatim, the expand/Refresh resolve hint, ZERO attribute rows [S:workspace-detail-frontier]; unresolvable (fetched FSP): the no-attributes-available explanation [I — state pinned by WorkspaceDetailTests; text spot-checked when it changes]
- [ ] Refresh STILL tops the right column with a populated panel below it: header row above the panel content, the panel scrolls under it, never pushes it out [S:workspace-detail]
- [ ] Panel content stays inside the right detail column — long values wrap within it, the column scrolls vertically only, nothing floats or layers over GraphHost [S:workspace-detail] [I — ADR-001 airspace rule, pinned by DetailPanelViewTests' airspace fact; re-check on every panel change]

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

- [ ] Settings/rule editor (Phase 3): live preview updates; import/export present
- [ ] Violation sidebar (AP 3.4): list with jump-to-node; "unexpanded areas are unchecked" notice visible
