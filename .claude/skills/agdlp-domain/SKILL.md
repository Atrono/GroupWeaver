---
name: agdlp-domain
description: AGDLP/AGUDLP domain knowledge for Active Directory group nesting - group scopes, what AD enforces vs. what is convention, the nesting matrix, naming patterns. Use when implementing or reviewing RuleEngine logic, the default nesting matrix, or fixture design.
---

# AGDLP domain knowledge

> Authoritative since AP 3.1 (ADR-008). Implementation-binding facts (STJ
> options, glob semantics, precedence) live in `.claude/rules/rule-model.md`;
> this skill carries the DOMAIN reasoning behind them.

## Group scopes

| Scope | Abbrev | May contain (same forest) | Typical AGDLP role |
|---|---|---|---|
| Global | GG | users/computers/GGs from SAME domain | role/account aggregation ("A → G") |
| Universal | UG | users, GGs, UGs from ANY domain | cross-domain role bundling (the "U" in AGUDLP) |
| Domain Local | DL | users, GGs, UGs, DLs from any domain | resource access ("DL → P") |

## What AD enforces vs. what GroupWeaver checks

AD itself blocks some nestings (DL into GG, UG into GG, DL into UG). Seeing
one of those edges means broken/imported data — the default matrix still says
`deny` so it surfaces as an error instead of being silently trusted.
GroupWeaver's RuleEngine judges the *conventions* AD permits but AGDLP forbids:

- user/computer directly member of a DL or UG (should go via GG)
- DL nested inside DL (strict AGDLP forbids; AD allows)
- circular nesting (AD allows it; traversal must terminate — `MembershipTraversal.Walk`)
- empty groups (only loaded-and-empty counts; `null` members = unchecked)

## The default nesting matrix (ADR-008, strict AGDLP + AGUDLP lane)

Rows = containing group (parent), columns = direct member. Only GG/DL/UG rows
exist — OU containment is not membership; edges with a non-group parent are
outside the judged domain. `unlisted: deny` makes future kinds fail closed.

|  | User | Computer | GG | DL | UG | External |
|---|---|---|---|---|---|---|
| **GG** | allow | allow | allow | deny | deny | allow |
| **DL** | deny | deny | allow | deny | allow | **info** |
| **UG** | deny | deny | allow | deny | warning | allow |

Reasoning worth keeping:
- GG←GG (role nesting, "AGGDLP") is conformant → `allow` by default; flagging
  it is a one-cell flip (see `examples/rulesets/pure-agdlp.jsonc`).
- DL←UG `allow` = the AGUDLP lane (G→U→DL); pure-AGDLP shops flip it to deny.
- UG←UG is legal in AD and outside the canonical lane → per-cell `warning`.
- **External columns:** on live scoped loads every member outside the loaded
  scope resolves to `Kind.External` — non-allow GG/UG cells would mass-flag
  healthy forests. Only the DL row is `info`: that is where built-ins and FSPs
  legitimately surface, and it is what makes the default ignore list
  demonstrably DO something (issue #14 resolution — remove an ignore entry,
  an info appears on the fixture DLs; zero dataset churn).
- External is never a rule *subject*: naming may not target kind External,
  emptyGroup judges group kinds only, cycles consist of walked members.

## Naming conventions (default rule set)

Anchored .NET regex per group kind, case-sensitive, `NonBacktracking` (linear
time on untrusted community files — lookarounds/backreferences rejected at
validation):

- `naming-gg`: `^GG_([A-Z][A-Za-z0-9]*)(_[A-Z][A-Za-z0-9]*)+$` — ≥2 PascalCase tokens
- `naming-dl`: `^DL_[A-Z][A-Za-z0-9]*(-[A-Za-z0-9]+)*_(RW|RO)$` — resource may hyphenate (FS-Sales)
- `naming-ug`: `^UG_[A-Z][A-Za-z0-9]*$`

Evaluated against `SamAccountName ?? Name`. Fixture offenders (demo + lab):
`SalesTeamGlobal`, `GG_X` (one token), `dl-finance-extra` (lowercase prefix);
the other 37 demo group names pass — pinned in `DefaultRulesetTests`.

## Ignore list rationale

Well-known principals are not yours to rename or restructure → ship them
ignored, but visibly/editable (delete an entry to start judging). DN globs on
never-localized container names (`*,CN=Builtin,*`, `*,CN=ForeignSecurityPrincipals,*`)
are locale-safe — the lab DC is German (`Administratoren`), covered by a
read-only `RequiresAd` test binding `<SID=S-1-5-32-544>`. The well-known
`CN=Users` entries use English CNs; their `note` fields carry the
localization caveat. **Never** ignore `*,CN=Users,*` wholesale — that hides
real DL←User violations for ordinary users parked in the default container
(exactly the small-shop ADs the tool targets). SID-based ignoring is
impossible by design: `AdObject` has no SID (data-model contract).

## Severity / evaluation model

Severity order Info(0) < Warning(1) < Error(2) is pinned (AP 3.4 max-roll-up).
Evaluation order per object/edge: global ignore → per-rule exceptions → check.
A global ignore match exempts the object everywhere and any edge with a
matching endpoint; per-rule `exceptions` suppress only that rule (`endpoint:
parent|member|any` narrows nesting exceptions).

AP 3.2 TDD baseline (full demo snapshot, default ruleset): **3 nesting errors,
1 cycle error, 3 naming warnings, 12 empty-group infos, 0 External infos**
(External findings exist but are all suppressed by the default ignore list —
test suppression in both directions).

## Fixtures

`tools/seed-testad.ps1` seeds `OU=AGDLP-Lab` (authoritative list in the
script); the embedded demo dataset mirrors it (`src/Providers/Demo/README.md`).
Shared deliberate violations: DL_FS-Sales_RW←user, UG_AllStaff←user,
DL_FS-Finance_RO←DL_Nested_RO, GG_Circle_A↔GG_Circle_B, 12 empty groups,
3 naming offenders. Lab-only: dangling FSP in DL_App-ERP_RW. Orphan users
u111–u140 are seeded but NOT detectable under the v1 ruleset (explicit
ADR-008 gap; future rule + schemaVersion bump).
