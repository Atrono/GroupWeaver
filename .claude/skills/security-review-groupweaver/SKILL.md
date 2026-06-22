---
name: security-review-groupweaver
description: GroupWeaver's threat model for the pre-release /security-review gate - the attack surfaces, the one defense to verify at each, and the finding-classes that have recurred (smart-quote PowerShell breakout, guard-predicate drift, relaxed-JSON-encoder regression, CRLF-vs-upstream-SHA). Use before a tagged release, when auditing LDAP/WebView/export/ruleset code, or running /security-review.
---

# GroupWeaver security review

The generic `/security-review` command does the mechanics; this is the
project-specific MAP so the review is targeted, not a from-scratch rediscovery.
Run it over `git log v<prev>..HEAD` before every tag (the [[cut-release]] gate).
**GroupWeaver is read-only**: the #1 invariant dominates everything below.

## Surfaces → the one defense to verify at each

| Surface | Files | Verify |
|---|---|---|
| **Read-only invariant** (existential) | `src/Providers/LdapProvider.cs`, all of `src/` | NO `Set-AD*`/`New-AD*`/`Remove-AD*`/`CommitChanges`/`SetInfo`/`Invoke`. Provider is `FindAll()` reads only. The only such tokens in `src/` are in a comment documenting the ban. |
| **LDAP filter injection** | `LdapProvider.cs`, `AdsPath.cs` | Every `DirectorySearcher.Filter` is a compile-time CONSTANT (no `$"…{var}…"`). DN→ADsPath escapes `/` (`AdsPath.For`/`EscapeDn`, #16: an unescaped `/` in the base DN redirects the integrated-auth bind to an attacker host). The `server` arg is interpolated RAW — safe only because no UI/CLI exposes a server field; flag if one is added. |
| **PowerShell plan export** (highest-severity artifact) | `src/Core/Plan/PlanScriptExporter.cs`, `PlanText.cs`, `Rfc4514.cs` | Untrusted tokens emitted ONLY inside single-quoted literals via the `Ps1()`/`Guard()` choke point, ASCII `'`→`''`. `Guard` REJECTS any token containing a char in `PlanText.IsUnsafe`. Open `PlanText.cs` and confirm `IsUnsafe` still covers the **U+2018–U+201F smart-quote range** + control chars + U+0085/U+2028/U+2029, AND that the author-time guards (`PlanModel.AddNode`/`RenameNode`) share the SAME predicate (#77). |
| **CSV / HTML report export** | `src/Core/Export/ViolationReportExporter.cs` | CSV: formula-injection guard (`= + - @ tab/CR/LF` → `'` prefix) runs BEFORE RFC-4180 quoting (order is load-bearing, #45). HTML: every token `WebUtility.HtmlEncode`d and emitted as element TEXT only — never into an attribute; severity color is class-keyed CSS; no external CSS/JS/font refs. |
| **WebView2 bridge** | `src/App/Graph/CytoscapeGraphRenderer.cs`, `GraphJson.cs`, `GraphMessageParser.cs`, `web/bridge.js` | Outbound: `GraphJson.WireOptions` uses the DEFAULT `JavaScriptEncoder` (ASCII-only output escapes U+2028/29 + `<` — the whole JS-literal-injection defense, #28). A switch to `UnsafeRelaxedJsonEscaping` is a code-injection finding. JS looks up nodes by `cy.getElementById` only, never selector concat. Inbound `GraphMessageParser.Parse` is TOTAL (malformed → `UnknownMessage`, never throws); base64 PNG reply decode is try/caught. Containment: DevTools off, `NewWindowRequested` handled, navigation cancelled for non-`file://`. |
| **Untrusted ruleset / regex (ReDoS + parser DoS)** | `RulesetLoader.cs`, `GlobMatcher.cs`, `App/Settings/NamingPreview.cs` | All untrusted regex `NonBacktracking` + finite `MatchTimeout`. `MaxPatternLength` cap rejects oversized patterns BEFORE `new Regex` (NonBacktracking DFA *construction* cost scales with pattern size — the timeout does NOT bound it; #52). Glob cache bounded + evict-on-cap; preview regex never interned. JSONC strict (`UnmappedMemberHandling.Disallow`); invalid file → embedded default, never a merge. |
| **Untrusted directory data** | `LdapEntry.cs`, `MemberCollector.cs`, `DnPath.cs`, `MembershipTraversal` | Unresolvable is a value, never an exception (only bind failure throws). `int.TryParse`/`DateTime.TryParse` invariant on hostile `groupType`/dates. Scope decided by escape-aware `DnPath` (not naive `EndsWith`, #29). Cycle walk terminates (DoS). |
| **Supply chain / provenance** | `.gitattributes`, `*.csproj`, `.github/workflows/`, `THIRD-PARTY-NOTICES.md` | Deps exactly pinned (`Avalonia.Controls.WebView [11.4.0]` bracket-pinned). Vendored cytoscape SHA256 pinned to upstream npm in a test + notices; `src/App/web/vendor/** -text` so checkout CRLF doesn't corrupt the hash. Actions SHA-pinned (not tags). `ci.yml` `contents: read`; release scopes write/id-token/attestations only. |

## Recurring finding-classes (institutional memory — NOT visible in current code)

Each of these was found-and-fixed before and is easy to regress. Re-check every release:

- **Smart-quote PowerShell breakout** (HIGH, v0.2): PowerShell tokenizes U+2018–U+201F as string delimiters, so a near-invisible Unicode quote escapes a single-quoted literal. Defense lives in `PlanText.IsUnsafe` — confirm the range is still there.
- **Guard-predicate DRIFT** (#77): the dangerous shape in this codebase is "one rule, several sites that must share a predicate." Diff them side by side: `IsUnsafe` (export vs `AddNode` vs `RenameNode`); regex options + length-cap (`RulesetLoader` vs `NamingPreview` vs `GlobMatcher`). The `NamingPreview` per-keystroke regex lacking a construction length-cap is the live candidate.
- **New emit/export path bypasses the shared `Guard()`** (#65, ToolVersion): any newly-added emitted token must route through the same gate. Grep the export/exporter for un-guarded interpolation.
- **Relaxed-JSON-encoder regression:** any change to `GraphJson.WireOptions` adding a relaxed encoder reopens bridge injection.
- **Vendored-binary CRLF:** a new vendored binary without an explicit `-text` lets `* text=auto` rewrite it on Windows checkout, silently breaking the upstream-SHA integrity check (#53; see [[lab-environment]]).

## The first-hour checks (highest leverage)

1. Open `PlanText.cs`; diff `IsUnsafe` against the `AddNode`/`RenameNode` author-time guards.
2. Read the `NamingPreview.Evaluate` call site for a missing construction length-cap.
3. Confirm `GraphJson.WireOptions` still uses the default (non-relaxed) JSON encoder.
4. Re-grep `src/` for new `Set-AD*`/`CommitChanges`/`Filter = $"…{var}…"` and any new export token not routed through `Guard()`.
