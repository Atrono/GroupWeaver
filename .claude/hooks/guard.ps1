# PreToolUse guard (Bash|PowerShell): blocks destructive git commands and inline
# AD-mutating cmdlets. Best-effort backstop - the CLAUDE.md prohibitions are
# primary. The sanctioned AD-write path is `pwsh tools/seed-testad.ps1`, whose
# command text contains no AD verb, so it passes untouched.
$ErrorActionPreference = 'SilentlyContinue'

$payload = [Console]::In.ReadToEnd()
try { $json = $payload | ConvertFrom-Json } catch { exit 0 }
$cmd = [string]$json.tool_input.command
if (-not $cmd) { exit 0 }

function Block([string]$reason) {
    [Console]::Error.WriteLine("BLOCKED by .claude/hooks/guard.ps1: $reason - see CLAUDE.md non-negotiable rules.")
    exit 2
}

# Force pushes in any spelling (--force, --force-with-lease, -f, +refspec)
if ($cmd -match 'git\s+push\b' -and $cmd -match '(\s--force(-with-lease)?\b|\s-f\b|\s\+\S+)') {
    Block 'force push'
}
if ($cmd -match 'git\s+reset\s+--hard\s+\S*origin') {
    Block 'hard reset to origin'
}

# Inline AD-mutating cmdlets (anything but Get-AD*)
$adWrite = '(?i)\b(New|Set|Remove|Add|Move|Rename|Enable|Disable|Unlock|Clear|Restore|Grant|Revoke)-AD[A-Za-z]+'
if ($cmd -match $adWrite) {
    Block "inline AD-mutating cmdlet ($($Matches[0])); only tools/seed-testad.ps1 may write to AD"
}

exit 0
