# SessionStart: orient the session - effort reminder, current phase, last journal.
$ErrorActionPreference = 'SilentlyContinue'

Write-Output '=== GroupWeaver session start ==='
Write-Output 'Effort: ultracode expected (/effort ultracode); ultrathink on hard problems. See CLAUDE.md.'

$root = if ($env:CLAUDE_PROJECT_DIR) { $env:CLAUDE_PROJECT_DIR } else { (Get-Location).Path }
$journal = Join-Path $root 'docs/journal'
$last = Get-ChildItem -Path $journal -Filter '*.md' -File -ErrorAction SilentlyContinue |
    Where-Object Name -notlike 'BLOCKED-*' | Sort-Object Name | Select-Object -Last 1
if ($last) {
    Write-Output "Latest journal entry ($($last.Name)) - current phase is recorded here:"
    Get-Content $last.FullName -TotalCount 15
}
else {
    Write-Output 'No journal entries yet - if bootstrap is incomplete, start at CLAUDE.md "Bootstrap".'
}
$blocked = Get-ChildItem -Path $journal -Filter 'BLOCKED-*.md' -File -ErrorAction SilentlyContinue
if ($blocked) { Write-Output "Open blockers: $($blocked.Name -join ', ')" }
exit 0
