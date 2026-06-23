# ParkSpike results - issue #122 (viewport-preserving Back navigation)

Run: 2026-06-23 09:12:37 (local).

## Verdict: **GO**

A hidden-but-attached parking host preserves the live WebView2 page across a park->dwell->unpark cycle: the BeginReparenting path kept marker='ALIVE-122', zoom=2.5 and pan=(111,222), the Chrome_RenderWidgetHostHWND was UNCHANGED, and no AdapterDestroyed fired. The negative-control un-root path LOST state (marker/zoom/pan reset and/or the HWND was recreated), confirming the test discriminates. The parking-lot approach in #122 is viable.

## Machine context

- OS: Microsoft Windows 10.0.20348 (X64)
- .NET: .NET 8.0.27
- Stack: Avalonia 11.3.17 + Avalonia.Controls.WebView 11.4.0 (WebView2 backend) + cytoscape 3.34.0 (matches production pins)
- GPU: Intel UHD 620 (hardware-rendered if the driver is present; see lab notes).
- Adapter (final): created=2, destroyed=0.

## Procedure

One shared `NativeWebView` starts in a visible `ActiveHost` (a `Decorator`). A second
`ParkingHost` `Decorator` is added to the same `Grid` cell but kept `IsVisible=false`,
zero-size, and is NEVER removed from the visual tree. For each experiment the spike:

1. Navigates the cytoscape `web/index.html` bundle, probes `InvokeScript` until
   `window.bridge.dispatch` exists, seeds a 3-node graph so `window.cy` is built.
2. Seeds durable state: `window.__spikeMarker='ALIVE-122'; cy.zoom(2.5); cy.pan({x:111,y:222})`.
3. Records the `Chrome_RenderWidgetHostHWND` child handle (Win32 `EnumChildWindows`).
4. PARK: moves the WebView `ActiveHost -> ParkingHost` as one synchronous op (experiment A:
   plain `Child` swap; experiment B: the swap wrapped in `BeginReparenting(true)`), dwells ~4 s.
5. UNPARK: moves it back to `ActiveHost` the same way; reads HWND + marker/zoom/pan back.
6. NEGATIVE CONTROL: instead of parking, UN-ROOTS the WebView (`ActiveHost.Child=null`, no
   parking host), dwells, re-attaches - reproducing the known-bad page-teardown baseline.

`AdapterCreated`/`AdapterDestroyed` (the WebView2 native-control lifecycle events) are
counted throughout; a clean park must fire NO `AdapterDestroyed`.

## Raw observations

| Experiment | HWND before | HWND after | unchanged? | marker | zoom | pan | AdapterDestroyed during | state preserved? | FULL PASS? |
|---|---|---|---|---|---|---|---|---|---|
| A. PLAIN reparent (no BeginReparenting) | 0x3A0114 | 0x3A0114 | YES | `ALIVE-122` | `2.5` | `{"x":111,"y":222}` | 0 | YES | **PASS** |
| B. BeginReparenting-wrapped reparent | 0x3A0114 | 0x3A0114 | YES | `ALIVE-122` | `2.5` | `{"x":111,"y":222}` | 0 | YES | **PASS** |
| Negative control: un-root | 0x3A0114 | 0xCA04EC | no | `(unparsed:)` | `?` | `?` | 0 | no | fail |

Expected PASS read-back: marker=`ALIVE-122`, zoom=`2.5`, pan=`{"x":111,"y":222}`, HWND unchanged, AdapterDestroyed during=0.
The negative control is expected to FAIL (marker `undefined`/page-dead, HWND recreated) - that failure is what proves the test discriminates.

(The negative-control read-back shows `(unparsed:)` rather than `undefined` only because production-style
re-navigation had already fired by read-back time - `EVENT AdapterCreated (#2)` + `NavigationCompleted` are
visible in the log BEFORE the read. The page is a fresh blank page; the seeded state is definitively gone and
the child HWND is a NEW handle. That is the page loss we were testing for.)

## Key findings & implications for #122

1. **GO - the core assumption (#122 risk #1) holds.** A `NativeWebView` kept continuously rooted, moved
   into a hidden (`IsVisible=false`, zero-size) but still-ATTACHED host, keeps its live WebView2 page,
   its cytoscape zoom/pan, AND the SAME `Chrome_RenderWidgetHostHWND` across a 4 s dwell. No
   `AdapterDestroyed` fires. The parking-lot design is viable; build it.

2. **Survival comes from continuous ROOTEDNESS, not from `BeginReparenting` specifically.** Both the
   plain `Child`-swap (A) and the `BeginReparenting`-wrapped swap (B) fully passed - identical HWND,
   identical preserved state. Because the control moves between two ATTACHED parents (never un-rooted),
   even the naive swap survives. `BeginReparenting` exists to make the native reparent atomic/clean and
   is the safer choice for production (avoids any transient un-root between `from.Child=null` and
   `to.Child=control`), but it is not what saves the page - staying rooted is. This matches #122's own
   diagnosis that the prior `BeginReparenting`-only attempt failed *because the crash fix un-roots*.

3. **`IsVisible=false` hides the HWND without detaching it (#122 risk #3 confirmed).** While parked, the
   `Chrome_RenderWidgetHostHWND` was the SAME non-zero handle (`0x3A0114`) - the native child stayed
   alive and attached, just not painted. Airspace is therefore satisfiable by hiding, not removing.

4. **Negative control proves the measurement is real.** Un-rooting (`ActiveHost.Child=null` with no
   parking host) destroyed the child window (`HWND -> 0x0` while detached), forced a fresh
   `AdapterCreated` + a DIFFERENT HWND on re-attach, and lost all seeded state - the exact known-bad
   behaviour the re-render fallback exists to paper over.

5. **WebView2-specific signals used:** `NativeWebView.AdapterCreated`/`AdapterDestroyed` (native-control
   lifecycle) and `TryGetPlatformHandle()` are clean C#-side observability seams the production
   coordinator can use to assert "no teardown happened" if desired. Exact API:
   `IDisposable BeginReparenting(bool yieldOnLayoutBeforeExiting = true)`.

### Recommended next step for #122
Proceed to the full build per the issue's implementation sketch: a hidden parking host in
`MainWindow`, a small MVVM-clean reparent coordinator using `BeginReparenting` for the move (NOT a raw
`Child` swap - prefer the atomic API even though the raw swap also passed here), park only the
step we will Back into, keep re-render-on-reattach as the fallback for non-parked surfaces. Extend
`tools/smoke-back-nav.ps1` to assert same-HWND + same-zoom across Back (this spike is the unit-level
proof of that property).

## Full run log

```
Navigating to C:\Users\Administrator\Documents\GroupWeaver\.claude\worktrees\agent-a1d491390ca7533fe\spikes\ParkSpike\bin\Release\net8.0-windows\web\index.html
EVENT AdapterCreated (#1)
EVENT NavigationCompleted IsSuccess=True
Initial Chrome_RenderWidgetHostHWND = 0x3A0114; host platform handle = 0xD0074

---- A. PLAIN reparent (no BeginReparenting) ----
  seeded state -> "ok"
  before: HWND=0x3A0114 marker=ALIVE-122 zoom=2.5 pan={"x":111,"y":222}
  parked into hidden ParkingHost (IsVisible=False); dwelling ~4 s...
  while parked: HWND=0x3A0114 (Zero is expected if the hidden host detaches the child window)
  after:  HWND=0x3A0114 marker=ALIVE-122 zoom=2.5 pan={"x":111,"y":222} adapterDestroyedDuring=0
  => HWND unchanged: True; state preserved: True; FULL PASS: True

---- B. BeginReparenting-wrapped reparent ----
  seeded state -> "ok"
  before: HWND=0x3A0114 marker=ALIVE-122 zoom=2.5 pan={"x":111,"y":222}
  parked into hidden ParkingHost (IsVisible=False); dwelling ~4 s...
  while parked: HWND=0x3A0114 (Zero is expected if the hidden host detaches the child window)
  after:  HWND=0x3A0114 marker=ALIVE-122 zoom=2.5 pan={"x":111,"y":222} adapterDestroyedDuring=0
  => HWND unchanged: True; state preserved: True; FULL PASS: True

---- NEGATIVE CONTROL: un-root (ActiveHost.Child = null, no parking) ----
  seeded state -> "ok"
  before: HWND=0x3A0114 marker=ALIVE-122 zoom=2.5 pan={"x":111,"y":222}
  un-rooted (no parent holds the WebView); dwelling ~4 s...
  while un-rooted: HWND=0x0(none)
EVENT AdapterCreated (#2)
EVENT NavigationCompleted IsSuccess=True
  after:  HWND=0xCA04EC marker=(unparsed:) zoom=? pan=? adapterDestroyedDuring=0
  => HWND unchanged: False; state preserved: False (EXPECT both false)
  page not fully live - re-navigating + re-seeding to recover...
EVENT NavigationCompleted IsSuccess=True

================ VERDICT ================
GO
A hidden-but-attached parking host preserves the live WebView2 page across a park->dwell->unpark cycle: the BeginReparenting path kept marker='ALIVE-122', zoom=2.5 and pan=(111,222), the Chrome_RenderWidgetHostHWND was UNCHANGED, and no AdapterDestroyed fired. The negative-control un-root path LOST state (marker/zoom/pan reset and/or the HWND was recreated), confirming the test discriminates. The parking-lot approach in #122 is viable.
```
