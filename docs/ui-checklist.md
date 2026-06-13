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
- [ ] Severity halo distinct from kind (AP 3.4, ADR-010): a flagged node shows a colored OVERLAY glow (Error #D13438 thick / Warning #F7A30B medium / Info #4FA3E3 thin) behind its kind-colored, kind-shaped body — severity never reuses fill, shape, or border; monotonic padding (7/6/5) is a redundant colorblind channel [S:graph-focus] [P severity palette parity C#↔JS: #D13438/#F7A30B/#4FA3E3] [P overlay-color per sev class]
- [ ] Severity survives lazy expand: after graphUpdate the re-Evaluated halos re-attach on the live instance (wire field re-sent), unflagged nodes keep overlay-opacity 0, frontier-resolved nodes re-judged by true kind [P sev present on post-update elements] [S:graph-expanded]
- [ ] Roll-up cue: a loaded group hiding flagged descendants shows a wider/fainter max-severity ring at fit zoom (the exact "n below" count is authoritative in the sidebar — canvas-only cytoscape has no numeric badge) [S:graph-overview] [P node[below] overlay-padding]

## B. Native chrome (Avalonia)

Screenshot fixture: `tests/GroupWeaver.App.Tests/Screenshots/ShellScreenshotTests.cs`
renders every shipped shell state via real Skia (real DemoProvider, real views) to
`artifacts/ui/<view>-<W>x<H>.png` at **both** 1280×720 and 1920×1080:
`connection-idle`, `connection-error`, `rootpicker-demo`, `rootpicker-demo-tail`,
`workspace-demo`, `workspace-webview2-missing`, `workspace-detail`,
`workspace-detail-frontier`, `workspace-violations`, `settings-naming`,
`settings-rules`, `settings-matrix`, `settings-ignore`, `settings-exceptions`,
`settings-file`, `settings-validation` — 32 PNGs per run.

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
- [ ] "Export image" button (AP 4.1, ADR-013 §3) sits in the same header row beside Reload scope / Refresh — native chrome, never over GraphHost; label "Export image", tooltip present; armed once a load completes with a renderer wired (CanExportGraphImage), greyed pre-load / while loading [S:workspace-demo] [I — enablement pinned by WorkspaceExportTests]
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

### Violations sidebar (AP 3.4)

`workspace-violations` drives the demo default ruleset (the 19-finding baseline:
4 error / 3 warning / 12 info) into the right-column split.

- [ ] Sidebar tops the right column ABOVE the detail stack (vertical split, beside GraphHost, never over the graph — ADR-001 airspace); header "Findings (n)" [S:workspace-violations]
- [ ] Severity-summary chip strip in the header evidences all three severities above the fold: E/W/i glyph squares in the palette (#D13438/#F7A30B/#4FA3E3) + counts (demo: E 4 · W 3 · i 12) [S:workspace-violations] [T:ShellScreenshotTests — chip brushes + counts pinned]
- [ ] Report-export pair (AP 4.1, ADR-013 §6): "Export CSV" + "Export HTML" buttons right-aligned in the header row, legible, not crowding the title or the chip strip below; tooltips present; enabled once a load completes (CanExportReport → Snapshot is not null), greyed pre-load [S:workspace-violations]
- [ ] Rows in canonical report order: severity glyph (color + redundant letter E/W/i), wrapping message, dimmed subject name; glyph colors match the graph halos [S:workspace-violations]
- [ ] "Unexpanded areas are unchecked" hint visible whenever UncheckedDns is non-empty (demo: the two ignored builtin DNs), shown even in the all-clear state [S:workspace-violations]
- [ ] All-clear: "No rule violations found." when Violations is empty; hint still shows if areas remain unchecked [I — WorkspaceViolationsTests]
- [ ] Jump-to-node: a row frames the node (FocusAsync) and selects it (detail panel syncs); disabled while loading; raw-External anchors never error [I — WorkspaceViolationsTests]
- [ ] Selection sync: a graph nodeClick highlights matching sidebar row(s); multiple findings on one DN all highlight [I — WorkspaceViolationsTests]

### Settings / rule editor (AP 3.3)

`settings-rules`, `settings-naming`, `settings-matrix`, `settings-ignore`,
`settings-exceptions`, `settings-file`, `settings-validation` are captured by
ShellScreenshotTests (the modal `SettingsWindow` shown standalone via `.Show()`,
both 1280×720 and 1920×1080). The settings page is its own Window (ADR-011 /
ADR-003 D5), opened from the shell top command strip.

- [ ] Settings affordance: a "⚙ Settings" button in the shell top command strip (below the WebView2 banner, above step content), never over GraphHost [S:workspace-demo]
- [ ] Tabs present and reachable: Rules, Naming, Matrix, Ignore & Exceptions, File; validation band + Apply/Save/Cancel footer persist outside the TabControl [S:settings-rules]
- [ ] Rules master grid: every rule (nesting, each naming, circular, empty-group) with Enabled toggle + Severity selector; E/W/i glyph + palette (#D13438/#F7A30B/#4FA3E3) parity with SeverityConverters [S:settings-rules] [T:ShellScreenshotTests — severity parity]
- [ ] Naming live preview: typing a sample shows ✓ matches (green) or ✗ would be flagged (severity) against the pattern; an invalid pattern shows the loader's plain-text error, no crash; "GG_Vertrieb_Lesen" vs the GG pattern reads ✓ [S:settings-naming] [T:NamingPreviewTests / NamingPreviewConverterTests]
- [ ] Naming kind selector offers the 6 legal kinds only (no External), badge-colored (AdObjectKindConverters parity) [S:settings-naming]
- [ ] Matrix editor: 3 parent rows (GG/DL/UG) × 6 member cols (User/Computer/GG/DL/UG/External, no OU), kind-badge headers; each cell a 5-way allow(green)/deny/error/warning/info chip; Unlisted fallback + rule-wide default severity present and labeled; AGUDLP lane readable [S:settings-matrix]
- [ ] Ignore + exceptions: dn/name mode toggle, glob field, note field rendered PLAIN TEXT (control chars never interpreted, #45), add/remove; nesting exceptions show the Any/Parent/Member endpoint control, naming/simple ones do not [S:settings-ignore] [S:settings-exceptions] [T:MatchEntryNotePlainTextTests]
- [ ] Circular + empty-group: Enabled + Severity present [S:settings-rules]
- [ ] File tab: Import / Export / Reset-to-default present; Save + Apply present and distinct (Apply = live no-write, Save = live + atomic persist) [S:settings-file]
- [ ] Validation panel: on an invalid edit or a rejected user-file-on-open, errors list as "{path} — {message}", message STRICTLY plain text (#45); save/export blocked while invalid [S:settings-validation] [T:SettingsValidationTests]
- [ ] Invalid-user-file banner: when the app runs on the default because the saved file was rejected, the window says so and offers Fix/Reset; the on-disk file is never auto-rewritten [S:settings-validation] [T:SettingsValidationTests — on-disk byte-unchanged]
- [ ] Live re-thread: Apply/Save re-evaluates the open workspace (severity halos + sidebar update) with NO graph rebuild / viewport kept [I — SettingsShellIntegrationTests: Assert.Same(Graph), UpdateGraphAsync not ShowGraphAsync]
- [ ] Airspace: settings is its own Window, never layered over the workspace GraphHost [I — design rule / ADR-003 D5]

### Plan mode editor (AP 4.2.3)

`plan-editor` is captured by the plan-editor screenshot fixture (the demo shell driven
into Plan Mode via the workspace "Design plan" button, a representative plan seeded — a
few groups, a user, memberships, and at least one AGDLP finding — rendered through the
`PlanView` DataTemplate, both 1280×720 and 1920×1080). Plan Mode is a sibling shell step
(ADR-014), reached from the workspace and returning to the same Ist workspace via "← Back
to explore"; the editor is panel-based and the read-only graph is the live preview.

- [ ] Airspace held: GraphHost (the live preview, left) is the reserved region; the editor
      panel sits BESIDE it in its own column, never floating/layering over the graph
      (ADR-001 guardrail 5) [S:plan-editor]
- [ ] Header: "Plan" title + "← Back to explore" button; the editor column scrolls without
      clipping at both sizes (no overlapping or cut-off controls) [S:plan-editor]
- [ ] Add-object form: kind selector (User / Global group / Domain-local group / Universal
      group), Name field, SAM field shown ONLY for User; "Add" button; kind labels legible
      [S:plan-editor]
- [ ] Add-membership form: parent selector (GROUPS only) + child selector (any object) with
      kind badges, "Add member" button; a hint that only a group can have members [S:plan-editor]
- [ ] Objects list: each row a kind badge (AdObjectKindConverters palette — U #038387 /
      GG #107C10 / DL #A14000 / UG #744DA9, same kind = same color as graph/picker) + name;
      selecting a row reveals Rename (field + button) and Remove [S:plan-editor]
- [ ] Memberships list: "parent ← child" rows (child is a member of parent — legend reading),
      each with a Remove affordance [S:plan-editor]
- [ ] Live validation: the findings list ("Findings (n)") reflects the current plan after every
      edit — severity glyph (color + letter, SeverityConverters parity) + message + subject;
      all-clear text when the plan is clean [S:plan-editor] [I — PlanModeEditorTests]
- [ ] Inline edit error: a rejected edit (duplicate name, control chars, rename collision)
      shows a plain-text red message; the form keeps the user's input to fix [I — PlanModeEditorTests]
- [ ] Selection sync: a graph node click selects the matching Objects-list row (and vice
      versa), and highlights matching findings rows [I — PlanModeEditorTests]
- [ ] No AD-write affordance anywhere in Plan Mode; export is a separate, explicit action
      (AP 4.2.4) — this slice has no Export button yet [I — design rule / CLAUDE.md]
