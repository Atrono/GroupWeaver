# ADR-038: Autonomous windowed E2E harness — a PowerShell scenario-runner over flag-gated app seams

**Status:** Proposed · **Date:** 2026-07-03
**Decides:** issue #239 (autonomous windowed E2E harness + scenario suite) · **Phase:** 4 (feedback-driven)
**Builds on:** ADR-037 (the app-side evidence this harness consumes: JSONL log, crash marker, heartbeat), ADR-024/ADR-025 (the step-swap/parking lifecycle the primary scenarios pin), ADR-031 (connection targeting — the sever scenarios' entry point), `.claude/rules/lab-environment.md` (the no-input-desktop driving model, PS 5.1 + ASCII-only, capture-and-discard), `tools/smoke-back-nav.ps1` + `tools/lib/webview-capture.ps1` (the driver investment being generalized)

## Context

Windowed E2E coverage today is exactly one script, `tools/smoke-back-nav.ps1`, pinning
one journey (back-nav crash #120 + viewport preservation #122). Full user journeys on the
real app with real WebView2 — connect → pick root → load → interact → audit → export —
are never exercised end-to-end; fault injection (corrupt state files, severed directory,
killed WebView2 children) and scale runs don't exist; and when a windowed run fails there
is no machine-readable evidence, so nothing can triage it autonomously.

The environment dictates the shape ([[lab-environment]]): no interactive input desktop
(UIA patterns for Avalonia chrome, posted WM_* for the Chromium child, PrintWindow
captures once UIA goes Chromium-blind), PS 5.1 for the GAC `UIAutomationClient`
(ASCII-only scripts), pixel output nondeterministic across GPU/software rendering (state
probes, never golden images), CI has no DC and no guaranteed input desktop (windowed +
`RequiresAd` scenarios are lab-box-local). The full scenario catalog (P0 crash/corruption,
P1 core journeys, P2 polish/soak, the CI-vs-lab split, and the deliberate exclusions)
lives in the approved plan and lands as `tools/e2e/scenarios/` + its README; this ADR
pins the architecture and policies that make those scenarios deterministic and safe.

## Decision

### D1 — Shape: a PowerShell scenario-runner in `tools/e2e/`, not a C#/FlaUI xUnit project.

`run-e2e.ps1` (pwsh 7) orchestrates: discovery via `scenarios/scenarios.psd1` (Name,
Tags smoke|full|perf|requires-ad, TimeoutSec, RetrySignatures, Budgets), sequencing,
watchdog, artifact/summary management. Each scenario runs as a `powershell.exe` 5.1 child
dot-sourcing `lib/e2e-driver.ps1`, which generalizes `smoke-back-nav.ps1` +
`tools/lib/webview-capture.ps1` — the library that already encodes the lab's hard-won
fixes (capture-and-discard, `PW_RENDERFULLCONTENT`, DPI-before-`GetWindowRect`,
largest-visible-Chromium-HWND disambiguation, detent-normalized wheel). Porting that to
C# would re-open every closed flake for zero functional gain; instead all *intelligence*
moves into flag-gated C# seams (D3) and the PS driver stays thin glue. House rule
"hooks/scripts PowerShell-native" applies.

### D2 — Two-tier driving policy; the automation channel observes, never acts.

Tier A input-fidelity scenarios (back-nav, canvas clicks, expand) act **exclusively** via
UIA + posted WM_* — they prove hittability and airspace. Tier B feature scenarios may
*gate and assert* on read-only probes but still *act* via real input. The `--e2e` channel
carries **no mutation commands** — no invoke, no click; `state` and `quit` only. This
keeps E2E honest while making gates deterministic.

### D3 — App seams: flag-gated, demo-mode-required, observation-only.

All seams require `--demo` (the `--dump-graph` exit-64 precedent); extending any to live
mode is a deliberate future decision, not a default. Ranked:

1. **`--state-dir <path>`** — hermetic per-scenario `%APPDATA%` root; the three stores
   already take injected base dirs (`UiStateStore(string)`, `RulesetLocator(string)`,
   `AuditRunStore(string)`); the composition root resolves once. The driver also sets
   `WEBVIEW2_USER_DATA_FOLDER` and `GROUPWEAVER_LOG_DIR` (ADR-037) per scenario. This
   retires the smoke script's backup/mutate/restore of the operator's real state and
   kills the #124 real-%APPDATA% failure class.
2. **`--e2e` stdio JSONL channel** — stdout event trace (`StepChanged`, load/renderer
   errors, `demoConnected`) mirroring ADR-037 events; stdin commands `state`
   (step/nodeCount/selection/isLoading snapshot) and `quit` (graceful close, so clean
   exit is assertable distinctly from kill).
3. **Bridge `stateProbe` → `stateReport`** — page-truth scalars only (nodes, edges, zoom,
   pan, selected, `cy.animated()` — the settle barrier), cloned from the existing
   ping/pong seq idiom; new `IGraphRenderer.ProbeStateAsync`, parser case,
   `FakeGraphRenderer` update. Never rich objects out of the page.
4. **Deterministic geometry + forced reduced motion under `--e2e`** — fixed window
   position/size app-side; the ADR-034 reduced-motion path so native transitions never
   race captures.
5. **`--demo-data <path>`** — a `DemoProvider` file-path ctor through the same strict
   `LoadDataset` validation (enables ×N synthetic scale datasets).
6. **`AutomationProperties.AutomationId`** on pre-WebView chrome (Connect card, root
   picker) — removes name/localization coupling where UIA still sees Avalonia.

### D4 — Scenario lifecycle: hermetic, watchdogged, classified, sparingly retried.

One scenario = one fresh app process + one fresh state dir. Per-manifest `TimeoutSec`
watchdog; on fire, kill the child then kill-tree the app + its msedgewebview2
descendants. Every result is classified: `PRODUCT-CRASH` (process exited; stderr is the
truth) / `PRODUCT-ASSERT` (alive but wrong) / `INFRA-DRIVE` (UIA/WebView2/launch
plumbing) / `TIMEOUT`. Retry **once**, only for `INFRA-DRIVE`/`TIMEOUT` signatures
explicitly listed in the manifest (the `build.ps1` dotnet-format signature-gate
precedent) — never blanket retries. Scenarios run strictly sequentially (one desktop,
exclusive foreground geometry). Cross-cutting invariants asserted in every scenario:
alive at each step boundary, clean-shutdown gate (exit ≤5 s code 0), zero new WER dumps
and event-log crash entries in the run window, zero jsError in the trace, no unexpected
top-level windows, clean stderr, state-dir integrity, and — for live-AD scenarios — a
fixture health post-check (object count + max `whenChanged` unchanged under the lab OU).

### D5 — Evidence contract and `verdict.json` v1.

Artifacts land run-first: `artifacts/e2e/runs/<timestamp>/<scenario>/` (one run = one
summary + one prune unit). Always captured: checkpoint PNG timeline (PrintWindow,
capture-and-discard), app stdout/stderr, `trace.jsonl` (the `--e2e` event trace),
`harness.jsonl` (one line per driver action and capability poll — polls are where hangs
localize), `result.json`. On failure additionally: 3-frame final burst, UIA tree dump,
child-HWND inventory, Application event-log excerpt, WER/crashpad dumps, the zipped
scenario state dir. `verdict.json` v1 (runId, scenario, appSessionId, verdict
pass|fail|harness-error, exitCode, failure{step,reason,evidenceHint}, appErrors = every
app-log line ≥ Warning, processExit, timings, artifact map) is what the autonomous
triager reads first. `summary.json` records gitSha and renderingMode (GPU vs software)
per run; perf budgets are keyed by that mode, never compared across modes.
`artifacts/e2e/` is **gitignored** (like `artifacts/ui/`) — evidence from lab-AD
scenarios (screenshots, zipped state dirs, event-log excerpts) must never reach the
public repo; the only publish path is `report-github.ps1`, which emits signatures and
counts only.

### D6 — Fault-injection policy: read-only-safe and auto-reverting.

Directory faults use ADR-031 targeting (TEST-NET address; invalid target) and a
`netsh interface portproxy` 3890→389 forwarder deleted mid-session — **never
`net stop ntds` by default** (loopback firewall filtering is unreliable and a failed
NTDS restart bricks the lab; the portproxy exercises the identical
`DirectoryUnavailableException` path; NTDS stop stays a documented last-resort variant
sequenced last with a health post-check). WebView2 faults kill msedgewebview2 children of
our process tree only. State-file faults inject into the scenario's own `--state-dir`.
Every fault reverts in `finally`.

**Live-AD scenarios run seamless by design** (the D3 demo gate keeps `--state-dir`/
`--e2e`/`stateProbe` unavailable there): they are driven by UIA/WM + PrintWindow alone,
get app-side evidence via `GROUPWEAVER_LOG_DIR` (an ADR-037 env seam, deliberately not
demo-gated), and are **carved out of the state-dir integrity invariant** — they read and
may touch the operator's real `%APPDATA%`, so the runner brackets them with the proven
backup/restore idiom instead. Extending the seams to live mode remains a deliberate
future decision.

### D7 — Autonomy loop: nightly sweep → signature-deduped GitHub issues; CI stays headless.

A Windows Scheduled Task (registered once by `register-nightly-task.ps1`) runs
`run-e2e.ps1 -Tag full` → `report-github.ps1`: signature hash in the issue title, label
`e2e-failure`, search-open-first → comment on match, create otherwise; a green run
comments-and-closes stale `e2e-failure` issues it owns (deterministic PS + `gh`, the
ci-sentinel diagnose-never-fix posture). A thin `e2e-run` skill runs `-Tag smoke`
(~3–4 min) on demand before pushes. No windowed sweep in git hooks. GitHub-hosted CI
keeps the existing headless gates; a `workflow_dispatch`-only windowed job that treats
`INFRA-DRIVE` as neutral is a stretch goal and never blocks PRs.

### D8 — Staged deep diagnostics: CDP sidecar later, not first.

The primary probe path is the stdio trace + bridge `stateProbe` (PowerShell-consumable,
no Node dependency in windowed runs; jsError already flows page→host). A CDP sidecar
(`--remote-debugging-port` via `WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS` + Playwright
`connectOverCDP`) plus a read-only `window.__gwDiag` handle is a later slice for
out-of-band console capture when the bridge itself is what died.

## Where the code lives

- `tools/e2e/`: `run-e2e.ps1`, `report-github.ps1`, `register-nightly-task.ps1`,
  `gen-demo-dataset.ps1`, `README.md` (driving policy + how to add a scenario),
  `lib/e2e-driver.ps1` + `lib/e2e-channel.ps1` + `lib/e2e-evidence.ps1`,
  `scenarios/scenarios.psd1` + one `.ps1` per scenario. `tools/smoke-back-nav.ps1`
  becomes a thin wrapper or is retired once the port is green.
- `src/App/StartupOptions.cs` + `Program.cs`: `--e2e`, `--state-dir`, `--demo-data`
  parsing + the demo-only guards.
- `src/App/Automation/E2eChannel.cs` (new): the stdio channel (flag-gated, observe-only).
- `src/App/Graph/GraphMessageParser.cs` + `web/graph.js`: `stateProbe`/`stateReport`
  beside the existing ping/pong.
- `src/Providers/DemoProvider.cs`: the file-path ctor through strict `LoadDataset`.
- `tests/`: headless pins for flag parsing, channel framing, probe parsing
  (`FakeGraphRenderer`), and the P0.3 corrupt-state twins.

## Security-review note

Every seam is **flag-gated and demo-mode-required** (exit-64 refusal otherwise, the
`--dump-graph` precedent), so no seam exposes live-directory data or behavior. The
channel is observation-only by design review — no command mutates state, and nothing in
the harness or seams adds a directory-write path (fixture changes remain exclusively the
sanctioned seed path). `report-github.ps1` publishes signatures, classes, and counts —
never DNs (windowed scenarios run demo/lab fixture data at that). The portproxy fault is
local machine config, created and deleted by the run, DC untouched. **No new LDAP,
file-format, or deserialization surface in the product** (`--demo-data` reuses the
existing strict validation on operator-chosen local files).

## Rejected alternatives

- **C# xUnit + FlaUI harness.** Not blocked technically (FlaUI wraps COM UIA, not the
  GAC client), but it discards the proven PS driver investment, re-opens closed flakes,
  and adds a test-host layer between the watchdog and a separate GUI process; the
  failure-triage model here is process-shaped, not test-framework-shaped.
- **CDP/Playwright as the primary windowed probe.** Adds a Node dependency and
  multi-target resolution to every scenario; the bridge probe covers page truth while
  the bridge lives — CDP is staged for the bridge-is-dead cases (D8).
- **Golden-image pixel gates.** Nondeterministic across GPU/software rendering — repo
  policy; screenshots stay evidence, not gates.
- **`net stop ntds` as the default sever.** See D6 — same app code path via portproxy at
  zero DC risk.
- **Backup/restore of the real `%APPDATA%` per scenario.** The proven-but-hazardous
  smoke idiom; hermetic `--state-dir` removes the shared-state hazard class entirely.
- **Windowed suite as a blocking CI gate.** No DC, no guaranteed input desktop on hosted
  runners; lab-local nightly + on-demand smoke is the reliable signal.
- **Scheduling via git hooks or a Claude-driven loop.** Minutes-long windowed runs don't
  belong in hooks; a Scheduled Task + deterministic reporter needs no model in the loop.

## Consequences

- `smoke-back-nav.ps1` is superseded by scenario `back-nav` inside the runner; the
  invariant pack (clean-shutdown gate, WER scan, window-set check) applies to every
  scenario, not one.
- The app gains small, flag-gated, demo-only automation seams — production behavior with
  no flags is byte-identical; headless tests pin the guards.
- A nightly machine-readable signal (`summary.json`/`verdict.json`) plus a deterministic
  GitHub feedback loop makes windowed regressions self-reporting; scenario P0.2 lands
  red and flips green when ADR-037's heartbeat ships (the deliberate acceptance pair).
- Scale truth becomes measurable (mode-keyed budgets over `--demo-data` fixtures); the
  lab fixture gains the opt-in mega-group/distribution/comma-RDN additions (#248) via
  the sanctioned seed path.
- Implementation: WP3 (#242), WP4 (#243), WP5 (#244), WP6 (#245), WP7 (#246), WP8
  (#247), WP9 (#248); Status flips to Accepted when WP4 lands.
