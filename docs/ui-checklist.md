# UI checklist — GroupWeaver

Judged by the `ui-verifier` agent on every UI change (CLAUDE.md DoD step 2).
Two parts: (A) graph layer via Playwright/headless Chromium, (B) native chrome
via Avalonia.Headless. Screenshots go to `artifacts/ui/` (gitignored).

**The bar is the v0.2 polish standard (raised 2026-06-22):** WCAG 2.2 AA — 1.4.3
text contrast (4.5:1), 1.4.11 non-text/graphical contrast (3:1), 1.4.1 use-of-color
(every semantic cue keeps a redundant non-color channel) — measured by
`tools/check-contrast.ps1`; plus a declared visual identity (the single-source
`BrandTokens` palette + the `src/App/Styles` type scale; ADR-021) and crafted
interaction motion (ADRs 017–020). Colour is parity-pinned C#↔JS — see the
identity items below; never recolour without re-pinning the mirrors.

## A. Graph layer (browser bundle)

Screenshot fixture: `tools/test-graph-bundle.ps1` drives `tests/graph-bundle/verify.mjs`
(Playwright, headless Chromium, 1600×1000) against the LITERAL shipped `src/App/web`
bundle fed the literal GraphBuilder demo dump — it writes `graph-overview.png`,
`graph-focus.png`, `graph-cycle.png`, `graph-expanded.png` and `graph-diff.png`
(the last from the hand-built gap dataset in the verify.mjs DIFF tripwire).
`workspace-live-graph.png` is the windowed smoke capture of the real app (`--demo`,
DPI-aware PrintWindow via `tools/capture-window.ps1`) — re-take it whenever the
renderer/mount path changes.

Evidence tags: **[S:name]** = judge from `artifacts/ui/<name>.png`; **[P]** = pinned by
a `tests/graph-bundle/verify.mjs` assertion; **[T:Class]** = pinned by the named xUnit
test; **[I]** = interactive/manual — cannot be evidenced by a static frame.

- [ ] Node types visually distinct — all 7 kinds differ by BOTH color and shape: User, GG, DL, UG, OU, Computer, External/unresolvable; every fill is graphical-object-distinguishable on the page bg (WCAG 1.4.11), with DL/UG/Computer (sub-3:1 fills) carrying a 2px #8A93A3 contrast-lift ring so the body's boundary still reads (ADR-021, #90); the palette is the single-source `BrandTokens` [S:graph-focus] [P palette parity C#↔JS for every kind] [P KIND_BORDER ring #8A93A3 on DL/UG/Computer, absent on the rest]
- [ ] Nesting edges legible, direction unambiguous: membership = bezier with arrowhead drawn member → group (the legend's "is member of" reading); containment = dashed, arrowless [S:graph-focus] [S:graph-cycle — the seeded antiparallel A↔B pair shows as two separated curves with both arrowheads visible, never one merged line]
- [ ] Concentric layout sane: root centered, no node overlap at ~200 demo nodes [S:graph-overview] [T:GraphBuilderGeometryTests] [P rendered pairwise min center distance ≥ 44 + loaded counts match the fixture]
- [ ] Label contrast sufficient: labels sit BELOW the nodes on the dark page background with a dark outline, never on the node fill [S:graph-focus] (labels are hidden at fit zoom BY DESIGN via `min-zoomed-font-size` — EXCEPT the root and Error-severity nodes, deliberately always-labeled at fit per ADR-018 F9; otherwise judge graph-focus, never the overview)
- [ ] Drag/zoom respond [I — manual spot-check on the windowed `--demo` run; done for AP 2.2 via posted mouse input + frame diffs]
- [ ] Focus / jump-to-node EASES the camera (cy.animate fit, ~280ms ease-out-cubic — never a synchronous cut); `focused` still confirms once per command (incl. empty/un-pannable targets); reduced-motion falls back to instant fit (ADR-017) [P focus camera-counter `__gwAnimateCalls` ≥1 + animated end-viewport == reference cy.fit] [P reduced-motion probe: instant]
- [ ] Selection feedback (ADR-018): tapping a node selects it (white border, raised) and DIMS non-neighbors via `background-blacken` (the selected node + its 1-hop closed neighborhood stay bright); a flagged dimmed node KEEPS its severity halo at full strength (dim darkens only the kind fill, never overlay/underlay); background-tap clears it; instant (no animation — `__gwAnimateCalls`/`__gwEnterAnims` untouched) [S:graph-selection] [P select+dim+halo-survives+clear+counters-untouched]
- [ ] REVERSE selection sync (ADR-020, #96): a sidebar jump / violations-row selection drives the SAME `:selected` + neighborhood-dim on the canvas (the VM→JS `select` command reuses `applySelection`, so it is byte-identical to a tap and INSTANT — never the motion counters); an empty/unknown target clears it; the command rides its OWN renderer channel, never the focus channel (the JumpCommand "exactly one focus per jump" pin holds) [P select-command == tap parity + clear-on-empty + counters-untouched]
- [ ] Hover affordance (ADR-018): mouseover brightens/border-bumps a node, mouseout restores it; reduced-motion = instant flip [P hover class toggle on emit('mouseover')/('mouseout')]
- [ ] Selective labels at fit (ADR-018 F9): the root and Error-severity nodes are labeled at the overview/fit zoom while plain nodes stay hidden (root.mzfs==0, error.mzfs==0, plain.mzfs==10) — orientation without clutter [S:graph-selection] [P per-class min-zoomed-font-size: root/error 0 < plain 10]
- [ ] Live workspace mount: the real app's WebView shows the rendered graph (in-page legend top-left, no airspace violation) and the status row carries the graph summary [S:workspace-live-graph]
- [ ] Encoding-key signature (ADR-018-adjacent, #87): the top-left legend is a crafted KEY, not a bullet list — four whitespace-grouped sections (KINDS / SEVERITY / DIFF / edges) with tracked uppercase eyebrows; KIND rows show an inline real-shape SVG swatch (mirrors the canvas node: circle/triangle/diamond/pentagon/rounded-rect/square/dashed-circle, pinned hexes, uniform hairline for 3:1 non-text) + a right-aligned tabular **live per-kind count** matching `cy.nodes()` and refreshing on graphCommit/lazy-expand; SEVERITY + DIFF are key-only (glyphs document the halo/underlay channels — NO counts, to avoid contradicting the sidebar's finding tally); stays `pointer-events:none`, left of center, within the viewport (max-height clamped). [S:graph-legend-key] [P per-kind counts == cy.nodes() tally; 7 kind + 2 edge + 3 sev + 3 diff present; box left-of-center, pe:none]
- [ ] Lazy-expand responds: dbltap round-trips `nodeExpand` — including on a node the update itself added (handlers are bound on the cy core and survive) — and the result lands replace-in-place on the LIVE instance: viewport untouched (no fit), post-update `loaded` counts match the mutated set, the dropped membership edge gone (ADR-005 D1) [P graphUpdate phase] [S:graph-expanded]
- [ ] Lazy-expand MOTION: genuinely-new nodes FADE IN (element opacity 0→1, ~240ms) while survivors replace instantly — the enter tween never moves the camera/position and never touches overlay/underlay opacity ON SURVIVORS (a new+flagged node's own halo fades in with it, ADR-017); reduced-motion = instant full-opacity add (ADR-017) [P `__gwEnterAnims` new-only, survivor absent, settles to opacity 1] [P survivor overlay/underlay-opacity 0 throughout] [P reduced-motion probe: no enter tween]
- [ ] Lazy-expand BUSY cue (ADR-019, #94): the node being expanded shows a transient busy ring (#4FA3E3 overlay, opacity 0.35, padding 8) for the directory round-trip, cleared automatically on the next graphUpdate (and on the fetch-fail/cancel path); a flagged node's own severity halo still WINS the overlay channel (`node[busy][!sev]`); static — no per-frame tween (software-floor safe), fire-and-forget so it never touches the focus/motion counters [P busy overlay set #4FA3E3/0.35/8 + severity-wins on a flagged node + clears on graphUpdate + counters-untouched]
- [ ] Expanded vs. collapsed state distinguishable by kind resolution (ADR-005 D5): unexpanded frontier nodes render External (dashed gray); expansion restyles them to their true kind color/shape — no extra badge to judge [S:graph-expanded — the post-update node shows its true kind at label zoom] [T:WorkspaceExpandTests — External frontier resolved via GetObjectAsync]
- [ ] Severity halo distinct from kind (AP 3.4, ADR-010): a flagged node shows a colored OVERLAY glow (Error #D13438 thick / Warning #F7A30B medium / Info #4FA3E3 thin) behind its kind-colored, kind-shaped body — severity never reuses fill, shape, or border; monotonic padding (7/6/5) is a redundant colorblind channel [S:graph-focus] [P severity palette parity C#↔JS: #D13438/#F7A30B/#4FA3E3] [P overlay-color per sev class]
- [ ] Severity survives lazy expand: after graphUpdate the re-Evaluated halos re-attach on the live instance (wire field re-sent), unflagged nodes keep overlay-opacity 0, frontier-resolved nodes re-judged by true kind [P sev present on post-update elements] [S:graph-expanded]
- [ ] Roll-up cue: a loaded group hiding flagged descendants shows a wider/fainter max-severity ring at fit zoom (the exact "n below" count is authoritative in the sidebar — canvas-only cytoscape has no numeric badge) [S:graph-overview] [P node[below] overlay-padding]

### Gap analysis graph (AP / ADR-015)

`graph-diff.png` is the verify.mjs DIFF tripwire's frame: a hand-built gap dataset
with one node + one membership edge per diff status (Added / Removed / Unchecked),
a Common node/edge carrying NO diff field, and a COEXIST node that is BOTH Added
and an Error finding. Diff owns the cytoscape `underlay-*` channel on nodes plus a
`line-*` override on edges — DISJOINT from kind (fill/shape), root/External
(border), and severity (overlay) — so a diffed node still reads its kind and a
diffed-and-flagged node shows both cues.

- [ ] Added reads GREEN: Added nodes carry a green (#2FAE4E) underlay glow and Added edges draw solid green; the green is distinct from GG's #107C10 fill at demo node scale [S:graph-diff] [P node[diff='added'] underlay-color / edge[diff='added'] line-color]
- [ ] Removed reads RED-ORANGE **and** faded: Removed nodes carry a red-orange (#E0503A) underlay AND a dimmed (background-opacity 0.45) kind fill — the brightness channel makes added≠removed without relying on hue; Removed edges draw red-orange **dashed**; the red-orange is distinct from severity error #D13438 [S:graph-diff] [P node[diff='removed'] underlay + background-opacity 0.45 / edge[diff='removed'] line-style dashed]
- [ ] Unchecked reads GRAY **and** faint: Unchecked nodes carry a fainter (opacity 0.35) neutral-gray (#8A8F98) underlay; Unchecked edges draw gray **dotted** at low opacity — clearly subordinate to Added/Removed [S:graph-diff] [P node[diff='unchecked'] underlay / edge[diff='unchecked'] line-style dotted]
- [ ] Kind survives the diff: every diffed node still reads its kind color AND shape underneath the underlay (the underlay is a separate layer beneath the body; removed only fades the fill, never recolors it) — diff cues never reuse fill, shape, or border [S:graph-diff]
- [ ] COEXIST without collision: the node that is simultaneously Added (green underlay) and an Error finding (red severity overlay) shows BOTH cues legibly — the green underlay beneath and the red halo behind, neither masking the other nor the kind body [S:graph-diff] [P COEXIST keystone: underlay #2FAE4E + overlay #D13438 both present]
- [ ] No-diff control unchanged: the Common node (no diff field) shows no underlay glow and the Common edge keeps its plain member styling — byte-identical to a pre-Gap render [S:graph-diff] [P Common node underlay-opacity 0]
- [ ] Diff cues legible at demo node scale: underlay padding (8/8/6) reads as a visible ring around the small kind bodies, not a hairline; dashed/dotted edge patterns are distinguishable [S:graph-diff]

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

### Visual identity & type (v0.2 polish — ADR-021 / #90 / #91 / #93)

Cross-cutting bar; the per-surface items below inherit it. The type scale + tokens are
available app-wide (App.axaml), but a view only reads them where it opts in by class —
adopted across **connect / root-picker / detail-panel / workspace / settings / plan / gap**
(#106 brought Plan/Gap onto the scale + per-hue ink, closing the last gap). Gap's diff
badges use the stronger `BrandTokens.OnLightTextStrong` (#000000) ink, not the page-bg
`OnLightText` (#1b1f27): the mid-tone Removed `#E0503A` reaches only 4.23:1 with the latter
(ADR-021, resolved deferral).

- [ ] Declared type scale, not default-Fluent: the `src/App/Styles/Typography.axaml` classes (display / title / subtitle / heading / subheading / body / eyebrow / secondary / caption / dn / dn-strong) give a real hierarchy across the adopted views — they read as an intentional product, not a template [S:connection-idle] [S:workspace-detail] [S:rootpicker-demo] [T:TypographyTests — the display size + mono DN family are EFFECTIVELY applied, fail if the include is dropped]
- [ ] Wordmark signature: the connect "GroupWeaver" is one display-class TextBlock with a weight-paired two-run split (Group = Light, Weaver = SemiBold) — one intentional move, no colour, no decoration [S:connection-idle] [T:TypographyTests — two Runs, weights Light then SemiBold]
- [ ] Monospace honesty: every DN + sAMAccountName (detail panel `dn-strong`/`dn`, root-picker candidate DN, workspace placeholder root DN) renders in the tabular-mono face so verbatim untouched directory data is visibly distinct from proportional display Names + prose attribute values [S:workspace-detail] [S:rootpicker-demo] [T:TypographyTests — DN/SAM resolve the Cascadia Mono stack]
- [ ] Per-hue on-badge ink (WCAG 1.4.3, ADR-021): the severity glyph text is white on Error #D13438 (4.93:1) but DARK #1b1f27 ink on Warning #F7A30B (8.02:1) and Info #4FA3E3 (6.04:1) — the old white-on-amber 2.06 / white-on-blue 2.73 fails are gone; kind badges keep white (their fills pass); the redundant E/W/i letter + shape channels are untouched (1.4.1) [S:workspace-violations] [S:settings-rules] [S:settings-matrix]
- [ ] Action hierarchy: primary actions are accent-filled, secondary are ghost-outlined (Connect vs Demo; settings Save vs Apply/Cancel) — no two-identical-grey-buttons ambiguity [S:connection-idle] [S:settings-file]
- [ ] Structural card chrome: the shared `BrandTokens`/`Tokens.axaml` card neutrals (translucent-white fill + hairline border + rounded corners) frame content off the void where used (connect card, unchecked-hint info block, naming cards) — distinct from the semantic palette, theme-neutral [S:connection-idle] [S:workspace-violations]

### Connect step

- [ ] Both connect paths reachable: "Connect to domain" and "Demo mode" buttons present, enabled, clearly separated; Connect is the **accent primary**, Demo the **ghost secondary** — a clear Connect-vs-Demo hierarchy, not two identical grey buttons (#93) [S:connection-idle]
- [ ] Connect composition: the content sits in a quiet token-chrome card lifted off the void, with an "ACTIVE DIRECTORY AUDIT" eyebrow above the weight-split wordmark and faint concentric rings (#10FFFFFF, non-interactive) echoing the graph — reads as a deliberate first-run, not a default template (#93) [S:connection-idle]
- [ ] Integrated-auth context line shows `DOMAIN\user` under the live button; no credential fields anywhere on the step [S:connection-idle]
- [ ] Demo hint line under the demo button explains "no domain needed" [S:connection-idle]
- [ ] Inline error block: red, wraps without clipping, legible on the theme background; live-path message carries the try-Demo-mode hint on its own line [S:connection-error]
- [ ] Error block hidden again once the message clears [I]
- [ ] Busy state: indeterminate progress bar while a connect is in flight; both buttons disabled until it resolves [I]

### Root picker

- [ ] Kind badges (OU/GG/DL/UG) visually distinguishable at a glance; badge label readable on every badge color (no dark-on-dark) [S:rootpicker-demo]
- [ ] Candidate rows: name prominent (proportional), DN below it in the dimmed tabular-mono `dn` face but legible; long DNs still ellipsize (CharacterEllipsis) instead of overflowing the row [S:rootpicker-demo]
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
- [ ] Header kind badge: label readable on the badge color, palette parity with the root-picker badges and the graph node colors (same kind = same color everywhere); name prominent (heading) beside it, SAM + DN in the dimmed tabular-mono face below it (`dn`/`dn-strong`, #91 honesty) [S:workspace-detail]
- [ ] Long DN WRAPS fully across lines and is text-selectable — never truncated/ellipsized (ADR-007 D4: the panel is the full-value surface, unlike the picker rows), DN rendered verbatim incl. escape sequences [I — pinned by DetailPanelViewTests (SelectableTextBlock, ordinal verbatim match); wrap spot-checked on the workspace-detail pair]
- [ ] Load-state honesty (ADR-007 D3) — no selection: "Click a node to inspect it." placeholder [S:workspace-demo]; not loaded: External badge, DN verbatim, the expand/Refresh resolve hint, ZERO attribute rows [S:workspace-detail-frontier]; unresolvable (fetched FSP): the no-attributes-available explanation [I — state pinned by WorkspaceDetailTests; text spot-checked when it changes]
- [ ] Refresh STILL tops the right column with a populated panel below it: header row above the panel content, the panel scrolls under it, never pushes it out [S:workspace-detail]
- [ ] Panel content stays inside the right detail column — long values wrap within it, the column scrolls vertically only, nothing floats or layers over GraphHost [S:workspace-detail] [I — ADR-001 airspace rule, pinned by DetailPanelViewTests' airspace fact; re-check on every panel change]
- [ ] Panel rhythm + terminus (#92): the content stack uses a calm spacing rhythm and ends in a faint terminus hairline (#91 card-border token) so the under-fill below a SHORT panel reads as deliberate empty space, not a clipped/broken region — the tall fixed-300px column track no longer reads as a dead black gutter at 1920 [S:workspace-detail] [S:workspace-detail-frontier — the largest-gutter case]

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
- [ ] Severity-summary chip strip in the header evidences all three severities above the fold: E/W/i glyph squares in the palette (#D13438/#F7A30B/#4FA3E3) with per-hue ink (white E, dark W/i — WCAG 1.4.3, #90) + counts (demo: E 4 · W 3 · i 12); chips stay 18×18 [S:workspace-violations] [T:ShellScreenshotTests — chip brushes + counts pinned]
- [ ] Report-export pair (AP 4.1, ADR-013 §6): "Export CSV" + "Export HTML" buttons right-aligned in the header row, legible, not crowding the title or the chip strip below; tooltips present; enabled once a load completes (CanExportReport → Snapshot is not null), greyed pre-load [S:workspace-violations]
- [ ] Rows in canonical report order: severity glyph (color + per-hue ink + redundant letter E/W/i, 20×20), wrapping message, dimmed subject name; glyph colors match the graph halos [S:workspace-violations]
- [ ] Findings scroll-clip reads as a SCROLL region (#92): the list fades softly at its bottom edge (render-only OpacityMask, opaque to ~0.92) so a finding clipped at the fold reads as scrollable, not a guillotined render bug — the topmost findings stay crisp, geometry unchanged (the 18×18 chip strip is outside the mask) [S:workspace-violations]
- [ ] "Unexpanded areas are unchecked" hint is a DISTINCT affordance (chrome#10, #92): a tinted/bordered info block (the #91 card tokens) with a plain-text ⓘ glyph, not easy-to-miss dimmed body copy; visible whenever UncheckedDns is non-empty (demo: the two ignored builtin DNs), shown even in the all-clear state [S:workspace-violations]
- [ ] All-clear: "No rule violations found." when Violations is empty; the unchecked info block still shows if areas remain unchecked [I — WorkspaceViolationsTests]
- [ ] Jump-to-node: a row frames the node (FocusAsync) AND drives the canvas selection (`:selected` + neighborhood dim via the #96 reverse-sync `select` command) and the detail panel; disabled while loading; raw-External anchors never error; the jump stays exactly one FocusAsync (select rides its own channel) [I — WorkspaceViolationsTests] [P select-command parity, section A]
- [ ] Selection sync: a graph nodeClick highlights matching sidebar row(s) AND a sidebar selection drives the graph `:selected` (bidirectional, ADR-018 + ADR-020); multiple findings on one DN all highlight [I — WorkspaceViolationsTests]

### Settings / rule editor (AP 3.3)

`settings-rules`, `settings-naming`, `settings-matrix`, `settings-ignore`,
`settings-exceptions`, `settings-file`, `settings-validation` are captured by
ShellScreenshotTests (the modal `SettingsWindow` shown standalone via `.Show()`,
both 1280×720 and 1920×1080). The settings page is its own Window (ADR-011 /
ADR-003 D5), opened from the shell top command strip.

- [ ] Settings affordance: a "⚙ Settings" button in the shell top command strip (below the WebView2 banner, above step content), never over GraphHost [S:workspace-demo]
- [ ] Tabs present and reachable: Rules, Naming, Matrix, Ignore & Exceptions, File; validation band + Apply/Save/Cancel footer persist outside the TabControl, with Save the **accent primary** and Apply/Cancel **ghost secondary** (#93; Content strings + order unchanged) [S:settings-rules]
- [ ] Rules master grid: every rule (nesting, each naming, circular, empty-group) with Enabled toggle + Severity selector; E/W/i glyph + palette (#D13438/#F7A30B/#4FA3E3) parity with SeverityConverters, with the per-hue ink (white E, dark W/i — #90) [S:settings-rules] [T:ShellScreenshotTests — severity parity]
- [ ] Naming live preview: typing a sample shows ✓ matches (green) or ✗ would be flagged (severity) against the pattern; an invalid pattern shows the loader's plain-text error, no crash; "GG_Vertrieb_Lesen" vs the GG pattern reads ✓ [S:settings-naming] [T:NamingPreviewTests / NamingPreviewConverterTests]
- [ ] Naming kind selector offers the 6 legal kinds only (no External), badge-colored (AdObjectKindConverters parity); per-rule blocks are framed as token-chrome cards (#93) [S:settings-naming]
- [ ] Matrix editor: 3 parent rows (GG/DL/UG) × 6 member cols (User/Computer/GG/DL/UG/External, no OU), kind-badge headers; each cell a 5-way allow(green)/deny/error/warning/info chip with per-hue glyph ink (white on allow/deny/error, dark on warning/info — #90); Unlisted fallback + rule-wide default severity present and labeled; AGUDLP lane readable [S:settings-matrix]
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

### Gap mode view (AP / ADR-015)

`gap-view` is captured by the Gap view screenshot fixture (a `GapViewModel` seeded with a
representative hand-built diff — a couple of Added objects, a Removed object, a Common node,
and a known-but-unloaded Ist area — `RefreshAsync` run, rendered through the `GapView`
DataTemplate, both 1280×720 and 1920×1080). Gap mode is a sibling shell step (ADR-014/ADR-015)
reached from Plan Mode via the "Gap analysis" button and returning to the SAME plan via
"← Back to explore". It shows the Plan-vs-Ist DIFF, never rule severity. The headless fixture
has no renderer, so GraphHost shows its placeholder — the right-hand chrome is the judged surface.

- [ ] Airspace held: GraphHost (the diff preview, left) is the reserved region; the gap chrome
      sits BESIDE it in its own column, never floating/layering over the graph (ADR-001
      guardrail 5) [S:gap-view]
- [ ] Header: "Gap" title + "← Back to explore" button (Back returns to the same plan, never a
      reload); the chrome column has no clipped or overlapping controls at both sizes [S:gap-view]
- [ ] Gap-summary line: a single legible line of node deltas / membership deltas / unchecked
      tally ("+a / −b objects · +c / −d memberships · e unchecked"); hidden until the first
      refresh computes a summary [S:gap-view]
- [ ] Changes list ("Changes (n)"): each row a GapKind glyph badge in the DIFF palette
      (Added/EdgeAdded green #2FAE4E "+", Removed/EdgeRemoved red-orange #E0503A "−",
      UnverifiableArea gray #8A8F98 "?" — parity with the graph diff cues) + wrapping message +
      dimmed subject name; the colors match the slice-5 graph diff overlay [S:gap-view]
- [ ] Row tap jumps: the whole row is a flat command button focusing + selecting the finding's
      anchor; the active row carries the selection-highlight band (SelectionHighlightConverters
      parity) [I — GapModeTests]
- [ ] Unchecked banner: an amber honesty note containing the word "unexpanded" visible whenever
      the diff has a known-but-unloaded Ist parent (HasUncheckedAreas) [S:gap-view]
- [ ] All-clear: "No differences — the plan matches the current structure." when the diff has no
      findings (HasFindings false) [I — GapModeTests]
- [ ] No AD-write affordance anywhere in Gap mode; the borrowed Ist is read-only (ADR-015 D3)
      [I — design rule / CLAUDE.md]
