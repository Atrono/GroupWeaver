# ADR-041: Visual regression via structural invariants — no committed pixel baselines

**Status:** Accepted · **Date:** 2026-07-11
**Decides:** issue #334 (how silent visual drift is caught between LLM-judged passes) · **Phase:** 4 (feedback-driven)
**Builds on:** ADR-021 (single-source tokens — the pins these gates enforce), ADR-004/ADR-012 (`src/App/web` byte-identity hash chain the gates must never touch), ADR-038 (state probes over golden images — the same environment reasoning, now applied to the headless side), `.claude/rules/harness.md` (build.ps1 as the sole gate extension point), `docs/ui-checklist.md` (the "intended pin" markers in the scaling and exported-artifacts sections this ADR fulfills)

## Context

Every GroupWeaver screenshot is produced fresh and judged fresh — by the
`ui-verifier` agent (DoD step 2, LLM vision) or a human. Nothing compares a
frame to a prior frame, so a silent layout drift between commits is invisible
unless a judge happens to catch it on an unrelated pass. The classic industry
answer is committed golden images plus a pixel/SSIM diff gate. The 2026-07-10
harness gap analysis asked for a deliberate decision; the 2026-07-11 targeted
fit-audit (docs/ux-fit-audit-2026-07-11.md) sharpened the stakes: three
workspace fixtures were found byte-identical (evidence silently void), the
never-opened export HTML carries real rendering defects (#329), and the new
checklist scaling items are unjudgeable without a 2× pin.

Hard constraints: this box is **disposable** (eval license — a rebuild changes
font rasterization and GPU rendering wholesale, [[lab-environment]]);
`artifacts/` is gitignored by design; the shipped web bundle is hash-verified
byte-identical through the release chain (no tool may rewrite it); and every
UI diff already passes two judges (ui-verifier LLM + `reviewer`), so a pixel
gate would add churn without adding an independent observer.

## Decision

### D1 — No committed pixel baselines, ever, for app UI.

Golden-image diffing is rejected as a class, not deferred: (a) a box rebuild
invalidates every baseline at once, training the "just regenerate" reflex that
makes the gate noise; (b) every intentional UI change forces a baseline
refresh reviewed by the same LLM that made the change — the gate never has an
independent observer; (c) baselines would need a committed home with binary
churn per UI PR (~41 PNGs × sizes × themes), inverting the gitignored-
`artifacts/` design. ADR-038 already banned golden images for the windowed
harness on nondeterminism grounds; this extends the same policy to headless
Skia and Chromium output. Revisiting this requires a new ADR, not a tooling
experiment.

### D2 — The pixel-diff bug classes are covered by deterministic structural invariants instead.

Three gates, each asserting a *property* a pixel-diff would only evidence
indirectly:

1. **Native text clipping** — `TextClippingSweepTests` (Avalonia.Headless)
   walks the realized `TextBlock`s of every drivable shell surface and fails
   any whose `DesiredSize` exceeds its arranged `Bounds` (tolerance for
   deliberate ellipsis/wrap styles). This is the #286 watermark-clip class,
   pinned forever.
2. **Web-layer geometry + 2× scale** — `tests/graph-bundle/verify.mjs` gains
   structural asserts: node bounding boxes inside the viewport, legend/control
   labels not overflowing their containers, themed tokens resolving, and the
   control cluster present in a `deviceScaleFactor: 2` context (fulfilling the
   checklist scaling section's "intended pin").
3. **Exported-HTML render check** — the generated findings HTML is loaded in
   headless Chromium and asserted to render with zero console errors and the
   pinned palette present. This is the regression backstop for the #329 export
   fixes and the first automated opening of a written export artifact. The
   HTML comes from a new `--dump-export <path>` seam: demo-mode-required,
   flag-gated, observation-only — the exact `--dump-graph` exit-64 precedent
   (ADR-038 D3); it runs the real `ViolationReportExporter` over the demo
   baseline and exits.

### D3 — Font determinism is a precondition for measurement asserts.

`DesiredSize` depends entirely on the resolved font. A system-font or
rasterizer difference between this box and the `windows-2022` CI runner would
make D2.1 green locally and red in CI (or the reverse). Measurement tests
therefore pin the font: if the app already ships an embedded `FontFamily`
(avares://), the test theme pins to it; otherwise the test host embeds a
bundled .ttf. Asserting measurements against the ambient system font is a
review-rejectable defect in any future measurement test.

### D4 — The LLM judge loop keeps aesthetics, and gets guardrails.

Deterministic gates take over the oscillation-prone judgments (contrast —
already gated via `check-contrast.ps1 -Gate`; clipping; accessible names —
`AccessibleNameSweepTests`); the ui-verifier LLM pass keeps what only judgment
can do (composition, hierarchy, "reads as intentional"). The judge→fix loop is
bounded (encoded in `.claude/agents/ui-verifier.md` + CLAUDE.md DoD step 2):
max 3 fix rounds per failing checklist item; each round must flip ≥1 item
fail→pass on a FRESH capture; an identical fail set across two rounds stops
early; identical fixes are never retried and a fix that regresses a passing
item is reverted. On cap/no-progress the loop aborts in a defined state:
`git restore` of the files the unfinished rounds touched, then the stuck rule
(`docs/journal/BLOCKED-<topic>.md`, commit, next work package). Exiting by
weakening a checklist item or gate is prohibited.

## Where the code lives

- `tests/GroupWeaver.App.Tests/TextClippingSweepTests.cs` (new): the D2.1
  sweep; drives surfaces via the `AccessibleNameSweepTests` harness idiom
  (temp-dir `UiStateStore`, anti-vacuity floors) / future commitment: any new
  shell surface joins the sweep when it becomes headless-drivable.
- `tests/GroupWeaver.App.Tests/TestAppBuilder.cs` + `GroupWeaver.App.Tests.csproj`
  + `tests/GroupWeaver.App.Tests/Assets/Fonts/*` (OFL Selawik + Cascadia Mono):
  the D3 test-host font pins / commitment: production font resolution stays
  untouched; the pins are canary-tested.
- `tests/GroupWeaver.App.Tests/AppCliTests.cs` + `tools/test-cli-matrix.ps1`
  (checks 5–6): the `--dump-export` seam pins (success/refusal/usage).
- `tests/graph-bundle/verify.mjs` (extend): D2.2 structural asserts + the 2×
  probe context / commitment: geometry asserts stay in verify.mjs, never in a
  second harness.
- `src/App/Program.cs` (extend): the `--dump-export <path>` demo-only seam
  beside `--dump-graph` / commitment: extending it to live mode is a
  deliberate future decision, never a default (ADR-038 D3 policy).
- `tests/graph-bundle/` (extend) + `tools/test-graph-bundle.ps1`: D2.3 loads
  the export HTML produced via the seam — reads export OUTPUT only;
  `src/App/web` and `tools/lint-web.ps1` stay untouched (harness.md
  check-only contract).
- `.claude/agents/ui-verifier.md` + `CLAUDE.md` (DoD step 2): the D4 loop
  guardrails.
- `docs/ui-checklist.md`: the scaling/export "intended pin" markers flip to
  `[P]`/`[T]` as each gate lands.

## Security-review note

No directory-write path is added or changed; no new LDAP, file-format, or
deserialization surface. The `--dump-export` seam writes one local file to an
explicitly passed path, demo-mode-required and flag-gated — the same class and
defense as the shipped `--dump-graph` seam (no live-directory data can flow
through it). D2.3 renders that locally generated HTML in headless Chromium
inside the test harness — the same trust boundary as the existing verify.mjs
bundle run (local file, no network); the exporter's output being well-formed
is exactly what the gate checks. Read-only product invariant intact.

## Rejected alternatives

- **Committed pixel/SSIM baselines** — D1; rebuild-invalidated, self-reviewed
  churn, contradicts gitignored-artifacts design.
- **Third-party visual-regression services (Percy/Chromatic)** — external
  account + upload surface for a security-sensitive read-only AD tool;
  violates the minimal-surface posture.
- **Screenshot hash comparison (cheap "pixel-lite")** — all the brittleness of
  D1 with none of the diagnostics; a single antialiasing change flips every
  hash.
- **Unbounded "fix until pass" judge loop (status quo)** — no iteration cap or
  progress predicate; best-practice guidance and our own stuck rule both
  demand bounded self-correction (D4).

## Consequences

- The checklist's scaling and exported-artifact sections gain real `[P]`/`[T]`
  pins; the ui-verifier's job narrows to genuinely aesthetic judgment.
- CI inherits D2.1 via `dotnet test` and D2.2/D2.3 via the existing graph-
  bundle verification step — no workflow YAML change, no new build.ps1 step.
- The #329 export-format fixes get a permanent render backstop; fixture
  regressions of the byte-identical-frames kind (#332) surface as failed
  anti-vacuity/geometry asserts rather than silently void evidence.
- Nothing changes for Core/Providers; the only product-code change is the
  App-side demo-only `--dump-export` seam (D2.3).
