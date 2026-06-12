# ADR-003: App shell — Avalonia skeleton, MVVM toolkit, and the console contract

**Status:** Accepted · **Date:** 2026-06-12
**Decides:** PLANNING.md AP 2.1 (app shell: MVVM approach, navigation, wiring,
fate of the M1 console stub) · **Phase:** 2, AP 2.1

## Context

Phase 1 ended with `src/App` as a console stub whose only job was the M1 DoD
line (`connected, N groups loaded`), pinned by a stdout process test. AP 2.1
replaces it with the real Avalonia shell that AP 2.2 mounts the WebView graph
into. That conversion forces a cluster of decisions that are cheap now and
expensive later: which MVVM machinery (it must stay deterministic under
Avalonia.Headless — DoD step 2 verifies all native chrome headlessly), which
exact package versions (ADR-001 already pins the WebView at 11.4.0), how the
app navigates between its three steps without fighting the WebView airspace
constraint (ADR-001 guardrail 5: nothing draws over the native child HWND),
how providers reach ViewModels in a two-provider read-only app, and what
happens to the console contract once the exe becomes a GUI app.

## Decision

### D1 — MVVM: CommunityToolkit.Mvvm (source generators)

`CommunityToolkit.Mvvm` 8.4.2, using `[ObservableProperty]`/`[RelayCommand]`
source generators. The toolkit is MIT, compile-time only (no runtime framework
lock-in — generated code is plain INPC the debugger steps through), and
contains no scheduler machinery: under Avalonia.Headless, behavior is driven
deterministically by `Dispatcher.UIThread.RunJobs()` with nothing else to pump.
*Rejected:* ReactiveUI (Rx scheduler friction under headless tests, large API
surface for a four-ViewModel app); hand-rolled INPC (pure boilerplate, no
analyzers catching mistakes).

### D2 — Versions: Avalonia core pinned exactly 11.3.17

All Avalonia packages (`Avalonia`, `.Desktop`, `.Themes.Fluent`,
`.Diagnostics`) are pinned **exactly 11.3.17** — the only combination
spike-validated on this box against the ADR-001-pinned
`Avalonia.Controls.WebView` 11.4.0 (full AP 0.1 evidence chain). The headless
test packages (`Avalonia.Headless.*`) must equal the core version exactly —
Avalonia's headless platform is version-locked to core. The app also takes the
two spike-derived AP 2.2 prerequisites *now*: TFM `net8.0-windows` (overrides
`Directory.Build.props`) and `app.manifest` with `<supportedOS>` (without it,
`NativeControlHost` cannot create the WebView child HWND). Future version
bumps are deliberate, reviewed commit-body events — never drive-by.
*Rejected:* floating to latest 11.x (unvalidated against the WebView pin;
supply-chain drift).

### D4 — Console fate: WinExe plus a permanent `--check` flag

`OutputType` becomes `WinExe` (no console window for the GUI), and the M1
console behavior survives as the **permanent** headless smoke/diagnostic
command `--check`: it never initializes Avalonia and preserves the M1 output
lines verbatim (`GroupWeaver {version}`, the connection description,
`connected, N groups loaded`, exit 0; the existing stderr messages and exit 1
on failure). Because a WinExe starts with no std handles when launched
interactively, `--check` attaches to the parent console
(`AttachConsole(ATTACH_PARENT_PROCESS)`) and re-binds `Console.Out`/`Error`;
redirected pipes (tests/CI) keep working untouched. The M1 stdout test is
updated/relocated to invoke `--demo --check` — never deleted.
*Rejected:* dropping the console path (loses the M1 smoke contract and the
only zero-UI connectivity diagnostic); keeping `OutputType Exe` (flashing
console window behind the GUI forever).

### D5 — Navigation: one window, step-switched content

A single `MainWindow` whose content switches between the three steps
(Connect → PickRoot → Workspace) via `ShellViewModel.CurrentStep` — no modal
dialog chain, no window swapping. Step-switched content is trivially drivable
under Avalonia.Headless (set the property, `RunJobs()`, assert), and it
encodes ADR-001 airspace guardrail 5 structurally: the workspace layout
reserves a named `GraphHost` center region for the native WebView HWND, panels
live *beside* it, and anything genuinely modal must be a separate top-level
`Window` — nothing is ever layered over the graph region.
*Rejected:* modal dialog flow (headless-hostile, and dialogs over the
workspace collide with the airspace constraint); swapping top-level windows
per step (lifetime juggling for zero benefit).

### D7 — Wiring: manual composition root, no DI container

A manual composition root in `App.OnFrameworkInitializationCompleted` builds
the object graph by hand, with one seam: a `Func<bool, IDirectoryProvider>`
factory (default `demo ? DemoProvider : LdapProvider`) so tests substitute
providers without a container. Error policy: `DirectoryUnavailableException`
is caught **only** at the ViewModel boundary and surfaces as an inline error
with a demo-mode hint; every other exception bubbles — a crash is a bug, not
something to swallow. *Rejected:* `Microsoft.Extensions.DependencyInjection`
(pure weight for two providers and four ViewModels; a container hides the
object graph this size of app can state explicitly).

## Consequences

- The shell is headless-testable end to end (D1 + D5), keeping DoD step 2's
  two-part verification honest: Avalonia.Headless for chrome, Playwright for
  the graph bundle (ADR-001 guardrail 6).
- The AP 2.2 landmines (TFM, manifest, airspace-shaped layout) are already
  defused; mounting the WebView is additive.
- `--check` stays the canonical "is anything reachable" diagnostic for users
  and CI alike; its output lines are a frozen contract pinned by tests.
- Exact pins mean Avalonia/toolkit upgrades only happen as reviewed,
  spike-or-test-backed commits — slower, deliberate, traceable.

## Decided in commit bodies

Commit-body-class decisions recorded alongside this ADR, not in it:

- **D3** — WebView2 Runtime startup check (AP 2.1/O4) probes the registry
  instead of taking a `Microsoft.Web.WebView2` SDK dependency.
- **D6** — headless UI tests live in a separate `net8.0-windows` test project
  (the existing test project stays `net8.0`; CI excludes nothing).
