# Lab box environment facts

- **Stale agent worktrees hijack the shell cwd (found in session 8):** the
  PowerShell tool's working directory persists across tool calls and can be
  left inside a finished subagent's `.claude/worktrees/agent-*` dir. Once that
  worktree's git metadata is pruned, git commands there silently retarget the
  MAIN repo (git walks up to the repo root) while *relative pathspecs* still
  resolve against the dead directory — branch switches land in the main repo,
  `git add <relative>` says "pathspec did not match". A shell parked there also
  blocks `git worktree remove`/`Remove-Item` with Permission denied. Fix:
  `Set-Location` back to the repo root before git ops after any worktree
  cleanup; prefer absolute paths or `git -C` in orchestrator sessions; remove
  leftover worktree dirs only after the owning agent's processes have exited.
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
- **Cytoscape wheel-zoom driving (found during M2 GIF):** after 4 wheel events
  cytoscape switches to discrete-wheel mode and normalizes EVERY detent to
  ~×1.0055 zoom (`h = 3/250 × wheelSensitivity 0.2`) — the `wParam` delta
  magnitude is ignored. Inflating deltas does nothing; post ONE `WM_MOUSEWHEEL`
  per detent (~25 ms apart) and scale by detent COUNT. Pointer-anchored zoom:
  aim the message coordinates at the target cluster.
- **GraphSpike gotchas (bind Phase 2):** exe needs `app.manifest` with
  `<supportedOS>` for NativeControlHost; perf harnesses must drive DOM-level
  gestures (programmatic `cy.zoom()/cy.pan()` bypasses cytoscape's viewport
  optimizations); `invokeCSharpAction` is injected async (bridge queues);
  file:// = opaque origin mutes `window.onerror` details.
- **Screenshot gotchas (found during AP 2.1):** the desktop runs at >100% DPI
  scale — PrintWindow captures of live windows need
  `SetThreadDpiAwarenessContext(-4)` first or `GetWindowRect` crops the right
  edge (reusable: `tools/capture-window.ps1`, also sets `PW_RENDERFULLCONTENT`
  for WebView2 content). Headless: `CaptureRenderedFrame` lags one compositor
  batch — the first capture after a VM mutation returns the *previous* frame;
  capture-and-discard then capture (no sleeps; see
  `tests/GroupWeaver.App.Tests/Screenshots/`).
- **Windowed-smoke driving (found during AP 2.2):** this agent context has no
  interactive input desktop (`SetCursorPos` fails, `OpenInputDesktop` denied) —
  real mouse injection is impossible. Drive Avalonia chrome via UIA patterns
  (`SelectionItemPattern`/`InvokePattern` work); drive the WebView canvas by
  posting `WM_LBUTTON*`/`WM_MOUSEMOVE`/`WM_MOUSEWHEEL` directly to the
  `Chrome_RenderWidgetHostHWND` child. Once that child HWND exists, UIA
  descendant queries on the window return ONLY Chromium content — Avalonia
  TextBlocks vanish from the UIA tree; judge Avalonia-side state from
  PrintWindow captures instead.
- **AD quirks (found during AP 1.5):** SAM rejects well-known special-identity
  SIDs (S-1-5-11 etc.) as members of *account-domain* groups — only BUILTIN
  aliases may hold them; lab FSP fixture therefore uses a fabricated
  foreign-domain SID via the `<SID=...>` binding form. `Get-ADGroupMember`
  throws "unspecified error" on any group containing an unresolvable FSP —
  always read the raw `member` attribute instead. The DC is German-localized
  (localized group names, German `dotnet` output) — never depend on localized
  names; well-known container names (`CN=ForeignSecurityPrincipals`) are safe.
- **Console display: OEM codepage 850 + glyph-poor fonts (found 2026-06-15):** a
  fresh box defaults to CP850 (not UTF-8) and ships only Consolas/Lucida, so
  Claude Code's TUI and Unicode output render as `?` boxes. Fixed reproducibly in
  `bootstrap.ps1`: §2c installs `tools/powershell-profile.ps1` (forces UTF-8
  console encoding, stays SILENT on stdout, PSReadLine interactive-only); §2d
  installs `CaskaydiaMono NFM` + Windows Terminal and sets that font as the
  conhost default. **conhost does NO font fallback** — `✻` (U+273B, Claude's
  spinner) AND `⏵⏵` (U+23F5, the bypass-permissions mode indicator) are absent
  from every installed monospace font (Consolas AND Cascadia/CaskaydiaMono NFM),
  so they stay `?`/boxes in conhost; only **Windows Terminal** (`wt`, falls
  back to Segoe UI Symbol) renders the full set. Takes effect in NEW console
  windows only — a running session keeps the old codepage/font.
