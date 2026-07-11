---
name: wrap-session
description: How to end a GroupWeaver session per CLAUDE.md - derive the date + next session number, draft the Done/Decided/Next journal entry in the house style, stage ONLY the journal, then commit and push as TWO separate calls (the guard blocks a combined commit+push as a force-push). Use at the end of every session, after a /compact, or before a required reboot.
---

# Wrapping a session

CLAUDE.md mandates the close-out: *"End every session: append 3-5 lines to
`docs/journal/YYYY-MM-DD.md` (done, decided, next), then commit + push - the
journal only recovers what reached the remote."* This skill is the **ordered
sequence + the two guard gotchas that silently break it**. It is the close-out
counterpart to `.claude/hooks/session-start.ps1`, which surfaces the latest
entry's first 15 lines to orient the *next* session. The `Stop` hook
(`.claude/hooks/wrap-session-reminder.ps1`) nudges you here when
`docs/journal/<today>.md` is still missing — a reminder, never a substitute.

Run it autonomously at session end - composing the entry IS the work (judgment
about what the session did), so never ask the user; just write, commit, push.

## Sequence

1. **Derive the coordinates** (read-only; same listing the SessionStart hook
   uses). List `docs/journal/*.md`, drop `BLOCKED-*`, `Sort-Object Name`, take the
   last; parse `Session N` from its `# ...` header line and use **N+1**. Date =
   today, `Get-Date -Format 'yyyy-MM-dd'`. **Same-day rule:** if today's file
   already exists (a second session today, or post-`/compact`), refresh/append
   that entry rather than overwrite it and keep its existing session number.
2. **Compose** the entry in the exact house style (see template). Title is
   `topic -> outcome`. The header + `**Phase:**` line + the lead of `## Done` must
   sit within the **first 15 lines** - that is the slice the SessionStart hook
   shows next session, so it has to carry the orienting context on its own.
   `## Next` is `- (clear)` (or the queued WP / blocker) when nothing is in flight.
3. **Write** `docs/journal/<date>.md`. **Fit-audit staleness check (cadence,
   2026-07-11):** while composing `## Next`, list `docs/ux-fit-audit-*.md` and
   take the newest by name (none found = stale); if its date is more than 14 days ago, add
   `- run a whole-app fit-audit (last: <date>, stale)` to `## Next`. The audit
   itself is next-session work — never run it as part of the wrap.
4. **Stage only it:** `git add docs/journal/<date>.md` - plus any ADR/rule files
   *this* session created, named explicitly. **Never `git add -A`** (it would
   sweep unrelated WIP into the journal commit).
5. **Commit (call #1):** `git commit -m "docs(journal): record session N - <title> (#PR)"`
   with the repo's standard trailers. Keep the subject free of literal AD-write
   cmdlet names (see Gotchas).
6. **Push (call #2 - a SEPARATE tool call):** `git push`.
7. **Confirm it reached the remote:** `git status` clean and `git log origin/main -1`
   shows the journal commit. The journal only recovers what was pushed.

Run every git command from the repo root - a stale agent-worktree cwd silently
retargets git (see [[lab-environment]]).

## Entry template (house style)

```
# YYYY-MM-DD - Session N (topic -> outcome)

**Phase:** Phase 4 (feedback-driven). <1-3 sentence context: what prompted the work, the verdict>

## Done

- **#NNN / PR #NNN (squash `hash`).** <what shipped, the key design choices>.
  Gate green (NNN App + NNN Core), reviewer APPROVE, ui-verifier PASS, CI green.

## Decided

- **<one-line principle>.** <why; link any ADR addendum>.

## Next

- (clear)  <or: the queued WP / open blocker>
```

Em-dash / arrow glyphs are fine here - this is `.md` (UTF-8); the ASCII-only rule
is `.ps1`-only ([[lab-environment]]). Match the live shape of the last entry
(`docs/journal/2026-06-27.md`) if in doubt.

## Gotchas (the whole reason this is a skill, not muscle memory)

- **Commit and push are TWO separate tool calls - never one command.** The
  PreToolUse guard (`.claude/hooks/guard.ps1`) tests the command text with
  PowerShell `-match` (case-**insensitive**), so its force-push alternative
  `\s-f\b` also matches `-F`. A combined `git commit -F msg && git push` (or
  `git commit -m ... && git push`) therefore trips the **force-push** block. Issue
  the commit, then issue `git push` as a second call.
- **Keep the commit subject free of literal `*-AD*` write-cmdlet names.** The same
  guard substring-matches AD-write verbs (`New-/Set-/Remove-/Add-/...-AD*`) in the
  command text and blocks them. If the session touched AD-write topics, paraphrase
  in the subject (e.g. "seed fixture" not the cmdlet name). The journal *file body*
  is never part of a command string, so its prose is unaffected - only the commit
  subject matters.
- **Stage the journal explicitly, not `-A`** - the box often carries unrelated WIP
  (artifacts, scratch) that must not ride along in a `docs(journal):` commit.
