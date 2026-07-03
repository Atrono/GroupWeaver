# ADR-037: Production logging & diagnostics — a JSONL file sink, crash handling, and a bridge liveness heartbeat

**Status:** Accepted (WP1 #240 landed 2026-07-03) · **Date:** 2026-07-03
**Decides:** issue #238 (production logging/diagnostics + crash handling; consumed by the #239 E2E harness) · **Phase:** 4 (feedback-driven)
**Builds on:** ADR-004/ADR-005 (the bridge message protocol the trace summarizes), ADR-008 + ADR-032 / `RulesetLocator` / `AuditRunStore` (the `%APPDATA%\GroupWeaver\` persistence idiom: atomic write, never-throw, injected-base-dir seam, hardened JSON), ADR-024 (`ReNavigateAndReplayAsync`'s capability probe — reused by the heartbeat), `.claude/rules/lab-environment.md` (GPU-driver loss on rebuild → the rendering-mode banner field)

## Context

The app has **no logging at all**: three `Debug.WriteLine` calls in `AuditRunStore`, CLI
`Console.WriteLine` in `Program`, and nothing else. There is no global exception handler —
`Program.Main` calls `StartWithClassicDesktopLifetime` bare, so an async-void RelayCommand
fault or a UI-thread exception kills the process silently. Known silent failure modes:
a WebView2 child-process crash leaves a dead graph with no signal; fire-and-forget bridge
commands (busy/select/theme) drop their errors; ruleset degradation to the embedded
default is visible only inside the Settings window; `ExportPngAsync` timing out returns
`null` and the user gets a blank file.

Two consumers force the issue now. The autonomous E2E harness (#239) needs
machine-readable app-side evidence to classify a failing windowed run (product crash vs
assert vs infrastructure) without a human watching. And real users filing GitHub issues
have nothing to attach. The hard constraints: **read-only product** (logging must never
add an AD interaction or any network I/O), **privacy** (logs will contain DNs and account
names of a real directory — default output must be safe to attach to a public issue),
**hot paths** (chunked bridge traffic at the 10K-edge target must not pay for logging),
and **Core purity** (`RuleEngine.Evaluate`/`GraphBuilder.Build` are pure static — see
[[rule-engine]], [[data-model]]).

## Decision

### D1 — `Microsoft.Extensions.Logging.Abstractions` as the API; a hand-rolled JSONL file sink; no Serilog. Core gets no logger.

The API is MEL.Abstractions (one dependency-free package; `LoggerMessage` source
generators give `IsEnabled`-guarded, allocation-free calls). The sink is our own
(~250 lines, new `src/App/Diagnostics/`) because the actual requirement — one process,
JSONL, size cap, never-throw — is small enough to own with existing house idioms (strict
STJ encoder, UTF-8 no BOM, atomic file ops, never-throw). **Core takes no logger
dependency**: App-side callers time `RuleEngine.Evaluate`/`GraphBuilder.Build` with a
`Stopwatch` and log the result. `src/Providers` types accept an optional
`ILogger? logger = null` ctor parameter (the defaulted-param idiom — existing call sites
and tests compile unchanged).

### D2 — Composition: set-once `AppLog`, `NullLoggerFactory` default, GUI path only.

`Program.Main` (GUI path) parses log flags/env → builds the sink + `ILoggerFactory` →
stores it in a set-once `AppLog` static that defaults to `NullLoggerFactory.Instance`, so
every headless test runs exactly as today. `App.OnFrameworkInitializationCompleted`
threads it into `ShellViewModel` like the existing stores. The `--check` / `--dump-graph`
CLI paths stay console-only — their stdout is pinned by `AppCliTests` and must not change.

### D3 — Sink mechanics: `%APPDATA%\GroupWeaver\logs\gw-<utcstamp>-<pid>.jsonl`.

Directory overridable via `GROUPWEAVER_LOG_DIR` (the harness seam). One STJ object per
line with fixed leading fields `ts, lvl, cat, evt, sid`, then the payload, optional
`ex {type, msgScrubbed, stack}`. Bounded `Channel<LogEvent>` (overflow drops with a
counter → one `LogBackpressureDropped` Warn), single background writer, flush ~500 ms and
immediately on Warning+. 5 MB per-file roll, startup retention prune to 10 files / 20 MB.
Never-throw everywhere: a logging failure must never surface as an app failure.

### D4 — Levels and the event-name contract.

File default is **Information** — safe by construction (counts, durations, error codes,
step names; never subject data). Debug = per-operation internals (still redacted). Trace =
bridge per-chunk/per-message summaries (sizes and types only, **never payloads**).
`--verbose-logs` / `GROUPWEAVER_LOG=trace` raises volume only, never sensitivity. Stable
PascalCase `evt` names per category (`App.Lifecycle`, `App.Shell`, `Ldap`, `Graph.Renderer`,
`Graph.Bridge`, `Rules.Engine`, `Store.*`, `Export`, `Crash`) are the machine contract the
E2E triager greps; uniqueness is pinned by test. An 8-char random session id `sid` joins
every line to the banner and crash marker; a monotonic `op` counter joins
Started/Completed pairs per pipeline.

### D5 — The catalog covers every currently-silent failure.

Highlights (the full catalog lands with the code): `StepChanged{from,to,trigger}` (the E2E
timeline backbone) · `LdapOpCompleted/Failed{hresult,kind}` + `LdapRangedRetrieval{rounds,
totalMembers}` + **`LdapMembersUnresolved{unresolvedCount,totalCount}`** (the silent
DN→External coercion, aggregated per call) · `ScopeLoadStarted/Completed/Failed`,
`GraphBuilt`, `RuleEvaluated{durationMs,violations,bySeverity}`, **`LoadErrorShown`** ·
`AdapterCreated/AdapterDestroyed`, `BridgeReady{webglRenderer}`, `RenderDispatchStarted`,
`RenderCompleted`, **`RenderTimeout{phase}`** (the 60 s timeouts, now attributable),
`JsErrorReported`, **`FireAndForgetFailed{command}`**, `SingleFlightViolation`,
**`ExportPngFailed{reason=timeout|decodeNull|dispatch}`** · `RulesetLoaded{source,hash}`,
**`RulesetDegraded{errorCount,firstPaths}` at the composition root** (not just Settings
UI), **`AuditRunSkipped{reason}`** (replaces the three `Debug.WriteLine`s),
`Export{Started,Completed,Failed}`. `UiStateSaveFailed` stays Debug — saves remain
best-effort by contract (ADR-022 D4).

### D6 — Startup banner + rendering-mode truth.

`AppStarted`: version, mode (demo|live), sid, pid, OS build, .NET/Avalonia versions,
WebView2 runtime version (already probed once), log level, redaction mode, and flag NAMES
only (never `--server` values). `UiEnvironment` (from `MainWindow.OnOpened`): screens,
resolution, `RenderScaling`. The graph.js `ready` message gains a `webglRenderer` field
(`WEBGL_debug_renderer_info`; "SwiftShader" = software rendering — the lab box loses its
GPU driver on every rebuild and perf numbers must state their mode).
`GraphMessageParser` already tolerates extra fields, so this is an additive
`ReadyMessage` change.

### D7 — Crash handling: last-chance handlers, persist-first marker, exit 70.

try/catch around `StartWithClassicDesktopLifetime` → Critical log + crash marker +
bounded 2 s flush + exit code 70 (this catches today's silent async-void/UI-thread
deaths). `AppDomain.CurrentDomain.UnhandledException` takes the same path;
`TaskScheduler.UnobservedTaskException` logs Error + `SetObserved()`. The crash marker
`logs\crash-<sid>-<utc>.json` (schemaVersion, sid, exType, scrubbed message, stack,
version, logFile) is written temp+move **before** the flush attempt (persist-first, the
ADR-032 idiom); the next start logs `PreviousCrashDetected`. **WER LocalDumps registry
setup belongs to `tools/bootstrap.ps1` only** (lab box; the shipped app never writes HKLM).

### D8 — Bridge liveness heartbeat instead of `ProcessFailed` — the WebView2-crash detector.

Verified: the app uses `Avalonia.Controls.WebView`, not the Microsoft WebView2 SDK.
`NativeWebView` exposes only navigation/message/adapter events; `ProcessFailed` exists
only on a package-internal interop interface, and `CytoscapeGraphRenderer` already
documents that raw-COM vtable pokes on the platform handle are out of scope. Substitute:
(1) while a graph step is active and navigated, a 10 s UI-thread timer runs the same
`typeof window.bridge.dispatch === 'function'` capability probe `ReNavigateAndReplayAsync`
already uses (2 s-bounded, skipped while a command is in flight); one miss =
`HeartbeatMissed` Warn, three consecutive = `HeartbeatLost` Error + `RaiseError` into the
existing RendererError→LoadError banner ("The graph view stopped responding — Reload
scope."). (2) `AdapterCreated`/`AdapterDestroyed` bracket the native child's life — an
unexpected Destroy is the crash smell. (3) The E2E harness independently watches
msedgewebview2 children, the event log, and crashpad dumps.

### D9 — Redaction: session-salted hashes, default-safe, `--log-plain` opt-in.

Sensitive: DNs and fragments, object/sAMAccountNames, server/domain/baseDn, run-file
slugs, full user paths, provider exception messages (they embed DNs), jsError text. Not
sensitive: counts, durations, HResults, error kinds, versions, step names, the GPU
string, the ruleset hash. Mechanism, two layers: (1) **call-site typed helpers**
`Redactor.Dn(v)` / `Redactor.Host(v)` → `dn#a1b2c3d4` (first 8 hex of
SHA-256(sessionSalt‖value)) — stable within a session so events join, unlinkable across
sessions; structured fields are pre-redacted at the call site, never formatter-guessed
(the guard-predicate-drift lesson). (2) a free-text scrubber for exception/jsError
messages (`(CN|OU|DC)=…` runs + the concrete server/baseDn strings learned at connect).
Default = redacted at **every** level. `--log-plain` swaps in the identity redactor,
suffixes the filename `-PLAIN`, and writes a first-line warning; E2E/lab runs use
`--verbose-logs --log-plain` (demo/fixture data only). A "no raw DN at default level"
sweep test is the guarantee. **Redaction gates the next tagged release** — logging never
ships without it.

### D10 — User diagnostics stay minimal; no telemetry, ever.

An "Open logs folder" button in Settings and an issue-template line ("files without
`-PLAIN` are safe to attach"); later, `--diag` zips logs+versions (excluding `-PLAIN`
files, `runs\*.json`, `ruleset.jsonc`). Everything stays on the local disk — no upload,
no endpoint, zero AD interaction added by any of this.

## Where the code lives

- `src/App/Diagnostics/` (new): `FileLogSink` + `AppLog` (set-once factory holder) +
  `Redactor` — the sink/redaction unit-test surface.
- `src/App/Program.cs`: flag/env parsing, sink construction, last-chance handlers, crash
  marker, exit 70; CLI paths untouched (stdout pinned by `AppCliTests`).
- `src/App/App.axaml.cs`: threads the factory into `ShellViewModel`; logs
  `RulesetDegraded` at load.
- `src/App/Graph/CytoscapeGraphRenderer.cs` + `GraphMessageParser.cs` + `web/graph.js`:
  the densest event surface — bridge trace, timeouts-with-phase, adapter lifecycle,
  heartbeat, `ExportPngFailed`, the `ready`+`webglRenderer` extension.
- `src/Providers/LdapProvider.cs`: op timing/classification, ranged-retrieval stats,
  unresolved-member aggregation (optional-logger ctor).
- `src/App/ViewModels/ShellViewModel.cs`: `StepChanged` and step-machine events.
- `src/App/Audit/AuditRunStore.cs`: the three `Debug.WriteLine` → `AuditRunSkipped{reason}`.
- `tools/bootstrap.ps1`: WER LocalDumps registry (idempotent, lab box setup).

## Security-review note

The log file is a **new local write surface** (user-profile `%APPDATA%`, atomic,
never-throw — the ADR-032 conventions). It must use the strict/non-relaxed STJ encoder
(the relaxed-encoder regression is a recurring finding class), and the default-level
output must contain **no raw DN/name/server data** — pinned by the D9 sweep test. Logs
are excluded from exports and public media. **No directory-write path, no new LDAP or
deserialization surface** (the sink only writes; nothing reads logs back into the app),
**no network I/O**. The heartbeat only re-runs an existing read-only script probe.

## Rejected alternatives

- **Serilog/NLog.** 2+ third-party assemblies with their own JSON/config stacks in a repo
  that pins every dependency and security-reviews each release; the need is ~250 lines.
- **`ILogger` inside Core.** Breaks the pure/static/UI-free contracts ([[rule-engine]]);
  timings are observable from the App call sites at zero purity cost.
- **EventSource/ETW.** Opaque to users filing issues and awkward for a PowerShell harness
  to consume; JSONL is greppable by both.
- **Raw COM interop to reach `ProcessFailed`.** The renderer already declares vtable
  pokes on the platform handle out of scope; the heartbeat detects the same failure class
  with supported APIs. Revisit only if exact failure *reasons* become load-bearing.
- **Formatter-level redaction (scrub everything at serialization).** Guessing sensitivity
  from string shape is exactly the guard-predicate-drift failure class; call-site typed
  helpers make sensitivity explicit per field, with the scrubber as a free-text safety
  net only.
- **Telemetry/crash upload.** Read-only trust posture; everything stays on disk.

## Consequences

- The E2E harness (#239) gets machine-readable app evidence: the trace timeline, the
  crash marker, `verdict.json`'s `appErrors` extraction, and the heartbeat turn "the
  graph died silently" into a classified, assertable event (scenario P0.2's acceptance).
- Users can attach a default-level log to a public issue without leaking directory data.
- New tests: never-throw sink (unwritable/locked/deleted-mid-run), roll/retention with
  injected caps, leading-field-order pin, redaction hash stability + escaped-DN scrubber
  corpus (reusing the `Dn`/`DnPath` test DNs) + raw-DN-absence sweep, disabled-Trace does
  zero work on the chunk path, flush-on-crash deadline, heartbeat via the existing
  renderer fakes; `RendererFaultNeverCrashesTests` is extended, never weakened.
- `AppCliTests` continues to pin that CLI stdout is unchanged; the headless suite runs
  under `NullLoggerFactory` untouched.
- WP1 (#240) implements D1–D7 core, WP2 (#241) D5/D6/D8 renderer surface, WP10 (#249)
  D9/D10; Status flips to Accepted when WP1 lands.
