<#
.SYNOPSIS
    E2E scenario: Back navigation between graph-bearing shell steps (port of
    tools/smoke-back-nav.ps1 onto the e2e driver; ADR-038 WP4, issue #243).

.DESCRIPTION
    Behavior-identical port of the proven smoke-back-nav journey: originally the
    reproduce-first harness for the Back-navigation WebView2 crash (#120 /
    ADR-024), it ALSO asserts viewport preservation (#122 / ADR-025). Drives the
    live app from the workspace (Ist) graph into Plan mode and back, and through
    the Plan->Gap round-trip, asserting (a) the process stays alive after every
    step swap (with the ~1.5 s delayed-crash settle before each check, so a
    delayed unhandled exception is attributed to the right step), and (b) the
    Chrome_RenderWidgetHostHWND is UNCHANGED across each Back into a parked step
    (proof the live page + cytoscape viewport survived the parking-lot reparent).

      PATH A: Design plan -> (confirm Plan step) -> Back        [primary crash path]
      PATH B: Design plan -> Gap analysis -> Back -> Back       [full round-trip]

    Step swaps are confirmed by PIXEL signals (UIA is Chromium-blind post-WebView):
    workspace = blue External frontier blob PRESENT; fresh Plan = blob ABSENT.

    %APPDATA% ui-state.json is backed up ON DISK (<file>.e2e-bak via the driver's
    Backup-OperatorState), forced to RailCollapsed=false (the "Design plan" button
    lives INSIDE the rail), and restored in finally - with leftover-recovery
    sweeps at scenario start and in run-e2e.ps1 covering watchdog kills that skip
    this finally. This is the proven-but-hazardous idiom ADR-038 D3.1 retires:
    once the --state-dir seam lands (WP5, #244), this block is replaced by a
    hermetic per-scenario state dir.

    Tier A input fidelity (ADR-038 D2): all actions are UIA + posted WM_*.
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
    $ArtifactDir = Join-Path $repoRoot ('artifacts\e2e\adhoc\back-nav-{0:yyyyMMdd-HHmmss}' -f (Get-Date))
}
if (-not $AppExe) {
    $AppExe = Join-Path $repoRoot 'src\App\bin\Release\net8.0-windows\GroupWeaver.App.exe'
}
# NOTE: $StateDir is accepted but UNUSED until the --state-dir seam lands (WP5,
# ADR-038 D3.1); see the %APPDATA% backup/restore idiom below.

. (Join-Path (Split-Path -Parent $PSScriptRoot) 'lib\e2e-driver.ps1')

Initialize-E2eContext -ScenarioName 'back-nav' -ArtifactDir $ArtifactDir
$runStart = Get-Date

# Proven render-confirmation root + rendered node color (see smoke-back-nav):
# the blue External frontier node is the never-occluded render signal; a fresh
# Plan step starts with an EMPTY canvas (no External blob).
$filterText = 'DL_FS-Finance_RW'
$colorExternalNode = @(49, 85, 115)

# --- %APPDATA% ui-state bracketing (WP5 retires this; see header) ---------------
# Backup/restore go through the driver's ON-DISK Backup-/Restore-OperatorState
# (<file>.e2e-bak): a runner watchdog kill skips this scenario's finally block,
# so an in-memory backup would die with the process and leave the operator's
# real ui-state.json clobbered. The on-disk backup survives the kill and is
# restored by the sweeps in Backup-OperatorState (scenario start) and
# run-e2e.ps1 (post-watchdog + end of run).
$script:uiStatePath = Join-Path ([Environment]::GetFolderPath('ApplicationData')) 'GroupWeaver\ui-state.json'
function Set-RailExpanded {
    Backup-OperatorState -Path $script:uiStatePath
    # Match UiState's shape (RailWidth/RailCollapsed); RailCollapsed=false = expanded.
    @{ RailWidth = 340; RailCollapsed = $false } | ConvertTo-Json | Set-Content -Encoding UTF8 $script:uiStatePath
    Write-DriverLog 'rail-forced-expanded' @{ path = $script:uiStatePath }
}

# --- step-swap pixel confirms (from smoke-back-nav, over the driver primitive) --
function Confirm-Workspace([string]$Where, [int]$TimeoutSec = 15) {
    $deadline = (Get-Date).AddSeconds($TimeoutSec)
    while ($true) {
        if (Test-CanvasBlob $colorExternalNode) {
            Write-DriverLog 'confirmed-workspace' @{ afterWhat = $Where }
            return
        }
        if ((Get-Date) -gt $deadline) { throw "ASSERT::step-swap confirm FAILED: workspace graph not rendered after '$Where' (no External node)" }
        Start-Sleep -Milliseconds 400
    }
}
function Confirm-Plan([string]$Where, [int]$TimeoutSec = 12) {
    $deadline = (Get-Date).AddSeconds($TimeoutSec)
    while ($true) {
        if (-not (Test-CanvasBlob $colorExternalNode)) {
            Write-DriverLog 'confirmed-plan' @{ afterWhat = $Where }
            return
        }
        if ((Get-Date) -gt $deadline) { throw "ASSERT::step-swap confirm FAILED: still on workspace after '$Where' (External node still present - click missed?)" }
        Start-Sleep -Milliseconds 400
    }
}

# Settle delay between a click and the next capture/click (lets the new view's
# WebView mount + layout settle); the Confirm-* helpers do the real verification.
function Wait-Step([string]$What, [int]$Ms = 1200) {
    Start-Sleep -Milliseconds $Ms
    Write-DriverLog 'settled' @{ afterWhat = $What }
}

Set-RailExpanded

$failed = $false
try {
    [void](Start-E2EApp -ExePath $AppExe)

    Invoke-DemoRootLoad -FilterText $filterText

    Wait-ChromiumChild -TimeoutSec 60
    [void](Wait-NodeBlob { Save-Probe } $colorExternalNode 30 'the external frontier node (render signal)')
    Write-DriverLog 'workspace-rendered' @{}
    Capture-Checkpoint 'ist'
    Assert-Alive 'initial workspace render'

    # #122 / ADR-025: record the workspace canvas's child HWND now (the SOLE
    # Chromium child - Plan/Gap not created yet). After the Plan round-trip it
    # must be UNCHANGED: the parked surface survived the Back.
    $wsHwnd0 = Get-VisibleChromiumHwnd
    Write-DriverLog 'baseline-hwnd' @{ step = 'workspace'; hwnd = "0x{0:X}" -f $wsHwnd0.ToInt64() }

    # --- button capture-coordinate map (read off checkpoint PNGs at the fixed
    #     60,60 + clamped-min geometry; UIA is blind to these post-WebView) ------
    # RECALIBRATED 2026-07-03 (WP4): the redesign moved the rail action rows, so
    # the coordinates smoke-back-nav pinned on 2026-06-23 are stale. Current map:
    # Workspace rail: "Design plan" is the first action button in the actions
    # grid below the Findings panel (01-ist.png: centred ~1067,560).
    $PT_DesignPlan = @(1067, 560)
    # PlanView editor column (right): under the "Plan" title, button row
    # [New plan] [Gap analysis] [Export script] at y~192, then
    # [<- Back to explore] on its own row at y~241 (02-plan.png).
    $PT_GapAnalysis = @(1095, 192)
    $PT_PlanBack = @(1003, 241)
    # GapView header row (right): [Export CSV] [Export HTML] [<- Back to plan],
    # Back rightmost (04-gap.png).
    $PT_GapBack = @(1405, 150)

    # =============================================================================
    # PATH A: Design plan -> (confirm Plan step) -> Back  [the primary crash path]
    # =============================================================================
    # NOTE on ordering: the confirmed #120 crash is a DELAYED unhandled exception on
    # the next render/layout pass after the Back swap (~1-1.5s later), NOT synchronous
    # with the click. So after every swap: WAIT for the new view to settle, THEN
    # Throw-IfCrashed (attributes a delayed crash to that exact step), THEN
    # Assert-Alive + capture + pixel-confirm.
    Write-DriverLog 'path-a-start' @{}
    Click-CapturePoint $PT_DesignPlan[0] $PT_DesignPlan[1] 'Design plan'
    Wait-Step 'Plan step mount (own WebView)' 1500
    Throw-IfCrashed 'Design plan -> Plan step'
    Assert-Alive 'Design plan -> Plan step'
    Capture-Checkpoint 'plan'
    Confirm-Plan 'Design plan'

    Click-CapturePoint $PT_PlanBack[0] $PT_PlanBack[1] '<- Back to explore (Plan->Workspace)'
    Wait-Step 'workspace WebView re-attach' 1800
    Throw-IfCrashed 'Plan -> Back (workspace)'   # <-- the #120 crash assertion
    Assert-Alive 'Plan -> Back (workspace)'
    Capture-Checkpoint 'back-to-ist'
    Confirm-Workspace 'Plan -> Back'
    # #122 / ADR-025: the Plan was abandoned+disposed on Back, so the workspace child
    # is the sole visible Chromium child again. Same handle = viewport preserved.
    Update-ChromiumHwnd
    Start-Sleep -Milliseconds 400
    Assert-SameHwnd $wsHwnd0 (Get-VisibleChromiumHwnd) 'Plan -> Back to Workspace'
    Write-DriverLog 'path-a-survived' @{}

    # =============================================================================
    # PATH B: Design plan -> Gap analysis -> Back -> Back  [the full round-trip]
    # =============================================================================
    Write-DriverLog 'path-b-start' @{}
    Click-CapturePoint $PT_DesignPlan[0] $PT_DesignPlan[1] 'Design plan (round 2)'
    Wait-Step 'Plan step mount (round 2)' 1500
    Throw-IfCrashed 'Design plan -> Plan step (round 2)'
    Assert-Alive 'Design plan -> Plan step (round 2)'
    Confirm-Plan 'Design plan (round 2)'
    # #122 / ADR-025: record the PLAN canvas's child HWND (workspace is parked+hidden,
    # so the largest VISIBLE child is the plan's). The Gap round-trip parks the plan
    # surface; Back must return the SAME handle.
    $planHwnd0 = Get-VisibleChromiumHwnd
    Write-DriverLog 'baseline-hwnd' @{ step = 'plan'; hwnd = "0x{0:X}" -f $planHwnd0.ToInt64() }

    Click-CapturePoint $PT_GapAnalysis[0] $PT_GapAnalysis[1] 'Gap analysis'
    Wait-Step 'gap WebView mount + RefreshAsync' 1800
    Throw-IfCrashed 'Plan -> Gap step'
    Assert-Alive 'Plan -> Gap step'
    Capture-Checkpoint 'gap'
    # Positive Gap-mount confirm (blind-spot closure): the blob probe CANNOT tell
    # Gap from Plan (both render no blue External blob), so a drifted/missed
    # 'Gap analysis' click would let PATH B pass vacuously (Confirm-Plan stays
    # true on Plan; Assert-SameHwnd is trivially true on the same child). A
    # FORWARD swap mounts a FRESH Chromium child for the entering step (ADR-024/
    # ADR-025 - only a Back restores a parked one), so the on-screen child HWND
    # CHANGING away from the plan's is the mount proof. Bounded poll; full
    # step-name state confirmation arrives with the WP5/WP6 stateProbe seam.
    $deadline = (Get-Date).AddSeconds(10)
    while ($true) {
        Update-ChromiumHwnd
        $gapHwnd = Get-VisibleChromiumHwnd
        if ($gapHwnd -ne [IntPtr]::Zero -and $gapHwnd -ne $planHwnd0) {
            Write-DriverLog 'confirmed-gap-mount' @{ planHwnd = "0x{0:X}" -f $planHwnd0.ToInt64(); gapHwnd = "0x{0:X}" -f $gapHwnd.ToInt64() }
            break
        }
        if ((Get-Date) -gt $deadline) {
            throw "ASSERT::gap-mount - visible Chromium child is still the Plan's ($planHwnd0): Gap step never mounted (click missed?)"
        }
        Start-Sleep -Milliseconds 400
    }

    Click-CapturePoint $PT_GapBack[0] $PT_GapBack[1] '<- Back to plan (Gap->Plan)'
    Wait-Step 'plan re-shown' 1500
    Throw-IfCrashed 'Gap -> Back (plan)'
    Assert-Alive 'Gap -> Back (plan)'
    Confirm-Plan 'Gap -> Back (plan)'
    # #122 / ADR-025: Gap was abandoned+disposed on Back; the plan surface was
    # unparked. Same plan child HWND across the Gap round-trip = viewport preserved.
    Update-ChromiumHwnd
    Start-Sleep -Milliseconds 400
    Assert-SameHwnd $planHwnd0 (Get-VisibleChromiumHwnd) 'Gap -> Back to Plan'

    Click-CapturePoint $PT_PlanBack[0] $PT_PlanBack[1] '<- Back to explore (Plan->Workspace, round 2)'
    Wait-Step 'workspace re-attach (round 2)' 1800
    Throw-IfCrashed 'Plan -> Back (workspace, round 2)'
    Assert-Alive 'Plan -> Back (workspace, round 2)'
    Capture-Checkpoint 'final-ist'
    Confirm-Workspace 'Plan -> Back (round 2)'
    Write-DriverLog 'path-b-survived' @{}

    # --- cross-cutting invariant pack (ADR-038 D4) -------------------------------
    Assert-NoUnexpectedDialogs 'end of journey'
    Invoke-CleanShutdownGate
    Assert-CleanStderr 'after shutdown'
    Assert-NoNewWerDumps -Since $runStart -Where 'end of scenario'

    Write-Result -Result pass -AppExitCode 0
}
catch {
    $failed = $true
    Complete-E2eFailure -ErrorRecord $_ -RunStart $runStart
}
finally {
    Stop-E2EAppForce
    Restore-OperatorState -Path $script:uiStatePath
}

if ($failed) { exit 1 } else { exit 0 }
