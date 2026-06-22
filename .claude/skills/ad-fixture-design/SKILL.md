---
name: ad-fixture-design
description: How to seed, extend, and verify the AGDLP-Lab integration-test fixtures - the only sanctioned AD-write path, the AD mechanics (foreign-SID FSP binding, Get-ADGroupMember traps, well-known-SID rejection), and the live-AD test count pins a fixture change breaks. Use when re-seeding after a box rebuild, adding a deliberate violation/fixture, or verifying the lab fixture state.
---

# AGDLP-Lab fixture design

The procedure + AD mechanics behind `tools/seed-testad.ps1`. The DOMAIN reasoning
and the canonical fixture/violation LIST live in [[agdlp-domain]]; the AD quirks
are also in [[lab-environment]]. This skill is how you *operate and change* the
fixtures safely.

## The one write path (non-negotiable)

- The ONLY sanctioned AD write is `pwsh tools/seed-testad.ps1`, run ONLY by the
  `ad-fixture-admin` subagent, ONLY against `OU=AGDLP-Lab,DC=agdlp,DC=lab`. The
  main session never runs AD-mutating PowerShell and never inlines `New-AD*`/
  `Set-AD*`/`Add-ADGroupMember`.
- To change fixtures you EDIT the script (reviewed), then `ad-fixture-admin` runs
  it. Never fix-forward with a manual AD edit.
- Safety is structural: the script aborts unless `Get-ADDomain` is `agdlp.lab`,
  and `Assert-LabPath` refuses any DN not ending in the lab OU. Every `Ensure-*`
  helper is idempotent (pre-check → create/update/skip). Keep both intact.

## Extending the fixtures

Use the existing `Ensure-OU/User/Group/Computer/Member/ForeignSidMember` helpers —
they enforce scope + idempotency for you. Users are disabled (no passwords);
group `sAMAccountName == Name`. A new deliberate violation is just one more
`Ensure-*` call in the matching block; see [[agdlp-domain]] for which
violation each fixture encodes.

**Every object/edge you add or remove breaks the live-AD count pins — update them
in the SAME change** (this is the part that bites):

- `tests/GroupWeaver.Tests/Providers/Ldap/LdapProviderIntegrationTests.cs` — pins
  the total object count, the OU/kind breakdown, and the **membership-edge count**.
- `tests/GroupWeaver.Tests/Providers/Ldap/RuleEngineLabIntegrationTests.cs` — pins
  the External-edge count, the exact `UncheckedDns` set (every frontier FSP/user
  DN), the suppression-toggle finding counts, and the partial-scope `OU=Groups`
  frontier count.

These are `Category=RequiresAd` tests. **CI runs `-SkipAdTests`, so CI will NOT
catch lab-fixture drift** — the local full gate is the only thing that does:

```
pwsh tools/build.ps1        # NOT -SkipAdTests; runs the RequiresAd pins against the live fixtures
```

Re-derive the pinned numbers from a green run rather than guessing — a full-scope
add usually moves several pins at once (edge count, `UncheckedDns`, the 19-finding
baseline toggle). The lab fixture is SEPARATE from the DemoProvider dataset
(`src/Providers/Demo/`); their divergence (lab has FSP edges, demo has builtin
edges) is intentional — never "sync" one to the other.

## AD mechanics (the non-obvious traps)

- **Dangling FSP = `<SID=...>` binding.** `Set-ADGroup -Add @{ member = "<SID=$sid>" }`
  on an in-OU group. The DC resolves it and **system-creates
  `CN=<sid>,CN=ForeignSecurityPrincipals,<baseDN>` OUTSIDE the lab OU**, then
  stores the resolved FSP DN (not the `<SID=>` form) back in `member` — idempotency
  pre-checks against that DN.
- **Use a FABRICATED foreign-domain SID** (`S-1-5-21-<fake-domain>-<rid>`).
  Well-known special-identity SIDs (`S-1-5-11`, etc.) are REFUSED by SAM as members
  of account-domain groups (#9) — only BUILTIN aliases may hold them.
- **Never `Get-ADGroupMember` on a group that may hold a dangling FSP** — it throws
  "unspecified error" trying to resolve the FSP. Read the raw `member` attribute
  instead (`Get-ADGroup ... -Properties member`). This applies to seeding pre-checks
  AND your verification commands.
- **German-localized DC:** never depend on localized group names
  (`Administratoren`); well-known container CNs (`CN=ForeignSecurityPrincipals`,
  `CN=Builtin`) are locale-safe.
- **`/` is legal in an RDN** and LDAP returns it unescaped, but ADSI ADsPaths treat
  it as a path separator — the `OU=Research/Development` fixture pins
  `LdapProvider`'s escaping (#16).

## Verifying the fixtures (read-only)

`Get-AD*` is read-only and unrestricted. After a seed, confirm: object total +
OU/kind counts under the lab OU; the deliberate violations, the A↔B cycle, the
empty groups (see [[agdlp-domain]] for the expected set); and each FSP via the raw
`member` attribute (NOT `Get-ADGroupMember`) plus the FSP placeholder objects under
`CN=ForeignSecurityPrincipals`. The authoritative check is the product code path:
the green `RequiresAd` suite above.

## Teardown caveat

FSP placeholders live OUTSIDE the lab OU, so "wipe `OU=AGDLP-Lab`" misses them.
There is no teardown script — on a from-scratch rebuild, delete the
`CN=S-1-5-21-...,CN=ForeignSecurityPrincipals` objects manually too, then re-seed.
