# ADR-008: Rule model — JSONC ruleset, strict-AGDLP default, whole-file precedence

**Status:** Accepted · **Date:** 2026-06-12
**Decides:** PLANNING.md AP 3.1 (rule model format, schema, defaults) + issue #14 · **Phase:** 3

## Context

AP 3.2 (RuleEngine), 3.3 (settings editor with import/export), and 3.4 (graph
traffic light) all consume one rule model: a configurable nesting matrix over
the membership-edge kinds, naming patterns per kind, separate circularity and
empty-group checks, per-rule enabled+severity, a visible default ignore list,
and a generic per-rule exception mechanism. The file is hand-edited and
community-shared, i.e. untrusted input. src/Core has zero PackageReferences
(BCL-only) and the repo serializes exclusively with System.Text.Json
(DemoProvider, GraphJson — both fail-loud). No user-profile persistence exists
yet anywhere in src/. Issue #14 asks whether exercising the default ignore
list requires first-class built-in objects in the demo dataset or a lab edge
to real built-ins.

## Decision

1. **Format: JSON with comments (`.jsonc`), parsed by System.Text.Json**
   (`ReadCommentHandling.Skip`, `AllowTrailingCommas`, `PropertyNameCaseInsensitive`,
   `UnmappedMemberHandling.Disallow`). Comments were YAML's only decisive
   advantage; JSONC delivers them at zero dependency cost, keeps Core BCL-only,
   avoids YAML's footguns (Norway problem, indent drift, `\`-quoting), and
   matches the repo's STJ-everywhere, fail-loud convention. Unknown properties
   are errors (typos like `"severty"` fail with a JSON path); sole tolerated
   extra is top-level `$schema`. The AP 3.3 editor will not preserve `//`
   comments on save — every ignore/exception entry therefore carries a `note`
   data field that survives round-trips.
2. **Schema v1** (`schemaVersion: 1`, anything else rejected as
   "written by a newer GroupWeaver"): top-level `name/description/author`,
   `nesting` (matrix), `naming[]`, `circular`, `emptyGroup`, `ignore[]`. Every
   rule has `enabled` + `severity` (`error|warning|info`; enum order
   Info<Warning<Error is pinned for AP 3.4's max() roll-up). Fixed rule ids
   `nesting`/`circular`/`empty-group` live in `RuleIds`; naming rules carry
   user-chosen unique kebab-case ids. Because unknown properties are rejected,
   ANY additive schema change requires a schemaVersion bump; newer loaders may
   accept several versions, older apps refuse newer files with a clear error.
3. **Nesting matrix: rows = parent (containing group) kind ⊆ {GG, DL, UG};
   columns = direct member kind ⊆ {User, Computer, GG, DL, UG, External};**
   keys are exact `AdObjectKind` names (demo-JSON convention, exact-case).
   Cells: `allow` | `deny` (rule severity) | `error|warning|info` (per-cell
   severity override). Missing row/column falls back to `unlisted` (default
   `deny`) — future kinds fail closed without breaking v1 files. Edges whose
   parent kind is not GG/DL/UG (e.g. an expanded External node — External IS
   fetchable per ADR-005/006) are out of the matrix's judged domain. OU never
   appears: containment is not membership. Circularity and empty groups are
   separate rules, never matrix entries; `emptyGroup` applies to group kinds
   only and counts only loaded-and-empty (`GetMembers` empty list; `null` =
   unchecked), `circular` consumes `MembershipTraversal.Walk(...).Cycles`.
4. **Default ruleset = strict AGDLP with the AGUDLP lane allowed** (G→DL and
   G→U→DL conformant; accounts only in GGs; DL-in-DL deny). External member
   cells: `info` only on the DL row (where built-ins/FSPs surface); `allow` on
   GG/UG rows — on live scoped loads every out-of-scope member resolves to
   External, and a non-allow default there would mass-flag healthy forests.
5. **Ignore/exception semantics:** match entries carry exactly one of `dn` /
   `name` glob (`*` = any run incl. commas, `?` = one char; `Regex.Escape`d
   then substituted; anchored `^…$`, `IgnoreCase|CultureInvariant|NonBacktracking`
   — linear time on untrusted files, no ReDoS, no backtracking fallback) plus
   `note`. A global `ignore` match exempts the object everywhere and exempts
   edges when either endpoint matches; per-rule `exceptions` suppress only that
   rule (`endpoint: parent|member|any` narrows nesting exceptions; a match on
   any DN in a cycle suppresses that cycle). Name entries match `Name` OR
   `SamAccountName` and never match raw DNs absent from the snapshot. Naming
   `pattern` is an anchored-as-written .NET regex, case-sensitive by default
   (`(?i)` inline supported), `NonBacktracking|CultureInvariant`; unsupported
   constructs (lookarounds/backrefs) fail validation with a message naming the
   limitation. The default ignore list ships populated: Builtin + FSP container
   globs (never-localized container names) and the well-known CN=Users
   accounts/groups (English CNs; notes carry the localization caveat — SID-based
   ignoring is impossible by design, AdObject has no SID).
6. **Locations & precedence:** embedded default
   `src/Core/Rules/DefaultRuleset.jsonc` (logical name
   `GroupWeaver.Core.Rules.DefaultRuleset.jsonc`), byte-synced copy in
   `examples/rulesets/` (drift-pinned by test). User file
   `%APPDATA%\GroupWeaver\ruleset.jsonc` — this pins the repo-wide convention:
   all user-profile persistence lives under `%APPDATA%\GroupWeaver\`.
   **Whole-file replacement, no merge:** valid user file wins outright; invalid
   user file → run on embedded default + surface path-addressed errors loudly;
   absent → default (materialized only on first AP 3.3 save, never auto-copied).
   Import/export = copy whole file. A community file must be readable as the
   complete truth.
7. **Loading:** internal DTO layer → collect-ALL semantic validation errors
   (JSON-pathed, one pass — AP 3.3 live preview renders the full list); syntax
   errors (malformed JSON, unknown property) surface with line/position.
   `Load` never throws on bad input. The writer (`RulesetSerializer`) ships in
   AP 3.1: strict camelCase JSON, null-omitting, atomic temp-file+move save,
   `Save→Load→Save` fixed-point pinned — AP 3.3 import/export builds on it.
8. **Issue #14: resolved with NO dataset/lab change.** The subject of an
   External-member finding is the edge whose parent is an in-scope DL — already
   rule-evaluable. The DL row's `info` External cell makes the existing demo
   built-in DNs and the lab FSP produce findings that the default ignore list
   demonstrably suppresses (entry removed → finding appears); zero test-pin
   churn. External stays exempt as a *subject* by construction (naming may not
   target kind External; not a group kind for emptyGroup; cycles consist of
   walked members).

## Consequences

- AP 3.2 TDD baseline on the full demo snapshot under the default ruleset:
  **3 nesting errors, 1 cycle (error), 3 naming warnings, 12 empty-group
  infos, 0 External infos** (all pre-suppressed by the default ignore list).
- Not detectable in v1 (explicit gaps): orphan users (u111–u140) — needs a new
  rule + schemaVersion bump; GG←GG nesting (`GG_IT_Admins`←`GG_IT_Backup`) is
  `allow` by default, one cell flip flags it.
- AP 3.2 must consume `Walk` (cycles/frontier), read `Edges` once per
  evaluation, and test suppression both ways (entry present/removed), including
  the synthetic in-snapshot `CN=…,CN=Builtin` DL whose name violates the DL
  pattern (suppression comes from the ignore list, not from kind).
- AP 3.3 needs "reset to default" + "export default" affordances (whole-file
  precedence means default improvements never auto-reach customized files) and
  renders `note` fields verbatim. M3 packaging ships `examples/rulesets/`.
- Binding facts recorded in `.claude/rules/rule-model.md` (STJ options, glob
  semantics, fixed rule ids, severity order, precedence, External-never-subject).
- Core.csproj gains its first `InternalsVisibleTo` (GroupWeaver.Tests) for the
  internal DTO layer, mirroring src/Providers.

## Rejected alternatives

YAML/YamlDotNet (comments only via first-ever Core package or a split loader;
Norway-problem/indent footguns; comments lost on editor save anyway); strict
JSON (no comments in a hand-edited file — disqualifying); first-class built-in
imitation objects in the demo dataset (changes pinned GroupCount=40 and cascades
through 194/141/196/193/44/12 pins + the public M2 GIF for zero added rule
coverage); sanctioned lab edge to real built-ins (drags German-localized CNs
into fixtures the rules must not depend on); merge/overlay precedence (community
files stop being self-describing; editor would render three-way provenance);
complete-and-exact matrix requirement (any future kind breaks every v1 file —
`unlisted` fails closed instead); backtracking-regex fallback with timeout
(weakens the linear-time guarantee on untrusted community files); ignoring
`*,CN=Users,*` wholesale (hides real DL←User violations in small-shop ADs —
the exact target audience); auto-copying the default to %APPDATA% on first run
(freezes defaults at first-run version forever).
