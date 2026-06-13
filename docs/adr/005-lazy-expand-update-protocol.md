# ADR-005: Lazy expand — replace-in-place updates, cache policy, refresh

**Status:** Accepted · **Date:** 2026-06-12
**Refines:** ADR-004 (wire contract, renderer seam) for AP 2.3 · **Phase:** 2

## Context

AP 2.3 adds double-click lazy expand, a snapshot-backed cache, and a per-node
refresh. The wire contract (ADR-004 D4) only knows full re-init
(graphChunk→graphCommit: cy.destroy + fit — loses viewport), edge wire ids are
per-build counters (unstable across builds), and the global concentric layout
repositions EXISTING nodes on every build — an add-only protocol would void the
no-overlap proof. `LoadScopeAsync` already marks every in-scope group
members-loaded, so fetches only happen at the out-of-scope frontier
(group-rooted scopes, External endpoints) and on refresh. AP 2.4 (traversal
cycle protection) and AP 3.4 ("unexpanded areas unchecked") build on the
null-vs-empty load-state contract; nothing here may fabricate it.

## Decision

1. **Replace-in-place update protocol.** Reuse the graphChunk accumulator; new
   commit verb `graphUpdate`: one `cy.batch` removes all elements and adds the
   accumulated set on the LIVE instance — no destroy, no fit, viewport and
   handlers (bound on the cy core) untouched. Confirmation reuses `loaded`
   with post-update totals. `graphUpdate` before any `graphCommit` → `jsError`.
2. **Renderer seam grows two methods.** `UpdateGraphAsync(GraphModel)`
   (chunks + graphUpdate, awaits loaded) and `FocusAsync(dns)` (sends `focus`,
   awaits the now-parsed `focused`). Same single-flight guard and 60 s
   bounded-wait → RendererError-and-return-normally policy as ShowGraphAsync.
3. **Expand semantics.** Fetch iff snapshot kind ∈ {GG, DL, UG, External} and
   not IsLoaded; everything else (loaded nodes, users, computers, OUs) gets
   FocusAsync(node + cached members) — never a fabricated SetMembers. Fetch
   order is transactional: GetObjectAsync (only if the DN is not in
   snapshot.Objects — resolves External frontier nodes to their true kind),
   GetMembersAsync, THEN AddObject(s) + SetMembers, rebuild, UpdateGraphAsync,
   GraphSummary, FocusAsync(parent + members). One global busy gate (IsLoading
   reused); overlapping gestures are dropped, not queued (snapshot is not
   thread-safe; everything stays sequential on the UI thread). Errors mirror
   the load policy: DirectoryUnavailable → LoadError (cleared on each new
   attempt), cancellation quiet, everything else propagates via the observable
   `Expansion` task (Initialization pattern, never fire-and-forget).
4. **Refresh = forced expand of SelectedDn.** Button in the detail-column
   header (native chrome, never over GraphHost); enabled iff a fetchable kind
   is selected and not busy; label "Refresh" (shipped UI strings are English).
   SetMembers REPLACES: stale membership edges vanish; ex-member NODES remain
   (snapshot never removes objects; GraphBuilder totality) — in-scope ones
   keep a still-true containment edge, expanded out-of-scope ones linger on
   the outer ring. Accepted for v0.1.
5. **Expanded vs. collapsed is visible by kind resolution:** frontier nodes
   render External (dashed gray); expansion resolves them to true kind
   color/shape. No extra wire fields.

## Consequences

- Wire contract delta is one command verb + one parsed message (`focused`);
  JS stays a dumb accumulator; chunked and flat output still share GraphJson.
- Every expand recomputes the whole layout (global rings) — existing nodes
  move; the deliberate FocusAsync camera move replaces viewport-restore logic
  and animation concerns.
- Cycle safety needs no mechanism: one level per gesture, no traversal;
  AP 2.4's visited-DN tracking stays out of this package.
- Orphan ex-member nodes and whole-scope reload are explicit follow-ups.

**Reload scope (issue #30, 2026-06-13):** `ReloadScopeCommand` re-runs
`LoadScopeAsync(RootDn)` → fresh `DirectorySnapshot` → `GraphBuilder.Build` →
`ShowGraphAsync` (destroy+fit; the viewport reset is honest — topology is
wholesale-new, so `UpdateGraphAsync`'s in-place preservation is meaningless).
It clears `SelectedDn`/`LoadError`, re-Evaluates against the *live* ruleset, and
shares one `RunScopeLoadAsync` body with the ctor load (single build site).
Because the fresh snapshot's `Objects` are rebuilt from scratch, reload has no
ex-member objects — it cures the D4 orphan residual **by construction, with no
pruning code**. The snapshot stays append-only (no `RemoveObject`). Graph-layer
reachability pruning for the node-Refresh-without-reload residual is deferred to
its own follow-up issue and would need its own ADR: it breaks `GraphBuilder`
totality (ADR-004 D1 / ADR-009 D6) and creates graph-vs-report node-set
divergence (a pruned node could still carry a finding the sidebar lists).

## Rejected alternatives

Full re-show per expand (destroy+fit loses viewport, flashes); id-based diff
protocol (edge ids are per-build counters — unsound); add-only protocol
(global layout moves existing nodes; voids the no-overlap proof); mode flag on
ShowGraphAsync (show and update have different post-conditions/tests);
per-node in-flight tracking (snapshot not thread-safe; complexity without
benefit); queueing gestures (hides caller bugs); SetMembers([]) on OU/user
double-click (fabricates load state AP 3.2/3.4 depend on); German UI label
(shipped strings are English); whole-scope refresh (different operation,
backlog).
