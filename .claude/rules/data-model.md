# Core data-model contracts (AP 1.3, binding for later phases)

Pinned by tests in `tests/GroupWeaver.Tests/Core/`; change only with a reviewed
PR that updates the tests deliberately.

- **DN is the sole identity.** Compared via `Dn.Comparer` (OrdinalIgnoreCase) —
  EVERY DN-keyed dictionary/set/equality in the codebase must use it (cycle
  detection in AP 2.4 depends on consistent keying). DN strings are stored
  as-given, never canonicalized/rewritten. No GUID/SID properties; `AdObject`
  has no equality override.
- **`DirectorySnapshot.GetMembers`: `null` ≠ empty.** `null` = parent never
  loaded (lazy expand AP 2.3, "unexpanded areas unchecked" AP 3.4); empty
  list = loaded and genuinely empty (empty-group check AP 3.2). `SetMembers`
  REPLACES prior members (refresh semantics) and de-duplicates
  case-insensitively. Not thread-safe by contract.
- **`DirectorySnapshot.Objects` is APPEND-ONLY — there is NO `RemoveObject`, and
  there must not be one.** Removing a loaded object would tear the `IsLoaded` /
  null-vs-empty tri-state (a removed loaded parent reads as "never loaded" =
  AP 3.4 "unexpanded unchecked" = a lie) and resurrect `MembershipTraversal.Walk`
  frontier nodes. Orphan ex-member nodes after a member-dropping refresh are
  cured by **whole-scope reload** (`ReloadScopeCommand` rebuilds a FRESH snapshot
  from `LoadScopeAsync`; issue #30, ADR-005 addendum), never by mutating the
  current one. Graph-layer reachability pruning is the only sanctioned future
  alternative and needs its own ADR (it breaks GraphBuilder totality).
- **Unresolvable is a value, never an exception.** Unknown DN → `null`
  (`GetObjectAsync`) / `Kind.External` (members, `GetKind`) / empty list
  (`GetMembersAsync` on vanished parent). Only directory-unreachable/bind
  failure throws (`DirectoryUnavailableException`). FSPs map to `External`
  (AP 1.5).
- **Kind mapper** (`AdObjectKindMapper`): `computer` beats `user` in objectClass
  chains; groupType scope bits 0x2/0x4/0x8, builtin 0x1 → DomainLocal, security
  bit 0x80000000 IGNORED (scope ≠ security/distribution category — distribution
  groups are out of scope until v0.4); scopeless/null groupType and unrecognized
  chains → `External`. DemoProvider JSON stores `kind` strings directly and does
  NOT go through the mapper (AP 1.4).
- **`AdObject.Attributes`** is case-insensitive-keyed and provider-filtered
  (whitelist enforcement is the provider's job, AP 1.5); init-copy THROWS on
  case-duplicate keys — providers must not produce them. The detail panel
  (AP 2.5) may bind only `Attributes` + the typed members (Dn/Kind/Name/
  SamAccountName).
- **Perf note:** `DirectorySnapshot.Edges` is recomputed O(E) per access —
  GraphBuilder (AP 2.2) must read it once per build, never in a per-node loop.
- **`MembershipTraversal.Walk` (AP 2.4) is the ONLY sanctioned transitive
  membership walk:** iterative DFS, `Dn.Comparer`-keyed visited/gray sets, reads
  `GetMembers` per node (NEVER `Edges`), cycles and frontier are result values
  (no throw, no depth bound); frontier kind set = ADR-005 fetchable kinds.
  AP 3.2/3.4 must consume it, not re-roll a walk.
