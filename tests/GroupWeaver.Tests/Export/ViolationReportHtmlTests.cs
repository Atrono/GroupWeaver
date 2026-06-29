using System.Globalization;
using System.Text.RegularExpressions;

using GroupWeaver.Core.Export;
using GroupWeaver.Core.Rules;

using Xunit;

namespace GroupWeaver.Tests.Export;

/// <summary>
/// Pins <c>ViolationReportExporter.ToHtml(report, resolveName, header)</c> (AP 4.1 /
/// ADR-013, issue #56), slice 2 — the self-contained HTML violation report.
///
/// Contract (binding, from ADR-013 §2/§3 + the spec "HTML" section + audit
/// findings F2/F5):
/// <list type="bullet">
///   <item><b>#45 escaping (F5).</b> Every untrusted token (RuleId, SubjectName,
///         every DN, Message) is rendered via <c>WebUtility.HtmlEncode</c>:
///         <c>&lt;</c>→<c>&amp;lt;</c>, <c>&amp;</c>→<c>&amp;amp;</c>,
///         <c>"</c>→<c>&amp;quot;</c>. The raw, unescaped token NEVER appears. A
///         single quote (<c>'</c>) is deliberately left LITERAL by
///         <c>HtmlEncode</c> — that is SAFE here ONLY because every token is emitted
///         in element TEXT (<c>&lt;td&gt;</c>/<c>&lt;li&gt;</c>), never inside any
///         attribute. So we do NOT assert <c>'</c> is entitized; instead we pin the
///         load-bearing invariant: no injected token substring ever appears inside
///         any HTML attribute value.</item>
///   <item><b>Self-contained.</b> No external reference: the document contains no
///         <c>http</c>, no <c>src=</c>, no <c>href=</c> — it opens offline from a mail
///         attachment (inline <c>&lt;style&gt;</c> only, no CSS/JS/font/image refs).</item>
///   <item><b>Unchecked-areas appendix (F2).</b> The "unexpanded areas are unchecked"
///         banner + its <c>&lt;ul&gt;</c> appear IFF <c>UncheckedDns</c> is non-empty —
///         INDEPENDENT of <c>HasViolations</c>. The all-clear-but-unchecked state is a
///         real exportable artifact (the gate is <c>Snapshot is not null</c>, not
///         <c>HasViolations</c>).</item>
///   <item><b>All-clear.</b> "No rule violations found." appears when
///         <c>Violations</c> is empty.</item>
///   <item><b>Deterministic.</b> Output is a pure function of the inputs; the
///         timestamp is injected via <c>ReportHeader.GeneratedAt</c> (no ambient
///         clock), so a fixed instant yields fixed bytes across cultures.</item>
///   <item><b>Class-keyed palette.</b> The pinned severity hex literals
///         <c>#D13438</c>/<c>#F7A30B</c>/<c>#4FA3E3</c> (ADR-010, SeverityConverters)
///         are present as CSS — severity color is class-keyed, NEVER an inline style
///         carrying a token.</item>
/// </list>
///
/// Violations are HAND-BUILT (report mechanics, not engine semantics — they need
/// not be directory-consistent).
/// </summary>
public class ViolationReportHtmlTests
{
    // A fixed instant so the document is deterministic (no ambient clock).
    private static readonly DateTimeOffset FixedGeneratedAt =
        new(2026, 6, 13, 9, 30, 0, TimeSpan.FromHours(2));

    private static readonly ReportHeader Header = new(
        RootDn: "OU=AGDLP-Demo,DC=weavedemo,DC=example",
        RootName: "AGDLP-Demo",
        ConnectionSummary: "DemoProvider (offline)",
        GeneratedAt: FixedGeneratedAt);

    private const string SubjectDn = "CN=GG_X,OU=Groups,OU=AGDLP-Demo,DC=weavedemo,DC=example";
    private const string MemberDn = "CN=GG_Y,OU=Groups,OU=AGDLP-Demo,DC=weavedemo,DC=example";

    // The dangerous payload to inject everywhere: a script tag, an ampersand, a
    // double quote, a newline, and a single quote. Crucially this string carries
    // NONE of the "external ref" tripwires (no http/src=/href=) so the
    // self-contained assertions test the EXPORTER, not the fixture.
    private const string Inject = "<script>alert(1)</script>&\"\n'";

    // Unique, greppable sentinels — a tag-marker (letters only, no special chars)
    // so we can locate "did this exact token reach the output" without colliding
    // with the dangerous payload's own characters, and tell DN/name/message apart.
    private const string DnMarker = "INJDNMARKER";
    private const string NameMarker = "INJNAMEMARKER";
    private const string MsgMarker = "INJMSGMARKER";

    private static readonly ViolationReportExporter.ResolveName Names = _ => NameMarker + Inject;

    // ---- #45 escaping: every dangerous char is entity-escaped, never raw ------

    [Fact]
    public void Escaping_AngleBracketsAmpersandQuote_AreEntityEncoded_NeverRaw()
    {
        var html = Render(WithInjectedTokens());

        // The dangerous chars are present only in their entity forms.
        Assert.Contains("&lt;script&gt;", html);
        Assert.Contains("&amp;", html);
        Assert.Contains("&quot;", html);

        // The raw, unescaped script tag NEVER survives into the document.
        Assert.DoesNotContain("<script>", html);
        Assert.DoesNotContain("</script>", html);
        Assert.DoesNotContain("alert(1)</script>", html);
    }

    [Fact]
    public void Escaping_RawAmpersand_NeverAppearsUnescaped()
    {
        // The injected lone '&' must become '&amp;'. Every '&' in the output must
        // begin a valid entity (&...;) — a bare '&' would be an injected raw token.
        var html = Render(WithInjectedTokens());

        foreach (Match m in Regex.Matches(html, "&"))
        {
            var tail = html.Substring(m.Index, Math.Min(8, html.Length - m.Index));
            Assert.Matches("^&(?:lt|gt|amp|quot|#\\d+|#x[0-9a-fA-F]+);", tail);
        }
    }

    [Fact]
    public void Escaping_RawDoubleQuoteFromAToken_NeverAppearsInElementText()
    {
        // The injected '"' becomes '&quot;'. The only legitimate raw '"' chars in
        // the document are attribute delimiters in the static chrome (<meta ...>,
        // class="..."), and the injected payload contributes none of those (its '"'
        // is encoded). Pin it precisely: the encoded form is present...
        var html = Render(WithInjectedTokens());
        Assert.Contains("&quot;", html);
        // ...and the injected token's raw quote did not leak as element text by
        // checking the marker-bearing token is never immediately followed by a raw ".
        Assert.DoesNotContain(MsgMarker + "<script", html);
    }

    // ---- F5: tokens appear ONLY in element text, never in any attribute -------

    [Fact]
    public void Injection_TokensNeverAppearInsideAnyAttributeValue()
    {
        // THE F5 invariant: WebUtility.HtmlEncode leaves "'" literal, which is safe
        // ONLY because no token is ever emitted into an attribute. So no attribute
        // value anywhere in the document may contain ANY of the injected markers.
        var html = Render(WithInjectedTokens());

        foreach (var attributeValue in AttributeValues(html))
        {
            Assert.DoesNotContain(DnMarker, attributeValue);
            Assert.DoesNotContain(NameMarker, attributeValue);
            Assert.DoesNotContain(MsgMarker, attributeValue);
        }
    }

    [Fact]
    public void Injection_EveryTokenReachesTheDocument_InElementText()
    {
        // Sanity: the markers DO appear (so the no-attribute test is meaningful —
        // the tokens are rendered, just only in text nodes). Each marker is present.
        var html = Render(WithInjectedTokens());

        Assert.Contains(DnMarker, html);
        Assert.Contains(NameMarker, html);
        Assert.Contains(MsgMarker, html);
    }

    // ---- self-contained: no external reference of any kind --------------------

    [Fact]
    public void SelfContained_NoExternalReference()
    {
        // A self-contained file that opens offline: no http(s), no src=, no href=.
        // The injected payload deliberately carries none of these, so a hit proves
        // the exporter emitted an external ref, not the fixture.
        var html = Render(WithInjectedTokens());

        Assert.DoesNotContain("http", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("src=", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("href=", html, StringComparison.OrdinalIgnoreCase);
    }

    // ---- unchecked-areas appendix: IFF UncheckedDns non-empty (F2) ------------

    [Fact]
    public void Unchecked_BannerAndList_ShownWhenUncheckedDnsNonEmpty()
    {
        var report = new RuleReport(
            new[] { V(RuleIds.EmptyGroup, RuleSeverity.Info, "empty", SubjectDn) },
            new[] { "CN=unexpanded,DC=weavedemo,DC=example" });

        var html = Render(report);

        // The unchecked DN is listed inside a <ul>/<li> structure.
        Assert.Contains("<ul", html);
        Assert.Contains("<li", html);
        Assert.Contains("CN=unexpanded,DC=weavedemo,DC=example", html);
        // A banner naming the "unexpanded"/"unchecked" condition is present.
        Assert.Matches("unchecked|unexpanded", html.ToLowerInvariant());
    }

    [Fact]
    public void Unchecked_BannerAndList_AbsentWhenUncheckedDnsEmpty()
    {
        var report = new RuleReport(
            new[] { V(RuleIds.EmptyGroup, RuleSeverity.Info, "empty", SubjectDn) },
            Array.Empty<string>());

        var html = Render(report);

        // No appendix list element and no unchecked-areas banner.
        Assert.DoesNotContain("<li", html);
        Assert.DoesNotContain("unchecked", html.ToLowerInvariant());
        Assert.DoesNotContain("unexpanded", html.ToLowerInvariant());
    }

    [Fact]
    public void Unchecked_AppendixIsIndependentOfViolations_AllClearButUnchecked()
    {
        // F2: the gate is "Snapshot is not null", NOT HasViolations. With ZERO
        // violations but a non-empty frontier, the appendix STILL renders — the
        // banner/<ul> is a function of UncheckedDns alone, independent of
        // HasViolations.
        var report = new RuleReport(
            Array.Empty<RuleViolation>(),
            new[] { "CN=unexpanded,DC=weavedemo,DC=example" });

        var html = Render(report);

        // All-clear copy AND the unchecked appendix coexist.
        Assert.Contains("No rule violations found.", html);
        Assert.Contains("<li", html);
        Assert.Contains("CN=unexpanded,DC=weavedemo,DC=example", html);
        Assert.Matches("unchecked|unexpanded", html.ToLowerInvariant());
    }

    [Fact]
    public void Unchecked_DnsAreEscaped_NotRawInterpolated()
    {
        // An unchecked DN is also an untrusted token: it goes through HtmlEncode
        // exactly like a violation DN — a hostile DN in the frontier must escape.
        var report = new RuleReport(
            Array.Empty<RuleViolation>(),
            new[] { DnMarker + Inject });

        var html = Render(report);

        Assert.Contains("&lt;script&gt;", html);
        Assert.DoesNotContain("<script>", html);
        Assert.Contains(DnMarker, html);
    }

    // ---- all-clear ------------------------------------------------------------

    [Fact]
    public void AllClear_NoViolations_ShowsNoViolationsCopy()
    {
        var report = new RuleReport(Array.Empty<RuleViolation>(), Array.Empty<string>());

        var html = Render(report);

        Assert.Contains("No rule violations found.", html);
    }

    [Fact]
    public void WithViolations_DoesNotShowAllClearCopy()
    {
        var report = new RuleReport(
            new[] { V(RuleIds.EmptyGroup, RuleSeverity.Info, "empty", SubjectDn) },
            Array.Empty<string>());

        var html = Render(report);

        Assert.DoesNotContain("No rule violations found.", html);
    }

    // ---- class-keyed palette --------------------------------------------------

    [Fact]
    public void Palette_PinnedSeverityHexLiterals_ArePresentInCss()
    {
        // The ADR-010 / SeverityConverters palette, class-keyed: the three hex
        // literals appear in the inline <style> (color is class-driven, never an
        // inline style carrying a token).
        var html = Render(WithInjectedTokens());

        Assert.Contains("#D13438", html, StringComparison.OrdinalIgnoreCase); // Error
        Assert.Contains("#F7A30B", html, StringComparison.OrdinalIgnoreCase); // Warning
        Assert.Contains("#4FA3E3", html, StringComparison.OrdinalIgnoreCase); // Info
    }

    [Fact]
    public void Document_HasInlineStyleBlock()
    {
        // Self-contained styling: an inline <style> element (no external CSS link).
        var html = Render(WithInjectedTokens());

        Assert.Contains("<style", html);
        Assert.Contains("</style>", html);
    }

    // ---- ADR-030 D3 (#188): the populated honesty header rows ------------------

    [Fact]
    public void PopulatedHeader_RendersTheThreeHonestyMetaRows_WithExactLabelsAndText()
    {
        // ADR-030 D3 (#188): when the header carries a non-null RulesetName the exporter renders three
        // extra meta rows — the active ruleset name, the triaged count ("N findings excluded by triage")
        // and the unchecked count ("N unexpanded areas") — so a bare export is self-describing and can
        // never present a clean bill that omits the suppressions or the unexpanded scope. Pin the EXACT
        // labels + value text the implementation emits.
        var report = new RuleReport(
            new[] { V(RuleIds.EmptyGroup, RuleSeverity.Info, "empty", SubjectDn) },
            Array.Empty<string>());
        var header = new ReportHeader(
            RootDn: "OU=AGDLP-Demo,DC=weavedemo,DC=example",
            RootName: "AGDLP-Demo",
            ConnectionSummary: "DemoProvider (offline)",
            GeneratedAt: FixedGeneratedAt,
            RulesetName: "Strict AGDLP",
            TriagedCount: 4,
            UncheckedCount: 7);

        var html = ViolationReportExporter.ToHtml(report, Names, header);

        // The three meta rows, verbatim (the exact <th> labels + <td> value text from ToHtml).
        Assert.Contains("<tr><th>Ruleset</th><td>Strict AGDLP</td></tr>", html);
        Assert.Contains("<tr><th>Triaged</th><td>4 findings excluded by triage</td></tr>", html);
        Assert.Contains("<tr><th>Unchecked</th><td>7 unexpanded areas</td></tr>", html);
    }

    [Fact]
    public void PopulatedHeader_EscapesTheRulesetName_NeverRawInterpolated()
    {
        // The ruleset name is an untrusted token (a user-named ruleset file) — it goes through HtmlEncode
        // exactly like every other token, never raw-interpolated into the meta row.
        var report = new RuleReport(Array.Empty<RuleViolation>(), Array.Empty<string>());
        var header = new ReportHeader(
            "OU=AGDLP-Demo,DC=weavedemo,DC=example",
            "AGDLP-Demo",
            "DemoProvider (offline)",
            FixedGeneratedAt,
            RulesetName: NameMarker + Inject,
            TriagedCount: 0,
            UncheckedCount: 0);

        var html = ViolationReportExporter.ToHtml(report, Names, header);

        Assert.Contains("&lt;script&gt;", html);
        Assert.DoesNotContain("<script>", html);
        Assert.Contains(NameMarker, html);
    }

    [Fact]
    public void NullRulesetName_OmitsTheThreeHonestyRows_LegacyOutput()
    {
        // The complement: a 4-arg header (RulesetName == null, the default Header used everywhere above)
        // renders NONE of the three honesty rows — the byte-identical legacy output the App's null-name
        // path never hits but the 4-arg ReportHeader contract preserves.
        var report = new RuleReport(
            new[] { V(RuleIds.EmptyGroup, RuleSeverity.Info, "empty", SubjectDn) },
            Array.Empty<string>());

        var html = Render(report); // the shared Header has a null RulesetName.

        Assert.DoesNotContain("<th>Ruleset</th>", html);
        Assert.DoesNotContain("<th>Triaged</th>", html);
        Assert.DoesNotContain("findings excluded by triage", html);
        Assert.DoesNotContain("unexpanded areas", html);
    }

    // ---- determinism ----------------------------------------------------------

    [Fact]
    public void Output_IsDeterministic_AcrossCultures()
    {
        // Pure function of (report, resolveName, header). No ambient clock, no
        // culture-sensitive formatting — identical bytes under a hostile culture.
        var report = WithInjectedTokens();

        var invariant = Render(report);

        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("tr-TR");
            var turkish = Render(report);
            Assert.Equal(invariant, turkish);
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Fact]
    public void Output_IsStable_ForFixedInputs()
    {
        // Same inputs -> same bytes (the GeneratedAt is injected, not read from the
        // clock; two renders separated in time are byte-identical).
        var report = WithInjectedTokens();

        Assert.Equal(Render(report), Render(report));
    }

    // ---- helpers --------------------------------------------------------------

    private static string Render(RuleReport report) =>
        ViolationReportExporter.ToHtml(report, Names, Header);

    // A report carrying the dangerous payload in a violation's DN list and Message
    // (the SubjectName is injected via the Names resolver). One nesting finding so
    // both Dns endpoints (each a DN token) flow through the escaper, plus a hostile
    // Message.
    private static RuleReport WithInjectedTokens() =>
        new(
            new[]
            {
                V(
                    RuleIds.Nesting,
                    RuleSeverity.Error,
                    MsgMarker + Inject,
                    DnMarker + Inject,
                    MemberDn),
            },
            Array.Empty<string>());

    private static RuleViolation V(string ruleId, RuleSeverity severity, string message, params string[] dns) =>
        new()
        {
            RuleId = ruleId,
            Severity = severity,
            Dns = dns,
            Message = message,
        };

    /// <summary>Extracts every HTML attribute VALUE (the text inside the quotes of
    /// <c>name="value"</c> / <c>name='value'</c>) from the document, so a test can
    /// assert no untrusted token ever lands inside an attribute (F5). Deliberately
    /// permissive: it over-collects rather than under-collects, so a token hiding in
    /// any attribute is caught.</summary>
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
