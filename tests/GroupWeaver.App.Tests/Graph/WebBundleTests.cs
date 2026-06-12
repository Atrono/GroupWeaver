using System.Security.Cryptography;

using Xunit;

namespace GroupWeaver.App.Tests.Graph;

/// <summary>
/// Pins the shipped production web bundle (AP 2.2 S3, ADR-004): the four files the
/// <c>CytoscapeGraphRenderer</c> navigates to must land next to the App binary — and
/// therefore, via this project's ProjectReference and transitive Content copying, next
/// to this test assembly. Plain file/text assertions, no Avalonia: the bundle is static
/// content, and these tripwires guard the contracts proven in the GraphSpike —
/// <c>bridge.js</c> (byte-identical: the <c>__bridgeSendShim</c> seam + async-injection
/// queue) and vendored Cytoscape 3.34.0 (SHA256-identical: no drift), plus regression
/// tripwires on <c>graph.js</c>/<c>index.html</c> (selector-concatenation bug, spike-only
/// harness code, wire-protocol messages, palette parity with
/// <c>AdObjectKindConverters</c>, label zoom threshold, script order).
/// </summary>
public sealed class WebBundleTests
{
    /// <summary>
    /// Node palette per kind — must stay in lockstep with the C# brushes in
    /// <c>src/App/Views/AdObjectKindConverters.cs</c> (User teal, GG green, DL rust,
    /// UG purple, OU blue, Computer slate, External gray).
    /// </summary>
    private static readonly string[] PaletteHexes =
    [
        "#038387", "#107C10", "#A14000", "#744DA9", "#0F6CBD", "#556070", "#757575",
    ];

    // --- 1. Bundle is copied to the output directory ---------------------------------

    [Theory]
    [InlineData("index.html")]
    [InlineData("bridge.js")]
    [InlineData("graph.js")]
    [InlineData("vendor/cytoscape.min.js")]
    public void Bundle_FileIsCopiedToOutputDirectory(string relativePath)
    {
        var path = ShippedWebPath(relativePath.Split('/'));
        Assert.True(
            File.Exists(path),
            $"'{path}' not found — src/App must ship web/{relativePath} as a Content item "
            + "with CopyToOutputDirectory so it flows through the ProjectReference.");
    }

    // --- 2./3. Verbatim copies from the spike (proven contracts, no drift) -----------

    [Fact]
    public void Vendor_CytoscapeIsSha256IdenticalToSpike()
    {
        var shipped = RequireShipped("vendor", "cytoscape.min.js");
        var spike = SpikeWebPath("vendor", "cytoscape.min.js");

        Assert.Equal(Sha256Hex(spike), Sha256Hex(shipped));
    }

    [Fact]
    public void Bridge_IsByteIdenticalToSpike()
    {
        var shipped = RequireShipped("bridge.js");
        var spike = SpikeWebPath("bridge.js");

        // Byte comparison is safe: both copies live in this repo under the same
        // `* text=auto` normalization, so line endings cannot legitimately differ.
        Assert.Equal(File.ReadAllBytes(spike), File.ReadAllBytes(shipped));
    }

    // --- 4. graph.js regression tripwires ---------------------------------------------

    [Fact]
    public void Graph_LooksUpNodesById_NeverBySelectorConcatenation()
    {
        var text = ReadShippedText("graph.js");

        // ADR-004 D5: cy.getElementById ONLY — cy.$('#'+dn) silently matches nothing
        // for every comma-containing DN.
        Assert.Contains("getElementById", text, StringComparison.Ordinal);
        Assert.DoesNotMatch(@"[""']#[""']\s*\+", text);
    }

    [Fact]
    public void Graph_DoesNotShipSpikeHarnessCode()
    {
        var text = ReadShippedText("graph.js");

        // measureFps / measureGestureFps / triggerError are GraphSpike perf-harness
        // commands; none of them may reach the production bundle.
        Assert.DoesNotContain("measureFps", text, StringComparison.Ordinal);
        Assert.DoesNotContain("measureGestureFps", text, StringComparison.Ordinal);
        Assert.DoesNotContain("triggerError", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Graph_SpeaksTheWireProtocol()
    {
        var text = ReadShippedText("graph.js");

        // ADR-004 D4/D5: chunked ingest plus the two node interaction events.
        Assert.Contains("graphChunk", text, StringComparison.Ordinal);
        Assert.Contains("graphCommit", text, StringComparison.Ordinal);
        Assert.Contains("nodeClick", text, StringComparison.Ordinal);
        Assert.Contains("nodeExpand", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Graph_PaletteMatchesAdObjectKindConverters()
    {
        var text = ReadShippedText("graph.js");

        foreach (var hex in PaletteHexes)
        {
            Assert.True(
                text.Contains(hex, StringComparison.OrdinalIgnoreCase),
                $"graph.js is missing palette color '{hex}' — node colors must stay in "
                + "lockstep with AdObjectKindConverters.");
        }
    }

    [Fact]
    public void Graph_HidesLabelsAtFitZoom()
    {
        var text = ReadShippedText("graph.js");

        // ADR-004 consequence: labels appear only when zoomed in.
        Assert.Contains("min-zoomed-font-size", text, StringComparison.Ordinal);
    }

    // --- 5. index.html structure -------------------------------------------------------

    [Fact]
    public void Index_HasCytoscapeContainerDiv()
    {
        var text = ReadShippedText("index.html");

        Assert.Matches(@"(?i)<div\b[^>]*\bid\s*=\s*[""']cy[""']", text);
    }

    [Fact]
    public void Index_ReferencesBridgeBeforeGraph()
    {
        var text = ReadShippedText("index.html");

        var bridgeAt = text.IndexOf("bridge.js", StringComparison.OrdinalIgnoreCase);
        var graphAt = text.IndexOf("graph.js", StringComparison.OrdinalIgnoreCase);

        Assert.True(bridgeAt >= 0, "index.html does not reference bridge.js.");
        Assert.True(graphAt >= 0, "index.html does not reference graph.js.");
        Assert.True(
            bridgeAt < graphAt,
            "bridge.js must load BEFORE graph.js — graph.js uses window.bridge at load time.");
    }

    // --- helpers -------------------------------------------------------------------------

    /// <summary>Path of a shipped bundle file under the test (= App) output directory.</summary>
    private static string ShippedWebPath(params string[] segments)
        => Path.Combine([AppContext.BaseDirectory, "web", .. segments]);

    /// <summary>
    /// Path of a GraphSpike bundle file, located relative to the repo root by walking up
    /// from the test output directory to <c>GroupWeaver.sln</c> (suite idiom, see
    /// <c>AppCliTests.FindAppBinary</c>). The spike files are committed sources, so a
    /// missing one is a hard failure, not a skip.
    /// </summary>
    private static string SpikeWebPath(params string[] segments)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "GroupWeaver.sln")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        var path = Path.Combine([dir.FullName, "spikes", "GraphSpike", "web", .. segments]);
        Assert.True(File.Exists(path), $"spike reference file '{path}' not found.");
        return path;
    }

    /// <summary>Shipped bundle path with a clear missing-file message instead of an IO exception.</summary>
    private static string RequireShipped(params string[] segments)
    {
        var path = ShippedWebPath(segments);
        Assert.True(
            File.Exists(path),
            $"'{path}' not found — src/App must ship it as Content with CopyToOutputDirectory.");
        return path;
    }

    private static string ReadShippedText(params string[] segments)
        => File.ReadAllText(RequireShipped(segments));

    private static string Sha256Hex(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }
}
