#Requires -Version 7.2
<#
.SYNOPSIS
    Render an HTML + text coverage report from a `build.ps1 -Coverage` run.

.DESCRIPTION
    Consumes the cobertura files that `pwsh tools/build.ps1 -Coverage` wrote to
    artifacts/coverage/ and renders them with ReportGenerator (pinned local
    dotnet tool, .config/dotnet-tools.json) to artifacts/coverage/report/.
    Prints the text summary so the headline number lands in the console.
    Measurement only - no threshold, no gating (audit plan item 6; a gate is a
    separate, later decision once a baseline exists).
#>
[CmdletBinding()]
param()

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
Get-Content (Join-Path $reportDir 'Summary.txt')
Write-Host ''
Write-Host "HTML report: $reportDir\index.html" -ForegroundColor Green
exit 0
