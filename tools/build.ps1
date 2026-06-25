#Requires -Version 7
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

# `dotnet format` runs as its own child process and re-resolves the dotnet CLI from
# DOTNET_HOST_PATH / DOTNET_ROOT (NOT the PATH muxer that launched it). When those
# vars are unset - intermittently the case on CI's windows-2022 runner after
# setup-dotnet - the format step flakes with "Unable to locate dotnet CLI. Is it on
# the PATH?", cured only by a rerun. Resolve the absolute dotnet host once and pin
# both vars so the format probe is deterministic on the dev box and in CI alike;
# the format step then invokes this absolute path (so the host stamps DOTNET_HOST_PATH
# for its child too).
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

Invoke-Step 'dotnet format (verify no changes)' {
    & $dotnet format $solution --verify-no-changes --no-restore
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
