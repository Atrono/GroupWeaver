# ADR-031: Connection domain targeting — audit a specified domain/DC under integrated auth

**Status:** Proposed · **Date:** 2026-06-29
**Decides:** issue #189 (UX fit-audit Lever 2) · **Phase:** 4 (feedback-driven)
**Builds on:** ADR-003 (app-shell / connect step + the D7 inline-error policy), PLANNING.md E1/E2 (read-only, integrated Windows auth, **no credentials in code**), ADR-005 (`LoadScopeAsync` accepts any `baseDn`), PLANNING.md AP 2.1 (the mandatory scope picker). Refines the Connect surface + the Root Picker candidate set only.

## Context

The live connect path always builds `new LdapProvider()` with no arguments (`src/App/App.axaml.cs:46`,
`src/App/Program.cs:84`) — a serverless bind to the machine's joined domain via the DC locator. Yet the
provider **already takes the parameters to target anything**: `LdapProvider(string? server = null,
string? baseDn = null, ...)` (`src/Providers/LdapProvider.cs:44`) — `server` null = serverless, `baseDn` null
= read `defaultNamingContext` from RootDSE (cached, `LdapProvider.cs:294-310`). The targeting capability is
fully built in Core and reachable from **no UI or CLI entry point**. Separately, the Root Picker's candidate
query is hard-filtered to OUs and groups (`(|(objectClass=organizationalUnit)(objectClass=group))`,
`LdapProvider.cs:97`), so the **domain root is never a pickable scope** and a one-pass whole-domain audit is
impossible.

A governance auditor's job is auditing directories, *plural* — a customer's domain, a child/resource domain
reached over a trust, or from a hardened audit workstation in a management forest. Today the tool can only
ever audit the single domain the box is joined to. The most capable part of the stack is coded to lift this
ceiling; it is simply not surfaced.

The binding constraint is **E2: read-only, integrated Windows auth, no credentials in code.** Lifting reach
must not introduce a credential prompt or any stored secret.

## Decision

### D1 — An optional "Advanced — target a specific domain or DC" disclosure on the Connect card.

The Connect step gains a collapsed-by-default disclosure with a **server/DC host** field and an optional
**base DN** field. Connecting with them populated builds `new LdapProvider(server, baseDn)` (the existing
ctor); empty keeps the zero-config `new LdapProvider()` serverless default. **Still integrated auth, still no
stored credentials** — the bind uses the current user's Windows token, exactly as today. The common case (a
domain-joined auditor auditing their own domain) is unchanged and needs no input.

### D2 — Integrated-auth reach only; alternate credentials stay an OS concern (E2 upheld).

Targeting reaches any domain/DC bindable **under the current user's integrated auth** — the joined domain, a
specific DC, and trusted domains over Kerberos/NTLM. Auditing an **untrusted** forest that needs *different*
credentials is served by the OS, not an in-app field: launch GroupWeaver with `runas /netonly` as the audit
account (network credentials for the bind without storing anything). This boundary is stated on the Connect
card / README. **No credential UI, no secret persistence** — E2 is not weakened.

### D3 — The domain root becomes a first-class scope candidate.

`GetRootCandidatesAsync` prepends a synthesized **"Whole domain (`DC=…`)"** entry built from the effective
base DN (`EffectiveBaseDn()` / `defaultNamingContext`, already read at connect). `LoadScopeAsync` already
accepts any `baseDn` including the domain DN (`LdapProvider.cs:115-128`), so this is a **candidate-list
addition, not a load-path change.** (Surfacing CN-containers such as `CN=Users` — where default groups live —
so they too can be scoped is a natural follow-on, tracked but not required by this ADR.)

### D4 — Confirm the target domain *before* binding.

The Connect card surfaces the **detected target domain FQDN** (cheap RootDSE / `defaultNamingContext` read,
no full enumerate) in the helper text — e.g. *"as `DOMAIN\user` against `foo.corp`"* — so the auditor confirms
*which* directory before committing to a potentially large pull. Post-connect the target is already in
`DirectoryConnection.Description` (`LDAP {server} — {baseDn}`, `LdapProvider.cs:83`); D4 brings that
confirmation forward to the moment of connecting (evidence-trail discipline: *this report is of domain X*).

### D5 — Validate the new user input; the read-only invariant is untouched.

`server` and `baseDn` are new untrusted inputs that flow into `AdsPath.For(server, dn)` →
`LDAP://{server}/{dn}`. **`server`** is validated as a host/IP (no embedded `LDAP://` path, no slashes/
filter metacharacters); **`baseDn`** is used only as a *search base* (the object filters stay fixed string
literals — it is never concatenated into an LDAP **filter**), and is checked as a well-formed DN before the
bind. Connect failures keep the ADR-003 D7 inline-error policy (and gain the auditor-first triage copy from
the sibling #188-adjacent finding). No new write path of any kind — the bind, the candidate query, and the
scope load are all reads.

### D6 — Pinned by tests.

`ConnectionFlowTests` gains a targeted-connect case (server/baseDn wired through to the provider; serverless
default preserved when blank); a Root-Picker test asserts the synthesized whole-domain candidate is present
and loads. `docs/ui-checklist.md` gains a Connect criterion for the Advanced disclosure + the pre-bind target
line.

## Where the code lives

- `ConnectionView.axaml` / `ConnectionViewModel` (App): the Advanced disclosure, the pre-bind target line, and
  passing `server`/`baseDn` into the provider factory (today's hardcoded `new LdapProvider()` at
  `App.axaml.cs:46`).
- `LdapProvider` (Providers): **no change** — the `server`/`baseDn` ctor + `EffectiveBaseDn` already exist;
  add only the input-validation guard if not already covered by `AdsPath`.
- `GetRootCandidatesAsync` (Providers): prepend the synthesized whole-domain candidate.

## Security-review note

The one new attack surface is the two free-text inputs (`server`, `baseDn`). The defense to verify: `server`
is a bare host (no path/scheme injection into `AdsPath`), and `baseDn` is a DN used as a **search base only**
— it never reaches an LDAP filter, so it is not a filter-injection vector (the filters are fixed literals).
No credentials are read or stored (E2). No new file format, no new deserialization, **no directory-write
path.** Read-only product, intact.

## Rejected alternatives

- **An in-app credential field / "connect as" with stored or prompted secrets.** Violates E2 ("keine
  Credentials im Code", the README security story). The legitimate alternate-account path is OS-level
  `runas /netonly` at launch — no secret ever touches the app.
- **Auto-enumerate and list every trusted domain in a dropdown.** Scope creep and a real perf/permission
  surface (trust enumeration); a single target field plus the serverless default covers the persona. A true
  multi-domain *aggregate* audit is the larger, separate concern in #189's sibling RootPicker findings.
- **Make targeting mandatory / drop the serverless default.** Regresses the zero-config common case (the
  domain-joined auditor auditing their own domain). Targeting is an *optional* disclosure; the no-arg default
  stays the path of least resistance.
- **Treat `baseDn` as a free LDAP query.** No — `baseDn` is a search base; the object-class filters stay
  fixed literals so user input never composes a filter.

## Consequences

- **The reach ceiling is lifted with a small wiring + UI change over Core that already exists** — the
  consultant / MSP / multi-domain / over-a-trust auditor is served, and the whole-domain one-pass audit
  becomes possible.
- **The zero-config default is unchanged** — blank Advanced fields = today's serverless joined-domain connect.
- **E2 holds** — integrated auth only, no credential UI, no stored secret; alternate creds are an OS `runas`
  concern, documented not coded.
- **One new input-validation defense** (`server`/`baseDn`) enters the security-review surface; everything else
  is read-only and unchanged.
- Pairs with the recurring-cadence work (#190): a confirmed, named target domain is exactly the run-identity
  anchor a persisted audit run records.
