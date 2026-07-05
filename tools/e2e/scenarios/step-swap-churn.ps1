<#
.SYNOPSIS
    E2E scenario: repeated step-swap churn over the --e2e channel (ADR-038
    D3.2/WP6b, P0.6, issue #245).

.DESCRIPTION
    The cheapest possible proof the --e2e state/quit channel plumbing (WP6a's
    E2eChannel.cs + this WP's e2e-channel.ps1) actually works end to end, before
    investing in anything novel: churns the shell through
    Workspace -> Plan -> Gap -> Plan -> Workspace -> Audit -> Workspace, TWICE,
    confirming EVERY step transition via Wait-E2eStep (the app's own
    ShellViewModel.CurrentStepName, not a pixel/HWND heuristic) and
    Wait-E2eSettled (the renderer's cy.animated() page truth) instead of the
    fixed Wait-Step sleeps / pixel-blob confirms back-nav.ps1 needed before this
    channel existed. This directly closes back-nav.ps1's own noted gap ("full
    step-name state confirmation arrives with the WP5/WP6 stateProbe seam").

    Reuses back-nav.ps1's calibrated Plan/Gap/Back pixel coordinates and
    audit-run-persist.ps1's Audit coordinate; the Audit step's own
    back-to-workspace affordance (AuditView's "<- Back" button, Row 6, below the
    fold) is calibrated here for the first time - the page must be scrolled to
    the bottom (same over-scroll idiom as audit-run-persist) before it is
    clickable.

    State is HERMETIC (ADR-038 D3.1, WP5) AND channelled (ADR-038 D3.2, WP6):
    '--demo --state-dir <dir> --e2e' (Start-E2EApp -E2e asserts --demo is
    present). Tier B (ADR-038 D2): actions are still real UIA/posted WM_* input
    (Tier A) - the channel only GATES/ASSERTS via read-only 'state' polls, never
    invokes/clicks.

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
    $ArtifactDir = Join-Path $repoRoot ('artifacts\e2e\adhoc\step-swap-churn-{0:yyyyMMdd-HHmmss}' -f (Get-Date))
}
if (-not $AppExe) {
    $AppExe = Join-Path $repoRoot 'src\App\bin\Release\net8.0-windows\GroupWeaver.App.exe'
}
if (-not $StateDir) {
    $StateDir = Join-Path $env:TEMP ('gw-e2e\adhoc\step-swap-churn-{0:yyyyMMdd-HHmmss}' -f (Get-Date))
}

. (Join-Path (Split-Path -Parent $PSScriptRoot) 'lib\e2e-driver.ps1')

Initialize-E2eContext -ScenarioName 'step-swap-churn' -ArtifactDir $ArtifactDir
$runStart = Get-Date

# Same 2-node scope every other scenario uses - no findings needed for a pure
# navigation churn.
$filterText = 'DL_FS-Finance_RW'
$colorExternalNode = @(49, 85, 115)

# Hermetic state (ADR-038 D3.1, WP5): deterministic ui-state.json (rail expanded +
# dark theme) into the scenario's OWN state dir - never the real %APPDATA%.
[void](Initialize-E2eStateDir -StateDir $StateDir)

# --- button capture-coordinate map ------------------------------------------------
# Workspace/Plan/Gap: back-nav.ps1's calibrated map (RECALIBRATED 2026-07-03, WP4).
$PT_DesignPlan = @(1067, 560)
$PT_GapAnalysis = @(1095, 192)
$PT_PlanBack = @(1003, 241)
$PT_GapBack = @(1405, 150)
# Workspace -> Audit: audit-run-persist.ps1's calibrated map.
$PT_Audit = @(1390, 621)
$PT_AuditWheel = @(700, 450)
# Audit -> Workspace: AuditView.axaml Row 6 "<- Back" button (Grid.Column=0, left of
# the export/show-in-graph group), BELOW the run-history card - only reachable after
# the same over-scroll-to-bottom idiom audit-run-persist.ps1 uses for Save-audit-run.
# Calibrated against a checkpoint PNG taken at the fixed 60,60/1480x920 geometry.
$PT_AuditBack = @(115, 866)

$failed = $false
try {
    # --demo is MANDATORY with --state-dir/--e2e (both app-side demo gates); the
    # startup auto-connect lands directly on the root picker.
    [void](Start-E2EApp -ExePath $AppExe -AppArgs @('--demo') -StateDir $StateDir -E2e)
    Assert-Alive 'launch (window up)'

    Invoke-RootLoad -FilterText $filterText
    Assert-Alive 'demo connect + root load'

    Wait-ChromiumChild -TimeoutSec 60
    [void](Wait-NodeBlob { Save-Probe } $colorExternalNode 30 'the external frontier node (render signal)')
    Write-DriverLog 'workspace-rendered' @{}
    Capture-Checkpoint 'workspace'
    Assert-Alive 'workspace render'
    [void](Wait-E2eStep -Expected 'Workspace' -TimeoutSec 15 -What 'initial workspace state')
    Wait-E2eSettled -TimeoutSec 10 -What 'initial workspace settle' | Out-Null

    for ($cycle = 1; $cycle -le 2; $cycle++) {
        Write-DriverLog 'churn-cycle-start' @{ cycle = $cycle }

        # Workspace -> Plan
        Click-CapturePoint $PT_DesignPlan[0] $PT_DesignPlan[1] "Design plan (cycle $cycle)"
        Throw-IfCrashed "Design plan -> Plan step (cycle $cycle)"
        [void](Wait-E2eStep -Expected 'Plan' -TimeoutSec 15 -What "Workspace -> Plan (cycle $cycle)")
        Wait-E2eSettled -TimeoutSec 10 -What "Plan settle (cycle $cycle)" | Out-Null
        Assert-Alive "Workspace -> Plan (cycle $cycle)"
        Capture-Checkpoint "cycle$cycle-plan"

        # Plan -> Gap
        Click-CapturePoint $PT_GapAnalysis[0] $PT_GapAnalysis[1] "Gap analysis (cycle $cycle)"
        Throw-IfCrashed "Plan -> Gap step (cycle $cycle)"
        [void](Wait-E2eStep -Expected 'Gap' -TimeoutSec 15 -What "Plan -> Gap (cycle $cycle)")
        Wait-E2eSettled -TimeoutSec 10 -What "Gap settle (cycle $cycle)" | Out-Null
        Assert-Alive "Plan -> Gap step (cycle $cycle)"
        Capture-Checkpoint "cycle$cycle-gap"

        # Gap -> Plan (Back)
        Click-CapturePoint $PT_GapBack[0] $PT_GapBack[1] "<- Back to plan (cycle $cycle)"
        Throw-IfCrashed "Gap -> Back to Plan (cycle $cycle)"
        [void](Wait-E2eStep -Expected 'Plan' -TimeoutSec 15 -What "Gap -> Plan (cycle $cycle)")
        Wait-E2eSettled -TimeoutSec 10 -What "Plan re-settle (cycle $cycle)" | Out-Null
        Assert-Alive "Gap -> Back to Plan (cycle $cycle)"

        # Plan -> Workspace (Back)
        Click-CapturePoint $PT_PlanBack[0] $PT_PlanBack[1] "<- Back to explore (cycle $cycle)"
        Throw-IfCrashed "Plan -> Back to Workspace (cycle $cycle)"
        [void](Wait-E2eStep -Expected 'Workspace' -TimeoutSec 15 -What "Plan -> Workspace (cycle $cycle)")
        Wait-E2eSettled -TimeoutSec 10 -What "Workspace re-settle (cycle $cycle)" | Out-Null
        Assert-Alive "Plan -> Back to Workspace (cycle $cycle)"
        Capture-Checkpoint "cycle$cycle-back-to-workspace"

        # Workspace -> Audit
        Click-CapturePoint $PT_Audit[0] $PT_Audit[1] "Audit (cycle $cycle)"
        Throw-IfCrashed "Workspace -> Audit step (cycle $cycle)"
        [void](Wait-E2eStep -Expected 'Audit' -TimeoutSec 15 -What "Workspace -> Audit (cycle $cycle)")
        Assert-Alive "Workspace -> Audit step (cycle $cycle)"
        Capture-Checkpoint "cycle$cycle-audit-top"

        # Audit's own Back affordance sits below the fold - over-scroll to the bottom
        # first (same idiom as audit-run-persist.ps1's Save-audit-run approach).
        Send-MainWindowWheel -Ticks -40 -CaptureX $PT_AuditWheel[0] -CaptureY $PT_AuditWheel[1]
        Start-Sleep -Milliseconds 600
        Assert-Alive "Audit page scrolled (cycle $cycle)"
        Capture-Checkpoint "cycle$cycle-audit-bottom"

        # Audit -> Workspace (Back)
        Click-CapturePoint $PT_AuditBack[0] $PT_AuditBack[1] "<- Back to workspace from Audit (cycle $cycle)"
        Throw-IfCrashed "Audit -> Back to Workspace (cycle $cycle)"
        [void](Wait-E2eStep -Expected 'Workspace' -TimeoutSec 15 -What "Audit -> Workspace (cycle $cycle)")
        Wait-E2eSettled -TimeoutSec 10 -What "Workspace settle after Audit (cycle $cycle)" | Out-Null
        Assert-Alive "Audit -> Back to Workspace (cycle $cycle)"
        Capture-Checkpoint "cycle$cycle-final-workspace"

        Write-DriverLog 'churn-cycle-survived' @{ cycle = $cycle }
    }

    # --- cross-cutting invariant pack (ADR-038 D4) plus the trace-error scan ------
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
