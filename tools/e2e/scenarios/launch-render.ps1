<#
.SYNOPSIS
    E2E smoke scenario: launch -> demo connect -> root load -> workspace render ->
    clean shutdown (ADR-038 WP4, issue #243).

.DESCRIPTION
    The minimal end-to-end life cycle: launch the real exe on the hermetic
    --state-dir seam (ADR-038 D3.1, WP5: '--demo --state-dir <dir>' - the demo
    gate makes --demo mandatory, and the auto-connect skips the Connect card
    entirely, so the live operator identity is never captured and only demo data
    is ever touched), load the proven 2-node root, gate on the rendered workspace
    graph (the blue External frontier node blob - the never-occluded render
    signal), capture one checkpoint, then the clean-shutdown gate.

    State is HERMETIC: a deterministic ui-state.json (rail expanded, dark theme)
    is pre-seeded into the scenario's own state dir; the app, its WebView2
    profile, and its log sink all live under that dir. The operator's real
    %APPDATA% is never read or written (no Backup-OperatorState needed).

    Cross-cutting invariants (ADR-038 D4): alive at each boundary, no unexpected
    top-level #32770 dialogs, clean shutdown (exit <= 5 s, code 0), clean stderr,
    zero new WER dumps in the run window.

    Tier A input fidelity (ADR-038 D2): all actions are UIA + posted WM_*.
    Windows PowerShell 5.1 (relaunches itself from pwsh); ASCII-ONLY file.
#>
[CmdletBinding()]
param(
    [string]$ArtifactDir = '',
    [string]$StateDir = '',
    [string]$AppExe = ''
)

$ErrorActionPreference = 'Stop'

# --- relaunch under Windows PowerShell 5.1 when started from pwsh -------------
if ($PSVersionTable.PSEdition -eq 'Core') {
    $ps51 = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
    & $ps51 -NoProfile -NonInteractive -ExecutionPolicy Bypass -File $PSCommandPath `
        -ArtifactDir $ArtifactDir -StateDir $StateDir -AppExe $AppExe
    exit $LASTEXITCODE
}

# --- defaults for direct (runner-less) invocation ------------------------------
$repoRoot = Split-Path -Parent (Split-Path -Parent (Split-Path -Parent $PSScriptRoot))
if (-not $ArtifactDir) {
    $ArtifactDir = Join-Path $repoRoot ('artifacts\e2e\adhoc\launch-render-{0:yyyyMMdd-HHmmss}' -f (Get-Date))
}
if (-not $AppExe) {
    $AppExe = Join-Path $repoRoot 'src\App\bin\Release\net8.0-windows\GroupWeaver.App.exe'
}
if (-not $StateDir) {
    $StateDir = Join-Path $env:TEMP ('gw-e2e\adhoc\launch-render-{0:yyyyMMdd-HHmmss}' -f (Get-Date))
}

. (Join-Path (Split-Path -Parent $PSScriptRoot) 'lib\e2e-driver.ps1')

Initialize-E2eContext -ScenarioName 'launch-render' -ArtifactDir $ArtifactDir
$runStart = Get-Date

# Hermetic state (ADR-038 D3.1, WP5): deterministic ui-state.json into the
# scenario's OWN state dir - the operator's real %APPDATA% is never touched.
[void](Initialize-E2eStateDir -StateDir $StateDir)

# Proven render-confirmation root + rendered node color, identical to
# smoke-back-nav / record-demo-gif: the blue External frontier node of the 2-node
# DL_FS-Finance_RW scope is the never-occluded render signal.
$filterText = 'DL_FS-Finance_RW'
$colorExternalNode = @(49, 85, 115)

$failed = $false
try {
    # --demo is MANDATORY with --state-dir (the app-side demo gate); the startup
    # auto-connect lands directly on the root picker, so no 'Demo mode' click.
    [void](Start-E2EApp -ExePath $AppExe -AppArgs @('--demo') -StateDir $StateDir)
    Assert-Alive 'launch (window up)'

    Invoke-RootLoad -FilterText $filterText
    Assert-Alive 'demo connect + root load'

    Wait-ChromiumChild -TimeoutSec 60
    [void](Wait-NodeBlob { Save-Probe } $colorExternalNode 30 'the external frontier node (render signal)')
    Write-DriverLog 'workspace-rendered' @{}
    Capture-Checkpoint 'workspace-rendered'
    Assert-Alive 'workspace render'

    Assert-NoUnexpectedDialogs 'workspace rendered'

    Invoke-CleanShutdownGate
    Assert-CleanStderr 'after shutdown'
    Assert-NoNewWerDumps -Since $runStart -Where 'end of scenario'

    Write-Result -Result pass -AppExitCode 0
}
catch {
    $failed = $true
    Complete-E2eFailure -ErrorRecord $_ -RunStart $runStart
}
finally {
    Stop-E2EAppForce
}

if ($failed) { exit 1 } else { exit 0 }
