# ADR-015: Gap analysis — diff the live Ist structure against the proposed Plan

## Context

v0.3 (PLANNING.md E7 step 3, #66) compares the **actual** loaded AD structure
(the "Ist" `DirectorySnapshot`) against the **proposed** `PlanModel` and visualizes what is
added, removed, or unchanged — building directly on Plan Mode (ADR-014). GroupWeaver stays
read-only toward AD (CLAUDE.md non-negotiable): the diff only *reads* both structures.

The existing pieces are deliberately reusable: `PlanProjection.ToSnapshot` already turns a
plan into a `DirectorySnapshot`, so a diff is a comparison of two homogeneous snapshots;
`GraphBuilder` is a pure, report-blind topology projector (ADR-009/010); the wire layer
(`GraphJson.MapNodes/MapEdges`) is the single choke point where ADR-010 added severity. The
hard questions are: how Ist and Plan DNs align, where diff status lives without breaking
GraphBuilder's blindness, how the Ist load-state tri-state (ADR-005) is handled honestly, and
how the result is surfaced without polluting the pinned `RuleReport`. Decided by a judged
3-way design panel (minimal-wire / first-class-model / pragmatic-MVP → adversarial synthesis).

## Decision

1. **A pure-Core diff, reusing the engine's discipline.** New
   `SnapshotDiff.Compute(DirectorySnapshot ist, DirectorySnapshot plan)` (plan =
   `PlanProjection.ToSnapshot(planModel)`, verbatim) — static, deterministic, total, UI-free,
   never throws on directory content, calls no provider, mutates neither input (mirrors
   `RuleEngine.Evaluate`). Result `SnapshotDiff`: `NodeStatus`
   (`IReadOnlyDictionary<string, DiffStatus>`, `Dn.Comparer`-keyed), `EdgeStatus`
   (`IReadOnlyDictionary<MembershipEdge, DiffStatus>` — `MembershipEdge` reused as the key, it
   already hashes via `Dn.Comparer`), and `UncheckedParents` (sorted OrdinalIgnoreCase).
   `enum DiffStatus { Common, Added, Removed, Unchecked }`. `ist.Edges`/`plan.Edges` are each
   read **exactly once** into a local (the O(E)-recompute perf contract; review-enforced —
   `DirectorySnapshot` is sealed — and flagged in every `SnapshotDiff` PR body).

2. **DiffStatus lives nowhere in the Core graph model.** `GraphNode`, `GraphEdge`,
   `GraphModel`, and `GraphBuilder` stay pure, report-blind **and** diff-blind topology
   projectors (ADR-009/010 unchanged). Diff status reaches cytoscape **only** as new wire
   fields, joined at the single existing choke point: `GraphJson.MapNodes` gains an optional
   `nodeDiffMap` param + a `WhenWritingNull`-ignored `NodeDto.Diff`; `MapEdges` gains an
   optional `edgeDiffMap` param + a `WhenWritingNull`-ignored `EdgeDto.Diff` (the one genuine
   new signature — edges carried no report before). Tokens come from a `DiffWire.ToToken`
   helper decoupled from the enum (mirrors `SeverityWire`). A null map yields **byte-identical**
   output to today's wire — a pinned regression guard protects every existing flat/chunked/
   severity test. The severity `overlay-*` channel is **not** multiplexed (a node can be both
   Added and a finding anchor).

3. **The Gap graph is rendered from a union snapshot.** Pure helper
   `SnapshotDiff.BuildUnion(ist, plan)` `AddObject`s every DN from both sides with
   **deterministic Ist-wins** on Common DNs (the real loaded object; `AddObject` is latest-wins,
   so order is fixed and documented) and `SetMembers` the per-parent union, so
   `GraphBuilder.Build(union, RootDn)` — **unchanged** — materializes every Added, Removed, and
   Common node for painting.

4. **Exact-DN matching, gated on `BaseOuDn == RootDn`.** Matching is exact-DN only under
   `Dn.Comparer`. `PlanModel.FormDn` forms `CN=<rfc4514>,<BaseOuDn>`, so when the plan's base OU
   equals the Ist scope root an authored object's DN is byte-identical to its Ist counterpart —
   the natural authoring flow, since `OnDesignPlan` already seeds `BaseOuDn = workspace.RootDn`.
   Gap is offered/computed **only** when `Dn.Comparer.Equals(plan.BaseOuDn, workspace.RootDn)`;
   otherwise the Gap command is `CanExecute = false` with a banner naming the required root
   (a mismatch would yield a useless all-Added/all-Removed diff). Relative-DN re-base alignment
   and name/SAM fuzzy matching are deferred — re-canonicalization is forbidden by the data-model
   contract ("DN strings stored as-given").

5. **Honest load-state via the tri-state.** Node diff is always total. An edge under an Ist
   parent whose `GetMembers` is `null` (unexpanded) is classified `Unchecked` — never falsely
   Added or Removed — and the parent goes to `UncheckedParents`. `GapViewModel.HasUncheckedAreas`
   drives a loud banner ("N Ist areas unexpanded — edges there are shown unchecked, not removed;
   reload/expand for a complete diff"), mirroring the existing `RuleReport.UncheckedDns` honesty.
   A whole-scope reload is **offered, never auto-run** (ADR-005).

6. **A synthesized `GapReport`, separate from `RuleReport`.** Gap answers "what changed," not
   "what is wrong." Pure `GapReportBuilder.Build(diff, ist, plan)` yields a flat, deterministically
   ordered list of `GapFinding { GapKind, IReadOnlyList<string> Dns (Dns[0] = jump anchor),
   presentation-only Message }`, `GapKind ∈ {NodeAdded, NodeRemoved, EdgeAdded, EdgeRemoved,
   UnverifiableArea}`. It is a **separate** Core type (it never pollutes the pinned `RuleReport`
   or mints `RuleId`s) but is shaped identically, so the App reuses the existing
   `ViolationRowModel`/sidebar/`FocusAsync` machinery via a thin adapter, plus a `GapSummary`
   counts line. The plan's own `RuleReport` (rule compliance) stays available in Plan Mode.

7. **A sibling `GapViewModel`, ADR-014 dispose discipline preserved.** Entered from Plan Mode
   via `ShellViewModel.OnGapAnalysis(plan, workspace)` (a sibling of `OnDesignPlan`). The shell
   holds both live steps, so it reads `workspace.Snapshot` directly and passes it **borrowed**
   (read-only, never owned, never disposed) plus `plan.Plan`. `GapViewModel` owns its **own**
   `IGraphRenderer`, is `Track()`-ed in `_disposableSteps`, disposed only at teardown; Back
   returns the **same** never-disposed `PlanViewModel`. It never mutates the borrowed Ist snapshot
   (the diff only reads `Objects`/`Edges`/`GetMembers`/`IsLoaded`), so ADR-005 append-only holds
   by construction.

8. **One new renderer-seam method.** `IGraphRenderer.ShowDiffGraphAsync(GraphModel union,
   SnapshotDiff diff, CancellationToken)` as a **default no-op** (fakes inherit it, mirroring
   `ExportPngAsync`) — *not* a new positional arg on `ShowGraphAsync`, so every pinned
   Workspace/Plan test stays green. It commits with `graphCommit` (destroy+fit): a gap render is
   a fresh wholesale topology, never `UpdateGraphAsync`. `graph.js` copies `n.diff`/`e.diff` into
   data; new `node[diff=…]`/`edge[diff=…]` style rules ride channels **disjoint** from kind
   (background/shape), root+External (border-*), and severity (overlay-*): edges use
   line-color/style/opacity; nodes use background-dim + a diff tint. A `verify.mjs` DIFF tripwire
   pins the palette and proves diff + severity read independently and legibly together.

## Consequences

- The **only** new Core files are `src/Core/Diff/` (`DiffStatus`, `SnapshotDiff` with
  `Compute`+`BuildUnion`, `GapReport`+`GapReportBuilder`). `DirectorySnapshot`, `PlanModel`,
  `PlanProjection`, `GraphBuilder`, `GraphNode/Edge/Model`, `RuleEngine`, `RuleReport` are
  untouched — the pinned contracts hold.
- Read-only is absolute on the entire Gap path: no `Set-AD*`/`New-AD*`/`Remove-AD*`/
  `CommitChanges`; the detail panel stays whitelist-only (reviewer confirms per slice).
- Node visual-channel scarcity is the sharpest implementation risk (kind owns background+shape,
  root/External own border-*, severity owns overlay-*): node diff routes onto background-dim +
  tint and **must** be proven legible against a node that is simultaneously Added + Warning-halo +
  External-border at 200 nodes (the combined-case ui-verifier check).
- A Common-DN **kind clash** (plan authors GG where Ist loaded a User at the same DN) is silently
  `Common` in v0.3 because `Modified` is cut — documented, and the strongest argument for the
  deferred Modified follow-up.
- Decomposed into 9 implementation slices (Core diff → union/summary → GapReport → wire →
  graph.js+verify → renderer seam → GapViewModel → shell wiring → GapView+checklist), each a
  vertical TDD slice with the standard DoD; this ADR is the design contract they implement. A
  `.claude/rules/` entry records the durable contract ("Gap diff is wire-only; GraphBuilder stays
  diff-blind; `SnapshotDiff` reads `Edges` once").

## Rejected alternatives

- **DiffStatus on `GraphNode`/`GraphEdge` (a Core graph-model field):** breaks GraphBuilder's
  report-blind/diff-blind purity (ADR-009/010); the wire-field-at-the-choke-point pattern reuses
  the exact severity precedent.
- **Multiplexing diff onto the severity `overlay-*` channel:** a node can be Added *and* a finding
  anchor simultaneously; collapsing the axes loses information and breaks the AP 3.4 severity
  tripwire.
- **A `DiffGraphModel` wrapper / parallel render path:** forces a second renderer seam and
  re-chunking; the union-snapshot-through-the-unchanged-builder reuses `GraphChunker`'s single
  slicing path verbatim.
- **`Func<DirectorySnapshot?>` indirection for the Ist reference:** the shell already holds both
  live steps, so a borrowed read-only reference passed directly is simpler and equally
  ADR-014-clean.
- **Injecting gap rows into the `RuleReport`:** pollutes the pinned report contract and forces
  `RuleId` churn; a separate identically-shaped `GapReport` reuses the sidebar without the cost.
- **Name/SAM fuzzy matching or relative-DN re-basing now:** needs canonicalization the data-model
  contract forbids; deferred with the explicit `BaseOuDn == RootDn` gate.
- **Forcing a whole-scope reload before the diff:** surprising and potentially a long blocking
  fetch (ADR-005); the honest `Unchecked` banner offers it instead.

**Deferred to named follow-ups (each its own ADR-gated work package):** `DiffStatus.Modified`
(kind/SAM delta on a Common DN), cross-scope re-base alignment (`BaseOuDn != RootDn`), forced
pre-diff whole-scope reload, and merging gap findings into the `RuleReport`.
