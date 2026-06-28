# CLAUDE.md — GroupWeaver

Windows desktop app that visualizes Active Directory structures as an interactive
graph and audits A-G-DL structure + configurable naming conventions — the "P"
(actual permission grants) is invisible to the tool and permanently out of scope.
**Read-only product.** Source of truth: `PLANNING.md` (scope/phases), `docs/adr/` (ADRs).

## Non-negotiable rules

- **NEVER add a code path that writes to Active Directory.** Any `Set-AD*`,
  `New-AD*`, `Remove-AD*`, `DirectoryEntry.CommitChanges` inside `src/` is a
  critical bug — reject it in review.
- **The ONLY sanctioned AD writes:** test fixtures via `tools/seed-testad.ps1`,
  run exclusively by the `ad-fixture-admin` subagent — never touch objects outside
  `OU=AGDLP-Lab,DC=agdlp,DC=lab` (sole exception: bootstrap step 2's DC promotion).
- NEVER weaken, skip, or delete tests to make a build green. Fix the cause.
- NEVER force-push, rewrite history on `main`, or delete `.git`.
- NEVER commit secrets/tokens. `gh` auth lives in the OS, not the repo.
- This machine is a **dedicated, disposable lab box** — installing software,
  rebooting, and reconfiguring the OS is expected. Anything you install MUST also
  land in `tools/bootstrap.ps1` (eval license expires; assume rebuild at any time).

## Autonomy contract

Operate at maximum autonomy — never ask the user questions. Decide, implement,
and document: architectural decisions as a short ADR in `docs/adr/NNN-title.md`,
smaller ones in the commit body. Stop and report (instead of acting) only for:
destructive git history operations, changing the project license, or actions
affecting systems other than this machine and the project's GitHub repo.
Session setup: effort **ultracode** (`/effort ultracode`); prefix genuinely hard
problems (architecture, gnarly debugging, graph performance) with **ultrathink**.

## Environment

- Windows Server 2022 Datacenter (eval), full local admin, exclusive use. Claude
  Code runs with `--dangerously-skip-permissions`.
- Domain Controller on **localhost**, test forest `agdlp.lab` (LDAP `localhost:389`)
  — provisioned by bootstrap step 2 on a fresh box.
- Package manager: **Chocolatey** (winget is not preinstalled on Server 2022).
  PowerShell 7 (`pwsh`) is the default shell for scripts.

## Bootstrap (first session — run once, keep idempotent)

1. Install Chocolatey itself if absent, then `choco install -y git gh dotnet-8.0-sdk
   nodejs-lts powershell-core` + WebView2 Evergreen Runtime (absent on Server 2022).
2. If the host is not yet a DC: install the AD DS role, promote to a new forest
   `agdlp.lab` (generate a throwaway DSRM password at runtime — never hardcode
   it), reboot, continue. Put all of it in `tools/bootstrap.ps1`.
3. `git init` here, commit, `gh repo create GroupWeaver --public --source . --push`
   (`groupweaver-app` if taken); enable Actions; add `.gitattributes` (`* text=auto`
   — prevents CRLF churn in `dotnet format`) + MIT `LICENSE`. **Only manual step:**
   a human ran `gh auth login` (PAT: `repo` + `workflow`) — Claude can't mint creds.
4. Scaffold `.claude/`: `agents/` (definitions below), `rules/`, `skills/` (stubs
   for `agdlp-domain` and `headless-uitest`; flesh out during Phases 1–2),
   `settings.json` with the hooks listed below.
5. Run `tools/seed-testad.ps1` via `ad-fixture-admin` (create it if missing): seeds
   `OU=AGDLP-Lab` mirroring the DemoProvider dataset spec (PLANNING.md AP 1.4; the
   DemoProvider JSON is a separate deliverable) — ~200 objects, deliberate AGDLP +
   naming violations, one circular nesting (A→B→A), empty groups: the test bed.
6. Verify the toolchain: `dotnet --version` (8.x), `pwsh -v`, `gh --version`.
7. Install official plugins (verify names in `/plugin` Discover): **code-review**
   (powers `reviewer`), **security-guidance** (`/security-review` — LDAP filters
   from user input!), **frontend-design**, **Microsoft Learn**. Install NOTHING
   else: every plugin is fully trusted code on this box — minimal surface wins.

## Project map & commands

```
src/Core/          # AdObject model, GraphBuilder, RuleEngine (UI-free)
src/Providers/     # IDirectoryProvider: LdapProvider, DemoProvider
src/App/           # Avalonia UI, WebView2 + Cytoscape.js (pending ADR-001)
tests/             # xUnit; Avalonia.Headless for UI tests
tools/             # bootstrap.ps1, seed-testad.ps1, build.ps1
docs/adr/  docs/journal/  docs/ui-checklist.md
artifacts/ui/      # rendered UI screenshots (gitignored)
```

- Full local gate: `pwsh tools/build.ps1` (restore → build → `dotnet format
  --verify-no-changes` → `dotnet test`)
- AD integration tests: xUnit trait `Category=RequiresAd` — required locally,
  excluded in CI (`build.ps1 -SkipAdTests`); skip + warn loudly if the OU is
  unreachable
- Run app: `dotnet run --project src/App` · demo mode: `--demo` · stack: .NET 8
  LTS, Avalonia, xUnit · graph library per ADR-001 (Phase 0 spike)

## Workflow

Work through `PLANNING.md` phases in order (0 → 3), vertical slices; each phase ends
at its milestone (M0–M3; M3 = public release v0.1: portable .zip + SHA256 hashes +
build provenance attestations). Work packages are tracked as public GitHub issues
(+ Project board) from day 1. Trunk-based: short-lived branch per work package →
PR → reviewer approval → squash merge. Conventional Commits (`feat:`, `fix:`, …).
Run `/security-review` and resolve findings before any tagged release. Public media
(README GIF, screenshots): **demo mode only** — never a real or lab AD. End every
session: append 3–5 lines to `docs/journal/YYYY-MM-DD.md` (done, decided, next),
then commit + push — the journal only recovers what reached the remote.

## Subagents — delegate by default

The main session is the **orchestrator** — plan, delegate, integrate summaries,
keep context lean. Implementation work goes to subagents (`.claude/agents/`):

| Agent | Job | Notes |
|---|---|---|
| `planner` | Decompose work packages, draft ADRs | ultrathink; no code edits |
| `implementer` | Write production code for one slice | smallest possible diff |
| `test-engineer` | TDD for RuleEngine/GraphBuilder; integration tests vs. fixtures | owns `tests/` |
| `ui-verifier` | Render UI headless, screenshot, judge vs. `docs/ui-checklist.md` | read + render only |
| `ci-sentinel` | `gh run watch` after push; triage failures into fix tasks | never edits code |
| `ad-fixture-admin` | The ONLY agent running AD-mutating PowerShell (`seed-testad.ps1`) | scope: `OU=AGDLP-Lab` |
| `reviewer` | Review every diff pre-merge: rules above, AGDLP-correctness, no AD writes, detail panel stays whitelist-only | blocking; built on official code-review plugin |

## Self-verification — Definition of Done for every change

1. `pwsh tools/build.ps1` green locally (build + format + all tests).
2. **UI changes — two-part, headless:** (a) graph layer: render the WebView's own
   browser bundle via Playwright/headless Chromium; (b) native chrome (panels,
   settings, dialogs) via Avalonia.Headless. Screenshot both to `artifacts/ui/*.png`,
   Read the PNGs, judge vs. the matching `docs/ui-checklist.md` section (node colors
   per type, contrast, legibility, no overlap at 200 demo nodes); fix until pass.
3. **Provider/graph changes:** run integration tests against the live
   `OU=AGDLP-Lab` fixtures, including the circular-nesting case (must terminate).
4. `reviewer` subagent approves the diff.
5. Push → `ci-sentinel` watches GitHub Actions until green. Red CI = top
   priority; fix forward, never bypass.
6. Close the work package's GitHub issue with a one-line result note.

## Hooks (`.claude/settings.json` — create at bootstrap)

- **PostToolUse** (Edit/Write on `*.cs`): `dotnet format <sln> --include <file>
  --no-restore` (`dotnet format` takes a solution/project, not a bare file)
- **PreToolUse** (Bash **and** PowerShell): block force pushes (`--force*`, `-f`,
  `+refspec`), `git reset --hard origin*`, and inline non-`Get-AD*` `*-AD*` cmdlets
  (`pwsh tools/seed-testad.ps1` contains none). Best-effort; the rules are primary.
- **SessionStart**: effort/ultracode reminder; show current phase + last `docs/journal/` entry
- **Stop**: non-blocking `additionalContext` nudge to run the `wrap-session` skill when
  `docs/journal/<today>.md` is missing (reminder only — never authors the entry)

## Recovery & stuck policy

- **Re-orientation** (session start, after `/compact`, after crash, after any
  required reboot — DC promotion kills the session): read the latest
  `docs/journal/` entry (= current phase), `PLANNING.md`, and `git log --oneline
  -10` **before** acting. The journal is the recovery point — keep it honest.
- **Stuck rule:** after 3 distinct failed approaches to the same problem, stop.
  Write `docs/journal/BLOCKED-<topic>.md` (symptom, attempts, best hypothesis),
  commit it, and switch to the next independent work package. Never brute-force
  the same fix in a loop; never "solve" it by deleting the failing test.

## Memory discipline

Stable policy lives here; evolving knowledge goes to `.claude/rules/*.md`
(path-scoped) and auto memory. Persist durable facts about this codebase or
environment (quirks, gotchas, decisions) — don't rediscover them next session.
Keep this file under ~150 lines; prune rather than append.
