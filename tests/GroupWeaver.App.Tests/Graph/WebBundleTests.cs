using System.Security.Cryptography;

using GroupWeaver.App.Views;

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
    public void Vendor_CytoscapeMatchesRecordedUpstreamSha256()
    {
        // Supply-chain provenance (#52): the vendored bundle must stay byte-identical to
        // the official npm distribution cytoscape@3.34.0/dist/cytoscape.min.js, whose
        // SHA256 (raw, LF) is recorded in THIRD-PARTY-NOTICES.md. The file is marked
        // `-text` in .gitattributes so it is checked out byte-for-byte (no CRLF rewrite),
        // making this an INDEPENDENT upstream check, not a self-referential in-repo compare.
        const string upstreamSha256 =
            "9c2a3bf2592e0b14a1f7bec07c03a54f16dedf32af9cd0af155c716aa6c87bc3";

        var shipped = RequireShipped("vendor", "cytoscape.min.js");

        Assert.Equal(upstreamSha256, Sha256Hex(shipped), ignoreCase: true);
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

        // ADR-005 D1/D2 wire delta (AP 2.3): replace-in-place commit verb, focus
        // command, focused confirmation. 'focus' is quoted because the bare word
        // is a substring of 'focused' and would match trivially.
        Assert.Contains("graphUpdate", text, StringComparison.Ordinal);
        Assert.Contains("'focus'", text, StringComparison.Ordinal);
        Assert.Contains("focused", text, StringComparison.Ordinal);

        // ADR-019 (#94): the in-canvas busy-ring command. Single-quoted to match the
        // `case 'busy':` literal so the case can't silently vanish from the bundle.
        Assert.Contains("'busy'", text, StringComparison.Ordinal);

        // ADR-020 (#96): the reverse sidebar->graph selection-sync command. Single-quoted
        // to match the `case 'select':` literal so the case can't silently vanish.
        Assert.Contains("'select'", text, StringComparison.Ordinal);
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
    public void Graph_LiftsLowContrastKindFillsWithABorder()
    {
        var text = ReadShippedText("graph.js");

        // ADR-021 / WCAG 1.4.11 (#90): the three kind FILLS whose graphical-object
        // contrast vs the #1b1f27 page bg falls below the 3:1 floor (DL 2.55:1 /
        // UG 2.66:1 / Computer 2.59:1) are LIFTED by a 2px ring — color pinned to
        // BrandTokens.NodeLiftRing (#8A93A3, 5.33:1). The fills are UNCHANGED (the
        // PaletteHexes containment above still holds).
        //
        // ADR-026 WP1b moved the literal value into the THEME table: the per-kind
        // rules now read 'border-color': t.nodeLiftRing / 'border-width': t.nodeLiftWidth,
        // and the #8A93A3 ring hex lives as THEME.dark.nodeLiftRing. The ring is the DARK
        // 1.4.11 lift (width 2); on the LIGHT canvas the three fills already clear 3:1
        // (DL 5.98 / UG 5.75 / Computer 5.90) so the ring is dropped (nodeLiftWidth 0).
        // Pin the value via the THEME-table form (the new single source) plus the per-
        // kind binding, so a drift in either the value, the quoting, or the binding
        // fails here. The intent is unchanged: DL/UG/Computer are lifted on dark.
        Assert.Contains("nodeLiftRing: '#8A93A3'", text, StringComparison.Ordinal);
        Assert.Contains("'border-color': t.nodeLiftRing", text, StringComparison.Ordinal);
        Assert.Contains("'border-width': t.nodeLiftWidth", text, StringComparison.Ordinal);

        // The DARK lift width is 2 (the ring shows); the LIGHT lift width is 0 (no ring
        // needed — fills clear 3:1 on the light canvas). Both pinned so a flip of either
        // theme's nodeLiftWidth is caught.
        Assert.Contains("nodeLiftWidth: 2", text, StringComparison.Ordinal);
        Assert.Contains("nodeLiftWidth: 0", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// ADR-026 D5 / WP1b light-canvas parity: the page-relative graph tokens re-tone for the
    /// light theme, and <see cref="BrandTokens"/> carries the documented C# mirror of the light
    /// values graph.js owns in its <c>THEME.light</c> table (the wire carries only the variant
    /// string — no token values cross the bridge). This is the C# end of the hand-mirror parity
    /// chain for the LIGHT palette, the exact analogue of
    /// <c>AdObjectKindConvertersTests</c> for the dark fills: each <c>BrandTokens.Graph*LightHex</c>
    /// constant must appear inside graph.js's <c>THEME.light</c> table, so a drift between the C#
    /// source of truth and the JS mirror surfaces here. Scoped to the <c>THEME.light { … }</c>
    /// slice (never the whole file) so a value that exists only in <c>THEME.dark</c> / <c>CHROME</c>
    /// can never satisfy a light assertion.
    /// </summary>
    [Fact]
    public void Graph_LightThemeTableMatchesBrandTokens()
    {
        var lightTable = ThemeLightTable(ReadShippedText("graph.js"));

        // Every BrandTokens.Graph*LightHex role (ADR-026 D5) — the C# source of truth — must be
        // present in graph.js's THEME.light table. Compared against the actual token constants
        // (not a re-hardcoded copy) so the parity is transparent: a change to either side fails.
        string[] lightHexes =
        [
            BrandTokens.GraphCanvasLightHex,        // #F5F6F8 canvas / label outline
            BrandTokens.GraphLabelInkLightHex,      // #1C2127 node label ink
            BrandTokens.GraphEdgeMemberLightHex,    // #5A6473 membership edge
            BrandTokens.GraphEdgeContainsLightHex,  // #3A424E containment edge
            BrandTokens.GraphRootBorderLightHex,    // #1C2127 root border
            BrandTokens.GraphExternalBorderLightHex, // #6B7480 External dashed border
            BrandTokens.GraphSelectionBorderLightHex, // #1C2127 node:selected border
            BrandTokens.GraphSeverityErrorLightHex,   // #D63A4A severity error halo
            BrandTokens.GraphSeverityWarningLightHex, // #BD7C00 severity warning halo
            BrandTokens.GraphSeverityInfoLightHex,    // #2F6FE0 severity info halo + busy
            BrandTokens.GraphDiffAddedLightHex,       // #1F9D57 diff added
            BrandTokens.GraphDiffRemovedLightHex,     // #D63A4A diff removed
            BrandTokens.GraphDiffUncheckedLightHex,   // #5A6473 diff unchecked
        ];
        foreach (var hex in lightHexes)
        {
            Assert.True(
                lightTable.Contains(hex, StringComparison.OrdinalIgnoreCase),
                $"graph.js THEME.light is missing light token '{hex}' — the light graph palette "
                + "must stay in lockstep with BrandTokens.Graph*LightHex (ADR-026 D5 hand-mirror).");
        }

        // The light canvas drops the DL/UG/Computer 1.4.11 border-lift (fills clear 3:1 on
        // light): THEME.light.nodeLiftWidth is 0 (ADR-026 D5). The 2px lift is DARK-only.
        Assert.Contains("nodeLiftWidth: 0", lightTable, StringComparison.Ordinal);
    }

    /// <summary>
    /// ADR-026 D1 / WP1b: the DARK graph tokens are byte-identical to the shipped palette (every
    /// ADR-021 WCAG ratio holds). Re-confirm the dark literals are unchanged after the THEME-table
    /// refactor — both the kind fills (PaletteHexes, theme-invariant) and the page-relative dark
    /// roles that ADR-026 D5 leaves untouched on the dark side.
    /// </summary>
    [Fact]
    public void Graph_DarkThemeTableLiteralsUnchanged()
    {
        var darkTable = ThemeDarkTable(ReadShippedText("graph.js"));

        // The page-relative DARK role tokens (ADR-026 D5 "Dark (unchanged)" column). canvas/
        // label-ink/label-outline/edges/borders + the node-lift ring (2px, DL/UG/Computer) and
        // the severity/diff/busy hues, all byte-identical to the pre-WP1b bundle.
        string[] darkRoleHexes =
        [
            "#1b1f27",  // canvasBg / labelOutline (PageBackground)
            "#E8ECF2",  // labelInk + rootBorder
            BrandTokens.NodeLiftRingHex, // #8A93A3 dark 1.4.11 lift ring
            "#B0B6BF",  // externalBorder
            "#FFFFFF",  // selectionBorder (white, both themes)
            "#8E9BB4",  // edgeMember
            "#6B788F",  // edgeContains
            BrandTokens.ErrorHex,    // #D13438 severity error
            BrandTokens.WarningHex,  // #F7A30B severity warning
            BrandTokens.InfoHex,     // #4FA3E3 severity info + busy
            BrandTokens.AddedHex,    // #2FAE4E diff added
            BrandTokens.RemovedHex,  // #E0503A diff removed
            BrandTokens.UncheckedHex, // #8A8F98 diff unchecked
        ];
        foreach (var hex in darkRoleHexes)
        {
            Assert.True(
                darkTable.Contains(hex, StringComparison.OrdinalIgnoreCase),
                $"graph.js THEME.dark is missing dark token '{hex}' — the dark palette must stay "
                + "byte-identical to the shipped values (ADR-026 D1: dark is provably unchanged).");
        }

        // The dark lift width is 2 (the ring shows on DL/UG/Computer).
        Assert.Contains("nodeLiftWidth: 2", darkTable, StringComparison.Ordinal);
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

    /// <summary>
    /// Slice of graph.js's <c>THEME</c> variant table for <paramref name="variant"/>
    /// (<c>dark</c> or <c>light</c>): from the <c>&lt;variant&gt;: {</c> opener to its matching
    /// closing brace. Used by the palette-parity asserts so a value is checked SCOPED to the
    /// intended variant — a hex that lives only in the OTHER variant (or in CHROME) can never
    /// satisfy the assertion. A structural failure to locate the table fails loudly here.
    /// </summary>
    private static string ThemeTable(string graphJs, string variant)
    {
        // THEME holds exactly two top-level keys; each opens `<variant>: {`. Locate the opener,
        // then brace-match to its close so a sibling variant's table is never included.
        var opener = $"{variant}: {{";
        var start = graphJs.IndexOf(opener, StringComparison.Ordinal);
        Assert.True(start >= 0, $"graph.js does not contain a THEME.{variant} table opener '{opener}'.");

        var braceStart = start + opener.Length - 1; // index of the '{'
        var depth = 0;
        for (var i = braceStart; i < graphJs.Length; i++)
        {
            if (graphJs[i] == '{')
            {
                depth++;
            }
            else if (graphJs[i] == '}')
            {
                depth--;
                if (depth == 0)
                {
                    return graphJs.Substring(braceStart, i - braceStart + 1);
                }
            }
        }

        Assert.Fail($"graph.js THEME.{variant} table has no matching closing brace.");
        return string.Empty; // unreachable (Assert.Fail throws)
    }

    private static string ThemeLightTable(string graphJs) => ThemeTable(graphJs, "light");

    private static string ThemeDarkTable(string graphJs) => ThemeTable(graphJs, "dark");

    private static string Sha256Hex(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }
}
