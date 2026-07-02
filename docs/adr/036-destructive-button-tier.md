# ADR-036: Destructive button tier — a red outline class for state-discarding actions

**Status:** Accepted · **Date:** 2026-07-02
**Decides:** issue #236 — the session-40 Lever-2 follow-up (reviewer + ui-verifier findings on #221 / PR #222): destructive actions must read distinctly from benign ghost secondaries, via a dedicated class instead of class reuse · **Phase:** 4 (feedback-driven)
**Builds on:** ADR-021 (WCAG token retone), ADR-026 (theme light/dark tokens), ADR-033 (focus-visible ring), `docs/ui-checklist.md` §B, the #221 action-hierarchy class pins

## Context

Lever 2 of the 2026-07-01 fit-audit (#221) established the native action hierarchy —
accent primaries vs ghost secondaries — as a reuse-existing-classes-only slice, and pinned
the principle **"destructive is never accent"** in
`SettingsFileTabActionHierarchyTests.ResetButton_IsGhost_AndNeverAccent`. What it
deliberately did NOT do is make destructive actions read as destructive: today the
Settings `Reset to default` (discards the whole in-memory ruleset mirror), the Plan
`New plan` (empties the durable #122 keep-alive draft), and the Plan selected-node
`Remove` (removes an object AND cascades every incident membership,
`PlanModel.RemoveNode`) are visually identical to benign ghosts like `Export script` and
`← Back to explore`; the Settings naming-rule `Remove` (deletes a whole configured rule
with its pattern and exceptions list) carries no class at all. Reviewer and ui-verifier
both flagged this during #222: an auditor scanning a button row gets no signal about
which click discards work.

Constraints: read-only product (presentation only — these actions already exist and
mutate only in-memory drafts; no AD write is anywhere near this); FluentTheme templates
re-brushed by class setters, no custom Button `ControlTemplate` (ADR-033 constraint);
WCAG 2.2 AA floors (1.4.3 text 4.5:1, 1.4.11 non-text 3:1) on both card and page in BOTH
themes; the app has no confirmation dialogs and this decision does not add one.

## Decision

### D1 — A fourth button tier: `Button.destructive`, a red OUTLINE mirroring `accent-outline`'s grammar.
Hollow at rest — transparent background, 1px opaque destructive-ink hairline border,
destructive-ink label — with a translucent red-soft wash on hover/pressed (the exact
`accent-outline` state grammar and template seams). Rationale: the outline register sits
between the grey ghosts and the filled accent in loudness, so a destructive action reads
clearly as "different — careful" **without shouting louder than the card's accent
primary** ("destructive is never the primary" stays settled). A filled red would compete
with the accent call-to-action and clone the severity-Error badge language (in this app a
red FILL means "finding", never "action"); a ghost with only red text is a colour-only
cue too weak to scan. Geometry mirrors `ghost` exactly (Padding `10,6`, CornerRadius 4,
BorderThickness 1) so every ghost→destructive swap is layout-stable.

### D2 — Tokens: a new theme-resolved role pair, values reusing the proven accessible reds.
Two new roles in `Tokens.axaml` `ThemeDictionaries`, mirrored as constants in
`BrandTokens`:

- `DestructiveTextBrush` — ink AND border: dark `#FF8A8E`, light `#A4262C`. These are the
  WP6a validation-error hues, the one accessible red already derived per theme (the raw
  severity red `#D13438` FAILS as dark-page ink at 3.35:1). Deliberately a **separate
  role, not a shared token**: the validation ink is tuned against the error-band
  composites and must stay independently retunable — sharing would let a future band
  retone silently break button contrast (the ADR-021 per-context-ink pattern).
- `DestructiveSoftBrush` — hover/pressed wash: the severity red `#D13438` at the
  accent-soft alphas, dark `#29D13438`, light `#1FD13438` (mirroring
  `AccentSoftBrush`'s 16%/12%).

Measured ratios (WCAG 2.x, the `tools/check-contrast.ps1` formula): dark ink 7.29:1 on
the page `#1b1f27`, 5.77:1 on the card composite `#2D3138`, 5.26:1 on the hover wash over
card `#473138`; light ink 6.25:1 on the page `#ECEEF1`, 5.75:1 on the card composite
`#E3E5E8`, 4.86:1 on the hover wash over card `#E1CFD3` (the floor case). Every state
clears the 4.5:1 TEXT floor, so the 1px border — the same opaque ink — clears the 3:1
non-text floor everywhere by construction (the `accent-outline` discipline: an opaque ink
border, never a translucent line tint that could composite below the floor).

### D3 — Membership: red marks discards beyond the adjacent row; single-row removes stay put.
The rule (pinned by the ui-checklist row and the class-pin tests): **an action carries
`destructive` iff one click discards user-authored state beyond the single row it sits
beside — whole-draft resets and compound/cascading deletions. It is never `accent` and
never the card's primary.** Membership now:

- IN: Settings File-tab `Reset to default` (whole ruleset mirror), Plan `New plan` (whole
  draft), Plan selected-node `Remove` (object + edge cascade), Settings naming-rule
  `Remove` (a compound rule incl. its exceptions).
- OUT, deliberately: the Plan per-membership-row `Remove` and the Settings
  ignore-/exception-row `Remove`s (they delete exactly the row they sit beside — red on
  every list row is an alarm wall that dilutes the signal), the Audit triage actions
  (Acknowledge/Suppress/Un-triage are reversible BY DESIGN, ADR-028 — styling them
  destructive would contradict their contract), `Clear filters`/`Clear` selection (view
  state), and Refresh/Reload (re-fetches directory truth, discards no authored work).

### D4 — Out of scope: confirmation UX; focus ring unchanged.
This is a visual class only — no confirmation dialogs, no undo (the app has neither
today; adding one is a behavioural decision needing its own ADR if feedback demands it —
`New plan` and `Reset to default` would be the first candidates). The ADR-033 keyboard
focus ring stays the app-wide ACCENT ring on destructive controls too: focus colour is
one invariant app-wide channel (focus location), and must not be overloaded with action
semantics; the ring's ≥3:1 discernibility is against the card/page and unaffected by the
red hairline it overdraws.

## Where the code lives
- `src/App/Styles/Tokens.axaml`: `DestructiveTextBrush` + `DestructiveSoftBrush` in BOTH
  ThemeDictionaries (dark `#FF8A8E`/`#29D13438`, light `#A4262C`/`#1FD13438`).
- `src/App/Views/BrandTokens.cs`: `DestructiveTextHex` / `DestructiveTextLightHex` /
  `DestructiveSoftHex` / `DestructiveSoftLightHex` (+ the ratio documentation).
- `src/App/App.axaml`: the `Button.destructive` style block (rest / pointerover /
  pressed), directly after `Button.accent-outline`.
- `src/App/Views/PlanView.axaml`: `NewPlanButton` and `RemoveButton` swap
  `Classes="ghost"` → `Classes="destructive"`; the per-membership-row Remove stays ghost.
- `src/App/Views/SettingsWindow.axaml`: `Reset to default` swaps ghost → destructive; the
  naming-rule card `Remove` gains `Classes="destructive"`; ignore/exception-row Removes
  unchanged.
- `tools/check-contrast.ps1`: six new destructive text-pair rows (both themes,
  page/card/wash).
- `docs/ui-checklist.md` §B: a new destructive-tier row under "Visual identity & type"
  plus wording updates to the Settings File-tab and Plan rows.
- Tests: `tests/GroupWeaver.App.Tests/Views/DestructiveButtonTokenParityTests.cs` (new),
  updates to `PlanActionHierarchyViewTests` / `SettingsFileTabActionHierarchyTests`, a
  new naming-rule/ignore-row pin in `tests/GroupWeaver.App.Tests/Settings/`.

## Security-review note
Presentation-only. **No directory-write path, no new LDAP / file-format /
deserialization surface**, no provider call, nothing reads or persists user data. The
styled actions themselves are pre-existing and mutate only in-memory drafts; the
read-only product invariant is untouched and no new attack surface exists.

## Rejected alternatives
- **Filled red button (a "danger primary").** Rejected: shouts louder than the accent
  primary on the same card (violates the settled destructive-never-primary hierarchy) and
  collides with the severity-Error fill vocabulary, where red fill means "finding".
- **Ghost with red ink only (no red border).** Rejected: a colour-only differentiation is
  too weak to scan in a button row and leans on colour alone (1.4.1-adjacent); the
  hairline is the reliable non-text cue, exactly why `accent-outline` has one.
- **Reuse `ValidationErrorTextBrush` directly instead of a new role.** Rejected: role
  coupling — the validation ink is tuned to the error-band composites; retuning either
  role would silently drag the other (same reason ADR-033 COULD reuse `AccentTextBrush`:
  there the ROLE was identical — accent ink; here it is not).
- **Use the severity red `#D13438` as the ink.** Rejected on measurement: 3.35:1 on the
  dark page — fails the 4.5:1 text floor (the exact failure WP6a already solved).
- **Flag every Remove/Clear in the app.** Rejected: over-flagging turns the settings
  lists and plan rows into a red wall; the signal only works if red is rare (D3's rule).
- **Confirmation dialogs in this slice.** Rejected as out of scope (D4) — behavioural
  change, own ADR.
- **A red focus ring on destructive controls.** Rejected: fragments the one-focus-colour
  invariant ADR-033 just established.
- **Class name `danger`.** Rejected: the pinned in-house prose ("destructive never
  accent", the #221 test names) already says "destructive"; one word everywhere wins.

## Consequences
- The #221 class pins move deliberately: `ResetButton_IsGhost_AndNeverAccent` becomes
  `ResetButton_IsDestructive_AndNeverAccent` (contains `destructive`, contains neither
  `accent` nor `ghost`); the Plan pins split (selected-node Remove + New plan →
  destructive; Gap analysis / Export script / Back / per-row Remove stay ghost AND gain
  a `DoesNotContain("destructive")` arm — the dilution guard is now executable).
- New both-theme token-parity coverage (the `FocusRingTokenParityTests` idiom:
  per-variant `TryFindResource`, no global variant flip) pins the four hexes against
  `BrandTokens`, plus a realized-control arm proving a `destructive` button's
  border/foreground paint the token.
- `tools/check-contrast.ps1` reports the six destructive pairs, so the ink can never
  silently drift below the floors; the D2 table is re-checkable by running the script.
- `settings-file`, `settings-rules`/`settings-naming`, and `plan-editor` screenshots
  change (one red-outline button each); the ui-verifier judges them against the new
  ui-checklist row in both themes.
- The naming-rule `Remove` gains class geometry (Padding 10,6 vs the Fluent default) — a
  one-pixel-class change the ui-verifier confirms causes no clipping in the rule-card
  header grid.
- Future destructive actions have a home: apply D3's rule, add the class, extend the
  pins. No schema, wire, or Core change of any kind.
