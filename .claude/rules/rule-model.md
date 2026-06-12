# Rule-model contracts (AP 3.1 / ADR-008, binding for AP 3.2–3.4)

Pinned by tests in `tests/GroupWeaver.Tests/Core/Rules/`; change only with a
reviewed PR that updates the tests deliberately.

- **Format: JSONC via System.Text.Json** (`RulesetJson.ReadOptions`):
  `ReadCommentHandling.Skip`, `AllowTrailingCommas`,
  `PropertyNameCaseInsensitive`, `UnmappedMemberHandling.Disallow` — unknown
  properties are ERRORS (sole tolerated extra: top-level `$schema`).
  `Load` never throws on bad input; semantic validation collects ALL errors
  (JSON-pathed) in one pass. Writer (`RulesetSerializer`): camelCase, nulls
  omitted, matrix keys verbatim PascalCase, atomic temp-file+move save.
- **Glob semantics** (`GlobMatcher`): `Regex.Escape` then `\*`→`.*` (crosses
  commas), `\?`→`.`; anchored `^…$`;
  `IgnoreCase|CultureInvariant|NonBacktracking` (linear time on untrusted
  files); memoized static cache — records stay pure data, no Regex fields.
  Name entries match `Name` OR `SamAccountName`, NEVER raw DNs.
- **Whole-file precedence, no merge:** valid `%APPDATA%\GroupWeaver\
  ruleset.jsonc` wins outright; invalid → embedded default + loud errors;
  absent → default (never auto-copied). `%APPDATA%\GroupWeaver\` is THE
  repo-wide convention for all user-profile persistence.
- **Rule ids:** fixed `RuleIds.Nesting`/`Circular`/`EmptyGroup`
  (`nesting`/`circular`/`empty-group`); naming rules carry user-chosen
  kebab-case ids, unique case-insensitively. Severity enum order is PINNED:
  `Info=0 < Warning=1 < Error=2` (AP 3.4 max() roll-up depends on it).
- **External is never a rule SUBJECT:** naming may not target kind `External`
  (validation error), emptyGroup applies to group kinds only, cycles consist
  of walked members. External appears only as a matrix COLUMN (member side).
- **Matrix judged domain:** only edges whose PARENT kind is GG/DL/UG; missing
  row/column falls back to `unlisted` (default `deny`, fails closed). OU
  containment is not membership and never appears.
- **AP 3.2 TDD baseline** (full demo snapshot, default ruleset): 3 nesting
  errors, 1 cycle error, 3 naming warnings, 12 empty-group infos, 0 External
  infos (all pre-suppressed by the default ignore list — suppression must be
  tested both ways: entry present/removed).
- **`examples/rulesets/` ships in the M3 portable .zip**;
  `default-strict-agdlp.jsonc` is drift-pinned byte-identical (after CRLF
  normalization) to the embedded `GroupWeaver.Core.Rules.DefaultRuleset.jsonc`.
