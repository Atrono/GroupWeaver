// ParkSpike - de-risking spike for issue #122 (viewport-preserving Back navigation).
// THROWAWAY exploratory code; the deliverable is a GO/NO-GO verdict in RESULTS-parking.md.
//
// The one question: if we NEVER un-root the shared NativeWebView - instead moving it
// (via BeginReparenting) from a visible ActiveHost into a HIDDEN-but-still-ATTACHED
// "ParkingHost", dwelling there, then moving it back - does the WebView2 page + JS state
// (cytoscape zoom/pan) SURVIVE, and is the native Chrome_RenderWidgetHostHWND child the
// SAME handle (not recreated)?
//   PASS (GO): marker + zoom + pan preserved AND HWND unchanged after park->dwell->unpark,
//              WHILE the negative-control un-root path LOSES them (proves the test discriminates).
//   FAIL (NO-GO): the parked page also dies or the HWND is recreated.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;

namespace ParkSpike;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
        CultureInfo.DefaultThreadCurrentUICulture = CultureInfo.InvariantCulture;
        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    private static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>().UsePlatformDetect();
}

internal sealed class App : Application
{
    public override void Initialize() => Styles.Add(new FluentTheme());

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = new MainWindow(desktop);
        }

        base.OnFrameworkInitializationCompleted();
    }
}

internal sealed class MainWindow : Window
{
    private readonly IClassicDesktopStyleApplicationLifetime _desktop;
    private readonly NativeWebView _webView;
    private readonly Decorator _activeHost;   // visible, in the visual tree
    private readonly Decorator _parkingHost;  // ALWAYS attached, IsVisible=false, zero-size
    private readonly TextBlock _status;
    private readonly List<string> _statusLines = new();
    private readonly List<string> _log = new();
    private readonly string _repoRoot;

    // Native-control lifecycle counters: AdapterCreated/AdapterDestroyed fire when the
    // WebView2 adapter (the underlying native control) is created / torn down. A clean
    // park MUST NOT fire AdapterDestroyed.
    private int _adapterCreated;
    private int _adapterDestroyed;

    public MainWindow(IClassicDesktopStyleApplicationLifetime desktop)
    {
        _desktop = desktop;
        _repoRoot = FindRepoRoot();
        Title = "ParkSpike - #122";
        Width = 1300;
        Height = 850;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        _webView = new NativeWebView();
        _webView.AdapterCreated += (_, _) =>
        {
            _adapterCreated++;
            Log($"EVENT AdapterCreated (#{_adapterCreated})");
        };
        _webView.AdapterDestroyed += (_, _) =>
        {
            _adapterDestroyed++;
            Log($"EVENT AdapterDestroyed (#{_adapterDestroyed})");
        };
        _webView.NavigationCompleted += (_, e) => Log($"EVENT NavigationCompleted IsSuccess={e.IsSuccess}");

        // ActiveHost: visible, fills the left grid column. The WebView starts here.
        _activeHost = new Decorator { Child = _webView };
        Grid.SetColumn(_activeHost, 0);

        // ParkingHost: hidden, zero-size, but ALWAYS in the visual tree (never removed).
        // This is the crux of risk #1/#3: does IsVisible=false keep the native child
        // attached/alive while hiding the HWND?
        _parkingHost = new Decorator
        {
            IsVisible = false,
            Width = 0,
            Height = 0,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top,
        };
        Grid.SetColumn(_parkingHost, 0); // same cell as ActiveHost; it is hidden anyway

        _status = new TextBlock
        {
            Text = "",
            TextWrapping = TextWrapping.Wrap,
            FontSize = 11,
            Margin = new Thickness(10),
        };
        var statusScroll = new ScrollViewer { Content = _status };
        Grid.SetColumn(statusScroll, 1);

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,460") };
        grid.Children.Add(_activeHost);
        grid.Children.Add(_parkingHost);
        grid.Children.Add(statusScroll);
        Content = grid;

        Opened += async (_, _) => await RunSpikeAsync();
    }

    // ---------------------------------------------------------------- the spike

    private async Task RunSpikeAsync()
    {
        var verdict = "INCONCLUSIVE";
        var reasoning = "(spike did not complete)";
        ExpResult? plainPark = null, beginPark = null, unrootCtrl = null;

        try
        {
            await NavigateAndWaitReadyAsync();

            // Sanity: the very first child HWND must exist before we measure anything.
            var hwnd0 = FindRenderWidgetHwnd();
            Log($"Initial Chrome_RenderWidgetHostHWND = {Fmt(hwnd0)}; host platform handle = {Fmt(HostHandle())}");
            if (hwnd0 == IntPtr.Zero)
            {
                throw new InvalidOperationException("No Chrome_RenderWidgetHostHWND child found after initial nav.");
            }

            // ---- Experiment A: plain reparent (clear ActiveHost, set ParkingHost) ----
            plainPark = await RunParkExperimentAsync(
                name: "A. PLAIN reparent (no BeginReparenting)",
                useBeginReparenting: false);

            // Recover the page if A tore it down, so B starts from a known-good live page.
            await EnsureLivePageAsync();

            // ---- Experiment B: BeginReparenting-wrapped reparent ----
            beginPark = await RunParkExperimentAsync(
                name: "B. BeginReparenting-wrapped reparent",
                useBeginReparenting: true);

            await EnsureLivePageAsync();

            // ---- Negative control: UN-ROOT (set ActiveHost.Child=null, no parking) ----
            unrootCtrl = await RunUnrootControlAsync();

            // ---- Verdict ----
            // GO requires: at least one park technique preserves marker+zoom+pan AND keeps the
            // SAME Chrome_RenderWidgetHostHWND, AND no AdapterDestroyed during that park, WHILE
            // the negative-control un-root path LOSES state (discriminating).
            var parkPass = (plainPark is { } p && p.IsFullPass) || (beginPark is { } b && b.IsFullPass);
            var negDiscriminates = unrootCtrl is { } u && !u.StatePreserved;

            if (parkPass && negDiscriminates)
            {
                verdict = "GO";
                var which = (beginPark?.IsFullPass ?? false) ? "BeginReparenting" :
                            (plainPark?.IsFullPass ?? false) ? "plain reparent" : "?";
                reasoning =
                    $"A hidden-but-attached parking host preserves the live WebView2 page across a park->dwell->unpark cycle: " +
                    $"the {which} path kept marker='ALIVE-122', zoom=2.5 and pan=(111,222), the Chrome_RenderWidgetHostHWND " +
                    $"was UNCHANGED, and no AdapterDestroyed fired. The negative-control un-root path LOST state " +
                    $"(marker/zoom/pan reset and/or the HWND was recreated), confirming the test discriminates. " +
                    $"The parking-lot approach in #122 is viable.";
            }
            else if (!negDiscriminates)
            {
                verdict = "INCONCLUSIVE";
                reasoning =
                    "The negative-control un-root path did NOT lose state, so the test does not discriminate " +
                    "between park and un-root - the measurement is not trustworthy. Investigate before trusting any verdict.";
            }
            else
            {
                verdict = "NO-GO";
                reasoning =
                    "Even while parked in a hidden-but-attached host, the WebView2 page did NOT survive intact " +
                    "(marker/zoom/pan lost and/or the Chrome_RenderWidgetHostHWND was recreated / AdapterDestroyed fired). " +
                    "The negative-control un-root path behaved as the known-bad baseline. The parking-lot approach in " +
                    "#122 is moot; #122 stays on the accepted re-render-on-reattach fallback.";
            }
        }
        catch (Exception ex)
        {
            verdict = "NO-GO (exception)";
            reasoning = $"Spike threw: {ex.GetType().Name}: {ex.Message}. A clean NO-GO with evidence is a valid outcome.";
            Log("FATAL: " + ex);
        }

        Log("");
        Log("================ VERDICT ================");
        Log(verdict);
        Log(reasoning);

        WriteResults(verdict, reasoning, plainPark, beginPark, unrootCtrl);
        _desktop.Shutdown(verdict == "GO" ? 0 : 1);
    }

    /// <summary>
    /// One park experiment: seed durable state, record HWND, move WebView ActiveHost -> ParkingHost
    /// (optionally inside a BeginReparenting scope, as a single synchronous operation), dwell ~4 s,
    /// move it back, then read state + HWND back.
    /// </summary>
    private async Task<ExpResult> RunParkExperimentAsync(string name, bool useBeginReparenting)
    {
        Log("");
        Log($"---- {name} ----");
        var r = new ExpResult { Name = name };

        // Seed durable state on the live page.
        await SeedStateAsync();
        r.HwndBefore = FindRenderWidgetHwnd();
        r.AdapterDestroyedBefore = _adapterDestroyed;
        var (mB, zB, pB) = await ReadStateAsync();
        Log($"  before: HWND={Fmt(r.HwndBefore)} marker={mB} zoom={zB} pan={pB}");

        // PARK: move the WebView from ActiveHost into ParkingHost as ONE synchronous op.
        // Plain: just swap Child references. BeginReparenting: do the swap inside the scope,
        // then dispose the scope. The bool 'yieldOnLayoutBeforeExiting' defaults to true; we
        // pass true (let the control settle its native reparent on the next layout pass).
        void DoMove(Decorator from, Decorator to)
        {
            from.Child = null;
            to.Child = _webView;
        }

        if (useBeginReparenting)
        {
            using (_webView.BeginReparenting(true))
            {
                DoMove(_activeHost, _parkingHost);
            }
        }
        else
        {
            DoMove(_activeHost, _parkingHost);
        }

        Log($"  parked into hidden ParkingHost (IsVisible={_parkingHost.IsVisible}); dwelling ~4 s...");
        // Pump layout so any deferred reparent/teardown actually happens while parked.
        await PumpAsync(TimeSpan.FromSeconds(4));

        var hwndWhileParked = FindRenderWidgetHwnd();
        Log($"  while parked: HWND={Fmt(hwndWhileParked)} (Zero is expected if the hidden host detaches the child window)");

        // UNPARK: move it back to ActiveHost, same technique.
        if (useBeginReparenting)
        {
            using (_webView.BeginReparenting(true))
            {
                DoMove(_parkingHost, _activeHost);
            }
        }
        else
        {
            DoMove(_parkingHost, _activeHost);
        }

        // Give the recreated/reattached child a moment to settle, then read back.
        await PumpAsync(TimeSpan.FromMilliseconds(800));

        r.HwndAfter = FindRenderWidgetHwnd();
        r.AdapterDestroyedDuring = _adapterDestroyed - r.AdapterDestroyedBefore;
        (r.Marker, r.Zoom, r.Pan) = await ReadStateAsync();
        Log($"  after:  HWND={Fmt(r.HwndAfter)} marker={r.Marker} zoom={r.Zoom} pan={r.Pan} adapterDestroyedDuring={r.AdapterDestroyedDuring}");
        Log($"  => HWND unchanged: {r.HwndUnchanged}; state preserved: {r.StatePreserved}; FULL PASS: {r.IsFullPass}");
        return r;
    }

    /// <summary>
    /// Negative control: reproduce the KNOWN-BAD behaviour. Instead of parking, UN-ROOT the
    /// WebView (set ActiveHost.Child = null with NO parking host holding it), dwell, then
    /// re-attach. This is what production's leaving view does today; it must LOSE state /
    /// recreate the HWND, proving the test measures something real.
    /// </summary>
    private async Task<ExpResult> RunUnrootControlAsync()
    {
        Log("");
        Log("---- NEGATIVE CONTROL: un-root (ActiveHost.Child = null, no parking) ----");
        var r = new ExpResult { Name = "Negative control: un-root" };

        await SeedStateAsync();
        r.HwndBefore = FindRenderWidgetHwnd();
        r.AdapterDestroyedBefore = _adapterDestroyed;
        var (mB, zB, pB) = await ReadStateAsync();
        Log($"  before: HWND={Fmt(r.HwndBefore)} marker={mB} zoom={zB} pan={pB}");

        // UN-ROOT: nothing holds the control. This is the un-rooting the issue describes.
        _activeHost.Child = null;
        Log("  un-rooted (no parent holds the WebView); dwelling ~4 s...");
        await PumpAsync(TimeSpan.FromSeconds(4));

        var hwndWhileDetached = FindRenderWidgetHwnd();
        Log($"  while un-rooted: HWND={Fmt(hwndWhileDetached)}");

        // RE-ATTACH (production re-navigates+replays here; we DON'T, to show the raw page loss).
        _activeHost.Child = _webView;
        await PumpAsync(TimeSpan.FromMilliseconds(800));

        r.HwndAfter = FindRenderWidgetHwnd();
        r.AdapterDestroyedDuring = _adapterDestroyed - r.AdapterDestroyedBefore;
        (r.Marker, r.Zoom, r.Pan) = await ReadStateAsync();
        Log($"  after:  HWND={Fmt(r.HwndAfter)} marker={r.Marker} zoom={r.Zoom} pan={r.Pan} adapterDestroyedDuring={r.AdapterDestroyedDuring}");
        Log($"  => HWND unchanged: {r.HwndUnchanged}; state preserved: {r.StatePreserved} (EXPECT both false)");

        // Leave a live page for nothing further; but recover anyway for tidiness.
        await EnsureLivePageAsync();
        return r;
    }

    // ---------------------------------------------------------------- page helpers

    private async Task NavigateAndWaitReadyAsync()
    {
        var indexPath = Path.Combine(AppContext.BaseDirectory, "web", "index.html");
        Log($"Navigating to {indexPath}");
        _webView.Navigate(new Uri(indexPath));
        await WaitBridgeReadyAsync();

        // The bundle only builds cytoscape once a tiny dataset is committed. Ship a minimal
        // graph so window.cy exists (zoom/pan need a real cytoscape instance).
        await SeedGraphAsync();
    }

    /// <summary>Probe InvokeScript until the page accepts script and window.bridge.dispatch exists.</summary>
    private async Task WaitBridgeReadyAsync()
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(60);
        while (true)
        {
            string? probe = null;
            try
            {
                probe = await _webView
                    .InvokeScript("(typeof window.bridge!=='undefined'&&typeof window.bridge.dispatch==='function')?'1':'0'")
                    .WaitAsync(TimeSpan.FromSeconds(2));
            }
            catch
            {
                // "Unable to invoke script before any page was loaded" until nav lands - keep polling.
            }

            if (probe is not null && probe.Contains('1'))
            {
                return;
            }

            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("Bridge never became ready within 60 s.");
            }

            await Task.Delay(150);
        }
    }

    /// <summary>Probe until window.cy exists (cytoscape built).</summary>
    private async Task WaitCyReadyAsync()
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                var r = await _webView.InvokeScript("(window.cy?'1':'0')").WaitAsync(TimeSpan.FromSeconds(2));
                if (r is not null && r.Contains('1'))
                {
                    return;
                }
            }
            catch
            {
                // keep polling
            }

            await Task.Delay(150);
        }

        Log("  WARN: window.cy never appeared within 30 s.");
    }

    private async Task SeedGraphAsync()
    {
        // Minimal 3-node / 2-edge graph through the existing chunk/commit protocol.
        await _webView.InvokeScript(
            "window.bridge.dispatch({type:'graphChunk',nodes:[" +
            "{id:'a',label:'A',kind:'root',x:0,y:0}," +
            "{id:'b',label:'B',kind:'group',x:120,y:0}," +
            "{id:'c',label:'C',kind:'user',x:60,y:100}]})");
        await _webView.InvokeScript(
            "window.bridge.dispatch({type:'graphChunk',edges:[" +
            "{id:'e1',s:'a',t:'b'},{id:'e2',s:'a',t:'c'}]})");
        await _webView.InvokeScript("window.bridge.dispatch({type:'graphCommit'})");
        await WaitCyReadyAsync();
    }

    /// <summary>Seed the durable marker + a distinctive zoom/pan onto the live cytoscape instance.</summary>
    private async Task SeedStateAsync()
    {
        await WaitCyReadyAsync();
        var res = await _webView.InvokeScript(
            "window.__spikeMarker='ALIVE-122'; if(window.cy){cy.zoom(2.5); cy.pan({x:111,y:222});} 'ok'");
        Log($"  seeded state -> {res}");
    }

    /// <summary>Read marker / zoom / pan back from the page.</summary>
    private async Task<(string marker, string zoom, string pan)> ReadStateAsync()
    {
        try
        {
            var raw = await _webView.InvokeScript(
                    "[String(window.__spikeMarker), window.cy?String(cy.zoom()):'no-cy', window.cy?JSON.stringify(cy.pan()):'no-cy'].join('|')")
                .WaitAsync(TimeSpan.FromSeconds(5));

            // InvokeScript returns the JS string result as a JSON-ENCODED string literal, e.g.
            //   "ALIVE-122|2.5|{\"x\":111,\"y\":222}"
            // (outer quotes + ESCAPED inner quotes). Deserialize it so the pan JSON's quotes are
            // un-escaped - a naive .Trim('"') would leave \" in place and break the comparison.
            string s;
            try
            {
                s = System.Text.Json.JsonSerializer.Deserialize<string>(raw ?? "\"\"") ?? "";
            }
            catch
            {
                s = (raw ?? "").Trim('"');
            }

            var parts = s.Split('|');
            return parts.Length == 3
                ? (parts[0], parts[1], parts[2])
                : ($"(unparsed:{s})", "?", "?");
        }
        catch (Exception ex)
        {
            // InvokeScript faulting "before any page was loaded" = the page is GONE.
            return ($"<page dead: {ex.GetType().Name}>", "dead", "dead");
        }
    }

    /// <summary>If the page died (marker lost), re-navigate + re-seed so the next experiment starts clean.</summary>
    private async Task EnsureLivePageAsync()
    {
        var (marker, _, _) = await ReadStateAsync();
        if (marker == "ALIVE-122" || marker == "undefined")
        {
            // 'undefined' = page alive but marker cleared by a fresh load; cy may need rebuild.
            try
            {
                var cy = await _webView.InvokeScript("(window.cy?'1':'0')").WaitAsync(TimeSpan.FromSeconds(2));
                if (cy is not null && cy.Contains('1'))
                {
                    return; // page + cy both live
                }
            }
            catch
            {
                // fall through to re-nav
            }
        }

        Log("  page not fully live - re-navigating + re-seeding to recover...");
        _webView.Navigate(new Uri(Path.Combine(AppContext.BaseDirectory, "web", "index.html")));
        await WaitBridgeReadyAsync();
        await SeedGraphAsync();
    }

    // ---------------------------------------------------------------- win32 / layout

    /// <summary>Pump the UI (process deferred layout + native messages) for a span without blocking.</summary>
    private static async Task PumpAsync(TimeSpan span)
    {
        var deadline = DateTime.UtcNow + span;
        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(50);
        }
    }

    private IntPtr HostHandle()
    {
        try
        {
            return _webView.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        }
        catch
        {
            return IntPtr.Zero;
        }
    }

    /// <summary>Find the Chrome_RenderWidgetHostHWND under this top-level window (recursive enum).</summary>
    private IntPtr FindRenderWidgetHwnd()
    {
        var top = TryGetTopLevelHwnd();
        if (top == IntPtr.Zero)
        {
            return IntPtr.Zero;
        }

        return FindDescendantByClass(top, "Chrome_RenderWidgetHostHWND");
    }

    private IntPtr TryGetTopLevelHwnd()
    {
        try
        {
            return this.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        }
        catch
        {
            return IntPtr.Zero;
        }
    }

    private static IntPtr FindDescendantByClass(IntPtr parent, string className)
    {
        IntPtr found = IntPtr.Zero;
        var sb = new StringBuilder(256);
        EnumChildWindows(parent, (hwnd, _) =>
        {
            sb.Clear();
            GetClassName(hwnd, sb, sb.Capacity);
            if (sb.ToString() == className)
            {
                found = hwnd;
                return false; // stop
            }

            return true;
        }, IntPtr.Zero);
        return found;
    }

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumChildWindows(IntPtr hWndParent, EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

    // ---------------------------------------------------------------- reporting

    private static string Fmt(IntPtr h) => h == IntPtr.Zero ? "0x0(none)" : "0x" + h.ToString("X");

    private void Log(string line)
    {
        Console.WriteLine(line);
        _log.Add(line);
        _statusLines.Add(line);
        if (_statusLines.Count > 40)
        {
            _statusLines.RemoveAt(0);
        }

        _status.Text = string.Join('\n', _statusLines);
    }

    private void WriteResults(string verdict, string reasoning, ExpResult? plain, ExpResult? begin, ExpResult? neg)
    {
        try
        {
            var path = Path.Combine(_repoRoot, "spikes", "ParkSpike", "RESULTS-parking.md");
            var sb = new StringBuilder();
            sb.AppendLine("# ParkSpike results - issue #122 (viewport-preserving Back navigation)");
            sb.AppendLine();
            sb.AppendLine($"Run: {DateTime.Now:yyyy-MM-dd HH:mm:ss} (local).");
            sb.AppendLine();
            sb.AppendLine($"## Verdict: **{verdict}**");
            sb.AppendLine();
            sb.AppendLine(reasoning);
            sb.AppendLine();
            sb.AppendLine("## Machine context");
            sb.AppendLine();
            sb.AppendLine($"- OS: {RuntimeInformation.OSDescription} ({RuntimeInformation.OSArchitecture})");
            sb.AppendLine($"- .NET: {RuntimeInformation.FrameworkDescription}");
            sb.AppendLine("- Stack: Avalonia 11.3.17 + Avalonia.Controls.WebView 11.4.0 (WebView2 backend) + cytoscape 3.34.0 (matches production pins)");
            sb.AppendLine("- GPU: Intel UHD 620 (hardware-rendered if the driver is present; see lab notes).");
            sb.AppendLine($"- Adapter (final): created={_adapterCreated}, destroyed={_adapterDestroyed}.");
            sb.AppendLine();
            sb.AppendLine("## Procedure");
            sb.AppendLine();
            sb.AppendLine("One shared `NativeWebView` starts in a visible `ActiveHost` (a `Decorator`). A second");
            sb.AppendLine("`ParkingHost` `Decorator` is added to the same `Grid` cell but kept `IsVisible=false`,");
            sb.AppendLine("zero-size, and is NEVER removed from the visual tree. For each experiment the spike:");
            sb.AppendLine();
            sb.AppendLine("1. Navigates the cytoscape `web/index.html` bundle, probes `InvokeScript` until");
            sb.AppendLine("   `window.bridge.dispatch` exists, seeds a 3-node graph so `window.cy` is built.");
            sb.AppendLine("2. Seeds durable state: `window.__spikeMarker='ALIVE-122'; cy.zoom(2.5); cy.pan({x:111,y:222})`.");
            sb.AppendLine("3. Records the `Chrome_RenderWidgetHostHWND` child handle (Win32 `EnumChildWindows`).");
            sb.AppendLine("4. PARK: moves the WebView `ActiveHost -> ParkingHost` as one synchronous op (experiment A:");
            sb.AppendLine("   plain `Child` swap; experiment B: the swap wrapped in `BeginReparenting(true)`), dwells ~4 s.");
            sb.AppendLine("5. UNPARK: moves it back to `ActiveHost` the same way; reads HWND + marker/zoom/pan back.");
            sb.AppendLine("6. NEGATIVE CONTROL: instead of parking, UN-ROOTS the WebView (`ActiveHost.Child=null`, no");
            sb.AppendLine("   parking host), dwells, re-attaches - reproducing the known-bad page-teardown baseline.");
            sb.AppendLine();
            sb.AppendLine("`AdapterCreated`/`AdapterDestroyed` (the WebView2 native-control lifecycle events) are");
            sb.AppendLine("counted throughout; a clean park must fire NO `AdapterDestroyed`.");
            sb.AppendLine();
            sb.AppendLine("## Raw observations");
            sb.AppendLine();
            sb.AppendLine("| Experiment | HWND before | HWND after | unchanged? | marker | zoom | pan | AdapterDestroyed during | state preserved? | FULL PASS? |");
            sb.AppendLine("|---|---|---|---|---|---|---|---|---|---|");
            AppendRow(sb, plain);
            AppendRow(sb, begin);
            AppendRow(sb, neg);
            sb.AppendLine();
            sb.AppendLine("Expected PASS read-back: marker=`ALIVE-122`, zoom=`2.5`, pan=`{\"x\":111,\"y\":222}`, HWND unchanged, AdapterDestroyed during=0.");
            sb.AppendLine("The negative control is expected to FAIL (marker `undefined`/page-dead, HWND recreated) - that failure is what proves the test discriminates.");
            sb.AppendLine();
            sb.AppendLine("## Full run log");
            sb.AppendLine();
            sb.AppendLine("```");
            foreach (var l in _log)
            {
                sb.AppendLine(l);
            }

            sb.AppendLine("```");

            File.WriteAllText(path, sb.ToString());
            Console.WriteLine($"Results written to {path}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to write RESULTS-parking.md: {ex}");
        }
    }

    private static void AppendRow(StringBuilder sb, ExpResult? r)
    {
        if (r is null)
        {
            sb.AppendLine("| (not run) | - | - | - | - | - | - | - | - | - |");
            return;
        }

        sb.AppendLine(
            $"| {r.Name} | {Fmt(r.HwndBefore)} | {Fmt(r.HwndAfter)} | {(r.HwndUnchanged ? "YES" : "no")} | " +
            $"`{r.Marker}` | `{r.Zoom}` | `{r.Pan}` | {r.AdapterDestroyedDuring} | {(r.StatePreserved ? "YES" : "no")} | " +
            $"{(r.IsFullPass ? "**PASS**" : "fail")} |");
    }

    private static string FindRepoRoot()
    {
        // In a git WORKTREE, ".git" is a FILE (a gitdir pointer), not a directory - so probe for
        // BOTH. Anchor on the spike's own folder: walk up until we find the dir that CONTAINS
        // "spikes/ParkSpike" (the worktree root), so RESULTS lands in this worktree, never the
        // main repo. Fall back to the .git probe, then cwd.
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "spikes", "ParkSpike")) ||
                Directory.Exists(Path.Combine(dir.FullName, ".git")) ||
                File.Exists(Path.Combine(dir.FullName, ".git")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        return Environment.CurrentDirectory;
    }

    private sealed class ExpResult
    {
        public required string Name { get; init; }
        public IntPtr HwndBefore { get; set; }
        public IntPtr HwndAfter { get; set; }
        public string Marker { get; set; } = "";
        public string Zoom { get; set; } = "";
        public string Pan { get; set; } = "";
        public int AdapterDestroyedBefore { get; set; }
        public int AdapterDestroyedDuring { get; set; }

        // HWND must be non-null and identical before/after.
        public bool HwndUnchanged => HwndBefore != IntPtr.Zero && HwndAfter == HwndBefore;

        // State preserved = the exact seeded values survived.
        public bool StatePreserved =>
            Marker == "ALIVE-122" &&
            ParsesTo(Zoom, 2.5) &&
            Pan.Replace(" ", "") is "{\"x\":111,\"y\":222}";

        // Full pass also requires the native control was never torn down.
        public bool IsFullPass => HwndUnchanged && StatePreserved && AdapterDestroyedDuring == 0;

        private static bool ParsesTo(string s, double expect) =>
            double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && Math.Abs(v - expect) < 1e-6;
    }
}
