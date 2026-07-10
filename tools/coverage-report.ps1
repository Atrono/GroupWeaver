#Requires -Version 7.2
<#
.SYNOPSIS
    Render an HTML + text coverage report from a `build.ps1 -Coverage` run.

.DESCRIPTION
    Consumes the cobertura files that `pwsh tools/build.ps1 -Coverage` wrote to
    artifacts/coverage/ and renders them with ReportGenerator (pinned local
    dotnet tool, .config/dotnet-tools.json) to artifacts/coverage/report/.
    Prints the text summary so the headline number lands in the console.

.PARAMETER MinLine
    Optional line-coverage floor in percent (#311 item 12). When set, the script
    exits non-zero if product-assembly line coverage falls below it. CI passes 83
    - two points under the measured CI-equivalent baseline (85.1% with
    Category=RequiresAd excluded; the full local suite measures higher because
    the live-AD tests exercise LdapProvider). Raise deliberately, never lower to
    make a build green (CLAUDE.md test rule applies in spirit).
#>
[CmdletBinding()]
param(
    [double]$MinLine = 0
)

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
$coverageDir = Join-Path $repoRoot 'artifacts/coverage'
$reportDir = Join-Path $coverageDir 'report'

$cobertura = Get-ChildItem -Path $coverageDir -Recurse -Filter '*.cobertura.xml' -ErrorAction SilentlyContinue
if (-not $cobertura) {
    Write-Host "No cobertura files under $coverageDir - run 'pwsh tools/build.ps1 -Coverage' first." -ForegroundColor Red
    exit 1
}
Write-Host "Found $($cobertura.Count) cobertura file(s)." -ForegroundColor Cyan

dotnet tool restore | Out-Null
if ($LASTEXITCODE -ne 0) { Write-Host 'dotnet tool restore failed.' -ForegroundColor Red; exit 1 }

$reports = ($cobertura.FullName -join ';')
dotnet tool run reportgenerator `
    "-reports:$reports" `
    "-targetdir:$reportDir" `
    '-reporttypes:Html;TextSummary' `
    '-assemblyfilters:+GroupWeaver.Core;+GroupWeaver.Providers;+GroupWeaver.App'
if ($LASTEXITCODE -ne 0) { Write-Host 'reportgenerator failed.' -ForegroundColor Red; exit $LASTEXITCODE }

Write-Host ''
$summary = Get-Content (Join-Path $reportDir 'Summary.txt')
$summary
Write-Host ''
Write-Host "HTML report: $reportDir\index.html" -ForegroundColor Green

if ($MinLine -gt 0) {
    # Compute the percentage from the covered/coverable integers - locale-proof
    # (the formatted "Line coverage:" string could use a comma decimal separator
    # on a de-DE box).
    $covered = [long](($summary | Select-String '^\s*Covered lines:').Line -replace '\D', '')
    $coverable = [long](($summary | Select-String '^\s*Coverable lines:').Line -replace '\D', '')
    if ($coverable -eq 0) { Write-Host 'FAILED: no coverable lines found in the report.' -ForegroundColor Red; exit 1 }
    $pct = [math]::Round(100.0 * $covered / $coverable, 1)
    if ($pct -lt $MinLine) {
        Write-Host "FAILED: line coverage $pct% is below the $MinLine% floor." -ForegroundColor Red
        exit 1
    }
    Write-Host "OK: line coverage $pct% meets the $MinLine% floor." -ForegroundColor Green
}
exit 0
