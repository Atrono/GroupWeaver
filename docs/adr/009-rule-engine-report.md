# ADR-009: RuleEngine evaluation & report contract

**Status:** Accepted · **Date:** 2026-06-13
**Decides:** PLANNING.md AP 3.2 (RuleEngine) + issue #38 · **Phase:** 3

## Context

AP 3.3 (live preview re-evaluates on every ruleset edit) and AP 3.4 (per-node
max-severity indicator, "n below" roll-up, violations sidebar with jump-to-node,
"unexpanded areas are unchecked" hint) consume one evaluation result. DN is the
sole identity everywhere (data-model contract); GraphNode/NodeDto carry the DN
verbatim, so a Dn.Comparer-keyed report maps 1:1 onto rendered nodes. Walk
(ADR-006) delegates cross-start cycle identity to this AP.

## Decision

1. **`RuleEngine.Evaluate(DirectorySnapshot, Ruleset)` — static, pure, sync,
   no scope root, no incrementality.** The snapshot is the scope; full re-run
   per lazy expand and per ruleset edit (ms at the 10K-edge target). Kinds are
   re-resolved every call. `Edges` is read exactly once into a local (review-
   enforced; the type is sealed). Naming regexes compile per call
   (`NonBacktracking|CultureInvariant`) — no static memo, so AP 3.3 preview
   keystrokes never intern into process memory.
2. **Flat result:** `RuleViolation { RuleId, Severity, Dns, Message }` with the
   Dns[0]-anchor invariant — nesting `[parent, member]` (both endpoints marked;
   the member node must not go dark, even when it is a raw DN rendered as a
   synthetic External node), naming/empty `[subject]`, circular = canonical
   cycle. Messages are presentation; identity is structured. Record equality is
   reference-based over Dns — consumers compare projections.
3. **Canonical cycle identity:** rotate Walk's path slice to the
   Dn.Comparer-minimal DN (unique on a gray path), never reverse; dedup across
   walks element-wise under Dn.Comparer; sweep = sorted distinct loaded parents
   (from the one Edges read), skip-if-seen, cumulative seen set. Findings are
   deduped canonical back-edge cycles, not exhaustive simple-cycle enumeration
   (exponential, non-goal); every cyclic SCC yields >= 1 finding. The sweep runs
   even with `circular` disabled — it feeds the frontier.
4. **`UncheckedDns`** = walk frontiers ∪ {in-snapshot fetchable kinds with
   `GetMembers == null`} (the load-state scan covers parents no walk reaches,
   e.g. LdapProvider's vanished-group arm), deduped, sorted, and NEVER filtered
   by ignore — load-state truth, not judgment.
5. **Suppression order:** global ignore → per-rule exceptions → check. Raw DNs
   absent from the snapshot are matched only via `MatchEntry.MatchesDn`
   (dn entries; name entries never). Global ignore exempts edges on either
   endpoint and cycles on any cycle DN; nesting exceptions honor endpoint
   narrowing. Evaluating the matrix cell before suppression is an
   observationally equivalent optimization.
6. **Determinism:** violations ordered by EnumerateRules() block order, then
   element-wise OrdinalIgnoreCase over Dns; independent of insertion and
   dictionary order. Aggregation (`MaxSeverityByDn`, `ViolationsFor`,
   `ViolationsAmong`) lives engine-side — GraphBuilder stays a pure topology
   projector; ruleset edits never force graph rebuilds.

## Consequences

- Demo baseline is executable: 3 nesting errors, 1 cycle error, 3 naming
  warnings, 12 empty-group infos, 0 External infos; UncheckedDns = the two
  ignored builtin DNs (the hint has demo coverage by construction).
- Both-endpoint attribution escalates member-node colors (DL_Nested_RO renders
  Error); AP 3.4 can render anchor-only via Dns[0] without an engine change.
- Worst-case circular sweep O(S·(V+E)) on many-roots-into-shared-subgraph
  snapshots; acceptable, revisit only with a profiled forest (would need an
  ADR-006-reviewed Walk change, never a second walk).

## Rejected alternatives

Incremental/dirty-region evaluation (unjustified at target scale); Tarjan SCC
condensation (a second transitive walk — violates the Walk mandate); seeding
Walk with a black set (changes a pinned ADR-006 contract for a non-problem);
parent-only nesting attribution (member indicator goes dark; escape hatch kept);
GraphBuilder-side severity merge (couples ruleset edits to rebuilds); violation
class hierarchy (UI binds flat); severity-sorted report (unstable under live
preview); static naming-regex memo (preview keystrokes leak into process cache);
ignore-filtered frontier (would hide unchecked areas the hint exists to surface).
