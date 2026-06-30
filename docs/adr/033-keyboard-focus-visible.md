# ADR-033: App-wide keyboard-focus-visible ring — close the native-chrome WCAG 2.4.7 gap

**Status:** Accepted · **Date:** 2026-06-30
**Decides:** the 2026-06-30 whole-journey UX audit's one net-new accessibility gap — native Avalonia chrome shows no visible keyboard-focus indicator
**Builds on:** ADR-021 (WCAG token retone), ADR-026 (theme light/dark tokens), `src/App/Styles/Tokens.axaml` (`AccentTextBrush`)

## Context

The 2026-06-30 whole-journey UX audit found that GroupWeaver's native chrome renders no
discernible keyboard-focus indicator: a keyboard-only auditor tabbing through Connect, the
root picker, the workspace rail, Settings, or the audit table cannot tell which control
holds focus. This fails WCAG 2.2 **2.4.7 Focus Visible** and **2.4.11 Focus Appearance**,
and it is the only net-new accessibility gap the audit surfaced — the rest of the v0.2 bar
(1.4.3 text contrast, 1.4.11 non-text contrast, 1.4.1 use-of-colour) is met. The graph
WebView bundle already ships its own `:focus-visible` ring (`src/App/web`), so this decision
covers the NATIVE Avalonia surface only.

Constraints the fix must respect: the app is a **read-only product** (presentation only, no
behaviour change); the controls use FluentTheme's default templates with re-brushed class
setters (`ghost` / `accent` / `accent-outline` / `segment` / `chip`) and define **no custom
button `ControlTemplate`** (only `GridSplitter` has one, ADR-022), so the ring can be added by
style rather than per-template surgery; the ring must clear WCAG 1.4.11 non-text **3:1** on
BOTH the card and page backgrounds in BOTH themes; and it must be **keyboard-only** (not
appear on pointer interaction), per 2.4.7's intent.

## Decision

### D1 — One app-wide `:focus-visible` ring, keyboard-only.
Add `:focus-visible` styles in `App.axaml` (alongside the existing button class styles) so
every keyboard-focusable native control shows a clear ring when — and only when — focus
arrives via keyboard or programmatically (Avalonia 11.3's `:focus-visible` pseudo-class is set
on keyboard/programmatic focus, never on pointer press). Pointer clicks raise no ring. Covered
control set: `Button` (all classes — `ghost` / `accent` / `accent-outline` / `hyperlink` /
`segment` / `chip` / `colhead`), `ToggleButton`, `CheckBox`, `ComboBox`, and `ListBoxItem`
(the findings / categories / picker rows). The `segment` / `chip` filter controls are `Button`s
with bound `Classes`, so they inherit the `Button` rule.

### D2 — Ring token = `AccentTextBrush`; 2px stroke, on the control's own corner radius.
The ring uses `{DynamicResource AccentTextBrush}` (dark `#A99BFF` / light `#4A3CC8`) — the
accessible accent ink already pinned by ADR-021/026, which clears **5.48:1 on card** and
**6.93:1 on page** in both themes (well over the 3:1 non-text floor; the dark/light asymmetry
is the luminance flip). 2px width on each control's existing corner radius so the ring reads as
a halo, not a second border. No fill change, no animation (static — so no reduced-motion
concern, which the native chrome does not yet guard). The brand accent doubling as the focus
colour keeps parity with the WebView bundle's ring.

### D3 — A custom accent `FocusAdorner` replacing the Fluent default, leaving the `ControlTemplate`s intact.
The ring is a custom `FocusAdorner` — a `Border` stroked `{DynamicResource AccentTextBrush}`,
`BorderThickness=2`, the control's `CornerRadius` — set by a `:focus-visible` style per covered
control, **replacing** Fluent's default focus visual (which DOES composite into the headless
Skia capture path and otherwise dominates: white in dark, black in light, a solid blue fill on
ComboBox). No `ControlTemplate` is replaced. Two load-bearing details, both found empirically
and pinned by render-proof: (a) the adorner `Border` uses `Margin=0` (edge-hugging) — an
*outward* negative-margin adorner is clipped away by controls with `ClipToBounds=true` (e.g.
ComboBox); (b) ComboBox additionally needs `:focus-visible /template/ Border#HighlightBackground
{ Background: Transparent }` to suppress Fluent's solid blue focus fill that would mask the
accent. Because the adorner is a `Border` with `CornerRadius`, the ring rounds correctly on every
control — including the radius-12 filter chip (no square-corner compromise).

## Where the code lives
- `src/App/App.axaml` (`Application.Styles`, the `:focus-visible` block): the single source of
  the ring — `:focus-visible` `FocusAdorner` setters for Button/ToggleButton/CheckBox/ComboBox/
  ListBoxItem plus the ComboBox `HighlightBackground` blue-suppression — beside the existing
  `Button.ghost/accent/accent-outline/hyperlink` class styles.
- `src/App/Styles/Tokens.axaml`: `AccentTextBrush` (dark `#A99BFF` / light `#4A3CC8`) is the
  single source of the ring colour — reused, not a new token (the earlier `FocusRingShadow`
  BoxShadows token was removed when the mechanism became the FocusAdorner).
- `src/App/Views/AuditView.axaml`: `:focus-visible` `FocusAdorner` overrides giving the `segment`
  (radius 0) and `chip` (radius 12) filter Buttons corner-radius parity, so the chip ring rounds.
- Out of scope: `src/App/web/*` — the graph bundle already has its own `:focus-visible` ring.

## Security-review note
Presentation-only. **No directory-write path, no new LDAP / file-format / deserialization
surface**, no provider call, nothing reads or persists user data — the read-only product
invariant is untouched. The change is a visual style on existing controls; no new attack
surface (the `security-review-groupweaver` threat model is unaffected).

## Rejected alternatives
- **Rely on FluentTheme's default focus visual.** Rejected: the audit observed it is not
  discernible on these surfaces in either theme — the gap is real, not a missing include. An
  explicit, contrast-pinned, tokenized ring is testable.
- **A `:focus-visible` inset `BoxShadow` on each control's template part (instead of a
  `FocusAdorner`).** Tried first and rejected: the shadow IS applied, but Fluent's default focus
  visual composites ON TOP and masks it (white/black/blue dominate; on ComboBox the accent is
  fully covered) — so it does not deliver the accent ring D2 requires. Replacing the default via
  a custom `FocusAdorner` (D3) is what actually renders the accent.
- **Replace each control's full `ControlTemplate` app-wide.** Rejected: a far larger
  template-by-template diff than swapping just the `FocusAdorner`, for no benefit.
- **Ring on ALL focus (pointer + keyboard).** Rejected: a ring on mouse-click is visual noise
  and not what 2.4.7 asks for; `:focus-visible` (keyboard-only) is the correct scope.
- **Animate the ring in.** Rejected: motion adds nothing to a focus indicator and would need a
  reduced-motion guard the native chrome lacks; a static ring is simpler and safer.

## Consequences
- New headless coverage pins the `:focus-visible` `FocusAdorner` on a representative control of
  each covered type (adorner present + its `Border.BorderBrush` == `AccentTextBrush` under Tab
  focus, absent under Pointer), in both themes, plus a pixel/colour check that the rendered ring
  band reads the accent (NOT white/black/blue) — the latter is the guard the first BoxShadow
  attempt lacked, which let the Fluent default mask the accent unnoticed. The ui-verifier judges
  focused frames. No existing baseline text/brush changes.
- The ring token vs card+page contrast is affirmed in the PR (and may be added to
  `tools/check-contrast.ps1`) so the ring cannot silently drift below 3:1.
- Keyboard auditors get a visible focus path across the whole native journey; mouse users see
  no change. The other 2026-06-30 audit bigger-bets (Plan persistence, finding-row unification,
  in-Audit triage, mode discoverability, native motion, Gap export, …) are unaffected.
