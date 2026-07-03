<#
.SYNOPSIS
    DEPRECATED thin wrapper: the Back-navigation windowed smoke now lives in the
    E2E harness (ADR-038 WP4, issue #243).

.DESCRIPTION
    The journey body (the #120 / ADR-024 crash paths and the #122 / ADR-025
    viewport-preservation pins) was ported behavior-identically to
    tools/e2e/scenarios/back-nav.ps1 on the shared driver
    tools/e2e/lib/e2e-driver.ps1 - WITH a recalibrated button coordinate map:
    the map pinned here on 2026-06-23 had gone stale against the redesigned rail
    (this script failed at the 'Design plan' click before the port).

    This wrapper invokes the runner with only the back-nav scenario and passes
    the exit code through, so existing invocations keep working. Prefer:

        pwsh tools/e2e/run-e2e.ps1 -Scenario back-nav
        pwsh tools/e2e/run-e2e.ps1 -Tag smoke      # the full smoke pack

.EXAMPLE
    pwsh tools/smoke-back-nav.ps1
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'

Write-Host 'DEPRECATED: tools/smoke-back-nav.ps1 is a thin wrapper now; the journey lives in tools/e2e/scenarios/back-nav.ps1 (ADR-038 WP4).'

# Always spawn a fresh pwsh 7 child (the runner is #Requires -Version 7; this
# wrapper may be invoked from Windows PowerShell 5.1) - exit code passthrough.
& pwsh -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'e2e\run-e2e.ps1') -Scenario 'back-nav'
exit $LASTEXITCODE
