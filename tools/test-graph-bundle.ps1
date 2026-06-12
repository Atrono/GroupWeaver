#Requires -Version 7
<#
.SYNOPSIS
    GroupWeaver graph-bundle gate: dump demo fixture -> Playwright-verify the shipped bundle.

.DESCRIPTION
    Single gate for the browser graph layer (ADR-004 Consequences), used identically
    on the dev box and in CI: builds the app if needed, dumps the demo graph fixture
    (--demo --dump-graph, the ONLY sanctioned dump mode - live AD never reaches
    artifacts), then runs tests/graph-bundle/verify.mjs against the literal
    src/App/web bundle. Screenshots land in artifacts/ui/graph-*.png.
    Each step fails the script with a non-zero exit code.
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
$appExe = Join-Path $repoRoot 'src/App/bin/Release/net8.0-windows/GroupWeaver.App.exe'
$fixtureDir = Join-Path $repoRoot 'artifacts/graph-fixtures'
$fixturePath = Join-Path $fixtureDir 'demo-graph.json'
$uiDir = Join-Path $repoRoot 'artifacts/ui'

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

if (-not (Test-Path $appExe)) {
    Invoke-Step 'dotnet build src/App (Release)' {
        dotnet build (Join-Path $repoRoot 'src/App') -c Release
    }
}

New-Item -ItemType Directory -Force $fixtureDir | Out-Null
New-Item -ItemType Directory -Force $uiDir | Out-Null

Invoke-Step 'dump demo graph fixture (--demo --dump-graph)' {
    # WinExe: PowerShell only waits for GUI-subsystem processes when their output
    # is consumed - Start-Process -Wait gives a deterministic exit code instead.
    $app = Start-Process -FilePath $appExe -ArgumentList '--demo', '--dump-graph', "`"$fixturePath`"" `
        -NoNewWindow -PassThru -Wait
    $global:LASTEXITCODE = $app.ExitCode
}
if (-not (Test-Path $fixturePath)) {
    Write-Host "FAILED: fixture not written to $fixturePath" -ForegroundColor Red
    exit 1
}

Push-Location (Join-Path $repoRoot 'tests/graph-bundle')
try {
    Invoke-Step 'npm ci (graph-bundle harness deps)' { npm ci }

    # Idempotent and fast when the browser is already under %LOCALAPPDATA%\ms-playwright.
    Invoke-Step 'npx playwright install chromium' { npx playwright install chromium }

    Invoke-Step 'node verify.mjs (bundle verification + screenshots)' {
        node verify.mjs $fixturePath $uiDir
    }
}
finally {
    Pop-Location
}

Write-Host ''
Write-Host 'Graph bundle gate passed.' -ForegroundColor Green
exit 0
