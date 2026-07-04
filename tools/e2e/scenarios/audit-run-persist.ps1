<#
.SYNOPSIS
    E2E scenario: audit-run persistence through the hermetic --state-dir seam
    (ADR-038 D3.1/WP5, issue #244).

.DESCRIPTION
    Proves the WP5 state seam end to end: launch demo on '--demo --state-dir
    <dir>' -> root load -> switch to the Audit step (rail action row) -> scroll
    the audit page to its run-history card -> click "Save audit run" -> assert
    exactly ONE runs\*.json appeared IN THE SCENARIO STATE DIR (never the real
    %APPDATA%), parses as JSON, and carries the ADR-032 fields (schemaVersion,
    rootDn, findings array - spot-pinned, not over-pinned) -> clean shutdown.

    The operator's real %APPDATA%\GroupWeaver is asserted UNTOUCHED: ui-state.json
    is hashed before/after (when it exists) and the real runs\ directory must gain
    no file. State is fully hermetic: a deterministic ui-state.json (rail expanded,
    dark theme) is pre-seeded into the scenario state dir; app stores, WebView2
    profile and log sink all live under that dir (driver env seam).

    Step-swap confirmation: the Audit step owns NO graph surface (AuditView has no
    GraphHost), so the workspace WebView gets parked+hidden on the swap - 'no
    VISIBLE Chromium child' is the audit-mount signal; the saved run FILE is the
    Save-click confirmation (the task-level truth).

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
    $ArtifactDir = Join-Path $repoRoot ('artifacts\e2e\adhoc\audit-run-persist-{0:yyyyMMdd-HHmmss}' -f (Get-Date))
}
if (-not $AppExe) {
    $AppExe = Join-Path $repoRoot 'src\App\bin\Release\net8.0-windows\GroupWeaver.App.exe'
}
if (-not $StateDir) {
    $StateDir = Join-Path $env:TEMP ('gw-e2e\adhoc\audit-run-persist-{0:yyyyMMdd-HHmmss}' -f (Get-Date))
}

. (Join-Path (Split-Path -Parent $PSScriptRoot) 'lib\e2e-driver.ps1')

Initialize-E2eContext -ScenarioName 'audit-run-persist' -ArtifactDir $ArtifactDir
$runStart = Get-Date

# Proven render-confirmation root + rendered node color (see launch-render):
# the blue External frontier node of the 2-node DL_FS-Finance_RW scope.
$filterText = 'DL_FS-Finance_RW'
$colorExternalNode = @(49, 85, 115)

# Hermetic state (ADR-038 D3.1, WP5): deterministic ui-state.json (rail expanded +
# dark theme) into the scenario's OWN state dir - never the real %APPDATA%.
[void](Initialize-E2eStateDir -StateDir $StateDir)

# --- real-%APPDATA% untouched baseline (the seam's whole point) -----------------
# SHA-256 via .NET, NOT Get-FileHash: under the pwsh-7 runner the 5.1 child inherits
# pwsh's PSModulePath, so Microsoft.PowerShell.Utility's SCRIPT functions (Get-FileHash)
# fail to auto-load even though its compiled cmdlets (ConvertTo-Json) keep working.
function Get-FileSha256([string]$Path) {
    $sha = [System.Security.Cryptography.SHA256]::Create()
    try {
        $stream = [IO.File]::OpenRead($Path)
        try { return ([BitConverter]::ToString($sha.ComputeHash($stream)) -replace '-', '') }
        finally { $stream.Dispose() }
    }
    finally { $sha.Dispose() }
}

$realGwDir = Join-Path ([Environment]::GetFolderPath('ApplicationData')) 'GroupWeaver'
$realUiStatePath = Join-Path $realGwDir 'ui-state.json'
$realUiStateHashBefore = $null
if (Test-Path $realUiStatePath) {
    $realUiStateHashBefore = Get-FileSha256 $realUiStatePath
}
$realRunsDir = Join-Path $realGwDir 'runs'
$realRunsBefore = @()
if (Test-Path $realRunsDir) {
    $realRunsBefore = @(Get-ChildItem $realRunsDir -Filter '*.json' -File | Select-Object -ExpandProperty Name)
}
Write-DriverLog 'real-appdata-baseline' @{
    uiStateHash = "$realUiStateHashBefore"; runsCount = $realRunsBefore.Count
}

$failed = $false
try {
    # --demo is MANDATORY with --state-dir (the app-side demo gate); the startup
    # auto-connect lands directly on the root picker, so no 'Demo mode' click.
    [void](Start-E2EApp -ExePath $AppExe -AppArgs @('--demo') -StateDir $StateDir)
    Assert-Alive 'launch (window up)'

    Invoke-RootLoad -FilterText $filterText
    Assert-Alive 'demo connect + root load'

    Wait-ChromiumChild -TimeoutSec 60
    [void](Wait-NodeBlob { Save-Probe } $colorExternalNode 30 'the external frontier node (render signal)')
    Write-DriverLog 'workspace-rendered' @{}
    Capture-Checkpoint 'workspace'
    Assert-Alive 'workspace render'

    # --- button capture-coordinate map (read off checkpoint PNGs at the fixed
    #     60,60 + clamped-min geometry; UIA is blind to these post-WebView) ------
    # Workspace rail action row (below the Findings panel; same grid back-nav's
    # "Design plan" lives in): "Audit" is the LAST action button (accent outline
    # with the shield glyph), second row of the wrapped action grid (01-workspace.png).
    $PT_Audit = @(1390, 621)
    # AuditView after scroll-to-bottom (03-audit-bottom.png): the run-history card's
    # button row, "Save audit run" leftmost.
    $PT_SaveRun = @(155, 785)
    # Wheel aim point for the audit page's outer ScrollViewer: page center; nested
    # scrollables chain to the outer ScrollViewer once exhausted.
    $PT_AuditWheel = @(700, 450)

    # --- switch to the Audit step -------------------------------------------------
    Click-CapturePoint $PT_Audit[0] $PT_Audit[1] 'Audit'
    Start-Sleep -Milliseconds 1200
    Throw-IfCrashed 'Workspace -> Audit step'
    Assert-Alive 'Workspace -> Audit step'

    # Audit-mount confirm: AuditView owns no graph surface, so the workspace WebView
    # is parked+hidden by the swap - no VISIBLE Chromium child remains (bounded poll).
    $deadline = (Get-Date).AddSeconds(10)
    while ($true) {
        if ((Get-VisibleChromiumHwnd) -eq [IntPtr]::Zero) {
            Write-DriverLog 'confirmed-audit-mount' @{}
            break
        }
        if ((Get-Date) -gt $deadline) {
            throw 'ASSERT::audit-mount - a visible Chromium child persists: Audit step never mounted (click missed?)'
        }
        Start-Sleep -Milliseconds 400
    }
    Capture-Checkpoint 'audit-top'

    # --- scroll the audit page to its bottom (run-history card + actions) ---------
    # Over-scroll on purpose: the ScrollViewer clamps at the end, pinning the page
    # bottom-aligned so the Save button's capture coordinates are deterministic.
    Send-MainWindowWheel -Ticks -40 -CaptureX $PT_AuditWheel[0] -CaptureY $PT_AuditWheel[1]
    Start-Sleep -Milliseconds 600
    Assert-Alive 'audit page scrolled'
    Capture-Checkpoint 'audit-bottom'

    # --- Save audit run ------------------------------------------------------------
    $runsDir = Join-Path $StateDir 'GroupWeaver\runs'
    if (Test-Path $runsDir) {
        $stale = @(Get-ChildItem $runsDir -Filter '*.json' -File)
        if ($stale.Count -gt 0) {
            throw "ASSERT::state-dir - runs dir not empty BEFORE the save ($($stale.Count) file(s)): stale scenario state"
        }
    }
    Click-CapturePoint $PT_SaveRun[0] $PT_SaveRun[1] 'Save audit run'
    Throw-IfCrashed 'Save audit run'

    # The saved FILE is the click confirmation: exactly one runs\*.json in the
    # SCENARIO state dir (bounded poll; the store's write is atomic temp+move).
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

    # --- spot-pin the ADR-032 shape (schemaVersion / rootDn / findings array) ------
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

    # --- the real %APPDATA% must be untouched (the seam's contract) ----------------
    $realUiStateHashAfter = $null
    if (Test-Path $realUiStatePath) {
        $realUiStateHashAfter = Get-FileSha256 $realUiStatePath
    }
    if ("$realUiStateHashBefore" -ne "$realUiStateHashAfter") {
        throw "ASSERT::real-appdata - ui-state.json changed (hash '$realUiStateHashBefore' -> '$realUiStateHashAfter'): the state seam leaked"
    }
    $realRunsAfter = @()
    if (Test-Path $realRunsDir) {
        $realRunsAfter = @(Get-ChildItem $realRunsDir -Filter '*.json' -File | Select-Object -ExpandProperty Name)
    }
    $leaked = @($realRunsAfter | Where-Object { $realRunsBefore -notcontains $_ })
    if ($leaked.Count -gt 0) {
        throw "ASSERT::real-appdata - new run file(s) in the REAL runs dir: $($leaked -join ', '): the state seam leaked"
    }
    Write-DriverLog 'real-appdata-untouched' @{ uiStateHash = "$realUiStateHashAfter"; runsCount = $realRunsAfter.Count }

    # --- cross-cutting invariant pack (ADR-038 D4) ---------------------------------
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
}

if ($failed) { exit 1 } else { exit 0 }
