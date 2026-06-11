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
    dotnet format $solution --verify-no-changes --no-restore
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
