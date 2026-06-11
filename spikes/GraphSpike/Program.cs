// GraphSpike - Phase 0 spike (AP 0.1, issue #1). Throwaway code; results feed ADR-001.
// Proves: 5k-node cytoscape graph inside Avalonia-hosted NativeWebView (WebView2),
// pan/zoom FPS, bidirectional bridge roundtrips, dataset transfer time, JS error capture.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;

namespace GraphSpike;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        // Invariant culture so RESULTS.md numbers are unambiguous (box is de-DE:
        // "609.387 bytes" would mean 609,387).
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

internal sealed record SpikeNode(string Id, string Label, string Kind, double X, double Y);

internal sealed record SpikeEdge(string Id, string S, string T);

internal sealed class Dataset
{
    public required List<SpikeNode> Nodes { get; init; }
    public required List<SpikeEdge> Edges { get; init; }
    public required string CollapsedParentId { get; init; }
    public required List<SpikeNode> HiddenChildren { get; init; }
    public required List<SpikeEdge> HiddenEdges { get; init; }

    // 5,000 visible nodes in concentric rings ("AD in the center", BFS depth = ring),
    // 4,999 tree edges + 1,500 cross edges = 6,499. One collapsed L2 node (n42) holds
    // 25 hidden children delivered later via the expand command (-> 5,025 / 6,524).
    public static Dataset Build()
    {
        int[] levelCounts = { 1, 10, 100, 1000, 3889 }; // sum = 5000
        string[] levelKinds = { "root", "ou", "group", "group", "user" };
        var nodes = new List<SpikeNode>(5000);
        var edges = new List<SpikeEdge>(6499);
        var levelStart = new int[levelCounts.Length];

        double radius = 0;
        int id = 0;
        for (int depth = 0; depth < levelCounts.Length; depth++)
        {
            levelStart[depth] = id;
            int count = levelCounts[depth];
            if (depth > 0)
            {
                // Enough circumference for ~12 px spacing, at least 450 px ring gap.
                radius = Math.Max(radius + 450, count * 12.0 / (2 * Math.PI));
            }

            for (int j = 0; j < count; j++)
            {
                double angle = 2 * Math.PI * j / count + depth * 0.35;
                string kind = levelKinds[depth];
                if (depth == 4 && j % 4 == 3)
                {
                    kind = "computer";
                }

                string nodeId = $"n{id}";
                string label = depth == 0 ? "agdlp.lab" : $"{kind.ToUpperInvariant()}-{id:D4}";
                nodes.Add(new SpikeNode(
                    nodeId, label, kind,
                    Math.Round(radius * Math.Cos(angle), 1),
                    Math.Round(radius * Math.Sin(angle), 1)));

                if (depth > 0)
                {
                    int parentIndex = levelStart[depth - 1] + (int)((long)j * levelCounts[depth - 1] / count);
                    edges.Add(new SpikeEdge($"t{id}", $"n{parentIndex}", nodeId));
                }

                id++;
            }
        }

        // 1,500 random cross edges (membership-style) between depth >= 2 nodes.
        var rng = new Random(42);
        int firstL2 = levelStart[2];
        for (int i = 0; i < 1500; i++)
        {
            int a = rng.Next(firstL2, id);
            int b = rng.Next(firstL2, id);
            if (a == b)
            {
                b = a == id - 1 ? firstL2 : a + 1;
            }

            edges.Add(new SpikeEdge($"x{i}", $"n{a}", $"n{b}"));
        }

        // Collapsed node n42 (an L2 group): 25 hidden children around it.
        const string parentId = "n42";
        var parent = nodes[42];
        var hidden = new List<SpikeNode>(25);
        var hiddenEdges = new List<SpikeEdge>(25);
        for (int i = 0; i < 25; i++)
        {
            double angle = 2 * Math.PI * i / 25;
            hidden.Add(new SpikeNode(
                $"h{i}", $"USR-HID-{i:D2}", "user",
                Math.Round(parent.X + 140 * Math.Cos(angle), 1),
                Math.Round(parent.Y + 140 * Math.Sin(angle), 1)));
            hiddenEdges.Add(new SpikeEdge($"he{i}", parentId, $"h{i}"));
        }

        nodes[42] = parent with { Label = parent.Label + " (collapsed)" };

        return new Dataset
        {
            Nodes = nodes,
            Edges = edges,
            CollapsedParentId = parentId,
            HiddenChildren = hidden,
            HiddenEdges = hiddenEdges,
        };
    }
}

internal sealed class MainWindow : Window
{
    private readonly IClassicDesktopStyleApplicationLifetime _desktop;
    private readonly NativeWebView _webView;
    private readonly TextBlock _detailPanel;
    private readonly TextBlock _statusPanel;
    private readonly List<string> _statusLines = new();
    private readonly List<string> _jsErrors = new();
    private readonly List<string> _resultLines = new();
    private readonly Dictionary<string, TaskCompletionSource<JsonElement>> _waiters = new();
    private readonly string _repoRoot;
    private bool _navSucceeded;
    private string? _userAgent;

    public MainWindow(IClassicDesktopStyleApplicationLifetime desktop)
    {
        _desktop = desktop;
        _repoRoot = FindRepoRoot();
        Title = "GraphSpike - AP 0.1";
        Width = 1400;
        Height = 900;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        _webView = new NativeWebView();
        Grid.SetColumn(_webView, 0);

        _detailPanel = new TextBlock { Text = "(no node selected)", TextWrapping = TextWrapping.Wrap };
        _statusPanel = new TextBlock { Text = "", TextWrapping = TextWrapping.Wrap, FontSize = 11 };

        // Airspace: the WebView is a native child HWND, so the detail panel lives
        // BESIDE it in a separate grid column - never on top of it.
        var rightPanel = new DockPanel { Margin = new Thickness(12) };
        Grid.SetColumn(rightPanel, 1);
        var detailHeader = new TextBlock { Text = "Detail panel", FontWeight = FontWeight.Bold };
        DockPanel.SetDock(detailHeader, Dock.Top);
        DockPanel.SetDock(_detailPanel, Dock.Top);
        var statusHeader = new TextBlock
        {
            Text = "Status",
            FontWeight = FontWeight.Bold,
            Margin = new Thickness(0, 12, 0, 0),
        };
        DockPanel.SetDock(statusHeader, Dock.Top);
        rightPanel.Children.Add(detailHeader);
        rightPanel.Children.Add(_detailPanel);
        rightPanel.Children.Add(statusHeader);
        rightPanel.Children.Add(new ScrollViewer { Content = _statusPanel });

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,340") };
        grid.Children.Add(_webView);
        grid.Children.Add(rightPanel);
        Content = grid;

        _webView.WebMessageReceived += (_, e) =>
        {
            var body = e.Body ?? "";
            if (Dispatcher.UIThread.CheckAccess())
            {
                HandleMessage(body);
            }
            else
            {
                Dispatcher.UIThread.Post(() => HandleMessage(body));
            }
        };
        _webView.NavigationCompleted += (_, e) =>
        {
            _navSucceeded = e.IsSuccess;
            Log($"NavigationCompleted: IsSuccess={e.IsSuccess} Url={e.Request}");
        };

        Opened += async (_, _) => await RunSpikeAsync();
    }

    // ---------- message plumbing ----------

    private void HandleMessage(string body)
    {
        JsonElement msg;
        try
        {
            msg = JsonDocument.Parse(body).RootElement.Clone();
        }
        catch (JsonException ex)
        {
            Log($"Unparseable web message ({ex.Message}): {Truncate(body, 200)}");
            return;
        }

        var type = msg.TryGetProperty("type", out var t) ? t.GetString() ?? "?" : "?";
        if (type == "jsError")
        {
            var text = $"[{msg.GetPropertyOrDefault("source")}] {msg.GetPropertyOrDefault("message")} {msg.GetPropertyOrDefault("where")}";
            _jsErrors.Add(text);
            Log($"JS error captured: {Truncate(text, 200)}");
            return;
        }

        if (_waiters.Remove(type, out var tcs))
        {
            tcs.TrySetResult(msg);
        }
        else
        {
            Log($"Unexpected message: {Truncate(body, 200)}");
        }
    }

    private Task<JsonElement> WaitForAsync(string type, TimeSpan timeout)
    {
        var tcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _waiters[type] = tcs;
        return tcs.Task.WaitAsync(timeout);
    }

    private async Task DispatchAsync(string commandJson) =>
        await _webView.InvokeScript($"window.bridge.dispatch({commandJson})");

    // ---------- the self-driving spike sequence ----------

    private async Task RunSpikeAsync()
    {
        var swTotal = Stopwatch.StartNew();
        bool pass1Render = false, pass2Fps = false, pass3aClick = false,
             pass3bExpand = false, pass4Transfer = false, pass5Errors = false;

        try
        {
            var dataset = Dataset.Build();

            // -- navigate & wait for the bridge --
            var readyTask = WaitForAsync("ready", TimeSpan.FromSeconds(60));
            var indexPath = Path.Combine(AppContext.BaseDirectory, "web", "index.html");
            Log($"Navigating to {indexPath}");
            _webView.Navigate(new Uri(indexPath));
            var ready = await readyTask;
            _userAgent = ready.GetPropertyOrDefault("userAgent");
            Log($"Bridge ready after {swTotal.ElapsedMilliseconds} ms. UA: {_userAgent}");

            // -- criterion 4: ship the dataset, measure transfer+load+initial render --
            // WebResourceRequested in Avalonia.Controls.WebView 11.4.0 exposes only the
            // request (Uri/Method/Headers) - no way to provide a response body - so the
            // fetch()-interception strategy is unworkable; chunked InvokeScript instead.
            var loadedTask = WaitForAsync("loaded", TimeSpan.FromSeconds(120));
            long datasetBytes = 0;
            int chunkCount = 0;
            var swTransfer = Stopwatch.StartNew();
            foreach (var chunk in Chunks(dataset.Nodes, 500))
            {
                var json = JsonSerializer.Serialize(new
                {
                    type = "graphChunk",
                    nodes = chunk.Select(n => new { id = n.Id, label = n.Label, kind = n.Kind, x = n.X, y = n.Y }),
                });
                datasetBytes += Encoding.UTF8.GetByteCount(json);
                chunkCount++;
                await DispatchAsync(json);
            }

            foreach (var chunk in Chunks(dataset.Edges, 1000))
            {
                var json = JsonSerializer.Serialize(new
                {
                    type = "graphChunk",
                    edges = chunk.Select(e => new { id = e.Id, s = e.S, t = e.T }),
                });
                datasetBytes += Encoding.UTF8.GetByteCount(json);
                chunkCount++;
                await DispatchAsync(json);
            }

            var transferOnlyMs = swTransfer.Elapsed.TotalMilliseconds;
            await DispatchAsync("""{"type":"graphCommit"}""");
            var loaded = await loadedTask;
            var transferLoadRenderMs = swTransfer.Elapsed.TotalMilliseconds;

            int nodeCount = loaded.GetProperty("nodeCount").GetInt32();
            int edgeCount = loaded.GetProperty("edgeCount").GetInt32();
            pass1Render = nodeCount == 5000 && edgeCount == 6499;
            pass4Transfer = true;
            Result($"Dataset: {dataset.Nodes.Count} nodes, {dataset.Edges.Count} edges, payload {datasetBytes:N0} bytes in {chunkCount} InvokeScript chunks");
            Result($"Transfer (chunks only): {transferOnlyMs:F0} ms");
            Result($"Transfer + parse + cy init + first render (.NET wall clock): {transferLoadRenderMs:F0} ms");
            Result($"  JS-side: chunk span {loaded.GetProperty("chunkSpanMs").GetDouble():F0} ms, element build {loaded.GetProperty("buildMs").GetDouble():F0} ms, cy init {loaded.GetProperty("cyInitMs").GetDouble():F0} ms, first render {loaded.GetProperty("firstRenderMs").GetDouble():F0} ms");
            Result($"Rendered: {nodeCount} nodes / {edgeCount} edges (expected 5000 / 6499)");
            // ADR-001 data point: cytoscape's webgl renderer hard-crashes without
            // WebGL2, so record whether this environment would even offer it.
            Result($"WebGL2 available in WebView2 (not used; canvas renderer only): {(loaded.TryGetProperty("webgl2", out var webgl2) && webgl2.GetBoolean() ? "yes" : "no")}");

            // -- bridge baseline: ping/pong roundtrips --
            var pings = new List<double>();
            for (int i = 0; i < 5; i++)
            {
                var pongTask = WaitForAsync("pong", TimeSpan.FromSeconds(10));
                var swPing = Stopwatch.StartNew();
                await DispatchAsync($$"""{"type":"ping","seq":{{i}}}""");
                await pongTask;
                pings.Add(swPing.Elapsed.TotalMilliseconds);
            }

            Result($"Bridge ping/pong roundtrip (5x): avg {pings.Average():F1} ms, min {pings.Min():F1} ms, max {pings.Max():F1} ms");

            // -- criterion 2: synthetic pan/zoom FPS for ~3 s, in both renderer configs --
            // (a) textureOnViewport:false = honest full-redraw cost per frame;
            // (b) textureOnViewport:true  = cytoscape's intended interaction mode for
            //     large graphs (blits a cached texture while the viewport moves).
            double avgFpsRaw = 0, avgFpsTex = 0;
            foreach (var texture in new[] { false, true })
            {
                if (texture)
                {
                    var reloadedTask = WaitForAsync("loaded", TimeSpan.FromSeconds(60));
                    await DispatchAsync("""{"type":"reinit","textureOnViewport":true}""");
                    var reloaded = await reloadedTask;
                    Log($"Re-init with textureOnViewport:true: cy init {reloaded.GetProperty("cyInitMs").GetDouble():F0} ms, {reloaded.GetProperty("nodeCount").GetInt32()} nodes");
                }

                var fpsTask = WaitForAsync("fps", TimeSpan.FromSeconds(30));
                await DispatchAsync("""{"type":"measureFps","durationMs":3000}""");
                var fps = await fpsTask;
                double avgFps = fps.GetProperty("avgFps").GetDouble();
                double minFps = fps.GetProperty("minFps").GetDouble();
                if (texture)
                {
                    avgFpsTex = avgFps;
                }
                else
                {
                    avgFpsRaw = avgFps;
                }

                Result($"Pan/zoom FPS (textureOnViewport:{texture.ToString().ToLowerInvariant()}) over {fps.GetProperty("durationMs").GetDouble():F0} ms ({fps.GetProperty("frames").GetInt32()} frames): avg {avgFps:F1}, min {minFps:F1} (worst frame {fps.GetProperty("maxFrameMs").GetDouble():F0} ms)");
            }

            // Human-gesture path (synthetic DOM mouse-drag + wheel on the container):
            // the only path where cytoscape's hideEdgesOnViewport/textureOnViewport
            // optimizations actually engage (verified in cytoscape 3.34.0 source).
            // Run on the textureOnViewport:true instance = production config.
            var gestureTask = WaitForAsync("gestureFps", TimeSpan.FromSeconds(30));
            await DispatchAsync("""{"type":"measureGestureFps","durationMs":3000}""");
            var gesture = await gestureTask;
            double gestureAvg = gesture.GetProperty("avgFps").GetDouble();
            double gestureMin = gesture.GetProperty("minFps").GetDouble();
            Result($"Pan/zoom FPS (human-gesture path: DOM drag + wheel, textureOnViewport:true) over {gesture.GetProperty("durationMs").GetDouble():F0} ms ({gesture.GetProperty("frames").GetInt32()} frames): avg {gestureAvg:F1}, min {gestureMin:F1} (worst frame {gesture.GetProperty("maxFrameMs").GetDouble():F0} ms)");

            pass2Fps = avgFpsRaw >= 10 || avgFpsTex >= 10 || gestureAvg >= 10;

            // -- criterion 3a: JS -> .NET node click --
            var clickTask = WaitForAsync("nodeClick", TimeSpan.FromSeconds(10));
            var swClick = Stopwatch.StartNew();
            await DispatchAsync($$"""{"type":"clickTest","id":"{{dataset.CollapsedParentId}}"}""");
            var click = await clickTask;
            var clickMs = swClick.Elapsed.TotalMilliseconds;
            var clickedId = click.GetPropertyOrDefault("id");
            _detailPanel.Text = $"Node: {clickedId}\nLabel: {click.GetPropertyOrDefault("label")}\nKind: {click.GetPropertyOrDefault("kind")}\nClick latency: {clickMs:F1} ms";
            pass3aClick = clickedId == dataset.CollapsedParentId;
            Result($"Node click (synthetic tap on {dataset.CollapsedParentId}) -> .NET received in {clickMs:F1} ms; detail panel updated");

            // -- criterion 3b: .NET -> JS expand command --
            var expandedTask = WaitForAsync("expanded", TimeSpan.FromSeconds(10));
            var expandJson = JsonSerializer.Serialize(new
            {
                type = "expand",
                parentId = dataset.CollapsedParentId,
                children = dataset.HiddenChildren.Select(n => new { id = n.Id, label = n.Label, kind = n.Kind, x = n.X, y = n.Y }),
                edges = dataset.HiddenEdges.Select(e => new { id = e.Id, s = e.S, t = e.T }),
            });
            var swExpand = Stopwatch.StartNew();
            await DispatchAsync(expandJson);
            var expanded = await expandedTask;
            var expandMs = swExpand.Elapsed.TotalMilliseconds;
            int expandedNodes = expanded.GetProperty("nodeCount").GetInt32();
            pass3bExpand = expandedNodes == 5025;
            Result($"Expand {dataset.CollapsedParentId} (+25 children): confirmed in {expandMs:F1} ms, node count now {expandedNodes} (expected 5025), edges {expanded.GetProperty("edgeCount").GetInt32()}, JS add {expanded.GetProperty("addMs").GetDouble():F1} ms");

            // -- criterion 5: JS error reaches the .NET log --
            // The page is served from file://, so Chromium treats each script as an
            // opaque origin and mutes window.onerror details to "Script error." -
            // the routing is proven by a NEW window.onerror entry arriving after the
            // trigger (nothing else throws at this point in the sequence).
            int errorsBefore = _jsErrors.Count;
            await DispatchAsync("""{"type":"triggerError"}""");
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
            while (DateTime.UtcNow < deadline && !_jsErrors.Skip(errorsBefore).Any(x => x.Contains("[window.onerror]")))
            {
                await Task.Delay(100);
            }

            pass5Errors = _jsErrors.Skip(errorsBefore).Any(x => x.Contains("[window.onerror]"));
            Result($"JS error capture: deliberate window.onerror {(pass5Errors ? "arrived in .NET log (message muted to 'Script error.' by file:// opaque-origin policy)" : "DID NOT arrive within 10 s")}; total JS errors this run: {_jsErrors.Count}");

            // Zoom onto the expanded neighborhood so the screenshot shows visible
            // nodes/edges/labels (at full-fit zoom the 5k nodes are sub-pixel).
            var focusedTask = WaitForAsync("focused", TimeSpan.FromSeconds(10));
            var focusIds = dataset.HiddenChildren.Select(n => n.Id).Prepend(dataset.CollapsedParentId);
            await DispatchAsync(JsonSerializer.Serialize(new { type = "focus", ids = focusIds }));
            await focusedTask;
            await Task.Delay(400); // let the final frame + detail panel paint before the screenshot
        }
        catch (Exception ex)
        {
            Result($"FATAL: {ex.GetType().Name}: {ex.Message}");
            Log(ex.ToString());
        }

        TryScreenshot(Path.Combine(_repoRoot, "artifacts", "ui", "graphspike.png"));

        var summary = new[]
        {
            (name: "1. Render 5,000 nodes + 6,499 edges in Avalonia-hosted WebView", ok: pass1Render),
            (name: "2. Pan/zoom usable (avg FPS >= 10 in at least one measured interaction path)", ok: pass2Fps),
            (name: "3a. JS -> .NET node click + detail panel + latency", ok: pass3aClick),
            (name: "3b. .NET -> JS expand command + confirmation (5,025 nodes)", ok: pass3bExpand),
            (name: "4. Dataset transfer + load + initial render measured", ok: pass4Transfer),
            (name: "5. JS console/window.onerror routed into .NET log", ok: pass5Errors),
        };
        bool allPass = summary.All(s => s.ok);
        Result("");
        foreach (var (name, ok) in summary)
        {
            Result($"{(ok ? "PASS" : "FAIL")}  {name}");
        }

        Result($"OVERALL: {(allPass ? "PASS" : "FAIL")} (total spike runtime {swTotal.Elapsed.TotalSeconds:F1} s)");
        WriteResults(allPass);
        _desktop.Shutdown(allPass ? 0 : 1);
    }

    // ---------- reporting ----------

    private void Result(string line)
    {
        _resultLines.Add(line);
        Log(line);
    }

    private void Log(string line)
    {
        Console.WriteLine(line);
        _statusLines.Add(line);
        if (_statusLines.Count > 28)
        {
            _statusLines.RemoveAt(0);
        }

        _statusPanel.Text = string.Join('\n', _statusLines);
    }

    private void WriteResults(bool allPass)
    {
        try
        {
            var path = Path.Combine(_repoRoot, "spikes", "GraphSpike", "RESULTS.md");
            var sb = new StringBuilder();
            sb.AppendLine("# GraphSpike results (AP 0.1, Phase 0)");
            sb.AppendLine();
            sb.AppendLine($"Run: {DateTime.Now:yyyy-MM-dd HH:mm:ss} (local). Overall: **{(allPass ? "PASS" : "FAIL")}**.");
            sb.AppendLine();
            sb.AppendLine("## Machine context");
            sb.AppendLine();
            sb.AppendLine($"- OS: {RuntimeInformation.OSDescription} ({RuntimeInformation.OSArchitecture})");
            sb.AppendLine($"- CPU: {Environment.ProcessorCount} logical processors; GPU: Intel UHD Graphics 620, driver 31.0.101.2141 installed 2026-06-11 mid-spike (pre-driver software-rendered baseline: RESULTS-software-rendering.md)");
            sb.AppendLine($"- .NET: {RuntimeInformation.FrameworkDescription}");
            sb.AppendLine($"- Stack: Avalonia 11.3.17 + Avalonia.Controls.WebView 11.4.0 (WebView2 backend) + cytoscape 3.34.0 (canvas renderer, preset layout, haystack edges, no arrows, pixelRatio 1, min-zoomed-font-size 12, hideEdgesOnViewport true, textureOnViewport measured both off and on)");
            sb.AppendLine($"- WebView UA: {_userAgent}");
            sb.AppendLine();
            sb.AppendLine("## Measurements (verbatim run output)");
            sb.AppendLine();
            sb.AppendLine("```");
            foreach (var line in _resultLines)
            {
                sb.AppendLine(line);
            }

            sb.AppendLine("```");
            sb.AppendLine();
            sb.AppendLine("## Findings");
            sb.AppendLine();
            sb.AppendLine("- `WebResourceRequested` in Avalonia.Controls.WebView 11.4.0 exposes only the request (`Uri`, `Method`, `Headers` - headers mutable via `TrySet`/`TryRemove`); there is **no response-providing API**, so serving `graph.json` via interception is unworkable. Fallback used: chunked `InvokeScript` batches (500 nodes / 1,000 edges per call) - see numbers above.");
            sb.AppendLine($"- Navigation: file:// URL of the on-disk bundle; NavigationCompleted IsSuccess={_navSucceeded}. `invokeCSharpAction` is injected by the host; the bridge queues outbound messages until it appears (needed in practice - see bridge.js).");
            sb.AppendLine("- Avalonia's `NativeControlHost` (which hosts the WebView2 child HWND) throws `Unable to create child window for native control host` unless the exe carries an app.manifest with a `<supportedOS>` Windows 10 entry - added `app.manifest` to fix.");
            sb.AppendLine("- file:// pages put every script in an opaque origin, so `window.onerror` details are muted to `Script error.` - the error still reaches .NET through the bridge, but messages are useless. The product should serve its bundle from a non-opaque origin (e.g. NavigateToString with baseUri, a custom scheme, or a loopback listener) to get real stack traces.");
            sb.AppendLine("- **Benchmarking pitfall (key ADR-001 input):** cytoscape's `hideEdgesOnViewport`/`textureOnViewport` only engage while its *input handlers* set gesture flags (`pinching || hoverData.dragging || swipePanning || data.wheelZooming`; hideEdges also accepts `cy.animated()`) - verified in the 3.34.0 source. A programmatic `cy.zoom()`/`cy.pan()`/`cy.viewport()` loop therefore measures the *full-redraw* cost and bypasses both optimizations, which is why textureOnViewport made no difference in the synthetic loop. FPS was measured three ways: (a) `cy.viewport()` loop with textureOnViewport:false, (b) same loop with textureOnViewport:true (expected ~equal to (a) per the above), (c) synthetic DOM mouse-drag + wheel events on the container - the literal human input path, where both optimizations engage. The PASS/FAIL verdict for 'interaction stays usable' is based on the best measured path; all numbers reported.");
            sb.AppendLine("- Cosmetic: Chromium logs `Failed to unregister class Chrome_WidgetWin_0` during WebView2 teardown at shutdown; harmless, after results are written.");
            if (_jsErrors.Count > 0)
            {
                sb.AppendLine($"- JS errors captured during the run ({_jsErrors.Count}):");
                foreach (var err in _jsErrors)
                {
                    sb.AppendLine($"  - {Truncate(err, 300)}");
                }
            }

            File.WriteAllText(path, sb.ToString());
            Console.WriteLine($"Results written to {path}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to write RESULTS.md: {ex}");
        }
    }

    private void TryScreenshot(string path)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var scale = RenderScaling;
            var size = FrameSize ?? ClientSize;
            int w = (int)Math.Ceiling(size.Width * scale);
            int h = (int)Math.Ceiling(size.Height * scale);
            using var bmp = new System.Drawing.Bitmap(w, h);
            using var g = System.Drawing.Graphics.FromImage(bmp);
            g.CopyFromScreen(Position.X, Position.Y, 0, 0, new System.Drawing.Size(w, h));
            bmp.Save(path, System.Drawing.Imaging.ImageFormat.Png);
            Log($"Screenshot saved to {path}");
        }
        catch (Exception ex)
        {
            Log($"Screenshot failed (non-fatal, continuing): {ex.Message}");
        }
    }

    // ---------- helpers ----------

    private static IEnumerable<List<T>> Chunks<T>(List<T> source, int size)
    {
        for (int i = 0; i < source.Count; i += size)
        {
            yield return source.GetRange(i, Math.Min(size, source.Count - i));
        }
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, ".git")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? Environment.CurrentDirectory;
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";
}

internal static class JsonExtensions
{
    public static string GetPropertyOrDefault(this JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var v)
            ? v.ToString()
            : "";
}
