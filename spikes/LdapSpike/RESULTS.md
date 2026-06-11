# LdapSpike results — AP 0.2 (Phase 0, issue #2)

Throwaway console spike proving read-only LDAP access patterns against the lab
DC (`agdlp.lab` on localhost:389, Integrated Windows Auth, no credentials in
code). Run: `dotnet run` (optional arg overrides PageSize, e.g. `dotnet run -- 50`).

## Verbatim output (`dotnet run`, warm run, 2026-06-11)

```
LdapSpike — read-only LDAP access-pattern spike (AP 0.2)
Bind: LDAP://localhost/OU=AGDLP-Lab,DC=agdlp,DC=lab (Integrated Windows Auth, no credentials in code)

connected, 40 groups loaded
  computer               10
  group                  40
  organizationalUnit      4
  user                  140
  total                 194
[2] paged subtree enumeration (PageSize=500): 113 ms

recursive group-membership resolution (depth-first, visited DN tracking):
  !! circular reference: group 'GG_Circle_A' already visited under group 'GG_Circle_B' (CN=GG_Circle_A,OU=Groups,OU=AGDLP-Lab,DC=agdlp,DC=lab -> CN=GG_Circle_B,OU=Groups,OU=AGDLP-Lab,DC=agdlp,DC=lab -> CN=GG_Circle_A,OU=Groups,OU=AGDLP-Lab,DC=agdlp,DC=lab)
  !! circular reference: group 'GG_Circle_B' already visited under group 'GG_Circle_A' (CN=GG_Circle_B,OU=Groups,OU=AGDLP-Lab,DC=agdlp,DC=lab -> CN=GG_Circle_A,OU=Groups,OU=AGDLP-Lab,DC=agdlp,DC=lab -> CN=GG_Circle_B,OU=Groups,OU=AGDLP-Lab,DC=agdlp,DC=lab)
  top 5 groups by transitive member count:
    DL_App-CRM_RW                direct=  1  transitive=106
    UG_AllStaff                  direct=  6  transitive=105
    DL_FS-Finance_RW             direct=  1  transitive= 21
    DL_FS-HR_RW                  direct=  1  transitive= 21
    DL_FS-IT_RW                  direct=  1  transitive= 21
  empty groups (no member values): 12
  member DNs pointing outside OU=AGDLP-Lab: 0
[3] recursive membership resolution: 1 ms
```

Multi-page proof (`dotnet run -- 50`, 194 objects = 4 server pages):

```
connected, 40 groups loaded
  total                 194
[2] paged subtree enumeration (PageSize=50): 105 ms
```

## Timings

| Step | Measurement |
|---|---|
| (2) paged subtree enumeration, PageSize=500 (1 page) | 88–126 ms (cold first run 126 ms, warm 88–113 ms) |
| (2) paged subtree enumeration, PageSize=50 (4 pages) | 105 ms — paging round trips are noise on localhost |
| (3) transitive resolution of all 40 groups, in-memory DFS | 1 ms |

## Findings for LdapProvider (AP 1.5)

- **Paging behaves; always set it.** `FindAll()` transparently drives the
  RFC 2696 paged-results control (verified across 4 pages at PageSize=50,
  identical counts). Without `PageSize`, AD's default `MaxPageSize` (1000)
  silently truncates larger result sets — LdapProvider must always set
  `PageSize` and must dispose the `SearchResultCollection` (`FindAll()` leaks
  an unmanaged handle otherwise).
- **Load once, walk in memory.** One subtree query loading
  `distinguishedName, objectClass, member` plus a DN→members dictionary makes
  full transitive resolution of all 40 groups cost ~1 ms. No per-group LDAP
  queries (and no `LDAP_MATCHING_RULE_IN_CHAIN`) needed at this scale; this is
  the right shape for GraphBuilder input.
- **Cycle handling works but reports twice.** Visited-set + recursion-stack
  DFS terminated cleanly on the seeded `GG_Circle_A <-> GG_Circle_B` nesting;
  the same cycle surfaces once per entry point, so RuleEngine must de-duplicate
  cycle findings by unordered DN pair, not by detection site.
- **Attribute presence and casing are quirky.** Empty groups (12 in the
  fixtures) return **no** `member` key at all — guard with
  `Properties.Contains("member")` rather than expecting an empty collection.
  Result property keys come back lowercased; the `ResultPropertyCollection`
  indexer is case-insensitive, but code must not depend on returned key casing.
- **`objectClass` is a chain; computers contain `user`.** Classification has
  to check the most specific class first (`computer` before `user`), and
  subtree scope includes the search base itself (the OU count of 4 includes
  `OU=AGDLP-Lab`).
- **Membership edges aren't complete in real AD.** `member` does not reflect
  `primaryGroupID` membership (e.g. Domain Users) — fine for the lab, but a
  documented blind spot for LdapProvider. No foreignSecurityPrincipals and no
  referrals were encountered; all 0 member DNs pointed outside the OU here,
  but real-world `member` values can reference objects outside the loaded
  scope and must be handled as external leaves.
