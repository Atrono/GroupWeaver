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
      6. Focus mode   -> posted single 'F' key (ADR-022 addendum) hides the command
                         strip + right rail; the graph goes edge-to-edge (presentation)

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

# P/Invoke surface + input/hunt/UIA helpers (DPI awareness is initialised on dot-source).
# Extracted to the shared lib so capture-motion.ps1 reuses the identical techniques.
. (Join-Path $PSScriptRoot 'lib\webview-capture.ps1')

$repoRoot = Split-Path -Parent $PSScriptRoot
if (-not $OutGif) { $OutGif = Join-Path $repoRoot 'docs\media\m2-explore.gif' }
$captureScript = Join-Path $PSScriptRoot 'capture-window.ps1'
$frameDir = Join-Path $repoRoot 'artifacts\ui\gif-frames'
$exe = Join-Path $repoRoot 'src\App\bin\Debug\net8.0-windows\GroupWeaver.App.exe'

# Node-hunt colors = the RENDERED dark-theme node colors, NOT the source palette
# (src/App/Views/AdObjectKindConverters.cs / web/graph.js). In the dark theme a node
# is a blue-gray fill with a thin kind-COLORED BORDER, so the border renders blended,
# well off the flat palette: DL rust 0xA14000 -> ~(136,94,69) on the canvas, while the
# LEGEND swatch keeps the exact palette 0xA14000 - hunting the rendered color is what
# distinguishes the graph node from the legend swatch (diagnosed 2026-06-17, #78).
$colorRoot = @(136, 94, 69)
$colorGlobalGroup = @(0x10, 0x7C, 0x10)
# The Avalonia detail-panel kind badge (unlike the cytoscape node) uses the EXACT
# source palette, same as the legend swatch - so the "node selected" confirmation
# hunts the palette rust, not the blended canvas color.
$colorRootBadge = @(0xA1, 0x40, 0x00)
# The External frontier node (GG_Finance_Staff, unresolved) renders BLUE on the dark
# canvas - NOT the legend's gray "External" swatch. It sits to the RIGHT of the rust
# root, so beat 4 hunts this blue right of the root's x (skips root + legend).
$colorExternalNode = @(49, 85, 115)

function Log([string]$msg) { Write-Host "[record-demo-gif $(Get-Date -Format HH:mm:ss)] $msg" }

# --- frame bookkeeping ---------------------------------------------------------
$script:frameIndex = 0
$script:lastFrame = $null

# PrintWindow on the WebView2 layer lags one compositor batch (lab-environment.md):
# the FIRST capture after a view/graph mutation returns the PREVIOUS frame. Capture
# twice back-to-back (no sleep between - the per-call powershell.exe spawn is the
# settle) and keep the second, so what lands is the live frame, never a stale one.
# This is what kept the click loop hunting the rust DL badge in a stale ROOT-PICKER
# frame (and clicking empty canvas) instead of the rendered graph node.
function Capture-Live([string]$path, [string]$label) {
    foreach ($pass in 1..2) {
        & powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass `
            -File $captureScript -ProcessId $app.Id -OutFile $path | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "capture-window.ps1 failed for $label" }
    }
}

function Save-Frame([int]$copies = 1) {
    for ($i = 0; $i -lt $copies; $i++) {
        $name = 'frame_{0:000}.png' -f $script:frameIndex
        $path = Join-Path $frameDir $name
        Capture-Live $path $name
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
    Capture-Live $path 'probe'
    return $path
}

# UIA helpers (Find-UiaFirst / Wait-Uia / Invoke-UiaButton), posted-input helpers
# (Get-WindowRectOf / Send-CanvasClick / Send-CanvasWheel / Find-NodeBlob /
# Wait-NodeBlob) come from tools/lib/webview-capture.ps1 (dot-sourced above). They
# read the caller-scoped $app + $chromiumHwnd by convention, exactly as the inline
# originals did. Wait-NodeBlob takes a probe-factory scriptblock so the caller owns
# HOW it captures - here that is this script's Save-Probe (double-capture lag fix).

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

# Launch WITHOUT --demo and UIA-click "Demo mode" OFF CAMERA (no frame captured
# before the root picker - the idle connect card renders the live operator
# identity). The script NEVER invokes "Connect to domain" - demo data only.
Log 'Launching GroupWeaver (no args - demo chosen via UIA, off camera)...'
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
    [void][GroupWeaver.WebViewCapture]::SetWindowPos($app.MainWindowHandle, [IntPtr]::Zero, 60, 60, 1480, 920, 0x14)

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
            [void][GroupWeaver.WebViewCapture]::PostMessage($app.MainWindowHandle, [GroupWeaver.WebViewCapture]::WM_CHAR, [IntPtr][int]$ch, [IntPtr]::Zero)
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
        $chromiumHwnd = [GroupWeaver.WebViewCapture]::FindDescendantByClass($app.MainWindowHandle, 'Chrome_RenderWidgetHostHWND')
    }
    Log 'WebView2 canvas is up'

    # The 2-node root scope (DL_FS-Finance_RW + its single External member) fits very
    # zoomed-in, and the larger #87 encoding-key legend can tuck the rust root UNDER
    # its top-left panel - so gate "rendered" on the External frontier node (blue,
    # centre, never under the legend), NOT the now-occludable root. Then pan the graph
    # down-right so the root clears the legend: this both lets the rust hunt find it
    # AND keeps the captured frame from showing a node half-hidden behind the legend.
    [void](Wait-NodeBlob { Save-Probe } $colorExternalNode 30 'the external frontier node (render signal)')
    Send-CanvasDrag 620 280 940 470
    Start-Sleep -Milliseconds 500
    [void](Wait-NodeBlob { Save-Probe } $colorRoot 30 'the root node (DomainLocalGroup rust)')
    Log 'graph rendered (panned clear of the legend)'
    Save-Frame 3

    # Self-verifying tap: success = the rust DL kind badge shows up in the detail
    # column. A failed tap may have DRAGGED the node, so re-probe every attempt.
    $selected = $false
    for ($attempt = 1; $attempt -le 4 -and -not $selected; $attempt++) {
        $rootBlob = Find-NodeBlob (Save-Probe) $colorRoot
        if (-not $rootBlob) { throw 'root node (rust) not found on the canvas' }
        Send-CanvasClick $rootBlob.X $rootBlob.Y $false
        try {
            [void](Wait-NodeBlob { Save-Probe } $colorRootBadge 3 'the DL badge in the detail panel' 'detail')
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
    # The External (GG_Finance_Staff) renders BLUE, to the RIGHT of the rust root; the
    # legend's "External" swatch is gray and was the old false target. Hunt the blue
    # right of the root's x (skips the root + legend swatch column) and double-click to
    # resolve it. Confirm via a real GlobalGroup-green node appearing IN THE CANVAS,
    # right of the legend's green swatch ($minX skips that always-present false match).
    $expanded = $false
    for ($attempt = 1; $attempt -le 3 -and -not $expanded; $attempt++) {
        $extBlob = Find-NodeBlob (Save-Probe) $colorExternalNode 'canvas' 30 ($rootBlob.X + 200)
        if (-not $extBlob) { throw 'External frontier node (blue, right of root) not found' }
        Send-CanvasClick $extBlob.X $extBlob.Y $true
        try {
            # GG_Finance_Staff resolves GlobalGroup-green and brings 20 members.
            [void](Wait-NodeBlob { Save-Probe } $colorGlobalGroup 6 'the expanded GlobalGroup node' 'canvas' 150)
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
    # Aim at the GlobalGroup-green node ($minX skips the legend swatch) so the zoom
    # visibly dives into the member cluster (pointer-anchored zoom keeps it on screen).
    $zoomTarget = Find-NodeBlob (Save-Probe) $colorGlobalGroup 'canvas' 30 150
    for ($burst = 0; $burst -lt 3; $burst++) {
        if ($zoomTarget) { Send-CanvasWheel 30 $zoomTarget.X $zoomTarget.Y }
        else { Send-CanvasWheel 30 }
        Start-Sleep -Milliseconds 350
        Save-Frame 1
    }
    Save-Frame 1

    # --- beat 6: focus (presentation) mode - hide the chrome, graph edge-to-edge -
    # Post the single 'F' key (ADR-022 addendum: F toggles focus mode in the workspace)
    # to the Avalonia window. The top command strip + the right rail collapse, leaving
    # the graph the full width. VK_F = 0x46, posted to the MAIN-window HWND so it reaches
    # Window.OnKeyDown even though the WebView canvas holds Win32 focus after the zoom.
    # We deliberately do NOT use F11 full-screen: that drops the title bar and changes the
    # capture dimensions mid-GIF; focus mode keeps the window size, so the frame stays the
    # same while the chrome melts away. Hold on the clean view as the finale.
    Log 'entering focus mode (F) - hiding panels for the presentation finale'
    Send-Key 0x46
    Start-Sleep -Milliseconds 900   # strip + rail collapse + WebView reflow settle
    Save-Frame 2
    Hold-Frame 3
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
