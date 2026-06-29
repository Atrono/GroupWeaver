# ADR-032: Audit run history — persisted findings runs + drift comparison

**Status:** Proposed · **Date:** 2026-06-29
**Decides:** issue #190 (UX fit-audit Lever 3) · **Phase:** 4 (feedback-driven)
**Builds on:** ADR-009 (`RuleReport` / `AuditSummary` — the finding identity `RuleId` + `PrimaryDn`, `Dn.Comparer`-keyed; deterministic `RuleViolationComparer` order), ADR-013 (the export pipeline — the same data this artifact persists), ADR-015 + `.claude/rules/gap-diff.md` (`SnapshotDiff`'s static/pure/total discipline and the Added/Removed/Common/Unchecked vocabulary), ADR-008 / `RulesetLocator` (the `%APPDATA%\GroupWeaver\` persistence convention: atomic write, never-throw, injected-base-dir test seam), `.claude/rules/data-model.md` (DN identity, the null-vs-empty unchecked tri-state).

## Context

The persona's defining trait is the **recurring audit** — the same directory, monthly or quarterly — and the
core deliverable is *evidence of change over time*: did the nesting Errors get fixed, did new off-convention
groups appear, is the empty-group count creeping. But persistence today is only `ui-state.json` +
`ruleset.jsonc` (`RulesetLocator`, `UiStateStore`); **no code path saves a snapshot, a `RuleReport`, an
`AuditSummary`, or a finding set** for later comparison. Gap mode diffs Plan-vs-Ist only — there is no
Ist-vs-prior-Ist path. The tool gives an excellent point-in-time photo but no film; the auditor hand-diffs
two exported CSVs. The single recurring trait of the persona is unserved.

The honesty constraint is load-bearing here: under lazy-expand a run is often over a **partially expanded**
scope (the unchecked tri-state). A naive drift diff that reads "this finding is gone" cannot distinguish
*remediated* from *not looked at this time* — exactly the over-claim the product's unchecked-honesty exists
to prevent. Any drift feature must carry that tri-state.

## Decision

### D1 — Persist a findings-level "audit run" artifact, not the raw directory.

A run records the *audit result*, not a copy of the directory: `schemaVersion`, `timestamp`, `rootDn`,
connection description, **ruleset name + content hash**, the `AuditSummary` (score, band, counts), the
`findings[]` (`RuleId`, `Severity`, `PrimaryDn`, the canonical `Dns`, `Message`), and `UncheckedDns[]` — in
the deterministic `RuleViolationComparer` order. This is the **minimal evidence** that answers "what changed,"
and it is **exactly the data ADR-013's CSV/HTML export already writes to disk** — so it adds **no new
disclosure surface** beyond the export the operator already invokes.

### D2 — Storage mirrors the established `%APPDATA%\GroupWeaver\` idiom.

Runs live under `%APPDATA%\GroupWeaver\runs\<timestamp>-<root-slug>.json`, written **atomically** (temp +
move) and read **never-throw** (a corrupt or older-schema file is skipped with a logged warning, never a
crash) — the same discipline as `RulesetLocator` / `UiStateStore`, via a new `AuditRunStore` with the same
**injected-base-dir test seam** (`%APPDATA%` in production, a temp dir in tests). Serialization uses the
**hardened `RulesetJson` options** (no unmapped members, the non-relaxed encoder) — a new on-disk format is a
new deserialization surface and inherits the project's existing JSON-hardening (see Security note).

### D3 — `AuditRunDiff.Compute(previous, current)` is pure Core, and carries the unchecked tri-state.

A new Core type `AuditRun` (record) + `AuditRunDiff.Compute` — **static, pure, total, UI-free, deterministic**,
mirroring `SnapshotDiff`'s discipline but over **findings** (keyed by finding identity `(RuleId, PrimaryDn)`,
`Dn.Comparer`-keyed). It buckets every finding into:

- **Fixed** — in `previous`, absent in `current`, *and* the subject is in a checked area of `current`.
- **New** — in `current`, absent in `previous`.
- **Still-open** — in both.
- **Now-unchecked** — in `previous`, absent in `current`, **but the subject sits under a parent that is
  unchecked in `current`** (`current.UncheckedDns`). This is the honest tri-state: a finding that vanished only
  because its area wasn't expanded this run is **never** counted as Fixed.

### D4 — A "Compare to previous run" view on the Audit screen.

The Audit screen gains a compare action: pick a saved run (default = the most recent prior run **for the same
`rootDn`**) and diff it against the **current live findings** (or another saved run). It renders the four
buckets and an **honesty banner when either run had unchecked areas** (the comparison is partial), parallel to
Gap's unchecked banner. A ruleset-hash mismatch between the two runs is surfaced too — drift under a *changed
ruleset* is a different claim and must be labelled, not silently blended.

### D5 — Opt-in; read-only; co-enables scope memory.

Saving a run is an explicit action ("Save audit run", beside the Audit export) and/or an opt-in auto-save on
export — the tool never accumulates directory data without a deliberate act. The only writes are to
`%APPDATA%\GroupWeaver\runs\`; **no AD write path anywhere.** Because each run records its `rootDn`, the
"recent scopes" memory (the cross-cutting scope-memory finding) is **derived from the runs index** — built
once, alongside this.

### D6 — Pinned by tests.

`AuditRunDiffTests` (Core) pin the buckets as **projections** (per the determinism discipline — compare sorted
`(identity, bucket)` pairs, never record identity), including the **Now-unchecked** case (a previous finding
whose parent is unchecked in the current run is Now-unchecked, not Fixed) and the ruleset-hash-mismatch flag.
`AuditRunStoreTests` (App, temp-dir seam) pin round-trip + never-throw on corrupt/older files. A screenshot
test covers the compare view + its honesty banner; `docs/ui-checklist.md` gains a criterion.

## Where the code lives

- `AuditRun` (record) + `AuditRunDiff.Compute` (Core, new `Core/Audit/` or alongside `Core/Diff/`): pure,
  deterministic, total — the same shape as `SnapshotDiff` (`src/Core/Diff/SnapshotDiff.cs`).
- `AuditRunStore` (App, beside `UiStateStore` / `RulesetLocator`): the `%APPDATA%\GroupWeaver\runs\` read/
  write/list, atomic + never-throw + injected-base-dir seam.
- `AuditViewModel` (App): the Save-run command, the compare command + bucketed projection, the honesty/hash
  banners.

## Security-review note

The new on-disk JSON is a **new deserialization surface** — it MUST use the hardened `RulesetJson` reader
options (the relaxed-JSON-encoder regression is a recurring finding class) and never-throw on malformed/older
files. The run stores DNs + finding messages (which embed names) — **the same data ADR-013's export already
writes**, so no disclosure beyond what export discloses, and it lands under the user-profile `%APPDATA%`
convention. **No new LDAP, no AD write, no remote I/O.**

## Rejected alternatives

- **Persist the full `DirectorySnapshot`** (enabling a structural `SnapshotDiff` of added/removed objects/
  edges and re-evaluation under a new ruleset). Rejected for v1: a *read-only* auditor writing a full copy of
  the customer's directory structure to disk is a materially larger data-at-rest footprint than the findings
  evidence, and the persona's question — "what *violations* changed" — is answered by the findings diff (a new
  off-convention group surfaces as a **New** finding; a clean new group is, correctly, not an audit event).
  Whole-structure drift regardless of findings is a heavier, separate future ADR; the findings run is
  re-derivable by re-scanning.
- **Reuse the live Gap / `SnapshotDiff` (node/edge) machinery for run comparison.** Wrong altitude — the
  auditor's question is about findings, not raw topology. `AuditRunDiff` is a small finding-set diff that
  *mirrors* `SnapshotDiff`'s discipline, not the node/edge diff itself.
- **A database / external store.** Over-engineered against the product's zero-install, portable, file-based,
  hand-inspectable persistence convention. A folder of JSON files under `%APPDATA%` is the right weight.
- **Auto-save every run silently.** Footprint/consent — saving is opt-in (or on export, an act the user already
  chose), so the tool never quietly accumulates directory data.
- **Count "absent in the new run" as Fixed unconditionally.** Dishonest — it conflates *remediated* with *not
  expanded this time*. The **Now-unchecked** bucket is mandatory; this is the whole reason drift needs the
  tri-state.

## Consequences

- **The recurring cadence is finally served**: Fixed / New / Still-open / Now-unchecked across runs, with the
  unchecked honesty carried into drift and ruleset changes labelled rather than blended.
- **Minimal data-at-rest** — findings only, at parity with the export the operator already writes; no full
  directory copy on disk.
- **Reuse, not new machinery** — the `SnapshotDiff` discipline and the `%APPDATA%\GroupWeaver\` atomic/never-
  throw persistence idiom are both inherited; `AuditRunStore` is a sibling of `UiStateStore`.
- **Co-enables scope memory** (the runs index carries each `rootDn`).
- **One new deserialization surface** for the security gate (hardened-reader + never-throw).
- **Closes the loop with ADR-030**: once runs persist, the score number subordinated there becomes exactly the
  right *longitudinal trend* metric, while the severity-gated band carries each point-in-time verdict.
