# RuleEngine contracts (AP 3.2 / ADR-009, binding for AP 3.3–3.4)

Pinned by tests in `tests/GroupWeaver.Tests/Core/Rules/RuleEngine*Tests.cs` and
the live-lab `tests/GroupWeaver.Tests/Providers/Ldap/RuleEngineLabIntegrationTests.cs`
(`RequiresAd`); change only with a reviewed PR that updates the tests deliberately.
Builds on [[rule-model]].

- **`RuleEngine.Evaluate(DirectorySnapshot, Ruleset)` — static, pure, sync,
  UI-free.** No scope-root parameter (the snapshot IS the scope; partial
  knowledge is the null-vs-empty tri-state). Never mutates the snapshot, never
  calls a provider, never throws on directory CONTENT (unresolvable DNs are
  values). Precondition: a loader-validated ruleset — a hand-built ruleset with
  an unsupported regex lets the `Regex` ctor exception propagate (programming
  error, not input error). Kinds re-resolved every call ⇒ re-evaluation after
  lazy expand re-judges by construction; **full re-run per expand and per
  ruleset edit (AP 3.3 preview), no incrementality** (ms at the 10K-edge target).
- **`snapshot.Edges` is read EXACTLY ONCE**, first statement, into a local —
  review-enforced only (`DirectorySnapshot` is sealed, not unit-testable; flag
  in every RuleEngine PR body). Nesting iterates it; the circular sweep derives
  its start set from it; nothing else touches the property.
- **`RuleViolation { RuleId, Severity, Dns, Message }`** — flat, `Dns[0]` is the
  AP 3.4 jump anchor. Per-rule `Dns` invariant (pinned): nesting `[parent,
  member]` (BOTH endpoints marked — the member node must not go dark, even as a
  raw-DN synthetic External node), naming `[subject]`, empty-group `[subject]`,
  circular = the canonical cycle rotation. `Message` is presentation only;
  identity lives in the structured fields. **Record equality is reference-based
  over `Dns` — consumers and tests compare PROJECTIONS (RuleId/Severity/Dns
  sequence/Message), never whole records.**
- **`RuleReport`** indexes are built once in the ctor, `Dn.Comparer`-keyed:
  `MaxSeverityByDn` (max over `Info<Warning<Error`; includes raw member DNs
  absent from the snapshot), `ViolationsFor(dn)` (unknown DN → empty, never
  throws, case-variant hits), `ViolationsAmong(dns)` (distinct, report order —
  the "n below" roll-up primitive). Aggregation is ENGINE-SIDE so GraphBuilder
  stays a pure topology projector and ruleset edits never force graph rebuilds.
- **Canonical cycle identity** (the cross-start identity ADR-006's `Walk`
  delegates here): rotate `Walk`'s path slice so the `Dn.Comparer`-minimal DN is
  first (unique on a gray path), **never reverse** (membership direction is
  meaningful); dedup across walks via an element-wise `Dn.Comparer` sequence
  comparer (case-variant first-encountered spellings collapse; first-found wins,
  deterministic via OrdinalIgnoreCase-sorted starts). Self-membership `[A]` is
  its own canonical form. Findings are deduped canonical back-edge cycles, **not
  exhaustive simple-cycle enumeration** (exponential, non-goal); every cyclic
  SCC yields ≥1 finding.
- **Circular sweep starts = distinct edge parents** (`Dn.Comparer` dedup, sorted
  OrdinalIgnoreCase), cumulative `seen` set, one `Walk` per unseen start. The
  sweep ALWAYS runs (it feeds `UncheckedDns`); only cycle→finding conversion is
  gated on `Circular.Enabled`. `MembershipTraversal.Walk` is the ONLY transitive
  traversal — never a second walk, never a black-set seed.
- **`UncheckedDns`** = every walk `Frontier` ∪ a one-pass `Objects` load-state
  scan (kind ∈ {GG,DL,UG,External} ∧ `!IsLoaded` — catches in-snapshot fetchables
  no walk reaches, e.g. LdapProvider's vanished-group arm). Deduped, sorted,
  **NEVER ignore-filtered** (load-state truth, not judgment) — ignored objects
  still surface here. This is the literal "unexpanded areas are unchecked."
- **Suppression order: global ignore → per-rule exceptions → check** (ADR-008
  §5). Raw DNs absent from the snapshot match only via `MatchEntry.MatchesDn`
  (dn entries; name entries never). Global ignore exempts edges on EITHER
  endpoint and cycles on ANY cycle DN; nesting exceptions honor endpoint
  narrowing (`Parent`/`Member`/`Any`). Nesting may evaluate the matrix cell
  before suppression (observationally equivalent — suppressing a non-finding is
  a no-op — and cheaper).
- **Determinism:** `Violations` ordered by `RuleViolationComparer`
  (`EnumerateRules` block order, then element-wise OrdinalIgnoreCase over `Dns`,
  shorter-prefix-first) — independent of insertion and dictionary order.
- **Complexity** (V objects, E edges, S distinct loaded parents, R naming
  rules): nesting O(E) + globs only on non-allow cells; naming O(V·R)
  linear-time regex; empty O(V); circular typical O(V+E), worst O(S·(V+E))
  (`Walk` is memoryless across calls — accepted at v0.1; a fix would be an
  ADR-006-reviewed `Walk` change, never a second walk).
- **AP 3.2 demo baseline is executable** (full snapshot, default ruleset):
  exactly 19 findings — 3 nesting errors, 1 cycle error, 3 naming warnings,
  12 empty-group infos, 0 External infos; `UncheckedDns` = the two ignored
  builtin member DNs. Both builtin edges tested both ways (entry removed ⇒
  exactly that one DL←External info appears). Lab: same shape with the FSP edge
  as the sole External source; partial-scope `OU=Groups` load turns DL←User
  errors into DL←External infos and puts user DNs in the frontier — the live
  proof of the External-column rationale.
