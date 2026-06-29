# Audit roll-up contracts (WP5 / AP 3.4, binding for the v0.4 Audit dashboard)

Pinned by tests in `tests/GroupWeaver.Tests/Core/Rules/AuditSummaryTests.cs` and
`tests/GroupWeaver.Tests/Core/Diff/GapReportTests.cs` + `GapUnionTests.cs`; change only
with a reviewed PR that updates the tests deliberately. Builds on [[data-model]],
[[rule-engine]], [[gap-diff]].

- **`AuditSummary.Compute(RuleReport, DirectorySnapshot, Ruleset)` — static, pure,
  deterministic, total, UI-free.** Mirrors `RuleEngine.Evaluate`: never calls a
  provider, never mutates its inputs, never throws on directory CONTENT. Derived
  entirely from the already-computed `RuleReport` plus a one load-state read of the
  snapshot — it does NOT re-evaluate rules and does **NOT read
  `DirectorySnapshot.Edges`** (the O(E)-recompute perf contract in [[data-model]] is
  untouched; `AuditSummary.cs` documents this one-way). Review-enforced.
- **Score math is pinned:** weights `ErrorWeight=3.0`, `WarningWeight=1.0`,
  `InfoWeight=0.25`; `raw = 100 − penalty/max(1,CheckedSubjects)·100`;
  `Score = clamp(round(raw, AwayFromZero), 0, 100)`. The denominator is a per-subject
  **average**, so a fixed finding count dilutes as the directory grows — the dilution
  ADR-030 (#188) addresses at the *band*, not the score (weights stay 3/1/0.25).
- **`BandFor` is an ACTIVE CHANGE SITE.** Today `BandFor(int score)` → four bands
  (`Excellent` ≥90 / `Good` ≥75 / `Fair` ≥50 / `Poor`). **ADR-030 (#188, Proposed)**
  changes the signature to `BandFor(score, critical, warnings)` and adds a fifth band
  `"Action required"`, gated in priority order: `Critical>0` ⇒ `"Action required"`
  (at any score); else `Warnings>0` ⇒ capped below `Excellent` (max `Good`); else the
  score band. `Band` is computed and non-persisted — **no `schemaVersion` bump, no
  ruleset-format change**. When implementing #188, update the `AuditSummaryTests`
  projections deliberately (incl. the AP-3.2 demo baseline, which then bands
  `"Action required"`).
- **Tri-state honesty:** an unloaded/unexpanded subtree is **never counted as
  `Passing`** (`snapshot.IsLoaded` gate) — the same null-vs-empty / "unexpanded areas
  unchecked" truth as [[data-model]] and `RuleEngine.UncheckedDns` ([[rule-engine]]).
  `UncheckedPresent = report.UncheckedDns.Count > 0`.
- **Equality:** `AuditSummary` is a value record, but consumers and tests compare
  PROJECTIONS — `(Score, Band)` tuples and the `ByRuleClass` entries — never whole-record
  or dictionary identity (the `RuleEngine` determinism discipline, [[rule-engine]]).
- **`GapSummary.From(SnapshotDiff)` — static, pure, deterministic, total.** Tallies the
  diff's `NodeStatus` / `EdgeStatus` values + `UncheckedParents.Count` into the flat
  counts record (`Added/Removed/Common` nodes & edges, `UncheckedEdges`,
  `UncheckedParents`) so the summary can never silently drift from the diff it
  summarizes. Reads the diff only — never re-diffs, never reads `Edges` (`SnapshotDiff`
  already did that once, [[gap-diff]]).
