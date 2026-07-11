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
- **`Band` is severity-gated, decoupled from the scalar (ADR-030 / #188, implemented).**
  `BandFor(score, critical, warnings)` returns one of FIVE bands in priority order:
  `Critical>0` ⇒ `"Action required"` (at *any* score, even 100 — the dilution guard);
  else `Warnings>0` ⇒ the score band **capped below `Excellent` at max `Good`**
  (Excellent→`Good`; lower bands pass through; Info-only does NOT cap); else the score
  band (`Excellent` ≥90 / `Good` ≥75 / `Fair` ≥50 / `Poor`, the private `ScoreBand`
  switch). `Band` is computed and non-persisted — **no `schemaVersion` bump, no
  ruleset-format change**. The AP-3.2 demo baseline bands `"Action required"` (4 live
  Errors; its `Score=55` is unchanged). Changing the gate is a reviewed `AuditSummaryTests`
  update (the projections pin every arm, incl. `Critical>0`-at-score-100).
- **Triage disclosure (ADR-030 D2) is App-side — Core stays pure.** `AuditViewModel`
  exposes `TriagedCount` (= would-be report findings − live report findings) and
  `HasTriaged`; the view renders the caveat beside the band. `AuditSummary.Compute` never
  sees the `[ack]`/`[suppress]` tag grammar (the ADR-028 Core/App boundary). The export
  header (ADR-030 D3) adds optional `ReportHeader.RulesetName`/`TriagedCount`/`UncheckedCount`;
  `ViolationReportExporter.ToHtml` renders those rows ONLY when `RulesetName is not null`
  — the binding property is that conditional rendering (a legacy 4-arg header shows zero
  Ruleset/Triaged/Unchecked rows), pinned scope-agnostically since the #329 revision
  (ADR-013 addendum 2026-07-11) deliberately re-pinned the exporter bytes (BOM,
  rectangular CSV, th-scope HTML).
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
