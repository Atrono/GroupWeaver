<#
.SYNOPSIS
    E2E scenario: drivable keyboard-shortcut sweep (ADR-038 WP6b, P1.4, issue #245).

.DESCRIPTION
    Proves the SUBSET of native/on-canvas keyboard shortcuts that a PostMessage-based
    driver (no interactive input desktop on this box) can actually exercise:

      - F11    - MainWindow.OnKeyDown toggles WindowState between Normal and
                 FullScreen (ADR-022 D1). Driven with Send-Key (targets MainWindow,
                 the native Avalonia gesture). Asserted STRUCTURALLY via
                 Get-WindowRectOf $app.MainWindowHandle: after the FIRST F11 the
                 window rect grows to the PRIMARY monitor's full pixel bounds
                 (GetSystemMetrics SM_CXSCREEN/SM_CYSCREEN - a small scenario-local
                 P/Invoke, nothing this shared elsewhere needs); after the SECOND
                 F11 the rect returns to the pre-toggle rect captured just before
                 the first press (not the original launch geometry - the two must
                 match each other, which they do regardless of exactly how Avalonia
                 restores WindowState.Normal).

      - F      - single-key focus-mode toggle (ADR-022 addendum), but ONLY handled
                 when DataContext is ShellViewModel and CurrentStep is
                 WorkspaceViewModel. Driven with Send-Key. ASSERTION DESIGN: rather
                 than a visual checkpoint diff (the plan's suggested fallback), this
                 uses a CHEAPER, more deterministic structural signal read directly
                 from source (WorkspaceView.axaml / MainWindow.axaml): entering focus
                 mode both hides the top strip (IsVisible="{Binding !IsFocusMode}")
                 AND collapses the rail column (ShellViewModel.ToggleFocusMode calls
                 workspace.SetRailCollapsed(true), which zeroes
                 WorkspaceViewModel.RailColumnWidth) - the GraphHost column is "*"
                 width, so the Chromium child HWND's own rect WIDENS the instant the
                 rail collapses, with no re-render/animation involved. Get-WindowRectOf
                 on the (already-resolved) Chromium child HWND before/after the 'F'
                 press is therefore a robust, cheap, non-visual proxy for "the rail
                 chrome is hidden" - checkpoints are still captured alongside as
                 secondary visual evidence, but are not the gate.

      - Esc    - ExitFullScreenAndFocus exits focus mode (same file). Driven with
                 Send-Key. Asserted as the REVERSE of the F check: the Chromium
                 child's rect narrows back to (approximately) the pre-F width.

      - +/-    - graph.js's OWN document-level keydown listener (~line 1554-1580):
                 a plain '+'/'=' key (no Ctrl/Cmd/Alt) calls controlZoom(1.2); a
                 plain '-' calls controlZoom(1/1.2). These are read by the WEB
                 BUNDLE, not Avalonia chrome, so MainWindow-targeted Send-Key never
                 reaches them - this is exactly why WP6b's new Send-CanvasKey exists
                 (tools/lib/webview-capture.ps1): identical to Send-Key but posts to
                 the Chromium child HWND. VK_OEM_PLUS (0xBB, unshifted -> '=') and
                 VK_OEM_MINUS (0xBD, unshifted -> '-') both match the listener's
                 checks WITHOUT any modifier key-STATE, sidestepping the general
                 "PostMessage can't establish a chord" limitation entirely for zoom.
                 Gated on state.zoom (the --e2e channel's stateProbe field)
                 increasing after '+' and decreasing after the following '-'.
                 DEVIATION found during implementation: Send-CanvasKey alone (with
                 no prior canvas interaction) never reached the listener - Chromium
                 gates keyboard delivery on the render widget believing it has
                 focus, which nothing earlier in the sweep (Avalonia-chrome-only
                 keys) had established. A single empty-background Send-CanvasClick
                 immediately before the zoom keys fixes this (see the inline
                 comment at that call site).

    EXPLICITLY OUT OF SCOPE (a real, unresolved product/tooling gap - not silently
    dropped): Ctrl+B (rail collapse), Ctrl+K / Ctrl+F (command palette), Ctrl+0 (fit
    view). All four require real modifier key-STATE (GetAsyncKeyState at the OS
    input-translation layer) that a PostMessage-only WM_KEYDOWN cannot establish,
    and this box has no interactive input desktop to set it another way (SetCursorPos
    fails, OpenInputDesktop denied - lab-environment.md). Driving them would need
    either an input-desktop-attachment investigation or a non-keyboard (mouse-
    clickable) affordance for each - out of scope for this scenario.

    Scope: 'DL_FS-Finance_RW' (the same 2-node scope step-swap-churn.ps1 uses) - no
    findings/violations needed, just a rendered workspace with a canvas to zoom.

    State is HERMETIC (ADR-038 D3.1, WP5) AND channelled (ADR-038 D3.2, WP6):
    '--demo --state-dir <dir> --e2e'. Tier B (ADR-038 D2): actions are real posted
    WM_* input (Tier A) - the channel only GATES/ASSERTS via read-only 'state'
    polls, never invokes/clicks.

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
    $ArtifactDir = Join-Path $repoRoot ('artifacts\e2e\adhoc\keyboard-sweep-{0:yyyyMMdd-HHmmss}' -f (Get-Date))
}
if (-not $AppExe) {
    $AppExe = Join-Path $repoRoot 'src\App\bin\Release\net8.0-windows\GroupWeaver.App.exe'
}
if (-not $StateDir) {
    $StateDir = Join-Path $env:TEMP ('gw-e2e\adhoc\keyboard-sweep-{0:yyyyMMdd-HHmmss}' -f (Get-Date))
}

. (Join-Path (Split-Path -Parent $PSScriptRoot) 'lib\e2e-driver.ps1')

# Scenario-local P/Invoke: the primary monitor's pixel bounds, for the F11
# full-screen assertion (nothing else in the shared libs needs this). Guarded
# the same way webview-capture.ps1 guards its own Add-Type (a second dot-source
# of this file within one process would otherwise throw on redefine).
if (-not ('GroupWeaver.KeyboardSweepNative' -as [type])) {
    Add-Type -Namespace GroupWeaver -Name KeyboardSweepNative -MemberDefinition @'
[DllImport("user32.dll")]
public static extern int GetSystemMetrics(int index);
'@
}
# SM_CXSCREEN = 0, SM_CYSCREEN = 1 (primary monitor, physical pixels - this
# thread is already per-monitor-DPI-aware via webview-capture.ps1's dot-source-
# time SetThreadDpiAwarenessContext(-4)).
$screenWidth = [GroupWeaver.KeyboardSweepNative]::GetSystemMetrics(0)
$screenHeight = [GroupWeaver.KeyboardSweepNative]::GetSystemMetrics(1)

# Virtual-key codes (see .DESCRIPTION for why each was picked).
$VK_F11 = 0x7A
$VK_F = 0x46
$VK_ESCAPE = 0x1B
$VK_OEM_PLUS = 0xBB   # unshifted -> '=' (matches graph.js's '+'/'=' check)
$VK_OEM_MINUS = 0xBD  # unshifted -> '-'

Initialize-E2eContext -ScenarioName 'keyboard-sweep' -ArtifactDir $ArtifactDir
$runStart = Get-Date

# Same 2-node scope every navigation-only scenario uses - no findings needed.
$filterText = 'DL_FS-Finance_RW'
$colorExternalNode = @(49, 85, 115)

# Hermetic state (ADR-038 D3.1, WP5): deterministic ui-state.json (rail expanded +
# dark theme) into the scenario's OWN state dir - never the real %APPDATA%.
[void](Initialize-E2eStateDir -StateDir $StateDir)

# Bounded poll on a WM_* window's rect until $Predicate is satisfied - the same
# 200ms-cadence bounded-wait shape as Wait-Uia/Wait-NodeBlob, kept scenario-local
# since this is the only scenario that gates on raw window geometry.
function Wait-RectMatch {
    param(
        [Parameter(Mandatory)][IntPtr]$Hwnd,
        [Parameter(Mandatory)][scriptblock]$Predicate,
        [int]$TimeoutSec = 8,
        [Parameter(Mandatory)][string]$What
    )
    $deadline = (Get-Date).AddSeconds($TimeoutSec)
    while ($true) {
        $rect = Get-WindowRectOf $Hwnd
        if (& $Predicate $rect) { return $rect }
        if ((Get-Date) -gt $deadline) { throw "ASSERT::keyboard-sweep - timed out after ${TimeoutSec}s waiting for $What" }
        Start-Sleep -Milliseconds 200
    }
}

$failed = $false
try {
    # --demo is MANDATORY with --state-dir/--e2e (both app-side demo gates); the
    # startup auto-connect lands directly on the root picker.
    [void](Start-E2EApp -ExePath $AppExe -AppArgs @('--demo') -StateDir $StateDir -E2e)
    Assert-Alive 'launch (window up)'

    Invoke-RootLoad -FilterText $filterText
    Assert-Alive 'demo connect + root load'

    Wait-ChromiumChild -TimeoutSec 60
    [void](Wait-NodeBlob { Save-Probe } $colorExternalNode 30 'the external frontier node (render signal)')
    Write-DriverLog 'workspace-rendered' @{}
    Capture-Checkpoint 'workspace'
    Assert-Alive 'workspace render'
    [void](Wait-E2eStep -Expected 'Workspace' -TimeoutSec 15 -What 'initial workspace state')
    Wait-E2eSettled -TimeoutSec 10 -What 'initial workspace settle' | Out-Null

    # --- F11 (x2): full-screen toggle, native MainWindow gesture ---------------------
    $preFullScreen = Get-WindowRectOf $app.MainWindowHandle
    Write-DriverLog 'f11-pre-toggle-rect' @{
        left = $preFullScreen.Left; top = $preFullScreen.Top
        width = ($preFullScreen.Right - $preFullScreen.Left); height = ($preFullScreen.Bottom - $preFullScreen.Top)
    }

    Send-Key $VK_F11
    Throw-IfCrashed 'F11 (enter full-screen)'
    $tol = 3
    $fullScreenRect = Wait-RectMatch -Hwnd $app.MainWindowHandle -TimeoutSec 8 -What 'window rect to grow to the screen bounds (F11 enter)' -Predicate {
        param($r)
        ([Math]::Abs(($r.Right - $r.Left) - $screenWidth) -le $tol) -and ([Math]::Abs(($r.Bottom - $r.Top) - $screenHeight) -le $tol)
    }
    Assert-Alive 'F11 enter full-screen'
    Capture-Checkpoint 'f11-fullscreen'
    Write-DriverLog 'f11-fullscreen-confirmed' @{
        width = ($fullScreenRect.Right - $fullScreenRect.Left); height = ($fullScreenRect.Bottom - $fullScreenRect.Top)
        screenWidth = $screenWidth; screenHeight = $screenHeight
    }

    Send-Key $VK_F11
    Throw-IfCrashed 'F11 (exit full-screen)'
    $restoredRect = Wait-RectMatch -Hwnd $app.MainWindowHandle -TimeoutSec 8 -What 'window rect to return to the pre-toggle rect (F11 exit)' -Predicate {
        param($r)
        ([Math]::Abs($r.Left - $preFullScreen.Left) -le $tol) -and ([Math]::Abs($r.Top - $preFullScreen.Top) -le $tol) -and
        ([Math]::Abs(($r.Right - $r.Left) - ($preFullScreen.Right - $preFullScreen.Left)) -le $tol) -and
        ([Math]::Abs(($r.Bottom - $r.Top) - ($preFullScreen.Bottom - $preFullScreen.Top)) -le $tol)
    }
    Assert-Alive 'F11 exit full-screen'
    Capture-Checkpoint 'f11-restored'
    Write-DriverLog 'f11-restore-confirmed' @{
        left = $restoredRect.Left; top = $restoredRect.Top
        width = ($restoredRect.Right - $restoredRect.Left); height = ($restoredRect.Bottom - $restoredRect.Top)
    }

    # --- F: focus-mode toggle (Workspace-only) - structural rail-collapse signal ----
    # $chromiumHwnd is the SAME handle across this (no step swap, just a resize) -
    # re-querying its rect directly is enough, no Update-ChromiumHwnd needed.
    $preFocus = Get-WindowRectOf $chromiumHwnd
    $preFocusWidth = $preFocus.Right - $preFocus.Left
    Write-DriverLog 'focus-pre-rect' @{ width = $preFocusWidth }

    Send-Key $VK_F
    Throw-IfCrashed "F (enter focus mode)"
    # Rail collapse frees its whole column to GraphHost ("*"); require a
    # comfortably-more-than-noise growth so this can't false-positive on a
    # sub-pixel layout jitter.
    $focusRect = Wait-RectMatch -Hwnd $chromiumHwnd -TimeoutSec 5 -What 'canvas to widen after F (rail collapse)' -Predicate {
        param($r) ($r.Right - $r.Left) -gt ($preFocusWidth + 50)
    }
    Assert-Alive 'F enter focus mode'
    Capture-Checkpoint 'focus-mode-on'
    $focusWidth = $focusRect.Right - $focusRect.Left
    Write-DriverLog 'focus-mode-confirmed' @{ preWidth = $preFocusWidth; postWidth = $focusWidth }

    # --- Esc: exits focus mode - reverse of the F check -----------------------------
    Send-Key $VK_ESCAPE
    Throw-IfCrashed 'Esc (exit focus mode)'
    $unfocusRect = Wait-RectMatch -Hwnd $chromiumHwnd -TimeoutSec 5 -What 'canvas to narrow after Esc (rail restored)' -Predicate {
        param($r) [Math]::Abs(($r.Right - $r.Left) - $preFocusWidth) -le 5
    }
    Assert-Alive 'Esc exit focus mode'
    Capture-Checkpoint 'focus-mode-off'
    Write-DriverLog 'focus-mode-exit-confirmed' @{ width = ($unfocusRect.Right - $unfocusRect.Left) }

    # --- +/- : on-canvas zoom via the NEW Send-CanvasKey, gated on state.zoom -------
    # DEVIATION (found during implementation): a bare Send-CanvasKey with no prior
    # canvas interaction does NOT reach graph.js's keydown listener - the first
    # attempt below reproduced with zero zoom change. Chromium's render-widget-host
    # gates keyboard delivery on the widget believing it HAS focus (ordinary browser
    # behavior for an unfocused view), and nothing before this point has ever given
    # the Chromium child real Win32/Chromium-internal focus (only Avalonia chrome
    # and MainWindow-targeted keys were used so far). A single background click
    # (empty canvas, well clear of both nodes at the initial Fit layout) is enough
    # to establish that focus - same technique Send-CanvasClick already uses
    # elsewhere, just needed here as a precondition rather than an assertion target.
    Send-CanvasClick 700 150 $false
    Throw-IfCrashed 'canvas background click (establish focus for zoom keys)'
    Wait-E2eSettled -TimeoutSec 10 -What 'pre-zoom settle' | Out-Null
    $beforeZoom = Wait-E2eState -TimeoutSec 10 -What 'pre-zoom state'
    if ($null -eq $beforeZoom.zoom) { throw 'ASSERT::keyboard-sweep - state.zoom is null before any zoom key (renderer not live?)' }
    Write-DriverLog 'pre-zoom-state' @{ zoom = $beforeZoom.zoom }

    Send-CanvasKey $VK_OEM_PLUS
    Throw-IfCrashed "'+' (canvas zoom in)"
    Wait-E2eSettled -TimeoutSec 10 -What 'post zoom-in settle' | Out-Null
    $afterPlus = Wait-E2eState -TimeoutSec 10 -What 'post zoom-in state'
    Assert-Alive "'+' canvas zoom in"
    if (-not ([double]$afterPlus.zoom -gt [double]$beforeZoom.zoom)) {
        throw "ASSERT::keyboard-sweep - state.zoom did not increase after '+' (before=$($beforeZoom.zoom) after=$($afterPlus.zoom))"
    }
    Write-DriverLog 'zoom-in-confirmed' @{ before = $beforeZoom.zoom; after = $afterPlus.zoom }

    Send-CanvasKey $VK_OEM_MINUS
    Throw-IfCrashed "'-' (canvas zoom out)"
    Wait-E2eSettled -TimeoutSec 10 -What 'post zoom-out settle' | Out-Null
    $afterMinus = Wait-E2eState -TimeoutSec 10 -What 'post zoom-out state'
    Assert-Alive "'-' canvas zoom out"
    if (-not ([double]$afterMinus.zoom -lt [double]$afterPlus.zoom)) {
        throw "ASSERT::keyboard-sweep - state.zoom did not decrease after '-' (before=$($afterPlus.zoom) after=$($afterMinus.zoom))"
    }
    Write-DriverLog 'zoom-out-confirmed' @{ before = $afterPlus.zoom; after = $afterMinus.zoom }
    Capture-Checkpoint 'zoom-sweep-done'

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
