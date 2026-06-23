<#
.SYNOPSIS
    Windowed --demo smoke for Back navigation between graph-bearing shell steps.
    Originally the reproduce-first harness for the Back-navigation WebView2 crash
    (#120 / ADR-024); now ALSO asserts viewport preservation (#122 / ADR-025).
    Drives the live app from the workspace (Ist) graph into Plan mode and back, and
    through the Plan->Gap round-trip, asserting (a) the process stays alive after
    every step swap, and (b) the Chrome_RenderWidgetHostHWND is UNCHANGED across each
    Back into a parked step - proof the live page + cytoscape viewport survived
    (parking-lot reparent) rather than being destroyed and re-rendered.

.DESCRIPTION
    The confirmed bug: switching the shell's CurrentStep (Workspace -> Plan -> Gap
    and back) tears down the leaving step's view and mounts the entering step's view.
    Every step builds its OWN IGraphRenderer with its OWN NativeWebView (WebView2),
    so a step swap DETACHES one WebView2 native control from the visual tree and
    re-attaches another (and, on Back, re-attaches a previously-detached one). If the
    renderer doesn't survive that detach/re-attach, the app crashes.

    This harness is DIAGNOSTIC and READ-ONLY w.r.t. src/: it builds + runs the app
    and posts input, but changes no production code. It launches the app WITHOUT
    --demo and clicks "Demo mode" via UIA (so only demo data is ever touched), drives
    to a rendered graph, then exercises:

      A. Design plan -> (confirm Plan step) -> Back              [primary crash path]
      B. Design plan -> Gap analysis -> Back -> Back             [the full round-trip]

    After each step swap it calls $app.Refresh() and asserts -not $app.HasExited.
    The first swap that exits the process IS the crash: the script prints the exit
    code and dumps the captured stderr/stdout (the unhandled-exception text), then
    exits 1. If every swap survives, it prints PASS and exits 0.

    Driving technique (.claude/rules/lab-environment.md "Windowed-smoke driving"):
    this agent context has no interactive input desktop, so Avalonia chrome is driven
    via UIA InvokePattern/SelectionItemPattern/ValuePattern and the WebView canvas via
    WM_* posted to the Chrome_RenderWidgetHostHWND child. Crucially, once that Chromium
    child HWND exists, UIA descendant queries on the window return ONLY Chromium content
    (the Avalonia rail buttons vanish from the UIA tree). So "Design plan" / "Gap
    analysis" / "Back" are driven by posting WM_LBUTTONDOWN/UP to the MAIN-window HWND
    at the button's rail rect, located by reading the button's UIA BoundingRectangle
    BEFORE the click (the element is still queryable by a targeted FindFirst even when a
    blanket descendants walk returns Chromium - and we fall back to a fresh query each
    time). The graph-rendered gate is the same pixel-hunt the demo recorder uses.

    Runs under Windows PowerShell 5.1 (UIAutomationClient is a .NET Framework GAC
    assembly pwsh/.NET 8 cannot load); invoking with pwsh relaunches itself. Builds
    the app if the exe is missing, always closes/kills the app in finally. Artifacts
    (screenshots + redirected app stdout/stderr) under artifacts/ui/smoke/ (gitignored).

.EXAMPLE
    pwsh tools/smoke-back-nav.ps1
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'

# --- relaunch under Windows PowerShell 5.1 when started from pwsh -------------
if ($PSVersionTable.PSEdition -eq 'Core') {
    $ps51 = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
    $argList = @('-NoProfile', '-NonInteractive', '-ExecutionPolicy', 'Bypass', '-File', $PSCommandPath)
    & $ps51 @argList
    exit $LASTEXITCODE
}

Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.Drawing

# Shared P/Invoke + input + hunt + UIA helpers (DPI awareness initialised on dot-source).
. (Join-Path $PSScriptRoot 'lib\webview-capture.ps1')

# ScreenToClient is not in the shared lib; add it here (this harness needs to post a
# WM_LBUTTON to the MAIN window in CLIENT coords from a capture/window-rect-relative point,
# whereas the lib's Send-CanvasClick only ever targets the Chromium CHILD). One-time guarded.
if (-not ('GroupWeaver.SmokeBackNav' -as [type])) {
    Add-Type -Namespace GroupWeaver -Name SmokeBackNav -MemberDefinition @'
[StructLayout(LayoutKind.Sequential)]
public struct POINT { public int X, Y; }
[DllImport("user32.dll")]
public static extern bool ScreenToClient(IntPtr hWnd, ref POINT pt);

// #122 / ADR-025: with the parking-lot host, a HIDDEN parked WebView's
// Chrome_RenderWidgetHostHWND COEXISTS with the visible step's child, so the lib's
// FindDescendantByClass (returns the FIRST match) is ambiguous. Pick the LARGEST
// IsWindowVisible match instead - the on-screen canvas; a parked child is hidden /
// zero-size (the ParkingLot Panel is IsVisible=false + 0x0), so it never wins.
[StructLayout(LayoutKind.Sequential)]
public struct RECT2 { public int Left, Top, Right, Bottom; }
[DllImport("user32.dll")] private static extern bool EnumChildWindows(IntPtr parent, EnumProc cb, IntPtr l);
[DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr h, System.Text.StringBuilder s, int max);
[DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr h);
[DllImport("user32.dll", SetLastError = true)] private static extern bool GetWindowRect(IntPtr h, out RECT2 r);
public delegate bool EnumProc(IntPtr h, IntPtr l);
private static IntPtr _best; private static long _bestArea; private static string _want;
private static bool Pick(IntPtr h, IntPtr l) {
    var sb = new System.Text.StringBuilder(256);
    GetClassName(h, sb, sb.Capacity);
    if (sb.ToString() == _want && IsWindowVisible(h)) {
        RECT2 r; GetWindowRect(h, out r);
        long area = (long)(r.Right - r.Left) * (r.Bottom - r.Top);
        if (area > _bestArea) { _bestArea = area; _best = h; }
    }
    return true; // keep enumerating ALL matches, not just the first
}
public static IntPtr FindLargestVisibleDescendant(IntPtr parent, string cls) {
    _best = IntPtr.Zero; _bestArea = 0; _want = cls;
    EnumChildWindows(parent, Pick, IntPtr.Zero);
    return _best;
}
'@
}

$repoRoot = Split-Path -Parent $PSScriptRoot
$smokeDir = Join-Path $repoRoot 'artifacts\ui\smoke'
$exe = Join-Path $repoRoot 'src\App\bin\Debug\net8.0-windows\GroupWeaver.App.exe'
$stderrFile = Join-Path $smokeDir 'app-stderr.txt'
$stdoutFile = Join-Path $smokeDir 'app-stdout.txt'

# Proven render-confirmation root + its rendered node colors, identical to
# record-demo-gif.ps1 / capture-motion.ps1 (the rust DL root blends on the dark canvas;
# the blue External frontier node is the never-occluded render signal). The crash is
# about the step-swap renderer detach, independent of node count, so the proven 2-node
# root keeps the "graph rendered" gate reliable.
$filterText = 'DL_FS-Finance_RW'
$colorRoot = @(136, 94, 69)
$colorExternalNode = @(49, 85, 115)

function Log([string]$msg) { Write-Host "[smoke-back-nav $(Get-Date -Format HH:mm:ss)] $msg" }

# --- single-shot DPI-aware probe capture (in-process; for the pixel-hunt gate) ---
# PrintWindow on the WebView2 layer lags one compositor batch (lab-environment.md):
# the first capture after a mutation returns the previous frame, so capture-and-discard
# then keep the second (the two PrintWindow calls are the settle).
$script:probePath = $null
function Save-Probe {
    if (-not $script:probePath) { $script:probePath = Join-Path $smokeDir '_probe.png' }
    [void][GroupWeaver.WebViewCapture]::CaptureBurst($app.MainWindowHandle, $smokeDir, 1, 0)
    Move-Item -Force (Join-Path $smokeDir 'frame_000.png') $script:probePath
    [void][GroupWeaver.WebViewCapture]::CaptureBurst($app.MainWindowHandle, $smokeDir, 1, 0)
    Move-Item -Force (Join-Path $smokeDir 'frame_000.png') $script:probePath
    return $script:probePath
}

# Raises the CRASH sentinel (handled in the catch as the crash, not infra) if the app has
# exited - so a delayed unhandled-exception crash on the next render/layout pass after a
# Back click is attributed to that step, not to a downstream capture/confirm helper that
# happened to run first and tripped over the now-dead process handle.
function Throw-IfCrashed([string]$afterWhat) {
    $app.Refresh()
    if ($app.HasExited) {
        Log "CRASH: app exited after '$afterWhat' (exit code $($app.ExitCode))"
        Write-Host ''
        Write-Host '================= app-stderr.txt ================='
        if (Test-Path $stderrFile) { Get-Content $stderrFile -Raw } else { Write-Host '(no stderr file)' }
        Write-Host '================= app-stdout.txt ================='
        if (Test-Path $stdoutFile) { Get-Content $stdoutFile -Raw } else { Write-Host '(no stdout file)' }
        Write-Host '================================================='
        throw "CRASH_AFTER::$afterWhat"
    }
}

function Capture-Shot([string]$name) {
    $app.Refresh()
    if ($app.HasExited) { Log "skip capture '$name' (app already exited)"; return }
    $path = Join-Path $smokeDir $name
    & powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass `
        -File (Join-Path $PSScriptRoot 'capture-window.ps1') -ProcessId $app.Id -OutFile $path | Out-Null
    Log "captured $name"
}

# --- liveness assertion -------------------------------------------------------
# The single source of truth for "did the step swap crash the app?". Refreshes the
# Process, and if it exited, dumps the redirected stderr/stdout (the unhandled
# exception) and signals the caller to fail.
function Assert-Alive([string]$afterWhat) {
    Start-Sleep -Milliseconds 600   # let the swap + any async teardown settle / fault
    $app.Refresh()
    if ($app.HasExited) {
        Log "CRASH: app exited after '$afterWhat' (exit code $($app.ExitCode))"
        Write-Host ''
        Write-Host '================= app-stderr.txt ================='
        if (Test-Path $stderrFile) { Get-Content $stderrFile -Raw } else { Write-Host '(no stderr file)' }
        Write-Host '================= app-stdout.txt ================='
        if (Test-Path $stdoutFile) { Get-Content $stdoutFile -Raw } else { Write-Host '(no stdout file)' }
        Write-Host '================================================='
        throw "CRASH_AFTER::$afterWhat"
    }
    Log "alive after '$afterWhat' (exit code: still running)"
}

# --- force the right rail EXPANDED before launch (deterministic) ----------------
# The rail collapse state is PERSISTED to %APPDATA%\GroupWeaver\ui-state.json
# (UiStateStore, ADR-022 D4) and seeded at WorkspaceViewModel construction. On this box
# prior sessions (focus-mode / demo recorder) left RailCollapsed=true, so the "Design
# plan" button - which lives INSIDE the rail (IsVisible bound to !IsRailCollapsed) -
# genuinely does not exist, and the tiny seam toggle pill does not reliably take a posted
# WM click (the WebView2 native child wins the airspace at the seam). Rather than fight
# that, we write RailCollapsed=false to the app's own persistence file BEFORE launch so
# the rail starts EXPANDED - the UiState.Default + fresh-box state. UIA is reachable after
# the WebView mounts (verified), so Invoke-UiaButton 'Design plan' then just works. This
# touches only the app's user-state file (not src/); we restore the prior content in the
# finally block so the box is left as we found it.
$script:uiStatePath = Join-Path ([Environment]::GetFolderPath('ApplicationData')) 'GroupWeaver\ui-state.json'
$script:uiStateBackup = $null
function Set-RailExpanded {
    $dir = Split-Path -Parent $script:uiStatePath
    if (-not (Test-Path $dir)) { New-Item -ItemType Directory -Force $dir | Out-Null }
    if (Test-Path $script:uiStatePath) {
        $script:uiStateBackup = Get-Content -Raw $script:uiStatePath
    }
    # Match UiState's shape (RailWidth/RailCollapsed); RailCollapsed=false = expanded.
    @{ RailWidth = 340; RailCollapsed = $false } | ConvertTo-Json | Set-Content -Encoding UTF8 $script:uiStatePath
    Log "forced rail EXPANDED via $($script:uiStatePath) (RailCollapsed=false)"
}
function Restore-RailState {
    if ($null -ne $script:uiStateBackup) {
        Set-Content -Encoding UTF8 $script:uiStatePath $script:uiStateBackup
        Log 'restored prior ui-state.json'
    }
}

# --- click an Avalonia chrome button by POSTED WM at a capture-coordinate point --
# CRITICAL lab fact, re-confirmed here (session 26): once the Chrome_RenderWidgetHostHWND
# child exists, UIA descendant queries on the window return ONLY Chromium content - the
# Avalonia RAIL buttons (Design plan / Reload scope / ...) and the Plan/Gap editor-column
# buttons VANISH from the UIA tree (FindAll returns only the 6 in-WebView/top-strip
# buttons). So they CANNOT be Invoke()d via UIA. They are driven by posting WM_LBUTTON to
# the MAIN-window HWND at the button's pixel location instead (the airspace-safe path: the
# rail/editor buttons sit BESIDE the WebView, never over it, so a posted main-window click
# lands on Avalonia, not the Chromium child). Coordinates are CAPTURE coords = main-window
# RECT-relative (PrintWindow PNG origin), converted to CLIENT coords via ScreenToClient
# (screen = windowRect.TopLeft + capturePoint; the title bar/border offset is exact, not
# guessed). Geometry is deterministic for the fixed 60,60 / clamped-min window.
function Click-CapturePoint([int]$captureX, [int]$captureY, [string]$label) {
    $mainRect = Get-WindowRectOf $app.MainWindowHandle
    $pt = New-Object GroupWeaver.SmokeBackNav+POINT
    $pt.X = $mainRect.Left + $captureX
    $pt.Y = $mainRect.Top + $captureY
    [void][GroupWeaver.SmokeBackNav]::ScreenToClient($app.MainWindowHandle, [ref]$pt)
    $lp = [GroupWeaver.WebViewCapture]::MakeLParam($pt.X, $pt.Y)
    $h = $app.MainWindowHandle
    [void][GroupWeaver.WebViewCapture]::PostMessage($h, [GroupWeaver.WebViewCapture]::WM_MOUSEMOVE, [IntPtr]::Zero, $lp)
    Start-Sleep -Milliseconds 80
    [void][GroupWeaver.WebViewCapture]::PostMessage($h, [GroupWeaver.WebViewCapture]::WM_LBUTTONDOWN, [IntPtr]1, $lp)
    [void][GroupWeaver.WebViewCapture]::PostMessage($h, [GroupWeaver.WebViewCapture]::WM_LBUTTONUP, [IntPtr]::Zero, $lp)
    Log "posted WM_LBUTTON to '$label' at capture ($captureX,$captureY) -> client ($($pt.X),$($pt.Y))"
}

# --- confirm a step swap landed by a pixel signal (UIA is blind post-WebView) ---
# We can't read button text once Chromium owns the UIA tree, so confirm step swaps by the
# CANVAS content: the Workspace (Ist) scope renders the 2-node graph whose blue External
# frontier node is the proven render signal; a freshly-entered Plan step starts with an
# EMPTY canvas (no nodes -> no External blob). So:
#   * On the WORKSPACE: the blue External blob IS present.
#   * On a fresh PLAN: the blue External blob is ABSENT (empty plan).
# This nails the load-bearing assertion (Back actually returned to the rendered Workspace,
# not just "app still alive"). Polled with the lag-fixed Save-Probe.
# A step swap DESTROYS the leaving step's Chrome_RenderWidgetHostHWND and the entering
# step mounts a FRESH one (NativeControlHost recreate), so the $chromiumHwnd resolved at
# launch goes stale after every swap - Find-NodeBlob's region math then trips
# "GetWindowRect failed for <deadHwnd>". Re-resolve the LIVE child before each blob check.
#   * #122 / ADR-025: with the parking-lot host, a leaving Back-target step's WebView is
#     PARKED (kept alive, hidden) rather than destroyed - so its Chrome_RenderWidgetHostHWND
#     COEXISTS with the entering step's child. Re-resolve the LARGEST VISIBLE child (the
#     on-screen canvas), not merely the first match, or the region math could lock onto a
#     hidden parked child. (Forward swaps still mount a fresh child for the entering step.)
function Update-ChromiumHwnd {
    $deadline = (Get-Date).AddSeconds(10)
    while ($true) {
        $app.Refresh()
        if ($app.HasExited) { throw "CRASH_AFTER::(detected re-resolving Chromium child) app exited" }
        $h = [GroupWeaver.SmokeBackNav]::FindLargestVisibleDescendant($app.MainWindowHandle, 'Chrome_RenderWidgetHostHWND')
        if ($h -eq [IntPtr]::Zero) {
            # Fallback: no VISIBLE match yet (mid-swap) - take any match so the pixel gate can retry.
            $h = [GroupWeaver.WebViewCapture]::FindDescendantByClass($app.MainWindowHandle, 'Chrome_RenderWidgetHostHWND')
        }
        if ($h -ne [IntPtr]::Zero) { $script:chromiumHwnd = $h; return }
        if ((Get-Date) -gt $deadline) { throw 'Chrome_RenderWidgetHostHWND not found after swap (re-resolve)' }
        Start-Sleep -Milliseconds 300
    }
}

# --- #122 / ADR-025: viewport-preservation assertion (the real-WebView2 acceptance proof) ---
# The on-screen canvas's Chrome_RenderWidgetHostHWND. With parking, a Back into a parked step
# returns the SAME child handle (the live page + cytoscape viewport survived); the pre-#122
# re-render destroyed+recreated it (a different handle, viewport re-fit). Same HWND across Back
# is the decisive proof that the page never reloaded - hence the zoom/pan are exactly as left
# (there is no code path that resets the viewport WITHOUT a reload).
function Get-VisibleChromiumHwnd {
    return [GroupWeaver.SmokeBackNav]::FindLargestVisibleDescendant($app.MainWindowHandle, 'Chrome_RenderWidgetHostHWND')
}
function Assert-SameHwnd($before, $after, [string]$what) {
    if ($before -eq [IntPtr]::Zero -or $after -eq [IntPtr]::Zero) {
        throw "HWND_IDENTITY::$what - could not resolve a visible Chromium child (before=$before after=$after)"
    }
    if ($before -ne $after) {
        throw "HWND_IDENTITY::$what - child HWND CHANGED across Back ($before -> $after): surface re-created, viewport NOT preserved"
    }
    Log "VIEWPORT-PRESERVED: '$what' kept the same Chrome_RenderWidgetHostHWND across Back ($before)"
}
function Test-ExternalBlob {
    $app.Refresh()
    if ($app.HasExited) { throw "CRASH_AFTER::(detected during pixel-confirm) app exited" }
    Update-ChromiumHwnd   # the previous step's child HWND is dead after the swap; refresh it
    $blob = Find-NodeBlob (Save-Probe) $colorExternalNode 'canvas' 30
    return [bool]$blob
}
function Confirm-Workspace([string]$where, [int]$timeoutSec = 15) {
    $deadline = (Get-Date).AddSeconds($timeoutSec)
    while ($true) {
        if (Test-ExternalBlob) { Log "confirmed WORKSPACE canvas after '$where' (External node present)"; return }
        if ((Get-Date) -gt $deadline) { throw "step-swap confirm FAILED: workspace graph not rendered after '$where' (no External node)" }
        Start-Sleep -Milliseconds 400
    }
}
function Confirm-Plan([string]$where, [int]$timeoutSec = 12) {
    $deadline = (Get-Date).AddSeconds($timeoutSec)
    while ($true) {
        if (-not (Test-ExternalBlob)) { Log "confirmed PLAN canvas after '$where' (empty - no External node)"; return }
        if ((Get-Date) -gt $deadline) { throw "step-swap confirm FAILED: still on workspace after '$where' (External node still present - click missed?)" }
        Start-Sleep -Milliseconds 400
    }
}

# Settle delay between a click and the next capture/click (lets the new view's WebView
# mount + layout settle); the Confirm-* helpers do the real verification.
function Wait-Step([string]$what, [int]$ms = 1200) {
    Start-Sleep -Milliseconds $ms
    Log "settled after '$what'"
}

# === build + launch ==============================================================
if (-not (Test-Path $exe)) {
    Log 'App binary missing - building src/App (Debug)...'
    & dotnet build (Join-Path $repoRoot 'src\App') -c Debug --nologo -v quiet
    if ($LASTEXITCODE -ne 0) { throw "dotnet build failed (exit $LASTEXITCODE)" }
}

if (Test-Path $smokeDir) { Remove-Item -Recurse -Force $smokeDir }
New-Item -ItemType Directory -Force $smokeDir | Out-Null

# Launch WITHOUT --demo (UIA-click Demo mode below, so the connect card with the live
# operator identity is never captured). Redirect stdout+stderr to files so an unhandled
# .NET exception is captured verbatim - this is the key deliverable.
# Force the rail expanded BEFORE launch so the in-rail "Design plan" button exists.
Set-RailExpanded

Log 'Launching GroupWeaver (no args - demo chosen via UIA; stdout/stderr redirected)...'
$app = Start-Process -FilePath $exe -PassThru `
    -RedirectStandardError $stderrFile -RedirectStandardOutput $stdoutFile

$crashed = $false
try {
    $deadline = (Get-Date).AddSeconds(30)
    while ($app.MainWindowHandle -eq [IntPtr]::Zero) {
        if ($app.HasExited) { throw "app exited during startup (exit $($app.ExitCode)) before a window appeared" }
        if ((Get-Date) -gt $deadline) { throw 'main window never appeared' }
        Start-Sleep -Milliseconds 250
        $app.Refresh()
    }

    # Deterministic geometry (same request as the media recorders; Avalonia clamps up to
    # its logical Min). SWP_NOZORDER|SWP_NOACTIVATE = 0x14 - posted input needs no activation.
    [void][GroupWeaver.WebViewCapture]::SetWindowPos($app.MainWindowHandle, [IntPtr]::Zero, 60, 60, 1480, 920, 0x14)

    # --- Demo mode (off camera) -> root picker -> filter -> select -> Load -------
    [void](Wait-Uia { Find-UiaFirst ([System.Windows.Automation.ControlType]::Button) 'Demo mode' } 30 "button 'Demo mode'")
    Start-Sleep -Milliseconds 500
    Invoke-UiaButton 'Demo mode'

    [void](Wait-Uia { Find-UiaFirst ([System.Windows.Automation.ControlType]::ListItem) $null } 30 'root candidates')
    Start-Sleep -Milliseconds 300

    $filterBox = Wait-Uia { Find-UiaFirst ([System.Windows.Automation.ControlType]::Edit) $null } 10 'filter box'
    $valuePattern = $null
    if ($filterBox.TryGetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern, [ref]$valuePattern)) {
        $valuePattern.SetValue($filterText)
    }
    else {
        $filterBox.SetFocus()
        Start-Sleep -Milliseconds 200
        foreach ($ch in $filterText.ToCharArray()) {
            [void][GroupWeaver.WebViewCapture]::PostMessage($app.MainWindowHandle, [GroupWeaver.WebViewCapture]::WM_CHAR, [IntPtr][int]$ch, [IntPtr]::Zero)
            Start-Sleep -Milliseconds 15
        }
    }
    Log "filtered root picker to '$filterText'"
    Start-Sleep -Milliseconds 500

    $item = Wait-Uia { Find-UiaFirst ([System.Windows.Automation.ControlType]::ListItem) $null } 10 'filtered candidate'
    $item.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
    Start-Sleep -Milliseconds 300
    Invoke-UiaButton 'Load'

    # --- wait for the workspace graph to render (Chromium child + node blob) ------
    $deadline = (Get-Date).AddSeconds(60)
    $chromiumHwnd = [IntPtr]::Zero
    while ($chromiumHwnd -eq [IntPtr]::Zero) {
        if ($app.HasExited) { throw "app exited while waiting for the WebView (exit $($app.ExitCode))" }
        if ((Get-Date) -gt $deadline) { throw 'Chrome_RenderWidgetHostHWND never appeared - WebView2 missing?' }
        Start-Sleep -Milliseconds 500
        $app.Refresh()
        $chromiumHwnd = [GroupWeaver.WebViewCapture]::FindDescendantByClass($app.MainWindowHandle, 'Chrome_RenderWidgetHostHWND')
    }
    Log 'WebView2 canvas is up'
    # Gate "rendered" on the External frontier node alone (blue, centre): the proven
    # never-occluded render signal. The 2-node DL_FS-Finance_RW scope fits very
    # zoomed-in and the rust root can tuck UNDER the top-left legend panel (the demo
    # recorder pans to clear it before hunting rust) - but this harness only needs
    # proof the graph rendered, and the External frontier is that proof.
    [void](Wait-NodeBlob { Save-Probe } $colorExternalNode 30 'the external frontier node (render signal)')
    Log 'workspace graph rendered'
    Capture-Shot '01-ist.png'
    Assert-Alive 'initial workspace render'

    # #122 / ADR-025: record the workspace canvas's child HWND now (it is the SOLE Chromium
    # child - Plan/Gap not created yet). After the Plan round-trip we assert it is UNCHANGED:
    # the parked surface was never destroyed, so its live page + viewport survived the Back.
    $script:wsHwnd0 = Get-VisibleChromiumHwnd
    Log "baseline workspace Chromium child HWND = $($script:wsHwnd0)"

    # --- button capture-coordinate map (read off 01-ist.png / 02-plan.png at the fixed
    #     60,60 + clamped-min geometry; UIA is blind to these post-WebView) --------------
    # Workspace rail: "Design plan" is the first action in the top-right WrapPanel
    # (01-ist.png: button centred ~1100,475).
    $PT_DesignPlan = @(1100, 475)
    # PlanView editor column (right): header row right-aligns
    # [Gap analysis] [Export script] [<- Back to explore] at y~145 (02-plan.png).
    $PT_GapAnalysis = @(1067, 145)
    $PT_PlanBack = @(1364, 145)
    # GapView chrome column (right, 360px): header row [Gap ........ <- Back to explore],
    # same y-band, Back rightmost (~1405,150).
    $PT_GapBack = @(1405, 150)

    # =============================================================================
    # PATH A: Design plan -> (confirm Plan step) -> Back  [the primary crash path]
    # =============================================================================
    # NOTE on ordering: the crash is a DELAYED unhandled exception on the next render/layout
    # pass after the Back swap (~1-1.5s later), NOT synchronous with the click. So after every
    # swap we WAIT for the new view to settle, THEN Throw-IfCrashed (attributes a delayed crash
    # to that exact step), THEN Assert-Alive + capture + pixel-confirm.
    Log '--- PATH A: Design plan -> Back ---'
    Click-CapturePoint $PT_DesignPlan[0] $PT_DesignPlan[1] 'Design plan'
    Wait-Step 'Plan step mount (own WebView)' 1500
    Throw-IfCrashed 'Design plan -> Plan step'
    Assert-Alive 'Design plan -> Plan step'
    Capture-Shot '02-plan.png'
    Confirm-Plan 'Design plan'

    Click-CapturePoint $PT_PlanBack[0] $PT_PlanBack[1] '<- Back to explore (Plan->Workspace)'
    Wait-Step 'workspace WebView re-attach' 1800
    Throw-IfCrashed 'Plan -> Back (workspace)'   # <-- the confirmed-crash assertion
    Assert-Alive 'Plan -> Back (workspace)'
    Capture-Shot '03-back-to-ist.png'
    Confirm-Workspace 'Plan -> Back'
    # #122 / ADR-025: the Plan was abandoned+disposed on Back, so the workspace child is the
    # sole visible Chromium child again. Assert it is the SAME handle as the baseline - proof
    # the parked surface (page + cytoscape viewport) survived rather than being re-rendered.
    Update-ChromiumHwnd
    Start-Sleep -Milliseconds 400
    Assert-SameHwnd $script:wsHwnd0 (Get-VisibleChromiumHwnd) 'Plan -> Back to Workspace'
    Log 'PATH A survived'

    # =============================================================================
    # PATH B: Design plan -> Gap analysis -> Back -> Back  [the full round-trip]
    # =============================================================================
    Log '--- PATH B: Design plan -> Gap analysis -> Back -> Back ---'
    Click-CapturePoint $PT_DesignPlan[0] $PT_DesignPlan[1] 'Design plan (round 2)'
    Wait-Step 'Plan step mount (round 2)' 1500
    Throw-IfCrashed 'Design plan -> Plan step (round 2)'
    Assert-Alive 'Design plan -> Plan step (round 2)'
    Confirm-Plan 'Design plan (round 2)'
    # #122 / ADR-025: record the PLAN canvas's child HWND (workspace is parked+hidden, so the
    # largest VISIBLE child is the plan's). The Gap round-trip parks the plan surface; Back must
    # return the SAME handle.
    $script:planHwnd0 = Get-VisibleChromiumHwnd
    Log "baseline plan Chromium child HWND = $($script:planHwnd0)"

    Click-CapturePoint $PT_GapAnalysis[0] $PT_GapAnalysis[1] 'Gap analysis'
    Wait-Step 'gap WebView mount + RefreshAsync' 1800
    Throw-IfCrashed 'Plan -> Gap step'
    Assert-Alive 'Plan -> Gap step'
    Capture-Shot '04-gap.png'

    Click-CapturePoint $PT_GapBack[0] $PT_GapBack[1] '<- Back to explore (Gap->Plan)'
    Wait-Step 'plan re-shown' 1500
    Throw-IfCrashed 'Gap -> Back (plan)'
    Assert-Alive 'Gap -> Back (plan)'
    Confirm-Plan 'Gap -> Back (plan)'
    # #122 / ADR-025: Gap was abandoned+disposed on Back; the plan surface was unparked. Assert
    # the plan child HWND is unchanged across the Gap round-trip (viewport preserved).
    Update-ChromiumHwnd
    Start-Sleep -Milliseconds 400
    Assert-SameHwnd $script:planHwnd0 (Get-VisibleChromiumHwnd) 'Gap -> Back to Plan'

    Click-CapturePoint $PT_PlanBack[0] $PT_PlanBack[1] '<- Back to explore (Plan->Workspace, round 2)'
    Wait-Step 'workspace re-attach (round 2)' 1800
    Throw-IfCrashed 'Plan -> Back (workspace, round 2)'
    Assert-Alive 'Plan -> Back (workspace, round 2)'
    Capture-Shot '05-final-ist.png'
    Confirm-Workspace 'Plan -> Back (round 2)'
    Log 'PATH B survived'
}
catch {
    if ($_.Exception.Message -like 'CRASH_AFTER::*') {
        $crashed = $true
        $where = $_.Exception.Message -replace '^CRASH_AFTER::', ''
        Write-Host ''
        Write-Host "FAIL: app crashed after step '$where' (Back-nav WebView2 crash reproduced)."
    }
    elseif ($_.Exception.Message -like 'HWND_IDENTITY::*') {
        # #122 / ADR-025 regression: no crash, but a Back re-created the WebView child instead of
        # restoring the parked one - the viewport was NOT preserved. A real feature failure.
        $crashed = $true
        $where = $_.Exception.Message -replace '^HWND_IDENTITY::', ''
        Write-Host ''
        Write-Host "FAIL (#122 viewport NOT preserved): $where"
    }
    else {
        # A driving/infrastructure failure (could not launch, UIA unreachable, WebView2
        # missing, render never confirmed) - report it honestly, distinct from the crash.
        $crashed = $true
        Write-Host ''
        Write-Host "FAIL (driving/infra, NOT the crash assertion): $($_.Exception.Message)"
        Write-Host $_.ScriptStackTrace
        # If the app died, its stderr is the truth - dump it too.
        $app.Refresh()
        if ($app.HasExited) {
            Write-Host "(app exit code $($app.ExitCode))"
            if (Test-Path $stderrFile) {
                Write-Host '================= app-stderr.txt ================='
                Get-Content $stderrFile -Raw
                Write-Host '================================================='
            }
        }
    }
}
finally {
    if (-not $app.HasExited) {
        Log 'Closing the app...'
        try {
            [void]$app.CloseMainWindow()
            if (-not $app.WaitForExit(5000)) { $app.Kill() }
        }
        catch { try { $app.Kill() } catch {} }
    }
    if ($script:probePath -and (Test-Path $script:probePath)) {
        Remove-Item -Force $script:probePath -ErrorAction SilentlyContinue
    }
    Restore-RailState
}

Write-Host ''
if ($crashed) {
    Write-Host 'RESULT: FAIL - Back navigation from a graph-bearing step did not survive (see above).'
    exit 1
}
else {
    Write-Host 'RESULT: PASS - all step swaps (Design plan/Gap/Back round-trips) kept the app alive.'
    exit 0
}
