<#
.SYNOPSIS
    DPI-aware PrintWindow capture of a live top-level window to a PNG.

.DESCRIPTION
    Captures a process's main window — including DirectComposition-rendered
    content such as WebView2 (PW_RENDERFULLCONTENT) — at physical pixel size.
    Sets the thread to PER_MONITOR_AWARE_V2 first: this desktop runs at >100%
    DPI scale and an unaware GetWindowRect returns virtualized coordinates that
    crop the right edge (.claude/rules/lab-environment.md). Runs under both
    Windows PowerShell 5.1 and pwsh 7 (System.Drawing is present in either).

.PARAMETER ProcessId
    Process whose MainWindowHandle is captured.

.PARAMETER OutFile
    Target PNG path; parent directories are created.

.EXAMPLE
    pwsh tools/capture-window.ps1 -ProcessId $app.Id -OutFile artifacts/ui/workspace-live-graph.png
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][int]$ProcessId,
    [Parameter(Mandatory)][string]$OutFile
)

$ErrorActionPreference = 'Stop'

Add-Type -AssemblyName System.Drawing
Add-Type -Namespace GroupWeaver -Name Capture -MemberDefinition @'
[DllImport("user32.dll")]
public static extern IntPtr SetThreadDpiAwarenessContext(IntPtr ctx);
[DllImport("user32.dll", SetLastError = true)]
public static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
[DllImport("user32.dll", SetLastError = true)]
public static extern bool PrintWindow(IntPtr hWnd, IntPtr hdc, uint flags);
[StructLayout(LayoutKind.Sequential)]
public struct RECT { public int Left, Top, Right, Bottom; }
'@

# DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2 = -4 (lab rule: BEFORE GetWindowRect).
[void][GroupWeaver.Capture]::SetThreadDpiAwarenessContext([IntPtr](-4))

$process = Get-Process -Id $ProcessId
$hwnd = $process.MainWindowHandle
if ($hwnd -eq [IntPtr]::Zero) {
    throw "process $ProcessId ($($process.ProcessName)) has no main window"
}

$rect = New-Object GroupWeaver.Capture+RECT
if (-not [GroupWeaver.Capture]::GetWindowRect($hwnd, [ref]$rect)) {
    throw "GetWindowRect failed for hwnd $hwnd"
}
$width = $rect.Right - $rect.Left
$height = $rect.Bottom - $rect.Top

$bitmap = New-Object System.Drawing.Bitmap($width, $height)
try {
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    try {
        $hdc = $graphics.GetHdc()
        try {
            # 0x2 = PW_RENDERFULLCONTENT: without it, GPU-composited child windows
            # (the WebView2/Chromium HWND) come back black.
            $ok = [GroupWeaver.Capture]::PrintWindow($hwnd, $hdc, 0x2)
        }
        finally {
            $graphics.ReleaseHdc($hdc)
        }
        if (-not $ok) {
            throw "PrintWindow failed for hwnd $hwnd"
        }
    }
    finally {
        $graphics.Dispose()
    }

    $directory = Split-Path -Parent $OutFile
    if ($directory -and -not (Test-Path $directory)) {
        New-Item -ItemType Directory -Force $directory | Out-Null
    }
    $bitmap.Save($OutFile, [System.Drawing.Imaging.ImageFormat]::Png)
}
finally {
    $bitmap.Dispose()
}

Write-Host "captured ${width}x${height} px -> $OutFile"
