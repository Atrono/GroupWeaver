# Lab box environment facts

- **GPU:** Intel UHD Graphics 620; the driver (31.0.101.2141) was only installed
  2026-06-11 *mid-Phase-0* — every run before that was Chromium software
  rendering. After a box rebuild (eval license!) the driver is gone again
  (Microsoft Basic Display Adapter) until reinstalled manually; `bootstrap.ps1`
  does NOT install it. Perf numbers must always state which rendering mode they
  were measured in; the software-rendered numbers are the target-audience floor
  (RDP/server/VM) — see `spikes/GraphSpike/RESULTS-software-rendering.md`.
- **No `sh`, no `jq` on PATH.** Git Bash's `sh.exe` exists under
  `C:\Program Files\Git\(usr\)bin` but is not on PATH; `jq` is not installed at
  all. Anything user-facing (statusline, hooks) must be PowerShell-native.
- **Hook wrappers must propagate exit codes:** `pwsh -Command "& script.ps1"`
  swallows the script's `exit n`; always append `; exit ($LASTEXITCODE ?? 1)`
  (see `.claude/settings.json`, fixed 2026-06-11). Claude Code only blocks on
  exit code 2.
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
