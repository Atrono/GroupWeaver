<#
.SYNOPSIS
    On-failure evidence collectors for the E2E harness (ADR-038 D5, WP4 #243).

.DESCRIPTION
    Dot-sourced by e2e-driver.ps1 (never standalone). Windows PowerShell 5.1,
    ASCII-ONLY (lab rule). Every collector is READ-ONLY: captures, enumerations,
    and event-log reads - nothing here mutates system or app state, and nothing
    touches AD.

    Collectors (each isolated in try/catch so evidence collection can never mask
    the original failure):
      * final-frame burst        - 3 PrintWindow frames of the main window
      * UIA tree dump            - bounded ControlView walk (NOTE: post-WebView
                                   the tree shows Chromium content ONLY)
      * child-HWND inventory     - class/visibility/rect per descendant window
      * event-log excerpt        - Application log, crash-relevant providers,
                                   last 10 minutes (Get-WinEvent, read-only)
      * WER dump scan            - %LOCALAPPDATA%\CrashDumps newer than run start
#>

if (-not ('GroupWeaver.E2eEvidenceNative' -as [type])) {
    Add-Type -Namespace GroupWeaver -Name E2eEvidenceNative -MemberDefinition @'
[StructLayout(LayoutKind.Sequential)]
public struct RECT3 { public int Left, Top, Right, Bottom; }
[DllImport("user32.dll")] private static extern bool EnumChildWindows(IntPtr parent, EnumProc cb, IntPtr l);
[DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr h, System.Text.StringBuilder s, int max);
[DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr h);
[DllImport("user32.dll", SetLastError = true)] private static extern bool GetWindowRect(IntPtr h, out RECT3 r);
public delegate bool EnumProc(IntPtr h, IntPtr l);

private static System.Collections.Generic.List<string> _kids;
private static bool KidCb(IntPtr h, IntPtr l) {
    var sb = new System.Text.StringBuilder(256);
    GetClassName(h, sb, sb.Capacity);
    RECT3 r; GetWindowRect(h, out r);
    _kids.Add(string.Format("0x{0:X}|{1}|{2}|{3},{4},{5},{6}",
        h.ToInt64(), sb, IsWindowVisible(h) ? "visible" : "hidden", r.Left, r.Top, r.Right, r.Bottom));
    return true;
}
public static string[] GetChildWindowInfo(IntPtr parent) {
    _kids = new System.Collections.Generic.List<string>();
    EnumChildWindows(parent, KidCb, IntPtr.Zero);
    return _kids.ToArray();
}
'@
}

# WER dumps under %LOCALAPPDATA%\CrashDumps newer than $Since. Also backs the
# per-scenario zero-new-dumps invariant (driver: Assert-NoNewWerDumps).
function Get-WerDumps {
    param([Parameter(Mandatory)][datetime]$Since)
    $dir = Join-Path $env:LOCALAPPDATA 'CrashDumps'
    if (-not (Test-Path $dir)) { return @() }
    return @(Get-ChildItem $dir -File | Where-Object { $_.LastWriteTime -gt $Since })
}

function Save-FinalFrameBurst {
    param(
        [Parameter(Mandatory)][IntPtr]$MainHwnd,
        [Parameter(Mandatory)][string]$OutDir
    )
    if (-not (Test-Path $OutDir)) { New-Item -ItemType Directory -Force $OutDir | Out-Null }
    # 3 frames, ~150 ms apart; the first frame doubles as the capture-and-discard
    # lag eater, the later ones are the evidence.
    [void][GroupWeaver.WebViewCapture]::CaptureBurst($MainHwnd, $OutDir, 3, 150)
}

function Save-UiaTreeDump {
    param(
        [Parameter(Mandatory)][IntPtr]$MainHwnd,
        [Parameter(Mandatory)][string]$OutFile,
        [int]$MaxDepth = 12,
        [int]$MaxNodes = 400
    )
    $lines = New-Object System.Collections.Generic.List[string]
    $lines.Add('# UIA tree dump (ControlView), bounded walk.')
    $lines.Add('# NOTE: once a Chrome_RenderWidgetHostHWND child exists, UIA descendant queries on')
    $lines.Add('# this window return ONLY Chromium content - the Avalonia chrome VANISHES from this')
    $lines.Add('# tree (lab-environment.md). Judge Avalonia-side state from the PNG captures instead.')
    $root = [System.Windows.Automation.AutomationElement]::FromHandle($MainHwnd)
    $walker = [System.Windows.Automation.TreeWalker]::ControlViewWalker
    $stack = New-Object System.Collections.Stack
    $stack.Push(@($root, 0))
    $count = 0
    while ($stack.Count -gt 0 -and $count -lt $MaxNodes) {
        $pair = $stack.Pop()
        $el = $pair[0]
        $depth = [int]$pair[1]
        $count++
        try {
            $indent = '  ' * $depth
            $ct = $el.Current.ControlType.ProgrammaticName
            $name = $el.Current.Name
            $cls = $el.Current.ClassName
            $lines.Add("$indent$ct | name='$name' | class='$cls'")
        }
        catch {
            $lines.Add(('  ' * $depth) + '(element unreadable: ' + $_.Exception.Message + ')')
            continue
        }
        if ($depth -lt $MaxDepth) {
            # Collect children first so they pop in document order.
            $children = New-Object System.Collections.Generic.List[object]
            try {
                $child = $walker.GetFirstChild($el)
                while ($null -ne $child) {
                    $children.Add($child)
                    $child = $walker.GetNextSibling($child)
                    if ($children.Count -ge 64) { break }
                }
            }
            catch { }
            for ($i = $children.Count - 1; $i -ge 0; $i--) {
                $stack.Push(@($children[$i], ($depth + 1)))
            }
        }
    }
    if ($count -ge $MaxNodes) { $lines.Add("# (truncated at $MaxNodes nodes)") }
    $lines | Set-Content -Path $OutFile -Encoding UTF8
}

function Save-ChildHwndInventory {
    param(
        [Parameter(Mandatory)][IntPtr]$MainHwnd,
        [Parameter(Mandatory)][string]$OutFile
    )
    $lines = New-Object System.Collections.Generic.List[string]
    $lines.Add('# child-HWND inventory: hwnd|class|visibility|left,top,right,bottom')
    foreach ($entry in [GroupWeaver.E2eEvidenceNative]::GetChildWindowInfo($MainHwnd)) {
        $lines.Add($entry)
    }
    $lines | Set-Content -Path $OutFile -Encoding UTF8
}

function Save-EventLogExcerpt {
    param(
        [Parameter(Mandatory)][string]$OutFile,
        [int]$Minutes = 10
    )
    $providers = @('.NET Runtime', 'Application Error', 'Windows Error Reporting')
    $lines = New-Object System.Collections.Generic.List[string]
    $lines.Add("# Application event log, providers: $($providers -join ', '), last $Minutes min (read-only)")
    try {
        $events = @(Get-WinEvent -FilterHashtable @{
                LogName      = 'Application'
                ProviderName = $providers
                StartTime    = (Get-Date).AddMinutes(-$Minutes)
            } -ErrorAction Stop)
        foreach ($ev in $events) {
            $msg = ''
            if ($ev.Message) { $msg = ($ev.Message -replace '\r?\n', ' | ') }
            $lines.Add(('{0:o} [{1}] id={2}: {3}' -f $ev.TimeCreated, $ev.ProviderName, $ev.Id, $msg))
        }
    }
    catch {
        # Get-WinEvent throws when no events match - that is a GOOD outcome here.
        $lines.Add("(no matching events: $($_.Exception.Message))")
    }
    $lines | Set-Content -Path $OutFile -Encoding UTF8
}

function Save-WerDumpScan {
    param(
        [Parameter(Mandatory)][string]$OutFile,
        [Parameter(Mandatory)][datetime]$Since
    )
    $lines = New-Object System.Collections.Generic.List[string]
    $lines.Add("# WER dumps in $env:LOCALAPPDATA\CrashDumps newer than $($Since.ToString('o'))")
    $dumps = @(Get-WerDumps -Since $Since)
    if ($dumps.Count -eq 0) {
        $lines.Add('(none)')
    }
    else {
        foreach ($d in $dumps) {
            $lines.Add(('{0} | {1} bytes | {2:o}' -f $d.FullName, $d.Length, $d.LastWriteTime))
        }
    }
    $lines | Set-Content -Path $OutFile -Encoding UTF8
}

# Orchestrates all collectors into <ArtifactDir>\evidence\. Window-bound collectors
# run only while the app process is alive with a window; the system-level ones
# (event log, WER) always run. Each collector failure is recorded, never rethrown.
function Invoke-E2eEvidenceCollection {
    param(
        [Parameter(Mandatory)][string]$ArtifactDir,
        $App,
        [Parameter(Mandatory)][datetime]$RunStart
    )
    $evDir = Join-Path $ArtifactDir 'evidence'
    if (-not (Test-Path $evDir)) { New-Item -ItemType Directory -Force $evDir | Out-Null }
    $errors = New-Object System.Collections.Generic.List[string]

    $mainHwnd = [IntPtr]::Zero
    if ($App) {
        try {
            $App.Refresh()
            if (-not $App.HasExited) { $mainHwnd = $App.MainWindowHandle }
        }
        catch { $errors.Add("process refresh: $($_.Exception.Message)") }
    }

    if ($mainHwnd -ne [IntPtr]::Zero) {
        try { Save-FinalFrameBurst -MainHwnd $mainHwnd -OutDir (Join-Path $evDir 'final-burst') }
        catch { $errors.Add("final-burst: $($_.Exception.Message)") }
        try { Save-UiaTreeDump -MainHwnd $mainHwnd -OutFile (Join-Path $evDir 'uia-tree.txt') }
        catch { $errors.Add("uia-tree: $($_.Exception.Message)") }
        try { Save-ChildHwndInventory -MainHwnd $mainHwnd -OutFile (Join-Path $evDir 'hwnd-inventory.txt') }
        catch { $errors.Add("hwnd-inventory: $($_.Exception.Message)") }
    }
    else {
        $errors.Add('window-bound collectors skipped: app exited or no window')
    }

    try { Save-EventLogExcerpt -OutFile (Join-Path $evDir 'eventlog.txt') }
    catch { $errors.Add("eventlog: $($_.Exception.Message)") }
    try { Save-WerDumpScan -OutFile (Join-Path $evDir 'wer-dumps.txt') -Since $RunStart }
    catch { $errors.Add("wer-scan: $($_.Exception.Message)") }

    if ($errors.Count -gt 0) {
        $errors | Set-Content -Path (Join-Path $evDir 'collection-errors.txt') -Encoding UTF8
    }
}
