using System.Text;

using GroupWeaver.Core.Rules;

namespace GroupWeaver.Core.Export;

/// <summary>
/// Serializes a <see cref="RuleReport"/> to a downloadable violation report
/// (AP 4.1 / ADR-013, issue #56). Pure Core: UI-free and App-type-free —
/// caller-side concerns (the picked file path, UTF-8 byte writing, the
/// snapshot-name lookup) are injected via the <see cref="ResolveName"/> delegate
/// and (for HTML) the <see cref="ReportHeader"/> value. Deterministic: no
/// ambient time, no culture sensitivity (ordinal string ops throughout;
/// severities render as enum NAMES, not localized strings).
/// </summary>
public static class ViolationReportExporter
{
    private const string Eol = "\r\n";

    /// <summary>The fixed CSV header row (ADR-013 §2, pinned by test).</summary>
    private const string CsvHeader = "RuleId,Severity,SubjectName,PrimaryDn,Dns,Message";

    /// <summary>The literal appendix section header for the unchecked-areas block.</summary>
    private const string UncheckedDnsHeader = "UncheckedDns";

    /// <summary>Resolves a DN to a friendly display name. The VM passes a closure
    /// mirroring <c>OnReportChanged</c> exactly:
    /// <c>dn =&gt; Snapshot.TryGetObject(dn, out var o) ? o.Name : dn</c> — the
    /// fallback is the DN itself, so Core never needs the snapshot.</summary>
    public delegate string ResolveName(string dn);

    /// <summary>
    /// Renders the report as RFC-4180 CSV. The fixed header row, then one row per
    /// finding in <see cref="RuleReport.Violations"/> canonical order VERBATIM
    /// (never re-sorted), then — iff <see cref="RuleReport.UncheckedDns"/> is
    /// non-empty — a blank line, the literal <c>UncheckedDns</c> header, and the
    /// DN list one per line. Every cell is encoded GUARD-BEFORE-QUOTE: the
    /// formula-injection guard (#45) prefixes a single <c>'</c> when the raw cell
    /// leads with <c>= + - @ \t \r \n</c>, then RFC-4180 quoting wraps cells that
    /// contain <c>" , \r \n</c> (so the <c>'</c> lands inside the quotes).
    /// Line endings are CRLF, including a trailing CRLF after the last line.
    /// </summary>
    public static string ToCsv(RuleReport report, ResolveName resolveName)
    {
        ArgumentNullException.ThrowIfNull(report);
        ArgumentNullException.ThrowIfNull(resolveName);

        var sb = new StringBuilder();
        sb.Append(CsvHeader).Append(Eol);

        foreach (var violation in report.Violations)
        {
            var primaryDn = violation.PrimaryDn;
            AppendCsvRow(
                sb,
                violation.RuleId,
                violation.Severity.ToString(),
                resolveName(primaryDn),
                primaryDn,
                string.Join(";", violation.Dns),
                violation.Message);
        }

        if (report.UncheckedDns.Count > 0)
        {
            sb.Append(Eol);
            sb.Append(UncheckedDnsHeader).Append(Eol);
            foreach (var dn in report.UncheckedDns)
            {
                sb.Append(EncodeAppendixCell(dn)).Append(Eol);
            }
        }

        return sb.ToString();
    }

    private static void AppendCsvRow(StringBuilder sb, params string[] cells)
    {
        for (var i = 0; i < cells.Length; i++)
        {
            if (i > 0)
            {
                sb.Append(',');
            }

            sb.Append(EncodeCell(cells[i]));
        }

        sb.Append(Eol);
    }

    /// <summary>Guard-before-quote (pinned order) for a row cell: formula-injection
    /// guard, then RFC-4180 quoting — so the <c>'</c> lands inside the quotes.
    /// A row cell sits between commas, so a literal comma MUST quote.</summary>
    private static string EncodeCell(string value) => QuoteRfc4180(GuardFormulaInjection(value));

    /// <summary>Guard-before-quote for an appendix cell. The appendix is a single
    /// column — a bare comma is unambiguous on its own line, so it does NOT force
    /// quoting (the test pins bare DNs like <c>CN=builtin,DC=…</c> unquoted).
    /// Quoting fires only to protect the formula guard's leading <c>'</c> or a
    /// value carrying a <c>"</c>/CR/LF (which WOULD break the one-DN-per-line
    /// structure). The guard runs first, so the <c>'</c> still lands inside the
    /// quotes when both fire (e.g. a leading <c>=</c> + comma).</summary>
    private static string EncodeAppendixCell(string value)
    {
        var guarded = GuardFormulaInjection(value);
        var guardFired = !ReferenceEquals(guarded, value);

        if (guardFired || value.IndexOfAny(LineBreakingQuoteTriggers) >= 0)
        {
            return "\"" + guarded.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
        }

        return guarded;
    }

    /// <summary>#45 formula-injection guard: a single leading <c>'</c> when the
    /// raw cell's first char is one of <c>= + - @ \t \r \n</c> — the characters a
    /// spreadsheet would otherwise interpret as a formula/command lead.</summary>
    private static string GuardFormulaInjection(string value)
    {
        if (value.Length == 0)
        {
            return value;
        }

        return value[0] switch
        {
            '=' or '+' or '-' or '@' or '\t' or '\r' or '\n' => "'" + value,
            _ => value,
        };
    }

    /// <summary>RFC-4180 quoting: wrap in <c>"…"</c> and double embedded <c>"</c>
    /// iff the cell contains a quote, comma, CR, or LF.</summary>
    private static string QuoteRfc4180(string value)
    {
        if (value.IndexOfAny(QuoteTriggers) < 0)
        {
            return value;
        }

        return "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
    }

    private static readonly char[] QuoteTriggers = { '"', ',', '\r', '\n' };

    /// <summary>The subset of quote triggers that would break a single-column
    /// appendix line (comma excluded — see <see cref="EncodeAppendixCell"/>).</summary>
    private static readonly char[] LineBreakingQuoteTriggers = { '"', '\r', '\n' };
}

/// <summary>
/// The HTML report's header block (ADR-013 §2): root identity, the connection
/// summary, and the injected generation timestamp. Kept here so Core carries the
/// whole report type model; <see cref="ViolationReportExporter"/>'s HTML
/// rendering (a later slice) consumes it. The timestamp is injected — never read
/// from the ambient clock — to keep output deterministic under test.
/// </summary>
public sealed record ReportHeader(
    string RootDn,
    string RootName,
    string ConnectionSummary,
    DateTimeOffset GeneratedAt);
