<#
.SYNOPSIS
    Thin PowerShell consumer for the app's --e2e stdio channel (ADR-038 D3.2,
    WP6b follow-up to WP6a's E2eChannel.cs).

.DESCRIPTION
    Dot-sourced by e2e-driver.ps1 (same pattern as e2e-evidence.ps1), so every
    scenario that dot-sources the driver gets these functions for free, in its
    own scope. Windows PowerShell 5.1, ASCII-ONLY (lab rule).

    Wire protocol (src/App/Automation/E2eChannel.cs): stdin accepts EXACTLY two
    commands - {"cmd":"state","seq":N} and {"cmd":"quit"}. ADR-038 D2: the
    channel OBSERVES, it never ACTS - no generic "send anything" helper is
    exposed here, on purpose. Every poll re-reads trace.jsonl fresh from disk -
    no in-memory queue, matching every other bounded-poll Wait-* helper's
    cost/complexity in the driver (e.g. Wait-NodeBlob).

    Requires Start-E2EApp -E2e (e2e-driver.ps1): the live $script:app.
    StandardInput handle this writes commands to, and the trace.jsonl file
    (under $script:E2eTraceFile) this reads events/replies from.
#>

# Module-scoped 'state' sequence counter (ADR-038 D3.2 wire shape: 'seq'/'reply'
# correlate a command to its reply). Starts at 1 on first Wait-E2eState call.
$script:E2eSeq = 0

# --- commands (ADR-038 D2: the ONLY two commands that will ever exist) -------------

# PRIVATE plumbing - not a general "send anything" entry point (ADR-038 D2: the
# channel observes, it never acts). The only two callers are Send-E2eState/
# Send-E2eQuit below, both fixed shapes. Writes raw UTF8 bytes directly to
# StandardInput.BaseStream (bypassing the StreamWriter's WriteLine) for encoding
# control - though the actual one-time BOM artifact this channel must tolerate
# is burned separately by Start-E2EAppProcess's warm-up write (e2e-driver.ps1),
# not by this raw-byte choice alone; see that function's note for the full story.
function Send-E2eCommand {
    param([Parameter(Mandatory)][string]$Json)
    $bytes = [System.Text.Encoding]::UTF8.GetBytes($Json + "`n")
    $stream = $script:app.StandardInput.BaseStream
    $stream.Write($bytes, 0, $bytes.Length)
    $stream.Flush()
}

function Send-E2eState {
    param([Parameter(Mandatory)][long]$Seq)
    Send-E2eCommand -Json ('{{"cmd":"state","seq":{0}}}' -f $Seq)
    Write-DriverLog 'e2e-send-state' @{ seq = $Seq }
}

function Send-E2eQuit {
    Send-E2eCommand -Json '{"cmd":"quit"}'
    Write-DriverLog 'e2e-send-quit' @{}
}

# --- trace --------------------------------------------------------------------------

# Re-parses trace.jsonl fresh on every call. A line that fails to parse (a partial
# line still being flushed by the OutputDataReceived handler) is skipped silently -
# it will parse cleanly on the NEXT poll once the writer finishes the line.
function Get-E2eTraceLines {
    if (-not $script:E2eTraceFile -or -not (Test-Path $script:E2eTraceFile)) { return @() }
    $parsed = New-Object System.Collections.Generic.List[object]
    foreach ($line in @(Get-Content -Path $script:E2eTraceFile)) {
        if (-not $line.Trim()) { continue }
        try { $parsed.Add((ConvertFrom-Json -InputObject $line)) }
        catch { } # unparseable trailing partial line - skip, it will settle next poll
    }
    return $parsed.ToArray()
}

# --- polling helpers (400ms cadence, matching Wait-NodeBlob / Wait-ChromiumChild) ---

# Sends ONE 'state' command (a fresh seq) and bounded-polls for its reply.
function Wait-E2eState {
    param(
        [int]$TimeoutSec = 15,
        [Parameter(Mandatory)][string]$What
    )
    $script:E2eSeq++
    $seq = $script:E2eSeq
    Send-E2eState -Seq $seq
    $deadline = (Get-Date).AddSeconds($TimeoutSec)
    while ($true) {
        $reply = @(Get-E2eTraceLines | Where-Object { $_.reply -eq $seq })
        if ($reply.Count -gt 0) { return $reply[-1] }
        if ((Get-Date) -gt $deadline) { throw "ASSERT::e2e-state - $What did not reply within ${TimeoutSec}s" }
        Start-Sleep -Milliseconds 400
    }
}

# Bounds the per-attempt Wait-E2eState timeout to whatever remains of the OUTER
# deadline (never more than 5s per attempt) so a slow final poll cannot itself
# blow through the caller's total budget.
function Get-E2eInnerTimeoutSec {
    param([Parameter(Mandatory)][datetime]$Deadline)
    $remaining = [Math]::Ceiling(($Deadline - (Get-Date)).TotalSeconds)
    return [int]([Math]::Max(1, [Math]::Min(5, $remaining)))
}

# Polls 'state' until the reply's page-truth 'animated' is false ($null - no active
# renderer, e.g. the Audit step - counts as already-settled). Replaces a guessed
# Start-Sleep after any step-swap or camera move wherever a renderer is live.
function Wait-E2eSettled {
    param(
        [int]$TimeoutSec = 15,
        [Parameter(Mandatory)][string]$What
    )
    $deadline = (Get-Date).AddSeconds($TimeoutSec)
    while ($true) {
        $state = Wait-E2eState -TimeoutSec (Get-E2eInnerTimeoutSec $deadline) -What $What
        if ($null -eq $state.animated -or $state.animated -eq $false) {
            Write-DriverLog 'e2e-settled' @{ what = $What }
            return $state
        }
        if ((Get-Date) -gt $deadline) { throw "ASSERT::e2e-settled - $What did not settle within ${TimeoutSec}s" }
        Start-Sleep -Milliseconds 400
    }
}

# Polls 'state' until the reply's 'step' matches $Expected.
function Wait-E2eStep {
    param(
        [Parameter(Mandatory)][string]$Expected,
        [int]$TimeoutSec = 15,
        [Parameter(Mandatory)][string]$What
    )
    $deadline = (Get-Date).AddSeconds($TimeoutSec)
    $lastSeen = $null
    while ($true) {
        $state = Wait-E2eState -TimeoutSec (Get-E2eInnerTimeoutSec $deadline) -What $What
        $lastSeen = $state.step
        if ($lastSeen -eq $Expected) {
            Write-DriverLog 'e2e-step-confirmed' @{ what = $What; step = $Expected }
            return $state
        }
        if ((Get-Date) -gt $deadline) {
            throw "ASSERT::e2e-step - $What did not reach step '$Expected' within ${TimeoutSec}s (last seen: '$lastSeen')"
        }
        Start-Sleep -Milliseconds 400
    }
}

# Bounded-polls the trace (not 'state') for an event named $Evt, optionally narrowed
# by $Where (a Where-Object-style predicate over $_, e.g. { $_.to -eq 'Workspace' }).
function Wait-E2eEvent {
    param(
        [Parameter(Mandatory)][string]$Evt,
        [scriptblock]$Where = { $true },
        [int]$TimeoutSec = 15,
        [Parameter(Mandatory)][string]$What
    )
    $deadline = (Get-Date).AddSeconds($TimeoutSec)
    while ($true) {
        $match = @(Get-E2eTraceLines | Where-Object { $_.evt -eq $Evt } | Where-Object $Where)
        if ($match.Count -gt 0) {
            Write-DriverLog 'e2e-event-confirmed' @{ what = $What; evt = $Evt }
            return $match[0]
        }
        if ((Get-Date) -gt $deadline) { throw "ASSERT::e2e-event - $What did not observe '$Evt' within ${TimeoutSec}s" }
        Start-Sleep -Milliseconds 400
    }
}

# End-of-scenario invariant: no LoadError/RendererError anywhere in the trace (none
# of the churn/journey scenarios expect one).
function Assert-E2eNoUnexpectedTraceErrors {
    $bad = @(Get-E2eTraceLines | Where-Object { $_.evt -eq 'LoadError' -or $_.evt -eq 'RendererError' })
    Write-DriverLog 'e2e-trace-error-scan' @{ badCount = $bad.Count }
    if ($bad.Count -gt 0) {
        $summary = ($bad | ForEach-Object { "$($_.evt): $($_.message)" }) -join ' | '
        throw "ASSERT::e2e-trace-errors - $($bad.Count) unexpected LoadError/RendererError event(s) in trace: $summary"
    }
}
