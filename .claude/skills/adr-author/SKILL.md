---
name: adr-author
description: How to write a GroupWeaver ADR in the house format - the header block (Status/Date/Decides/Phase/Builds-on), the D1..Dn decision sections, the mandatory "Where the code lives" map and "Security-review note", plus next-number / filing / issue-linking mechanics. Use when drafting any docs/adr/NNN-*.md, e.g. turning a UX lever or design decision into a Proposed ADR.
---

# Authoring an ADR

GroupWeaver records every architectural decision as `docs/adr/NNN-title.md`
(CLAUDE.md "Autonomy contract"). The format has grown well past plain
"Context / Decision / Consequences" - this skill is the **current house shape plus
the numbering/filing ritual**. Read two or three recent neighbours first
(`030`-`032`, `022`); matching their live shape beats this prose if they diverge.
Drafting is autonomous - composing the decision IS the work, so never ask the user.

The `planner` subagent drafts ADRs read-only and returns the text; the orchestrator
files it with this skill. A `Proposed` ADR is filed now and flipped to `Accepted`
when the code lands (the lever cadence below).

## Header block (every ADR opens with exactly this)

```
# ADR-NNN: <decision> — <optional honest one-line subtitle>

**Status:** Proposed | Accepted · **Date:** YYYY-MM-DD
**Decides:** issue #NNN (<what it settles>) · **Phase:** N (feedback-driven)
**Builds on:** ADR-XXX (<role>), `.claude/rules/<file>.md` (<contract>)   <- optional
**Refines:** ADR-YYY (<the one decision it narrows>)   <- optional
```

## Body sections (omit only the ones marked optional)

1. **`## Context`** — the problem, the personas it bites, and the hard constraints
   the fix must respect (e.g. "`X.Compute` is pure/total/UI-free Core"). Say why now.
2. **`## Decision`** — one `### D1 — <bold imperative claim>` heading per distinct
   decision, then `D2`, `D3`… Each Dn is self-contained and testable; number ad-hoc,
   there is no fixed count.
3. **`## Where the code lives`** — a bullet map `File.cs:Member (Module): role /
   future commitment.` Name the real change sites so the ADR guards the edit; this is
   the seam a later contract-rule or the `reviewer` subagent checks against.
4. **`## Security-review note`** — mandatory. Affirm the invariant: **no
   directory-write path, no new LDAP / file-format / deserialization attack surface**
   (read-only product). If a surface *does* change, name the defense that covers it
   (the `security-review-groupweaver` skill's threat model).
5. **`## Rejected alternatives`** — each path not taken with a one-line "why not".
   Load-bearing: it stops the decision being re-litigated next session.
6. **`## Consequences`** — what changes downstream, which tests/baselines move, what
   stays untouched. Optional **`## Addendum (YYYY-MM-DD)`** for post-`Accepted`
   refinements (append, never rewrite history).

Section ORDER varies a little across the corpus (some put Consequences before
Rejected alternatives) — match a recent neighbour; the section SET is the contract.

## Numbering & filing

1. **Next number:** list `docs/adr/`, take the highest `NNN`, add 1. There is **no
   index file** and a few legacy numbers sit out of order — trust the max, not the
   file count. Filename `docs/adr/NNN-kebab-case-title.md`, zero-padded to 3 (`033`).
2. **Write** the file; `.md` is UTF-8 so em-dashes/arrows are fine (the ASCII-only
   rule is `.ps1`-only, [[lab-environment]]).
3. **Link it from its issue:** drop a pointer comment on the deciding GitHub issue
   ("Proposed ADR-NNN filed: <path>"). The UX-lever cadence (#188-190) is
   **fit-audit → ranked lever → needs-ADR issue → Proposed ADR → pointer comment**;
   implementation flips Status to `Accepted` and closes the issue.
4. **Commit** `docs(adr): add ADR-NNN <title> (Proposed, #NNN)`; stage the ADR
   explicitly (never `git add -A`) and record it in the journal ([[wrap-session]]).

## Gotchas

- **The Security-review note and a real "Where the code lives" map are not
  optional** — they are what make an ADR reviewable and what the `/security-review`
  release gate and the `reviewer` subagent lean on.
- **Don't restate a contract the rules already pin** — link `.claude/rules/*.md`
  (e.g. [[rule-engine]], [[data-model]], [[audit-summary]]) instead of paraphrasing
  it into drift.
- **Proposed ≠ done.** A Proposed ADR is a plan; leave Status `Proposed` until the
  code lands, then flip to `Accepted` in the implementing PR.
