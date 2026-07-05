<#
.SYNOPSIS
    E2E scenario: a live ruleset edit through Settings' Advanced (JSONC) tab
    re-threads into the open workspace (ADR-038 WP6b, P1.8, issue #245).

.DESCRIPTION
    Proves ShellViewModel.OnRulesetApplied (src/App/ViewModels/ShellViewModel.cs,
    ~403-430): SettingsViewModel.ApplyRaw (WP6a) parses the Advanced tab's raw
    JSONC through the single RulesetLoader.Load gate and, on success, fires
    RulesetApplied - which the shell wires to re-thread the CURRENT step
    (workspace.ApplyRulesetAsync here) without a rebuild. This is the FIRST
    scenario to drive a modal window via UIA: SettingsWindow is a SEPARATE
    top-level Window with no WebView descendant, so - unlike MainWindow, which
    goes UIA-blind window-wide once the Chrome_RenderWidgetHostHWND child exists
    (lab-environment.md) - UIA works fully and normally inside it. The Settings
    gear itself is still MainWindow chrome, so it is still opened by pixel
    (Click-CapturePoint), exactly like every other post-WebView MainWindow
    control in the existing scenarios.

    Edit target: the default ruleset's emptyGroup rule (.claude/rules/rule-model.md
    / rule-engine.md AP 3.2 baseline: 12 empty-group Info findings on the full
    AGDLP-Demo scope) flipped enabled:true -> false via a narrow, section-scoped
    regex over the raw JSONC text - a safe, low-risk, info-severity toggle that
    is guaranteed to change the live findings count (12 Info findings vanish)
    without touching anything structurally risky (nesting/naming/circular).

    UIA element discovery inside the modal (no AutomationIds exist anywhere in
    SettingsWindow.axaml today, confirmed by inspection and an ad-hoc UIA-tree
    dump taken while building this scenario):
      - TabItem "Advanced (JSONC)" supports SelectionItemPattern (verified
        empirically - Avalonia's TabItem, like ListBoxItem, implements it).
      - "Load current" / "Apply JSONC" / "Cancel" are plain Buttons - by Name via
        InvokePattern, the same idiom Invoke-UiaButton already uses.
      - The raw-text TextBox is found by VALUE, not by structural position: of
        the FOUR Edit controls in the window (three single-line metadata boxes
        outside the TabControl, plus the raw editor inside the Advanced tab's
        content), only the raw editor's ValuePattern.Current.Value contains the
        literal substring "emptyGroup" - a content-based identity that survives
        regardless of layout, sidesteps the missing-AutomationId gap entirely,
        and was confirmed against a real captured RawEditorText dump before this
        script was written.
      - Avalonia TextBlock/SelectableTextBlock content IS exposed to UIA as
        ControlType.Text with Current.Name = the literal text (confirmed by the
        same dump - e.g. "Valid JSONC - Apply JSONC to make it the effective
        ruleset." and the RawEditorErrors band's "$.foo" JSON-pointer Path rows
        both show up this way). This is DIFFERENT from the plan's guess of an
        "errorRow Button" class for this band - that Button-with-Classes idiom
        is the OUTER structured-tab ValidationErrors band (SettingsWindow.axaml
        ~219-277), not RawEditorErrors (~857-883), which renders plain
        TextBlock/SelectableTextBlock rows. The "no validation error" check
        below is therefore: no ControlType.Text descendant whose Name starts
        with "$." - RulesetLoader.Load's PHASE-2 (semantic) validation errors
        use a "$.xxx" JSON-pointer Path (RulesetLoader.cs); only its PHASE-1
        (syntax) errors use a bare "$" with no trailing dot, which this filter
        deliberately does not match - a real structural signal for the
        semantic-error rows, not a guess (this scenario's edit can only ever
        produce valid JSON, so a phase-1 syntax error is not a live concern here).
      - Apply JSONC alone re-threads live (confirmed by reading
        SettingsViewModel.ApplyRaw/Apply JSONC's RelayCommand and
        ShellViewModel.OnRulesetApplied): it fires RulesetApplied on success,
        which the shell subscribes unconditionally in BuildSettingsViewModel -
        no separate footer "Apply"/"Save" click is needed, and Cancel (which
        only closes the window, per SettingsWindow.axaml.cs OnCancelClick) is
        safe to use afterward since it never reverts an already-applied change.

    The DEFINITIVE proof of re-threading is CANVAS- and COLOR-scoped, not a
    whole-window hash: ApplyRaw fires RulesetApplied SYNCHRONOUSLY, but
    ShellViewModel.OnRulesetApplied is `async void` and awaits
    WorkspaceViewModel.ApplyRulesetAsync -> a real WebView2
    UpdateGraphAsync round trip - so the re-thread is NOT complete the instant
    Apply JSONC returns. Worse, the --e2e channel's ProbeStateAsync shares the
    SAME single-flight guard as UpdateGraphAsync (CytoscapeGraphRenderer.
    EnterSingleFlight throws on overlap), so a 'state' poll that collides with
    the in-flight update comes back null/animated=false - Wait-E2eSettled alone
    is NOT a trustworthy one-shot barrier here. The fix: poll the RENDERED
    PIXELS themselves, canvas-region-cropped (Find-NodeBlob's 'canvas' region -
    the SAME idiom Wait-NodeBlob already uses everywhere else, excluding the
    title bar/sidebar/legend chrome a whole-window hash would include) for the
    Info-severity halo's actual on-screen blended color (sevInfo #4FA3E3 at
    sevInfoOpacity 0.40 over canvasBg #1b1f27, dark theme - graph.js; verified
    against real captures to land at ~RGB(48,84,114), stable across three
    independent runs) - present in force with count >= 8 before the edit (12
    empty-group Info findings' halos), then bounded-retried down to a small
    residual (<= max(4, before/3)) after Apply JSONC, timing out loudly rather
    than trusting a single snapshot. This is a specific, mechanism-traced
    signal ("the Info halos the emptyGroup rule paints are gone"), not "some
    pixel somewhere changed".

    Scope: the FULL demo root ('AGDLP-Demo', same as selection-sync.ps1) - the
    AP 3.2 baseline's 12 empty-group Info findings need the whole OU.

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
    $ArtifactDir = Join-Path $repoRoot ('artifacts\e2e\adhoc\settings-rethread-{0:yyyyMMdd-HHmmss}' -f (Get-Date))
}
if (-not $AppExe) {
    $AppExe = Join-Path $repoRoot 'src\App\bin\Release\net8.0-windows\GroupWeaver.App.exe'
}
if (-not $StateDir) {
    $StateDir = Join-Path $env:TEMP ('gw-e2e\adhoc\settings-rethread-{0:yyyyMMdd-HHmmss}' -f (Get-Date))
}

. (Join-Path (Split-Path -Parent $PSScriptRoot) 'lib\e2e-driver.ps1')

Initialize-E2eContext -ScenarioName 'settings-rethread' -ArtifactDir $ArtifactDir
$runStart = Get-Date

# The FULL demo root (selection-sync.ps1's rationale): the AP 3.2 baseline's 12
# empty-group Info findings need the whole OU, not the 2-node DL_FS-Finance_RW
# scope the other scenarios use.
$filterText = 'AGDLP-Demo'

# The Info-severity halo's actual on-screen blended color (see .DESCRIPTION):
# sevInfo #4FA3E3 = RGB(79,163,227) painted as a cytoscape overlay-color at
# sevInfoOpacity 0.40 BEHIND the node, over canvasBg #1b1f27 = RGB(27,31,39)
# (dark theme, graph.js) - alpha-blend = overlay*opacity + backdrop*(1-opacity).
# Verified against real captured checkpoints (not just computed): FindBlob at
# the standard tol=10 (Find-NodeBlob's own hardcoded tolerance) found this
# EXACT color present at count=21 before the emptyGroup flip and count=2 after,
# identically across three independent real runs (a deterministic layout for
# this fixed demo dataset) - this is the specific, mechanism-traced signal for
# "the Info halos the emptyGroup rule paints are gone", not a generic hash.
# NOTE: within Find-NodeBlob's tol=10, this also overlaps the transient
# lazy-expand "busy" ring (graph.js sevInfo hue at busyOpacity=0.35, ~RGB
# 45,77,105 vs this color's 48,84,114 - diffs 3/7/9, all under tol). Not a risk
# here (this scenario never triggers a lazy-expand and captures only after
# settle), but a future scenario reusing this idiom near a busy/expand step
# should pick a different color or add a settle gate first.
$colorInfoHalo = @(
    [int](79 * 0.40 + 27 * 0.60),
    [int](163 * 0.40 + 31 * 0.60),
    [int](227 * 0.40 + 39 * 0.60)
)

# Bounded-poll the CANVAS-cropped (Find-NodeBlob region='canvas' - excludes the
# title bar/sidebar/legend chrome a whole-window comparison would include)
# rendered pixel count of $colorInfoHalo until $Predicate is satisfied or
# $TimeoutSec elapses. This is the "poll for the real signal, never a single
# trust point" fix (see .DESCRIPTION): a single Wait-E2eSettled call is NOT a
# trustworthy barrier for WorkspaceViewModel.ApplyRulesetAsync's async
# UpdateGraphAsync round trip, since a colliding 'state' probe can read back
# as already-settled. Re-resolves the live Chromium child every attempt
# (Update-ChromiumHwnd) exactly like Test-CanvasBlob, and re-checks liveness
# so a crash mid-poll is attributed here, not to a downstream helper.
function Wait-CanvasBlobCount {
    param(
        [Parameter(Mandatory)][int[]]$Rgb,
        [Parameter(Mandatory)][scriptblock]$Predicate,
        [int]$TimeoutSec = 20,
        [Parameter(Mandatory)][string]$What
    )
    $deadline = (Get-Date).AddSeconds($TimeoutSec)
    $lastCount = $null
    while ($true) {
        Throw-IfCrashed "(polling) $What"
        Update-ChromiumHwnd -TimeoutSec 5
        $blob = Find-NodeBlob (Save-Probe) $Rgb 'canvas' 0
        $lastCount = $blob.Count
        if (& $Predicate $lastCount) {
            Write-DriverLog 'canvas-blob-settled' @{ what = $What; count = $lastCount }
            return $lastCount
        }
        if ((Get-Date) -gt $deadline) {
            throw "ASSERT::canvas-blob - $What did not settle within ${TimeoutSec}s (last count=$lastCount)"
        }
        Start-Sleep -Milliseconds 400
    }
}

# --- SettingsWindow UIA helpers (a SEPARATE top-level window, no WebView) -------
# Get-UiaRoot/Find-UiaFirst (webview-capture.ps1) are hardcoded to $app.MainWindowHandle;
# these local equivalents take an explicit root so the same idioms work against the
# modal SettingsWindow's own AutomationElement.

# Resolves the SettingsWindow HWND by title, via the same EnumWindows top-level
# inventory Assert-NoUnexpectedDialogs uses (GetTopLevelWindows). Bounded poll:
# ShowDialog's window creation is async relative to the posted click that opens it.
function Wait-SettingsWindowHandle {
    param([int]$TimeoutSec = 15)
    $deadline = (Get-Date).AddSeconds($TimeoutSec)
    while ($true) {
        $script:app.Refresh()
        if ($script:app.HasExited) {
            throw "CRASH_AFTER::(waiting for SettingsWindow) app exited (exit $($script:app.ExitCode))"
        }
        foreach ($w in @([GroupWeaver.E2eNative]::GetTopLevelWindows([uint32]$script:app.Id))) {
            $parts = $w -split '\|', 4
            if ($parts.Count -eq 4 -and $parts[3] -eq 'GroupWeaver Settings' -and $parts[2] -eq '1') {
                return [IntPtr][Convert]::ToInt64($parts[0].Substring(2), 16)
            }
        }
        if ((Get-Date) -gt $deadline) { throw "ASSERT::settings-window - SettingsWindow did not appear within ${TimeoutSec}s" }
        Start-Sleep -Milliseconds 300
    }
}

# The mirror wait for Cancel's Close(): confirms the modal actually tore down
# (ShowDialog's await returns) before the scenario resumes driving MainWindow.
function Wait-SettingsWindowClosed {
    param([int]$TimeoutSec = 15)
    $deadline = (Get-Date).AddSeconds($TimeoutSec)
    while ($true) {
        $script:app.Refresh()
        if ($script:app.HasExited) {
            throw "CRASH_AFTER::(waiting for SettingsWindow close) app exited (exit $($script:app.ExitCode))"
        }
        $stillOpen = $false
        foreach ($w in @([GroupWeaver.E2eNative]::GetTopLevelWindows([uint32]$script:app.Id))) {
            $parts = $w -split '\|', 4
            if ($parts.Count -eq 4 -and $parts[3] -eq 'GroupWeaver Settings') { $stillOpen = $true; break }
        }
        if (-not $stillOpen) { return }
        if ((Get-Date) -gt $deadline) { throw "ASSERT::settings-window - SettingsWindow did not close within ${TimeoutSec}s of Cancel" }
        Start-Sleep -Milliseconds 300
    }
}

function Find-UiaFirstIn {
    param(
        [Parameter(Mandatory)][System.Windows.Automation.AutomationElement]$Root,
        [Parameter(Mandatory)][System.Windows.Automation.ControlType]$Type,
        [string]$Name
    )
    $conds = New-Object System.Collections.Generic.List[System.Windows.Automation.Condition]
    $conds.Add((New-Object System.Windows.Automation.PropertyCondition(
                [System.Windows.Automation.AutomationElement]::ControlTypeProperty, $Type)))
    if ($Name) {
        $conds.Add((New-Object System.Windows.Automation.PropertyCondition(
                    [System.Windows.Automation.AutomationElement]::NameProperty, $Name)))
    }
    $cond = if ($conds.Count -eq 1) { $conds[0] } else { New-Object System.Windows.Automation.AndCondition($conds.ToArray()) }
    return $Root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $cond)
}

function Invoke-UiaButtonIn {
    param(
        [Parameter(Mandatory)][System.Windows.Automation.AutomationElement]$Root,
        [Parameter(Mandatory)][string]$Name
    )
    $btn = Wait-Uia { Find-UiaFirstIn -Root $Root -Type ([System.Windows.Automation.ControlType]::Button) -Name $Name } 15 "button '$Name'"
    $btn.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    Write-DriverLog 'settings-button-invoked' @{ name = $Name }
}

# Content-based identity (see the .DESCRIPTION note): the raw JSONC editor is
# the ONLY Edit control whose current value contains "emptyGroup" - survives
# regardless of layout, and was confirmed against a real captured dump.
function Wait-RawEditorElement {
    param(
        [Parameter(Mandatory)][System.Windows.Automation.AutomationElement]$Root,
        [int]$TimeoutSec = 10
    )
    $deadline = (Get-Date).AddSeconds($TimeoutSec)
    $editCond = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Edit)
    while ($true) {
        foreach ($el in @($Root.FindAll([System.Windows.Automation.TreeScope]::Descendants, $editCond))) {
            $vp = $null
            if ($el.TryGetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern, [ref]$vp)) {
                if ([string]$vp.Current.Value -like '*emptyGroup*') { return $el }
            }
        }
        if ((Get-Date) -gt $deadline) { throw "ASSERT::raw-editor - no Edit control's value contains 'emptyGroup' within ${TimeoutSec}s" }
        Start-Sleep -Milliseconds 300
    }
}

# RawEditorErrors renders each finding as a TextBlock/SelectableTextBlock row
# whose Path is a JSON pointer ("$.foo...") - a distinct plain-Text signal from
# the structured-tab ValidationErrors band's Button rows (see .DESCRIPTION).
# Returns the count of matching Text elements (0 = clean).
function Get-JsonPointerErrorTextCount {
    param([Parameter(Mandatory)][System.Windows.Automation.AutomationElement]$Root)
    $textCond = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Text)
    $matches = @($Root.FindAll([System.Windows.Automation.TreeScope]::Descendants, $textCond) |
        Where-Object { [string]$_.Current.Name -like '$.*' })
    return $matches.Count
}

# The Advanced tab's success line is "Valid JSONC EMDASH Apply JSONC..." - built via
# [char]0x2014 (never a literal em-dash byte in this file: PS 5.1 reads a no-BOM
# UTF-8 .ps1 through the ANSI/OEM codepage, so any non-ASCII byte mojibakes and
# breaks the PARSER, not just the display - lab-environment.md).
$script:ValidJsoncLineText = "Valid JSONC $([char]0x2014) Apply JSONC to make it the effective ruleset."

$failed = $false
try {
    # --demo is MANDATORY with --state-dir/--e2e (both app-side demo gates); the
    # startup auto-connect lands directly on the root picker.
    [void](Start-E2EApp -ExePath $AppExe -AppArgs @('--demo') -StateDir $StateDir -E2e)
    Assert-Alive 'launch (window up)'

    Invoke-RootLoad -FilterText $filterText
    Assert-Alive 'demo connect + full-root load'

    Wait-ChromiumChild -TimeoutSec 60
    [void](Wait-E2eStep -Expected 'Workspace' -TimeoutSec 15 -What 'initial workspace state')
    Wait-E2eSettled -TimeoutSec 15 -What 'initial workspace settle' | Out-Null
    Write-DriverLog 'workspace-rendered' @{}
    Capture-Checkpoint 'workspace'
    Assert-Alive 'workspace render'

    # --- BEFORE: the Info-halo blob present at baseline (12 empty-group findings) --
    # A bounded poll rather than a single Find-NodeBlob call (consistency with the
    # AFTER check's discipline, even though the initial render is not the racy spot).
    $beforeCount = Wait-CanvasBlobCount -Rgb $colorInfoHalo -TimeoutSec 15 `
        -Predicate { param($c) $c -ge 8 } `
        -What 'Info-severity halo baseline (before the edit)'
    Capture-Checkpoint 'before-edit'
    Write-DriverLog 'before-halo-count' @{ count = $beforeCount }

    # --- open Settings (MainWindow chrome, pixel-only post-WebView) ----------------
    # Calibrated against 01-workspace.png at the fixed 60,60/1480x920 geometry
    # (Start-E2EApp's SetWindowPos pin): the "gear Settings" button, top-right of
    # the command strip.
    $PT_Settings = @(1387, 76)
    Click-CapturePoint $PT_Settings[0] $PT_Settings[1] 'Settings'
    Throw-IfCrashed 'Settings click'

    $settingsHwnd = Wait-SettingsWindowHandle -TimeoutSec 15
    Assert-Alive 'Settings window opened'
    Write-DriverLog 'settings-window-opened' @{ hwnd = "0x{0:X}" -f $settingsHwnd.ToInt64() }
    $settingsRoot = [System.Windows.Automation.AutomationElement]::FromHandle($settingsHwnd)

    # --- select the Advanced (JSONC) tab --------------------------------------------
    $advancedTab = Wait-Uia {
        Find-UiaFirstIn -Root $settingsRoot -Type ([System.Windows.Automation.ControlType]::TabItem) -Name 'Advanced (JSONC)'
    } 15 "tab 'Advanced (JSONC)'"
    $selectionPattern = $null
    if (-not $advancedTab.TryGetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern, [ref]$selectionPattern)) {
        throw 'ASSERT::advanced-tab - TabItem does not support SelectionItemPattern (verified supported empirically; a regression here needs re-investigation)'
    }
    $selectionPattern.Select()
    Write-DriverLog 'advanced-tab-selected' @{}
    Start-Sleep -Milliseconds 300
    Throw-IfCrashed 'Advanced tab selected'

    # --- Load current: re-seed RawEditorText from the live structured mirror -------
    Invoke-UiaButtonIn -Root $settingsRoot -Name 'Load current'
    $rawEditor = Wait-RawEditorElement -Root $settingsRoot -TimeoutSec 10
    $rawValuePattern = $rawEditor.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)
    $originalText = [string]$rawValuePattern.Current.Value
    Write-DriverLog 'raw-editor-loaded' @{ length = $originalText.Length }

    # --- flip emptyGroup.enabled: true -> false, section-scoped ---------------------
    # Matches ONLY the emptyGroup block's own "enabled" key (the block immediately
    # follows the "emptyGroup": { header in RulesetSerializer's fixed property order -
    # confirmed against a real captured RawEditorText dump before this script was
    # written) - never circular's or nesting's "enabled" key.
    $emptyGroupPattern = '("emptyGroup":\s*\{\s*"enabled":\s*)true'
    if ($originalText -notmatch $emptyGroupPattern) {
        throw "ASSERT::raw-editor - RawEditorText does not contain the expected emptyGroup.enabled=true shape; cannot safely flip it"
    }
    $mutatedText = $originalText -replace $emptyGroupPattern, '${1}false'
    if ($mutatedText -eq $originalText) {
        throw 'ASSERT::raw-editor - the emptyGroup flip is a no-op (regex matched but text unchanged)'
    }
    $rawValuePattern.SetValue($mutatedText)
    Write-DriverLog 'raw-editor-mutated' @{ emptyGroupFlippedToFalse = $true }

    # Live inline validation (OnRawEditorTextChanged fires on every keystroke/SetValue):
    # the "valid" success line must appear BEFORE we ever click Apply JSONC, and no
    # JSON-pointer error row may be present.
    [void](Wait-Uia {
            Find-UiaFirstIn -Root $settingsRoot -Type ([System.Windows.Automation.ControlType]::Text) `
                -Name $script:ValidJsoncLineText
        } 10 "the 'Valid JSONC' success line")
    $preApplyErrorCount = Get-JsonPointerErrorTextCount -Root $settingsRoot
    if ($preApplyErrorCount -gt 0) {
        throw "ASSERT::raw-editor - $preApplyErrorCount JSON-pointer error row(s) present after the edit but before Apply JSONC (the flip should be syntactically valid)"
    }
    Capture-Checkpoint 'advanced-tab-edited'

    # --- Apply JSONC: the single gate, re-threads live on success -------------------
    Invoke-UiaButtonIn -Root $settingsRoot -Name 'Apply JSONC'
    Throw-IfCrashed 'Apply JSONC click'

    # No validation-error banner (RawEditorErrors renders plain Text rows whose Path
    # is a JSON pointer starting "$." - see .DESCRIPTION for why this is NOT the
    # Button-classed errorRow band, which belongs to the structured tabs instead).
    $postApplyErrorCount = Get-JsonPointerErrorTextCount -Root $settingsRoot
    if ($postApplyErrorCount -gt 0) {
        throw "ASSERT::apply-jsonc - $postApplyErrorCount JSON-pointer error row(s) present after Apply JSONC (the gate rejected a syntactically-valid edit)"
    }
    [void](Wait-Uia {
            Find-UiaFirstIn -Root $settingsRoot -Type ([System.Windows.Automation.ControlType]::Text) `
                -Name $script:ValidJsoncLineText
        } 10 "the 'Valid JSONC' success line (post-Apply)")
    Write-DriverLog 'apply-jsonc-clean' @{ errorCount = 0 }
    Capture-Checkpoint 'advanced-tab-applied'

    # --- close the modal: Cancel only closes (OnCancelClick), never reverts an
    #     already-applied change (SettingsWindow.axaml.cs) ---------------------------
    Invoke-UiaButtonIn -Root $settingsRoot -Name 'Cancel'
    Wait-SettingsWindowClosed -TimeoutSec 15
    Assert-Alive 'Settings window closed'
    Write-DriverLog 'settings-window-closed' @{}

    # --- AFTER: the workspace re-threaded (WorkspaceViewModel.ApplyRulesetAsync) ----
    # NOT a single Wait-E2eSettled trust point (see .DESCRIPTION): 'state' polls can
    # race the async UpdateGraphAsync round trip (shared single-flight guard with
    # ProbeStateAsync) and read back as already-settled while the update is still in
    # flight. Instead: bounded-poll the CANVAS-cropped rendered pixels themselves
    # until the Info-halo count genuinely drops, or fail loudly on timeout - the
    # emptyGroup flip removes ALL 12 Info findings from this scope (the AP 3.2
    # baseline has zero External-sourced infos), so a small residual ceiling
    # (self-calibrated off the observed baseline, floor 4) cleanly separates
    # "still showing the old halos" from "re-threaded".
    $afterCeiling = [Math]::Max(4, [int]($beforeCount / 3))
    $afterCount = Wait-CanvasBlobCount -Rgb $colorInfoHalo -TimeoutSec 20 `
        -Predicate { param($c) $c -le $afterCeiling } `
        -What 'Info-severity halo cleared (after Apply JSONC + Cancel)'
    Capture-Checkpoint 'after-edit'
    Write-DriverLog 'rethread-confirmed' @{ beforeCount = $beforeCount; afterCount = $afterCount; afterCeiling = $afterCeiling }

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
