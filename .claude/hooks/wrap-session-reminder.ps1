# Stop hook: nudge to run the wrap-session skill when today has no journal entry.
# A REMINDER ONLY - it never writes the entry (authoring Done/Decided/Next needs
# session judgment a hook cannot supply; the skill owns that). Non-blocking: it
# emits additionalContext and never decision:block, so it surfaces the nudge on
# the next turn without ever forcing Claude to keep working. Mirror partner of
# session-start.ps1 (which reads the latest entry to orient the next session).
$ErrorActionPreference = 'SilentlyContinue'

$payload = [Console]::In.ReadToEnd()
try { $json = $payload | ConvertFrom-Json } catch { $json = $null }
# Loop guard: if a Stop hook already blocked this turn, stay silent.
if ($json -and $json.stop_hook_active) { exit 0 }

$root = if ($env:CLAUDE_PROJECT_DIR) { $env:CLAUDE_PROJECT_DIR } else { (Get-Location).Path }
$today = Get-Date -Format 'yyyy-MM-dd'
$entry = Join-Path $root "docs/journal/$today.md"
if (Test-Path $entry) { exit 0 }   # already journaled today - nothing to remind

$msg = "Session-end reminder: docs/journal/$today.md does not exist yet. If you are wrapping up this session, run the wrap-session skill to write the Done/Decided/Next entry, then commit and push (the journal only recovers what reached the remote)."
$out = @{ hookSpecificOutput = @{ hookEventName = 'Stop'; additionalContext = $msg } }
$out | ConvertTo-Json -Compress
exit 0
