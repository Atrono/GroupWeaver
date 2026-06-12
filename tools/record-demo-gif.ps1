<#
.SYNOPSIS
    Records the demo-mode exploration GIF (M2 milestone evidence; reusable for AP 3.5).

.DESCRIPTION
    Launches the app WITHOUT --demo, then clicks the "Demo mode" button via UIA -
    the script never touches "Connect to domain", so only demo data can reach the
    public artifact. The connect card itself is NEVER captured: its auth-context
    line renders the live operator identity (ConnectionViewModel), and the
    public-media rule forbids lab AD identities in published media. Scripted beats
    (recording starts at beat 2):

      1. Connect card -> UIA InvokePattern on "Demo mode" (off camera, no frames)
      2. Root picker  -> UIA ValuePattern filter "DL_FS-Finance_RW", select, Load
      3. Graph        -> posted WM_LBUTTON* click on the root node (detail panel)
      4. Lazy expand  -> posted double-click on the single External frontier node
                         (CN=GG_Finance_Staff resolves + 20 members: 2 -> 22 nodes)
      5. Zoom         -> posted WM_MOUSEWHEEL bursts aimed at the expanded cluster

    A frame is captured via tools/capture-window.ps1 (DPI-aware PrintWindow, window
    only - no desktop leakage) after each beat plus in-between, then assembled with
    ffmpeg palettegen/paletteuse into docs/media/m2-explore.gif.

    Driving technique per .claude/rules/lab-environment.md: this agent context has
    no interactive input desktop, so Avalonia chrome is driven via UIA patterns and
    the WebView canvas via WM_* messages posted to the Chrome_RenderWidgetHostHWND
    child. Graph nodes are located by pixel-hunting their kind colors (palette in
    src/App/Views/AdObjectKindConverters.cs) inside the child HWND region of the
    capture - self-correcting, no layout math.

    Runs under Windows PowerShell 5.1 (UIAutomationClient is a .NET Framework GAC
    assembly that pwsh/.NET 8 cannot load); invoking it with pwsh relaunches itself.
    Re-runnable: wipes its frame directory, builds the app if needed, always closes
    the app at the end.

.EXAMPLE
    pwsh tools/record-demo-gif.ps1
#>
[CmdletBinding()]
param(
    [string]$OutGif
)

$ErrorActionPreference = 'Stop'

# --- relaunch under Windows PowerShell 5.1 when started from pwsh -------------
if ($PSVersionTable.PSEdition -eq 'Core') {
    $ps51 = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
    $argList = @('-NoProfile', '-NonInteractive', '-ExecutionPolicy', 'Bypass', '-File', $PSCommandPath)
    if ($OutGif) { $argList += @('-OutGif', $OutGif) }
    & $ps51 @argList
    exit $LASTEXITCODE
}

Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.Drawing

Add-Type -Namespace GroupWeaver -Name Gif -ReferencedAssemblies System.Drawing -MemberDefinition @'
[DllImport("user32.dll")]
public static extern IntPtr SetThreadDpiAwarenessContext(IntPtr ctx);
[DllImport("user32.dll", SetLastError = true)]
public static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
[DllImport("user32.dll", SetLastError = true)]
public static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
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
'@

# Physical-pixel coordinates everywhere (lab rule: BEFORE any GetWindowRect).
[void][GroupWeaver.Gif]::SetThreadDpiAwarenessContext([IntPtr](-4))

$repoRoot = Split-Path -Parent $PSScriptRoot
if (-not $OutGif) { $OutGif = Join-Path $repoRoot 'docs\media\m2-explore.gif' }
$captureScript = Join-Path $PSScriptRoot 'capture-window.ps1'
$frameDir = Join-Path $repoRoot 'artifacts\ui\gif-frames'
$exe = Join-Path $repoRoot 'src\App\bin\Debug\net8.0-windows\GroupWeaver.App.exe'

# Node palette (src/App/Views/AdObjectKindConverters.cs / web/graph.js, pinned by
# WebBundleTests): rust = DomainLocalGroup, gray = External, green = GlobalGroup.
$colorRoot = @(0xA1, 0x40, 0x00)
$colorExternal = @(0x75, 0x75, 0x75)
$colorGlobalGroup = @(0x10, 0x7C, 0x10)

function Log([string]$msg) { Write-Host "[record-demo-gif $(Get-Date -Format HH:mm:ss)] $msg" }

# --- frame bookkeeping ---------------------------------------------------------
$script:frameIndex = 0
$script:lastFrame = $null

function Save-Frame([int]$copies = 1) {
    for ($i = 0; $i -lt $copies; $i++) {
        $name = 'frame_{0:000}.png' -f $script:frameIndex
        $path = Join-Path $frameDir $name
        & powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass `
            -File $captureScript -ProcessId $app.Id -OutFile $path | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "capture-window.ps1 failed for $name" }
        $script:frameIndex++
        $script:lastFrame = $path
    }
}

# Repeat the previous frame (a hold beat) without re-capturing.
function Hold-Frame([int]$copies = 1) {
    for ($i = 0; $i -lt $copies; $i++) {
        $name = 'frame_{0:000}.png' -f $script:frameIndex
        Copy-Item $script:lastFrame (Join-Path $frameDir $name)
        $script:frameIndex++
    }
}

# Probe capture for pixel-hunting; NOT part of the frame sequence.
function Save-Probe {
    $path = Join-Path $frameDir 'probe.png'
    & powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass `
        -File $captureScript -ProcessId $app.Id -OutFile $path | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'capture-window.ps1 failed for probe' }
    return $path
}

# --- UIA helpers (Avalonia chrome; only safe BEFORE the WebView HWND exists) ----
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
    Log "invoked button '$name'"
}

# --- posted-input helpers (the WebView canvas; lab-environment.md technique) -----
function Get-WindowRectOf([IntPtr]$hwnd) {
    $rect = New-Object GroupWeaver.Gif+RECT
    if (-not [GroupWeaver.Gif]::GetWindowRect($hwnd, [ref]$rect)) { throw "GetWindowRect failed for $hwnd" }
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
    $lp = [GroupWeaver.Gif]::MakeLParam($clientX, $clientY)
    [void][GroupWeaver.Gif]::PostMessage($chromiumHwnd, [GroupWeaver.Gif]::WM_MOUSEMOVE, [IntPtr]::Zero, $lp)
    Start-Sleep -Milliseconds 150
    [void][GroupWeaver.Gif]::PostMessage($chromiumHwnd, [GroupWeaver.Gif]::WM_MOUSEMOVE, [IntPtr]::Zero, $lp)
    Start-Sleep -Milliseconds 50
    $clicks = if ($double) { 2 } else { 1 }
    for ($i = 0; $i -lt $clicks; $i++) {
        [void][GroupWeaver.Gif]::PostMessage($chromiumHwnd, [GroupWeaver.Gif]::WM_LBUTTONDOWN, [IntPtr]1, $lp)
        [void][GroupWeaver.Gif]::PostMessage($chromiumHwnd, [GroupWeaver.Gif]::WM_LBUTTONUP, [IntPtr]::Zero, $lp)
        if ($double -and $i -eq 0) { Start-Sleep -Milliseconds 90 }
    }
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
    $lp = [GroupWeaver.Gif]::MakeLParam($screenX, $screenY)
    $wp = [IntPtr]([int64]120 -shl 16)
    for ($i = 0; $i -lt $ticks; $i++) {
        [void][GroupWeaver.Gif]::PostMessage($chromiumHwnd, [GroupWeaver.Gif]::WM_MOUSEWHEEL, $wp, $lp)
        Start-Sleep -Milliseconds 25
    }
}

# Densest blob of a palette color in a capture region - 'canvas' = the Chromium
# child rect, 'detail' = the Avalonia detail column right of it (kind badge!).
# Returns @{X=..;Y=..;Count=..} in capture coordinates or $null.
function Find-NodeBlob([string]$capturePath, [int[]]$rgb, [string]$region = 'canvas', [int]$minCount = 30) {
    $mainRect = Get-WindowRectOf $app.MainWindowHandle
    $childRect = Get-WindowRectOf $chromiumHwnd
    if ($region -eq 'detail') {
        $left = $childRect.Right - $mainRect.Left
        $right = $mainRect.Right - $mainRect.Left
    }
    else {
        $left = $childRect.Left - $mainRect.Left
        $right = $childRect.Right - $mainRect.Left
    }
    $blob = [GroupWeaver.Gif]::FindBlob(
        $capturePath,
        $left, ($childRect.Top - $mainRect.Top),
        $right, ($childRect.Bottom - $mainRect.Top),
        $rgb[0], $rgb[1], $rgb[2], 10, 24)
    if ($blob[2] -lt $minCount) { return $null }
    return @{ X = $blob[0]; Y = $blob[1]; Count = $blob[2] }
}

function Wait-NodeBlob([int[]]$rgb, [int]$timeoutSec, [string]$what, [string]$region = 'canvas') {
    $deadline = (Get-Date).AddSeconds($timeoutSec)
    while ($true) {
        $blob = Find-NodeBlob (Save-Probe) $rgb $region
        if ($blob) { return $blob }
        if ((Get-Date) -gt $deadline) { throw "timed out after ${timeoutSec}s waiting for $what" }
        Start-Sleep -Milliseconds 400
    }
}

# === build + launch ==============================================================
if (-not (Test-Path $exe)) {
    Log 'App binary missing - building src/App (Debug)...'
    & dotnet build (Join-Path $repoRoot 'src\App') -c Debug --nologo -v quiet
    if ($LASTEXITCODE -ne 0) { throw "dotnet build failed (exit $LASTEXITCODE)" }
}

if (Test-Path $frameDir) { Remove-Item -Recurse -Force $frameDir }
New-Item -ItemType Directory -Force $frameDir | Out-Null
$gifDir = Split-Path -Parent $OutGif
if ($gifDir -and -not (Test-Path $gifDir)) { New-Item -ItemType Directory -Force $gifDir | Out-Null }

# Launch WITHOUT --demo: the GIF must show "Demo mode" being clicked. The script
# NEVER invokes "Connect to domain" - demo data only reaches the public artifact.
Log 'Launching GroupWeaver (no args - demo is chosen on camera)...'
$app = Start-Process -FilePath $exe -PassThru

try {
    $deadline = (Get-Date).AddSeconds(30)
    while ($app.MainWindowHandle -eq [IntPtr]::Zero) {
        if ((Get-Date) -gt $deadline) { throw 'main window never appeared' }
        Start-Sleep -Milliseconds 250
        $app.Refresh()
    }

    # Deterministic media geometry: request a compact window; Avalonia clamps the
    # physical request up to its logical MinWidth/MinHeight (960x600) per the
    # window DPI - on this 200% box that lands at 1946x1271, the most readable
    # source for a 960px-wide GIF. SWP_NOZORDER|SWP_NOACTIVATE = 0x14 - posted
    # input needs no activation.
    [void][GroupWeaver.Gif]::SetWindowPos($app.MainWindowHandle, [IntPtr]::Zero, 60, 60, 1480, 920, 0x14)

    # --- beat 1: connect card, choose Demo mode (OFF CAMERA) --------------------
    # No frames here: the connect card's auth-context line shows the live operator
    # identity - it must never appear in public media. The GIF starts at beat 2.
    [void](Wait-Uia { Find-UiaFirst ([System.Windows.Automation.ControlType]::Button) 'Demo mode' } 30 "button 'Demo mode'")
    Start-Sleep -Milliseconds 500   # let the first layout/render settle
    Invoke-UiaButton 'Demo mode'

    # --- beat 2: root picker - filter, select, load ----------------------------
    [void](Wait-Uia { Find-UiaFirst ([System.Windows.Automation.ControlType]::ListItem) $null } 30 'root candidates')
    Start-Sleep -Milliseconds 300
    Save-Frame 2

    $filterBox = Wait-Uia { Find-UiaFirst ([System.Windows.Automation.ControlType]::Edit) $null } 10 'filter box'
    $filterText = 'DL_FS-Finance_RW'
    $valuePattern = $null
    if ($filterBox.TryGetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern, [ref]$valuePattern)) {
        $valuePattern.SetValue($filterText)
    }
    else {
        # Fallback: focus the box and type via WM_CHAR posted to the Avalonia window.
        $filterBox.SetFocus()
        Start-Sleep -Milliseconds 200
        foreach ($ch in $filterText.ToCharArray()) {
            [void][GroupWeaver.Gif]::PostMessage($app.MainWindowHandle, [GroupWeaver.Gif]::WM_CHAR, [IntPtr][int]$ch, [IntPtr]::Zero)
            Start-Sleep -Milliseconds 15
        }
    }
    Log "filtered root picker to '$filterText'"
    Start-Sleep -Milliseconds 500
    Save-Frame 1

    $item = Wait-Uia { Find-UiaFirst ([System.Windows.Automation.ControlType]::ListItem) $null } 10 'filtered candidate'
    $item.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
    Start-Sleep -Milliseconds 300
    Save-Frame 1
    Invoke-UiaButton 'Load'

    # --- beat 3: graph renders; click the root node ----------------------------
    # From here on UIA is useless (the Chromium HWND owns the UIA tree) - posted
    # WM_* + pixel-hunting only.
    $deadline = (Get-Date).AddSeconds(60)
    $chromiumHwnd = [IntPtr]::Zero
    while ($chromiumHwnd -eq [IntPtr]::Zero) {
        if ((Get-Date) -gt $deadline) { throw 'Chrome_RenderWidgetHostHWND never appeared - WebView2 missing?' }
        Start-Sleep -Milliseconds 500
        $app.Refresh()
        $chromiumHwnd = [GroupWeaver.Gif]::FindDescendantByClass($app.MainWindowHandle, 'Chrome_RenderWidgetHostHWND')
    }
    Log 'WebView2 canvas is up'

    [void](Wait-NodeBlob $colorRoot 30 'the root node (DomainLocalGroup rust)')
    Log 'graph rendered'
    Save-Frame 3

    # Self-verifying tap: success = the rust DL kind badge shows up in the detail
    # column. A failed tap may have DRAGGED the node, so re-probe every attempt.
    $selected = $false
    for ($attempt = 1; $attempt -le 4 -and -not $selected; $attempt++) {
        $rootBlob = Find-NodeBlob (Save-Probe) $colorRoot
        if (-not $rootBlob) { throw 'root node (rust) not found on the canvas' }
        Send-CanvasClick $rootBlob.X $rootBlob.Y $false
        try {
            [void](Wait-NodeBlob $colorRoot 3 'the DL badge in the detail panel' 'detail')
            $selected = $true
        }
        catch {
            Log "click attempt $attempt did not select - retrying"
        }
    }
    if (-not $selected) { throw 'root node click never populated the detail panel' }
    Log 'root node selected; detail panel populated'
    Save-Frame 3

    # --- beat 4: lazy expand via double-click on the External frontier node ----
    $expanded = $false
    for ($attempt = 1; $attempt -le 4 -and -not $expanded; $attempt++) {
        $extBlob = Find-NodeBlob (Save-Probe) $colorExternal
        if (-not $extBlob) { throw 'no External (gray) frontier node found in the initial graph' }
        Send-CanvasClick $extBlob.X $extBlob.Y $true
        try {
            # GG_Finance_Staff resolves GlobalGroup-green and brings 20 members.
            [void](Wait-NodeBlob $colorGlobalGroup 8 'the expanded GlobalGroup node')
            $expanded = $true
        }
        catch {
            Log "expand attempt $attempt did not take - retrying"
        }
    }
    if (-not $expanded) { throw 'lazy expand never happened (no GlobalGroup-green node appeared)' }
    Log 'graph expanded (External frontier resolved to GlobalGroup + members)'
    Start-Sleep -Milliseconds 700   # focus fit + detail re-projection settle
    Save-Frame 4

    # --- beat 5: wheel zoom-in toward the expanded cluster ----------------------
    # Aim at the GlobalGroup-green blob so the zoom visibly dives into the member
    # cluster (pointer-anchored zoom keeps it on screen across bursts).
    $zoomTarget = Find-NodeBlob (Save-Probe) $colorGlobalGroup
    for ($burst = 0; $burst -lt 3; $burst++) {
        if ($zoomTarget) { Send-CanvasWheel 30 $zoomTarget.X $zoomTarget.Y }
        else { Send-CanvasWheel 30 }
        Start-Sleep -Milliseconds 350
        Save-Frame 1
    }
    Save-Frame 2
    Hold-Frame 2
}
finally {
    if (-not $app.HasExited) {
        Log 'Closing the app...'
        [void]$app.CloseMainWindow()
        if (-not $app.WaitForExit(5000)) { $app.Kill() }
    }
}

# === assemble the GIF ============================================================
Remove-Item (Join-Path $frameDir 'probe.png') -ErrorAction SilentlyContinue

$ffmpeg = Get-Command ffmpeg -ErrorAction SilentlyContinue
if (-not $ffmpeg) {
    # Same-session choco install: PATH not refreshed yet.
    $env:Path = [Environment]::GetEnvironmentVariable('Path', 'Machine') + ';' +
                [Environment]::GetEnvironmentVariable('Path', 'User') + ';' + $env:Path
    $ffmpeg = Get-Command ffmpeg -ErrorAction SilentlyContinue
}
if (-not $ffmpeg) { throw 'ffmpeg not found - run tools/bootstrap.ps1 (choco install ffmpeg)' }

Log "Assembling $OutGif from $script:frameIndex frames..."
# 2 fps storytelling pace; 960px wide; palettegen/paletteuse for a small, clean GIF.
& $ffmpeg.Source -y -loglevel error -framerate 2 -i (Join-Path $frameDir 'frame_%03d.png') `
    -vf 'scale=960:-1:flags=lanczos,split[s0][s1];[s0]palettegen=stats_mode=diff[p];[s1][p]paletteuse=dither=bayer:bayer_scale=4' `
    -loop 0 $OutGif
if ($LASTEXITCODE -ne 0) { throw "ffmpeg failed (exit $LASTEXITCODE)" }

$gifItem = Get-Item $OutGif
Log ("Done: {0} ({1:N0} KB, {2} frames)" -f $gifItem.FullName, ($gifItem.Length / 1KB), $script:frameIndex)
Log "Frames kept for inspection in $frameDir (gitignored)"
