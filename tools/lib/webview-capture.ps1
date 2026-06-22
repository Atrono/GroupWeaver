<#
.SYNOPSIS
    Shared, dot-sourceable WebView/Avalonia capture + posted-input library.

.DESCRIPTION
    The proven, reusable pieces that drive a live windowed GroupWeaver --demo run
    headlessly on this lab box (no interactive input desktop): the P/Invoke surface
    (PrintWindow, GetWindowRect, SetThreadDpiAwarenessContext, PostMessage,
    EnumChildWindows/GetClassName, MakeLParam, the FindBlob blob-hunter), DPI-aware
    init, the WebView-canvas input helpers (Send-CanvasClick / Send-CanvasWheel /
    Find-NodeBlob / Wait-NodeBlob), and the Avalonia UIA chrome helpers
    (Find-UiaFirst / Wait-Uia / Invoke-UiaButton).

    These were extracted VERBATIM from tools/record-demo-gif.ps1 (M2 GIF harness) so
    both record-demo-gif.ps1 and tools/capture-motion.ps1 share one source of the
    fiddly, lab-environment.md-documented techniques (DPI awareness BEFORE
    GetWindowRect, PW_RENDERFULLCONTENT so WebView2 isn't black, posting WM_* to the
    Chrome_RenderWidgetHostHWND child, cytoscape discrete-wheel normalization, pixel
    hunting rendered node colors).

    Read-only / dev tooling only: pure capture + analysis, zero AD or src/ writes.

    Contract for callers:
      * Dot-source this file: . "$PSScriptRoot/lib/webview-capture.ps1"
      * Requires Windows PowerShell 5.1 if the UIA helpers are used
        (UIAutomationClient is a .NET Framework GAC assembly pwsh/.NET 8 can't load).
      * Several helpers read caller-scoped variables by convention, exactly as the
        inline originals did: $app (the launched Process), $chromiumHwnd (the
        Chrome_RenderWidgetHostHWND IntPtr). The caller owns launch/teardown.

.NOTES
    Dot-sourcing this file runs Add-Type for GroupWeaver.WebViewCapture and calls
    SetThreadDpiAwarenessContext(-4). Idempotent: a second dot-source is a no-op for
    the type (Add-Type throws on redefine, so it's guarded).
#>

if (-not ('GroupWeaver.WebViewCapture' -as [type])) {
    Add-Type -AssemblyName System.Drawing
    Add-Type -Namespace GroupWeaver -Name WebViewCapture -ReferencedAssemblies System.Drawing -MemberDefinition @'
[DllImport("user32.dll")]
public static extern IntPtr SetThreadDpiAwarenessContext(IntPtr ctx);
[DllImport("user32.dll", SetLastError = true)]
public static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
[DllImport("user32.dll", SetLastError = true)]
public static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
[DllImport("user32.dll", SetLastError = true)]
public static extern bool PrintWindow(IntPtr hWnd, IntPtr hdc, uint flags);
[DllImport("user32.dll")]
public static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
[DllImport("user32.dll")]
public static extern bool EnumChildWindows(IntPtr parent, EnumProc proc, IntPtr lParam);
[DllImport("user32.dll", CharSet = CharSet.Unicode)]
public static extern int GetClassName(IntPtr hWnd, System.Text.StringBuilder name, int max);

public delegate bool EnumProc(IntPtr hWnd, IntPtr lParam);

[StructLayout(LayoutKind.Sequential)]
public struct RECT { public int Left, Top, Right, Bottom; }

public const uint WM_MOUSEMOVE = 0x0200;
public const uint WM_LBUTTONDOWN = 0x0201;
public const uint WM_LBUTTONUP = 0x0202;
public const uint WM_MOUSEWHEEL = 0x020A;
public const uint WM_CHAR = 0x0102;
public const uint WM_KEYDOWN = 0x0100;
public const uint WM_KEYUP = 0x0101;

public static IntPtr MakeLParam(int x, int y) {
    return (IntPtr)(((y & 0xFFFF) << 16) | (x & 0xFFFF));
}

// EnumChildWindows already recurses through all descendants.
private static IntPtr _found;
private static string _wanted;
private static bool Probe(IntPtr hWnd, IntPtr lParam) {
    var sb = new System.Text.StringBuilder(256);
    GetClassName(hWnd, sb, sb.Capacity);
    if (sb.ToString() == _wanted) { _found = hWnd; return false; }
    return true;
}
public static IntPtr FindDescendantByClass(IntPtr parent, string className) {
    _found = IntPtr.Zero;
    _wanted = className;
    EnumChildWindows(parent, Probe, IntPtr.Zero);
    return _found;
}

// Densest blob of pixels matching (r,g,b) within tol per channel inside the given
// capture-PNG region; returns {centroidX, centroidY, matchCount} or {-1,-1,0}.
// Grid-binned (cell px) so the centroid is taken over the densest 3x3 neighborhood
// - lands inside the node shape even when several same-color blobs exist.
public static int[] FindBlob(string path, int left, int top, int right, int bottom,
                             int r, int g, int b, int tol, int cell) {
    using (var bmp = new System.Drawing.Bitmap(path)) {
        left = Math.Max(0, left); top = Math.Max(0, top);
        right = Math.Min(bmp.Width, right); bottom = Math.Min(bmp.Height, bottom);
        int cols = (bmp.Width / cell) + 1;
        var count = new System.Collections.Generic.Dictionary<int, int>();
        var sumX = new System.Collections.Generic.Dictionary<int, long>();
        var sumY = new System.Collections.Generic.Dictionary<int, long>();
        var data = bmp.LockBits(new System.Drawing.Rectangle(0, 0, bmp.Width, bmp.Height),
            System.Drawing.Imaging.ImageLockMode.ReadOnly,
            System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        try {
            var row = new byte[data.Stride];
            for (int y = top; y < bottom; y++) {
                var rowPtr = new IntPtr(data.Scan0.ToInt64() + (long)y * data.Stride);
                System.Runtime.InteropServices.Marshal.Copy(rowPtr, row, 0, data.Stride);
                for (int x = left; x < right; x++) {
                    int px = x * 4;
                    if (Math.Abs(row[px + 2] - r) <= tol && Math.Abs(row[px + 1] - g) <= tol && Math.Abs(row[px] - b) <= tol) {
                        int key = (y / cell) * cols + (x / cell);
                        int c; count.TryGetValue(key, out c); count[key] = c + 1;
                        long sx; sumX.TryGetValue(key, out sx); sumX[key] = sx + x;
                        long sy; sumY.TryGetValue(key, out sy); sumY[key] = sy + y;
                    }
                }
            }
        } finally { bmp.UnlockBits(data); }
        int bestKey = -1, bestCount = 0;
        foreach (var kv in count) {
            if (kv.Value > bestCount) { bestCount = kv.Value; bestKey = kv.Key; }
        }
        if (bestKey < 0) { return new int[] { -1, -1, 0 }; }
        long tx = 0, ty = 0; int tc = 0;
        int bestRow = bestKey / cols, bestCol = bestKey % cols;
        foreach (var kv in count) {
            int krow = kv.Key / cols, kcol = kv.Key % cols;
            if (Math.Abs(krow - bestRow) <= 1 && Math.Abs(kcol - bestCol) <= 1) {
                tc += kv.Value; tx += sumX[kv.Key]; ty += sumY[kv.Key];
            }
        }
        return new int[] { (int)(tx / tc), (int)(ty / tc), tc };
    }
}

// In-process PrintWindow burst: capture N frames into PNGs as fast as possible,
// reusing one Bitmap+Graphics so there is no per-frame process-spawn cost (the cap
// that pins record-demo-gif at ~2fps). DPI-aware GetWindowRect, PW_RENDERFULLCONTENT
// (0x2) so the WebView2 canvas isn't black. Returns the wall-clock milliseconds the
// whole burst took so the caller can assemble at the MEASURED framerate. frameDir
// must already exist; files are frame_000.png.. .  intervalMs throttles the loop
// (0 = as fast as possible).
public static long CaptureBurst(IntPtr hWnd, string frameDir, int frameCount, int intervalMs) {
    RECT rect;
    if (!GetWindowRect(hWnd, out rect)) { throw new System.Exception("GetWindowRect failed in CaptureBurst"); }
    int width = rect.Right - rect.Left;
    int height = rect.Bottom - rect.Top;
    var sw = System.Diagnostics.Stopwatch.StartNew();
    using (var bmp = new System.Drawing.Bitmap(width, height))
    using (var gfx = System.Drawing.Graphics.FromImage(bmp)) {
        for (int i = 0; i < frameCount; i++) {
            long frameStart = sw.ElapsedMilliseconds;
            IntPtr hdc = gfx.GetHdc();
            try { PrintWindow(hWnd, hdc, 0x2); }
            finally { gfx.ReleaseHdc(hdc); }
            string name = System.IO.Path.Combine(frameDir, string.Format("frame_{0:000}.png", i));
            bmp.Save(name, System.Drawing.Imaging.ImageFormat.Png);
            if (intervalMs > 0) {
                long spent = sw.ElapsedMilliseconds - frameStart;
                int wait = intervalMs - (int)spent;
                if (wait > 0) { System.Threading.Thread.Sleep(wait); }
            }
        }
    }
    sw.Stop();
    return sw.ElapsedMilliseconds;
}
'@
}

# Physical-pixel coordinates everywhere (lab rule: BEFORE any GetWindowRect).
[void][GroupWeaver.WebViewCapture]::SetThreadDpiAwarenessContext([IntPtr](-4))

# --- UIA helpers (Avalonia chrome; only safe BEFORE the WebView HWND exists) ----
# Reads the caller-scoped $app (the launched Process), exactly as the inline original.
function Get-UiaRoot {
    $app.Refresh()
    return [System.Windows.Automation.AutomationElement]::FromHandle($app.MainWindowHandle)
}

function Find-UiaFirst([System.Windows.Automation.ControlType]$type, [string]$name) {
    $conds = New-Object System.Collections.Generic.List[System.Windows.Automation.Condition]
    $conds.Add((New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty, $type)))
    if ($name) {
        $conds.Add((New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::NameProperty, $name)))
    }
    $cond = if ($conds.Count -eq 1) { $conds[0] } else {
        New-Object System.Windows.Automation.AndCondition($conds.ToArray())
    }
    return (Get-UiaRoot).FindFirst([System.Windows.Automation.TreeScope]::Descendants, $cond)
}

function Wait-Uia([scriptblock]$probe, [int]$timeoutSec, [string]$what) {
    $deadline = (Get-Date).AddSeconds($timeoutSec)
    while ((Get-Date) -lt $deadline) {
        $el = & $probe
        if ($el) { return $el }
        Start-Sleep -Milliseconds 250
    }
    throw "timed out after ${timeoutSec}s waiting for $what"
}

function Invoke-UiaButton([string]$name) {
    $btn = Wait-Uia { Find-UiaFirst ([System.Windows.Automation.ControlType]::Button) $name } 30 "button '$name'"
    $btn.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    Write-Host "[webview-capture] invoked button '$name'"
}

# --- posted-input helpers (the WebView canvas; lab-environment.md technique) -----
# Read caller-scoped $app + $chromiumHwnd, exactly as the inline originals did.
function Get-WindowRectOf([IntPtr]$hwnd) {
    $rect = New-Object GroupWeaver.WebViewCapture+RECT
    if (-not [GroupWeaver.WebViewCapture]::GetWindowRect($hwnd, [ref]$rect)) { throw "GetWindowRect failed for $hwnd" }
    return $rect
}

# Click at capture-PNG coordinates: capture(0,0) = main-window rect origin, so
# screen = mainRect + capturePoint and child-client = screen - childRect origin.
# Any mousemove Chromium delivers between DOWN and UP turns the cytoscape tap
# into a node DRAG (observed: a synthesized hover recompute around the first
# interaction) - so park the hover on the target first, let it settle, and post
# DOWN/UP back-to-back with no sleep in between.
function Send-CanvasClick([int]$captureX, [int]$captureY, [bool]$double) {
    $mainRect = Get-WindowRectOf $app.MainWindowHandle
    $childRect = Get-WindowRectOf $chromiumHwnd
    $clientX = $mainRect.Left + $captureX - $childRect.Left
    $clientY = $mainRect.Top + $captureY - $childRect.Top
    $lp = [GroupWeaver.WebViewCapture]::MakeLParam($clientX, $clientY)
    [void][GroupWeaver.WebViewCapture]::PostMessage($chromiumHwnd, [GroupWeaver.WebViewCapture]::WM_MOUSEMOVE, [IntPtr]::Zero, $lp)
    Start-Sleep -Milliseconds 150
    [void][GroupWeaver.WebViewCapture]::PostMessage($chromiumHwnd, [GroupWeaver.WebViewCapture]::WM_MOUSEMOVE, [IntPtr]::Zero, $lp)
    Start-Sleep -Milliseconds 50
    $clicks = if ($double) { 2 } else { 1 }
    for ($i = 0; $i -lt $clicks; $i++) {
        [void][GroupWeaver.WebViewCapture]::PostMessage($chromiumHwnd, [GroupWeaver.WebViewCapture]::WM_LBUTTONDOWN, [IntPtr]1, $lp)
        [void][GroupWeaver.WebViewCapture]::PostMessage($chromiumHwnd, [GroupWeaver.WebViewCapture]::WM_LBUTTONUP, [IntPtr]::Zero, $lp)
        if ($double -and $i -eq 0) { Start-Sleep -Milliseconds 90 }
    }
}

# Background drag = cytoscape PAN. A WM_MOUSEMOVE with MK_LBUTTON held between DOWN
# and UP is exactly the "tap becomes a drag" case Send-CanvasClick avoids - here we
# WANT it, on EMPTY canvas, so cytoscape grab-pans the viewport by the drag delta
# (callers must NOT start on a node, or it drags the node instead). Capture coords,
# converted to child-client like Send-CanvasClick; stepped moves so the pan registers.
function Send-CanvasDrag([int]$fromX, [int]$fromY, [int]$toX, [int]$toY) {
    $mainRect = Get-WindowRectOf $app.MainWindowHandle
    $childRect = Get-WindowRectOf $chromiumHwnd
    $lpFor = {
        param([int]$cx, [int]$cy)
        [GroupWeaver.WebViewCapture]::MakeLParam(
            $mainRect.Left + $cx - $childRect.Left,
            $mainRect.Top + $cy - $childRect.Top)
    }
    # Park hover at the start, settle, then press (MK_LBUTTON = 1).
    [void][GroupWeaver.WebViewCapture]::PostMessage($chromiumHwnd, [GroupWeaver.WebViewCapture]::WM_MOUSEMOVE, [IntPtr]::Zero, (& $lpFor $fromX $fromY))
    Start-Sleep -Milliseconds 120
    [void][GroupWeaver.WebViewCapture]::PostMessage($chromiumHwnd, [GroupWeaver.WebViewCapture]::WM_LBUTTONDOWN, [IntPtr]1, (& $lpFor $fromX $fromY))
    Start-Sleep -Milliseconds 40
    $steps = 12
    for ($s = 1; $s -le $steps; $s++) {
        $ix = [int]($fromX + ($toX - $fromX) * $s / $steps)
        $iy = [int]($fromY + ($toY - $fromY) * $s / $steps)
        [void][GroupWeaver.WebViewCapture]::PostMessage($chromiumHwnd, [GroupWeaver.WebViewCapture]::WM_MOUSEMOVE, [IntPtr]1, (& $lpFor $ix $iy))
        Start-Sleep -Milliseconds 20
    }
    [void][GroupWeaver.WebViewCapture]::PostMessage($chromiumHwnd, [GroupWeaver.WebViewCapture]::WM_LBUTTONUP, [IntPtr]::Zero, (& $lpFor $toX $toY))
    Start-Sleep -Milliseconds 60
}

# WM_MOUSEWHEEL carries SCREEN coordinates (unlike the button messages). Optional
# capture coordinates aim the wheel (cytoscape zooms toward the pointer); default
# is the canvas center. One message per detent, MANY detents: cytoscape detects a
# discrete wheel after 4 events and normalizes EVERY detent to ~x1.0055 zoom
# (3/250 * wheelSensitivity 0.2 in the exponent) no matter how large the delta -
# inflating wParam is pointless, only the detent COUNT moves the needle
# (measured: ~x1.008 per detent, so ~90 detents per x2 of legible zoom).
function Send-CanvasWheel([int]$ticks, [int]$captureX = -1, [int]$captureY = -1) {
    $childRect = Get-WindowRectOf $chromiumHwnd
    if ($captureX -ge 0) {
        $mainRect = Get-WindowRectOf $app.MainWindowHandle
        $screenX = $mainRect.Left + $captureX
        $screenY = $mainRect.Top + $captureY
    }
    else {
        $screenX = [int](($childRect.Left + $childRect.Right) / 2)
        $screenY = [int](($childRect.Top + $childRect.Bottom) / 2)
    }
    $lp = [GroupWeaver.WebViewCapture]::MakeLParam($screenX, $screenY)
    $wp = [IntPtr]([int64]120 -shl 16)
    for ($i = 0; $i -lt $ticks; $i++) {
        [void][GroupWeaver.WebViewCapture]::PostMessage($chromiumHwnd, [GroupWeaver.WebViewCapture]::WM_MOUSEWHEEL, $wp, $lp)
        Start-Sleep -Milliseconds 25
    }
}

# Native Avalonia keyboard shortcut: post WM_KEYDOWN+WM_KEYUP for a virtual-key code
# DIRECTLY to the main-window HWND (NOT the Chrome child), so it reaches Avalonia's
# WndProc -> Window.OnKeyDown regardless of which child holds Win32 focus - PostMessage
# to a specific HWND bypasses focus routing (the WebView canvas can hold focus after a
# click, yet a single key still lands). SINGLE keys only: a chord (Ctrl+X) would need the
# modifier's async key STATE set, which PostMessage cannot establish - so the demo focus
# toggle is the single 'F' key (ADR-022 addendum), not Ctrl+B. lParam 0 (Avalonia's key
# translation does not read the repeat-count/scan-code bits here).
function Send-Key([int]$vk) {
    [void][GroupWeaver.WebViewCapture]::PostMessage($app.MainWindowHandle, [GroupWeaver.WebViewCapture]::WM_KEYDOWN, [IntPtr]$vk, [IntPtr]::Zero)
    Start-Sleep -Milliseconds 40
    [void][GroupWeaver.WebViewCapture]::PostMessage($app.MainWindowHandle, [GroupWeaver.WebViewCapture]::WM_KEYUP, [IntPtr]$vk, [IntPtr]::Zero)
}

# Densest blob of a palette color in a capture region - 'canvas' = the Chromium
# child rect, 'detail' = the Avalonia detail column right of it (kind badge!).
# Returns @{X=..;Y=..;Count=..} in capture coordinates or $null.
# $capturePath must be a PNG of the MAIN window (capture(0,0) = main-window origin).
function Find-NodeBlob([string]$capturePath, [int[]]$rgb, [string]$region = 'canvas', [int]$minCount = 30, [int]$minX = 0) {
    $mainRect = Get-WindowRectOf $app.MainWindowHandle
    $childRect = Get-WindowRectOf $chromiumHwnd
    if ($region -eq 'detail') {
        $left = $childRect.Right - $mainRect.Left
        $right = $mainRect.Right - $mainRect.Left
    }
    else {
        # $minX (capture coords) lets a caller push the left bound right of the root
        # node / legend swatch column - both share the node palette and would
        # otherwise win the blob (#78 diagnosis 2026-06-17).
        $left = $childRect.Left - $mainRect.Left
        if ($minX -gt $left) { $left = $minX }
        $right = $childRect.Right - $mainRect.Left
    }
    $blob = [GroupWeaver.WebViewCapture]::FindBlob(
        $capturePath,
        $left, ($childRect.Top - $mainRect.Top),
        $right, ($childRect.Bottom - $mainRect.Top),
        $rgb[0], $rgb[1], $rgb[2], 10, 24)
    if ($blob[2] -lt $minCount) { return $null }
    return @{ X = $blob[0]; Y = $blob[1]; Count = $blob[2] }
}

# Wait until a node blob of $rgb appears. $probeFactory is a scriptblock returning a
# fresh capture PNG path (caller owns HOW it captures - single-shot or burst-tail) so
# the lib stays agnostic to the caller's capture cadence.
function Wait-NodeBlob([scriptblock]$probeFactory, [int[]]$rgb, [int]$timeoutSec, [string]$what, [string]$region = 'canvas', [int]$minX = 0) {
    $deadline = (Get-Date).AddSeconds($timeoutSec)
    while ($true) {
        $blob = Find-NodeBlob (& $probeFactory) $rgb $region 30 $minX
        if ($blob) { return $blob }
        if ((Get-Date) -gt $deadline) { throw "timed out after ${timeoutSec}s waiting for $what" }
        Start-Sleep -Milliseconds 400
    }
}
