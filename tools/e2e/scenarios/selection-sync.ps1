<#
.SYNOPSIS
    E2E scenario: canvas<->sidebar selection sync over the --e2e channel
    (ADR-038 D3.2/WP6b, P1.3, issue #245; product contract: ADR-020).

.DESCRIPTION
    Proves BOTH selection-sync directions land on the authoritative
    state.selectedDn field (WP6a's E2eChannel.cs / this WP's e2e-channel.ps1),
    never a pixel/color guess:

    FORWARD (graph -> sidebar, ADR-018 D3): a canvas tap on a node fires
    nodeClick, which sets WorkspaceViewModel.SelectedDn - Wait-E2eState must
    show a non-null selectedDn matching the DN of a group in the loaded scope
    (kind-consistent: the clicked node is located by hunting the GlobalGroup
    node color/shape via Find-NodeBlob, so the resulting DN is asserted to
    look like a GG_* group under OU=Groups,OU=AGDLP-Demo).

    REVERSE (sidebar -> graph, ADR-020/#96): a click on the TOPMOST violations
    row's jump button invokes JumpCommand, which sets SelectedDn AND focuses
    the anchor. The topmost row's identity is derived, not guessed: the AP 3.2
    demo baseline (.claude/rules/rule-engine.md, pinned by
    RuleEngineDemoBaselineTests) is deterministic under the full AGDLP-Demo
    scope + the embedded default ruleset - canonical report order puts the
    first nesting violation (DL_FS-Finance_RO <- DL_Nested_RO deny) first, so
    row 0's PrimaryDn is KNOWN ahead of time: CN=DL_FS-Finance_RO,
    OU=Groups,OU=AGDLP-Demo,DC=weavedemo,DC=example. Wait-E2eState must show
    selectedDn become exactly that DN - and DIFFERENT from whatever the
    forward click selected (proving the reverse click actually changed
    something, not a stale no-op).

    Scope: the FULL demo root ('AGDLP-Demo') is loaded (not the 2-node
    DL_FS-Finance_RW scope the other scenarios use) - a non-trivial violations
    sidebar needs the AP 3.2 baseline's 19 findings. DemoProvider.LoadScopeAsync
    marks every in-scope group loaded eagerly, so this one root-load already
    yields the "full snapshot" the baseline is pinned against - no lazy-expand
    needed.

    State is HERMETIC (ADR-038 D3.1, WP5) AND channelled (ADR-038 D3.2, WP6):
    '--demo --state-dir <dir> --e2e'. Tier B (ADR-038 D2): actions are real
    UIA/posted WM_* input (Tier A) - the channel only GATES/ASSERTS via
    read-only 'state' polls, never invokes/clicks.

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
    $ArtifactDir = Join-Path $repoRoot ('artifacts\e2e\adhoc\selection-sync-{0:yyyyMMdd-HHmmss}' -f (Get-Date))
}
if (-not $AppExe) {
    $AppExe = Join-Path $repoRoot 'src\App\bin\Release\net8.0-windows\GroupWeaver.App.exe'
}
if (-not $StateDir) {
    $StateDir = Join-Path $env:TEMP ('gw-e2e\adhoc\selection-sync-{0:yyyyMMdd-HHmmss}' -f (Get-Date))
}

. (Join-Path (Split-Path -Parent $PSScriptRoot) 'lib\e2e-driver.ps1')

Initialize-E2eContext -ScenarioName 'selection-sync' -ArtifactDir $ArtifactDir
$runStart = Get-Date

# The FULL demo root (not the 2-node DL_FS-Finance_RW scope the other scenarios
# use) - the AP 3.2 baseline's 19 findings need the whole OU. Matches the root
# candidate's Name/Dn (RootPickerViewModel.FilteredCandidates substring match).
$filterText = 'AGDLP-Demo'

# GlobalGroup node palette (src/App/web/graph.js node[kind='GlobalGroup']):
# green triangle #107C10. Used BOTH as the render-confirmation blob (any GG_*
# node proves the full scope rendered) and as the forward-click target.
$colorGlobalGroup = @(16, 124, 16)

# The AP 3.2 demo baseline's canonical row 0 (.claude/rules/rule-engine.md /
# RuleEngineDemoBaselineTests.ExpectedBaseline[0]): the first nesting error,
# DL_FS-Finance_RO <- DL_Nested_RO (DL <- DL deny). ViolationRowModel.PrimaryDn
# = Dns[0] = the DL parent.
$expectedRow0Dn = 'CN=DL_FS-Finance_RO,OU=Groups,OU=AGDLP-Demo,DC=weavedemo,DC=example'
# Kind-consistency pattern for the FORWARD click's resulting DN (any GG_* group
# under the demo Groups OU) - proves a real, in-scope group was selected
# without needing to pin exactly WHICH one (multiple GG_* nodes share the same
# node color/shape, so Find-NodeBlob's densest-blob pick is not identity-exact).
$forwardDnPattern = '^CN=GG_[^,]+,OU=Groups,OU=AGDLP-Demo,DC=weavedemo,DC=example$'

# Hermetic state (ADR-038 D3.1, WP5): deterministic ui-state.json (rail expanded +
# dark theme) into the scenario's OWN state dir - never the real %APPDATA%.
[void](Initialize-E2eStateDir -StateDir $StateDir)

# --- button/row capture-coordinate map ------------------------------------------
# Canvas "+" zoom-in button (graph.js overlay toolbar, bottom-center of GraphHost;
# "Fit"/"+"/"-"/"Labels: auto"/"Issues only"). Calibrated against a checkpoint PNG
# taken at the fixed 60,60/1480x920 geometry (Start-E2EApp's SetWindowPos pin).
$PT_ZoomIn = @(606, 843)
# Violations sidebar, topmost row's jump button (ViolationsSidebarView.axaml): a
# point on the LEFT/CENTER of the row (severity glyph + message column), clear of
# the top-right "Why?" sibling button. Same calibration geometry as above.
$PT_SidebarRow0 = @(1150, 300)

$failed = $false
try {
    # --demo is MANDATORY with --state-dir/--e2e (both app-side demo gates); the
    # startup auto-connect lands directly on the root picker.
    [void](Start-E2EApp -ExePath $AppExe -AppArgs @('--demo') -StateDir $StateDir -E2e)
    Assert-Alive 'launch (window up)'

    Invoke-RootLoad -FilterText $filterText
    Assert-Alive 'demo connect + full-root load'

    Wait-ChromiumChild -TimeoutSec 60

    # Zoom in on the canvas before hunting a node blob: at the initial "Fit" view
    # the full 196-object AGDLP-Demo scope renders each node only a few px wide
    # (measured: ~3-4 matching pixels per GG_* triangle), well under Find-NodeBlob's
    # 30-pixel densest-cell floor. Six clicks on the canvas zoom-in overlay button
    # (index.html's zoom-in-btn, controlZoom(1.2) in graph.js - a DIFFERENT zoom
    # mechanism than record-demo-gif.ps1's wheel-based Send-CanvasWheel) reliably
    # grows the on-screen cluster enough for the color hunt below to land inside an
    # actual node shape.
    for ($i = 0; $i -lt 6; $i++) {
        Send-CanvasClick $PT_ZoomIn[0] $PT_ZoomIn[1] $false
        Start-Sleep -Milliseconds 200
    }
    Throw-IfCrashed 'canvas zoom-in'
    Capture-Checkpoint 'zoomed-in'

    # minX skips the KINDS legend's own green triangle swatch (top-left overlay, capture
    # x < ~426 at this window geometry) - the same always-present false-match class
    # record-demo-gif.ps1 and Find-NodeBlob's own doc comment warn about (#78).
    $ggBlob = Wait-NodeBlob { Save-Probe } $colorGlobalGroup 30 'a GlobalGroup node (render + findings signal)' 'canvas' 450
    Write-DriverLog 'workspace-rendered' @{ ggBlobX = $ggBlob.X; ggBlobY = $ggBlob.Y }
    Capture-Checkpoint 'workspace'
    Assert-Alive 'workspace render'
    [void](Wait-E2eStep -Expected 'Workspace' -TimeoutSec 15 -What 'initial workspace state')
    Wait-E2eSettled -TimeoutSec 10 -What 'initial workspace settle' | Out-Null

    # --- baseline: selection starts empty ------------------------------------------
    $initial = Wait-E2eState -TimeoutSec 10 -What 'pre-click selection state'
    if ($initial.selectedDn) {
        throw "ASSERT::pre-click - selectedDn is already '$($initial.selectedDn)', expected null before any click"
    }
    Write-DriverLog 'pre-click-selection-confirmed-empty' @{}

    # --- FORWARD: canvas tap -> selectedDn (ADR-018 D3 nodeClick) -------------------
    Send-CanvasClick $ggBlob.X $ggBlob.Y $false
    Throw-IfCrashed 'canvas click (forward selection)'
    $afterForward = Wait-E2eState -TimeoutSec 10 -What 'forward selection state'
    Assert-Alive 'forward selection click'
    Capture-Checkpoint 'forward-selected'

    if (-not $afterForward.selectedDn) {
        throw 'ASSERT::forward-selection - selectedDn is still null after a canvas click on a GlobalGroup node'
    }
    if ($afterForward.selectedDn -notmatch $forwardDnPattern) {
        throw "ASSERT::forward-selection - selectedDn '$($afterForward.selectedDn)' does not match the expected GG_* pattern '$forwardDnPattern'"
    }
    $forwardDn = [string]$afterForward.selectedDn
    Write-DriverLog 'forward-selection-confirmed' @{ selectedDn = $forwardDn }

    # --- REVERSE: sidebar row click -> selectedDn (ADR-020/#96) ---------------------
    Click-CapturePoint $PT_SidebarRow0[0] $PT_SidebarRow0[1] 'violations row 0 (jump)'
    Throw-IfCrashed 'sidebar row click (reverse selection)'
    $afterReverse = Wait-E2eState -TimeoutSec 10 -What 'reverse selection state'
    Assert-Alive 'reverse selection click'
    Capture-Checkpoint 'reverse-selected'

    if ("$($afterReverse.selectedDn)" -ne $expectedRow0Dn) {
        throw "ASSERT::reverse-selection - selectedDn is '$($afterReverse.selectedDn)', expected the known row-0 anchor '$expectedRow0Dn'"
    }
    if ([string]::Equals([string]$afterReverse.selectedDn, $forwardDn, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'ASSERT::reverse-selection - selectedDn did not change from the forward click (vacuous reverse-sync proof)'
    }
    Write-DriverLog 'reverse-selection-confirmed' @{ selectedDn = $afterReverse.selectedDn }

    # A jump also focuses the anchor (WorkspaceViewModel.JumpAsync); the settle wait
    # drains that camera move so a subsequent probe would not race it (none needed
    # here, but keeps the scenario's invariant pack timing honest).
    Wait-E2eSettled -TimeoutSec 10 -What 'post-jump camera settle' | Out-Null

    # --- cross-cutting invariant pack (ADR-038 D4) plus the trace-error scan --------
    Assert-NoUnexpectedDialogs 'end of journey'
    Invoke-CleanShutdownGate
    Assert-CleanStderr 'after shutdown'
    Assert-NoNewWerDumps -Since $runStart -Where 'end of scenario'
    Assert-E2eNoUnexpectedTraceErrors

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
