---
name: agdlp-domain
description: AGDLP/AGUDLP domain knowledge for Active Directory group nesting - group scopes, what AD enforces vs. what is convention, the nesting matrix, naming patterns. Use when implementing or reviewing RuleEngine logic, the default nesting matrix, or fixture design.
---

# AGDLP domain knowledge

> Stub - flesh out during Phases 1-3 as RuleEngine work begins (CLAUDE.md bootstrap step 4).

## Group scopes
| Scope | Abbrev | May contain (same forest) | Typical AGDLP role |
|---|---|---|---|
| Global | GG | users/computers/GGs from SAME domain | role/account aggregation ("A → G") |
| Universal | UG | users, GGs, UGs from ANY domain | cross-domain role bundling (the "U" in AGUDLP) |
| Domain Local | DL | users, GGs, UGs, DLs from any domain | resource access ("DL → P") |

## What AD enforces vs. what GroupWeaver checks
AD itself blocks some nestings (e.g. DL into GG, UG into GG). GroupWeaver's
RuleEngine checks the *conventions* AD permits but AGDLP forbids, e.g.:
- user directly member of a DL or UG (should go via GG)
- DL nested inside DL
- circular nesting (AD allows it; traversal must terminate)
- empty groups, orphaned users

The allowed-edge set is a **configurable matrix** (PLANNING.md AP 3.1); the
default matrix is strict AGDLP, with AGUDLP (UG layer) representable.

## Naming conventions (default rule set)
Pattern per group type/scope, e.g. `GG_<Dept>_<Role>`, `DL_<Resource>_<RW|RO>`,
`UG_<Purpose>`. Severities and per-rule ignore lists per PLANNING.md AP 3.1.

## Lab fixtures
`tools/seed-testad.ps1` seeds `OU=AGDLP-Lab` with deliberate violations of all
of the above - see the script for the authoritative list.
