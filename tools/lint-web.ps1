#Requires -Version 7.2
<#
.SYNOPSIS
    Check-only ESLint gate for the hand-authored web bundle (src/App/web).

.DESCRIPTION
    Runs the correctness lint (repo-root eslint.config.js) against the
    hand-written bundle JS - #301 item 8. STRICTLY non-mutating: no --fix
    exists in this script and never should, because CI verifies the published
    bundle byte-identical to the vendored source (ADR-004/ADR-012). The eslint
    binary lives in tests/graph-bundle/node_modules (the repo's single Node
    footprint); this script npm-ci's it when missing, mirroring
    tools/test-graph-bundle.ps1.
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
$bundleDir = Join-Path $repoRoot 'tests/graph-bundle'
$eslint = Join-Path $bundleDir 'node_modules/.bin/eslint.ps1'

if (-not (Test-Path $eslint)) {
    Write-Host '==> npm ci (tests/graph-bundle - eslint not present yet)' -ForegroundColor Cyan
    Push-Location $bundleDir
    try {
        npm ci
        if ($LASTEXITCODE -ne 0) { Write-Host 'FAILED: npm ci' -ForegroundColor Red; exit 1 }
    }
    finally { Pop-Location }
}

Write-Host '==> eslint (check-only) src/App/web/*.js' -ForegroundColor Cyan
Push-Location $repoRoot
try {
    & $eslint --config (Join-Path $repoRoot 'eslint.config.js') --max-warnings 0 'src/App/web/*.js'
    $code = $LASTEXITCODE
}
finally { Pop-Location }

if ($code -ne 0) {
    Write-Host "FAILED: eslint (exit $code)" -ForegroundColor Red
    exit $code
}
Write-Host 'OK: web bundle lint clean.' -ForegroundColor Green
exit 0
