#Requires -Version 5.1
<#
.SYNOPSIS
    HEURISTIC staleness reminder for the README demo media in docs/media.

.DESCRIPTION
    Compares the git committer timestamp of the newest commit that touched
    docs/media against the newest commit touching each media-SOURCE path (the
    UI that determines what the media shows). If any source path has a commit
    NEWER than the media, the media MAY be stale and should be refreshed.

    This is a git-timestamp HEURISTIC, NOT proof of drift. It can OVER-warn:
    - Rendering is non-deterministic across GPU vs software modes, so a strict
      pixel gate is impossible.
    - Not every source change actually changes what the media shows (e.g. a
      comment or a refactor with no visual effect).

    Therefore this check is NON-BLOCKING by default (always exits 0). It is a
    reminder: when it warns, regenerate the media via the record-demo-media
    skill, then re-run to confirm the warning clears. Pass -Strict to make it
    exit 1 on a stale trigger (for opt-in CI / release gating).

.PARAMETER Strict
    Exit 1 instead of 0 when the media appears stale. Default: exit 0 (reminder
    only, never fails the caller).
#>
[CmdletBinding()]
param(
    [switch]$Strict
)

$ErrorActionPreference = 'Stop'

# Repo root, robustly -- run all git from there so cwd never matters.
$root = (git rev-parse --show-toplevel 2>$null)
if (-not $root) {
    Write-Warning 'check-media-currency: not inside a git work tree - skipping.'
    exit 0
}
$root = $root.Trim()

# Newest commit touching the media itself (committer epoch).
$mediaEpochRaw = (git -C $root log -1 --format=%ct -- docs/media 2>$null)
if (-not $mediaEpochRaw) {
    Write-Warning 'check-media-currency: docs/media has no commits yet - nothing to compare against.'
    exit 0
}
$mediaEpoch = [long]$mediaEpochRaw.Trim()

# Media-SOURCE path specs: the UI that determines what the media shows.
$sourceSpecs = @(
    # Graph canvas -- affects both README PNGs and the walkthrough GIF.
    'src/App/web'
    'src/App/Graph'
    'src/App/Views/AdObjectKindConverters.cs'
    'src/App/Views/SeverityConverters.cs'
    # GIF native chrome -- affects the GIF walkthrough (pickers, panels, layout).
    'src/App/Views/RootPickerView.axaml'
    'src/App/Views/RootPickerView.axaml.cs'
    'src/App/Views/DetailPanelView.axaml'
    'src/App/Views/DetailPanelView.axaml.cs'
    'src/App/Views/ViolationsSidebarView.axaml'
    'src/App/Views/WorkspaceView.axaml'
    'src/App/Views/MainWindow.axaml'
    'src/App/Styles'
)

$triggers = @()
foreach ($spec in $sourceSpecs) {
    $epochRaw = (git -C $root log -1 --format=%ct -- $spec 2>$null)
    # Empty => git knows no commit for this path (untracked/removed) -- skip it.
    if (-not $epochRaw) { continue }
    $epoch = [long]$epochRaw.Trim()
    if ($epoch -gt $mediaEpoch) {
        $detail = (git -C $root log -1 --format='%h %ad %s' --date=short -- $spec 2>$null)
        if ($detail) { $detail = $detail.Trim() }
        $triggers += [pscustomobject]@{
            Spec   = $spec
            Detail = $detail
        }
    }
}

if ($triggers.Count -eq 0) {
    Write-Host 'OK: README media appears current (no media-source path has a commit newer than docs/media).'
    exit 0
}

Write-Warning 'README media may be STALE - source changed after the media was last regenerated. Refresh via the record-demo-media skill.'
foreach ($t in $triggers) {
    Write-Host ('  ' + $t.Spec)
    Write-Host ('      ' + $t.Detail)
}

if ($Strict) { exit 1 }
exit 0
