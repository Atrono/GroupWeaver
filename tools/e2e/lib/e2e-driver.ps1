<#
.SYNOPSIS
    Shared, dot-sourceable E2E scenario driver (ADR-038 WP4, issue #243).

.DESCRIPTION
    Generalizes the proven tools/smoke-back-nav.ps1 primitives into a reusable
    driver library for tools/e2e/scenarios/*.ps1. Dot-sources
    tools/lib/webview-capture.ps1 (the P/Invoke + UIA + pixel-hunt investment;
    NEVER duplicated here) and tools/e2e/lib/e2e-evidence.ps1 (on-failure
    collectors).

    Requires Windows PowerShell 5.1: UIAutomationClient is a .NET Framework GAC
    assembly pwsh/.NET 8 cannot load. Scenarios relaunch themselves under 5.1
    when started from pwsh; this file refuses to load under Core.

    ASCII-ONLY FILE (lab rule: PS 5.1 reads no-BOM scripts via the ANSI/OEM
    codepage; any non-ASCII byte is a parse hazard). Keep it that way.

    Driving policy (ADR-038 D2): Tier A input fidelity - all ACTIONS here are
    UIA patterns or posted WM_* messages; captures/probes are read-only. No
    mutation of anything but the app's own input queue. Zero AD interaction.

    Context contract: Initialize-E2eContext MUST be called first. The driver
    keeps script-scoped state ($script:app, $script:chromiumHwnd, artifact
    paths); because scenarios DOT-SOURCE this file, that script scope is the
    scenario's own, which is exactly how webview-capture.ps1's caller-scoped
    $app/$chromiumHwnd convention is satisfied.

    Failure classes (ADR-038 D4): PRODUCT-CRASH (process exited; stderr is the
    truth), PRODUCT-ASSERT (alive but wrong), INFRA-DRIVE (UIA/WebView2/launch
    plumbing), TIMEOUT (assigned by the runner's watchdog, never by the driver).
#>

if ($PSVersionTable.PSEdition -eq 'Core') {
    throw 'e2e-driver.ps1 requires Windows PowerShell 5.1 (UIAutomationClient is GAC-only); scenarios relaunch themselves.'
}

Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.Drawing

# Shared P/Invoke + input + hunt + UIA helpers (DPI awareness initialised on dot-source).
. (Join-Path $PSScriptRoot '..\..\lib\webview-capture.ps1')
# On-failure evidence collectors (final burst, UIA dump, HWND inventory, event log, WER scan).
. (Join-Path $PSScriptRoot 'e2e-evidence.ps1')

# Native helpers beyond the shared lib: ScreenToClient (main-window client-coord
# clicks), the largest-VISIBLE-Chromium-child disambiguation (#122 parking-lot:
# a hidden parked child COEXISTS with the visible one, so first-match is wrong),
# and EnumWindows top-level inventory (the unexpected-dialog invariant).
if (-not ('GroupWeaver.E2eNative' -as [type])) {
    Add-Type -Namespace GroupWeaver -Name E2eNative -MemberDefinition @'
[StructLayout(LayoutKind.Sequential)]
public struct POINT { public int X, Y; }
[DllImport("user32.dll")]
public static extern bool ScreenToClient(IntPtr hWnd, ref POINT pt);

[StructLayout(LayoutKind.Sequential)]
public struct RECT2 { public int Left, Top, Right, Bottom; }
[DllImport("user32.dll")] private static extern bool EnumChildWindows(IntPtr parent, EnumProc cb, IntPtr l);
[DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc cb, IntPtr l);
[DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr h, System.Text.StringBuilder s, int max);
[DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr h, System.Text.StringBuilder s, int max);
[DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr h);
[DllImport("user32.dll", SetLastError = true)] private static extern bool GetWindowRect(IntPtr h, out RECT2 r);
[DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
public delegate bool EnumProc(IntPtr h, IntPtr l);

// Largest VISIBLE descendant of a class: the on-screen canvas. A parked child is
// hidden / zero-size (ADR-025 parking lot), so it never wins.
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

// Top-level windows owned by a pid: "hwnd|class|visible(0/1)|title" per entry.
private static System.Collections.Generic.List<string> _top;
private static uint _pid;
private static bool TopCb(IntPtr h, IntPtr l) {
    uint p; GetWindowThreadProcessId(h, out p);
    if (p == _pid) {
        var cls = new System.Text.StringBuilder(256);
        GetClassName(h, cls, cls.Capacity);
        var title = new System.Text.StringBuilder(512);
        GetWindowText(h, title, title.Capacity);
        _top.Add(string.Format("0x{0:X}|{1}|{2}|{3}", h.ToInt64(), cls, IsWindowVisible(h) ? 1 : 0, title));
    }
    return true;
}
public static string[] GetTopLevelWindows(uint pid) {
    _top = new System.Collections.Generic.List<string>();
    _pid = pid;
    EnumWindows(TopCb, IntPtr.Zero);
    return _top.ToArray();
}
'@
}

# --- context ---------------------------------------------------------------------

$script:E2eScenario = $null
$script:E2eArtifactDir = $null
$script:E2eHarnessLog = $null
$script:E2eStartedAt = $null
$script:E2eCheckpointIndex = 0
$script:E2eStdOutFile = $null
$script:E2eStdErrFile = $null
$script:probePath = $null
$script:app = $null
$script:chromiumHwnd = [IntPtr]::Zero

function Initialize-E2eContext {
    param(
        [Parameter(Mandatory)][string]$ScenarioName,
        [Parameter(Mandatory)][string]$ArtifactDir
    )
    $script:E2eScenario = $ScenarioName
    $script:E2eArtifactDir = $ArtifactDir
    $script:E2eHarnessLog = Join-Path $ArtifactDir 'harness.jsonl'
    $script:E2eStartedAt = Get-Date
    $script:E2eCheckpointIndex = 0
    $script:E2eStdOutFile = Join-Path $ArtifactDir 'app-stdout.txt'
    $script:E2eStdErrFile = Join-Path $ArtifactDir 'app-stderr.txt'
    if (-not (Test-Path $ArtifactDir)) { New-Item -ItemType Directory -Force $ArtifactDir | Out-Null }
    Write-DriverLog 'scenario-start' @{ psVersion = $PSVersionTable.PSVersion.ToString() }
}

# One timestamped line per driver action / capability poll into harness.jsonl
# (ADR-038 D5: polls are where hangs localize) + a console mirror.
function Write-DriverLog {
    param(
        [Parameter(Mandatory)][string]$Action,
        [hashtable]$Data = @{}
    )
    $entry = [ordered]@{
        ts       = (Get-Date).ToUniversalTime().ToString('o')
        scenario = $script:E2eScenario
        action   = $Action
    }
    foreach ($key in $Data.Keys) { $entry[$key] = $Data[$key] }
    if ($script:E2eHarnessLog) {
        Add-Content -Path $script:E2eHarnessLog -Value ($entry | ConvertTo-Json -Compress -Depth 4) -Encoding UTF8
    }
    $detail = ''
    if ($Data.Count -gt 0) {
        $pairs = foreach ($key in $Data.Keys) { "$key=$($Data[$key])" }
        $detail = ' ' + ($pairs -join ' ')
    }
    Write-Host "[e2e $($script:E2eScenario) $(Get-Date -Format HH:mm:ss)] $Action$detail"
}

# --- launch / teardown -------------------------------------------------------------

# Launch the app exe with BOTH streams redirected (single-stream redirect on this
# WinExe deadlocks - the launch-smoke lesson), wait for the main window (bounded),
# then pin deterministic geometry (same request as the media recorders; Avalonia
# clamps up to its logical Min). Optional env overrides are set on THIS process
# before launch (children inherit) and restored right after - scenarios run one
# app at a time by contract (ADR-038 D4: strictly sequential).
function Start-E2EApp {
    param(
        [Parameter(Mandatory)][string]$ExePath,
        [string[]]$AppArgs = @(),
        [hashtable]$EnvOverrides = @{},
        [int]$WindowTimeoutSec = 30
    )
    if (-not (Test-Path $ExePath)) { throw "app exe not found: $ExePath (the runner builds Release; pass -AppExe)" }
    Write-DriverLog 'app-launch' @{ exe = $ExePath; args = ($AppArgs -join ' ') }

    $savedEnv = @{}
    foreach ($name in $EnvOverrides.Keys) {
        $savedEnv[$name] = [Environment]::GetEnvironmentVariable($name)
        [Environment]::SetEnvironmentVariable($name, [string]$EnvOverrides[$name])
    }
    try {
        if ($AppArgs.Count -gt 0) {
            $script:app = Start-Process -FilePath $ExePath -ArgumentList $AppArgs -PassThru `
                -RedirectStandardError $script:E2eStdErrFile -RedirectStandardOutput $script:E2eStdOutFile
        }
        else {
            $script:app = Start-Process -FilePath $ExePath -PassThru `
                -RedirectStandardError $script:E2eStdErrFile -RedirectStandardOutput $script:E2eStdOutFile
        }
    }
    finally {
        foreach ($name in $savedEnv.Keys) {
            [Environment]::SetEnvironmentVariable($name, $savedEnv[$name])
        }
    }
    # Cache the process handle NOW: without this, .ExitCode on a Start-Process
    # -PassThru object reads $null after the process exits (PS 5.1 quirk) and the
    # clean-shutdown gate cannot verify exit code 0.
    $null = $script:app.Handle

    $deadline = (Get-Date).AddSeconds($WindowTimeoutSec)
    while ($script:app.MainWindowHandle -eq [IntPtr]::Zero) {
        if ($script:app.HasExited) {
            Show-AppStreams
            throw "CRASH_AFTER::startup (exit $($script:app.ExitCode)) before a window appeared"
        }
        if ((Get-Date) -gt $deadline) { throw "main window never appeared within ${WindowTimeoutSec}s" }
        Start-Sleep -Milliseconds 250
        $script:app.Refresh()
    }
    # SWP_NOZORDER|SWP_NOACTIVATE = 0x14 - posted input needs no activation.
    [void][GroupWeaver.WebViewCapture]::SetWindowPos($script:app.MainWindowHandle, [IntPtr]::Zero, 60, 60, 1480, 920, 0x14)
    Write-DriverLog 'app-window-up' @{ appPid = $script:app.Id; hwnd = "0x{0:X}" -f $script:app.MainWindowHandle.ToInt64() }
    return $script:app
}

# Graceful close -> WaitForExit(grace) -> Kill. Returns @{ Graceful; AlreadyExited;
# ExitCode; WaitedMs }. The CLEAN-SHUTDOWN GATE lives in Invoke-CleanShutdownGate.
function Stop-E2EApp {
    param([int]$GraceSeconds = 5)
    if (-not $script:app) { return @{ Graceful = $false; AlreadyExited = $true; ExitCode = $null; WaitedMs = 0 } }
    $script:app.Refresh()
    if ($script:app.HasExited) {
        Write-DriverLog 'app-stop' @{ alreadyExited = $true; exitCode = $script:app.ExitCode }
        return @{ Graceful = $false; AlreadyExited = $true; ExitCode = $script:app.ExitCode; WaitedMs = 0 }
    }
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    try { [void]$script:app.CloseMainWindow() } catch { }
    $graceful = $script:app.WaitForExit($GraceSeconds * 1000)
    if (-not $graceful) {
        try { $script:app.Kill() } catch { }
        [void]$script:app.WaitForExit(5000)
    }
    $sw.Stop()
    $exitCode = $null
    $script:app.Refresh()
    if ($script:app.HasExited) { $exitCode = $script:app.ExitCode }
    Write-DriverLog 'app-stop' @{ graceful = $graceful; exitCode = $exitCode; waitedMs = $sw.ElapsedMilliseconds }
    return @{ Graceful = $graceful; AlreadyExited = $false; ExitCode = $exitCode; WaitedMs = $sw.ElapsedMilliseconds }
}

# The cross-cutting clean-shutdown invariant (ADR-038 D4): graceful exit within
# the grace window AND exit code 0, else PRODUCT-ASSERT.
function Invoke-CleanShutdownGate {
    param([int]$GraceSeconds = 5)
    $r = Stop-E2EApp -GraceSeconds $GraceSeconds
    if ($r.AlreadyExited) {
        throw "ASSERT::clean-shutdown - app had already exited (code $($r.ExitCode)) before the shutdown gate"
    }
    if (-not $r.Graceful) {
        throw "ASSERT::clean-shutdown - app did not exit within ${GraceSeconds}s of CloseMainWindow (killed)"
    }
    if ($r.ExitCode -ne 0) {
        throw "ASSERT::clean-shutdown - app exited code $($r.ExitCode), expected 0"
    }
    Write-DriverLog 'clean-shutdown-gate' @{ exitCode = 0; waitedMs = $r.WaitedMs }
}

function Show-AppStreams {
    Write-Host ''
    Write-Host '================= app-stderr.txt ================='
    if (Test-Path $script:E2eStdErrFile) { Get-Content $script:E2eStdErrFile -Raw } else { Write-Host '(no stderr file)' }
    Write-Host '================= app-stdout.txt ================='
    if (Test-Path $script:E2eStdOutFile) { Get-Content $script:E2eStdOutFile -Raw } else { Write-Host '(no stdout file)' }
    Write-Host '================================================='
}

# --- liveness (the delayed-crash attribution pair, verbatim smoke-back-nav) -------

# Immediate check, no settle: raises the CRASH sentinel if the app has exited - so
# a delayed unhandled-exception crash on the next render/layout pass after a click
# is attributed to that step, not to a downstream capture/confirm helper that
# happened to run first and tripped over the now-dead process handle.
function Throw-IfCrashed {
    param([Parameter(Mandatory)][string]$AfterWhat)
    $script:app.Refresh()
    if ($script:app.HasExited) {
        Write-DriverLog 'crash-detected' @{ afterWhat = $AfterWhat; exitCode = $script:app.ExitCode }
        Show-AppStreams
        throw "CRASH_AFTER::$AfterWhat"
    }
}

# Settle FIRST (the confirmed back-nav crash is a DELAYED unhandled exception
# ~1-1.5s after the swap, NOT synchronous with the click), then check.
function Assert-Alive {
    param(
        [Parameter(Mandatory)][string]$AfterWhat,
        [int]$SettleMs = 600
    )
    Start-Sleep -Milliseconds $SettleMs
    $script:app.Refresh()
    if ($script:app.HasExited) {
        Write-DriverLog 'crash-detected' @{ afterWhat = $AfterWhat; exitCode = $script:app.ExitCode }
        Show-AppStreams
        throw "CRASH_AFTER::$AfterWhat"
    }
    Write-DriverLog 'alive' @{ afterWhat = $AfterWhat }
}

# --- captures ----------------------------------------------------------------------

# Single-shot DPI-aware probe capture for pixel gates. PrintWindow on the WebView2
# layer lags one compositor batch (lab-environment.md): capture-and-discard, keep
# the second frame.
function Save-Probe {
    if (-not $script:probePath) { $script:probePath = Join-Path $script:E2eArtifactDir '_probe.png' }
    [void][GroupWeaver.WebViewCapture]::CaptureBurst($script:app.MainWindowHandle, $script:E2eArtifactDir, 1, 0)
    Move-Item -Force (Join-Path $script:E2eArtifactDir 'frame_000.png') $script:probePath
    [void][GroupWeaver.WebViewCapture]::CaptureBurst($script:app.MainWindowHandle, $script:E2eArtifactDir, 1, 0)
    Move-Item -Force (Join-Path $script:E2eArtifactDir 'frame_000.png') $script:probePath
    return $script:probePath
}

# Numbered checkpoint PNG (NN-name.png) under <artifactDir>\checkpoints via the
# same capture-and-discard idiom. Never throws on an exited app (logs the skip) -
# checkpoints are evidence, not gates.
function Capture-Checkpoint {
    param([Parameter(Mandatory)][string]$Name)
    $script:app.Refresh()
    if ($script:app.HasExited) {
        Write-DriverLog 'checkpoint-skipped' @{ name = $Name; reason = 'app exited' }
        return
    }
    $dir = Join-Path $script:E2eArtifactDir 'checkpoints'
    if (-not (Test-Path $dir)) { New-Item -ItemType Directory -Force $dir | Out-Null }
    $script:E2eCheckpointIndex++
    $file = Join-Path $dir ('{0:d2}-{1}.png' -f $script:E2eCheckpointIndex, $Name)
    try {
        [void][GroupWeaver.WebViewCapture]::CaptureBurst($script:app.MainWindowHandle, $dir, 1, 0)
        [void][GroupWeaver.WebViewCapture]::CaptureBurst($script:app.MainWindowHandle, $dir, 1, 0)
        Move-Item -Force (Join-Path $dir 'frame_000.png') $file
        Write-DriverLog 'checkpoint' @{ name = $Name; file = $file }
    }
    catch {
        Write-DriverLog 'checkpoint-skipped' @{ name = $Name; reason = $_.Exception.Message }
    }
}

# --- Chromium child resolution (the #122 parking-lot rules) -------------------------

function Get-VisibleChromiumHwnd {
    return [GroupWeaver.E2eNative]::FindLargestVisibleDescendant($script:app.MainWindowHandle, 'Chrome_RenderWidgetHostHWND')
}

# Initial appearance: any Chrome_RenderWidgetHostHWND match (bounded poll).
function Wait-ChromiumChild {
    param([int]$TimeoutSec = 60)
    $deadline = (Get-Date).AddSeconds($TimeoutSec)
    while ($true) {
        $script:app.Refresh()
        if ($script:app.HasExited) {
            Show-AppStreams
            throw "CRASH_AFTER::(waiting for the WebView) app exited (exit $($script:app.ExitCode))"
        }
        $h = [GroupWeaver.WebViewCapture]::FindDescendantByClass($script:app.MainWindowHandle, 'Chrome_RenderWidgetHostHWND')
        if ($h -ne [IntPtr]::Zero) {
            $script:chromiumHwnd = $h
            Write-DriverLog 'webview-up' @{ hwnd = "0x{0:X}" -f $h.ToInt64() }
            return
        }
        if ((Get-Date) -gt $deadline) { throw "Chrome_RenderWidgetHostHWND never appeared within ${TimeoutSec}s - WebView2 missing?" }
        Start-Sleep -Milliseconds 500
    }
}

# Re-resolve the LIVE on-screen canvas child after a step swap: a swap destroys or
# parks the leaving step's child, so any earlier handle can be stale. Prefers the
# LARGEST VISIBLE match (a hidden parked child coexists, ADR-025); falls back to
# any match mid-swap so the pixel gate can retry.
function Update-ChromiumHwnd {
    param([int]$TimeoutSec = 10)
    $deadline = (Get-Date).AddSeconds($TimeoutSec)
    while ($true) {
        $script:app.Refresh()
        if ($script:app.HasExited) { throw "CRASH_AFTER::(detected re-resolving Chromium child) app exited" }
        $h = Get-VisibleChromiumHwnd
        if ($h -eq [IntPtr]::Zero) {
            $h = [GroupWeaver.WebViewCapture]::FindDescendantByClass($script:app.MainWindowHandle, 'Chrome_RenderWidgetHostHWND')
        }
        if ($h -ne [IntPtr]::Zero) { $script:chromiumHwnd = $h; return }
        if ((Get-Date) -gt $deadline) { throw "Chrome_RenderWidgetHostHWND not found within ${TimeoutSec}s (re-resolve)" }
        Start-Sleep -Milliseconds 300
    }
}

# #122 / ADR-025 viewport-survival pin: the same child HWND across a Back into a
# parked step proves the live page + cytoscape viewport survived (there is no code
# path that resets the viewport WITHOUT a reload).
function Assert-SameHwnd {
    param($Before, $After, [Parameter(Mandatory)][string]$What)
    if ($Before -eq [IntPtr]::Zero -or $After -eq [IntPtr]::Zero) {
        throw "HWND_IDENTITY::$What - could not resolve a visible Chromium child (before=$Before after=$After)"
    }
    if ($Before -ne $After) {
        throw "HWND_IDENTITY::$What - child HWND CHANGED across Back ($Before -> $After): surface re-created, viewport NOT preserved"
    }
    Write-DriverLog 'viewport-preserved' @{ what = $What; hwnd = "0x{0:X}" -f ([IntPtr]$Before).ToInt64() }
}

# --- input -------------------------------------------------------------------------

# Click an Avalonia chrome element by POSTED WM at a capture-coordinate point.
# Once the Chromium child exists, UIA descendant queries return ONLY Chromium
# content (lab-environment.md) - rail/editor buttons CANNOT be Invoke()d, so they
# are driven by posting WM_LBUTTON to the MAIN-window HWND at the button's pixel
# location (airspace-safe: those buttons sit BESIDE the WebView, never over it).
# Capture coords = main-window RECT-relative (PrintWindow PNG origin), converted
# to CLIENT coords via ScreenToClient. Geometry is deterministic for the fixed
# 60,60 / clamped-min window Start-E2EApp pins.
function Click-CapturePoint {
    param(
        [Parameter(Mandatory)][int]$CaptureX,
        [Parameter(Mandatory)][int]$CaptureY,
        [Parameter(Mandatory)][string]$Label
    )
    $mainRect = Get-WindowRectOf $script:app.MainWindowHandle
    $pt = New-Object GroupWeaver.E2eNative+POINT
    $pt.X = $mainRect.Left + $CaptureX
    $pt.Y = $mainRect.Top + $CaptureY
    [void][GroupWeaver.E2eNative]::ScreenToClient($script:app.MainWindowHandle, [ref]$pt)
    $lp = [GroupWeaver.WebViewCapture]::MakeLParam($pt.X, $pt.Y)
    $h = $script:app.MainWindowHandle
    [void][GroupWeaver.WebViewCapture]::PostMessage($h, [GroupWeaver.WebViewCapture]::WM_MOUSEMOVE, [IntPtr]::Zero, $lp)
    Start-Sleep -Milliseconds 80
    [void][GroupWeaver.WebViewCapture]::PostMessage($h, [GroupWeaver.WebViewCapture]::WM_LBUTTONDOWN, [IntPtr]1, $lp)
    [void][GroupWeaver.WebViewCapture]::PostMessage($h, [GroupWeaver.WebViewCapture]::WM_LBUTTONUP, [IntPtr]::Zero, $lp)
    Write-DriverLog 'click-capture-point' @{ label = $Label; captureX = $CaptureX; captureY = $CaptureY; clientX = $pt.X; clientY = $pt.Y }
}

# --- journey building blocks --------------------------------------------------------

# The proven demo connect + root-load UIA sequence (verbatim smoke-back-nav):
# launch WITHOUT --demo, UIA-click "Demo mode" (so only demo data is ever touched
# and the connect card with the live operator identity is never captured), filter
# the root picker, select the first candidate, Load. UIA is reliable here because
# the WebView child does not exist yet.
function Invoke-DemoRootLoad {
    param([Parameter(Mandatory)][string]$FilterText)
    [void](Wait-Uia { Find-UiaFirst ([System.Windows.Automation.ControlType]::Button) 'Demo mode' } 30 "button 'Demo mode'")
    Start-Sleep -Milliseconds 500
    Invoke-UiaButton 'Demo mode'
    Write-DriverLog 'demo-mode-clicked' @{}

    [void](Wait-Uia { Find-UiaFirst ([System.Windows.Automation.ControlType]::ListItem) $null } 30 'root candidates')
    Start-Sleep -Milliseconds 300

    $filterBox = Wait-Uia { Find-UiaFirst ([System.Windows.Automation.ControlType]::Edit) $null } 10 'filter box'
    $valuePattern = $null
    if ($filterBox.TryGetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern, [ref]$valuePattern)) {
        $valuePattern.SetValue($FilterText)
    }
    else {
        $filterBox.SetFocus()
        Start-Sleep -Milliseconds 200
        foreach ($ch in $FilterText.ToCharArray()) {
            [void][GroupWeaver.WebViewCapture]::PostMessage($script:app.MainWindowHandle, [GroupWeaver.WebViewCapture]::WM_CHAR, [IntPtr][int]$ch, [IntPtr]::Zero)
            Start-Sleep -Milliseconds 15
        }
    }
    Write-DriverLog 'root-picker-filtered' @{ filter = $FilterText }
    Start-Sleep -Milliseconds 500

    $item = Wait-Uia { Find-UiaFirst ([System.Windows.Automation.ControlType]::ListItem) $null } 10 'filtered candidate'
    $item.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
    Start-Sleep -Milliseconds 300
    Invoke-UiaButton 'Load'
    Write-DriverLog 'root-load-clicked' @{}
}

# Crash-aware pixel probe: is a blob of $Rgb currently on the canvas? Re-resolves
# the live Chromium child first (stale-handle safety across step swaps).
function Test-CanvasBlob {
    param(
        [Parameter(Mandatory)][int[]]$Rgb,
        [int]$MinCount = 30
    )
    $script:app.Refresh()
    if ($script:app.HasExited) { throw "CRASH_AFTER::(detected during pixel-confirm) app exited" }
    Update-ChromiumHwnd
    $blob = Find-NodeBlob (Save-Probe) $Rgb 'canvas' $MinCount
    return [bool]$blob
}

# --- operator-state bracketing (ADR-038 D6: "the runner brackets them") ------------
# Scenarios that must touch the operator's REAL %APPDATA% state (until the WP5
# --state-dir seam) protect it with an ON-DISK backup beside the file
# (<file>.e2e-bak), NEVER an in-memory copy: the runner's watchdog kill path
# (Stop-Process -Force) skips the scenario's finally block, so an in-memory
# backup dies with the child and the operator's file stays clobbered. The
# .e2e-bak survives the kill; recovery sweeps run at scenario start (inside
# Backup-OperatorState) and in run-e2e.ps1 (after a watchdog kill + once at end
# of run). A file that did NOT pre-exist is recorded via the sentinel content
# below so recovery DELETES the forced file instead of leaving it behind on a
# fresh box. The sentinel and the '*.e2e-bak' convention are mirrored in
# run-e2e.ps1 (Restore-OperatorStateLeftovers) - keep them in sync.
$script:E2eNoPriorFileSentinel = '__GW_E2E_NO_PRIOR_FILE__'

function Backup-OperatorState {
    param([Parameter(Mandatory)][string]$Path)
    # Heal any leftover backup from a previously KILLED run first: the .e2e-bak
    # holds the true original; the file on disk is a dead run's forced state.
    Restore-OperatorState -Path $Path -Reason 'leftover-sweep (scenario start)'
    $bak = "$Path.e2e-bak"
    $dir = Split-Path -Parent $Path
    if (-not (Test-Path $dir)) { New-Item -ItemType Directory -Force $dir | Out-Null }
    if (Test-Path $Path) {
        Copy-Item -Force $Path $bak
        Write-DriverLog 'operator-state-backed-up' @{ path = $Path; bak = $bak; preExisting = $true }
    }
    else {
        Set-Content -Path $bak -Value $script:E2eNoPriorFileSentinel -Encoding ASCII
        Write-DriverLog 'operator-state-backed-up' @{ path = $Path; bak = $bak; preExisting = $false }
    }
}

function Restore-OperatorState {
    param(
        [Parameter(Mandatory)][string]$Path,
        [string]$Reason = 'finally'
    )
    $bak = "$Path.e2e-bak"
    if (-not (Test-Path $bak)) { return }
    $content = [string](Get-Content $bak -Raw)
    if ($content.Trim() -eq $script:E2eNoPriorFileSentinel) {
        if (Test-Path $Path) { Remove-Item -Force $Path }
        Remove-Item -Force $bak
        Write-DriverLog 'operator-state-restored' @{ path = $Path; reason = $Reason; preExisting = $false }
    }
    else {
        Move-Item -Force $bak $Path
        Write-DriverLog 'operator-state-restored' @{ path = $Path; reason = $Reason; preExisting = $true }
    }
}

# --- cross-cutting invariants -------------------------------------------------------

# No unexpected top-level #32770 dialogs (native Win32 dialog class - Avalonia
# never creates one; a visible match means an error/system dialog popped).
function Assert-NoUnexpectedDialogs {
    param([Parameter(Mandatory)][string]$Where)
    $wins = @([GroupWeaver.E2eNative]::GetTopLevelWindows([uint32]$script:app.Id))
    $dialogs = @($wins | Where-Object { ($_ -split '\|')[1] -eq '#32770' -and ($_ -split '\|')[2] -eq '1' })
    Write-DriverLog 'dialog-scan' @{ where = $Where; topLevelCount = $wins.Count; dialogCount = $dialogs.Count }
    if ($dialogs.Count -gt 0) {
        throw "ASSERT::unexpected-dialog at '$Where': $($dialogs -join '; ')"
    }
}

# Clean-stderr invariant: the app must not have written to stderr in a healthy run.
# EXPLICIT allowlist of known-benign lines the embedded Chromium layer (not app
# code) emits on teardown; anything else fails. Extend only deliberately, one
# exact pattern per known line - never a blanket Chromium-format pass (renderer
# crashes log in the same format and must keep failing this gate).
$script:E2eBenignStderrPatterns = @(
    # WebView2 browser shutdown while windows still exist (ERROR_CLASS_HAS_WINDOWS,
    # 1412) - routine on host-process CloseMainWindow, observed on every clean exit.
    '^\[\d{4}/\d{6}\.\d+:ERROR:.*window_impl\.cc.*Failed to unregister class Chrome_WidgetWin'
)
function Assert-CleanStderr {
    param([Parameter(Mandatory)][string]$Where)
    $content = ''
    if (Test-Path $script:E2eStdErrFile) { $content = [string](Get-Content $script:E2eStdErrFile -Raw) }
    $offending = @()
    $benignCount = 0
    if ($content) {
        foreach ($line in ($content -split "`r?`n")) {
            if (-not $line.Trim()) { continue }
            $isBenign = $false
            foreach ($pattern in $script:E2eBenignStderrPatterns) {
                if ($line -match $pattern) { $isBenign = $true; break }
            }
            if ($isBenign) { $benignCount++ } else { $offending += $line }
        }
    }
    if ($offending.Count -gt 0) {
        $excerpt = ($offending -join ' | ')
        if ($excerpt.Length -gt 300) { $excerpt = $excerpt.Substring(0, 300) + '...' }
        throw "ASSERT::stderr-not-clean at '$Where': $excerpt"
    }
    Write-DriverLog 'stderr-clean' @{ where = $Where; benignChromiumLines = $benignCount }
}

# Zero new WER dumps in the run window (evidence lib provides the scan).
function Assert-NoNewWerDumps {
    param([Parameter(Mandatory)][datetime]$Since, [Parameter(Mandatory)][string]$Where)
    $dumps = @(Get-WerDumps -Since $Since)
    Write-DriverLog 'wer-scan' @{ where = $Where; newDumps = $dumps.Count }
    if ($dumps.Count -gt 0) {
        throw "ASSERT::wer-dumps at '$Where': $($dumps.Count) new dump(s): $(($dumps | ForEach-Object { $_.Name }) -join ', ')"
    }
}

# --- result protocol ---------------------------------------------------------------

# Failure taxonomy (ADR-038 D4). TIMEOUT is assigned by the runner watchdog only.
function Resolve-FailureClass {
    param([Parameter(Mandatory)][string]$Message)
    if ($Message -like 'CRASH_AFTER::*') { return 'PRODUCT-CRASH' }
    if ($Message -like 'HWND_IDENTITY::*') { return 'PRODUCT-ASSERT' }
    if ($Message -like 'ASSERT::*') { return 'PRODUCT-ASSERT' }
    return 'INFRA-DRIVE'
}

function Get-FailureSignature {
    param([Parameter(Mandatory)][string]$Message)
    $first = ($Message -split "`r?`n")[0].Trim()
    if ($first.Length -gt 200) { $first = $first.Substring(0, 200) }
    return $first
}

# result.json - what the runner (and later the autonomous triager) reads.
function Write-Result {
    param(
        [Parameter(Mandatory)][ValidateSet('pass', 'fail')][string]$Result,
        [string]$Class = '',
        [string]$Signature = '',
        $AppExitCode = $null,
        [string]$Message = ''
    )
    $durationMs = 0
    if ($script:E2eStartedAt) { $durationMs = [int]((Get-Date) - $script:E2eStartedAt).TotalMilliseconds }
    $obj = [ordered]@{
        scenario    = $script:E2eScenario
        result      = $Result
        class       = $Class
        signature   = $Signature
        message     = $Message
        appExitCode = $AppExitCode
        startedUtc  = $script:E2eStartedAt.ToUniversalTime().ToString('o')
        finishedUtc = (Get-Date).ToUniversalTime().ToString('o')
        durationMs  = $durationMs
    }
    $obj | ConvertTo-Json -Depth 4 | Set-Content -Path (Join-Path $script:E2eArtifactDir 'result.json') -Encoding UTF8
    Write-DriverLog 'result-written' @{ result = $Result; class = $Class; signature = $Signature }
}

# Standard catch-block handler: classify, collect evidence while the app may still
# be alive, write result.json. Returns nothing; the scenario exits 1 afterwards.
function Complete-E2eFailure {
    param(
        [Parameter(Mandatory)][System.Management.Automation.ErrorRecord]$ErrorRecord,
        [datetime]$RunStart = [datetime]::MinValue
    )
    $msg = $ErrorRecord.Exception.Message
    $class = Resolve-FailureClass $msg
    $signature = Get-FailureSignature $msg
    Write-DriverLog 'scenario-failed' @{ class = $class; signature = $signature }
    Write-Host $ErrorRecord.ScriptStackTrace
    $exitCode = $null
    if ($script:app) {
        try {
            $script:app.Refresh()
            if ($script:app.HasExited) { $exitCode = $script:app.ExitCode }
        }
        catch { }
    }
    $since = $RunStart
    if ($since -eq [datetime]::MinValue -and $script:E2eStartedAt) { $since = $script:E2eStartedAt }
    try {
        Invoke-E2eEvidenceCollection -ArtifactDir $script:E2eArtifactDir -App $script:app -RunStart $since
    }
    catch {
        Write-DriverLog 'evidence-collection-failed' @{ reason = $_.Exception.Message }
    }
    Write-Result -Result fail -Class $class -Signature $signature -AppExitCode $exitCode -Message $msg
}

# Final teardown for scenario finally blocks: make sure the app is gone.
function Stop-E2EAppForce {
    if ($script:app) {
        try {
            $script:app.Refresh()
            if (-not $script:app.HasExited) {
                Write-DriverLog 'teardown-kill' @{}
                try {
                    [void]$script:app.CloseMainWindow()
                    if (-not $script:app.WaitForExit(5000)) { $script:app.Kill() }
                }
                catch { try { $script:app.Kill() } catch { } }
            }
        }
        catch { }
    }
}
