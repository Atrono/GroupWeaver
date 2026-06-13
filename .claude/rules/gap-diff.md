# Gap diff contracts (ADR-015 / #66, binding for the v0.3 Gap slices)

Pinned by tests in `tests/GroupWeaver.Tests/Core/Diff/SnapshotDiffTests.cs`; change
only with a reviewed PR that updates the tests deliberately. Builds on [[data-model]].

- **Gap diff is WIRE-ONLY.** `GraphBuilder` stays a pure report-AND-diff-blind
  topology projector (ADR-009/010 unchanged); `DiffStatus` lives only in
  `SnapshotDiff` and the wire DTOs, NEVER as a field on `GraphNode`/`GraphEdge`/
  `GraphModel`. Diff status reaches cytoscape solely through new
  `WhenWritingNull`-ignored wire fields joined at the existing `GraphJson` choke
  point (a null map => byte-identical wire output).
- **`SnapshotDiff.Compute(ist, plan)` is static, pure, deterministic, total,
  UI-free.** Never calls a provider, never mutates either input, never throws on
  directory CONTENT (mirrors `RuleEngine.Evaluate`). `NodeStatus` is
  `Dn.Comparer`-keyed; `EdgeStatus` is keyed by `MembershipEdge` (already hashes
  via `Dn.Comparer`). Result dictionaries do NOT override `Equals` — consumers
  compare PROJECTIONS (sorted (dn/edge, status) pairs + the `UncheckedParents`
  sequence), never record/dictionary identity.
- **`ist.Edges`/`plan.Edges` are each read EXACTLY ONCE** into a local (first two
  statements) — the O(E)-recompute perf contract (`DirectorySnapshot.Edges`,
  [[data-model]]). Review-enforced only (`DirectorySnapshot` is sealed, not
  unit-testable); flag it in every `SnapshotDiff` PR body.
- **The Unchecked rule (honest tri-state, ADR-005/ADR-015 D5):** a PLAN edge under
  a parent that is a KNOWN Ist object whose members were never loaded
  (`ist.TryGetObject(P, out _) && !ist.IsLoaded(P)`) is `Unchecked` — never falsely
  Added/Removed — and `P` enters `UncheckedParents` (distinct, sorted
  `StringComparer.OrdinalIgnoreCase`). It gates on KNOWN-Ist-object, NOT on
  load-state alone: a PLAN-ONLY parent (no Ist object) is genuinely new => its
  edge is `Added` and it NEVER enters `UncheckedParents`; a loaded-EMPTY `[]` Ist
  parent is loaded => its plan edge is `Added`, not `Unchecked`. An Ist edge always
  implies its parent is loaded, so only a plan edge can be `Unchecked`.
- **Direction:** plan is the proposed target. Plan-only => `Added`; ist-only =>
  `Removed`; in both => `Common`. NodeStatus is over the union of `ist.Objects` /
  `plan.Objects` DNs; EdgeStatus over the union of the two `Edges` sets.
