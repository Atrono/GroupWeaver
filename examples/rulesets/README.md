# Example rulesets

Ready-to-use rule files for GroupWeaver's rule engine (format: JSON with
comments, see [ADR-008](../../docs/adr/008-rule-model.md)).

| File | What it is |
|---|---|
| `default-strict-agdlp.jsonc` | The built-in default, published for reference: strict AGDLP with the AGUDLP lane (G→U→DL) allowed, `GG_`/`DL_`/`UG_` naming, circularity and empty-group checks, and the full well-known-principals ignore list. Byte-identical to the resource embedded in the app (a test pins this). |
| `pure-agdlp.jsonc` | A worked example that bans the universal-group lane entirely. Demonstrates matrix cell flips, a per-cell severity override, nesting exceptions with endpoint narrowing, and a per-rule naming exception. |
| `gg-nesting-forbidden.jsonc` | The default with one surgical change: GlobalGroup-in-GlobalGroup nesting is a hard `error` instead of `allow`. Pairs with the settings editor's single-cell-edit guarantee; full ignore list retained so it is drop-in usable. |

## Using a file

Copy it to `%APPDATA%\GroupWeaver\ruleset.jsonc` — or, once the settings
editor lands (AP 3.3), import it from there.

**A ruleset file replaces the built-in default completely; nothing is
merged.** That includes the ignore list: if your file omits the default's
well-known-principals entries (Builtin groups, FSPs, `CN=Users` defaults),
GroupWeaver starts judging those objects. When you start from a custom file,
copy over every default ignore entry you still want — `pure-agdlp.jsonc`
trims its list for brevity and says so in a comment.

You never need a file at all: with no user ruleset present, GroupWeaver runs
on the embedded default. An invalid user file is reported loudly and the
default is used instead.

## Editing notes

- `//` comments and trailing commas are legal. The settings editor will not
  preserve comments on save — put durable remarks into `note` fields, which
  are data and survive every round-trip.
- Glob syntax in `dn`/`name` fields: `*` = any run of characters (commas
  included), `?` = exactly one character, matching case-insensitive.

The v0.1 portable .zip ships this folder.
