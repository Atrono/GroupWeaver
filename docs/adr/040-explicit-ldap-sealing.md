# ADR-040: Explicit LDAP sealing and signing — fail loud instead of silently binding unprotected

**Status:** Accepted · **Date:** 2026-07-09
**Decides:** issue #291 (no explicit LDAPS/channel-sealing, found in the structural risk assessment) · **Phase:** 4 (feedback-driven)
**Builds on:** PLANNING.md E2 (read-only, integrated Windows auth, no credentials in code), ADR-005 (provider error model: unreachable/bind failure throws `DirectoryUnavailableException`), ADR-039 (the sibling perf fix this session; same file, no interaction)

## Context

Every `DirectoryEntry` `LdapProvider` constructs (`src/Providers/LdapProvider.cs`,
eight call sites: `ConnectAsync`, `GetRootCandidatesAsync`, `LoadScopeAsync`,
`FindEntry`, `CollectMembers`'s ranged-fetch lambda, `ResolveBatch`,
`ReadDefaultNamingContext`) is built with the bare one-argument constructor —
`AuthenticationType` is never set. .NET's default for that property is
`AuthenticationTypes.Secure` alone: the bind authenticates via Kerberos/NTLM
(integrated auth, no stored credentials — E2 upheld) but does **not** request
`Sealing` (encryption) or `Signing` (integrity) on top of it. Whether the actual
wire traffic ends up encrypted/signed then depends on server-side policy the app
has no visibility into and never checks: a domain with signing-not-required DCs, an
NTLM fallback without extended session security, or a deliberately weakened lab/test
domain all silently produce a working — but unprotected — bind. Full org structure
(group names, DNs, membership, every attribute in `AttributeWhitelist`) crosses the
wire on that connection. The lab DC (this box) happens to be a fresh, fully-patched
forest with strong defaults, so this gap is invisible locally; it only bites against
a real customer forest with weaker-than-default hardening — exactly the audience
ADR-031 (Connection domain targeting) built reach *toward*.

## Decision

### D1 — Every bind explicitly requests Kerberos sealing + signing; there is no unprotected mode.

A new private constant `RequiredAuth = AuthenticationTypes.Secure |
AuthenticationTypes.Sealing | AuthenticationTypes.Signing` and a single `internal`
factory `OpenEntry(string path)` (`new(path, null, null, RequiredAuth)` — `null`
username/password is unchanged, integrated auth only; `internal` rather than
`private` purely so `LdapProviderOpenEntryTests` can pin the auth flags directly,
via the existing `InternalsVisibleTo("GroupWeaver.Tests")` — not reachable from
`GroupWeaver.App`) replace every bare `new DirectoryEntry(path)` call. This is not
configurable and there is no fallback mode: the whole point is a wire-protection
floor that cannot be silently skipped.

### D2 — A negotiation failure fails loud, not silent.

If the target DC/domain cannot satisfy `Sealing`+`Signing` (e.g. NTLMv1 without
extended session security, a deliberately unhardened legacy domain), the ADSI bind
itself throws a `COMException` at first use (`RefreshCache`, `FindAll`, or property
access). No new exception-handling code is needed: `ConnectAsync`'s existing
try/catch and `RunAsync`'s existing catch-all both already map any unexpected
`COMException`/exception to `DirectoryUnavailableException` (ADR-005's provider
error model). The practical effect: a domain too weak to protect the connection now
surfaces as a clear connect failure instead of a working-but-unprotected session —
D2 is a consequence of D1, not new logic.

### D3 — LDAPS (`SecureSocketsLayer`, port 636) is deliberately not added.

Kerberos sealing works wherever Kerberos itself works — universal in an AD forest
built by this app's own bootstrap, no extra per-DC configuration. LDAPS additionally
requires a valid server certificate configured on every target DC, which is outside
this app's control and not guaranteed for an arbitrary customer forest (unlike a
Windows-first internal tool that can mandate its own PKI). Requiring Sealing+Signing
gets the same wire-protection outcome (Kerberos-negotiated encryption + integrity)
without adding a certificate-trust surface or a `server:636` config field nobody
would reliably have populated correctly. Revisit only if a real customer forest is
found where Kerberos sealing genuinely isn't available but LDAPS is (see Rejected
alternatives).

## Where the code lives

- `src/Providers/LdapProvider.cs`: new `RequiredAuth` constant, new `internal`
  `OpenEntry(string path)` factory; all eight `new DirectoryEntry(...)` call sites
  route through it.

## Security-review note

This closes a real wire-protection gap rather than opening a new attack surface: no
new input, no new filter/path composition (ADR-039 already covers that), no
credential storage or prompt (E2 unchanged — `username`/`password` stay `null`). The
one behavior change worth flagging for `/security-review`: a domain that previously
bound successfully but unprotected will now fail to connect at all if it truly
cannot negotiate Sealing+Signing. That is the intended outcome (fail loud, per D2),
not a regression — a customer running such a domain has a directory-hardening gap
this ADR surfaces rather than masks.

## Rejected alternatives

- **Make sealing/LDAPS an opt-in "Advanced" toggle (mirroring ADR-031's Advanced
  disclosure).** Rejected — a toggle implies the unprotected default is an
  acceptable choice; per house style, this should just be correct rather than
  configurable, and D1's floor costs nothing in the environments this app targets
  (real AD domains all speak Kerberos).
- **Add explicit LDAPS (`SecureSocketsLayer`) as a fallback when Sealing fails.**
  Rejected for now (D3) — adds a certificate-trust surface and a config field for a
  scenario (Kerberos sealing unavailable, LDAPS available) not yet observed against
  a real target. Revisit with a concrete case rather than speculatively.
- **Detect and warn instead of failing the bind.** Rejected — "warn but proceed
  unprotected" is exactly the silent-downgrade behavior this ADR closes; ADR-005's
  provider error model already treats bind failure as the correct signal, and a
  clear `DirectoryUnavailableException` message is more actionable than a warning
  the auditor might not notice before pulling sensitive org data over an
  unprotected connection.

## Consequences

- **No API surface change** — `LdapProvider`'s public constructor and every
  `IDirectoryProvider` method signature are unchanged; this is entirely inside the
  provider's own `DirectoryEntry` construction.
- **A previously-succeeding connect against a genuinely weak domain now fails
  loud** (D2) — the intended, documented behavior change.
- **No test behavior change expected** against the lab DC (fully-patched, Kerberos
  sealing available by default) — the existing `RequiresAd` integration suite is the
  regression proof; if it goes red, that is real signal the lab domain itself
  doesn't support the new floor, not a false failure to work around.
