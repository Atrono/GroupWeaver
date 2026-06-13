# ADR-011 — Settings editor architecture (AP 3.3)

## Status
Accepted (Phase 3, AP 3.3 / issue #39).

## Context
AP 3.3 adds the settings page: a read/write editor over the ADR-008 `Ruleset`,
persisted whole-file to `%APPDATA%\GroupWeaver\ruleset.jsonc`. The model records
are immutable (`required`/`init`); `RulesetLoader.Load` already collects ALL
validation errors with JSON paths and never throws; `RulesetSerializer`
round-trips `Serialize(Load(x))==x` on bytes, with `deny`(false,null) distinct
from `error`(false,Error) and a sparse matrix. `RulesetLocator.LoadEffective`
already carries a rejected user file's errors into the app on the embedded
default (AP 3.4 threaded them through the composition root, unsurfaced). The
shell is a single step-switched window (ADR-003 D5) whose Workspace step owns
the live, disposable graph renderer; ADR-001 airspace guardrail 5 forbids
layering anything over the graph region. ADR-009 forbids a static naming-regex
memo and says ruleset edits never force a graph rebuild (a full re-Evaluate is ms).

## Decision
1. **Modal `SettingsWindow`, not a 4th shell step or a workspace overlay.**
   ADR-003 D5's sanctioned escape hatch — "anything genuinely modal is its own
   top-level Window" — applies. Opened from a new shell-level "Settings" command
   in a top command strip; leaves `CurrentStep`, the step machine, and the live
   `CytoscapeGraphRenderer` untouched.
2. **Editable `ObservableObject` mirror tree; `RulesetLoader.Load` is the single
   save/import/apply validation gate.** The immutable records can't be bound; the
   mirror holds intermediate-invalid states. Flow: mirror → `BuildRuleset()` →
   `Serialize` → `Load` → on Success persist/apply the re-parsed `Ruleset`, else
   surface `result.Errors`. `Serialize(BuildRuleset(LoadDefault()))` is byte-equal
   to the embedded default (pinned). The matrix mirror preserves source-cell
   PRESENCE (a `Present` flag) so a sparse file round-trips byte-for-byte and the
   `deny`/`error` token distinction survives.
3. **Apply/Save re-thread via `WorkspaceViewModel.ApplyRulesetAsync`** — re-Evaluate
   over the existing snapshot → `UpdateGraphAsync` (replace-in-place, viewport
   kept), never `GraphBuilder.Build`, never `ShowGraphAsync`. Apply = live only;
   Save = live + atomic persist. `IsLoading`-gated; an in-flight pipeline re-reads
   the now-settable `_ruleset`.
4. **Live preview compiles a throwaway, never-interned `Regex`**
   (`NonBacktracking | CultureInvariant`, the engine's exact options) — linear-time
   (no ReDoS), faithful to evaluation, and not memoized (ADR-009). `RulesetValidationError.Message`
   and every `MatchEntry.Note` render strictly as plain text (`TextBlock.Text` /
   `SelectableTextBlock`) — untrusted files may carry control chars (#45). A
   rejected user file on open is surfaced, never auto-rewritten or auto-replaced.

## Consequences
Settings never touches the live graph's topology or viewport; the rejected-file
errors AP 3.4 carried are finally visible; the editor's saved/exported file is
provably reloadable (it passed `Load`); the byte fixed point and `examples/rulesets/`
drift pin are protected by the matrix-presence rule; preview keystrokes never leak
into process memory; control-char tokens can never misrender or spoof. The mirror
is new code future rule editors inherit.

## Rejected alternatives
- 4th shell step — tears down/rebuilds the disposable terminal Workspace graph on entry/re-entry; airspace pressure.
- Workspace overlay panel over GraphHost — ADR-001 airspace violation.
- Editing the immutable records / `record with` per keystroke — impossible (`init`-only) / no field-level INPC, can't hold intermediate invalid states.
- A parallel hand-rolled validator instead of `RulesetLoader.Load` — duplicates the one validation source, drifts from file semantics.
- Dense-always matrix — breaks `Serialize(Load(x))==x` and the `examples/rulesets/` byte pin.
- Re-pick-root to apply a ruleset — heavy rebuild + lost viewport for a ms re-Evaluate.
- Static naming-regex preview memo — ADR-009-forbidden process-memory leak of untrusted patterns.
- Merge/overlay on import; auto-writing the default or rewriting the broken file on open — violate ADR-008 whole-file precedence / clobber recoverable user work.
