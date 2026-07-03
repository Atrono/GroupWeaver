#Requires -Version 7
<#
.SYNOPSIS
    GroupWeaver CLI probe matrix gate: four process-level checks against the REAL
    built exe (ADR-038 / WP3, issue #242).

.DESCRIPTION
    Pins the headless CLI surface as a gate, used identically on the dev box and
    in CI (reuses an existing Release build, builds one if absent):

      1. --check --demo           exit 0 within 120 s; pinned 'GroupWeaver <version>'
                                  line + 'connected, N groups loaded' on stdout.
      2. bare --check             MUST EXIT within a 60 s watchdog - the
                                  launch-smoke-hang regression guard (historical:
                                  bare --check on CI hit live LDAP and hung 44 min).
                                  Outcome is environment-dependent and BOTH pass:
                                  exit 0 (DC reachable, lab box) or exit 1 with the
                                  directory-unavailable message on stderr (CI, no
                                  DC). FAIL = still running at 60 s or any other exit.
      3. --dump-graph (no --demo) exit 64 and no file written (the demo-only pin:
                                  live-AD structure must never reach artifacts).
      4. --demo --dump-graph x2   byte-identical dumps (determinism pin), both
                                  parse as JSON.

    Process handling per the pack-release launch-smoke lessons (docs/journal
    2026-06-13): ALWAYS redirect BOTH stdout and stderr (single-stream redirect
    on this WinExe deadlocks - the other handle stays attached to a console that
    never drains), bounded waits, kill-on-timeout, non-zero exit on any failure.
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
$appExe = Join-Path $repoRoot 'src/App/bin/Release/net8.0-windows/GroupWeaver.App.exe'

if (-not (Test-Path $appExe)) {
    Write-Host ''
    Write-Host '==> dotnet build src/App (Release)' -ForegroundColor Cyan
    dotnet build (Join-Path $repoRoot 'src/App') -c Release
    if ($LASTEXITCODE -ne 0) {
        Write-Host "FAILED: dotnet build src/App (exit code $LASTEXITCODE)" -ForegroundColor Red
        exit $LASTEXITCODE
    }
}

# Runs the built exe with BOTH streams redirected to temp files and a hard
# timeout; on timeout the process TREE is killed. Returns
# @{ TimedOut; ExitCode; StdOut; StdErr }.
function Invoke-AppExe {
    param(
        [Parameter(Mandatory)][string[]]$ExeArgs,
        [Parameter(Mandatory)][int]$TimeoutSec
    )
    $outPath = Join-Path ([System.IO.Path]::GetTempPath()) "gw-cli-out-$([guid]::NewGuid()).txt"
    $errPath = Join-Path ([System.IO.Path]::GetTempPath()) "gw-cli-err-$([guid]::NewGuid()).txt"
    try {
        $proc = Start-Process -FilePath $appExe -ArgumentList $ExeArgs -NoNewWindow -PassThru `
            -RedirectStandardOutput $outPath -RedirectStandardError $errPath
        if (-not $proc.WaitForExit($TimeoutSec * 1000)) {
            try { $proc.Kill($true) } catch { }
            return @{ TimedOut = $true; ExitCode = $null; StdOut = ''; StdErr = '' }
        }
        return @{
            TimedOut = $false
            ExitCode = $proc.ExitCode
            StdOut   = if (Test-Path $outPath) { [string](Get-Content $outPath -Raw) } else { '' }
            StdErr   = if (Test-Path $errPath) { [string](Get-Content $errPath -Raw) } else { '' }
        }
    }
    finally {
        Remove-Item $outPath, $errPath -ErrorAction SilentlyContinue
    }
}

$script:failed = $false

function Report {
    param(
        [Parameter(Mandatory)][string]$Check,
        [Parameter(Mandatory)][bool]$Pass,
        [Parameter(Mandatory)][string]$Detail
    )
    if ($Pass) {
        Write-Host "PASS: $Check - $Detail" -ForegroundColor Green
    }
    else {
        $script:failed = $true
        Write-Host "FAIL: $Check - $Detail" -ForegroundColor Red
    }
}

$tmpDir = Join-Path ([System.IO.Path]::GetTempPath()) "gw-cli-matrix-$([guid]::NewGuid())"
New-Item -ItemType Directory -Force $tmpDir | Out-Null

try {
    # ---------- check 1: --check --demo (pinned stdout, exit 0) ----------
    Write-Host ''
    Write-Host '==> Check 1: --check --demo (exit 0, pinned stdout, 120 s bound)' -ForegroundColor Cyan
    $r = Invoke-AppExe -ExeArgs @('--check', '--demo') -TimeoutSec 120
    if ($r.TimedOut) {
        Report 'check 1 (--check --demo)' $false 'did not exit within 120 s (killed)'
    }
    elseif ($r.ExitCode -ne 0) {
        Report 'check 1 (--check --demo)' $false "exited $($r.ExitCode), expected 0. stderr: $($r.StdErr.Trim() -replace '\r?\n', ' | ')"
    }
    elseif ($r.StdOut -notmatch '(?m)^GroupWeaver \S+') {
        Report 'check 1 (--check --demo)' $false "stdout is missing the pinned 'GroupWeaver <version>' line. stdout: $($r.StdOut.Trim() -replace '\r?\n', ' | ')"
    }
    elseif ($r.StdOut -notmatch 'connected,\s+\d+\s+groups loaded') {
        Report 'check 1 (--check --demo)' $false "stdout is missing the pinned 'connected, N groups loaded' summary. stdout: $($r.StdOut.Trim() -replace '\r?\n', ' | ')"
    }
    else {
        Report 'check 1 (--check --demo)' $true "exit 0; stdout: $($r.StdOut.Trim() -replace '\r?\n', ' | ')"
    }

    # ---------- check 2: bare --check under the 60 s watchdog ----------
    Write-Host ''
    Write-Host '==> Check 2: bare --check (60 s watchdog - launch-smoke-hang regression guard)' -ForegroundColor Cyan
    $r = Invoke-AppExe -ExeArgs @('--check') -TimeoutSec 60
    if ($r.TimedOut) {
        Report 'check 2 (bare --check)' $false 'still running at the 60 s watchdog (killed) - the launch-smoke-hang regression'
    }
    elseif ($r.ExitCode -eq 0) {
        Report 'check 2 (bare --check)' $true "exit 0 (domain reachable in this environment); stdout: $($r.StdOut.Trim() -replace '\r?\n', ' | ')"
    }
    elseif ($r.ExitCode -eq 1 -and $r.StdErr -like '*no domain reachable in this user context*') {
        Report 'check 2 (bare --check)' $true 'exit 1 with the directory-unavailable message on stderr (no DC in this environment)'
    }
    else {
        Report 'check 2 (bare --check)' $false "exited $($r.ExitCode), expected 0 or 1 + directory-unavailable. stderr: $($r.StdErr.Trim() -replace '\r?\n', ' | ')"
    }

    # ---------- check 3: --dump-graph without --demo (demo-only refusal) ----------
    Write-Host ''
    Write-Host '==> Check 3: --dump-graph without --demo (exit 64, no file written)' -ForegroundColor Cyan
    $noDemoDump = Join-Path $tmpDir 'dump-no-demo.json'
    $r = Invoke-AppExe -ExeArgs @('--dump-graph', "`"$noDemoDump`"") -TimeoutSec 60
    if ($r.TimedOut) {
        Report 'check 3 (--dump-graph, no --demo)' $false 'did not exit within 60 s (killed)'
    }
    elseif ($r.ExitCode -ne 64) {
        Report 'check 3 (--dump-graph, no --demo)' $false "exited $($r.ExitCode), expected 64. stderr: $($r.StdErr.Trim() -replace '\r?\n', ' | ')"
    }
    elseif (Test-Path $noDemoDump) {
        Report 'check 3 (--dump-graph, no --demo)' $false "wrote $noDemoDump despite the demo-only refusal"
    }
    else {
        Report 'check 3 (--dump-graph, no --demo)' $true 'exit 64 and no file written (demo-only refusal intact)'
    }

    # ---------- check 4: --demo --dump-graph twice (determinism pin) ----------
    Write-Host ''
    Write-Host '==> Check 4: --demo --dump-graph twice (byte-identical, valid JSON)' -ForegroundColor Cyan
    $dumpA = Join-Path $tmpDir 'dump-a.json'
    $dumpB = Join-Path $tmpDir 'dump-b.json'
    $runsOk = $true
    foreach ($dump in @($dumpA, $dumpB)) {
        $r = Invoke-AppExe -ExeArgs @('--demo', '--dump-graph', "`"$dump`"") -TimeoutSec 120
        if ($r.TimedOut) {
            Report 'check 4 (--demo --dump-graph x2)' $false "dump to $dump did not exit within 120 s (killed)"
            $runsOk = $false
            break
        }
        if ($r.ExitCode -ne 0 -or -not (Test-Path $dump)) {
            Report 'check 4 (--demo --dump-graph x2)' $false "dump to $dump exited $($r.ExitCode) (file exists: $(Test-Path $dump)). stderr: $($r.StdErr.Trim() -replace '\r?\n', ' | ')"
            $runsOk = $false
            break
        }
    }
    if ($runsOk) {
        $hashA = (Get-FileHash $dumpA -Algorithm SHA256).Hash
        $hashB = (Get-FileHash $dumpB -Algorithm SHA256).Hash
        $jsonOk = $true
        try {
            Get-Content $dumpA -Raw | ConvertFrom-Json | Out-Null
            Get-Content $dumpB -Raw | ConvertFrom-Json | Out-Null
        }
        catch {
            $jsonOk = $false
        }
        if ($hashA -ne $hashB) {
            Report 'check 4 (--demo --dump-graph x2)' $false "dumps differ (SHA256 $hashA vs $hashB) - determinism regression"
        }
        elseif (-not $jsonOk) {
            Report 'check 4 (--demo --dump-graph x2)' $false 'a dump did not parse as JSON'
        }
        else {
            Report 'check 4 (--demo --dump-graph x2)' $true "byte-identical dumps (SHA256 $hashA), both valid JSON"
        }
    }
}
finally {
    Remove-Item $tmpDir -Recurse -Force -ErrorAction SilentlyContinue
}

Write-Host ''
if ($script:failed) {
    Write-Host 'CLI matrix gate FAILED.' -ForegroundColor Red
    exit 1
}
Write-Host 'CLI matrix gate passed.' -ForegroundColor Green
exit 0
