<#
.SYNOPSIS
    E2E scenario: lazy-expand race (ADR-038 WP6b, P1.1, issue #245).

.DESCRIPTION
    Proves WorkspaceViewModel.OnNodeExpandRequested's single global busy gate
    (src/App/ViewModels/WorkspaceViewModel.cs: "if (_disposed || IsLoading ||
    Snapshot is null) { return; }") holds under a rapid-fire double-expand: two
    dbltaps fired at two DIFFERENT unexpanded frontier nodes with NO settle in
    between never double-fetches, never crashes, and converges to a nodeCount
    growth attributable to EXACTLY ONE of the two candidates.

    SCOPE DESIGN (the calibration problem the plan flagged as open): the root
    candidate 'UG_AllStaff' (a UniversalGroup, itself a valid GetRootCandidatesAsync
    entry) is loaded as the scope ROOT. DemoProvider.LoadScopeAsync only adds
    objects that are DN-descendants of the base DN - a group's own DN has no
    descendants in this flat dataset, so the ONLY in-scope object is UG_AllStaff
    itself; its 6 real members (5 role groups + one raw user DN) are recorded via
    SetMembers but never added as Objects, so GraphBuilder's CollectSeeds
    materializes every one of them as a synthetic External seed (data-model.md:
    GetKind returns External for any DN missing from the snapshot, regardless of
    its TRUE directory kind). This is a genuinely, honestly PARTIALLY loaded scope
    (UG_AllStaff.IsLoaded=true, every member's own membership unloaded) - no
    "expand one level first" step is needed, unlike the plan's fallback: all 6
    frontier nodes are already visible at the very first Fit render (confirmed:
    initial state.nodeCount=7, edgeCount=6 - the root plus 6 External seeds).

    Two of those six - GG_Sales_Staff and GG_Finance_Staff - are picked as the
    race targets: both are REAL GlobalGroup objects in the full embedded dataset
    (demo-directory.json) with exactly 20 direct User members each (all disjoint
    department name sets, confirmed no overlap), so expanding EITHER one adds
    exactly 20 new nodes (the group's own DN was already counted as an External
    seed pre-expand, so its own resolution contributes 0 net growth; only its
    fetched members are new). This gives a decisive three-way signal on the
    combined post-race nodeCount growth: 0 = both gestures dropped (gate broken
    the other way / render regression), 20 = EXACTLY ONE of the two won (the gate
    held), 40 = BOTH fetched (the gate is missing entirely). Canvas coordinates
    for both nodes were empirically calibrated off a checkpoint screenshot at the
    fixed Start-E2EApp geometry (60,60 / 1480x920) and independently verified
    end-to-end pre-implementation: a lone dbltap on EITHER coordinate alone grows
    nodeCount from 7 to exactly 27 (edgeCount 6 to 47), and 7 back-to-back
    no-settle race runs (both click orders) all converged on nodeCount=27 - never
    7, never 47.

    State is HERMETIC (ADR-038 D3.1, WP5) AND channelled (ADR-038 D3.2, WP6):
    '--demo --state-dir <dir> --e2e'. Tier B (ADR-038 D2): actions are real
    posted WM_* input (Tier A) - the channel only GATES/ASSERTS via read-only
    'state' polls, never invokes/clicks.

    Windows PowerShell 5.1 (relaunches itself from pwsh); ASCII-ONLY file.
#>
[CmdletBinding()]
param(
    [string]$ArtifactDir = '',
    [string]$StateDir = '',
    [string]$AppExe = ''
)

$ErrorActionPreference = 'Stop'

# --- relaunch under Windows PowerShell 5.1 when started from pwsh -------------
if ($PSVersionTable.PSEdition -eq 'Core') {
    $ps51 = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
    & $ps51 -NoProfile -NonInteractive -ExecutionPolicy Bypass -File $PSCommandPath `
        -ArtifactDir $ArtifactDir -StateDir $StateDir -AppExe $AppExe
    exit $LASTEXITCODE
}

# --- defaults for direct (runner-less) invocation ------------------------------
$repoRoot = Split-Path -Parent (Split-Path -Parent (Split-Path -Parent $PSScriptRoot))
if (-not $ArtifactDir) {
    $ArtifactDir = Join-Path $repoRoot ('artifacts\e2e\adhoc\lazy-expand-race-{0:yyyyMMdd-HHmmss}' -f (Get-Date))
}
if (-not $AppExe) {
    $AppExe = Join-Path $repoRoot 'src\App\bin\Release\net8.0-windows\GroupWeaver.App.exe'
}
if (-not $StateDir) {
    $StateDir = Join-Path $env:TEMP ('gw-e2e\adhoc\lazy-expand-race-{0:yyyyMMdd-HHmmss}' -f (Get-Date))
}

. (Join-Path (Split-Path -Parent $PSScriptRoot) 'lib\e2e-driver.ps1')

Initialize-E2eContext -ScenarioName 'lazy-expand-race' -ArtifactDir $ArtifactDir
$runStart = Get-Date

# 'UG_AllStaff' (RootPickerViewModel.FilteredCandidates substring match) - a
# UniversalGroup root candidate whose own DN has no descendants, so LoadScopeAsync
# loads ONLY the group itself: a genuinely partial scope (see .DESCRIPTION).
$filterText = 'UG_AllStaff'

# Two of UG_AllStaff's 6 raw members (demo-directory.json), both REAL GlobalGroup
# objects with exactly 20 disjoint User members each - the race targets. Canvas
# coordinates calibrated at the initial Fit view (no zoom/pan needed - see the
# .DESCRIPTION verification note).
$nodeADn = 'CN=GG_Sales_Staff,OU=Groups,OU=AGDLP-Demo,DC=weavedemo,DC=example'
$PT_NodeA = @(617, 361)
$knownDeltaA = 20

$nodeBDn = 'CN=GG_Finance_Staff,OU=Groups,OU=AGDLP-Demo,DC=weavedemo,DC=example'
$PT_NodeB = @(495, 697)
$knownDeltaB = 20

# Hermetic state (ADR-038 D3.1, WP5): deterministic ui-state.json (rail expanded +
# dark theme) into the scenario's OWN state dir - never the real %APPDATA%.
[void](Initialize-E2eStateDir -StateDir $StateDir)

$failed = $false
try {
    # --demo is MANDATORY with --state-dir/--e2e (both app-side demo gates); the
    # startup auto-connect lands directly on the root picker.
    [void](Start-E2EApp -ExePath $AppExe -AppArgs @('--demo') -StateDir $StateDir -E2e)
    Assert-Alive 'launch (window up)'

    Invoke-RootLoad -FilterText $filterText
    Assert-Alive 'demo connect + narrow-scope load'

    Wait-ChromiumChild -TimeoutSec 60
    [void](Wait-E2eStep -Expected 'Workspace' -TimeoutSec 15 -What 'initial workspace state')
    Wait-E2eSettled -TimeoutSec 10 -What 'initial workspace settle' | Out-Null
    Capture-Checkpoint 'workspace'
    Assert-Alive 'workspace render'

    # --- baseline: the honestly-partial scope, 2 known frontier nodes visible ------
    $before = Wait-E2eState -TimeoutSec 10 -What 'pre-race state'
    if ($before.nodeCount -ne 7 -or $before.edgeCount -ne 6) {
        throw "ASSERT::pre-race - nodeCount/edgeCount is $($before.nodeCount)/$($before.edgeCount), expected 7/6 (root + 6 External frontier seeds) - the scope shape this scenario is calibrated against has drifted"
    }
    Write-DriverLog 'pre-race-confirmed' @{ nodeCount = $before.nodeCount; edgeCount = $before.edgeCount }

    # --- THE RACE: two dbltaps at two DIFFERENT frontier nodes, NO settle between --
    # (ADR-005 D3's single global busy gate is the thing under test - a settle here
    # would defeat the point.)
    Send-CanvasClick $PT_NodeA[0] $PT_NodeA[1] $true
    Send-CanvasClick $PT_NodeB[0] $PT_NodeB[1] $true
    Throw-IfCrashed 'double dbltap (lazy-expand race)'

    Wait-E2eSettled -TimeoutSec 20 -What 'post-race settle' | Out-Null
    $after = Wait-E2eState -TimeoutSec 10 -What 'post-race state'
    Assert-Alive 'post-race'
    Capture-Checkpoint 'post-race'

    $delta = [int]$after.nodeCount - [int]$before.nodeCount
    Write-DriverLog 'race-result' @{
        beforeNodeCount = $before.nodeCount
        afterNodeCount  = $after.nodeCount
        delta           = $delta
        knownDeltaA     = $knownDeltaA
        knownDeltaB     = $knownDeltaB
    }

    # The decisive three-way signal (see .DESCRIPTION): 0 = both gestures dropped
    # (a render/gate regression the OTHER way), 20 = exactly one of A/B won (the
    # busy gate held - the expected, correct outcome), 40 = BOTH fetched (the gate
    # is missing). Node A and B's known deltas happen to be EQUAL (20 and 20,
    # both 20-member GlobalGroups) - a double-fetch or a dropped-both race would
    # still be caught, since 40 and 0 match neither.
    if ($delta -ne $knownDeltaA -and $delta -ne $knownDeltaB) {
        throw "ASSERT::lazy-expand-race - nodeCount grew by $delta (from $($before.nodeCount) to $($after.nodeCount)), expected EXACTLY node A's ($nodeADn, +$knownDeltaA) or node B's ($nodeBDn, +$knownDeltaB) known member count - not 0 (both dropped) and not $($knownDeltaA + $knownDeltaB) (both fetched)"
    }
    Write-DriverLog 'lazy-expand-race-confirmed' @{ delta = $delta }

    # --- cross-cutting invariant pack (ADR-038 D4) plus the trace-error scan --------
    Assert-NoUnexpectedDialogs 'end of journey'
    Invoke-CleanShutdownGate
    Assert-CleanStderr 'after shutdown'
    Assert-NoNewWerDumps -Since $runStart -Where 'end of scenario'
    Assert-E2eNoUnexpectedTraceErrors

    Write-Result -Result pass -AppExitCode 0
}
catch {
    $failed = $true
    Complete-E2eFailure -ErrorRecord $_ -RunStart $runStart
}
finally {
    Stop-E2EAppForce
}

if ($failed) { exit 1 } else { exit 0 }
