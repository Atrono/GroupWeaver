# ADR-030: Audit health band severity-gate — never read green over a live Error; disclose triage

**Status:** Proposed · **Date:** 2026-06-29
**Decides:** issue #188 (UX fit-audit Lever 1) · **Phase:** 4 (feedback-driven)
**Builds on:** ADR-009 (`RuleEngine.Evaluate` / `RuleReport` — the severity model `Info<Warning<Error`), ADR-028 (triage as tagged global-ignore entries; the would-be vs live report split; the `[ack]`/`[suppress]` grammar lives in App's `TriageEntry`), ADR-013 (export pipeline / `ViolationReportExporter` header), PLANNING.md AP 3.4 / WP5c (the audit health dashboard). Refines the `AuditSummary` band derivation only.

## Context

The audit dashboard's most prominent element is the health ring — a band-coloured ring + `"{Score}/100"`
inner disc (`AuditView.axaml:128-141`). `AuditSummary.BandFor` is a **pure threshold switch on the scalar
score** (`AuditSummary.cs:170-176`: ≥90 Excellent, ≥75 Good, ≥50 Fair, else Poor), and the score is a
per-subject penalty **average** — `raw = 100 − (3·Err + 1·Warn + 0.25·Info)/max(1,CheckedSubjects)·100`
(`AuditSummary.cs:152-154`). Two consequences make the headline dishonest in exactly the artifact a
governance auditor screenshots into an attestation:

1. **Dilution.** Because the denominator is `CheckedSubjects`, the same finding weighs less as the
   directory grows. On the ~40-subject demo, 4 errors score 55/"Fair"; but **one unresolved
   circular-nesting Error among ~2000 checked subjects scores ≈99.85 → 100 → band "Excellent."** A green
   "Excellent" sits above a live AGDLP-breaking structural Error.
2. **Silent triage.** Acknowledge/Suppress drop findings from the *live* report that drives the score
   (ADR-028 D3; `AuditViewModel.cs:995-1000`), so the ring goes green after triage with **no disclosure**
   on the summary that the clean number excludes N suppressed findings. The findings *table* honestly mutes
   triaged rows with a status pill — but the band and tiles (the screenshotted part) do not.

This is an **honesty-that-doesn't-travel** failure: the truth exists in the data (the `Critical`/`Warnings`
counts are already fields on `AuditSummary`; the would-be report already exists per ADR-028) but it does not
survive into the headline or the export. The load-bearing trust the product stakes on honesty about what was
and wasn't clean (the same discipline as the Unchecked caveat, `AuditView.axaml:276-288`) is undercut at the
band.

A hard constraint binds the fix: **`AuditSummary.Compute` is pure/total/UI-free Core** (mirrors
`RuleEngine.Evaluate`; reads load-state, never `Edges`, never throws on content). It must not learn the
triage tag grammar — that is an App concern (ADR-028: `TriageEntry` lives in App). So band-gating (which
reads only severity counts already in Core) belongs in Core; triage *disclosure* (which needs the would-be
vs live split) belongs in the App VM/view.

## Decision

### D1 — The authoritative headline is the BAND, gated by worst LIVE severity, decoupled from the scalar.

`AuditSummary` gains an explicit `Band` that is **no longer a pure function of `Score`**. The gate is applied
in `Compute` over the already-computed live counts, in priority order:

- **`Critical > 0` → band `"Action required"`** (a new, top-priority, red band) — *regardless of the score.*
  "Action required" is deliberately **not a score claim**: with the score possibly at 99 a "Poor" band would
  be self-contradictory, whereas "Action required" honestly says *a live structural Error exists, act on it*
  without asserting the directory is low-quality overall.
- **else `Warnings > 0` → band capped below "Excellent"** (max `"Good"`): a live Warning forbids the
  top "genuinely clean" band but, being non-critical, may still read "Good".
- **else → the existing score band** (Excellent/Good/Fair/Poor) over a genuinely clean checked scope.

The **numeric score and ring fraction are unchanged** — `Score = Score/100` still fills the ring, honest
about *how much* passed — but the **band word leads** (it is the primary headline) and the ring **colour
follows the gated band** (red for "Action required" even at 99% fill; the full red ring + "Action required" +
the Critical tile reading "1" together tell the true story: much passing, one critical break). The
`"{Score}/100"` figure is demoted to a **secondary trend metric** beside the band, never the green headline
on its own.

The score **penalty math and the 3/1/0.25 weights are untouched** — re-weighting / documenting the formula
is a separate, lower-priority concern (UX-audit bucket-C; not this ADR). This ADR changes *band derivation
and presentation*, not the score.

### D2 — Disclose triage on the summary, parallel to the Unchecked caveat (App-side, not Core).

When live triage entries exist (Acknowledged + Suppressed > 0), the audit summary renders a caveat beside the
band — *"N findings acknowledged/suppressed — excluded from this score"* — styled exactly like the existing
Unchecked caveat block (`AuditView.axaml:276-288`) so the qualifier travels with the headline. The count is
an **App/VM concern**: `AuditViewModel` already holds both the would-be report (ADR-028 D3) and the live
report, so `TriagedCount = wouldBe.Findings − live.Findings` (equivalently, the count of `[ack]`/`[suppress]`
tagged ignore entries). **`AuditSummary.Compute` stays pure over the live report** and never sees a triage
tag — preserving the Core/App boundary ADR-028 set.

### D3 — The honesty caveats travel into the export header.

`ViolationReportExporter` (ADR-013) gains, in its existing header (root/connection/timestamp/counts):
the **active ruleset name**, the **triaged count** ("N findings excluded by triage"), and the **unchecked
count**. A screenshot of the ring or a bare export can no longer present a clean bill that omits the live
Error band, the suppressions, or the unexpanded scope. (The full per-finding consolidated report is UX-audit
Lever 5 / a separate issue; D3 is only the self-describing header.)

### D4 — Pinned by Core + screenshot tests; a new ui-checklist criterion.

- `AuditSummaryTests` (Core) pin the gate as projections (per the rule-engine determinism discipline — compare
  `(Score, Band)`, never record identity): `Critical>0` ⇒ band `"Action required"` *even at score 100* (the
  1-error/2000-subjects case); `Warnings>0, Critical==0` ⇒ band ∈ {Good, Fair, Poor}, never Excellent; all-clean
  ⇒ score band. The pinned AP-3.2 demo baseline (3 nesting + 1 cycle errors) now bands **"Action required"**,
  not its old scalar band — a deliberate, reviewed test update.
- `Wp5cAuditScreenshotTests` render the gated band + the triage caveat (an Error fixture and a triaged
  fixture). `docs/ui-checklist.md` §Audit gains a `[B]` criterion: *the band never reads Excellent/Good while a
  live Error exists; a triage caveat shows when suppressions are live.*

## Where the code lives

- `AuditSummary.Compute` / a new `BandFor(score, critical, warnings)` (Core): the severity gate. The `Band`
  string set grows by one (`"Action required"`); `Band` is a computed, non-persisted field — **no
  `schemaVersion` bump, no ruleset-format change** (cf. ADR-028's same discipline).
- The ring-colour converter + `AutomationProperties.Name` (App, `AuditView.axaml` / its converters): map the
  new band to the red severity hue and the spoken summary ("Directory health: action required, 1 critical").
- `AuditViewModel` (App): expose `TriagedCount` / `HasTriaged` (would-be − live) for the D2 caveat; bind the
  secondary score figure.
- `ViolationReportExporter` (Core/Export, ADR-013): the D3 header fields.

## Security-review note

No new attack surface: no new LDAP, no new file format, no new deserialization, and — the invariant — **no
directory-write path**. The band gate is arithmetic over counts already computed; the export header adds
read-only metadata. Read-only product, untouched.

## Rejected alternatives

- **Re-weight the score (e.g. make an Error worth more) so it can't dilute.** No weight makes a single Error
  in a 10k-object directory dominate a 0–100 *average* without absurd magnitudes, and it would silently
  change every historical comparison. The honest fix is to stop letting the *band* be a pure function of the
  diluting scalar — gate it on the raw severity counts. (Documenting/justifying the weights remains a separate
  bucket-C item.)
- **Reuse "Poor" instead of a new "Action required" band.** A 99/100 score showing "Poor" is internally
  contradictory and reads as "low quality," not "one critical break to fix." A distinct, non-numeric
  top-priority band states the truth without fighting the score.
- **Thread the would-be report into `AuditSummary.Compute` to compute the triaged count in Core.** Couples
  pure Core to App's triage tag grammar (ADR-028 deliberately put `TriageEntry` in App) and re-introduces the
  parallel-knowledge the boundary avoids. The VM already has both reports — the count is one subtraction there.
- **Hide / zero the score when an Error exists.** Removes honest information (how much *did* pass) and breaks
  the score's value as a trend metric. Keep the number; subordinate it; let the gated band carry the verdict.
- **Cap a Warning-only directory at "Fair" (amber).** Considered (it aligns band colour with the Warning hue),
  but over-penalises non-critical naming drift; capping below "Excellent" (max "Good") is the honest floor
  without crying wolf. A future review could tighten it — an additive change to one gate branch, not a
  contract change.

## Consequences

- **The headline can no longer lie by omission.** A live Error forces "Action required" at any score; live
  Warnings forbid "Excellent"; live triage is disclosed beside the band and in the export header. The
  attestation artifact (screenshot or export) is self-describing.
- **The AP-3.2 demo baseline's band changes** (errors present ⇒ "Action required"); the executable baseline
  test and the `Wp5c` screenshot are updated deliberately. The **score number and the 19-finding/penalty math
  are unchanged** — only band derivation and presentation move.
- **One new band string** flows to the ring-colour map and the automation name; dark/light parity is
  unaffected (it reuses the existing Error/red severity hue, ADR-021/010). No token-parity-mirror entry (it is
  not a new colour, just a new mapping to the existing severity red).
- **`AuditSummary.Compute` stays pure/total/UI-free**; the triage disclosure is App-side, so the Core/App
  boundary and the ADR-028 tag-grammar ownership are preserved.
- **Score-as-trend is now coherent** with Lever 3 (#190): once audit runs persist, the subordinated number is
  exactly the right longitudinal metric, while the band carries the point-in-time verdict.
