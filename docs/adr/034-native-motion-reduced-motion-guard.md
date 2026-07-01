# ADR-034: Native-chrome motion — deferred; the reduced-motion approach + the blocking constraint

**Status:** Accepted · **Date:** 2026-07-01
**Decides:** the 2026-06-30 UX audit's "native motion" lever — DEFERRED; this ADR records why and the plan for a future cohesive slice
**Builds on:** ADR-017 (graph motion + reduced-motion mandate), ADR-024 (step-swap lifecycle), ADR-025 (parking lot)

## Context

The graph WebView ships crafted, reduced-motion-aware easing (ADR-017); the native Avalonia
chrome has **zero declared transitions** — the shell step swaps (Connect → Root picker →
Workspace → Audit/Plan/Gap) and the rail collapse cut instantly. The audit flagged the polish
gradient between the signature graph and the chrome.

Two platform facts bound any fix: Avalonia 11.3 does **not** expose the OS reduced-motion /
animation preference (the Windows source of truth is `SystemParametersInfo(
SPI_GETCLIENTAREAANIMATION)`); and a `GridLength` column width cannot be tweened by Avalonia
`Transitions`. And a hard testability fact: `Avalonia.Headless` (the DoD render harness)
short-circuits transitions, so a tween is not screenshot-evidenceable.

An implementation was attempted (a reduced-motion `IMotionSettings` seam over the SPI probe,
motion tokens, and a gated `TransitioningContentControl` cross-fade on the step host) and then
**fully reverted** — see D2. Nothing ships in code from this WP; this ADR is the decision record.

## Decision

### D1 — Defer native motion; land no code now.
The lever is investigated but the actual animation cannot ship safely today (D2), and landing the
reduced-motion seam + tokens *without a consumer* would be speculative dead code (against the
smallest-diff / no-dead-code discipline). So this ADR records the approach + the blocking
constraint, and the code lands as one cohesive slice when the constraint is resolved.

### D2 — The step-swap cross-fade is BLOCKED by the ADR-025 parking lot (empirically).
A `TransitioningContentControl` / `CrossFade` keeps the LEAVING step view alive during the fade,
which desynchronizes the parking-lot park/mount/detach ordering — ADR-025's load-bearing invariant
that detach happens right after the synchronous `ParkSurface → CurrentStep` swap. Forcing the
cross-fade ON regressed two lifecycle tests: `ParkingLotBackNavigationTests
.ForwardThenBack_KeepsParkedSurfacesBounded` (after `Plan→Back→Workspace` the parking lot held **2**
surfaces, not 1) and `BackNavigationStepSwapTests.PlanBack_ReMountsTheSharedGraphControl_
WithoutDoubleParentingCrash` (the live surface's `GraphHost` was no longer uniquely reachable as the
current step's host while two step views were transiently in the tree). The reduced-motion path
(no transition, instant swap) passed everything — the regression is intrinsic to the animated
transition, not the gate. A parking-lot-safe transition (an opacity-only fade that does not
double-present, or park-then-fade coordination) is a materially larger change than the polish
warrants now.

### D3 — The rail-collapse animation is also deferred.
A `GridLength` column width can't be tweened, and the rail `Border` is `IsVisible`-collapsed +
`ClipToBounds` — animating it cleanly (a `MaxWidth` double tween kept visible through the fold) is
materially more complex for marginal value; instant rail collapse is acceptable.

### D4 — When implemented, the plan (validated by the spike):
- **Reduced motion:** an injectable `IMotionSettings { bool AnimationsEnabled }` seam; a
  `DefaultMotionSettings` reads `SystemParametersInfo(SPI_GETCLIENTAREAANIMATION = 0x1042, 0,
  ref BOOL, 0)` on `user32.dll` (never-throws → animations-ENABLED on any failure / non-Windows).
  Verified live on this lab box: the call succeeds and reports animations OFF (typical Server/RDP).
  Inject it (optional ctor param, production default) mirroring the theme `IPlatformThemeProvider`.
- **Motion tokens:** `MotionDurationFast` (~150ms) + a `CubicEaseOut` easing (parity with the graph
  bundle's ADR-017 `ease-out-cubic`) in `Tokens.axaml`.
- **The transition:** NOT a `TransitioningContentControl` on the step host (D2). Prefer an
  opacity-only fade that never keeps two step views live, or coordinate the fade with the parking
  lot's park/detach ordering, and pin the ADR-025/024 lifecycle tests with the animation forced ON.

## Where the code lives
Nothing ships from this WP (D1). The future slice will add `src/App/Settings/IMotionSettings.cs`
(+ `DefaultMotionSettings`), motion tokens in `src/App/Styles/Tokens.axaml`, and a parking-lot-safe
transition in `src/App/Views/MainWindow.axaml(.cs)` — together with its tests and consumer. The
rail animation (D3) and `src/App/web/*` (graph has its own ADR-017 motion) stay out of scope.

## Security-review note
No code ships. The future implementation's only new external touch is a READ of a Windows UI
preference via `SystemParametersInfo(SPI_GETCLIENTAREAANIMATION)` — a benign local user-setting
read, never a write; no directory-write path, no new LDAP / file-format / deserialization surface.
The read-only product invariant is untouched.

## Rejected alternatives
- **Land the reduced-motion seam + tokens now as an unused "foundation."** Rejected: an unwired
  seam + unused tokens are dead code by the project's review criteria; the code should land with its
  consumer as one cohesive slice.
- **Ship the `TransitioningContentControl` cross-fade anyway.** Rejected: it regresses the ADR-025
  parking-lot lifecycle (D2) — a real defect, not cosmetic.
- **Rely on Avalonia to auto-follow OS reduced-motion.** Rejected: Avalonia 11.3 doesn't expose it;
  a Win32 `SPI_GETCLIENTAREAANIMATION` probe is required.

## Consequences
- Native chrome keeps its instant step swaps + rail collapse for now — no visible motion change.
- This ADR prevents a future session from re-investigating the native-motion lever from scratch:
  the reduced-motion detection approach is settled, the token plan is settled, and the parking-lot
  blocker (with the exact failing tests) is documented so the reattempt starts from the right
  transition design.
