---
name: cut-release
description: How to cut a public GroupWeaver release - version bump, the two in-sync pins, the pack-release pre-flight + security gate, tag-on-the-bump-commit, the release.yml auto-publish, and post-tag verification of the public zip (SHA256 + provenance + launch). Use when bumping the version, cutting v0.x.y, or producing the portable .zip download.
---

# Cutting a release

Mirrors v0.1 / v0.2.0 / v0.2.1 (ADR-012). The per-step mechanics live in
`tools/pack-release.ps1` (read it once) and `.github/workflows/release.yml`; this
skill is the **ordered sequence + the gotchas those files don't state**. Start
from a clean `main` with the v0.x.y change-set already merged.

## The four bump files (items 1–2 are hard pins)

A version bump touches exactly four files. Items 1–2 are hard pins — miss either
and a gate goes red; items 3–4 are docs that must still be updated:

1. `Directory.Build.props` — `<Version>`. The single source of truth (stamps
   `AssemblyInformationalVersion`, the `--check` banner, the zip name).
2. `tests/GroupWeaver.Tests/SmokeTests.cs` — `Assert.StartsWith("X.Y.Z", …)` in
   `CoreAssembly_InformationalVersion_StartsWithPinnedVersion` (+ its comment).
   Bump #1 without this and `dotnet test`/CI is red.
3. `README.md` — the `**Latest release: vX.Y.Z**` line in the "Download" section + the
   three `GroupWeaver-X.Y.Z-win-x64.zip` references in "Verify your download".
4. `CHANGELOG.md` — new `## [X.Y.Z] - <date>` section.

Do NOT touch the `ToolVersion: "0.2.0"` / `9.9.9-test` literals in the Plan-export
tests — they are fixed fixtures, decoupled from the app version on purpose.

## Sequence

1. **Bump** the four files above (branch `chore/release-X.Y.Z`).
2. **Local gate:** `pwsh tools/build.ps1` (build + format + all tests incl. the
   pin smoke). Run from the repo root — a stale agent-worktree cwd silently
   retargets git (see [[lab-environment]]).
3. **Pack pre-flight:** `pwsh tools/pack-release.ps1 -Version X.Y.Z`. This is the
   identical script CI runs; green here is the strongest proof the release works.
   It publishes self-contained single-file win-x64 (NO trim/R2R/compression —
   ADR-012, reflection-reached types), checks the web bundle is byte-identical,
   then launch-smokes the *published* exe (`--check --demo` must print
   `GroupWeaver X.Y.Z` + `connected, N groups loaded`; `--demo --dump-graph`).
   Output is gitignored `artifacts/release/` — never commit it.
4. **Security gate (required before any tag):** run `/security-review` over
   `git log v<prev>..HEAD`; resolve every finding. Use
   [[security-review-groupweaver]] for where to look. Then `reviewer` approves
   the bump diff.
5. **PR → squash merge** (trunk-based). `ci-sentinel` watches CI green first.
6. **Tag the merged bump commit, then push the tag:**
   `git tag vX.Y.Z <bump-sha>` → `git push origin vX.Y.Z`.
7. **Watch `release.yml`** to green (`gh run watch`). It derives the version from
   the tag, re-runs the pack with `-Emit github`, attests provenance, and
   `gh release create`s the zip + `.sha256`.
8. **Verify the PUBLIC artifact** (the real M-step): `gh release download vX.Y.Z`
   into a clean dir → `Get-FileHash` matches the `.sha256` sidecar →
   `gh attestation verify <zip> --repo Atrono/GroupWeaver` exits 0 → extract and
   run the exe `--check --demo` → exit 0 with the right banner.

## Gotchas (the non-obvious, gate-failing bits)

- **Tag the bump commit, not `main`'s HEAD.** Journal/CI-fix commits land on
  `main` *after* the tag by design, so HEAD drifts past the release commit. Pass
  the explicit SHA: `git tag vX.Y.Z <bump-sha>`.
- **Repo must be PUBLIC** or the attestation step fails ("Feature not available
  for user-owned private repositories") — it failed exactly this way on the first
  v0.1 run.
- **`dotnet format` CLI-resolution flake (#110):** on `windows-2022`, `dotnet
  format` (a child process) intermittently can't locate the SDK after
  `setup-dotnet` (resolves via `DOTNET_HOST_PATH`/`DOTNET_ROOT`, not PATH).
  `tools/build.ps1` pins both vars and invokes format via the absolute dotnet
  host, so the shared gate is deterministic; if a release run still dies with
  "Unable to locate dotnet CLI", that's the cause, not your bump.
- **The pack's zip manifest is hardcoded** (`$webFiles`/`$rulesetFiles`/
  `$rootFiles`). If the release adds/removes a shipped `web/` file or
  `examples/rulesets/` entry, update those arrays or the manifest verify fails.
- **Smoke uses `--check --demo`, never bare `--check`** — bare `--check` hits the
  live `LdapProvider` (no DC on a runner → hang/exit 1). And the smoke redirects
  BOTH stdout+stderr: `Start-Process` on this WinExe deadlocks if only one stream
  is redirected (the real cause of the original 44-min hang, not the AD call).
- **Commit + push gotcha:** never combine `git commit -F` and `git push` in one
  shell command — the guard hook flags it as a force-push. Split them.
- **Attestation action is pinned by SHA** (`@v3.0.0`; v2 wrapped the removed Node
  20 runtime, #79). `gh attestation verify` is version-agnostic.
