<#
.SYNOPSIS
    E2E scenario: the golden journey - connect, load, interact, plan, gap, audit,
    export, quit - over the --e2e channel (ADR-038 D3.2/WP6b, P0.1, issue #245).

.DESCRIPTION
    Superset scenario: drives the FULL step sequence in one run, composed almost
    entirely from patterns 6 prior WP6b scenarios already proved, rather than
    inventing anything new (the two genuine design forks below are the only new
    ground):

      1. Connect proof: '--demo --state-dir <dir> --e2e' (every other scenario's
         hermetic launch - '--e2e' alone exit-64s without '--demo', confirmed in
         StartupOptions.cs, and '--demo' auto-connects past the literal Connect
         card, so "real Connect-card click" and "observe over the channel" are
         mutually exclusive).

         DEVIATION found during implementation, verified on this box: the plan's
         proposed 'DemoConnected' trace EVENT is structurally unobservable for a
         '--demo' CLI launch, every time - not a flaky timing issue.
         DemoProvider.ConnectAsync() returns an ALREADY-COMPLETED
         Task.FromResult(...), so ConnectionViewModel.ConnectCoreAsync's 'await
         provider.ConnectAsync()' never yields: the ENTIRE chain
         (ShellViewModel's ctor -> ConnectDemoCommand.ExecuteAsync(null) ->
         OnConnected -> CurrentStep = new RootPickerViewModel(...), which fires
         StepChanged/DemoConnected) runs SYNCHRONOUSLY to completion inside
         'new ShellViewModel(...)' in App.axaml.cs's composition root - several
         lines before '_e2eChannel = new E2eChannel(shell, mainWindow)' exists to
         subscribe ShellViewModel.StepChanged. The event fires into zero
         listeners on every '--demo' launch (confirmed: Wait-E2eEvent times out
         at 30s, reproducibly - this is an ordering bug, not a budget one). A
         real fix belongs in the composition root/ShellViewModel wiring (a
         separate, reviewed App-side change with its own test-impact surface -
         out of scope for a scenario-only PR; flagged for a fast-follow, not
         silently worked around). This scenario instead uses a 'state'-POLL
         connect proof, Wait-E2eStep -Expected 'PickRoot', which is immune to
         the race BY CONSTRUCTION (state is read live at poll time off whatever
         CurrentStep already is, never off a historical trace event) and proves
         the identical fact the plan wanted: the '--demo' auto-connect reached
         the root picker, observed over the channel.
      2. Root load: Invoke-RootLoad 'AGDLP-Demo' (selection-sync.ps1 /
         audit-zero-drift.ps1's full-scope filter - a rich, non-trivial
         workspace; the 2-node DL_FS-Finance_RW scope other scenarios use would
         be too thin for a "journey").
      3. Interact: one canvas click (select), one wheel (zoom), one drag (pan) -
         breadth, not depth (selection/zoom/pan already have dedicated deep
         scenarios: selection-sync.ps1, keyboard-sweep.ps1, record-demo-gif.ps1).
         The click reuses selection-sync.ps1's zoom-in-then-hunt approach (six
         canvas zoom-in-overlay clicks before hunting a GlobalGroup-green blob -
         at Fit view the full 196-object scope renders nodes too small for
         Find-NodeBlob's 30px floor).
      4. Design plan -> Gap analysis -> back -> back: step-swap-churn.ps1's
         calibrated Plan/Gap/Back coordinates and Wait-E2eStep/Wait-E2eSettled
         gating, ONE cycle (not step-swap-churn's repeated churn - that depth is
         its own scenario's job).
      5. Audit -> scroll to bottom -> Save audit run -> confirm exactly one run
         file: audit-run-persist.ps1's calibrated Audit/SaveRun/AuditWheel
         coordinates and save-and-confirm-file logic verbatim. THIS is the
         "export" proof (design fork #2 below).
      6. Quit: Send-E2eQuit + a bounded WaitForExit (design fork #3 below - the
         one genuinely new piece).

    DESIGN FORK - export proof: native CSV/HTML/script export all route through
    StorageProviderExportFileDialogs -> an OS common Save dialog, and nothing in
    the driver or lab-environment.md establishes whether that is drivable under
    "no interactive input desktop" on this box. Decision (ADR-038 WP6b plan,
    Order 7): use "Save audit run" (the internal store, no OS picker, already
    proven end to end by audit-run-persist.ps1) as the journey's export proof.
    Driving the native Save dialog is a separate, out-of-scope investigation.

    DESIGN FORK - graceful quit: every other scenario's clean-shutdown gate
    (Invoke-CleanShutdownGate) drives Stop-E2EApp's CloseMainWindow() (posts
    WM_CLOSE to the top-level window). This is the ONE scenario that instead
    exercises the --e2e channel's OWN graceful-close command
    ({"cmd":"quit"} -> Dispatcher.UIThread.Post(() => _window.Close()),
    E2eChannel.cs) end to end - proving the channel's close path works, not just
    WM_CLOSE. Invoke-GracefulQuitShutdownGate (below, scenario-local: this is
    the only scenario that needs it) mirrors Invoke-CleanShutdownGate's exact
    assertions (graceful exit within the grace window, exit code 0) but
    triggers via Send-E2eQuit instead of CloseMainWindow - equivalent
    clean-shutdown verification, a different mechanism under test.

    State is HERMETIC (ADR-038 D3.1, WP5) AND channelled (ADR-038 D3.2, WP6):
    '--demo --state-dir <dir> --e2e'. Tier B (ADR-038 D2): actions are real
    UIA/posted WM_* input (Tier A) - the channel only GATES/ASSERTS via
    read-only 'state' polls (plus this scenario's one 'quit' command), never
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
    $ArtifactDir = Join-Path $repoRoot ('artifacts\e2e\adhoc\golden-journey-{0:yyyyMMdd-HHmmss}' -f (Get-Date))
}
if (-not $AppExe) {
    $AppExe = Join-Path $repoRoot 'src\App\bin\Release\net8.0-windows\GroupWeaver.App.exe'
}
if (-not $StateDir) {
    $StateDir = Join-Path $env:TEMP ('gw-e2e\adhoc\golden-journey-{0:yyyyMMdd-HHmmss}' -f (Get-Date))
}

. (Join-Path (Split-Path -Parent $PSScriptRoot) 'lib\e2e-driver.ps1')

Initialize-E2eContext -ScenarioName 'golden-journey' -ArtifactDir $ArtifactDir
$runStart = Get-Date

# The FULL demo root (selection-sync.ps1 / audit-zero-drift.ps1's filter) - a
# rich, non-trivial workspace for a journey that touches Plan/Gap/Audit.
$filterText = 'AGDLP-Demo'

# GlobalGroup node palette (src/App/web/graph.js node[kind='GlobalGroup']): green
# triangle #107C10 (selection-sync.ps1).
$colorGlobalGroup = @(16, 124, 16)
# Kind-consistency pattern for the click's resulting selection (any GG_* group
# under the demo Groups OU) - selection-sync.ps1's forward-click pattern.
$forwardDnPattern = '^CN=GG_[^,]+,OU=Groups,OU=AGDLP-Demo,DC=weavedemo,DC=example$'

# Hermetic state (ADR-038 D3.1, WP5): deterministic ui-state.json (rail expanded +
# dark theme) into the scenario's OWN state dir - never the real %APPDATA%.
[void](Initialize-E2eStateDir -StateDir $StateDir)

# --- calibrated coordinate map (lifted verbatim from the scenarios named) -------
# Canvas "+" zoom-in overlay button + violations-adjacent geometry: selection-sync.ps1.
$PT_ZoomIn = @(606, 843)
# Workspace/Plan/Gap step-swap map: step-swap-churn.ps1 (itself reusing back-nav.ps1).
$PT_DesignPlan = @(1067, 560)
$PT_GapAnalysis = @(1095, 192)
$PT_PlanBack = @(1003, 241)
$PT_GapBack = @(1405, 150)
# Workspace -> Audit + Save-audit-run map: audit-run-persist.ps1 / audit-zero-drift.ps1.
$PT_Audit = @(1390, 621)
$PT_SaveRun = @(155, 785)
$PT_AuditWheel = @(700, 450)

# --- graceful-quit shutdown gate (NEW - the design fork this scenario alone tests) --
# Mirrors Invoke-CleanShutdownGate's exact assertions (graceful exit within the grace
# window, exit code 0) but triggers the exit via the --e2e channel's OWN 'quit'
# command (Send-E2eQuit -> {"cmd":"quit"} -> Dispatcher.UIThread.Post(() =>
# _window.Close()), E2eChannel.cs) instead of Stop-E2EApp's CloseMainWindow()
# (posted WM_CLOSE) - proving the channel's close path end to end, which no other
# scenario exercises. Scenario-local (not promoted to e2e-driver.ps1): this is the
# ONE scenario in the suite meant to drive this path.
function Invoke-GracefulQuitShutdownGate {
    param([int]$GraceSeconds = 5)
    Send-E2eQuit
    $graceful = $script:app.WaitForExit($GraceSeconds * 1000)
    if (-not $graceful) {
        Write-DriverLog 'graceful-quit-timeout' @{ graceSeconds = $GraceSeconds }
        try { $script:app.Kill() } catch { }
        [void]$script:app.WaitForExit(5000)
        throw "ASSERT::clean-shutdown - app did not exit within ${GraceSeconds}s of Send-E2eQuit (killed)"
    }
    $script:app.Refresh()
    $exitCode = $script:app.ExitCode
    Write-DriverLog 'graceful-quit-shutdown-gate' @{ exitCode = $exitCode }
    if ($exitCode -ne 0) {
        throw "ASSERT::clean-shutdown - app exited code $exitCode after Send-E2eQuit, expected 0"
    }
}

$failed = $false
try {
    # --- 1. connect (hermetic launch + DemoConnected trace event) -----------------
    [void](Start-E2EApp -ExePath $AppExe -AppArgs @('--demo') -StateDir $StateDir -E2e)
    Assert-Alive 'launch (window up)'

    # 'state'-poll proof, NOT Wait-E2eEvent 'DemoConnected' - see the DEVIATION note
    # in the header comment (the event is structurally unobservable for a --demo
    # CLI launch; a state poll is immune to that race by construction).
    [void](Wait-E2eStep -Expected 'PickRoot' -TimeoutSec 30 -What 'demo auto-connect (root picker reached)')
    Write-DriverLog 'connect-confirmed' @{}

    # --- 2. pick root + load -------------------------------------------------------
    Invoke-RootLoad -FilterText $filterText
    Assert-Alive 'demo connect + full-root load'

    Wait-ChromiumChild -TimeoutSec 60
    Write-DriverLog 'workspace-rendered' @{}
    Capture-Checkpoint 'workspace'
    Assert-Alive 'workspace render'
    [void](Wait-E2eStep -Expected 'Workspace' -TimeoutSec 30 -What 'initial workspace state')
    Wait-E2eSettled -TimeoutSec 30 -What 'initial workspace settle' | Out-Null

    # --- 3. interact: click (select) + wheel (zoom) + drag (pan) -------------------
    # Zoom-in overlay clicks first (selection-sync.ps1): at the initial "Fit" view the
    # full 196-object AGDLP-Demo scope renders nodes too small for Find-NodeBlob's
    # 30-pixel densest-cell floor. Six clicks reliably grow the cluster enough.
    for ($i = 0; $i -lt 6; $i++) {
        Send-CanvasClick $PT_ZoomIn[0] $PT_ZoomIn[1] $false
        Start-Sleep -Milliseconds 200
    }
    Throw-IfCrashed 'canvas zoom-in (interact setup)'
    Capture-Checkpoint 'zoomed-in'

    # minX skips the KINDS legend's own green triangle swatch (same always-present
    # false-match class selection-sync.ps1 / record-demo-gif.ps1 guard against, #78).
    $ggBlob = Wait-NodeBlob { Save-Probe } $colorGlobalGroup 30 'a GlobalGroup node (interact click target)' 'canvas' 450

    # -- click: select a node --
    Send-CanvasClick $ggBlob.X $ggBlob.Y $false
    Throw-IfCrashed 'canvas click (select)'
    $afterClick = Wait-E2eState -TimeoutSec 10 -What 'post-click selection state'
    if (-not $afterClick.selectedDn -or $afterClick.selectedDn -notmatch $forwardDnPattern) {
        throw "ASSERT::interact-click - selectedDn '$($afterClick.selectedDn)' does not match the expected GG_* pattern after a canvas click"
    }
    Wait-E2eSettled -TimeoutSec 10 -What 'post-click settle' | Out-Null
    Write-DriverLog 'interact-click-confirmed' @{ selectedDn = "$($afterClick.selectedDn)" }
    Capture-Checkpoint 'interact-selected'

    # -- wheel: zoom --
    Send-CanvasWheel 4
    Throw-IfCrashed 'canvas wheel (zoom)'
    $afterWheel = Wait-E2eSettled -TimeoutSec 10 -What 'post-wheel settle'
    Assert-Alive 'canvas wheel (zoom)'
    Write-DriverLog 'interact-wheel-confirmed' @{ zoom = "$($afterWheel.zoom)" }
    Capture-Checkpoint 'interact-zoomed'

    # -- drag: pan (empty-background start point - clear of the zoomed node cluster
    #    and the bottom overlay toolbar; even a mis-hit just drags a node instead of
    #    panning, which stays a valid "app stays alive and responsive" proof too) --
    Send-CanvasDrag 500 250 650 350
    Throw-IfCrashed 'canvas drag (pan)'
    $afterDrag = Wait-E2eSettled -TimeoutSec 10 -What 'post-drag settle'
    Assert-Alive 'canvas drag (pan)'
    Write-DriverLog 'interact-drag-confirmed' @{ panX = "$($afterDrag.panX)"; panY = "$($afterDrag.panY)" }
    Capture-Checkpoint 'interact-panned'

    # --- 4. design plan -> gap analysis -> back -> back (step-swap-churn.ps1, 1 cycle)
    Click-CapturePoint $PT_DesignPlan[0] $PT_DesignPlan[1] 'Design plan'
    Throw-IfCrashed 'Workspace -> Plan step'
    [void](Wait-E2eStep -Expected 'Plan' -TimeoutSec 15 -What 'Workspace -> Plan')
    Wait-E2eSettled -TimeoutSec 10 -What 'Plan settle' | Out-Null
    Assert-Alive 'Workspace -> Plan step'
    Capture-Checkpoint 'plan'

    Click-CapturePoint $PT_GapAnalysis[0] $PT_GapAnalysis[1] 'Gap analysis'
    Throw-IfCrashed 'Plan -> Gap step'
    [void](Wait-E2eStep -Expected 'Gap' -TimeoutSec 15 -What 'Plan -> Gap')
    Wait-E2eSettled -TimeoutSec 10 -What 'Gap settle' | Out-Null
    Assert-Alive 'Plan -> Gap step'
    Capture-Checkpoint 'gap'

    Click-CapturePoint $PT_GapBack[0] $PT_GapBack[1] '<- Back to plan'
    Throw-IfCrashed 'Gap -> Back to Plan'
    [void](Wait-E2eStep -Expected 'Plan' -TimeoutSec 15 -What 'Gap -> Plan')
    Wait-E2eSettled -TimeoutSec 10 -What 'Plan re-settle' | Out-Null
    Assert-Alive 'Gap -> Back to Plan'

    Click-CapturePoint $PT_PlanBack[0] $PT_PlanBack[1] '<- Back to explore'
    Throw-IfCrashed 'Plan -> Back to Workspace'
    [void](Wait-E2eStep -Expected 'Workspace' -TimeoutSec 15 -What 'Plan -> Workspace')
    Wait-E2eSettled -TimeoutSec 10 -What 'Workspace re-settle' | Out-Null
    Assert-Alive 'Plan -> Back to Workspace'
    Capture-Checkpoint 'back-to-workspace'

    # --- 5. audit -> scroll -> Save audit run -> confirm one run file --------------
    # (audit-run-persist.ps1's flow/coordinates verbatim - this is the export proof)
    Click-CapturePoint $PT_Audit[0] $PT_Audit[1] 'Audit'
    Throw-IfCrashed 'Workspace -> Audit step'
    [void](Wait-E2eStep -Expected 'Audit' -TimeoutSec 15 -What 'Workspace -> Audit')
    Assert-Alive 'Workspace -> Audit step'
    Capture-Checkpoint 'audit-top'

    # Over-scroll on purpose: the ScrollViewer clamps at the end, pinning the page
    # bottom-aligned so the Save button's capture coordinates are deterministic.
    Send-MainWindowWheel -Ticks -40 -CaptureX $PT_AuditWheel[0] -CaptureY $PT_AuditWheel[1]
    Start-Sleep -Milliseconds 600
    Assert-Alive 'audit page scrolled'
    Capture-Checkpoint 'audit-bottom'

    $runsDir = Join-Path $StateDir 'GroupWeaver\runs'
    if (Test-Path $runsDir) {
        $stale = @(Get-ChildItem $runsDir -Filter '*.json' -File)
        if ($stale.Count -gt 0) {
            throw "ASSERT::state-dir - runs dir not empty BEFORE the save ($($stale.Count) file(s)): stale scenario state"
        }
    }
    Click-CapturePoint $PT_SaveRun[0] $PT_SaveRun[1] 'Save audit run'
    Throw-IfCrashed 'Save audit run'

    $deadline = (Get-Date).AddSeconds(10)
    $runFiles = @()
    while ($true) {
        if (Test-Path $runsDir) {
            $runFiles = @(Get-ChildItem $runsDir -Filter '*.json' -File)
            if ($runFiles.Count -gt 0) { break }
        }
        if ((Get-Date) -gt $deadline) {
            throw "ASSERT::run-file - no runs\*.json appeared under '$runsDir' within 10s of Save (click missed or store mis-based?)"
        }
        Start-Sleep -Milliseconds 400
    }
    Assert-Alive 'save audit run'
    Capture-Checkpoint 'run-saved'
    if ($runFiles.Count -ne 1) {
        throw "ASSERT::run-file - expected exactly 1 run file, found $($runFiles.Count): $(($runFiles | ForEach-Object { $_.Name }) -join ', ')"
    }

    $runJson = $null
    try {
        $runJson = Get-Content $runFiles[0].FullName -Raw | ConvertFrom-Json
    }
    catch {
        throw "ASSERT::run-file - '$($runFiles[0].Name)' did not parse as JSON: $($_.Exception.Message)"
    }
    if ([int]$runJson.schemaVersion -ne 1) {
        throw "ASSERT::run-file - schemaVersion is '$($runJson.schemaVersion)', expected 1"
    }
    if ([string]$runJson.rootDn -notlike "*$filterText*") {
        throw "ASSERT::run-file - rootDn '$($runJson.rootDn)' does not name the loaded root '$filterText'"
    }
    $findingsProp = $runJson.PSObject.Properties['findings']
    if (-not $findingsProp -or $null -eq $findingsProp.Value) {
        throw 'ASSERT::run-file - the findings array is missing'
    }
    $findings = @($findingsProp.Value)
    Write-DriverLog 'run-file-verified' @{
        file = $runFiles[0].Name; schemaVersion = 1; findings = $findings.Count
    }

    # --- 6. finish: graceful quit over the channel + the invariant pack ------------
    Assert-NoUnexpectedDialogs 'end of journey'
    Invoke-GracefulQuitShutdownGate
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
