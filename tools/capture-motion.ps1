<#
.SYNOPSIS
    In-process high-fps PrintWindow burst recorder for judging GroupWeaver
    interaction FEEL/MOTION (the docs/ui-checklist.md [I] items a static frame can't show).

.DESCRIPTION
    record-demo-gif.ps1 captures ONE frame per beat (2 fps, a per-frame powershell.exe
    spawn) - it shows END-STATES, not the transition between them. This script records
    the actual animation at high framerate (one reused Bitmap+Graphics+PrintWindow in a
    tight in-process loop, ~15-30 fps for a short window) and assembles the clip at the
    MEASURED framerate, so playback speed matches reality and the clip tells the truth
    about smoothness.

    It launches the app in DEMO mode (never "Connect to domain" - the connect card shows
    the live operator identity and must never be captured; demo data only, exactly like
    record-demo-gif), drives to the rendered graph via UIA + posted WM_* input, then
    records a high-fps clip AROUND each gesture:

      (a) lazy-expand : double-click the External frontier node -> expand/focus-fit animation
      (b) wheel-zoom  : a WM_MOUSEWHEEL burst aimed at the expanded cluster -> zoom animation
      (c) drag-pan    : WM_MOUSEMOVE with the left button held -> pan animation

    Each clip is written to artifacts/ui/motion/ (gitignored - clips are evidence, not
    source) as an mp4 (libx264 via ffmpeg; -Gif also emits a .gif). The output line for
    each clip states the MEASURED fps, frame count, duration, and the RENDERING MODE
    (GPU vs software) it ran under - per lab-environment.md the software-rendered numbers
    are the target-audience (RDP/server/VM) floor, so the clip must say which it is.

    Read-only / dev tooling: pure capture, zero AD or src/ writes.

    Shares the P/Invoke surface + input/hunt/UIA helpers with record-demo-gif.ps1 via
    tools/lib/webview-capture.ps1. Runs under Windows PowerShell 5.1 (UIA is a .NET
    Framework GAC assembly); invoking with pwsh relaunches itself.
    Re-runnable: wipes its frame dir, builds the app if the exe is missing, always
    closes the app in finally.

.PARAMETER DurationMs
    Length of each gesture's capture window in milliseconds (default 1500).

.PARAMETER FrameIntervalMs
    Min ms between frames in the burst (default 0 = as fast as PrintWindow allows).
    Raise it to cap fps if a clip is uselessly large.

.PARAMETER Gif
    Also emit a .gif alongside each .mp4 (mp4 is the default; gif is optional).

.EXAMPLE
    pwsh tools/capture-motion.ps1
    pwsh tools/capture-motion.ps1 -DurationMs 2000 -Gif
#>
[CmdletBinding()]
param(
    [int]$DurationMs = 1500,
    [int]$FrameIntervalMs = 0,
    [switch]$Gif
)

$ErrorActionPreference = 'Stop'

# --- relaunch under Windows PowerShell 5.1 when started from pwsh -------------
if ($PSVersionTable.PSEdition -eq 'Core') {
    $ps51 = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
    $argList = @('-NoProfile', '-NonInteractive', '-ExecutionPolicy', 'Bypass', '-File', $PSCommandPath,
        '-DurationMs', $DurationMs, '-FrameIntervalMs', $FrameIntervalMs)
    if ($Gif) { $argList += '-Gif' }
    & $ps51 @argList
    exit $LASTEXITCODE
}

Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.Drawing

# Shared P/Invoke + input + hunt + UIA helpers (DPI awareness is initialised here).
. (Join-Path $PSScriptRoot 'lib\webview-capture.ps1')

$repoRoot = Split-Path -Parent $PSScriptRoot
$motionDir = Join-Path $repoRoot 'artifacts\ui\motion'
$frameDir = Join-Path $motionDir '_frames'
$exe = Join-Path $repoRoot 'src\App\bin\Debug\net8.0-windows\GroupWeaver.App.exe'

# Node-hunt colors = the RENDERED dark-theme node colors, NOT the source palette
# (src/App/Views/AdObjectKindConverters.cs / web/graph.js). In the dark theme a node
# is a blue-gray fill with a thin kind-COLORED BORDER, so the border renders blended,
# well off the flat palette (diagnosed 2026-06-17, #78). Same constants as record-demo-gif.
$colorRoot = @(136, 94, 69)            # DL rust root, blended on the canvas
$colorGlobalGroup = @(0x10, 0x7C, 0x10)  # GlobalGroup green (expanded cluster)
$colorRootBadge = @(0xA1, 0x40, 0x00)    # Avalonia detail badge uses the EXACT palette
$colorExternalNode = @(49, 85, 115)      # External frontier renders BLUE, right of root

function Log([string]$msg) { Write-Host "[capture-motion $(Get-Date -Format HH:mm:ss)] $msg" }

# --- rendering mode (GPU vs software) -----------------------------------------
# Pragmatic detection (lab-environment.md): Microsoft Basic Display Adapter => no GPU
# driver => Chromium software rendering. Otherwise label by the adapter name. State it
# on every clip's output line - the software-rendered numbers are the perf-floor target.
function Get-RenderingMode {
    try {
        $gpu = Get-CimInstance Win32_VideoController -ErrorAction Stop |
            Select-Object -First 1 -ExpandProperty Name
    }
    catch { $gpu = $null }
    if (-not $gpu) { return @{ Mode = 'software'; Adapter = 'unknown' } }
    if ($gpu -match 'Basic Display Adapter') {
        return @{ Mode = 'software'; Adapter = $gpu }
    }
    return @{ Mode = 'GPU'; Adapter = $gpu }
}
$render = Get-RenderingMode
Log "rendering mode: $($render.Mode) (adapter: $($render.Adapter))"

# --- single-shot probe capture (for pixel hunting between bursts) --------------
# In-process PrintWindow of the main window to a probe PNG. PrintWindow on the
# WebView2 layer lags one compositor batch (lab-environment.md): the FIRST capture
# after a mutation returns the PREVIOUS frame, so capture-and-discard then keep the
# second (no sleep needed - the two PrintWindow calls are the settle).
$script:probePath = $null
function Save-Probe {
    if (-not $script:probePath) { $script:probePath = Join-Path $motionDir '_probe.png' }
    [void][GroupWeaver.WebViewCapture]::CaptureBurst($app.MainWindowHandle, $motionDir, 1, 0)
    # CaptureBurst writes frame_000.png into $motionDir; rename to the probe path,
    # then capture once more so the kept frame is live (lag fix).
    Move-Item -Force (Join-Path $motionDir 'frame_000.png') $script:probePath
    [void][GroupWeaver.WebViewCapture]::CaptureBurst($app.MainWindowHandle, $motionDir, 1, 0)
    Move-Item -Force (Join-Path $motionDir 'frame_000.png') $script:probePath
    return $script:probePath
}

# --- the high-fps burst recorder ----------------------------------------------
# Runs $action (the gesture driver) on a background runspace while this thread spins
# the in-process PrintWindow burst, so the capture overlaps the live animation.
# Assembles at the MEASURED fps (elapsed / frames) and prints the rendering mode.
function Record-Gesture([string]$name, [scriptblock]$action) {
    if (Test-Path $frameDir) { Remove-Item -Recurse -Force $frameDir }
    New-Item -ItemType Directory -Force $frameDir | Out-Null

    # Estimate frame budget at an optimistic 30 fps ceiling; CaptureBurst self-times
    # and we assemble at the measured rate regardless of how many actually land.
    $estFps = if ($FrameIntervalMs -gt 0) { [int](1000 / $FrameIntervalMs) } else { 30 }
    $frameCount = [Math]::Max(2, [int]($DurationMs / 1000.0 * $estFps))

    # Drive the gesture on a separate runspace so input fires DURING the burst.
    $ps = [PowerShell]::Create()
    $ps.Runspace = [RunspaceFactory]::CreateRunspace()
    $ps.Runspace.Open()
    # Share the caller-scoped handles the input helpers need.
    $ps.Runspace.SessionStateProxy.SetVariable('app', $app)
    $ps.Runspace.SessionStateProxy.SetVariable('chromiumHwnd', $chromiumHwnd)
    $ps.Runspace.SessionStateProxy.SetVariable('libPath', (Join-Path $PSScriptRoot 'lib\webview-capture.ps1'))
    $ps.Runspace.SessionStateProxy.SetVariable('gestureBody', $action)
    [void]$ps.AddScript({
            Add-Type -AssemblyName System.Drawing
            . $libPath
            Start-Sleep -Milliseconds 120   # let the capture loop spin up first
            & $gestureBody
        })
    $handle = $ps.BeginInvoke()

    Log "recording '$name': $frameCount-frame burst over ~${DurationMs}ms..."
    $elapsedMs = [GroupWeaver.WebViewCapture]::CaptureBurst($app.MainWindowHandle, $frameDir, $frameCount, $FrameIntervalMs)

    # Drain the gesture runspace.
    [void]$ps.EndInvoke($handle)
    foreach ($e in $ps.Streams.Error) { Log "gesture '$name' error: $e" }
    $ps.Runspace.Close()
    $ps.Dispose()

    $frames = (Get-ChildItem (Join-Path $frameDir 'frame_*.png')).Count
    $fps = if ($elapsedMs -gt 0) { [Math]::Round($frames * 1000.0 / $elapsedMs, 1) } else { 0 }
    $durationSec = [Math]::Round($elapsedMs / 1000.0, 2)

    $mp4 = Join-Path $motionDir "$name.mp4"
    Assemble-Clip $frameDir $fps $mp4
    Log ("clip '{0}': {1} frames, {2}s, {3} fps [{4} rendering] -> {5}" -f `
            $name, $frames, $durationSec, $fps, $render.Mode, $mp4)
    if ($Gif) {
        $gif = Join-Path $motionDir "$name.gif"
        Assemble-Gif $frameDir $fps $gif
        Log "  + gif -> $gif"
    }
}

# --- ffmpeg assembly (measured fps in, real-speed clip out) --------------------
$script:ffmpeg = $null
function Resolve-Ffmpeg {
    if ($script:ffmpeg) { return $script:ffmpeg }
    $f = Get-Command ffmpeg -ErrorAction SilentlyContinue
    if (-not $f) {
        # Same-session choco install: PATH not refreshed yet (same as record-demo-gif).
        $env:Path = [Environment]::GetEnvironmentVariable('Path', 'Machine') + ';' +
        [Environment]::GetEnvironmentVariable('Path', 'User') + ';' + $env:Path
        $f = Get-Command ffmpeg -ErrorAction SilentlyContinue
    }
    if (-not $f) { throw 'ffmpeg not found - run tools/bootstrap.ps1 (choco install ffmpeg)' }
    $script:ffmpeg = $f.Source
    return $script:ffmpeg
}

function Assemble-Clip([string]$dir, [double]$fps, [string]$out) {
    $ff = Resolve-Ffmpeg
    if ($fps -le 0) { $fps = 15 }
    # 960px wide, libx264, yuv420p (broad player compat), even dims via scale pad.
    & $ff -y -loglevel error -framerate $fps -i (Join-Path $dir 'frame_%03d.png') `
        -vf 'scale=960:-2:flags=lanczos' -c:v libx264 -pix_fmt yuv420p -movflags +faststart $out
    if ($LASTEXITCODE -ne 0) { throw "ffmpeg (mp4) failed (exit $LASTEXITCODE)" }
}

function Assemble-Gif([string]$dir, [double]$fps, [string]$out) {
    $ff = Resolve-Ffmpeg
    if ($fps -le 0) { $fps = 15 }
    & $ff -y -loglevel error -framerate $fps -i (Join-Path $dir 'frame_%03d.png') `
        -vf 'scale=960:-1:flags=lanczos,split[s0][s1];[s0]palettegen=stats_mode=diff[p];[s1][p]paletteuse=dither=bayer:bayer_scale=4' `
        -loop 0 $out
    if ($LASTEXITCODE -ne 0) { throw "ffmpeg (gif) failed (exit $LASTEXITCODE)" }
}

# === build + launch ==============================================================
if (-not (Test-Path $exe)) {
    Log 'App binary missing - building src/App (Debug)...'
    & dotnet build (Join-Path $repoRoot 'src\App') -c Debug --nologo -v quiet
    if ($LASTEXITCODE -ne 0) { throw "dotnet build failed (exit $LASTEXITCODE)" }
}

if (Test-Path $motionDir) { Remove-Item -Recurse -Force $motionDir }
New-Item -ItemType Directory -Force $motionDir | Out-Null

# Launch WITHOUT --demo and UIA-click "Demo mode" OFF CAMERA (no frame captured before
# the root picker - the idle connect card renders the live operator identity). The
# script NEVER invokes "Connect to domain" - demo data only.
Log 'Launching GroupWeaver (no args - demo chosen via UIA, off camera)...'
$app = Start-Process -FilePath $exe -PassThru

try {
    $deadline = (Get-Date).AddSeconds(30)
    while ($app.MainWindowHandle -eq [IntPtr]::Zero) {
        if ((Get-Date) -gt $deadline) { throw 'main window never appeared' }
        Start-Sleep -Milliseconds 250
        $app.Refresh()
    }

    # Deterministic media geometry: same request as record-demo-gif (Avalonia clamps up
    # to its logical Min 960x600). SWP_NOZORDER|SWP_NOACTIVATE = 0x14.
    [void][GroupWeaver.WebViewCapture]::SetWindowPos($app.MainWindowHandle, [IntPtr]::Zero, 60, 60, 1480, 920, 0x14)

    # --- beat 1: connect card, choose Demo mode (OFF CAMERA) --------------------
    [void](Wait-Uia { Find-UiaFirst ([System.Windows.Automation.ControlType]::Button) 'Demo mode' } 30 "button 'Demo mode'")
    Start-Sleep -Milliseconds 500
    Invoke-UiaButton 'Demo mode'

    # --- beat 2: root picker - filter, select, load ----------------------------
    [void](Wait-Uia { Find-UiaFirst ([System.Windows.Automation.ControlType]::ListItem) $null } 30 'root candidates')
    Start-Sleep -Milliseconds 300

    $filterBox = Wait-Uia { Find-UiaFirst ([System.Windows.Automation.ControlType]::Edit) $null } 10 'filter box'
    $filterText = 'DL_FS-Finance_RW'
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

    # --- beat 3: graph renders --------------------------------------------------
    # From here UIA is useless (the Chromium HWND owns the UIA tree) - posted WM_* +
    # pixel-hunting only.
    $deadline = (Get-Date).AddSeconds(60)
    $chromiumHwnd = [IntPtr]::Zero
    while ($chromiumHwnd -eq [IntPtr]::Zero) {
        if ((Get-Date) -gt $deadline) { throw 'Chrome_RenderWidgetHostHWND never appeared - WebView2 missing?' }
        Start-Sleep -Milliseconds 500
        $app.Refresh()
        $chromiumHwnd = [GroupWeaver.WebViewCapture]::FindDescendantByClass($app.MainWindowHandle, 'Chrome_RenderWidgetHostHWND')
    }
    Log 'WebView2 canvas is up'

    [void](Wait-NodeBlob { Save-Probe } $colorRoot 30 'the root node (DomainLocalGroup rust)')
    Log 'graph rendered'

    # =============================================================================
    # GESTURE (a): lazy-expand - double-click the External frontier node and record
    # the expand + focus-fit animation at high fps.
    # =============================================================================
    $rootBlob = Find-NodeBlob (Save-Probe) $colorRoot
    if (-not $rootBlob) { throw 'root node (rust) not found on the canvas' }
    $extBlob = Find-NodeBlob (Save-Probe) $colorExternalNode 'canvas' 30 ($rootBlob.X + 200)
    if (-not $extBlob) { throw 'External frontier node (blue, right of root) not found' }
    $extX = $extBlob.X; $extY = $extBlob.Y
    Record-Gesture 'a-lazy-expand' {
        Send-CanvasClick $extX $extY $true
    }.GetNewClosure()

    # Confirm the expand actually took (a green GlobalGroup node now exists in canvas)
    # before recording the next gesture against the expanded cluster.
    [void](Wait-NodeBlob { Save-Probe } $colorGlobalGroup 8 'the expanded GlobalGroup node' 'canvas' 150)
    Log 'lazy expand confirmed (GlobalGroup cluster present)'
    Start-Sleep -Milliseconds 500   # let focus-fit settle before the zoom gesture

    # =============================================================================
    # GESTURE (b): wheel-zoom - a WM_MOUSEWHEEL burst aimed at the expanded cluster.
    # =============================================================================
    $zoomTarget = Find-NodeBlob (Save-Probe) $colorGlobalGroup 'canvas' 30 150
    $zx = if ($zoomTarget) { $zoomTarget.X } else { -1 }
    $zy = if ($zoomTarget) { $zoomTarget.Y } else { -1 }
    Record-Gesture 'b-wheel-zoom' {
        # ~40 detents over the window: cytoscape normalizes each to ~x1.008, so this
        # is a visible, smooth dive into the cluster (pointer-anchored zoom).
        Send-CanvasWheel 40 $zx $zy
    }.GetNewClosure()
    Start-Sleep -Milliseconds 400

    # =============================================================================
    # GESTURE (c): drag-pan - WM_MOUSEMOVE with the left button held, dragging on
    # EMPTY canvas (cytoscape pans the viewport; a drag on a node would move the node).
    # =============================================================================
    # Pan grip = a point near the canvas top-left that is unlikely to sit on a node.
    $childRect = Get-WindowRectOf $chromiumHwnd
    $mainRect = Get-WindowRectOf $app.MainWindowHandle
    $gripStartX = ($childRect.Left - $mainRect.Left) + 120
    $gripStartY = ($childRect.Top - $mainRect.Top) + 120
    $panChild = $chromiumHwnd
    $panMainHandle = $app.MainWindowHandle
    Record-Gesture 'c-drag-pan' {
        $cr = Get-WindowRectOf $panChild
        $mr = Get-WindowRectOf $panMainHandle
        # capture coords -> child-client coords
        $cx = $mr.Left + $gripStartX - $cr.Left
        $cy = $mr.Top + $gripStartY - $cr.Top
        $down = [GroupWeaver.WebViewCapture]::MakeLParam($cx, $cy)
        # Park hover, press, then drag in small steps to the right+down (smooth pan).
        [void][GroupWeaver.WebViewCapture]::PostMessage($panChild, [GroupWeaver.WebViewCapture]::WM_MOUSEMOVE, [IntPtr]::Zero, $down)
        Start-Sleep -Milliseconds 60
        [void][GroupWeaver.WebViewCapture]::PostMessage($panChild, [GroupWeaver.WebViewCapture]::WM_LBUTTONDOWN, [IntPtr]1, $down)
        for ($step = 1; $step -le 24; $step++) {
            $mx = $cx + ($step * 14)
            $my = $cy + ($step * 9)
            $lp = [GroupWeaver.WebViewCapture]::MakeLParam($mx, $my)
            # wParam bit 0x0001 = MK_LBUTTON (button held during the move = drag).
            [void][GroupWeaver.WebViewCapture]::PostMessage($panChild, [GroupWeaver.WebViewCapture]::WM_MOUSEMOVE, [IntPtr]1, $lp)
            Start-Sleep -Milliseconds 35
        }
        $up = [GroupWeaver.WebViewCapture]::MakeLParam(($cx + 24 * 14), ($cy + 24 * 9))
        [void][GroupWeaver.WebViewCapture]::PostMessage($panChild, [GroupWeaver.WebViewCapture]::WM_LBUTTONUP, [IntPtr]::Zero, $up)
    }.GetNewClosure()
}
finally {
    if (-not $app.HasExited) {
        Log 'Closing the app...'
        [void]$app.CloseMainWindow()
        if (-not $app.WaitForExit(5000)) { $app.Kill() }
    }
    # Tidy the scratch frame dir + probe; keep the clips (the deliverable).
    if (Test-Path $frameDir) { Remove-Item -Recurse -Force $frameDir -ErrorAction SilentlyContinue }
    if ($script:probePath -and (Test-Path $script:probePath)) { Remove-Item -Force $script:probePath -ErrorAction SilentlyContinue }
}

Log "Done. Clips in $motionDir (gitignored). Rendering mode: $($render.Mode)."
