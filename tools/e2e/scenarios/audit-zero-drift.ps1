<#
.SYNOPSIS
    E2E scenario: audit zero-drift determinism through two same-snapshot runs
    (ADR-038 D3.2/WP6b, P1.2, issue #245; product contract: ADR-032).

.DESCRIPTION
    Proves the ADR-032 determinism contract end to end: running the audit TWICE
    against the same unmutated snapshot/ruleset produces identical findings
    (order + content) and the same ruleset hash - the only field allowed to
    differ between the two saved runs is Timestamp.

    Near-total reuse of audit-run-persist.ps1's flow (rail -> Audit step ->
    scroll to bottom -> "Save audit run"), run TWICE with a deliberate
    Start-Sleep -Seconds 2 between clicks: AuditRunStore.Save's file name is
    second-granularity ({Timestamp:yyyyMMdd'T'HHmmss'Z'}-{slug}.json), so two
    saves within the same second would silently overwrite instead of producing
    two files to compare - the sleep is real wall-clock time, not something the
    --e2e channel can substitute for.

    Scope: the FULL demo root ('AGDLP-Demo'), not the 2-node DL_FS-Finance_RW
    scope the other scenarios use - the AP 3.2 baseline's 19 findings make the
    "the findings arrays are identical" proof actually meaningful (a 0/1-finding
    scope would pass this scenario vacuously). Since this scenario never touches
    the canvas, it skips the node-color-blob render hunt selection-sync.ps1 needs
    for its forward-click target (which itself needs six zoom-in clicks to grow
    the full 196-object scope's nodes past Find-NodeBlob's floor) - a Chromium
    child appearing plus a channel-confirmed 'Workspace' step/settle is a
    sufficient, and more robust, "did it actually load" gate here.

    State is HERMETIC (ADR-038 D3.1, WP5) AND channelled (ADR-038 D3.2, WP6):
    '--demo --state-dir <dir> --e2e'. Tier B (ADR-038 D2): actions are real
    UIA/posted WM_* input (Tier A) - the channel only GATES/ASSERTS via
    read-only 'state' polls, never invokes/clicks.

    The operator's real %APPDATA%\GroupWeaver is asserted UNTOUCHED (same idiom
    as audit-run-persist.ps1: SHA-256 via .NET, never Get-FileHash, per the
    PS-5.1-PSModulePath gotcha).

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
    $ArtifactDir = Join-Path $repoRoot ('artifacts\e2e\adhoc\audit-zero-drift-{0:yyyyMMdd-HHmmss}' -f (Get-Date))
}
if (-not $AppExe) {
    $AppExe = Join-Path $repoRoot 'src\App\bin\Release\net8.0-windows\GroupWeaver.App.exe'
}
if (-not $StateDir) {
    $StateDir = Join-Path $env:TEMP ('gw-e2e\adhoc\audit-zero-drift-{0:yyyyMMdd-HHmmss}' -f (Get-Date))
}

. (Join-Path (Split-Path -Parent $PSScriptRoot) 'lib\e2e-driver.ps1')

Initialize-E2eContext -ScenarioName 'audit-zero-drift' -ArtifactDir $ArtifactDir
$runStart = Get-Date

# The FULL demo root (not the 2-node DL_FS-Finance_RW scope) - the AP 3.2
# baseline's 19 findings give the two-run comparison real content to diff.
$filterText = 'AGDLP-Demo'

# Hermetic state (ADR-038 D3.1, WP5): deterministic ui-state.json (rail expanded +
# dark theme) into the scenario's OWN state dir - never the real %APPDATA%.
[void](Initialize-E2eStateDir -StateDir $StateDir)

# --- real-%APPDATA% untouched baseline (same idiom as audit-run-persist.ps1) -----
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

# Deep, field-by-field comparison of two AuditRunFinding[] projections (AuditRun.cs:
# ruleId/severity/primaryDn/dns/message) - same order, same values. Throws with a
# precise diagnostic on the first mismatch; never a bare Count check.
function Assert-FindingsIdentical($ExpectedFindings, $ActualFindings, [string]$What) {
    $expected = @($ExpectedFindings)
    $actual = @($ActualFindings)
    if ($expected.Count -ne $actual.Count) {
        throw "ASSERT::zero-drift - $What findings count differs: $($expected.Count) vs $($actual.Count)"
    }
    for ($i = 0; $i -lt $expected.Count; $i++) {
        $e = $expected[$i]
        $a = $actual[$i]
        if ("$($e.ruleId)" -ne "$($a.ruleId)") {
            throw "ASSERT::zero-drift - $What finding[$i].ruleId differs: '$($e.ruleId)' vs '$($a.ruleId)'"
        }
        if ("$($e.severity)" -ne "$($a.severity)") {
            throw "ASSERT::zero-drift - $What finding[$i].severity differs: '$($e.severity)' vs '$($a.severity)'"
        }
        if ("$($e.primaryDn)" -ne "$($a.primaryDn)") {
            throw "ASSERT::zero-drift - $What finding[$i].primaryDn differs: '$($e.primaryDn)' vs '$($a.primaryDn)'"
        }
        if ("$($e.message)" -ne "$($a.message)") {
            throw "ASSERT::zero-drift - $What finding[$i].message differs: '$($e.message)' vs '$($a.message)'"
        }
        $eDns = @($e.dns)
        $aDns = @($a.dns)
        if ($eDns.Count -ne $aDns.Count) {
            throw "ASSERT::zero-drift - $What finding[$i].dns count differs: $($eDns.Count) vs $($aDns.Count)"
        }
        for ($j = 0; $j -lt $eDns.Count; $j++) {
            if ("$($eDns[$j])" -ne "$($aDns[$j])") {
                throw "ASSERT::zero-drift - $What finding[$i].dns[$j] differs: '$($eDns[$j])' vs '$($aDns[$j])'"
            }
        }
    }
}

$failed = $false
try {
    # --demo is MANDATORY with --state-dir/--e2e (both app-side demo gates); the
    # startup auto-connect lands directly on the root picker.
    [void](Start-E2EApp -ExePath $AppExe -AppArgs @('--demo') -StateDir $StateDir -E2e)
    Assert-Alive 'launch (window up)'

    Invoke-RootLoad -FilterText $filterText
    Assert-Alive 'demo connect + full-root load'

    # Render + channel liveness gate: a Chromium child plus a channel-confirmed
    # 'Workspace' step/settle is sufficient here - no canvas interaction follows,
    # so no node-color-blob hunt (and its zoom-in calibration) is needed.
    Wait-ChromiumChild -TimeoutSec 60
    Write-DriverLog 'workspace-rendered' @{}
    Capture-Checkpoint 'workspace'
    Assert-Alive 'workspace render'
    [void](Wait-E2eStep -Expected 'Workspace' -TimeoutSec 30 -What 'initial workspace state')
    Wait-E2eSettled -TimeoutSec 30 -What 'initial workspace settle' | Out-Null

    # --- button capture-coordinate map (audit-run-persist.ps1's calibrated map) ----
    $PT_Audit = @(1390, 621)
    $PT_SaveRun = @(155, 785)
    $PT_AuditWheel = @(700, 450)

    # --- switch to the Audit step (channel-confirmed, no pixel/HWND heuristic) -----
    Click-CapturePoint $PT_Audit[0] $PT_Audit[1] 'Audit'
    Throw-IfCrashed 'Workspace -> Audit step'
    [void](Wait-E2eStep -Expected 'Audit' -TimeoutSec 15 -What 'Workspace -> Audit')
    Assert-Alive 'Workspace -> Audit step'
    Capture-Checkpoint 'audit-top'

    # --- scroll the audit page to its bottom (run-history card + actions) ---------
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
            throw "ASSERT::state-dir - runs dir not empty BEFORE the first save ($($stale.Count) file(s)): stale scenario state"
        }
    }

    # --- Save audit run #1 ----------------------------------------------------------
    Click-CapturePoint $PT_SaveRun[0] $PT_SaveRun[1] 'Save audit run (run 1)'
    Throw-IfCrashed 'Save audit run (run 1)'

    $deadline = (Get-Date).AddSeconds(10)
    $run1File = $null
    while ($true) {
        if (Test-Path $runsDir) {
            $files = @(Get-ChildItem $runsDir -Filter '*.json' -File)
            if ($files.Count -eq 1) { $run1File = $files[0]; break }
            if ($files.Count -gt 1) {
                throw "ASSERT::run-file - expected exactly 1 run file after the FIRST save, found $($files.Count)"
            }
        }
        if ((Get-Date) -gt $deadline) {
            throw "ASSERT::run-file - no runs\*.json appeared under '$runsDir' within 10s of the first Save (click missed?)"
        }
        Start-Sleep -Milliseconds 400
    }
    Assert-Alive 'save audit run (run 1)'
    Capture-Checkpoint 'run1-saved'
    Write-DriverLog 'run1-saved' @{ file = $run1File.Name }

    # AuditRunStore.Save's file name is second-granularity
    # ({Timestamp:yyyyMMdd'T'HHmmss'Z'}-{slug}.json) - two saves within the same
    # second would silently overwrite. Real wall-clock time; the --e2e channel has
    # no substitute for this (it observes app state, not filesystem clock ticks).
    Start-Sleep -Seconds 2

    # --- Save audit run #2 (same unmutated snapshot/ruleset) ------------------------
    Click-CapturePoint $PT_SaveRun[0] $PT_SaveRun[1] 'Save audit run (run 2)'
    Throw-IfCrashed 'Save audit run (run 2)'

    $deadline = (Get-Date).AddSeconds(10)
    $run2File = $null
    while ($true) {
        if (Test-Path $runsDir) {
            $files = @(Get-ChildItem $runsDir -Filter '*.json' -File)
            if ($files.Count -eq 2) {
                $newOnes = @($files | Where-Object { $_.Name -ne $run1File.Name })
                if ($newOnes.Count -eq 1) { $run2File = $newOnes[0]; break }
            }
            if ($files.Count -gt 2) {
                throw "ASSERT::run-file - expected exactly 2 run files after the SECOND save, found $($files.Count)"
            }
        }
        if ((Get-Date) -gt $deadline) {
            throw "ASSERT::run-file - no SECOND runs\*.json appeared under '$runsDir' within 10s of the second Save (same-second overwrite or click missed?)"
        }
        Start-Sleep -Milliseconds 400
    }
    Assert-Alive 'save audit run (run 2)'
    Capture-Checkpoint 'run2-saved'
    Write-DriverLog 'run2-saved' @{ file = $run2File.Name }

    # --- exactly 2 files in the runs dir, final check --------------------------------
    $allRunFiles = @(Get-ChildItem $runsDir -Filter '*.json' -File)
    if ($allRunFiles.Count -ne 2) {
        throw "ASSERT::run-file - expected exactly 2 run files at the end, found $($allRunFiles.Count): $(($allRunFiles | ForEach-Object { $_.Name }) -join ', ')"
    }

    # --- parse both runs --------------------------------------------------------------
    $run1Json = $null
    try {
        $run1Json = Get-Content $run1File.FullName -Raw | ConvertFrom-Json
    }
    catch {
        throw "ASSERT::run-file - '$($run1File.Name)' (run 1) did not parse as JSON: $($_.Exception.Message)"
    }
    $run2Json = $null
    try {
        $run2Json = Get-Content $run2File.FullName -Raw | ConvertFrom-Json
    }
    catch {
        throw "ASSERT::run-file - '$($run2File.Name)' (run 2) did not parse as JSON: $($_.Exception.Message)"
    }

    if ([int]$run1Json.schemaVersion -ne 1) {
        throw "ASSERT::run-file - run 1 schemaVersion is '$($run1Json.schemaVersion)', expected 1"
    }
    if ([int]$run2Json.schemaVersion -ne 1) {
        throw "ASSERT::run-file - run 2 schemaVersion is '$($run2Json.schemaVersion)', expected 1"
    }
    if ([string]$run1Json.rootDn -notlike "*$filterText*") {
        throw "ASSERT::run-file - run 1 rootDn '$($run1Json.rootDn)' does not name the loaded root '$filterText'"
    }
    if ([string]$run2Json.rootDn -notlike "*$filterText*") {
        throw "ASSERT::run-file - run 2 rootDn '$($run2Json.rootDn)' does not name the loaded root '$filterText'"
    }

    $findings1Prop = $run1Json.PSObject.Properties['findings']
    $findings2Prop = $run2Json.PSObject.Properties['findings']
    if (-not $findings1Prop -or $null -eq $findings1Prop.Value) {
        throw 'ASSERT::run-file - run 1 findings array is missing'
    }
    if (-not $findings2Prop -or $null -eq $findings2Prop.Value) {
        throw 'ASSERT::run-file - run 2 findings array is missing'
    }
    $findings1 = @($findings1Prop.Value)
    $findings2 = @($findings2Prop.Value)

    # A vacuous "identical empty arrays" proof would not actually pin the ADR-032
    # determinism contract - the AGDLP-Demo scope must yield the AP 3.2 baseline's
    # non-trivial finding set (.claude/rules/rule-engine.md: 19 findings).
    if ($findings1.Count -eq 0) {
        throw 'ASSERT::zero-drift - run 1 has zero findings; the AGDLP-Demo scope should yield the AP 3.2 baseline (19 findings) - scope/root load likely wrong'
    }

    # --- THE zero-drift proof: findings identical (order + content), timestamp differs, ruleset hash identical
    Assert-FindingsIdentical -ExpectedFindings $findings1 -ActualFindings $findings2 -What 'run1 vs run2'
    Write-DriverLog 'findings-identical' @{ count = $findings1.Count }

    if ("$($run1Json.rulesetHash)" -ne "$($run2Json.rulesetHash)") {
        throw "ASSERT::zero-drift - rulesetHash differs between runs: '$($run1Json.rulesetHash)' vs '$($run2Json.rulesetHash)' (same ruleset should hash identically)"
    }
    if (-not $run1Json.rulesetHash) {
        throw 'ASSERT::zero-drift - rulesetHash is empty/missing'
    }

    if ("$($run1Json.timestamp)" -eq "$($run2Json.timestamp)") {
        throw 'ASSERT::zero-drift - timestamp is identical between the two runs; the second save may have overwritten the first (same-second race) rather than producing a genuinely distinct run'
    }
    Write-DriverLog 'zero-drift-confirmed' @{
        findings = $findings1.Count
        rulesetHash = "$($run1Json.rulesetHash)"
        timestamp1 = "$($run1Json.timestamp)"
        timestamp2 = "$($run2Json.timestamp)"
    }

    # --- the real %APPDATA% must be untouched (the state seam's contract) -----------
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
