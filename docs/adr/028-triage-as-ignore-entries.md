# ADR-028: Audit triage (Acknowledge / Suppress) as tagged global-ignore entries

**Status:** Accepted · **Date:** 2026-06-24
**Decides:** PLANNING.md WP5e (audit triage) + issue #158 · **Phase:** 4 (feedback-driven)
**Builds on:** ADR-008 (rule model — the durable `note` data field on every ignore/exception entry; ignore-list semantics), ADR-009 (`RuleEngine.Evaluate` is pure/total and re-run per ruleset edit), ADR-011 (the single Load gate in `SettingsViewModel` + the `RulesetApplied → OnRulesetApplied` live re-thread), ADR-025 (the parked Back-target surface).

## Context

The audit dashboard (WP5) lists every `RuleViolation` over the loaded Ist. Operators
need to triage a finding — mark it *acknowledged* ("seen, accepted") or *suppressed*
("intentionally hidden") — so a known, accepted deviation stops dragging the health
score down and stops lighting up the graph halos / violations rail, **without** editing
the directory (the product is read-only by design and AD writes are the #1
non-negotiable). Triage must be reversible and visible: a triaged finding may not simply
vanish, or the operator can never review or undo it.

The rule model already ships the exact mechanism: a global `ignore[]` list of `MatchEntry`
records, each with a `dn`/`name` glob and a durable free-form `note` (ADR-008 added `note`
precisely because the editor cannot preserve `//` comments). `RuleEngine.Evaluate` already
drops any finding whose subject matches an ignore entry, and the whole save/validate/apply
path already funnels through one gate (`SettingsViewModel`, ADR-011) that re-threads the
live workspace on success. The open question is whether triage needs new machinery
(a separate triage store, a new schema field, a new suppression "strength") or whether it
is exactly an ignore entry.

## Decision

1. **Acknowledge and Suppress are EQUAL-STRENGTH, reversible global-ignore entries.**
   Each appends one dn-mode `MatchEntry` to `Ruleset.Ignore`; the engine then ignores the
   finding exactly as for any other ignore entry — health rises, the graph + sidebar go
   quiet. The **only** difference between the two is a `[ack]` vs `[suppress]` marker at the
   start of `MatchEntry.Note` (e.g. `"[suppress] triaged in audit"`) — a human annotation of
   intent, visible and editable in the Settings ignore list. **No separate suppression
   strength, no new schema field, no `schemaVersion` bump** (an additive field would force a
   bump per ADR-008 §2; the `note` field is already durable and round-trips).

2. **One write path: the existing gate.** Triage never re-rolls serialization or
   validation. The shell builds the new ruleset = current ruleset + the appended/removed
   ignore entries via `BuildSettingsViewModel()` (the same seam Settings uses), mutates its
   `Ignore` mirror collection, and calls `Save()` — which runs `BuildRuleset → Serialize →
   RulesetLoader.Load → Save (atomic temp+move) → RulesetApplied`. **The only writes are
   `%APPDATA%\GroupWeaver\ruleset.jsonc` (atomic) + in-memory ruleset state. There is no AD
   write path anywhere in triage** — no `Set-AD*`/`New-AD*`/`Remove-AD*`/`CommitChanges`/
   `DirectoryEntry`. Any future PowerShell "remediation" export is **copy-only text** the
   operator runs themselves (WP5f), never executed by the app.

3. **The table shows WOULD-BE findings + a per-row Status.** So triaged rows stay visible
   and reversible, the audit findings table is projected from a *would-be* report:
   `RuleEngine.Evaluate(snapshot, ruleset with Ignore = the triage-tag-free ignore subset)`
   — i.e. the report the engine would produce with `[ack]`/`[suppress]` entries removed
   (plain ignore entries still suppress). Each row's `Status` (Open / Acknowledged /
   Suppressed) is then read from the **live** ruleset's tagged entries (match by escaped
   primary DN + tag). Health/score and the live graph/sidebar use the **live** (post-
   suppression) report — ack'd and suppressed both lift health. The cost is one extra
   `RuleEngine.Evaluate` per ruleset change, accepted under ADR-009's full-re-run contract
   (ms at the 10K-edge target); when no triage entry exists the would-be report *is* the
   live report and the extra evaluate is skipped.

4. **Reversal is precise and per-finding.** Un-triage (per-row or bulk over the selected
   triaged rows) removes the matching `MatchEntry` — identified by escaped DN + tag — and
   re-runs the gate; the finding reappears as Open and re-enters the live report. Because
   each triage is its own entry, reversal never disturbs another finding's entry or any
   plain (untagged) ignore entry.

5. **DN safety via glob-escaping.** The ignore `Dn` is a `GlobMatcher` glob (`*`/`?` are
   wildcards) and the ADR-008 dialect has **no literal-escape** for them. The subject is
   `violation.Dns[0]` (the primary DN); before storing, each literal `*` is replaced by a
   single `?` wildcard (and a literal `?` already matches one char). This keeps the glob
   **length-exact** — one glob char per DN char — so a literal `*` can no longer swallow the
   rest of the string, and the entry still matches its own DN (the round-trip the write path
   relies on). The price is that the one replaced column becomes "any single char"; that is a
   deliberate, documented **fail-safe** for a pathological input — **real AD DNs never
   contain a bare `*`/`?` (RFC 4514 escapes them), so the escape is the identity on every
   genuine DN.** An exact-only guarantee for `*`/`?`-bearing DNs would require a glob
   literal-escape mechanism in Core (an ADR-008 change), out of scope for this slice. The
   escaped form is the identity key for status detection and reversal, so reader and writer
   agree.

6. **Re-evaluation rides `RulesetApplied → OnRulesetApplied`.** The handler's audit arm
   recomputes the audit step's live + would-be reports + summary + table AND re-threads the
   **parked** workspace (`WorkspaceViewModel.ApplyRulesetAsync`) so the graph halos + rail
   update even though Audit (a table view, no renderer) is the current step.

## Where the code lives

- `TriageRequest` / `TriageKind` (App): the shell-seam DTO (escaped Dn, RuleId, kind, reason).
- `TriageEntry` (App): the single source of truth for the `[ack]`/`[suppress]` tag grammar,
  DN escaping, building/recognising/matching tagged entries, and the would-be ignore filter
  — consumed by both the writer (shell) and the reader (audit VM) so the grammar can't drift.
- `AuditViewModel` (App): the Ack/Suppress/Un-triage commands (bulk + per-row), the would-be
  report, and per-row `Status`. Decoupled from `SettingsViewModel` — it only invokes the
  shell-injected `Action<IReadOnlyList<TriageRequest>>` seam (armed in `OnAudit`, like the
  Design-plan callback).
- `ShellViewModel.ApplyTriage` (App): owns the gate — appends/removes ignore entries on a
  `BuildSettingsViewModel()` mirror and calls `Save()`.

## Security-review note

The new write is one more route into the already-reviewed ruleset save gate (relaxed-JSON-
encoder / atomic-write findings already covered there). The triage payload is well-formed
dn-mode entries built from snapshot DNs; the glob-escaping is the one new defense to verify.
No new LDAP, no new file format, no new deserialization surface, and — to re-state the
invariant — no directory-write path.

## Rejected alternatives

- **A separate triage store (a second JSON file / sidecar).** Duplicates the ignore-match
  machinery, splits the source of truth, and needs its own validate/persist/merge path —
  exactly the parallel gate ADR-011 forbids. The ignore list already does this, visibly and
  editably.
- **A new schema field (`triage`/`acknowledged`/`suppressedAt`).** Forces a `schemaVersion`
  bump (ADR-008 §2 — unknown properties are errors), breaks older apps reading the file, and
  buys nothing the durable `note` tag doesn't already provide.
- **A distinct suppression "strength" (suppress hides harder than acknowledge).** There is
  only one engine effect — ignored or not. Modelling two strengths would mean two engine code
  paths for no behavioural difference; the intent distinction is human metadata, so it lives
  in `note`.
- **Per-rule exceptions instead of global ignore.** Per-rule `exceptions` narrow one rule;
  a triaged finding is "this specific object's finding is accepted," which the global ignore's
  dual-endpoint / per-DN semantics express directly. Routing per-rule would need per-rule-class
  dispatch for no gain.
- **Coalescing many findings into one wildcard glob.** Tempting for bulk triage, but it makes
  reversal lossy (one glob can't be un-triaged per finding) and risks over-matching future
  objects. One exact, escaped entry per finding keeps triage precise and individually
  reversible.

## Consequences

- Triage is fully inspectable: every acknowledgement/suppression is a visible ignore entry
  with a tagged note, hand-editable in Settings and portable in an exported ruleset.
- The audit table and the health score legitimately diverge (table = would-be, score = live);
  the UI states this by listing triaged rows muted with a status pill rather than hiding them.
- A hand-edited `note` that drops the `[ack]`/`[suppress]` prefix demotes the entry to a plain
  ignore (the finding leaves the would-be table too) — acceptable: the operator chose to make
  it a permanent ignore.
- Bulk triage over N selected findings writes N entries and runs the gate once.
