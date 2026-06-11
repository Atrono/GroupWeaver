# ADR-002: primaryGroupID — whitelisted attribute, never a membership edge

**Status:** Accepted · **Date:** 2026-06-12
**Decides:** PLANNING.md AP 1.5 (documented `primaryGroupID` decision: primary
membership as an edge, yes/no) · **Phase:** 1, AP 1.5

## Context

Active Directory stores exactly one group membership per user/computer
*outside* the `member`/`memberOf` mechanism: the `primaryGroupID` attribute, a
bare RID on the account (default 513 = Domain Users for users, 515 = Domain
Computers for computers). The primary group's `member` attribute does **not**
list these accounts — confirmed by the Phase-0 spike
(`spikes/LdapSpike/RESULTS.md`: "`member` does not reflect `primaryGroupID`
membership"). GroupWeaver's graph and RuleEngine are built entirely on the
`member` relation, so primary memberships are structurally invisible to them.

This matters in two opposite directions. For the default values the hidden
edges are pure noise (every user in the domain → Domain Users). But re-pointing
`primaryGroupID` at a *non-default* group is a known (rare) technique to hold a
group membership that member-based tooling cannot see — exactly the kind of
blind spot an audit tool must not paper over silently.

## Decision

**LdapProvider emits NO membership edge for `primaryGroupID` in v0.1.**

- The graph and the RuleEngine see exactly the `member` relation — this is a
  stated product claim ("GroupWeaver visualizes `member`-based structure"), not
  a bug.
- `primaryGroupID` **is** on the user attribute whitelist
  (`src/Providers/AttributeWhitelist.cs`), so the detail panel (AP 2.5) shows
  the raw RID value on every user node: the blind spot stays inspectable by a
  human even though it never becomes an edge.

## Consequences

- \+ The ~140 noise edges a default domain would gain (every user → Domain
  Users) never exist, so they cannot drown the structural signal the tool is
  for.
- \+ No RID→SID→DN resolution machinery (domain-SID lookup, SID arithmetic,
  secondary searches) in the provider.
- − A re-pointed primary group is invisible as an edge. Mitigation recorded as
  a backlog RuleEngine check: **"`primaryGroupID` != 513/515 → warning"** — an
  attribute-level finding, no edge required.
- − The README section "What GroupWeaver does NOT see" (AP 3.5) must cite this
  ADR as the documented `primaryGroupID` blind spot.

## Alternatives considered

- **Edges for all primary groups:** buys ~one noise edge per account plus the
  full RID-resolution machinery, to display a relation that is uniform and
  uninteresting in the default case. Rejected.
- **Edges only for non-default values:** an edge that exists "sometimes",
  depending on the attribute value, misleads more than an explicit
  attribute-level check — users would reasonably conclude absence of the edge
  means absence of the membership. Rejected in favor of the backlog rule.
- **Don't fetch the attribute at all:** hides the value from the detail panel
  and blocks the future `primaryGroupID != 513/515` rule. Rejected — fetching
  one integer is free; not showing it makes the blind spot invisible instead
  of inspectable.
