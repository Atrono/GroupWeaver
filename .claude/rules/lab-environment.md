# Lab box environment facts

- **GPU:** Intel UHD Graphics 620; the driver (31.0.101.2141) was only installed
  2026-06-11 *mid-Phase-0* — every run before that was Chromium software
  rendering. After a box rebuild (eval license!) the driver is gone again
  (Microsoft Basic Display Adapter) until reinstalled manually; `bootstrap.ps1`
  does NOT install it. Perf numbers must always state which rendering mode they
  were measured in; the software-rendered numbers are the target-audience floor
  (RDP/server/VM) — see `spikes/GraphSpike/RESULTS-software-rendering.md`.
- **`bash`/`sh` ARE on the Machine PATH since 2026-06-12** (`C:\Program
  Files\Git\bin`, added by `bootstrap.ps1` step 2b; `usr\bin` deliberately NOT —
  its Unix `find`/`sort` risk shadowing Windows'). Sessions started before the
  change won't see it. `jq` is still not installed; keep statusline/hooks
  PowerShell-native anyway (Windows-first box).
- **Hook commands run through Git Bash, not PowerShell** (Claude Code's Windows
  default; PowerShell only if Git Bash is absent). Bash expands `$env` and
  `$LASTEXITCODE` to empty strings before `pwsh` ever sees the command — never
  use PowerShell `$`-syntax in the `command` string. Correct pattern (fixed
  2026-06-12): `pwsh -NoProfile -File "$CLAUDE_PROJECT_DIR/.claude/hooks/x.ps1"`
  — POSIX env-var form, and `-File` propagates the script's `exit n` natively
  (the old `-Command "& …"; exit ($LASTEXITCODE ?? 1)` wrapper is obsolete).
  Claude Code only blocks on exit code 2.
- **GraphSpike gotchas (bind Phase 2):** exe needs `app.manifest` with
  `<supportedOS>` for NativeControlHost; perf harnesses must drive DOM-level
  gestures (programmatic `cy.zoom()/cy.pan()` bypasses cytoscape's viewport
  optimizations); `invokeCSharpAction` is injected async (bridge queues);
  file:// = opaque origin mutes `window.onerror` details.
- **AD quirks (found during AP 1.5):** SAM rejects well-known special-identity
  SIDs (S-1-5-11 etc.) as members of *account-domain* groups — only BUILTIN
  aliases may hold them; lab FSP fixture therefore uses a fabricated
  foreign-domain SID via the `<SID=...>` binding form. `Get-ADGroupMember`
  throws "unspecified error" on any group containing an unresolvable FSP —
  always read the raw `member` attribute instead. The DC is German-localized
  (localized group names, German `dotnet` output) — never depend on localized
  names; well-known container names (`CN=ForeignSecurityPrincipals`) are safe.
