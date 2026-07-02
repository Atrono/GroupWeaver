#Requires -Version 7.2
<#
.SYNOPSIS
    GroupWeaver local build gate: restore -> build -> format check -> test.

.DESCRIPTION
    The single quality gate, used identically on the dev box and in CI
    (CI passes -SkipAdTests to exclude tests with trait Category=RequiresAd).
    Each step fails the script with a non-zero exit code.

.PARAMETER SkipAdTests
    Exclude integration tests that require the live AGDLP-Lab AD fixtures.
#>
[CmdletBinding()]
param(
    [switch]$SkipAdTests
)

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
$solution = Join-Path $repoRoot 'GroupWeaver.sln'

# Belt-and-braces: resolve the absolute dotnet host once and pin DOTNET_HOST_PATH /
# DOTNET_ROOT so `dotnet format` (a child process that re-resolves the CLI itself)
# never depends on ambient env. Pinning alone (issue #110) did NOT cure the
# "Unable to locate dotnet CLI" flake, though (#110 recurrence = #232): the real
# cause is an upstream race in dotnet-format's own CLI probe -- it spawns the dotnet
# host and reads its stdout via async redirection, and Process.WaitForExit vs
# BeginOutputReadLine can lose that output on loaded machines (dotnet/format#2000;
# still seen on the 8.x series per dotnet/sdk#44957). The actual mitigation is the
# retry-once in the format step below; the pinning stays as belt-and-braces.
$dotnet = (Get-Command dotnet -ErrorAction Stop).Source
$env:DOTNET_HOST_PATH = $dotnet
$env:DOTNET_ROOT = Split-Path -Parent $dotnet

function Invoke-Step {
    param(
        [Parameter(Mandatory)][string]$Name,
        [Parameter(Mandatory)][scriptblock]$Action
    )
    Write-Host ''
    Write-Host "==> $Name" -ForegroundColor Cyan
    & $Action
    if ($LASTEXITCODE -ne 0) {
        Write-Host "FAILED: $Name (exit code $LASTEXITCODE)" -ForegroundColor Red
        exit $LASTEXITCODE
    }
    Write-Host "OK: $Name" -ForegroundColor Green
}

Invoke-Step 'dotnet restore' { dotnet restore $solution }

Invoke-Step 'dotnet build (Release)' { dotnet build $solution --no-restore -c Release }

# Retry ONCE on dotnet-format's upstream CLI-probe race (dotnet/format#2000, #232):
# exit 4 plus the literal "Unable to locate dotnet CLI" output is that race's exact
# signature. A real format diff exits 2 and never emits that message, so it -- and
# every other failure -- falls through to Invoke-Step's failure handling unchanged.
Invoke-Step 'dotnet format (verify no changes)' {
    & $dotnet format $solution --verify-no-changes --no-restore 2>&1 | Tee-Object -Variable formatProbe
    if ($LASTEXITCODE -eq 4 -and ($formatProbe -join "`n") -like '*Unable to locate dotnet CLI*') {
        Write-Host 'WARNING: dotnet format hit the upstream CLI-probe race (exit 4, dotnet/format#2000) -- retrying once (issue #232).' -ForegroundColor Yellow
        & $dotnet format $solution --verify-no-changes --no-restore
    }
}

$testArgs = @($solution, '--no-build', '-c', 'Release')
if ($SkipAdTests) {
    $testArgs += @('--filter', 'Category!=RequiresAd')
    Write-Host 'AD integration tests (Category=RequiresAd) are excluded.' -ForegroundColor Yellow
}
Invoke-Step 'dotnet test (Release)' { dotnet test @testArgs }

Write-Host ''
Write-Host 'Build gate passed.' -ForegroundColor Green
exit 0
