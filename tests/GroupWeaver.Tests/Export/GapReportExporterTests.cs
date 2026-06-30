using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

using GroupWeaver.Core.Diff;
using GroupWeaver.Core.Export;

using Xunit;

namespace GroupWeaver.Tests.Export;

/// <summary>
/// Pins <c>GapReportExporter.ToCsv(report, summary, resolveName)</c> and
/// <c>ToHtml(report, summary, resolveName, header)</c> — the Plan-vs-Ist gap-diff serializers
/// (ADR-015 / #66), the Gap-mode counterpart of <see cref="ViolationReportExporter"/>. The
/// VM wiring around them is pinned by <c>tests/GroupWeaver.App.Tests/GapModeTests.cs</c>;
/// these tests pin the pure-Core serializers themselves.
///
/// <para>Binding contract (from the exporter doc + ADR-015 D5/D6):</para>
/// <list type="bullet">
///   <item><b>CSV header verbatim:</b> <c>Kind,SubjectName,PrimaryDn,SecondaryDn,Message</c>.</item>
///   <item>Rows iterate <c>report.Findings</c> in canonical block order VERBATIM (never
///         re-sorted): <c>Kind</c> = the <see cref="GapKind"/> NAME; <c>SubjectName</c> =
///         <c>resolveName(Dns[0])</c>; <c>PrimaryDn</c> = <c>Dns[0]</c>; <c>SecondaryDn</c> =
///         <c>Dns[1]</c> for EDGE findings (Edge{Added,Removed}) else EMPTY; <c>Message</c>
///         verbatim.</item>
///   <item><b>Two-layer cell encoding, GUARD BEFORE QUOTE (pinned order):</b> a cell leading
///         with <c>= + - @ \t \r \n</c> gets a single leading <c>'</c>; a cell containing
///         <c>" , \r \n</c> is RFC-4180 wrapped with embedded <c>"</c> doubled — so the
///         <c>'</c> lands INSIDE the quotes.</item>
///   <item><b>HTML:</b> self-contained (no <c>http</c>/<c>src=</c>/<c>href=</c>); every
///         untrusted token escaped via <c>WebUtility.HtmlEncode</c> and emitted ONLY in
///         element text; the <see cref="GapSummary"/> honesty counts (added/removed nodes &amp;
///         edges + unchecked areas) render; deterministic for a fixed injected
///         <see cref="DiffReportHeader.GeneratedAt"/>; a zero-finding report shows an all-clear
///         line and NO table rows.</item>
/// </list>
///
/// Findings/summaries are HAND-BUILT (serializer mechanics, not diff semantics — they need not
/// be directory-consistent), mirroring the <see cref="ViolationReportExporter"/> tests. CSV
/// structure that an embedded CRLF would make ambiguous is asserted via an RFC-4180 re-parse.
/// </summary>
public class GapReportExporterTests
{
    private const string Eol = "\r\n";
    private const string CsvHeader = "Kind,SubjectName,PrimaryDn,SecondaryDn,Message";

    private const string ParentDn = "CN=DL_FileShare_RW,OU=Groups,OU=AGDLP-Demo,DC=weavedemo,DC=example";
    private const string ChildDn = "CN=GG_FileShare_Members,OU=Groups,OU=AGDLP-Demo,DC=weavedemo,DC=example";
    private const string AddedDn = "CN=GG_Sales_EU,OU=Groups,OU=AGDLP-Demo,DC=weavedemo,DC=example";
    private const string RemovedDn = "CN=GG_LegacyTeam,OU=Groups,OU=AGDLP-Demo,DC=weavedemo,DC=example";
    private const string AreaDn = "CN=DL_Unexpanded,OU=Groups,OU=AGDLP-Demo,DC=weavedemo,DC=example";

    // dn -> friendly name; falls back to the DN itself for unknown DNs, exactly as the VM
    // closure does (Snapshot.TryGetObject(dn, out o) ? o.Name : dn).
    private static readonly ViolationReportExporter.ResolveName Names = dn => dn switch
    {
        ParentDn => "DL_FileShare_RW",
        ChildDn => "GG_FileShare_Members",
        AddedDn => "GG_Sales_EU",
        RemovedDn => "GG_LegacyTeam",
        AreaDn => "DL_Unexpanded",
        _ => dn,
    };

    // A fixed instant so the HTML is deterministic (no ambient clock).
    private static readonly DateTimeOffset FixedGeneratedAt =
        new(2026, 6, 30, 9, 30, 0, TimeSpan.FromHours(2));

    private static readonly DiffReportHeader Header = new(
        RootDn: "OU=AGDLP-Demo,DC=weavedemo,DC=example",
        RootName: "AGDLP-Demo",
        ConnectionSummary: "Gap analysis: plan compared against the current structure",
        GeneratedAt: FixedGeneratedAt);

    // === CSV: header + the SecondaryDn column rule (empty for node/area, child DN for edge) ===

    [Fact]
    public void Csv_Header_IsTheFixedColumnRow()
    {
        var csv = GapReportExporter.ToCsv(GapReport.Empty, EmptySummary(), Names);

        Assert.StartsWith(CsvHeader + Eol, csv);
    }

    [Fact]
    public void Csv_EmptyReport_IsHeaderOnly()
    {
        // No findings -> the file is the header row only (the summary is unused for CSV).
        var csv = GapReportExporter.ToCsv(GapReport.Empty, EmptySummary(), Names);

        Assert.Equal(CsvHeader + Eol, csv);
    }

    [Fact]
    public void Csv_NodeAddedRow_SecondaryDnEmpty_AllColumnsByKindNameAndResolvedSubject()
    {
        // A node finding: Dns = [subject]; SecondaryDn must be EMPTY; Kind cell = "NodeAdded";
        // SubjectName = resolveName(subject); Message verbatim.
        var report = new GapReport(new[]
        {
            new GapFinding(GapKind.NodeAdded, new[] { AddedDn }, "Object 'GG_Sales_EU' added."),
        });

        var row = Assert.Single(DataRows(GapReportExporter.ToCsv(report, EmptySummary(), Names)));

        Assert.Equal(
            new[]
            {
                "NodeAdded",       // Kind = the GapKind NAME
                "GG_Sales_EU",     // SubjectName via the resolver on Dns[0]
                AddedDn,           // PrimaryDn = Dns[0]
                string.Empty,      // SecondaryDn EMPTY for a node finding
                "Object 'GG_Sales_EU' added.", // Message verbatim
            },
            row);
    }

    [Fact]
    public void Csv_NodeRemovedRow_SecondaryDnEmpty()
    {
        var report = new GapReport(new[]
        {
            new GapFinding(GapKind.NodeRemoved, new[] { RemovedDn }, "Object 'GG_LegacyTeam' removed."),
        });

        var row = Assert.Single(DataRows(GapReportExporter.ToCsv(report, EmptySummary(), Names)));

        Assert.Equal("NodeRemoved", row[0]);
        Assert.Equal("GG_LegacyTeam", row[1]);
        Assert.Equal(RemovedDn, row[2]);
        Assert.Equal(string.Empty, row[3]); // node finding -> no secondary DN
    }

    [Fact]
    public void Csv_UnverifiableAreaRow_SecondaryDnEmpty()
    {
        // The honest load-state finding (ADR-015 D5): single subject DN, no secondary.
        var report = new GapReport(new[]
        {
            new GapFinding(GapKind.UnverifiableArea, new[] { AreaDn }, "Area 'DL_Unexpanded' not expanded."),
        });

        var row = Assert.Single(DataRows(GapReportExporter.ToCsv(report, EmptySummary(), Names)));

        Assert.Equal("UnverifiableArea", row[0]);
        Assert.Equal("DL_Unexpanded", row[1]);
        Assert.Equal(AreaDn, row[2]);
        Assert.Equal(string.Empty, row[3]);
    }

    [Theory]
    [InlineData(GapKind.EdgeAdded, "EdgeAdded")]
    [InlineData(GapKind.EdgeRemoved, "EdgeRemoved")]
    public void Csv_EdgeRow_SecondaryDnIsTheChildDn(GapKind kind, string expectedKindName)
    {
        // An edge finding: Dns = [parent, child]; PrimaryDn = parent (the SubjectName anchor),
        // SecondaryDn = child (Dns[1]) — POPULATED, unlike a node/area finding.
        var report = new GapReport(new[]
        {
            new GapFinding(kind, new[] { ParentDn, ChildDn }, "Membership changed."),
        });

        var row = Assert.Single(DataRows(GapReportExporter.ToCsv(report, EmptySummary(), Names)));

        Assert.Equal(expectedKindName, row[0]);
        Assert.Equal("DL_FileShare_RW", row[1]); // SubjectName resolves PARENT (Dns[0])
        Assert.Equal(ParentDn, row[2]);          // PrimaryDn = parent
        Assert.Equal(ChildDn, row[3]);           // SecondaryDn = child (Dns[1]) POPULATED
        Assert.Equal("Membership changed.", row[4]);
    }

    [Fact]
    public void Csv_Rows_FollowReportFindingsOrder_Verbatim()
    {
        // The report stores canonical block order; ToCsv emits rows 1:1 in that order, never
        // re-sorting. Build a report whose Findings are in a fixed order and assert the Kind
        // column matches index-for-index.
        var findings = new[]
        {
            new GapFinding(GapKind.NodeAdded, new[] { AddedDn }, "a"),
            new GapFinding(GapKind.NodeRemoved, new[] { RemovedDn }, "r"),
            new GapFinding(GapKind.EdgeAdded, new[] { ParentDn, ChildDn }, "ea"),
            new GapFinding(GapKind.EdgeRemoved, new[] { ParentDn, ChildDn }, "er"),
            new GapFinding(GapKind.UnverifiableArea, new[] { AreaDn }, "u"),
        };
        var report = new GapReport(findings);

        var rows = DataRows(GapReportExporter.ToCsv(report, EmptySummary(), Names));

        Assert.Equal(
            findings.Select(f => f.Kind.ToString()).ToArray(),
            rows.Select(r => r[0]).ToArray());
    }

    // === CSV: injection guard + RFC-4180 quoting, on the right columns ====================

    [Theory]
    [InlineData("=cmd()")]
    [InlineData("+1")]
    [InlineData("-1")]
    [InlineData("@SUM")]
    [InlineData("\tlead-tab")]
    [InlineData("\rlead-cr")]
    [InlineData("\nlead-lf")]
    public void Csv_Guard_SubjectName_LeadingDangerousChar_GetsApostrophePrefix(string hostileName)
    {
        // A SubjectName whose FIRST char is one of = + - @ TAB CR LF is neutralized with a
        // leading '. (The CR/LF leads also trip the quote layer; the round-trip decodes both.)
        var resolver = (ViolationReportExporter.ResolveName)(_ => hostileName);
        var report = new GapReport(new[]
        {
            new GapFinding(GapKind.NodeAdded, new[] { AddedDn }, "msg"),
        });

        var row = Assert.Single(DataRows(GapReportExporter.ToCsv(report, EmptySummary(), resolver)));

        Assert.Equal("'" + hostileName, row[1]); // SubjectName column, guarded
    }

    [Fact]
    public void Csv_Guard_PrimaryDn_LeadingDangerousChar_GetsApostrophePrefix()
    {
        // A hostile DN (an injected raw-DN synthetic node) leading with '=' is guarded on the
        // PrimaryDn column too — every cell goes through the guard.
        const string hostileDn = "=CN=Evil,DC=agdlp,DC=lab";
        var report = new GapReport(new[]
        {
            new GapFinding(GapKind.NodeAdded, new[] { hostileDn }, "msg"),
        });

        var csv = GapReportExporter.ToCsv(report, EmptySummary(), Names);

        // Guard (' for leading '='), then quote (for the comma): "'=CN=Evil,DC=agdlp,DC=lab".
        Assert.Contains("\"'=CN=Evil,DC=agdlp,DC=lab\"", csv);
        Assert.DoesNotContain("'\"=CN=Evil", csv); // ' is INSIDE the quotes, not before them
        var row = Assert.Single(DataRows(csv));
        // The RFC-4180 re-parse strips the quotes but the guard ' is field DATA -> it survives.
        Assert.Equal("'" + hostileDn, row[2]);
    }

    [Fact]
    public void Csv_Guard_Message_LeadingDangerousChar_GetsApostrophePrefix()
    {
        var report = new GapReport(new[]
        {
            new GapFinding(GapKind.NodeAdded, new[] { AddedDn }, "@HYPERLINK(\"evil\")"),
        });

        var row = Assert.Single(DataRows(GapReportExporter.ToCsv(report, EmptySummary(), Names)));

        Assert.Equal("'@HYPERLINK(\"evil\")", row[4]); // Message column, guarded
    }

    [Fact]
    public void Csv_Quote_SecondaryDnWithComma_IsWrapped_AndRoundTrips()
    {
        // The child DN (SecondaryDn) of an edge finding contains commas; it must be RFC-4180
        // wrapped so the commas are data, not field splits.
        var report = new GapReport(new[]
        {
            new GapFinding(GapKind.EdgeAdded, new[] { ParentDn, ChildDn }, "m"),
        });

        var csv = GapReportExporter.ToCsv(report, EmptySummary(), Names);

        Assert.Contains($"\"{ChildDn}\"", csv);              // wrapped verbatim
        var row = Assert.Single(DataRows(csv));
        Assert.Equal(ChildDn, row[3]);                       // the comma is not a split
    }

    [Fact]
    public void Csv_Quote_MessageWithEmbeddedQuote_DoublesTheQuote()
    {
        var report = new GapReport(new[]
        {
            new GapFinding(GapKind.NodeAdded, new[] { AddedDn }, "He said \"hi\""),
        });

        var csv = GapReportExporter.ToCsv(report, EmptySummary(), Names);

        Assert.Contains("\"He said \"\"hi\"\"\"", csv);       // wrapped, inner " doubled
        var row = Assert.Single(DataRows(csv));
        Assert.Equal("He said \"hi\"", row[4]);               // decodes back to the original
    }

    [Fact]
    public void Csv_Quote_MessageWithCrlf_IsWrapped_AndCrlfStaysInsideTheField()
    {
        const string msg = "line one\r\nline two";
        var report = new GapReport(new[]
        {
            new GapFinding(GapKind.NodeAdded, new[] { AddedDn }, msg),
        });

        var csv = GapReportExporter.ToCsv(report, EmptySummary(), Names);

        var row = Assert.Single(DataRows(csv));
        Assert.Equal(msg, row[4]);              // the embedded CRLF survives inside one field
        Assert.Contains($"\"{msg}\"", csv);     // wrapped
    }

    [Fact]
    public void Csv_GuardBeforeQuote_ApostropheLandsInsideTheQuotes()
    {
        // THE pinned ordering vector: a value leading with '-' (guard fires) AND containing a
        // comma (quote fires). Guard runs FIRST -> "'-2,3" with the ' INSIDE the quotes.
        var resolver = (ViolationReportExporter.ResolveName)(_ => "-2,3");
        var report = new GapReport(new[]
        {
            new GapFinding(GapKind.NodeAdded, new[] { AddedDn }, "msg"),
        });

        var csv = GapReportExporter.ToCsv(report, EmptySummary(), resolver);

        Assert.Contains("\"'-2,3\"", csv);
        Assert.DoesNotContain("'\"-2,3", csv);
        var row = Assert.Single(DataRows(csv));
        Assert.Equal("'-2,3", row[1]);
    }

    [Fact]
    public void Csv_Output_IsDeterministic_AcrossCultures()
    {
        // No timestamp, no culture-sensitive formatting (Kind is an enum NAME). Identical bytes
        // under a hostile culture (Turkish-I) — InvariantCulture / ordinal throughout.
        var report = new GapReport(new[]
        {
            new GapFinding(GapKind.EdgeAdded, new[] { ParentDn, ChildDn }, "m"),
            new GapFinding(GapKind.UnverifiableArea, new[] { AreaDn }, "u"),
        });

        var invariant = GapReportExporter.ToCsv(report, EmptySummary(), Names);

        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("tr-TR");
            Assert.Equal(invariant, GapReportExporter.ToCsv(report, EmptySummary(), Names));
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    // === HTML: self-contained + every token escaped (never a live tag/attribute) ===========

    // The dangerous payload to inject: a script tag, an ampersand, a double quote, a newline,
    // and a single quote. Carries NONE of the "external ref" tripwires (no http/src=/href=) so
    // the self-contained assertions test the EXPORTER, not the fixture.
    private const string Inject = "<script>alert(1)</script>&\"\n'";
    private const string DnMarker = "INJDNMARKER";
    private const string NameMarker = "INJNAMEMARKER";
    private const string MsgMarker = "INJMSGMARKER";

    [Fact]
    public void Html_Escaping_AngleBracketsAmpersandQuote_AreEntityEncoded_NeverRaw()
    {
        var html = RenderHtmlInjected();

        Assert.Contains("&lt;script&gt;", html);
        Assert.Contains("&amp;", html);
        Assert.Contains("&quot;", html);

        // The raw, unescaped script tag NEVER survives into the document (no live tag).
        Assert.DoesNotContain("<script>", html);
        Assert.DoesNotContain("</script>", html);
        Assert.DoesNotContain("alert(1)</script>", html);
    }

    [Fact]
    public void Html_Escaping_RawAmpersand_NeverAppearsUnescaped()
    {
        // Every '&' in the output must begin a valid entity (&...;) — a bare '&' would be an
        // injected raw token leaking through.
        var html = RenderHtmlInjected();

        foreach (Match m in Regex.Matches(html, "&"))
        {
            var tail = html.Substring(m.Index, Math.Min(8, html.Length - m.Index));
            Assert.Matches("^&(?:lt|gt|amp|quot|#\\d+|#x[0-9a-fA-F]+);", tail);
        }
    }

    [Fact]
    public void Html_Injection_TokensNeverAppearInsideAnyAttributeValue()
    {
        // WebUtility.HtmlEncode leaves "'" literal, which is safe ONLY because no token is ever
        // emitted into an attribute. So no attribute value may contain ANY injected marker.
        var html = RenderHtmlInjected();

        foreach (var attributeValue in AttributeValues(html))
        {
            Assert.DoesNotContain(DnMarker, attributeValue);
            Assert.DoesNotContain(NameMarker, attributeValue);
            Assert.DoesNotContain(MsgMarker, attributeValue);
        }
    }

    [Fact]
    public void Html_Injection_EveryTokenReachesTheDocument_InElementText()
    {
        // Sanity: the markers DO appear (so the no-attribute test is meaningful — the tokens are
        // rendered, just only in text nodes). DnMarker (Dns[0]), NameMarker (SubjectName via the
        // injecting resolver), MsgMarker (Message) are each present.
        var html = RenderHtmlInjected();

        Assert.Contains(DnMarker, html);
        Assert.Contains(NameMarker, html);
        Assert.Contains(MsgMarker, html);
    }

    [Fact]
    public void Html_SecondaryDn_IsEscaped_NotRawInterpolated()
    {
        // The edge child DN (the SecondaryDn cell) is also an untrusted token: a hostile child
        // DN goes through HtmlEncode exactly like the parent — never a live tag.
        var report = new GapReport(new[]
        {
            new GapFinding(GapKind.EdgeAdded, new[] { ParentDn, DnMarker + Inject }, "m"),
        });

        var html = RenderHtml(report);

        Assert.Contains("&lt;script&gt;", html);
        Assert.DoesNotContain("<script>", html);
        Assert.Contains(DnMarker, html);
    }

    [Fact]
    public void Html_SelfContained_NoExternalReference()
    {
        // A self-contained file that opens offline: no http(s), no src=, no href=. The injected
        // payload deliberately carries none of these, so a hit proves the EXPORTER emitted one.
        var html = RenderHtmlInjected();

        Assert.DoesNotContain("http", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("src=", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("href=", html, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Html_Document_HasInlineStyleBlock()
    {
        var html = RenderHtmlInjected();

        Assert.Contains("<style", html);
        Assert.Contains("</style>", html);
    }

    // === HTML: the GapSummary honesty counts render =======================================

    [Fact]
    public void Html_GapSummaryHonestyCounts_NodesEdgesAndUncheckedAreas_AreRendered()
    {
        // The summary carries the per-status node/edge deltas + the unchecked tally; the export
        // must surface all of them so a bare gap report is self-describing (ADR-015 D5: the
        // unexpanded Ist areas were never compared — never silently dropped).
        var summary = new GapSummary(
            AddedNodes: 2,
            RemovedNodes: 3,
            CommonNodes: 10,
            AddedEdges: 4,
            RemovedEdges: 5,
            CommonEdges: 20,
            UncheckedEdges: 6,
            UncheckedParents: 7);
        var report = new GapReport(new[]
        {
            new GapFinding(GapKind.NodeAdded, new[] { AddedDn }, "added"),
        });

        var html = GapReportExporter.ToHtml(report, summary, Names, Header);

        // The node + edge deltas and the unchecked-areas tally all reach the document.
        Assert.Contains("2 added, 3 removed", html);  // nodes
        Assert.Contains("4 added, 5 removed", html);  // edges
        Assert.Contains("7 unexpanded current-structure areas not compared", html);
    }

    [Fact]
    public void Html_HeaderIdentity_RootDnNameAndConnection_AreRendered_Escaped()
    {
        var html = RenderHtmlInjected();

        Assert.Contains(WebUtilEncode(Header.RootName), html);
        Assert.Contains(WebUtilEncode(Header.RootDn), html);
        Assert.Contains(WebUtilEncode(Header.ConnectionSummary), html);
    }

    [Fact]
    public void Html_GeneratedTimestamp_RendersTheInjectedInstant_NotAnAmbientClock()
    {
        // The header timestamp is INJECTED (DiffReportHeader.GeneratedAt), so a fixed instant
        // renders deterministically — the exporter keeps no ambient clock.
        var html = RenderHtmlInjected();

        Assert.Contains("2026-06-30 09:30:00", html);
    }

    // === HTML: deterministic for a fixed injected GeneratedAt =============================

    [Fact]
    public void Html_Output_IsStable_ForFixedInputs()
    {
        // Same inputs -> same bytes (the GeneratedAt is injected, not read from the clock; two
        // renders separated in time are byte-identical).
        Assert.Equal(RenderHtmlInjected(), RenderHtmlInjected());
    }

    [Fact]
    public void Html_Output_IsDeterministic_AcrossCultures()
    {
        // Pure function of (report, summary, resolveName, header). No ambient clock, no
        // culture-sensitive formatting — identical bytes under a hostile culture.
        var invariant = RenderHtmlInjected();

        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("tr-TR");
            Assert.Equal(invariant, RenderHtmlInjected());
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    // === HTML: a clean (zero-finding) report shows an all-clear line, NO table rows ========

    [Fact]
    public void Html_CleanReport_RendersAllClearLine_AndNoFindingsTableRows()
    {
        // A clean diff still has an exportable summary, but no findings: an all-clear line and
        // no <tbody>/<tr> findings rows (the GapSummary counts still render, so the meta table
        // exists — but the FINDINGS table body does not).
        var summary = new GapSummary(
            AddedNodes: 0,
            RemovedNodes: 0,
            CommonNodes: 8,
            AddedEdges: 0,
            RemovedEdges: 0,
            CommonEdges: 12,
            UncheckedEdges: 0,
            UncheckedParents: 0);

        var html = GapReportExporter.ToHtml(GapReport.Empty, summary, Names, Header);

        // The all-clear line shows...
        Assert.Contains("No differences", html);
        // ...the findings table is absent (its body / data-row elements never render).
        Assert.DoesNotContain("<tbody>", html);
        Assert.DoesNotContain("class=\"findings\"", html);

        // And the honesty counts (all zero here) still render — the meta table is self-describing.
        Assert.Contains("0 added, 0 removed", html);
        Assert.Contains("0 unexpanded current-structure areas not compared", html);
    }

    [Fact]
    public void Html_WithFindings_DoesNotShowAllClearLine_AndRendersTheFindingsTable()
    {
        var report = new GapReport(new[]
        {
            new GapFinding(GapKind.NodeRemoved, new[] { RemovedDn }, "removed"),
        });

        var html = RenderHtml(report);

        Assert.DoesNotContain("No differences", html);
        Assert.Contains("<tbody>", html);                 // the findings table is present
        Assert.Contains(WebUtilEncode(RemovedDn), html);  // the finding's DN reached the table
    }

    // === helpers ==========================================================================

    private static GapSummary EmptySummary() => new(0, 0, 0, 0, 0, 0, 0, 0);

    private static string RenderHtml(GapReport report) =>
        GapReportExporter.ToHtml(report, EmptySummary(), Names, Header);

    private static string WebUtilEncode(string value) => System.Net.WebUtility.HtmlEncode(value);

    /// <summary>A report carrying the dangerous payload in an edge finding's DN list and Message
    /// (the SubjectName is injected via the Names resolver). One edge finding so BOTH Dns
    /// endpoints (parent + child) flow through the escaper, plus a hostile Message.</summary>
    private static GapReport WithInjectedTokens() =>
        new(new[]
        {
            new GapFinding(
                GapKind.EdgeAdded,
                new[] { DnMarker + Inject, ChildDn },
                MsgMarker + Inject),
        });

    // For WithInjectedTokens the resolver must inject the NameMarker on the anchor DN it sees.
    // (The shared Names resolver falls back to the raw DN for unknown DNs, so wrap it here.)
    private static string RenderHtmlInjected() =>
        GapReportExporter.ToHtml(
            WithInjectedTokens(),
            EmptySummary(),
            _ => NameMarker + Inject,
            Header);

    /// <summary>Parses the CSV (RFC-4180) and returns the DATA rows only (header dropped). The
    /// parser honors quoted fields with embedded commas, doubled quotes, and embedded CRLFs so
    /// structure can be asserted without coupling to a particular rendering of those bytes.</summary>
    private static List<string[]> DataRows(string csv)
    {
        var records = ParseRfc4180(csv);

        Assert.NotEmpty(records);
        Assert.Equal(CsvHeader.Split(','), records[0]);

        var dataRows = new List<string[]>();
        for (var i = 1; i < records.Count; i++)
        {
            var record = records[i];
            Assert.Equal(5, record.Length);
            dataRows.Add(record);
        }

        return dataRows;
    }

    /// <summary>A minimal RFC-4180 reader (mirrors the ViolationReportCsvTests helper): CRLF
    /// record separator, comma field separator, double-quote quoting with "" escaping, quoted
    /// fields may carry commas/CRLFs/quotes. A trailing CRLF yields no spurious empty record.</summary>
    private static List<string[]> ParseRfc4180(string text)
    {
        var records = new List<string[]>();
        var fields = new List<string>();
        var field = new StringBuilder();
        var inQuotes = false;
        var i = 0;

        void EndField()
        {
            fields.Add(field.ToString());
            field.Clear();
        }

        void EndRecord()
        {
            EndField();
            records.Add(fields.ToArray());
            fields.Clear();
        }

        while (i < text.Length)
        {
            var c = text[i];

            if (inQuotes)
            {
                if (c == '"')
                {
                    if (i + 1 < text.Length && text[i + 1] == '"')
                    {
                        field.Append('"');
                        i += 2;
                        continue;
                    }

                    inQuotes = false;
                    i++;
                    continue;
                }

                field.Append(c);
                i++;
                continue;
            }

            switch (c)
            {
                case '"':
                    inQuotes = true;
                    i++;
                    break;
                case ',':
                    EndField();
                    i++;
                    break;
                case '\r' when i + 1 < text.Length && text[i + 1] == '\n':
                    EndRecord();
                    i += 2;
                    break;
                default:
                    field.Append(c);
                    i++;
                    break;
            }
        }

        if (field.Length > 0 || fields.Count > 0)
        {
            EndRecord();
        }

        return records;
    }

    /// <summary>Extracts every HTML attribute VALUE from the document so a test can assert no
    /// untrusted token ever lands inside an attribute. Permissive: over-collects rather than
    /// under-collects, so a token hiding in any attribute is caught.</summary>
    private static IEnumerable<string> AttributeValues(string html)
    {
        foreach (Match m in Regex.Matches(html, "=\\s*\"([^\"]*)\""))
        {
            yield return m.Groups[1].Value;
        }

        foreach (Match m in Regex.Matches(html, "=\\s*'([^']*)'"))
        {
            yield return m.Groups[1].Value;
        }
    }
}
