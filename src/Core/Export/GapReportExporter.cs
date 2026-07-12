using System.Globalization;
using System.Net;
using System.Text;

using GroupWeaver.Core.Diff;

namespace GroupWeaver.Core.Export;

/// <summary>
/// Serializes a <see cref="GapReport"/> (+ its <see cref="GapSummary"/>) to a downloadable
/// gap-diff report — the Plan-vs-Ist counterpart of <see cref="ViolationReportExporter"/>
/// (ADR-015 / #66). Pure Core: UI-free and App-type-free — caller-side concerns (the picked
/// file path, UTF-8 byte writing, the name lookup) are injected via the
/// <see cref="ViolationReportExporter.ResolveName"/> delegate and (for HTML) the
/// <see cref="DiffReportHeader"/> value. Deterministic: no ambient time, no culture
/// sensitivity (ordinal string ops throughout; gap kinds render as enum NAMES, not localized
/// strings; the injected header timestamp is the sole time source).
///
/// <para>The CSV escaping discipline mirrors <see cref="ViolationReportExporter"/> exactly —
/// guard-before-quote (the formula-injection guard, then RFC-4180 quoting, so the leading
/// <c>'</c> lands inside the quotes) — with a small private copy of the two guard helpers so
/// the existing exporter's byte output (and its golden tests) stay untouched. HTML escapes
/// every untrusted token via <see cref="WebUtility.HtmlEncode"/> and emits it ONLY in element
/// text, never an attribute.</para>
/// </summary>
public static class GapReportExporter
{
    private const string Eol = "\r\n";

    /// <summary>The single leading U+FEFF the CSV string carries (#329 defect 2,
    /// the <see cref="ViolationReportExporter"/> shared discipline): it travels IN
    /// the string, so any UTF-8 byte writer — including the App's BOM-less
    /// <c>UTF8Encoding(false)</c> writers — produces a file starting <c>EF BB BF</c>
    /// and Excel double-click decodes UTF-8, not ANSI. CSV-only; HTML stays
    /// BOM-less (its <c>meta charset</c> is the declaration).</summary>
    private const char Bom = (char)0xFEFF;

    /// <summary>The fixed CSV header row (pinned by test).</summary>
    private const string CsvHeader = "Kind,SubjectName,PrimaryDn,SecondaryDn,Message";

    /// <summary>
    /// Renders the gap report as RFC-4180 CSV. The leading <see cref="Bom"/>, the fixed header
    /// row, then one row per finding
    /// in <see cref="GapReport.Findings"/> canonical order VERBATIM (never re-sorted). Each row
    /// is <c>Kind,SubjectName,PrimaryDn,SecondaryDn,Message</c>: <c>Kind</c> is the
    /// <see cref="GapKind"/> NAME; <c>SubjectName</c> is <c>resolveName(Dns[0])</c>;
    /// <c>PrimaryDn</c> is <c>Dns[0]</c>; <c>SecondaryDn</c> is <c>Dns[1]</c> for edge findings
    /// (else empty); <c>Message</c> is verbatim. Every cell is encoded GUARD-BEFORE-QUOTE: the
    /// formula-injection guard prefixes a single <c>'</c> when the raw cell leads with
    /// <c>= + - @ \t \r \n</c>, then RFC-4180 quoting wraps cells that contain
    /// <c>" , \r \n</c> (so the <c>'</c> lands inside the quotes). Line endings are CRLF,
    /// including a trailing CRLF after the last line. The <paramref name="summary"/> is unused
    /// for CSV (the per-finding rows carry the whole diff); it is accepted for signature parity
    /// with <see cref="ToHtml"/> so callers build one header/summary pair for both formats.
    /// </summary>
    public static string ToCsv(GapReport report, GapSummary summary, ViolationReportExporter.ResolveName resolveName)
    {
        ArgumentNullException.ThrowIfNull(report);
        ArgumentNullException.ThrowIfNull(summary);
        ArgumentNullException.ThrowIfNull(resolveName);

        var sb = new StringBuilder();
        sb.Append(Bom).Append(CsvHeader).Append(Eol);

        foreach (var finding in report.Findings)
        {
            var primaryDn = finding.Dns[0];
            var secondaryDn = finding.Dns.Count > 1 ? finding.Dns[1] : string.Empty;
            AppendCsvRow(
                sb,
                finding.Kind.ToString(),
                resolveName(primaryDn),
                primaryDn,
                secondaryDn,
                finding.Message);
        }

        return sb.ToString();
    }

    /// <summary>
    /// Renders the gap report as a single self-contained HTML file (mirrors
    /// <see cref="ViolationReportExporter.ToHtml"/>). No external CSS/JS/font/image references —
    /// inline <c>&lt;style&gt;</c> only — so it opens offline from a mail attachment. The header
    /// block carries the root DN+name, the connection summary, the injected
    /// <see cref="DiffReportHeader.GeneratedAt"/> timestamp (no ambient clock), and the
    /// <see cref="GapSummary"/> counts as honesty rows (added/removed nodes &amp; edges, plus the
    /// unchecked-areas tally — so a bare export can never hide that some Ist areas were never
    /// expanded). Every untrusted token (Kind, SubjectName, every DN, Message) is escaped via
    /// <see cref="WebUtility.HtmlEncode"/> and emitted ONLY in element text
    /// (<c>&lt;td&gt;</c>), never in an attribute. When <see cref="GapReport.Findings"/> is empty
    /// the no-differences line shows. Deterministic and culture-invariant.
    /// </summary>
    public static string ToHtml(
        GapReport report, GapSummary summary, ViolationReportExporter.ResolveName resolveName, DiffReportHeader header)
    {
        ArgumentNullException.ThrowIfNull(report);
        ArgumentNullException.ThrowIfNull(summary);
        ArgumentNullException.ThrowIfNull(resolveName);
        ArgumentNullException.ThrowIfNull(header);

        var sb = new StringBuilder();
        sb.Append("<!DOCTYPE html>").Append(Eol);
        sb.Append("<html lang=\"en\">").Append(Eol);
        sb.Append("<head>").Append(Eol);
        sb.Append("<meta charset=\"utf-8\">").Append(Eol);
        sb.Append("<title>GroupWeaver gap report</title>").Append(Eol);
        sb.Append(StyleBlock).Append(Eol);
        sb.Append("</head>").Append(Eol);
        sb.Append("<body>").Append(Eol);

        // Header block — every dynamic field is escaped element text.
        sb.Append("<h1>GroupWeaver gap report</h1>").Append(Eol);
        sb.Append("<table class=\"meta\">").Append(Eol);
        AppendMetaRow(sb, "Root", Encode(header.RootName) + " (" + Encode(header.RootDn) + ")");
        AppendMetaRow(sb, "Connection", Encode(header.ConnectionSummary));
        AppendMetaRow(sb, "Generated", Encode(FormatTimestamp(header.GeneratedAt)));

        // GapSummary honesty rows: the per-status node/edge deltas plus the unchecked tally, so the
        // export is self-describing (the unchecked count is the load-state honesty caveat — ADR-015
        // D5: unexpanded Ist areas were never compared, never silently counted as a match).
        AppendMetaRow(
            sb,
            "Nodes",
            FormatCount(summary.AddedNodes) + " added, " + FormatCount(summary.RemovedNodes) + " removed");
        AppendMetaRow(
            sb,
            "Edges",
            FormatCount(summary.AddedEdges) + " added, " + FormatCount(summary.RemovedEdges) + " removed");
        // #329 (the gap-9 defect class): "area" pluralizes on the parent count — singular at
        // exactly one, plural at zero and N>1.
        AppendMetaRow(
            sb,
            "Unchecked",
            Plural(summary.UncheckedParents, "unexpanded current-structure area") + " not compared");

        sb.Append("</table>").Append(Eol);

        if (report.Findings.Count == 0)
        {
            sb.Append("<p class=\"all-clear\">No differences — the plan matches the current structure.</p>")
                .Append(Eol);
        }
        else
        {
            sb.Append("<table class=\"findings\">").Append(Eol);
            sb.Append("<thead><tr>")
                .Append("<th scope=\"col\">Kind</th><th scope=\"col\">Subject</th>")
                .Append("<th scope=\"col\">Primary DN</th><th scope=\"col\">Secondary DN</th>")
                .Append("<th scope=\"col\">Message</th>")
                .Append("</tr></thead>").Append(Eol);
            sb.Append("<tbody>").Append(Eol);
            foreach (var finding in report.Findings)
            {
                var primaryDn = finding.Dns[0];
                var secondaryDn = finding.Dns.Count > 1 ? finding.Dns[1] : string.Empty;
                sb.Append("<tr class=\"").Append(KindClass(finding.Kind)).Append("\">");
                sb.Append("<td>").Append(Encode(finding.Kind.ToString())).Append("</td>");
                sb.Append("<td>").Append(Encode(resolveName(primaryDn))).Append("</td>");
                sb.Append("<td>").Append(Encode(primaryDn)).Append("</td>");
                sb.Append("<td>").Append(Encode(secondaryDn)).Append("</td>");
                sb.Append("<td>").Append(Encode(finding.Message)).Append("</td>");
                sb.Append("</tr>").Append(Eol);
            }

            sb.Append("</tbody>").Append(Eol);
            sb.Append("</table>").Append(Eol);
        }

        sb.Append("</body>").Append(Eol);
        sb.Append("</html>").Append(Eol);

        return sb.ToString();
    }

    /// <summary>One key/value row of the header metadata table. The pre-escaped
    /// <paramref name="encodedValue"/> is emitted as element text only. The
    /// <c>&lt;th&gt;</c> is a ROW header — <c>scope="row"</c> (WCAG 1.3.1 / H63, #329).</summary>
    private static void AppendMetaRow(StringBuilder sb, string label, string encodedValue) =>
        sb.Append("<tr><th scope=\"row\">").Append(label).Append("</th><td>").Append(encodedValue)
            .Append("</td></tr>").Append(Eol);

    /// <summary>Escapes one untrusted token for HTML element text via
    /// <see cref="WebUtility.HtmlEncode"/>. The apostrophe is left literal — safe because tokens
    /// are never emitted into an attribute.</summary>
    private static string Encode(string value) => WebUtility.HtmlEncode(value);

    /// <summary>Maps a gap kind to its class-keyed CSS hook (palette in
    /// <see cref="StyleBlock"/>); the value goes in a static <c>class</c> attribute, never an
    /// untrusted token.</summary>
    private static string KindClass(GapKind kind) => kind switch
    {
        GapKind.NodeAdded or GapKind.EdgeAdded => "gap-added",
        GapKind.NodeRemoved or GapKind.EdgeRemoved => "gap-removed",
        _ => "gap-unchecked",
    };

    /// <summary>Culture-invariant ISO-8601 timestamp (offset-preserving), so output is
    /// byte-identical under any ambient culture.</summary>
    private static string FormatTimestamp(DateTimeOffset value) =>
        value.ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture);

    private static string FormatCount(int value) =>
        value.ToString(CultureInfo.InvariantCulture);

    /// <summary>Culture-invariant "N noun(s)": the trailing <c>s</c> appended to the
    /// whole phrase iff <paramref name="count"/> ≠ 1 (#329 pluralization).</summary>
    private static string Plural(int count, string noun) =>
        FormatCount(count) + " " + noun + (count == 1 ? string.Empty : "s");

    /// <summary>The inline stylesheet (no external refs). Kind color is class-keyed
    /// (<c>.gap-added</c>/<c>.gap-removed</c>/<c>.gap-unchecked</c>) — never an inline style
    /// carrying a token. #329: a single-theme LIGHT artifact, declared via
    /// <c>color-scheme:light</c>, and the body ink pairs an explicit background (WCAG F24).</summary>
    private const string StyleBlock =
        "<style>\r\n"
        + "body{font-family:Segoe UI,Arial,sans-serif;margin:24px;color:#1b1f27;"
        + "background:#ffffff;color-scheme:light;}\r\n"
        + "h1{font-size:1.4rem;}\r\n"
        + "table{border-collapse:collapse;width:100%;margin-bottom:16px;}\r\n"
        + "table.findings th,table.findings td{border:1px solid #ccc;padding:6px 8px;"
        + "text-align:left;vertical-align:top;}\r\n"
        + "table.meta th{text-align:left;padding:2px 12px 2px 0;width:120px;}\r\n"
        + "thead{background:#eef1f5;}\r\n"

        // The canonical DIFF palette, mirrored by hand (Core cannot reference App — the
        // Tokens.axaml discipline): BrandTokens.AddedHex/RemovedHex/UncheckedHex, the
        // graph bundle's diff cues, and GapKindConverters all pin these same hexes.
        + "tr.gap-added{border-left:4px solid #2FAE4E;}\r\n"
        + "tr.gap-removed{border-left:4px solid #E0503A;}\r\n"
        + "tr.gap-unchecked{border-left:4px solid #8A8F98;}\r\n"
        + ".all-clear{font-weight:600;}\r\n"
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

    /// <summary>Guard-before-quote (pinned order) for a row cell: formula-injection guard, then
    /// RFC-4180 quoting — so the <c>'</c> lands inside the quotes. A small private copy of the
    /// <see cref="ViolationReportExporter"/> discipline (kept private so that exporter's golden
    /// byte output is untouched).</summary>
    private static string EncodeCell(string value) => QuoteRfc4180(GuardFormulaInjection(value));

    /// <summary>Formula-injection guard: a single leading <c>'</c> when the raw cell's first char
    /// is one of <c>= + - @ \t \r \n</c> — the characters a spreadsheet would otherwise interpret
    /// as a formula/command lead.</summary>
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

    /// <summary>RFC-4180 quoting: wrap in <c>"…"</c> and double embedded <c>"</c> iff the cell
    /// contains a quote, comma, CR, or LF.</summary>
    private static string QuoteRfc4180(string value)
    {
        if (value.IndexOfAny(QuoteTriggers) < 0)
        {
            return value;
        }

        return "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
    }

    private static readonly char[] QuoteTriggers = { '"', ',', '\r', '\n' };
}

/// <summary>
/// The gap HTML report's header block (ADR-015 / #66): root identity, the connection summary,
/// and the injected generation timestamp — the diff counterpart of <see cref="ReportHeader"/>.
/// The timestamp is injected — never read from the ambient clock — to keep
/// <see cref="GapReportExporter.ToHtml"/> deterministic under test.
/// </summary>
/// <param name="RootDn">The scope root's distinguished name.</param>
/// <param name="RootName">The scope root's friendly display name.</param>
/// <param name="ConnectionSummary">The active connection's one-line summary.</param>
/// <param name="GeneratedAt">The injected generation instant (no ambient clock).</param>
public sealed record DiffReportHeader(
    string RootDn,
    string RootName,
    string ConnectionSummary,
    DateTimeOffset GeneratedAt);
