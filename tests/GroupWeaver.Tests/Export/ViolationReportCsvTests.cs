using System.Globalization;
using System.Text;

using GroupWeaver.Core.Export;
using GroupWeaver.Core.Rules;

using Xunit;

namespace GroupWeaver.Tests.Export;

/// <summary>
/// Pins <c>ViolationReportExporter.ToCsv(report, resolveName)</c> (AP 4.1 /
/// ADR-013, issue #56; REVISED by #329 / audit Lever 1 — the Excel-correctness
/// contract).
///
/// Contract (binding, from ADR-013 §3 + #329):
/// <list type="bullet">
///   <item><b>ONE rectangular RFC-4180 table.</b> Every record has exactly SEVEN
///         fields. The old two-section shape (blank line + a bare
///         <c>UncheckedDns</c> header + one-column DN lines) is GONE — strict
///         parsers and Excel see a single table (#329 defect 1).</item>
///   <item>Header row verbatim:
///         <c>Section,RuleId,Severity,SubjectName,PrimaryDn,Dns,Message</c>.</item>
///   <item><b>Leading UTF-8 BOM.</b> The returned string starts with EXACTLY ONE
///         U+FEFF, so any UTF-8 byte writer (including the App's BOM-less
///         <c>UTF8Encoding(false)</c> discipline) produces a file whose first
///         three bytes are <c>EF BB BF</c> — Excel double-click decodes UTF-8
///         instead of ANSI for the German-AD audience (#329 defect 2). The BOM
///         is CSV-only; HTML keeps its <c>meta charset</c>.</item>
///   <item><b>Finding rows:</b> <c>Section</c> = <c>finding</c>; rows iterate
///         <c>report.Violations</c> in canonical order VERBATIM (never re-sorted);
///         <c>Severity</c> = the enum NAME; <c>SubjectName</c> =
///         <c>resolveName(PrimaryDn)</c>; <c>PrimaryDn</c> = <c>Dns[0]</c>;
///         <c>Dns</c> = the FULL list joined with a single LF (<c>\n</c>) inside
///         ONE quoted cell — DNs cannot contain raw newlines, so the join is
///         unambiguous (the old bare-<c>;</c> join collided with RFC-4514
///         escaped semicolons, #329 defect 3) and Excel renders in-cell breaks.</item>
///   <item><b>Unchecked rows:</b> one row per <c>RuleReport.UncheckedDns</c>
///         entry, in report order: <c>Section</c> = <c>unchecked</c>;
///         RuleId/Severity/Message EMPTY; <c>SubjectName</c> =
///         <c>resolveName(dn)</c>; <c>PrimaryDn</c> = <c>Dns</c> = the DN — every
///         column keeps its finding-row meaning, so consumers can filter on
///         <c>Section</c> and keep pivoting on <c>PrimaryDn</c>.</item>
///   <item>Two-layer cell encoding on EVERY cell of EVERY row, GUARD BEFORE QUOTE
///         (pinned order): (1) formula-injection guard (#45) — a cell whose first
///         char is one of <c>= + - @ \t \r \n</c> gets a leading <c>'</c>;
///         (2) RFC-4180 quoting — a cell containing <c>" , \r \n</c> is wrapped
///         in <c>"..."</c> with embedded <c>"</c> doubled. The <c>'</c> therefore
///         lands INSIDE the quotes. The old appendix comma-exemption is dead.</item>
///   <item>CRLF record separators, trailing CRLF after the last record.
///         Deterministic: no timestamp, no culture/ambient state.</item>
/// </list>
///
/// Violations are HAND-BUILT (report mechanics, not engine semantics — they need
/// not be directory-consistent). The CSV is asserted as an exact byte-for-byte
/// string where the format is fully determined; an RFC-4180-aware re-parse pins
/// the record/field structure (and enforces 7-field rectangularity on every
/// record) where embedded LFs would otherwise be ambiguous.
/// </summary>
public class ViolationReportCsvTests
{
    private const string Eol = "\r\n";
    private const string Bom = "\uFEFF";
    private const string HeaderRow = "Section,RuleId,Severity,SubjectName,PrimaryDn,Dns,Message";

    // Column indexes of the rectangular table (pinned by the header row).
    private const int ColSection = 0;
    private const int ColRuleId = 1;
    private const int ColSeverity = 2;
    private const int ColSubjectName = 3;
    private const int ColPrimaryDn = 4;
    private const int ColDns = 5;
    private const int ColMessage = 6;

    private const string ParentDn = "CN=DL_FS-Finance_RO,OU=Groups,OU=AGDLP-Demo,DC=weavedemo,DC=example";
    private const string MemberDn = "CN=GG_Finance,OU=Groups,OU=AGDLP-Demo,DC=weavedemo,DC=example";
    private const string SubjectDn = "CN=GG_X,OU=Groups,OU=AGDLP-Demo,DC=weavedemo,DC=example";

    // dn -> friendly name; falls back to the DN itself for unknown DNs, exactly
    // as the VM closure does (Snapshot.TryGetObject(dn, out o) ? o.Name : dn).
    private static readonly ViolationReportExporter.ResolveName Names = dn => dn switch
    {
        ParentDn => "DL_FS-Finance_RO",
        MemberDn => "GG_Finance",
        SubjectDn => "GG_X",
        _ => dn,
    };

    // ---- BOM + header -----------------------------------------------------------

    [Fact]
    public void Output_StartsWithExactlyOneBom_ThenTheHeaderRow()
    {
        var csv = ViolationReportExporter.ToCsv(RuleReport.Empty, Names);

        // Exactly ONE leading U+FEFF, immediately followed by the header row —
        // never zero (Excel-ANSI mojibake) and never doubled (a stray second BOM
        // would become visible data in the first cell).
        Assert.StartsWith(Bom + HeaderRow + Eol, csv, StringComparison.Ordinal);
        Assert.Equal(-1, csv.IndexOf('\uFEFF', 1));
    }

    [Fact]
    public void Output_Utf8Bytes_StartWithEfBbBf()
    {
        // The end-to-end Excel contract (#329 defect 2): encoding the returned
        // string with the App's BOM-less writer encoding still yields a file whose
        // FIRST THREE BYTES are the UTF-8 BOM, because the BOM travels IN the
        // string. Byte 4 is the header's leading 'S' — one BOM, nothing between.
        var csv = ViolationReportExporter.ToCsv(RuleReport.Empty, Names);
        var bytes = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(csv);

        Assert.True(bytes.Length >= 4, "BOM + header expected");
        Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, bytes[..3]);
        Assert.Equal((byte)'S', bytes[3]);
    }

    [Fact]
    public void EmptyReport_IsBomPlusHeaderOnly_NoOtherRows()
    {
        // No violations and no unchecked DNs -> the file is BOM + header row only.
        var csv = ViolationReportExporter.ToCsv(RuleReport.Empty, Names);

        Assert.Equal(Bom + HeaderRow + Eol, csv);
    }

    // ---- finding rows -----------------------------------------------------------

    [Fact]
    public void FindingRow_RendersAllSevenColumns_SectionFinding_DnsJoinedWithLf()
    {
        // A nesting finding: Dns = [parent, member]; PrimaryDn = parent;
        // SubjectName = resolveName(parent); Dns cell = both joined with a
        // single LF inside ONE (quoted) cell.
        var nesting = V(RuleIds.Nesting, RuleSeverity.Error, "DL must not nest GG.", ParentDn, MemberDn);
        var report = new RuleReport(new[] { nesting }, Array.Empty<string>());

        var rows = DataRows(ViolationReportExporter.ToCsv(report, Names));

        var row = Assert.Single(rows);
        Assert.Equal(
            new[]
            {
                "finding",
                RuleIds.Nesting,
                "Error",
                "DL_FS-Finance_RO",
                ParentDn,
                ParentDn + "\n" + MemberDn,
                "DL must not nest GG.",
            },
            row);
    }

    [Fact]
    public void MultiDnCell_IsLfJoined_QuotedVerbatim_NeverSemicolonJoined()
    {
        // The pinned join (#329 defect 3): the raw text carries the LF-joined DN
        // list inside one quoted cell, and the legacy ';' join is gone. A DN can
        // never contain a raw newline, so the LF join is machine-splittable.
        var report = new RuleReport(
            new[]
            {
                V(RuleIds.Nesting, RuleSeverity.Error, "n", ParentDn, MemberDn),
                V(RuleIds.Circular, RuleSeverity.Error, "c", SubjectDn, MemberDn, ParentDn),
            },
            Array.Empty<string>());

        var csv = ViolationReportExporter.ToCsv(report, Names);

        // Exact quoted literal for the two-DN cell (quoted: the LF is a trigger).
        Assert.Contains("\"" + ParentDn + "\n" + MemberDn + "\"", csv, StringComparison.Ordinal);
        // The legacy sub-delimiter never appears between the joined DNs.
        Assert.DoesNotContain(ParentDn + ";" + MemberDn, csv, StringComparison.Ordinal);

        // Round-trip: splitting the decoded cell on LF restores the exact DN lists.
        var rows = DataRows(csv);
        Assert.Equal(new[] { ParentDn, MemberDn }, rows[0][ColDns].Split('\n'));
        Assert.Equal(new[] { SubjectDn, MemberDn, ParentDn }, rows[1][ColDns].Split('\n'));
    }

    [Fact]
    public void HostileDn_CommaQuoteSemicolon_RoundTripsExactly()
    {
        // The #329 hostile vector: a DN carrying a comma, a double-quote AND a
        // semicolon. RFC-4180: wrapped, embedded quotes doubled; the ';' is plain
        // data (the join is LF, so it cannot collide). The strict re-parse must
        // return the DN byte-for-byte.
        const string hostileDn = "CN=Evil \"Group\"; comma, here,DC=agdlp,DC=lab";
        var report = new RuleReport(
            new[] { V(RuleIds.Nesting, RuleSeverity.Error, "n", hostileDn, MemberDn) },
            Array.Empty<string>());

        var csv = ViolationReportExporter.ToCsv(report, Names);

        // The exact escaped literal of the PrimaryDn cell.
        Assert.Contains(
            "\"CN=Evil \"\"Group\"\"; comma, here,DC=agdlp,DC=lab\"",
            csv,
            StringComparison.Ordinal);

        var row = Assert.Single(DataRows(csv));
        Assert.Equal(hostileDn, row[ColPrimaryDn]);
        // And inside the LF-joined Dns cell the hostile DN splits back out exactly.
        Assert.Equal(new[] { hostileDn, MemberDn }, row[ColDns].Split('\n'));
    }

    [Fact]
    public void NonAscii_UmlautName_SurvivesAsUtf8Bytes_BehindTheBom()
    {
        // #329 defect 4: the German-AD vector. The umlaut name must reach the CSV
        // string verbatim and the UTF-8 bytes must carry the two-byte sequence for
        // 'ä' (0xC3 0xA4) behind the leading BOM — the BOM is what makes Excel
        // decode those bytes as UTF-8 on double-click.
        var resolver = (ViolationReportExporter.ResolveName)(_ => "Vertrieb-Käufer");
        var report = new RuleReport(
            new[] { V("naming-gg", RuleSeverity.Warning, "msg", SubjectDn) },
            Array.Empty<string>());

        var csv = ViolationReportExporter.ToCsv(report, resolver);
        Assert.Contains("Vertrieb-Käufer", csv, StringComparison.Ordinal);

        var bytes = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(csv);
        Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, bytes[..3]);
        // "Kä" = 0x4B 0xC3 0xA4 somewhere in the payload.
        var found = false;
        for (var i = 3; i < bytes.Length - 2 && !found; i++)
        {
            found = bytes[i] == 0x4B && bytes[i + 1] == 0xC3 && bytes[i + 2] == 0xA4;
        }

        Assert.True(found, "the UTF-8 byte sequence for 'Kä' (4B C3 A4) must survive into the payload");
    }

    // ---- Severity enum names ------------------------------------------------------

    [Theory]
    [InlineData(RuleSeverity.Error, "Error")]
    [InlineData(RuleSeverity.Warning, "Warning")]
    [InlineData(RuleSeverity.Info, "Info")]
    public void Severity_IsTheEnumName(RuleSeverity severity, string expected)
    {
        var report = new RuleReport(
            new[] { V("naming-gg", severity, "msg", SubjectDn) },
            Array.Empty<string>());

        var row = Assert.Single(DataRows(ViolationReportExporter.ToCsv(report, Names)));

        Assert.Equal(expected, row[ColSeverity]);
    }

    // ---- canonical order is preserved verbatim -------------------------------------

    [Fact]
    public void Rows_FollowReportViolationsOrder_Verbatim()
    {
        // The report stores canonical order; ToCsv emits rows 1:1 in that order,
        // never re-sorting. Build a report whose Violations are in a fixed order
        // and assert the RuleId column matches index-for-index.
        var violations = new[]
        {
            V(RuleIds.Nesting, RuleSeverity.Error, "n", ParentDn, MemberDn),
            V("naming-gg", RuleSeverity.Warning, "w", SubjectDn),
            V(RuleIds.Circular, RuleSeverity.Error, "c", SubjectDn, MemberDn),
            V(RuleIds.EmptyGroup, RuleSeverity.Info, "e", MemberDn),
        };
        var report = new RuleReport(violations, Array.Empty<string>());

        var rows = DataRows(ViolationReportExporter.ToCsv(report, Names));

        Assert.Equal(
            violations.Select(v => v.RuleId).ToArray(),
            rows.Select(r => r[ColRuleId]).ToArray());
        Assert.All(rows, r => Assert.Equal("finding", r[ColSection]));
    }

    // ---- RFC-4180 quoting -----------------------------------------------------

    [Fact]
    public void Quote_DnWithComma_IsWrappedInQuotes()
    {
        // A DN containing a comma must be wrapped; no embedded quote to double.
        const string dn = "CN=Builtin Administrators,DC=agdlp,DC=lab";
        var report = new RuleReport(
            new[] { V(RuleIds.EmptyGroup, RuleSeverity.Info, "empty", dn) },
            Array.Empty<string>());

        // The raw text must contain the quoted cell verbatim...
        var csv = ViolationReportExporter.ToCsv(report, Names);
        Assert.Contains($"\"{dn}\"", csv);

        // ...and round-trips to the exact field value (the comma is not a split).
        var row = Assert.Single(DataRows(csv));
        Assert.Equal(dn, row[ColPrimaryDn]);
    }

    [Fact]
    public void Quote_ValueWithEmbeddedQuote_DoublesTheQuote()
    {
        // A name containing a double-quote: wrap, and double the embedded ".
        var nameResolver = (ViolationReportExporter.ResolveName)(_ => "He said \"hi\"");
        var report = new RuleReport(
            new[] { V("naming-gg", RuleSeverity.Warning, "msg", SubjectDn) },
            Array.Empty<string>());

        var csv = ViolationReportExporter.ToCsv(report, nameResolver);

        // Raw: wrapped with the inner " doubled.
        Assert.Contains("\"He said \"\"hi\"\"\"", csv);
        // Round-trip: the field decodes back to the original value.
        var row = Assert.Single(DataRows(csv));
        Assert.Equal("He said \"hi\"", row[ColSubjectName]);
    }

    [Fact]
    public void Quote_ValueWithCrlf_IsWrapped_AndCrlfStaysInsideTheField()
    {
        // A Message containing a CRLF must be quoted so the CRLF is data, not a
        // record split. The RFC-4180 re-parse proves the embedded CRLF survives
        // inside one field rather than terminating the row.
        const string msg = "line one\r\nline two";
        var report = new RuleReport(
            new[] { V("naming-gg", RuleSeverity.Warning, msg, SubjectDn) },
            Array.Empty<string>());

        var csv = ViolationReportExporter.ToCsv(report, Names);

        var row = Assert.Single(DataRows(csv));
        Assert.Equal(msg, row[ColMessage]);
        // The embedded CRLF is wrapped — the message column is a quoted field.
        Assert.Contains($"\"{msg}\"", csv);
    }

    // ---- formula-injection guard (#45), BEFORE quoting ------------------------

    [Theory]
    [InlineData("=cmd()")]
    [InlineData("+1")]
    [InlineData("-1")]
    [InlineData("@SUM")]
    [InlineData("\tlead-tab")]
    [InlineData("\rlead-cr")]
    [InlineData("\nlead-lf")]
    public void Guard_LeadingDangerousChar_GetsApostrophePrefix(string hostileName)
    {
        // A value whose FIRST char is one of = + - @ TAB CR LF is neutralized
        // with a leading '. The guarded value of a plain (comma/quote-free,
        // non-control-bearing) cell needs no RFC quoting — except the CR/LF
        // leads, which the quote layer must then wrap (covered separately).
        var resolver = (ViolationReportExporter.ResolveName)(_ => hostileName);
        var report = new RuleReport(
            new[] { V("naming-gg", RuleSeverity.Warning, "msg", SubjectDn) },
            Array.Empty<string>());

        var csv = ViolationReportExporter.ToCsv(report, resolver);
        var row = Assert.Single(DataRows(csv));

        // The decoded field begins with the apostrophe guard then the original.
        Assert.Equal("'" + hostileName, row[ColSubjectName]);
    }

    [Fact]
    public void Guard_SafeLeadingChar_GetsNoPrefix()
    {
        // A value NOT leading with a dangerous char is emitted as-is (no ').
        // '=' mid-string is harmless; only a LEADING trigger char is guarded.
        var resolver = (ViolationReportExporter.ResolveName)(_ => "x=y");
        var report = new RuleReport(
            new[] { V("naming-gg", RuleSeverity.Warning, "msg", SubjectDn) },
            Array.Empty<string>());

        var row = Assert.Single(DataRows(ViolationReportExporter.ToCsv(report, resolver)));

        Assert.Equal("x=y", row[ColSubjectName]);
    }

    [Fact]
    public void Guard_BeforeQuote_ApostropheLandsInsideTheQuotes()
    {
        // THE pinned ordering vector: a value like "-2,3" leads with '-' (guard
        // fires) AND contains a comma (quote fires). Guard runs FIRST, so the
        // raw cell is "'-2,3" wrapped -> "'-2,3" with the ' INSIDE the quotes,
        // never "'"-2,3"" with the ' outside.
        var resolver = (ViolationReportExporter.ResolveName)(_ => "-2,3");
        var report = new RuleReport(
            new[] { V("naming-gg", RuleSeverity.Warning, "msg", SubjectDn) },
            Array.Empty<string>());

        var csv = ViolationReportExporter.ToCsv(report, resolver);

        // Exact raw cell: opening quote, then ', then -2,3, then closing quote.
        Assert.Contains("\"'-2,3\"", csv);
        // The ' must be INSIDE, not before the opening quote.
        Assert.DoesNotContain("'\"-2,3", csv);
        // Round-trip: the field decodes to the guarded value.
        var row = Assert.Single(DataRows(csv));
        Assert.Equal("'-2,3", row[ColSubjectName]);
    }

    // ---- unchecked rows (the ex-appendix, now rectangular) --------------------

    [Fact]
    public void UncheckedRows_AreRectangular_SectionUnchecked_AfterTheFindingRows()
    {
        // One row per unchecked DN, AFTER the finding rows, in RuleReport's
        // OrdinalIgnoreCase order: Section=unchecked; RuleId/Severity/Message
        // EMPTY; SubjectName=resolveName(dn); PrimaryDn=Dns=the DN. The DNs carry
        // commas, so those cells quote — pin the exact tail literal.
        var unchecked1 = "CN=ignored2,DC=agdlp,DC=lab";
        var unchecked2 = "CN=builtin,DC=agdlp,DC=lab";
        var report = new RuleReport(
            new[] { V(RuleIds.EmptyGroup, RuleSeverity.Info, "empty", SubjectDn) },
            new[] { unchecked1, unchecked2 });

        var csv = ViolationReportExporter.ToCsv(report, Names);

        // Exact tail: the two unchecked rows (RuleReport sorts OrdinalIgnoreCase,
        // so builtin sorts first). The resolver has no name for either DN, so
        // SubjectName falls back to the (quoted) DN itself.
        var expectedTail =
            "unchecked,,,\"CN=builtin,DC=agdlp,DC=lab\",\"CN=builtin,DC=agdlp,DC=lab\",\"CN=builtin,DC=agdlp,DC=lab\"," + Eol
            + "unchecked,,,\"CN=ignored2,DC=agdlp,DC=lab\",\"CN=ignored2,DC=agdlp,DC=lab\",\"CN=ignored2,DC=agdlp,DC=lab\"," + Eol;
        Assert.EndsWith(expectedTail, csv, StringComparison.Ordinal);

        // Structure: 1 finding row then 2 unchecked rows, all 7 fields wide.
        var rows = DataRows(csv);
        Assert.Equal(new[] { "finding", "unchecked", "unchecked" }, rows.Select(r => r[ColSection]).ToArray());
        Assert.Equal(unchecked2, rows[1][ColPrimaryDn]);
        Assert.Equal(unchecked2, rows[1][ColDns]);
        Assert.Equal(string.Empty, rows[1][ColRuleId]);
        Assert.Equal(string.Empty, rows[1][ColSeverity]);
        Assert.Equal(string.Empty, rows[1][ColMessage]);
    }

    [Fact]
    public void UncheckedRows_NoLegacyAppendixMarkers_NoBlankLine()
    {
        // The old shape is DEAD: no blank separator line, no bare "UncheckedDns"
        // header line. (This fixture has no embedded-CRLF cells, so a CRLFCRLF
        // anywhere would be the legacy blank separator.)
        var report = new RuleReport(
            new[] { V(RuleIds.EmptyGroup, RuleSeverity.Info, "empty", SubjectDn) },
            new[] { "CN=builtin,DC=agdlp,DC=lab" });

        var csv = ViolationReportExporter.ToCsv(report, Names);

        Assert.DoesNotContain("UncheckedDns", csv, StringComparison.Ordinal);
        Assert.DoesNotContain(Eol + Eol, csv, StringComparison.Ordinal);
    }

    [Fact]
    public void UncheckedRows_AppearEvenWithNoViolations_AllClearButUnchecked()
    {
        // F2: the export gate is "Snapshot is not null", NOT HasViolations — so
        // the all-clear-but-unchecked state is a real exportable artifact. With
        // zero violations but a non-empty frontier, the unchecked rows still
        // render — whole-file exact literal.
        var report = new RuleReport(
            Array.Empty<RuleViolation>(),
            new[] { "CN=unexpanded,DC=agdlp,DC=lab" });

        var csv = ViolationReportExporter.ToCsv(report, Names);

        Assert.Equal(
            Bom + HeaderRow + Eol
            + "unchecked,,,\"CN=unexpanded,DC=agdlp,DC=lab\",\"CN=unexpanded,DC=agdlp,DC=lab\",\"CN=unexpanded,DC=agdlp,DC=lab\"," + Eol,
            csv);
    }

    [Fact]
    public void UncheckedRows_DnCells_AreGuardedAndQuoted_PerCell()
    {
        // The same guard+quote rules apply to unchecked-row cells. A DN leading
        // with a dangerous char AND containing a comma triggers BOTH, ' inside
        // the quotes — no appendix comma-exemption survives (#329 defect 1).
        var hostile = "=CN=Evil,DC=agdlp,DC=lab";
        var report = new RuleReport(
            Array.Empty<RuleViolation>(),
            new[] { hostile });

        var csv = ViolationReportExporter.ToCsv(report, Names);

        // Guard (' for leading '='), then quote (for the comma): "'=CN=Evil,DC=agdlp,DC=lab".
        Assert.Contains("\"'=CN=Evil,DC=agdlp,DC=lab\"", csv);
        Assert.DoesNotContain("'\"=CN=Evil", csv);

        var row = Assert.Single(DataRows(csv));
        Assert.Equal("unchecked", row[ColSection]);
        Assert.Equal("'" + hostile, row[ColPrimaryDn]);
    }

    // ---- determinism / InvariantCulture ---------------------------------------

    [Fact]
    public void Output_IsDeterministic_AcrossCultures()
    {
        // No timestamp, no culture-sensitive formatting (severity is an enum
        // NAME, never a localized string). The byte output is identical under a
        // hostile culture (Turkish-I, comma decimal separator) — InvariantCulture.
        var report = new RuleReport(
            new[]
            {
                V(RuleIds.Nesting, RuleSeverity.Error, "n", ParentDn, MemberDn),
                V("naming-INFO", RuleSeverity.Info, "i", SubjectDn),
            },
            new[] { "CN=unexpanded,DC=agdlp,DC=lab" });

        var invariant = ViolationReportExporter.ToCsv(report, Names);

        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("tr-TR");
            var turkish = ViolationReportExporter.ToCsv(report, Names);
            Assert.Equal(invariant, turkish);
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    // ---- helpers --------------------------------------------------------------

    private static RuleViolation V(string ruleId, RuleSeverity severity, string message, params string[] dns) =>
        new()
        {
            RuleId = ruleId,
            Severity = severity,
            Dns = dns,
            Message = message,
        };

    /// <summary>Parses the CSV (RFC-4180) and returns ALL data rows (header
    /// dropped). Asserts the leading single BOM, the exact header record, and the
    /// #329 rectangularity contract: EVERY record has exactly seven fields. The
    /// parser honors quoted fields with embedded commas, doubled quotes, and
    /// embedded CR/LF bytes so structure can be asserted without coupling to a
    /// particular rendering of those bytes.</summary>
    private static List<string[]> DataRows(string csv)
    {
        Assert.StartsWith(Bom, csv, StringComparison.Ordinal);
        Assert.Equal(-1, csv.IndexOf('\uFEFF', 1)); // exactly one BOM

        var records = ParseRfc4180(csv[1..]);

        Assert.NotEmpty(records);
        Assert.Equal(HeaderRow.Split(','), records[0]);

        var dataRows = new List<string[]>();
        for (var i = 1; i < records.Count; i++)
        {
            var record = records[i];
            Assert.Equal(7, record.Length); // ONE rectangular table — no appendix
            dataRows.Add(record);
        }

        return dataRows;
    }

    /// <summary>A minimal RFC-4180 reader: CRLF record separator, comma field
    /// separator, double-quote quoting with "" escaping, quoted fields may carry
    /// commas/CRLFs/bare LFs/quotes. Trailing CRLF yields no spurious empty record.</summary>
    private static List<string[]> ParseRfc4180(string text)
    {
        var records = new List<string[]>();
        var fields = new List<string>();
        var field = new System.Text.StringBuilder();
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

        // A final unterminated record (no trailing CRLF) still counts; a trailing
        // CRLF leaves field/fields empty and produces no extra record.
        if (field.Length > 0 || fields.Count > 0)
        {
            EndRecord();
        }

        return records;
    }
}
