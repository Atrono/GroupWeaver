using System.Globalization;

using GroupWeaver.Core.Export;
using GroupWeaver.Core.Rules;

using Xunit;

namespace GroupWeaver.Tests.Export;

/// <summary>
/// Pins <c>ViolationReportExporter.ToCsv(report, resolveName)</c> (AP 4.1 /
/// ADR-013, issue #56), slice 1 — the violation-report CSV serializer.
///
/// Contract (binding, from ADR-013 §3 + the spec "CSV" section):
/// <list type="bullet">
///   <item>Header row verbatim: <c>RuleId,Severity,SubjectName,PrimaryDn,Dns,Message</c>.</item>
///   <item>Rows iterate <c>report.Violations</c> in canonical order VERBATIM —
///         the serializer never re-sorts (RuleReport already stores canonical order).</item>
///   <item><c>Severity</c> = the enum NAME (<c>Error</c>/<c>Warning</c>/<c>Info</c>),
///         InvariantCulture.</item>
///   <item><c>SubjectName</c> = <c>resolveName(PrimaryDn)</c>; <c>PrimaryDn</c> = <c>Dns[0]</c>;
///         <c>Dns</c> = the FULL list joined with ";" inside ONE cell (lossless —
///         carries everything the structured finding has, unlike the VM projection).</item>
///   <item>Two-layer cell encoding, GUARD BEFORE QUOTE (pinned order):
///         (1) formula-injection guard (#45) — a cell whose first char is one of
///         <c>= + - @ \t \r \n</c> gets a leading <c>'</c>; (2) RFC-4180 quoting —
///         a cell containing <c>" , \r \n</c> is wrapped in <c>"..."</c> with embedded
///         <c>"</c> doubled. The <c>'</c> therefore lands INSIDE the quotes.</item>
///   <item>Appendix: a blank line, then a literal <c>UncheckedDns</c> header line, then
///         the DN list one per line, each cell under the same guard+quote rules.</item>
///   <item>Deterministic: no timestamp, no culture/ambient state (the CSV carries no
///         header block — that is HTML-only).</item>
/// </list>
///
/// Violations are HAND-BUILT (report mechanics, not engine semantics — they need
/// not be directory-consistent). The CSV is asserted as an exact byte-for-byte
/// string where the format is fully determined; an RFC-4180-aware re-parse pins
/// the record/field structure where embedded CRLFs would otherwise be ambiguous.
/// </summary>
public class ViolationReportCsvTests
{
    private const string Eol = "\r\n";
    private const string HeaderRow = "RuleId,Severity,SubjectName,PrimaryDn,Dns,Message";

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

    // ---- header + a clean row -------------------------------------------------

    [Fact]
    public void Header_IsTheFixedColumnRow()
    {
        var csv = ViolationReportExporter.ToCsv(RuleReport.Empty, Names);

        Assert.StartsWith(HeaderRow + Eol, csv);
    }

    [Fact]
    public void EmptyReport_IsHeaderOnly_NoAppendix()
    {
        // No violations and no unchecked DNs -> the file is the header row only.
        var csv = ViolationReportExporter.ToCsv(RuleReport.Empty, Names);

        Assert.Equal(HeaderRow + Eol, csv);
    }

    [Fact]
    public void Row_RendersAllSixColumns_SubjectNameResolvedFromPrimaryDn()
    {
        // A nesting finding: Dns = [parent, member]; PrimaryDn = parent;
        // SubjectName = resolveName(parent); Dns cell = both joined with ';'.
        var nesting = V(RuleIds.Nesting, RuleSeverity.Error, "DL must not nest GG.", ParentDn, MemberDn);
        var report = new RuleReport(new[] { nesting }, Array.Empty<string>());

        var rows = DataRows(ViolationReportExporter.ToCsv(report, Names));

        var row = Assert.Single(rows);
        Assert.Equal(
            new[]
            {
                RuleIds.Nesting,
                "Error",
                "DL_FS-Finance_RO",
                ParentDn,
                $"{ParentDn};{MemberDn}",
                "DL must not nest GG.",
            },
            row);
    }

    // ---- Severity enum names --------------------------------------------------

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

        Assert.Equal(expected, row[1]);
    }

    // ---- canonical order is preserved verbatim --------------------------------

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
            rows.Select(r => r[0]).ToArray());
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
        Assert.Equal(dn, row[3]);
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
        Assert.Equal("He said \"hi\"", row[2]);
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
        Assert.Equal(msg, row[5]);
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
        Assert.Equal("'" + hostileName, row[2]);
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

        Assert.Equal("x=y", row[2]);
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
        Assert.Equal("'-2,3", row[2]);
    }

    // ---- UncheckedDns appendix ------------------------------------------------

    [Fact]
    public void Appendix_BlankLineThenHeaderThenDnList()
    {
        // The appendix is a separate section: a blank line, the literal
        // "UncheckedDns" header line, then each unchecked DN on its own line.
        // RuleReport sorts UncheckedDns OrdinalIgnoreCase, so the order is fixed.
        var unchecked1 = "CN=ignored2,DC=agdlp,DC=lab";
        var unchecked2 = "CN=builtin,DC=agdlp,DC=lab";
        var report = new RuleReport(
            new[] { V(RuleIds.EmptyGroup, RuleSeverity.Info, "empty", SubjectDn) },
            new[] { unchecked1, unchecked2 });

        var csv = ViolationReportExporter.ToCsv(report, Names);

        // Exact tail: header row, the one data row, blank line, appendix header,
        // then the two DNs in RuleReport's OrdinalIgnoreCase order.
        var expectedTail = string.Join(
            Eol,
            "UncheckedDns",
            "CN=builtin,DC=agdlp,DC=lab",
            "CN=ignored2,DC=agdlp,DC=lab") + Eol;
        Assert.EndsWith(expectedTail, csv);

        // And a genuine blank line separates the violations block from the appendix.
        Assert.Contains(Eol + Eol + "UncheckedDns" + Eol, csv);
    }

    [Fact]
    public void Appendix_AppearsEvenWithNoViolations_AllClearButUnchecked()
    {
        // F2: the export gate is "Snapshot is not null", NOT HasViolations — so
        // the all-clear-but-unchecked state is a real exportable artifact. With
        // zero violations but a non-empty frontier, the appendix still renders.
        var report = new RuleReport(
            Array.Empty<RuleViolation>(),
            new[] { "CN=unexpanded,DC=agdlp,DC=lab" });

        var csv = ViolationReportExporter.ToCsv(report, Names);

        Assert.Equal(
            string.Join(Eol, HeaderRow, "", "UncheckedDns", "CN=unexpanded,DC=agdlp,DC=lab") + Eol,
            csv);
    }

    [Fact]
    public void Appendix_DnCells_AreGuardedAndQuoted_PerCell()
    {
        // Same guard+quote rules apply to appendix cells. A DN that leads with a
        // dangerous char AND contains a comma triggers BOTH, ' inside the quotes.
        var hostile = "=CN=Evil,DC=agdlp,DC=lab";
        var report = new RuleReport(
            Array.Empty<RuleViolation>(),
            new[] { hostile });

        var csv = ViolationReportExporter.ToCsv(report, Names);

        // Guard (' for leading '='), then quote (for the comma): "'=CN=Evil,DC=agdlp,DC=lab".
        Assert.Contains("\"'=CN=Evil,DC=agdlp,DC=lab\"", csv);
        Assert.DoesNotContain("'\"=CN=Evil", csv);
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

    /// <summary>Parses the CSV (RFC-4180) and returns the DATA rows only (header
    /// dropped, appendix section dropped). The parser honors quoted fields with
    /// embedded commas, doubled quotes, and embedded CRLFs so structure can be
    /// asserted without coupling to a particular rendering of those bytes.</summary>
    private static List<string[]> DataRows(string csv)
    {
        var records = ParseRfc4180(csv);

        // Drop the header.
        Assert.NotEmpty(records);
        Assert.Equal(HeaderRow.Split(','), records[0]);

        var dataRows = new List<string[]>();
        for (var i = 1; i < records.Count; i++)
        {
            var record = records[i];

            // The appendix begins at a blank line followed by the "UncheckedDns"
            // header; stop collecting data rows there.
            if (record.Length == 1 && record[0].Length == 0)
            {
                break;
            }

            if (record.Length == 1 && record[0] == "UncheckedDns")
            {
                break;
            }

            Assert.Equal(6, record.Length);
            dataRows.Add(record);
        }

        return dataRows;
    }

    /// <summary>A minimal RFC-4180 reader: CRLF record separator, comma field
    /// separator, double-quote quoting with "" escaping, quoted fields may carry
    /// commas/CRLFs/quotes. Trailing CRLF yields no spurious empty record.</summary>
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
