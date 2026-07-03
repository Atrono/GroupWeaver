# tools/e2e - autonomous windowed E2E harness

Scenario runner for windowed end-to-end journeys against the REAL built app with
real WebView2, per ADR-038 (`docs/adr/038-windowed-e2e-harness.md`). Lab-box
local by design: scenarios need a desktop session and (for `requires-ad` tags)
the lab DC - they never run as a blocking CI gate.

```
pwsh tools/e2e/run-e2e.ps1                      # -Tag smoke (default)
pwsh tools/e2e/run-e2e.ps1 -Tag full
pwsh tools/e2e/run-e2e.ps1 -Scenario back-nav   # by name, tag filter skipped
```

## Two-tier driving policy (ADR-038 D2)

- **Tier A - input fidelity.** Scenarios that exist to prove hittability and
  airspace (back-nav, canvas clicks, expand) act EXCLUSIVELY via UIA patterns
  and posted `WM_*` messages. No shortcuts.
- **Tier B - feature scenarios.** May *gate and assert* on read-only probes
  (the WP5+ `--e2e` trace / bridge `stateProbe`), but every ACTION is still
  real input (UIA / posted WM_*).
- **No mutation commands ever.** The observation channel carries `state` and
  `quit` only; nothing in the harness drives the app through automation seams,
  and nothing here touches Active Directory. All app seams are flag-gated and
  demo-mode-required (ADR-038 D3).

Environment constraints baked into the driver (`.claude/rules/lab-environment.md`):
no interactive input desktop (`SetCursorPos` fails - posted messages only), UIA
goes Chromium-blind once the WebView child exists (pixel confirms instead),
PrintWindow lags one compositor batch (capture-and-discard on every probe),
Windows PowerShell 5.1 for the GAC `UIAutomationClient` (all `.ps1` here are
ASCII-only), bounded polls with explicit timeouts - never sleep-and-hope
(deliberate exception: the fixed settle before each liveness check, which IS the
delayed-crash attribution window for #120-class crashes).

## Failure classes (ADR-038 D4)

| Class | Meaning | Truth source |
|---|---|---|
| `PRODUCT-CRASH` | app process exited mid-journey | `app-stderr.txt` |
| `PRODUCT-ASSERT` | app alive but a pinned behavior is wrong | signature + checkpoints |
| `INFRA-DRIVE` | UIA/WebView2/launch plumbing failed | `harness.jsonl`, `driver-stdout.txt` |
| `TIMEOUT` | runner watchdog fired (assigned by the runner only) | `harness.jsonl` tail |

Retry policy: a failed scenario is retried ONCE iff its result signature matches
a `RetrySignatures` wildcard in the manifest - never blanket retries. Default is
`@()` (no retry) for every scenario.

## How to add a scenario

1. Create `scenarios/<name>.ps1`. Start from `launch-render.ps1`: keep the
   pwsh-to-5.1 relaunch shim, the `-ArtifactDir/-StateDir/-AppExe` params, the
   `Initialize-E2eContext` call, and the try/catch/finally shape
   (`Complete-E2eFailure` in catch, `Stop-E2EAppForce` in finally). Demo
   scenarios seed the hermetic state dir (`Initialize-E2eStateDir`) and launch
   via `Start-E2EApp -AppArgs @('--demo') -StateDir $StateDir`.
2. Drive with the driver primitives (`lib/e2e-driver.ps1`): `Start-E2EApp`,
   `Invoke-RootLoad` (`Invoke-DemoRootLoad` for seamless launches),
   `Click-CapturePoint`, `Send-MainWindowWheel`, `Test-CanvasBlob`,
   `Capture-Checkpoint`, `Assert-Alive`/`Throw-IfCrashed`,
   `Update-ChromiumHwnd`/`Assert-SameHwnd`.
3. End with the cross-cutting invariant pack: `Assert-NoUnexpectedDialogs`,
   `Invoke-CleanShutdownGate`, `Assert-CleanStderr`, `Assert-NoNewWerDumps`.
4. Register it in `scenarios/scenarios.psd1` (Name, Tags, TimeoutSec,
   RetrySignatures).
5. Keep the file ASCII-only (PS 5.1 no-BOM rule) and byte-verify it.

## Artifact layout (gitignored)

```
artifacts/e2e/runs/<yyyyMMdd-HHmmss>/
  summary.json            # run: startedUtc/gitSha/renderingMode/tag; per-scenario rows
  summary.md              # the same as a table
  <scenario>/             # (-retry suffix for the one sanctioned retry)
    result.json           # scenario/result/class/signature/durationMs/...
    harness.jsonl         # one line per driver action + capability poll
    app-stdout.txt, app-stderr.txt
    driver-stdout.txt, driver-stderr.txt
    _probe.png            # last pixel-gate probe frame
    checkpoints/NN-*.png  # numbered journey checkpoints
    evidence/             # on failure: final-burst/, uia-tree.txt,
                          # hwnd-inventory.txt, eventlog.txt, wer-dumps.txt
```

Per-scenario state dirs land under `%TEMP%\gw-e2e\<stamp>\<scenario>` and are
the **hermetic state seam** (ADR-038 D3.1, WP5): demo scenarios launch the app
with `--demo --state-dir <dir>` (`Start-E2EApp -StateDir`; `--demo` is mandatory
- the app refuses the seam with exit 64 otherwise), pre-seed a deterministic
`ui-state.json` via `Initialize-E2eStateDir` (rail expanded, dark theme), and
get the WebView2 profile + log sink pointed inside the same dir via per-child
env (`WEBVIEW2_USER_DATA_FOLDER`, `GROUPWEAVER_LOG_DIR`). The operator's real
`%APPDATA%` is never touched by demo scenarios. `Backup-/Restore-OperatorState`
and the runner's `.e2e-bak` leftover sweep REMAIN for live-AD scenarios, which
run seamless by design (ADR-038 D6). `renderingMode` in the summary
distinguishes GPU vs software rendering - perf budgets are keyed by mode and
never compared across modes (ADR-038 D5).

`tools/smoke-back-nav.ps1` is a deprecation wrapper around
`run-e2e.ps1 -Scenario back-nav`; the journey now lives in
`scenarios/back-nav.ps1`.
