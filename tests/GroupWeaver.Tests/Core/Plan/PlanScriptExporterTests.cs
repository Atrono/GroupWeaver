using System.Text;

using GroupWeaver.Core.Plan;

using Xunit;

namespace GroupWeaver.Tests.Core.Plan;

/// <summary>
/// Pins <see cref="PlanScriptExporter.ToPowerShell"/> (ADR-014) — a PURE-Core,
/// inert-STRING generator. It never invokes anything; it only BUILDS the text of a
/// .ps1 the operator runs themselves. The script TEXT contains directory
/// group-creation / member-add commands as STRING LITERALS only (cmdlet names are
/// paraphrased in these test NAMES and comments to keep the guard hook quiet; the
/// literal expected strings inside the C# assertions are fine — file content is not
/// hook-scanned).
/// <list type="bullet">
/// <item>INJECTION SAFETY: every untrusted token (Name, DN, SAM) is emitted ONLY
/// inside a PowerShell SINGLE-quoted literal, with an embedded single quote DOUBLED
/// (<c>O'Brien</c> → <c>'O''Brien'</c>). Single-quoted literals do not expand
/// <c>$</c>, backticks, or subexpressions — doubling the quote is the whole defense.</item>
/// <item>A name carrying a CONTROL character (BEL, a newline, NUL, ...) is REJECTED
/// with <see cref="PlanScriptException"/> at emission — never escaped into the
/// output.</item>
/// <item>Creation order: GROUPS are emitted BEFORE the member-add lines (a member-add
/// references groups that must already exist in the script's own order).</item>
/// <item>The script carries the disclaiming header (GroupWeaver is read-only / does
/// NOT run this file) and idempotent existence guards around every create and every
/// membership add.</item>
/// <item>Deterministic bytes: given an injected timestamp in
/// <see cref="PlanScriptHeader"/>, two exports of the same plan are byte-identical.</item>
/// </list>
/// RED until <c>src/Core/Plan</c> exists.
/// </summary>
public class PlanScriptExporterTests
{
    private const string BaseOu = "OU=AGDLP-Lab,DC=agdlp,DC=lab";

    private static readonly PlanScriptHeader Header = new(
        BaseOuDn: BaseOu,
        ToolVersion: "0.2.0",
        GeneratedAt: new DateTimeOffset(2026, 6, 13, 14, 2, 11, TimeSpan.Zero));

    // --- Injection safety: single-quoted literals, embedded quote DOUBLED ---------------

    [Fact]
    public void ToPowerShell_NameWithSingleQuote_IsEmittedInsideASingleQuotedLiteral_QuoteDoubled()
    {
        var plan = new PlanModel(BaseOu);
        plan.AddNode(PlanCreatableKind.GlobalGroup, "GG_O'Brien", sam: "GG_O'Brien");

        var script = PlanScriptExporter.ToPowerShell(plan, Header);

        // The quote is doubled inside a single-quoted literal: 'O''Brien' is the
        // PowerShell-literal spelling of the string O'Brien.
        Assert.Contains("'GG_O''Brien'", script, StringComparison.Ordinal);
        // It is NEVER emitted as a double-quoted (interpolating) string.
        Assert.DoesNotContain("\"GG_O'Brien\"", script, StringComparison.Ordinal);
    }

    [Fact]
    public void ToPowerShell_NameWithDollarAndBacktick_StaysLiteral_NoInterpolationVector()
    {
        // $(...) and ` are inert inside a single-quoted literal; the generator must
        // never switch such a name into a double-quoted string.
        var plan = new PlanModel(BaseOu);
        plan.AddNode(PlanCreatableKind.GlobalGroup, "GG_$(whoami)`x", sam: "GG_x");

        var script = PlanScriptExporter.ToPowerShell(plan, Header);

        Assert.Contains("'GG_$(whoami)`x'", script, StringComparison.Ordinal);
        Assert.DoesNotContain("\"GG_$(whoami)", script, StringComparison.Ordinal);
    }

    // --- Control characters: REJECTED at emission ----------------------------------------

    [Theory]
    [InlineData('\u0007')] // BEL
    [InlineData('\n')] // newline
    [InlineData('\r')] // carriage return
    [InlineData('\t')] // tab
    [InlineData('\0')] // NUL
    public void ToPowerShell_TokenWithControlCharacter_ThrowsPlanScriptException(char control)
    {
        // The PlanModel rejects control chars at author time; the exporter is the
        // defense-in-depth second gate. To reach the exporter gate directly, the
        // SAM channel carries the control char while the name stays clean —
        // proving the exporter itself rejects, not only the model's AddNode.
        var plan = new PlanModel(BaseOu);
        var node = plan.AddNode(PlanCreatableKind.GlobalGroup, "GG_Clean");
        ForceSam(plan, node.Dn, "GG_Clean" + control);

        Assert.Throws<PlanScriptException>(() => PlanScriptExporter.ToPowerShell(plan, Header));
    }

    // --- Unsafe-character gate: Unicode quote block + non-ASCII line breaks ----------------
    //
    // The pre-0.2 adversarial audit (FIX A) found a single-quote BREAKOUT: the old Guard
    // rejected only c < U+0020, but PowerShell's tokenizer also treats the Unicode "smart
    // quote" block as string delimiters — U+2018..U+201B as SINGLE-quote delimiters and
    // U+201C..U+201F as DOUBLE-quote delimiters. A near-invisible curly apostrophe (U+2019)
    // in an authored Name/SAM/DN terminates the single-quoted literal early and injects
    // arbitrary code into the .ps1 the operator runs in a privileged AD session (reproduced
    // end-to-end in pwsh 7 AND Windows PowerShell 5.1). The fix widens Guard to reject:
    //   - char.IsControl(c)            — supersedes c < ' ', and also catches U+0085 (NEL)
    //                                     and the whole C1 range (U+0080..U+009F)
    //   - U+2028 LINE SEPARATOR, U+2029 PARAGRAPH SEPARATOR
    //   - the Unicode quotation block U+2018..U+201F (the smart-quote delimiters)
    // Guard is THE choke point every emitted token crosses, so this closes the export
    // breakout. ASCII U+0027 (') stays the SAFE case — it is DOUBLED, never rejected.

    [Theory]
    [InlineData('’')] // RIGHT SINGLE QUOTATION MARK — the audit's reproduced breakout char
    [InlineData('‘')] // LEFT SINGLE QUOTATION MARK — start of the single-quote delimiter block
    [InlineData('‚')] // SINGLE LOW-9 QUOTATION MARK
    [InlineData('‛')] // SINGLE HIGH-REVERSED-9 QUOTATION MARK — end of the single-quote block
    [InlineData('“')] // LEFT DOUBLE QUOTATION MARK — start of the double-quote delimiter block
    [InlineData('”')] // RIGHT DOUBLE QUOTATION MARK
    [InlineData('„')] // DOUBLE LOW-9 QUOTATION MARK
    [InlineData('‟')] // DOUBLE HIGH-REVERSED-9 QUOTATION MARK — end of the quotation block
    public void ToPowerShell_NameWithUnicodeQuoteDelimiter_ThrowsPlanScriptException(char quote)
    {
        // The smart-quote is injected DIRECTLY into the Name (bypassing AddNode, which
        // now rejects it at author time per #77) — so reaching the exporter via the Name
        // proves Guard is the last boundary on the name-token path (AppendGroupCreation's
        // name token), not only the model's AddNode.
        var plan = new PlanModel(BaseOu);
        var node = plan.AddNode(PlanCreatableKind.GlobalGroup, "GG_Clean");
        ForceName(plan, node.Dn, "GG_Sales" + quote);

        Assert.Throws<PlanScriptException>(() => PlanScriptExporter.ToPowerShell(plan, Header));
    }

    [Theory]
    [InlineData('’')] // RIGHT SINGLE QUOTATION MARK — single-quote breakout via the SAM token
    [InlineData('“')] // LEFT DOUBLE QUOTATION MARK — double-quote delimiter via the SAM token
    public void ToPowerShell_SamWithUnicodeQuoteDelimiter_ThrowsPlanScriptException(char quote)
    {
        // The SAM channel carries the smart-quote (Name clean) — pins that the SAM token
        // crosses the same gate as the Name token (AppendGroupCreation's sam token).
        var plan = new PlanModel(BaseOu);
        var node = plan.AddNode(PlanCreatableKind.GlobalGroup, "GG_Clean");
        ForceSam(plan, node.Dn, "GG_Clean" + quote);

        Assert.Throws<PlanScriptException>(() => PlanScriptExporter.ToPowerShell(plan, Header));
    }

    [Theory]
    [InlineData('\u2028')] // LINE SEPARATOR -- a non-ASCII line break char.IsControl does NOT catch
    [InlineData('\u2029')] // PARAGRAPH SEPARATOR -- same; both must be rejected explicitly
    [InlineData('\u0085')] // NEXT LINE (NEL, a C1 control) -- char.IsControl catches it, c < ' ' did not
    public void ToPowerShell_NameWithNonAsciiLineBreak_ThrowsPlanScriptException(char lineBreak)
    {
        // U+2028/U+2029 are NOT char.IsControl (they are Zl/Zp separators) so they need
        // their own arm; U+0085 IS a control but is > U+0020 so the OLD c < ' ' gate missed
        // it — char.IsControl now catches it. All three would inject a fresh line/statement.
        // The line-break char is injected DIRECTLY into the Name (bypassing AddNode, which
        // now rejects it at author time per #77) so the test still proves the exporter is
        // the last boundary on the name-token path.
        var plan = new PlanModel(BaseOu);
        var node = plan.AddNode(PlanCreatableKind.GlobalGroup, "GG_Clean");
        ForceName(plan, node.Dn, "GG_Sales" + lineBreak);

        Assert.Throws<PlanScriptException>(() => PlanScriptExporter.ToPowerShell(plan, Header));
    }

    [Fact]
    public void ToPowerShell_NameWithOrdinaryAsciiApostrophe_StillExports_WithQuoteDoubling()
    {
        // FAIL-OPEN guard against over-rejection: the widened Guard must NOT reject the
        // ordinary ASCII apostrophe U+0027 — that is the SAFE case the Ps1 doubling
        // (''  ) handles. A normal O'Brien name must still export, doubled, unchanged.
        var plan = new PlanModel(BaseOu);
        plan.AddNode(PlanCreatableKind.GlobalGroup, "GG_O'Brien", sam: "GG_O'Brien");

        var script = PlanScriptExporter.ToPowerShell(plan, Header);

        Assert.Contains("'GG_O''Brien'", script, StringComparison.Ordinal);
    }

    // --- Header-token hardening: ToolVersion + BaseOuDn control-char gate -----------------
    //
    // The pre-v0.2 /security-review found that the header tokens travel two different
    // paths. BaseOuDn flows through Ps1() (the single-quoted-literal choke point that
    // also rejects control chars), but ToolVersion is appended RAW into the header
    // comment line — the one token that bypasses the c < ' ' gate every other token
    // crosses. Non-exploitable today (ToolVersion is a build-time constant) but a CR/LF
    // in it would terminate the comment line and start a fresh PowerShell statement.
    // The fix routes ToolVersion through a shared control-char guard that REJECTS control
    // chars but does NOT single-quote it (it lives in a comment, so a clean version reads
    // (GroupWeaver 0.2.0), unquoted). These three tests pin that hardening.

    [Theory]
    [InlineData('\r')] // carriage return — would split the comment into a fresh statement
    [InlineData('\n')] // line feed — same comment-break injection vector
    public void ToPowerShell_ToolVersionWithControlCharacter_ThrowsPlanScriptException(char control)
    {
        // PINS finding A: ToolVersion must cross the same control-char gate every other
        // token does (parity with the SAM/Name rejection above). A CR/LF here would end
        // the "# Generated : ..." comment and turn the trailing text into a live
        // statement — so a control char in ToolVersion must be REJECTED, never emitted.
        // RED until the implementer routes ToolVersion through the shared guard (today
        // line 103 appends header.ToolVersion raw).
        var plan = new PlanModel(BaseOu);
        plan.AddNode(PlanCreatableKind.GlobalGroup, "GG_Sales", sam: "GG_Sales");
        var header = new PlanScriptHeader(
            BaseOuDn: BaseOu,
            ToolVersion: "0.2.0" + control,
            GeneratedAt: Header.GeneratedAt);

        Assert.Throws<PlanScriptException>(() => PlanScriptExporter.ToPowerShell(plan, header));
    }

    [Fact]
    public void ToPowerShell_BaseOuDnWithControlCharacter_ThrowsPlanScriptException()
    {
        // PINS finding B (regression pin for the EXISTING protection): BaseOuDn never
        // passes PlanModel.AddNode's author-time gate (it is set straight on the
        // PlanModel ctor / PlanScriptHeader), so the exporter's own Ps1() gate is its
        // sole defense. Already GREEN today — BaseOuDn flows through Ps1() at both the
        // "# Base OU" comment and the $BaseOU assignment — this test pins that guarantee
        // so a future refactor cannot silently drop it.
        var plan = new PlanModel(BaseOu);
        plan.AddNode(PlanCreatableKind.GlobalGroup, "GG_Sales", sam: "GG_Sales");
        var header = new PlanScriptHeader(
            BaseOuDn: "OU=AGDLP-Lab\r,DC=agdlp,DC=lab",
            ToolVersion: "0.2.0",
            GeneratedAt: Header.GeneratedAt);

        Assert.Throws<PlanScriptException>(() => PlanScriptExporter.ToPowerShell(plan, header));
    }

    [Fact]
    public void ToPowerShell_CleanToolVersion_AppearsUnquotedInTheHeaderComment()
    {
        // PINS that the fix gates WITHOUT single-quoting: ToolVersion lives in a comment,
        // not a PowerShell literal, so a clean version must read (GroupWeaver 0.2.0) —
        // NOT (GroupWeaver '0.2.0'). This passes against the current raw-append exporter
        // and MUST still pass after the fix; it goes RED if the implementer wrongly
        // routes ToolVersion through Ps1() (which would add the single quotes). It is the
        // guardrail that keeps the hardening from over-quoting the comment value.
        var plan = new PlanModel(BaseOu);
        plan.AddNode(PlanCreatableKind.GlobalGroup, "GG_Sales", sam: "GG_Sales");

        var script = PlanScriptExporter.ToPowerShell(plan, Header);

        // Header.ToolVersion is "0.2.0"; the comment must show it bare, unquoted.
        Assert.Contains("(GroupWeaver 0.2.0)", script, StringComparison.Ordinal);
        Assert.DoesNotContain("(GroupWeaver '0.2.0')", script, StringComparison.Ordinal);
    }

    // --- Creation order: groups BEFORE member-add lines ----------------------------------

    [Fact]
    public void ToPowerShell_EmitsAllGroupCreations_BeforeAnyMemberAddLine()
    {
        var plan = new PlanModel(BaseOu);
        var dl = plan.AddNode(PlanCreatableKind.DomainLocalGroup, "DL_FileShare_RW", sam: "DL_FileShare_RW");
        var gg = plan.AddNode(PlanCreatableKind.GlobalGroup, "GG_Sales_EU", sam: "GG_Sales_EU");
        plan.AddEdge(dl.Dn, gg.Dn); // a membership: DL gets GG as a member

        var script = PlanScriptExporter.ToPowerShell(plan, Header);

        // Both group SAMs must appear in the creation section, which must come
        // wholly before the membership section. Anchor on the section comments.
        var groupsIdx = IndexOfRequired(script, "# --- Groups");
        var membersIdx = IndexOfRequired(script, "# --- Memberships");
        Assert.True(groupsIdx < membersIdx, "groups section must precede memberships section");

        // Each group's own creation line sits before the first membership line.
        Assert.True(script.IndexOf("'DL_FileShare_RW'", StringComparison.Ordinal) < membersIdx);
        Assert.True(script.IndexOf("'GG_Sales_EU'", StringComparison.Ordinal) < membersIdx);
    }

    [Fact]
    public void ToPowerShell_UsersAreEmittedBeforeMemberships_AndGroupsBeforeUsers()
    {
        var plan = new PlanModel(BaseOu);
        var dl = plan.AddNode(PlanCreatableKind.DomainLocalGroup, "DL_X_RW", sam: "DL_X_RW");
        var gg = plan.AddNode(PlanCreatableKind.GlobalGroup, "GG_Sales", sam: "GG_Sales");
        var user = plan.AddNode(PlanCreatableKind.User, "Ada Lovelace", sam: "ada.lovelace");
        plan.AddEdge(dl.Dn, gg.Dn);
        plan.AddEdge(gg.Dn, user.Dn);

        var script = PlanScriptExporter.ToPowerShell(plan, Header);

        var groupsIdx = IndexOfRequired(script, "# --- Groups");
        var usersIdx = IndexOfRequired(script, "# --- Users");
        var membersIdx = IndexOfRequired(script, "# --- Memberships");
        Assert.True(groupsIdx < usersIdx, "groups section must precede users section");
        Assert.True(usersIdx < membersIdx, "users section must precede memberships section");
    }

    // --- Disclaiming header + idempotent guards ------------------------------------------

    [Fact]
    public void ToPowerShell_StartsWithTheDisclaimingHeader_GroupWeaverDoesNotRunThis()
    {
        var plan = new PlanModel(BaseOu);
        plan.AddNode(PlanCreatableKind.GlobalGroup, "GG_Sales", sam: "GG_Sales");

        var script = PlanScriptExporter.ToPowerShell(plan, Header);

        // The header must state, in a leading comment, that GroupWeaver does NOT
        // execute this file (read-only product) and is for the operator to run.
        var firstLine = script.Split('\n')[0];
        Assert.StartsWith("#", firstLine.TrimEnd('\r'), StringComparison.Ordinal);
        Assert.Contains("GroupWeaver", script, StringComparison.Ordinal);
        Assert.Contains("NOT", script, StringComparison.Ordinal);
        Assert.Contains("read-only", script, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ToPowerShell_GuardsEveryCreateAndEveryMembershipAdd_WithAnExistenceCheck()
    {
        var plan = new PlanModel(BaseOu);
        var dl = plan.AddNode(PlanCreatableKind.DomainLocalGroup, "DL_X_RW", sam: "DL_X_RW");
        var gg = plan.AddNode(PlanCreatableKind.GlobalGroup, "GG_Sales", sam: "GG_Sales");
        plan.AddEdge(dl.Dn, gg.Dn);

        var script = PlanScriptExporter.ToPowerShell(plan, Header);

        // Idempotence: a "-not (... existence query ...)" guard wraps creates and
        // membership adds, so re-running the script is safe.
        Assert.Contains("if (-not", script, StringComparison.Ordinal);
        Assert.Contains("Set-StrictMode", script, StringComparison.Ordinal);
    }

    // --- Deterministic bytes given an injected timestamp ----------------------------------

    [Fact]
    public void ToPowerShell_WithAnInjectedTimestamp_IsByteIdenticalAcrossExports()
    {
        var plan = BuildRepresentativePlan();

        var first = PlanScriptExporter.ToPowerShell(plan, Header);
        var second = PlanScriptExporter.ToPowerShell(plan, Header);

        Assert.Equal(first, second);
        // Byte-level determinism, not just string equality (UTF-8 stability).
        Assert.Equal(Encoding.UTF8.GetBytes(first), Encoding.UTF8.GetBytes(second));
    }

    [Fact]
    public void ToPowerShell_EmbedsTheInjectedTimestamp_NotTheWallClock()
    {
        var plan = BuildRepresentativePlan();

        var script = PlanScriptExporter.ToPowerShell(plan, Header);

        // The header's timestamp comes from PlanScriptHeader.GeneratedAt, injected —
        // never DateTime.Now — so determinism is a property of the input.
        Assert.Contains("2026-06-13", script, StringComparison.Ordinal);
    }

    [Fact]
    public void ToPowerShell_OutputOrderIsDeterministic_IndependentOfAuthoringOrder()
    {
        // Two plans with the same objects authored in different orders must export
        // identically (stable sort by DN, per ADR-014's determinism note).
        var planA = new PlanModel(BaseOu);
        planA.AddNode(PlanCreatableKind.GlobalGroup, "GG_Bbb", sam: "GG_Bbb");
        planA.AddNode(PlanCreatableKind.GlobalGroup, "GG_Aaa", sam: "GG_Aaa");

        var planB = new PlanModel(BaseOu);
        planB.AddNode(PlanCreatableKind.GlobalGroup, "GG_Aaa", sam: "GG_Aaa");
        planB.AddNode(PlanCreatableKind.GlobalGroup, "GG_Bbb", sam: "GG_Bbb");

        Assert.Equal(
            PlanScriptExporter.ToPowerShell(planA, Header),
            PlanScriptExporter.ToPowerShell(planB, Header));
    }

    // --- Helpers --------------------------------------------------------------------------

    private static PlanModel BuildRepresentativePlan()
    {
        var plan = new PlanModel(BaseOu);
        var dl = plan.AddNode(PlanCreatableKind.DomainLocalGroup, "DL_FileShare_RW", sam: "DL_FileShare_RW");
        var gg = plan.AddNode(PlanCreatableKind.GlobalGroup, "GG_Sales_EU", sam: "GG_Sales_EU");
        var user = plan.AddNode(PlanCreatableKind.User, "Ada Lovelace", sam: "ada.lovelace");
        plan.AddEdge(dl.Dn, gg.Dn);
        plan.AddEdge(gg.Dn, user.Dn);
        return plan;
    }

    /// <summary>Mutates a node's SAM directly to inject an exporter-gate test value
    /// the model's AddNode would otherwise reject — exercising the exporter's OWN
    /// control-char gate (defense in depth), not the model's.</summary>
    private static void ForceSam(PlanModel plan, string dn, string sam)
    {
        Assert.True(plan.TryGetNode(dn, out var node));
        node!.SamAccountName = sam;
    }

    /// <summary>Mutates a node's Name directly to inject an exporter-gate test value
    /// the model's AddNode/RenameNode would otherwise reject at author time (#77) —
    /// exercising the exporter's OWN unsafe-char gate on the Name-token emission path
    /// (defense in depth), not the model's. The injected Name deliberately no longer
    /// matches the DN: this crafts a token that bypassed author-time validation,
    /// exactly the scenario the exporter must still reject.</summary>
    private static void ForceName(PlanModel plan, string dn, string newName)
    {
        Assert.True(plan.TryGetNode(dn, out var node));
        node!.Name = newName;
    }

    private static int IndexOfRequired(string haystack, string needle)
    {
        var idx = haystack.IndexOf(needle, StringComparison.Ordinal);
        Assert.True(idx >= 0, $"expected the script to contain '{needle}'");
        return idx;
    }
}
