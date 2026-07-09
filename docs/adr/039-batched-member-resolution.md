# ADR-039: Batched member resolution — chunked filter search replaces per-member binds

**Status:** Accepted · **Date:** 2026-07-09
**Decides:** issue #288 (N+1 LDAP round-trips on member expand, found in the structural risk assessment) · **Phase:** 4 (feedback-driven)
**Builds on:** ADR-005 (provider error model: unresolvable is a value), ADR-031 D5 (filters were fixed literals — this ADR is the one place that changes), `.claude/rules/lab-environment.md` (AD quirks), `.claude/rules/data-model.md` (DN identity via `Dn.Comparer`)

## Context

`LdapProvider.GetMembersAsync` (`src/Providers/LdapProvider.cs:182-215`) collects a
group's member DN list via `MemberCollector`/ranged retrieval (already efficient —
one call plus follow-ups per 1500-value page), then resolves **each member DN
individually**: `FindEntry(memberDn)` opens a fresh `DirectoryEntry` +
`DirectorySearcher` base-scope bind per member. Expanding one 10K-member group is
~10K separate LDAP round-trips. This runs on every node lazy-expand
(`WorkspaceViewModel.cs:1092`) and is the single largest gap between the documented
10K-edge/ms-level perf target and actual runtime behavior — no test currently proves
the target holds, and this finding is why.

The fix is a batched subtree search: one `DirectorySearcher` per chunk of member
DNs, filtered by `(|(distinguishedName=dn1)(distinguishedName=dn2)…)`, instead of one
bind per DN. This is the first place in the codebase that composes an LDAP **filter**
from directory-derived data — every existing filter (`AnyObjectFilter`, the
OU/group filter in `GetRootCandidatesAsync`) is a fixed string literal, and ADR-031
D5 explicitly relied on that ("user input never composes a filter"). Member DNs are
directory content, not user input, but the same injection mechanics apply: an
unescaped `*`, `(`, `)`, or `\` in a DN (all legal in an RDN, e.g. via `\28`/`\29`
escaped literals AD already returns unescaped in some property values) could widen or
truncate the filter. This ADR is the one place that defense now needs to exist.

## Decision

### D1 — Chunked, filter-escaped subtree search replaces per-member `FindEntry`.

`GetMembersAsync` collects the member DN list exactly as today, then resolves it in
one pass via a new `ResolveBatch(dns, cancellationToken)`: chunks the DN list into
groups of `MemberBatchSize` (200 — conservative relative to AD's default LDAP admin
limits, keeps individual filter strings small), builds one OR filter per chunk, and
runs one `DirectorySearcher` (subtree scope, `AttributeWhitelist.FetchProperties`)
per chunk against a single shared `DirectoryEntry` bind. A member DN not present in
the results (vanished, or genuinely unresolvable) falls back to `MakeExternal`,
exactly as the old per-member `FindEntry`-returns-null path did — the provider error
model (unresolvable is a value) is unchanged.

### D2 — The search root is the domain naming context, not the configured scope base.

Members can live outside the provider's configured `baseDn` (the FSP
cross-forest-SID test fixture is the concrete proof: `CN=ForeignSecurityPrincipals`
sits outside `OU=AGDLP-Lab`). `EffectiveBaseDn()` keeps its existing
`_baseDn`-or-`defaultNamingContext` fallback for scope loading; a new
`DomainNamingContext()` always reads/caches `defaultNamingContext` regardless of
`_baseDn`, and batched resolution roots there — a subtree search from the whole
domain NC finds any in-domain object exactly as the old base-scope bind-anywhere
`FindEntry` could.

### D3 — New `LdapFilter.Escape`: the RFC 4515 defense for the new filter-composition surface.

A new `internal static class LdapFilter` (mirrors `AdsPath`'s shape) escapes `\`,
`*`, `(`, `)`, and NUL as their two-digit hex form (`\5c`, `\2a`, `\28`, `\29`,
`\00`) per RFC 4515 §3. Every DN interpolated into the batch filter goes through it
— no exceptions, no "this DN is probably safe" shortcuts. This is now the
codebase's only filter-value escaper; `AdsPath.EscapeDn` (path escaping, `/` only)
is unrelated and unchanged.

### D4 — Resolved entries are matched back by `Dn.Comparer`.

The batch result dictionary is keyed `Dn.Comparer` (case-insensitive,
[[data-model]]) so a case-variant `distinguishedName` the directory returns still
matches the member-link DN that requested it — consistent with every other DN-keyed
structure in the codebase.

## Where the code lives

- `src/Providers/LdapFilter.cs` (new): RFC 4515 escaper, `internal static class`, same
  visibility/shape as `AdsPath`.
- `src/Providers/LdapProvider.cs`: `GetMembersAsync` calls `ResolveBatch` instead of
  per-DN `FindEntry`; new private `ResolveBatch`, `BuildDnFilter`, `DomainNamingContext`
  members; `EffectiveBaseDn` refactored to delegate to `DomainNamingContext` for its
  fallback arm (same cached field, no behavior change to existing callers).
- `tests/GroupWeaver.Tests/Providers/Ldap/LdapProviderIntegrationTests.cs`: existing
  `GetMembers_*` tests must keep passing unchanged (behavior-preserving from the
  caller's view); add a case proving a large member set resolves correctly in one
  pass and a case proving a metacharacter-bearing DN component doesn't break the
  batch filter.
- `tests/GroupWeaver.Tests/Providers/LdapFilterTests.cs` (new): unit tests for the
  escaper — pure, no AD needed.

## Security-review note

This is the one ADR in the corpus that **does** open a new filter-composition
surface (ADR-031 D5 previously closed it off entirely). The defense is D3:
`LdapFilter.Escape` is mandatory and total for every value entering the batch
filter — no directory-write path is added or touched (still `FindAll`/read-only,
no `CommitChanges`/`Invoke` anywhere near this change), no new deserialization, no
new file format. The threat model addition for `/security-review`
(`security-review-groupweaver` skill): a maliciously named AD object (an RDN
containing `)`, `*`, or `\`) must not be able to widen the batch filter to match
objects outside the intended member set, or degrade the search into an error. Both
are covered by escaping every DN before interpolation; `LdapFilterTests` pins the
escape table directly against RFC 4515's metacharacter set.

## Rejected alternatives

- **Search from the configured scope base instead of the domain NC.** Rejected —
  would silently drop out-of-scope members (the FSP case) back to `MakeExternal`
  even when they're actually resolvable, a behavior regression from today's
  bind-anywhere `FindEntry`.
- **One giant filter for the whole member list instead of chunking.** Rejected —
  unbounded filter length for very large groups risks exceeding LDAP server admin
  limits (`MaxReceiveBuffer` etc.) with no page-size-independent ceiling; chunking at
  a fixed, documented `MemberBatchSize` keeps every request small regardless of
  group size.
- **`System.DirectoryServices.Protocols` (S.DS.P) instead of ADSI `DirectorySearcher`.**
  A genuinely lower-level batching API, but a bigger surface change (different
  connection/auth model) than this fix needs; `DirectorySearcher` already supports
  everything D1-D4 require (subtree scope, paging, property whitelisting). Worth
  revisiting only if a future ADR needs connection-level control ADSI doesn't expose
  (e.g. ADR-040's explicit LDAPS/sealing option is a candidate for that discussion).
- **Reuse `AdsPath.EscapeDn` for filter values.** Rejected outright — it escapes `/`
  for ADsPath syntax, an entirely different escaping domain from RFC 4515 filter
  metacharacters; using it here would be a false sense of safety.

## Consequences

- **Perf:** member expansion drops from O(members) LDAP round-trips to
  O(members / `MemberBatchSize`) — the documented perf target becomes achievable;
  WP3 (issue-tracked separately) adds the load test that proves it at 10K scale.
- **New internal API surface:** `LdapFilter.Escape`, `ResolveBatch`,
  `DomainNamingContext` — all `internal`/`private`, no public contract change;
  `IDirectoryProvider`'s `GetMembersAsync` signature and return semantics
  (unresolvable → `External`, order preserved, duplicates tolerated) are unchanged.
- **`.claude/rules/lab-environment.md` AD quirks note stays accurate** — FSP
  resolution behavior (live-resolves when the FSP object exists, `MakeExternal`
  fallback otherwise) is preserved by D2/D1, just via a batched path.
- No `schemaVersion`, ruleset format, or wire-DTO changes — this is entirely inside
  the provider.
