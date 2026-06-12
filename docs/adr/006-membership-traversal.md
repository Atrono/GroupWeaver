# ADR-006: Membership traversal — visited-DN walk, cycles as values, frontier

**Status:** Accepted · **Date:** 2026-06-12
**Refines:** ADR-005 (which deferred visited-DN tracking here) for AP 2.4 · **Phase:** 2

## Context

No code in `src/` traverses membership edges transitively: providers load flat,
GraphBuilder climbs only acyclic DN ancestry, and lazy expand is one level per
gesture (ADR-005: "cycle safety needs no mechanism" at the gesture level).
Cycle termination at the UX level is already pinned by four tests. What is
missing is the shared primitive the next phases build on: AP 3.2's RuleEngine
circularity check must detect and REPORT cycles as violations (with a
jump-to-node DN path), and AP 3.4's roll-up aggregation needs the reachable
loaded descendants of a node plus the fact that unloaded territory exists below
("Nicht expandierte Bereiche sind ungeprüft"). Cycles are legal data in AD
(the lab seeds GG_Circle_A↔GG_Circle_B deliberately); the snapshot stores them
as-is — "cycle handling is the consumer's concern".

## Decision

1. **One walk primitive.** `MembershipTraversal.Walk(DirectorySnapshot, string
   startDn)` in `src/Core/Graph/` — pure, deterministic, UI-free, never calls a
   provider. Iterative DFS (explicit stack; no recursion — deep nesting and
   10K-member groups must not risk stack depth) down the membership digraph
   (group → member), children in stored SetMembers order. Adjacency is read via
   `snapshot.GetMembers(dn)` per node — NEVER `snapshot.Edges` (O(E) recompute
   per access, and only GetMembers carries the null-vs-empty tri-state).
2. **Result record `MembershipWalk(Visited, Cycles, Frontier)`.**
   - `Visited`: every DN reached, startDn first, DFS preorder, no duplicates
     under `Dn.Comparer`; DN strings as first encountered, never canonicalized.
   - `Cycles`: one entry per back edge u→v with v on the current DFS path
     (white/gray/black coloring keyed via `Dn.Comparer` — a plain visited set
     would misreport diamonds as cycles): the path slice `[v..u]`; the closing
     edge u→v is implied last→first. Self-membership yields a single-element
     path. Bounded by construction: Count ≤ membership edge count. The rotation
     is start-relative; cross-start cycle identity/normalization is AP 3.2's.
   - `Frontier`: subset of Visited in visit order — DNs whose
     `GetMembers == null` (never loaded) AND whose kind (via `GetKind`, unknown
     → External) is in ADR-005's fetchable set {GG, DL, UG, External}.
     Loaded-and-empty is NOT frontier; users/computers/OUs are leaves, never
     frontier (their members are never fetched — flagging them would gut the
     AP 3.4 hint). "Frontier" is congruent with "what a double-click would
     fetch".
3. **Cycles are values, never exceptions** (the "unresolvable is a value"
   philosophy): a cyclic directory must stay renderable and auditable — the
   cycle path IS the violation payload AP 3.2 reports.
4. **No defensive depth/visit bounds in production.** The visited set
   guarantees O(V+E) termination on the finite in-memory snapshot, and the walk
   takes no provider — there is no I/O channel to become unbounded through.
   Unboundedness guards live where data enters (paging, MemberCollector's 10K
   bound, mandatory root filter). Tests keep an independent step-count bound
   (≤ V+E+1) instead — proof without trusting the implementation.
5. **No app wiring in this package.** Consumers arrive with AP 3.2/3.4; the
   expand pipeline has no traversal to protect (ADR-005). No UI diff → the
   screenshot DoD step is N/A.

## Consequences

- AP 3.2/3.4 MUST consume this walk instead of re-rolling one (rule added to
  `.claude/rules/data-model.md`). A whole-snapshot cycle inventory is a
  composition (walk from each group DN in sorted order, dedupe by canonical
  rotation) or a deliberate later entry point — granularity is AP 3.2's call.
- The null≠empty GetMembers contract gains its first load-bearing consumer.
- The walk is pinned by hand-built snapshots (incl. self-membership and
  case-variant closing edges), the seeded demo cycle, and live-lab tests
  including "cycle hidden behind frontier until expanded".

## Rejected alternatives

All-simple-cycles enumeration (exponential in general; granularity belongs to
AP 3.2); Tarjan/SCC entry point now (speculative — no consumer until 3.2 fixes
the violation unit); `snapshot.Edges`-based adjacency (O(E) per access perf
rule; loses the null tri-state); depth/visit bounds (visited set + finite
snapshot suffice; bounds add a partial-result failure mode); provider-calling
traversal (unbounded I/O; breaks ADR-005's one-level contract and the
snapshot's single-thread contract); recursive DFS (stack depth); raw-null
frontier without the kind filter (flags every leaf user as unchecked); app
wiring/debug surface (no v0.1 consumer).
