using System.Globalization;
using System.Net;
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

    /// <summary>
    /// Renders the report as a single self-contained HTML file (ADR-013 §2/§3,
    /// #45). No external CSS/JS/font/image references — inline <c>&lt;style&gt;</c>
    /// only — so it opens offline from a mail attachment. The header block carries
    /// the root DN+name, the connection summary, the three severity counts, and the
    /// injected <see cref="ReportHeader.GeneratedAt"/> timestamp (no ambient clock).
    /// Every untrusted token (RuleId, SubjectName, every DN, Message, every
    /// unchecked DN) is escaped via <see cref="WebUtility.HtmlEncode"/> and emitted
    /// ONLY in element text (<c>&lt;td&gt;</c>/<c>&lt;li&gt;</c>), never in an
    /// attribute — so the apostrophe <c>WebUtility.HtmlEncode</c> leaves literal is
    /// safe (no single-quoted attribute carries a token; severity color is
    /// class-keyed CSS, never an inline style with a token). When
    /// <see cref="RuleReport.Violations"/> is empty the all-clear line shows; the
    /// unexpanded-areas banner + list shows iff <see cref="RuleReport.UncheckedDns"/>
    /// is non-empty (independent of whether there are violations — F2). Deterministic
    /// and culture-invariant.
    /// </summary>
    public static string ToHtml(RuleReport report, ResolveName resolveName, ReportHeader header)
    {
        ArgumentNullException.ThrowIfNull(report);
        ArgumentNullException.ThrowIfNull(resolveName);
        ArgumentNullException.ThrowIfNull(header);

        var errorCount = 0;
        var warningCount = 0;
        var infoCount = 0;
        foreach (var violation in report.Violations)
        {
            switch (violation.Severity)
            {
                case RuleSeverity.Error:
                    errorCount++;
                    break;
                case RuleSeverity.Warning:
                    warningCount++;
                    break;
                default:
                    infoCount++;
                    break;
            }
        }

        var sb = new StringBuilder();
        sb.Append("<!DOCTYPE html>").Append(Eol);
        sb.Append("<html lang=\"en\">").Append(Eol);
        sb.Append("<head>").Append(Eol);
        sb.Append("<meta charset=\"utf-8\">").Append(Eol);
        sb.Append("<title>GroupWeaver violation report</title>").Append(Eol);
        sb.Append(StyleBlock).Append(Eol);
        sb.Append("</head>").Append(Eol);
        sb.Append("<body>").Append(Eol);

        // Header block — every dynamic field is escaped element text.
        sb.Append("<h1>GroupWeaver violation report</h1>").Append(Eol);
        sb.Append("<table class=\"meta\">").Append(Eol);
        AppendMetaRow(sb, "Root", Encode(header.RootName) + " (" + Encode(header.RootDn) + ")");
        AppendMetaRow(sb, "Connection", Encode(header.ConnectionSummary));
        AppendMetaRow(sb, "Generated", Encode(FormatTimestamp(header.GeneratedAt)));
        AppendMetaRow(
            sb,
            "Findings",
            FormatCount(errorCount) + " error, " + FormatCount(warningCount) + " warning, "
                + FormatCount(infoCount) + " info");

        // ADR-030 D3 (#188): the honesty caveats travel into the header — the active ruleset name, the
        // triaged count and the unchecked count, so a bare export can never present a clean bill that
        // omits the suppressions or the unexpanded scope. Gated on a non-null RulesetName so a 4-arg
        // ReportHeader (RulesetName == null) yields byte-identical legacy output; the App always passes
        // a non-null name, so these rows always render in production.
        if (header.RulesetName is not null)
        {
            AppendMetaRow(sb, "Ruleset", Encode(header.RulesetName));
            AppendMetaRow(sb, "Triaged", FormatCount(header.TriagedCount) + " findings excluded by triage");
            AppendMetaRow(sb, "Unchecked", FormatCount(header.UncheckedCount) + " unexpanded areas");
        }

        sb.Append("</table>").Append(Eol);

        if (report.Violations.Count == 0)
        {
            sb.Append("<p class=\"all-clear\">No rule violations found.</p>").Append(Eol);
        }
        else
        {
            sb.Append("<table class=\"findings\">").Append(Eol);
            sb.Append("<thead><tr>")
                .Append("<th>Severity</th><th>Rule</th><th>Subject</th>")
                .Append("<th>Primary DN</th><th>DNs</th><th>Message</th>")
                .Append("</tr></thead>").Append(Eol);
            sb.Append("<tbody>").Append(Eol);
            foreach (var violation in report.Violations)
            {
                var primaryDn = violation.PrimaryDn;
                sb.Append("<tr class=\"").Append(SeverityClass(violation.Severity)).Append("\">");
                sb.Append("<td>").Append(Encode(violation.Severity.ToString())).Append("</td>");
                sb.Append("<td>").Append(Encode(violation.RuleId)).Append("</td>");
                sb.Append("<td>").Append(Encode(resolveName(primaryDn))).Append("</td>");
                sb.Append("<td>").Append(Encode(primaryDn)).Append("</td>");
                sb.Append("<td>").Append(Encode(string.Join("; ", violation.Dns))).Append("</td>");
                sb.Append("<td>").Append(Encode(violation.Message)).Append("</td>");
                sb.Append("</tr>").Append(Eol);
            }

            sb.Append("</tbody>").Append(Eol);
            sb.Append("</table>").Append(Eol);
        }

        if (report.UncheckedDns.Count > 0)
        {
            sb.Append("<div class=\"appendix\">").Append(Eol);
            sb.Append("<h2>Unexpanded areas (unchecked)</h2>").Append(Eol);
            sb.Append("<p>These areas were not expanded, so they were not checked.</p>")
                .Append(Eol);
            sb.Append("<ul>").Append(Eol);
            foreach (var dn in report.UncheckedDns)
            {
                sb.Append("<li>").Append(Encode(dn)).Append("</li>").Append(Eol);
            }

            sb.Append("</ul>").Append(Eol);
            sb.Append("</div>").Append(Eol);
        }

        sb.Append("</body>").Append(Eol);
        sb.Append("</html>").Append(Eol);

        return sb.ToString();
    }

    /// <summary>One key/value row of the header metadata table. The pre-escaped
    /// <paramref name="encodedValue"/> is emitted as element text only.</summary>
    private static void AppendMetaRow(StringBuilder sb, string label, string encodedValue) =>
        sb.Append("<tr><th>").Append(label).Append("</th><td>").Append(encodedValue)
            .Append("</td></tr>").Append(Eol);

    /// <summary>Escapes one untrusted token for HTML element text via
    /// <see cref="WebUtility.HtmlEncode"/>: <c>&amp;</c>→<c>&amp;amp;</c>,
    /// <c>&lt;</c>→<c>&amp;lt;</c>, <c>&gt;</c>→<c>&amp;gt;</c>, <c>"</c>→<c>&amp;quot;</c>.
    /// The apostrophe is left literal — safe because tokens are never emitted into an
    /// attribute (F5).</summary>
    private static string Encode(string value) => WebUtility.HtmlEncode(value);

    /// <summary>Maps a severity to its class-keyed CSS hook (palette in
    /// <see cref="StyleBlock"/>); the value goes in a static <c>class</c> attribute,
    /// never an untrusted token.</summary>
    private static string SeverityClass(RuleSeverity severity) => severity switch
    {
        RuleSeverity.Error => "sev-error",
        RuleSeverity.Warning => "sev-warning",
        _ => "sev-info",
    };

    /// <summary>Culture-invariant ISO-8601 timestamp (offset-preserving), so output
    /// is byte-identical under any ambient culture.</summary>
    private static string FormatTimestamp(DateTimeOffset value) =>
        value.ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture);

    private static string FormatCount(int value) =>
        value.ToString(CultureInfo.InvariantCulture);

    /// <summary>The inline stylesheet (no external refs). Severity color is
    /// class-keyed (<c>.sev-error</c>/<c>.sev-warning</c>/<c>.sev-info</c>) using the
    /// pinned ADR-010 palette — never an inline style carrying a token.</summary>
    private const string StyleBlock =
        "<style>\r\n"
        + "body{font-family:Segoe UI,Arial,sans-serif;margin:24px;color:#1b1f27;}\r\n"
        + "h1{font-size:1.4rem;}\r\n"
        + "table{border-collapse:collapse;width:100%;margin-bottom:16px;}\r\n"
        + "table.findings th,table.findings td{border:1px solid #ccc;padding:6px 8px;"
        + "text-align:left;vertical-align:top;}\r\n"
        + "table.meta th{text-align:left;padding:2px 12px 2px 0;width:120px;}\r\n"
        + "thead{background:#eef1f5;}\r\n"
        + "tr.sev-error{border-left:4px solid #D13438;}\r\n"
        + "tr.sev-warning{border-left:4px solid #F7A30B;}\r\n"
        + "tr.sev-info{border-left:4px solid #4FA3E3;}\r\n"
        + ".all-clear{font-weight:600;}\r\n"
        + ".appendix{margin-top:16px;}\r\n"
        + "</style>";

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
///
/// <para>ADR-030 D3 (#188) adds three OPTIONAL honesty fields — the active
/// <see cref="RulesetName"/>, the <see cref="TriagedCount"/> ("N findings excluded
/// by triage") and the <see cref="UncheckedCount"/> ("N unexpanded areas") — so a
/// bare export is self-describing. They default to null/0 (the 4-arg construction
/// remains valid); the exporter renders the three extra header rows ONLY when
/// <see cref="RulesetName"/> is non-null, so a 4-arg header yields byte-identical
/// legacy output.</para>
/// </summary>
public sealed record ReportHeader(
    string RootDn,
    string RootName,
    string ConnectionSummary,
    DateTimeOffset GeneratedAt,
    string? RulesetName = null,
    int TriagedCount = 0,
    int UncheckedCount = 0);
