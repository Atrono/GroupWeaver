#Requires -Version 7
<#
.SYNOPSIS
    E2E scenario runner: discovery, sequencing, watchdog, artifacts, summary
    (ADR-038 D1/D4/D5, WP4 issue #243).

.DESCRIPTION
    Orchestrates the windowed E2E scenarios in scenarios/scenarios.psd1. Each
    scenario runs as a powershell.exe (Windows PowerShell 5.1) child - the GAC
    UIAutomationClient requirement - strictly SEQUENTIALLY (one desktop,
    exclusive foreground geometry), each with its own artifact dir, state dir
    and a per-manifest TimeoutSec watchdog. On watchdog fire the child is
    killed, then any GroupWeaver.App / msedgewebview2 descendants are killed
    via a ParentProcessId walk. A failed scenario is retried ONCE iff its
    result signature matches a manifest RetrySignatures pattern (default none).

    Artifacts land run-first (ADR-038 D5): artifacts/e2e/runs/<stamp>/ holds
    summary.json + summary.md plus one subdir per scenario (harness.jsonl,
    result.json, app-stdout/stderr, checkpoints/, evidence/ on failure).
    artifacts/ is gitignored - evidence never reaches the public repo.

    Reuses an existing Release build of src/App, builds one if absent (the
    tools/test-cli-matrix.ps1 pattern). Zero AD interaction.

.EXAMPLE
    pwsh tools/e2e/run-e2e.ps1                      # smoke tag (default)
    pwsh tools/e2e/run-e2e.ps1 -Tag full
    pwsh tools/e2e/run-e2e.ps1 -Scenario back-nav   # by name, tag ignored
#>
[CmdletBinding()]
param(
    [ValidateSet('smoke', 'full', 'perf', 'requires-ad')]
    [string]$Tag = 'smoke',

    # Run only these scenario names (tag filter is skipped when given).
    [string[]]$Scenario = @()
)

$ErrorActionPreference = 'Stop'

$e2eRoot = $PSScriptRoot
$repoRoot = Split-Path -Parent (Split-Path -Parent $e2eRoot)
$appExe = Join-Path $repoRoot 'src\App\bin\Release\net8.0-windows\GroupWeaver.App.exe'

# --- locate or build the Release exe (test-cli-matrix.ps1 pattern) ----------------
if (-not (Test-Path $appExe)) {
    Write-Host ''
    Write-Host '==> dotnet build src/App (Release)' -ForegroundColor Cyan
    dotnet build (Join-Path $repoRoot 'src\App') -c Release
    if ($LASTEXITCODE -ne 0) {
        Write-Host "FAILED: dotnet build src/App (exit code $LASTEXITCODE)" -ForegroundColor Red
        exit $LASTEXITCODE
    }
}

# --- discovery ---------------------------------------------------------------------
$manifestPath = Join-Path $e2eRoot 'scenarios\scenarios.psd1'
$manifest = Import-PowerShellDataFile $manifestPath
$all = @($manifest.Scenarios)
foreach ($spec in $all) {
    foreach ($key in @('Name', 'Tags', 'TimeoutSec')) {
        if (-not $spec.ContainsKey($key)) { throw "scenarios.psd1: entry missing required key '$key'" }
    }
    if (-not (Test-Path (Join-Path $e2eRoot "scenarios\$($spec.Name).ps1"))) {
        throw "scenarios.psd1: no scenario script for '$($spec.Name)'"
    }
}

$selected = @(
    if ($Scenario.Count -gt 0) {
        $all | Where-Object { $Scenario -contains $_.Name }
    }
    else {
        $all | Where-Object { $_.Tags -contains $Tag }
    }
)
if ($selected.Count -eq 0) {
    Write-Host "No scenarios matched (Tag='$Tag', Scenario='$($Scenario -join ',')')." -ForegroundColor Red
    exit 2
}

# --- run layout ----------------------------------------------------------------------
$stamp = '{0:yyyyMMdd-HHmmss}' -f (Get-Date)
$runDir = Join-Path $repoRoot "artifacts\e2e\runs\$stamp"
$stateRoot = Join-Path $env:TEMP "gw-e2e\$stamp"
New-Item -ItemType Directory -Force $runDir | Out-Null

$gitSha = (git -C $repoRoot rev-parse HEAD).Trim()
$controllers = @(Get-CimInstance Win32_VideoController | Select-Object -ExpandProperty Name)
$renderingMode = if (@($controllers | Where-Object { $_ -notlike '*Microsoft Basic Display*' }).Count -gt 0) { 'gpu' } else { 'software' }
$startedUtc = (Get-Date).ToUniversalTime().ToString('o')

Write-Host ''
Write-Host "==> E2E run $stamp  (tag: $Tag, scenarios: $(($selected | ForEach-Object { $_.Name }) -join ', '))" -ForegroundColor Cyan
Write-Host "    git $gitSha, rendering mode: $renderingMode"
Write-Host "    artifacts: $runDir"

# --- watchdog kill-tree ---------------------------------------------------------------
# PPID walk over a Win32_Process snapshot taken BEFORE killing the child (PPIDs
# persist after parent death on Windows, but a pre-kill snapshot is the safest
# base). Only GroupWeaver.App / msedgewebview2 descendants are ever killed.
function Stop-ScenarioTree {
    param(
        [Parameter(Mandatory)][int]$ChildPid,
        [Parameter(Mandatory)][string]$Why
    )
    $table = @(Get-CimInstance Win32_Process | Select-Object ProcessId, ParentProcessId, Name)
    $byParent = @{}
    foreach ($p in $table) {
        $key = [int]$p.ParentProcessId
        if (-not $byParent.ContainsKey($key)) { $byParent[$key] = [System.Collections.Generic.List[object]]::new() }
        $byParent[$key].Add($p)
    }

    try { Stop-Process -Id $ChildPid -Force -ErrorAction Stop } catch { }

    $descendants = [System.Collections.Generic.List[object]]::new()
    $visited = [System.Collections.Generic.HashSet[int]]::new()
    $queue = [System.Collections.Generic.Queue[int]]::new()
    $queue.Enqueue($ChildPid)
    [void]$visited.Add($ChildPid)
    while ($queue.Count -gt 0) {
        $cur = $queue.Dequeue()
        if ($byParent.ContainsKey($cur)) {
            foreach ($child in $byParent[$cur]) {
                $cpid = [int]$child.ProcessId
                if ($visited.Add($cpid)) {
                    $descendants.Add($child)
                    $queue.Enqueue($cpid)
                }
            }
        }
    }
    foreach ($d in $descendants) {
        if ($d.Name -in @('GroupWeaver.App.exe', 'msedgewebview2.exe')) {
            Write-Host "    [$Why] killing descendant $($d.Name) (pid $($d.ProcessId))" -ForegroundColor Yellow
            try { Stop-Process -Id ([int]$d.ProcessId) -Force -ErrorAction Stop } catch { }
        }
    }
}

# --- leftover operator-state recovery (ADR-038 D6: "the runner brackets them") --------
# Scenarios that touch the operator's real %APPDATA% state back it up ON DISK as
# <file>.e2e-bak (driver: Backup-OperatorState) precisely because a watchdog kill
# skips the child's finally block. The runner therefore restores leftovers itself:
# after every watchdog kill and once at end of run. The sentinel marks a file that
# did NOT pre-exist (recovery deletes the forced file instead of keeping it) and
# MUST match e2e-driver.ps1's $E2eNoPriorFileSentinel.
$noPriorFileSentinel = '__GW_E2E_NO_PRIOR_FILE__'
function Restore-OperatorStateLeftovers {
    param([Parameter(Mandatory)][string]$Why)
    $operatorStateDir = Join-Path $env:APPDATA 'GroupWeaver'
    if (-not (Test-Path $operatorStateDir)) { return }
    foreach ($bak in @(Get-ChildItem -Path $operatorStateDir -Filter '*.e2e-bak' -File)) {
        $target = $bak.FullName.Substring(0, $bak.FullName.Length - '.e2e-bak'.Length)
        $content = [string](Get-Content $bak.FullName -Raw)
        if ($content.Trim() -eq $noPriorFileSentinel) {
            if (Test-Path $target) { Remove-Item -Force $target }
            Remove-Item -Force $bak.FullName
            Write-Host "    [$Why] removed forced operator state (did not pre-exist): $target" -ForegroundColor Yellow
        }
        else {
            Move-Item -Force $bak.FullName $target
            Write-Host "    [$Why] restored leftover operator state: $target" -ForegroundColor Yellow
        }
    }
}

# --- single scenario execution ---------------------------------------------------------
function Invoke-ScenarioOnce {
    param(
        [Parameter(Mandatory)][hashtable]$Spec,
        [Parameter(Mandatory)][string]$ArtDir,
        [Parameter(Mandatory)][string]$StateDir
    )
    New-Item -ItemType Directory -Force $ArtDir | Out-Null
    New-Item -ItemType Directory -Force $StateDir | Out-Null
    $scriptPath = Join-Path $e2eRoot "scenarios\$($Spec.Name).ps1"
    $childOut = Join-Path $ArtDir 'driver-stdout.txt'
    $childErr = Join-Path $ArtDir 'driver-stderr.txt'
    $childArgs = @(
        '-NoProfile', '-NonInteractive', '-ExecutionPolicy', 'Bypass',
        '-File', ('"{0}"' -f $scriptPath),
        '-ArtifactDir', ('"{0}"' -f $ArtDir),
        '-StateDir', ('"{0}"' -f $StateDir),
        '-AppExe', ('"{0}"' -f $appExe)
    )
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    $child = Start-Process -FilePath (Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe') `
        -ArgumentList $childArgs -PassThru -NoNewWindow `
        -RedirectStandardOutput $childOut -RedirectStandardError $childErr
    $timedOut = -not $child.WaitForExit($Spec.TimeoutSec * 1000)
    if ($timedOut) {
        Write-Host "    WATCHDOG: '$($Spec.Name)' exceeded $($Spec.TimeoutSec)s - killing" -ForegroundColor Red
        Stop-ScenarioTree -ChildPid $child.Id -Why 'watchdog'
        # The kill skipped the scenario's finally - restore the operator's state
        # from the on-disk backup NOW, before the next scenario launches the app.
        Restore-OperatorStateLeftovers -Why 'watchdog'
    }
    else {
        # Belt-and-braces sweep: a scenario's finally should have cleaned up; kill
        # anything it left behind so the NEXT scenario starts on a clean desktop.
        Stop-ScenarioTree -ChildPid $child.Id -Why 'post-scenario sweep'
    }
    $sw.Stop()

    $entry = [ordered]@{
        name        = $Spec.Name
        result      = 'fail'
        class       = ''
        signature   = ''
        durationMs  = $sw.ElapsedMilliseconds
        retried     = $false
        artifactDir = $ArtDir
    }
    if ($timedOut) {
        $entry.class = 'TIMEOUT'
        $entry.signature = "TIMEOUT::$($Spec.Name) exceeded $($Spec.TimeoutSec)s"
        return $entry
    }

    $resultPath = Join-Path $ArtDir 'result.json'
    if (-not (Test-Path $resultPath)) {
        $entry.class = 'INFRA-DRIVE'
        $entry.signature = "INFRA::no result.json (child exit $($child.ExitCode))"
        return $entry
    }
    $result = Get-Content $resultPath -Raw | ConvertFrom-Json
    $entry.result = $result.result
    $entry.class = [string]$result.class
    $entry.signature = [string]$result.signature
    if ($result.result -eq 'pass' -and $child.ExitCode -ne 0) {
        # Protocol mismatch - do not trust a pass with a failing child.
        $entry.result = 'fail'
        $entry.class = 'INFRA-DRIVE'
        $entry.signature = "INFRA::result.json says pass but child exited $($child.ExitCode)"
    }
    return $entry
}

# --- sequential run + single-retry policy -----------------------------------------------
$entries = [System.Collections.Generic.List[object]]::new()
foreach ($spec in $selected) {
    Write-Host ''
    Write-Host "==> scenario: $($spec.Name) (timeout $($spec.TimeoutSec)s)" -ForegroundColor Cyan
    $artDir = Join-Path $runDir $spec.Name
    $stateDir = Join-Path $stateRoot $spec.Name
    $entry = Invoke-ScenarioOnce -Spec $spec -ArtDir $artDir -StateDir $stateDir

    if ($entry.result -ne 'pass') {
        $retryPatterns = @($spec.RetrySignatures)
        $matched = @($retryPatterns | Where-Object { $entry.signature -like $_ })
        if ($matched.Count -gt 0) {
            Write-Host "    retrying once (signature matched '$($matched[0])')" -ForegroundColor Yellow
            $entry = Invoke-ScenarioOnce -Spec $spec `
                -ArtDir (Join-Path $runDir "$($spec.Name)-retry") `
                -StateDir (Join-Path $stateRoot "$($spec.Name)-retry")
            $entry.retried = $true
        }
    }

    if ($entry.result -eq 'pass') {
        Write-Host "    PASS $($spec.Name) ($([math]::Round($entry.durationMs / 1000.0, 1))s)" -ForegroundColor Green
    }
    else {
        Write-Host "    FAIL $($spec.Name) [$($entry.class)] $($entry.signature)" -ForegroundColor Red
        $tailPath = Join-Path $entry.artifactDir 'driver-stdout.txt'
        if (Test-Path $tailPath) {
            Write-Host '    --- driver-stdout tail ---'
            Get-Content $tailPath -Tail 15 | ForEach-Object { Write-Host "    $_" }
        }
    }
    $entries.Add($entry)
}

# End-of-run safety net: no scenario is running anymore, so ANY surviving
# .e2e-bak means a restore was missed (kill path, driver bug) - heal it here.
Restore-OperatorStateLeftovers -Why 'end-of-run sweep'

# --- summary -------------------------------------------------------------------------------
$summary = [ordered]@{
    run       = [ordered]@{
        startedUtc    = $startedUtc
        gitSha        = $gitSha
        renderingMode = $renderingMode
        tag           = $Tag
    }
    scenarios = @($entries)
}
$summary | ConvertTo-Json -Depth 6 | Set-Content -Path (Join-Path $runDir 'summary.json') -Encoding utf8

$md = [System.Collections.Generic.List[string]]::new()
$md.Add("# E2E run $stamp")
$md.Add('')
$md.Add("- started (UTC): $startedUtc")
$md.Add("- git: $gitSha")
$md.Add("- rendering mode: $renderingMode")
$md.Add("- tag: $Tag")
$md.Add('')
$md.Add('| scenario | result | class | duration | retried | signature |')
$md.Add('|---|---|---|---:|---|---|')
foreach ($e in $entries) {
    $cls = if ($e.class) { $e.class } else { '-' }
    $sig = if ($e.signature) { $e.signature } else { '-' }
    $md.Add("| $($e.name) | $($e.result) | $cls | $([math]::Round($e.durationMs / 1000.0, 1))s | $($e.retried) | $sig |")
}
$md | Set-Content -Path (Join-Path $runDir 'summary.md') -Encoding utf8

Write-Host ''
Write-Host (Get-Content (Join-Path $runDir 'summary.md') -Raw)

$failCount = @($entries | Where-Object { $_.result -ne 'pass' }).Count
if ($failCount -gt 0) {
    Write-Host "E2E run FAILED: $failCount of $($entries.Count) scenario(s) red. Artifacts: $runDir" -ForegroundColor Red
    exit 1
}
Write-Host "E2E run passed: $($entries.Count) scenario(s) green. Artifacts: $runDir" -ForegroundColor Green
exit 0
