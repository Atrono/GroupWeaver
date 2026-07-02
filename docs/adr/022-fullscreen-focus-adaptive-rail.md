# ADR-022: Full-screen / focus mode + adaptive collapsible workspace rail

**Status:** Accepted · **Date:** 2026-06-22
**Refines:** ADR-001 (airspace guardrail 5 — nothing native floats over the GraphHost HWND) · ADR-003 (D5 shell step state machine; the top command strip) · ADR-008/ADR-011 (`%APPDATA%\GroupWeaver\` is THE user-profile persistence convention; atomic temp-file+move writes) · **Decides:** the WP-A full-screen / large-monitor UX work package · **Phase:** 4 (post-v0.2 UX)
**Out of scope (separate work package):** in-graph zoom/fit/find controls — those are web-layer (sibling of `#legend`), never native, so they are deliberately excluded here and do not touch this ADR's airspace reasoning.

## Context

`WorkspaceView.axaml`'s `ColumnDefinitions="*,300"` is a 1280×720 layout that only *stretches the graph canvas* onto bigger screens; the right rail is hard-pinned at 300 px. On 1080p+ the rail is at once **cramped** — findings cards and the selected-node DN/description wrap to 4–5 lines — and mostly **empty**: with nothing selected, "Click a node to inspect it." floats centered in a tall void, and even a selected node leaves the lower rail blank (`artifacts/ui/workspace-*-1920x1080.png`). There is **no full-screen mode** (F11 is dead, `MainWindow` has no `WindowState` handling), no presentation/focus mode, and no way to give the graph the whole screen. This ADR adds full-screen, a focus/presentation mode, and an adaptive resizable+collapsible rail — **without** touching ADR-001 airspace or the pinned graph layout/motion/selection (ADR-004/017/018/020).

## Decision

1. **Full-screen is a pure view concern, no VM state.** `MainWindow` gains an **F11** `KeyBinding` whose code-behind toggles `WindowState` between the remembered prior state and `WindowState.FullScreen` (Avalonia drops the OS title bar in FullScreen; restore returns to the remembered state). **Esc** exits full-screen and focus mode (D2). No ViewModel surface — full-screen is chrome, not app state — so headless screenshot fixtures (which size the window directly) never exercise it; the keybindings are `[I]` interactive, like `OpenSettingsAsync`'s `ShowDialog`.

2. **Focus (presentation) mode lives on `ShellViewModel`.** `[ObservableProperty] bool _isFocusMode` + `ToggleFocusModeCommand`/`ExitFocusModeCommand`. The top command strip binds `IsVisible="{Binding !IsFocusMode}"` so focus mode hides it; the **WebView2-missing banner stays visible** (a missing-runtime warning must never be suppressed — and a missing runtime means there is no graph to present anyway). Focus mode is shell-level because the strip is shell chrome, and it propagates to the active step through the existing `CurrentStep`-dispatch seam (exactly as `OnRulesetApplied` does): `if (CurrentStep is WorkspaceViewModel w) w.SetRailCollapsed(IsFocusMode)`; non-workspace steps simply lose the strip (harmless). The workspace's **"Focus" button** calls up through an installed callback (`UseFocusToggleCallback`, mirroring `UseDesignPlanCallback`/`UseGapAnalysisCallback`), armed by the shell at `OnRootChosen` — dead until armed, so a renderer-less/headless workspace never half-toggles.

3. **The rail is an adaptive, collapsible region owned by `WorkspaceViewModel`.** Replace `*,300` with `*, Auto, {rail}`: the `Auto` middle is a thin native **seam** holding the `GridSplitter` and an always-present **◂/▸ collapse toggle**; the rail column width binds `RailWidth` (double, two-way, clamped **[300, 520]**, default **340**), and `IsRailCollapsed` collapses the rail column to 0 while the seam (and its ▸ expand chevron) stays. **Ctrl+B** toggles collapse. Every piece — splitter, chevron, rail — is native chrome sitting BESIDE GraphHost (to its right), never over it; the seam is the airspace-safe home for the expand affordance when collapsed (ADR-001 intact). The graph never re-layouts: the WebView reflows to its host bounds exactly as it already does on any window resize.

4. **Persisted rail state, best-effort.** `RailWidth` + `IsRailCollapsed` persist to `%APPDATA%\GroupWeaver\ui-state.json` via a new `UiStateStore` that mirrors `RulesetLocator`: a production ctor → `Environment.SpecialFolder.ApplicationData`, an injected-base-dir ctor as the headless test seam. Load is **never-throw** (missing/unreadable/corrupt ⇒ defaults, the `RulesetLocator.LoadEffective` degradation contract); save is atomic temp-file+move (the `RulesetSerializer` convention). This is app-preference state only — no untrusted input, no AD, read-only product unaffected.

5. **The reclaimed rail uses its space.** When `DetailPanel is null` the centered placeholder is replaced by a compact **scope-summary** card: object/edge totals (`GraphSummary`), the per-kind tally, and the severity tallies the sidebar chip strip already derives (`SeverityConverters.CountForSeverity`) — so empty rail reads as information, not a void. The four action buttons (Design plan / Reload scope / Refresh / Export image) reflow into one right-aligned `WrapPanel` that wraps only at the rail minimum, retiring the forced two-row stack. Findings (`2*`) and detail (`3*`) keep their proportions and flow into the now-wider rail.

## Consequences

- **Airspace + graph untouched.** All new chrome is native and beside/around GraphHost; nothing floats over the WebView2 HWND. In-graph controls (zoom/fit/find) remain a separate web-layer work package. The concentric layout (ADR-004), motion (ADR-017), and selection channels (ADR-018/020) are unchanged — this is container-level only.
- **Exit affordances in focus/full-screen:** **Esc**, plus the persistent ▸ chevron in the native seam (always clickable to bring the rail back). Any *on-graph* "Esc to exit" hint would have to be web-layer (the separate controls work package), never native-over-graph — so it is deliberately not added here.
- **Tests (test-engineer, `tests/`):** `ShellScreenshotTests` gains render cases — rail collapsed, focus mode (strip gone), no-selection scope-summary, and a **2560×1080 ultrawide** frame proving the void is gone; existing workspace frames re-baseline (rail default 300→340, button row reflow). A `ShellViewModel` unit test pins focus toggle (flips `IsFocusMode`, collapses the active workspace rail, no-ops on a non-workspace step); a `UiStateStore` test pins the round-trip + never-throw-on-corrupt. `docs/ui-checklist.md` §B gains rows for full-screen, focus mode, rail resize/collapse/persist, and the scope summary.
- **New write surface** `%APPDATA%\GroupWeaver\ui-state.json` — two scalars, best-effort, atomic; the pre-release `/security-review` gate notes it (no untrusted parse path that can break out — it's our own JSON, defaults on any failure).
- `RailWidth`'s [300,520] clamp keeps the graph from ever being squeezed below a usable width and the rail from dropping below its content minimum; the `GridSplitter` is the first user-driven layout in the app.

## Addendum (2026-06-22) — the focus-mode entry point D2 specified but the first cut omitted

The initial WP-A implementation wired focus mode end-to-end (`ShellViewModel.ToggleFocusModeCommand`/`ExitFocusModeCommand`, the workspace `ToggleFocusCommand` armed by `UseFocusToggleCallback`, the `!IsFocusMode` strip binding, Esc/F11 in `OnKeyDown`) but **never added a control that actually invokes it** — D2's "Focus button" was specified yet left out, and only the headless `WorkspaceFocus` screenshot test reached the command (programmatically). So focus mode shipped **unreachable by a user**. This addendum closes that:

1. **A Focus toggle in the top command strip** (`MainWindow.axaml`, beside ⚙ Settings) bound DIRECTLY to `ShellViewModel.ToggleFocusModeCommand` — the natural home, since focus mode is shell-level chrome (it hides that very strip; entered there, exited by Esc or pressing the key again). It is visible only on the workspace step (`ShellViewModel.IsWorkspaceStep`, raised on `CurrentStep` change), so the Connect/RootPicker strips are unchanged. This **supersedes** D2's workspace-button + `UseFocusToggleCallback` plan (kept in place, harmless, now redundant) — the strip is a better fit than the rail action row, which already carries four buttons.
2. **A single `F` key** in `MainWindow.OnKeyDown`, gated to the workspace step (`CurrentStep is WorkspaceViewModel`), toggling `ToggleFocusModeCommand`. Single-key (not a chord) so it is reliably postable via `WM_KEYDOWN` for the demo recorder; gated to the workspace, where there is no native text input to hijack (the web Find box lives inside the WebView's own HWND). Esc still exits (D1).

This makes focus mode demonstrable in the public demo GIF (`record-demo-media`): the recorder posts `F`, the chrome melts away, the graph goes edge-to-edge.

## Addendum (2026-06-27) — D5 scope-summary card reframed (redundancy removed, ruleset surfaced)

D5's scope-summary card filled the no-selection rail with object/edge totals, a **per-kind tally**, and
**severity tallies**. Two of those echoed information already on screen: the per-kind tally duplicated the
always-on graph legend's Kinds section (identical live counts), and the severity tally duplicated the findings
chip strip directly above the card in the same rail. Redundant filler competes with the findings list it sits
under and trains the eye to skip the region.

This addendum removes both duplicated blocks and replaces them with the **active ruleset name** — the single
audit-orientation fact the workspace surfaced nowhere (the root DN is already in the status bar, demo/live in
the top strip). The card now reads `Scope summary` / `Ruleset: <name>` / object-edge totals (`GraphSummary`,
unchanged) / the "Click a node to inspect it." hint — still "information, not a void," now non-redundant and
finally telling the user which ruleset their findings are judged against. `ScopeKindTally`/`KindTallyRow` are
retired; `GraphSummary` and the `SeverityConverters`/legend are untouched. (#186)

## Addendum (2026-07-02) — keyboard-focus continuity across the chrome melt (#230)

An operator report surfaced a consequence D2 never addressed: the Focus toggle lives in the very strip
focus mode hides, so activating it removes the focused control from the tree and **keyboard focus is
silently lost** (WCAG 2.4.3-adjacent). Esc/`F` still work (window-level `OnKeyDown`), but nothing visibly
holds focus and nothing points back out. This addendum defines the app's **first programmatic-focus
pattern** — future focus-moving features copy it rather than inventing a second one:

1. **Focus movement is view-owned.** `MainWindow.axaml.cs` observes `ShellViewModel.IsFocusMode` through
   the existing shell `PropertyChanged` subscription (the `OnOpened` seam) — no VM state, per D1's "chrome
   is a pure view concern". Firing on the property change catches every entry vector (button, `F`,
   command) at one choke point.
2. **On entry, park focus on the designated surviving affordance** — the seam chevron
   (`RailCollapseToggle`), exactly the control the Consequences named as the persistent exit affordance.
   Parking is unconditional (the rail melts too, taking nearly every other focusable control); the move
   uses `Focus(NavigationMethod.Tab)` so `:focus-visible` fires and the ADR-033 accent ring **renders** —
   the visible ring on an exit-adjacent control doubles as the "way back out" affordance. **Never focus
   the WebView2 HWND:** Win32 focus inside the native child would swallow the window-level Esc/`F`
   handlers, killing the exit gestures.
3. **On exit, restore focus to the Focus toggle** (symmetric round-trip), guarded by
   `IsEffectivelyVisible` (a non-workspace exit — programmatic/tests only — skips silently).

The transient "Esc to exit" banner considered in #230 stays **deliberately not added**, reaffirming the
Consequences bullet above: an on-graph hint would be web-layer (never native-over-graph, ADR-001), a
native layout-flowing banner contradicts the chrome-melt intent, and no WCAG SC requires it — the tooltip,
the `?`/F1 cheat sheet, and (now) the visible focus ring on the chevron cover discoverability. Pinned by
`ShellScreenshotTests` focus-continuity cases; the `workspace-focus-*` screenshot baselines gain the
chevron's focus ring. (#230)
