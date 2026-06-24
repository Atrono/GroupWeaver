using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

using GroupWeaver.App.ViewModels;
using GroupWeaver.Core.Model;
using GroupWeaver.Core.Rules;

using Xunit;

namespace GroupWeaver.App.Tests;

/// <summary>
/// Pins the WP5f (#160 / ADR-028) audit finding detail pane + read-only remediation snippet:
/// <see cref="RemediationSnippet.For"/> (the per-rule-class GUIDANCE template filled with the
/// finding's actual <see cref="RuleViolation.Dns"/>), <see cref="RemediationSnippet.ClassOf"/>
/// (the fixed-id → <see cref="RuleClass"/> mapping), the PowerShell single-quote escaping, the
/// <see cref="AuditFindingDetail.From"/> read-only projection, and the <see cref="AuditViewModel"/>
/// detail-selection seam (single active row) kept INDEPENDENT of the multi-select triage checkboxes.
///
/// <para><b>The load-bearing security pin (CLAUDE.md read-only product):</b> the snippet is INERT
/// TEXT — copy/display only, NEVER executed. We assert at the DATA level that EVERY line bearing an
/// <c>*-AD*</c>-style cmdlet (<c>Remove-ADGroupMember</c>/<c>Add-ADGroupMember</c>/
/// <c>Rename-ADObject</c>/<c>Remove-ADGroup</c>/<c>Set-ADGroup</c>) is <c>#</c>-commented when
/// trimmed, and that the FIRST line is the preview banner — so even a careless paste-and-run is a
/// no-op until the operator deliberately un-comments a line. There is no execution seam to test
/// (there is none in the product); this is the inert-text guarantee at the source-string level.</para>
///
/// <para>Per-rule <see cref="RuleViolation.Dns"/> invariant (rule-engine.md): nesting
/// <c>[parent, member]</c>, naming/empty <c>[subject]</c>, circular = the canonical cycle. The
/// snippet/projection tests build hand-crafted violations that honour it; the selection-separation
/// tests reuse the shared <see cref="LoadedScopeWithFindings"/> fixture + a real engine report.</para>
///
/// <para>Compares PROJECTIONS, never record identity (rule-engine.md / data-model.md).</para>
/// </summary>
public sealed class AuditFindingDetailTests
{
    private const string RootDn = "OU=Lab,DC=stub,DC=lab";

    // The four DNs the hand-built findings reference (real-shaped, no glob metacharacters).
    private const string ParentDn = "CN=DL_FileShare_RW,OU=Lab,DC=stub,DC=lab";
    private const string MemberDn = "CN=alice,OU=Lab,DC=stub,DC=lab";
    private const string CircleA = "CN=GG_Circle_A,OU=Lab,DC=stub,DC=lab";
    private const string CircleB = "CN=GG_Circle_B,OU=Lab,DC=stub,DC=lab";
    private const string SubjectDn = "CN=NotAConventionName,OU=Lab,DC=stub,DC=lab";

    // The set of *-AD* cmdlets the templates emit — used by the inert-text scanner. ANY line that
    // mentions one of these (commented or not) is a "cmdlet line" and MUST be #-commented.
    private static readonly string[] AdCmdlets =
    {
        "Remove-ADGroupMember",
        "Add-ADGroupMember",
        "Rename-ADObject",
        "Remove-ADGroup",
        "Set-ADGroup",
    };

    // === (1) RemediationSnippet.For per class: references the right DNs; ALWAYS inert ============

    /// <summary>
    /// The Nesting snippet (<c>Dns = [parent, member]</c>) references BOTH the parent DN and the
    /// member DN (a remove + a re-add through the correct layer), and obeys the universal inert-text
    /// contract: first line = the preview banner, every cmdlet-bearing line is <c>#</c>-commented.
    /// </summary>
    [Fact]
    public void For_Nesting_ReferencesParentAndMember_AndIsInert()
    {
        var v = Violation(RuleIds.Nesting, RuleSeverity.Error, ParentDn, MemberDn);
        var snippet = RemediationSnippet.For(v, "DL_FileShare_RW");

        // References BOTH endpoints of the disallowed nesting (each as a quoted DN literal).
        Assert.Contains(ParentDn, snippet, StringComparison.Ordinal);
        Assert.Contains(MemberDn, snippet, StringComparison.Ordinal);
        // It is a remove-then-re-add fix (both cmdlets present, both commented — see AssertInert).
        Assert.Contains("Remove-ADGroupMember", snippet, StringComparison.Ordinal);
        Assert.Contains("Add-ADGroupMember", snippet, StringComparison.Ordinal);

        AssertInert(snippet);
    }

    /// <summary>
    /// The Circular snippet references the cycle DNs AND a back-edge break (remove the closing
    /// last→first edge). The canonical cycle rotation is <c>[A, B]</c>; the snippet shows the cycle
    /// chain and a <c>Remove-ADGroupMember</c> breaking ONE edge. Inert-text contract holds.
    /// </summary>
    [Fact]
    public void For_Circular_ReferencesCycleAndABackEdgeBreak_AndIsInert()
    {
        // Canonical cycle rotation: min-DN first (CircleA < CircleB), closing edge implied B->A.
        var v = Violation(RuleIds.Circular, RuleSeverity.Error, CircleA, CircleB);
        var snippet = RemediationSnippet.For(v, "GG_Circle_A");

        // Both cycle members appear (the rendered chain A -> B -> A and the break command).
        Assert.Contains(CircleA, snippet, StringComparison.Ordinal);
        Assert.Contains(CircleB, snippet, StringComparison.Ordinal);
        // The break is a Remove-ADGroupMember on the closing back-edge (last -> first).
        Assert.Contains("Remove-ADGroupMember", snippet, StringComparison.Ordinal);
        // The rendered cycle chain makes the loop explicit (membership direction parent -> member).
        Assert.Contains("->", snippet, StringComparison.Ordinal);

        AssertInert(snippet);
    }

    /// <summary>
    /// The self-membership cycle <c>[A]</c> is its own canonical form (rule-engine.md): the snippet
    /// still references the single DN and a break, and stays inert. Guards the <c>Dns.Count == 1</c>
    /// cycle-format arm.
    /// </summary>
    [Fact]
    public void For_Circular_SelfMembership_ReferencesTheSingleDn_AndIsInert()
    {
        var v = Violation(RuleIds.Circular, RuleSeverity.Error, CircleA);
        var snippet = RemediationSnippet.For(v, "GG_Circle_A");

        Assert.Contains(CircleA, snippet, StringComparison.Ordinal);
        Assert.Contains("Remove-ADGroupMember", snippet, StringComparison.Ordinal);
        AssertInert(snippet);
    }

    /// <summary>
    /// The Naming snippet (<c>Dns = [subject]</c>) references the subject DN, carries a
    /// "review carefully" rename caution (a rename is disruptive — guidance, not a ready command),
    /// and stays inert (the example <c>Rename-ADObject</c>/<c>Set-ADGroup</c> lines are commented).
    /// </summary>
    [Fact]
    public void For_Naming_ReferencesSubjectAndAReviewCarefullyRenameNote_AndIsInert()
    {
        var v = Violation("naming-gg", RuleSeverity.Warning, SubjectDn);
        var snippet = RemediationSnippet.For(v, "NotAConventionName");

        Assert.Contains(SubjectDn, snippet, StringComparison.Ordinal);
        // The disruptive-rename caution (case-insensitive so the exact casing is not load-bearing).
        Assert.Contains("REVIEW CAREFULLY", snippet, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Rename-ADObject", snippet, StringComparison.Ordinal);

        AssertInert(snippet);
    }

    /// <summary>
    /// The Empty-group snippet (<c>Dns = [subject]</c>) references the subject DN (populate-or-remove
    /// guidance) and stays inert (the <c>Add-ADGroupMember</c>/<c>Remove-ADGroup</c> lines commented).
    /// </summary>
    [Fact]
    public void For_EmptyGroup_ReferencesSubject_AndIsInert()
    {
        var v = Violation(RuleIds.EmptyGroup, RuleSeverity.Info, SubjectDn);
        var snippet = RemediationSnippet.For(v, "GG_Empty_Team");

        Assert.Contains(SubjectDn, snippet, StringComparison.Ordinal);
        Assert.Contains("Remove-ADGroup", snippet, StringComparison.Ordinal);
        AssertInert(snippet);
    }

    /// <summary>
    /// The universal inert-text pin restated as a single parameterized sweep over EVERY rule class:
    /// the first line is the preview banner and NO line bears a bare/uncommented <c>*-AD*</c> cmdlet.
    /// This is the load-bearing "never executed / inert text" guarantee at the data level.
    /// </summary>
    [Theory]
    [InlineData(RuleIds.Nesting)]
    [InlineData(RuleIds.Circular)]
    [InlineData("naming-gg")]
    [InlineData(RuleIds.EmptyGroup)]
    public void For_EveryClass_FirstLineIsBanner_AndNoBareCmdletLine(string ruleId)
    {
        // [parent, member] satisfies every class (the extra DN is ignored by naming/empty/single-cycle).
        var v = Violation(ruleId, RuleSeverity.Warning, ParentDn, MemberDn);
        var snippet = RemediationSnippet.For(v, "subject");

        var lines = SplitLines(snippet);
        Assert.Equal(RemediationSnippet.PreviewBanner, lines[0]); // mandatory first line

        AssertInert(snippet);
    }

    // === (2) ClassOf mapping: fixed ids -> their class; arbitrary kebab ids -> Naming ============

    /// <summary>
    /// <see cref="RemediationSnippet.ClassOf"/> maps the three fixed ids to their class and folds
    /// EVERY other id (user naming kebab-ids) into <see cref="RuleClass.Naming"/>.
    /// </summary>
    [Fact]
    public void ClassOf_FixedIdsMapDirectly_ArbitraryNamingIdsFoldToNaming()
    {
        Assert.Equal(RuleClass.Nesting, RemediationSnippet.ClassOf(RuleIds.Nesting));
        Assert.Equal(RuleClass.Circular, RemediationSnippet.ClassOf(RuleIds.Circular));
        Assert.Equal(RuleClass.EmptyGroup, RemediationSnippet.ClassOf(RuleIds.EmptyGroup));

        // Arbitrary user naming kebab-ids -> Naming.
        Assert.Equal(RuleClass.Naming, RemediationSnippet.ClassOf("naming-gg"));
        Assert.Equal(RuleClass.Naming, RemediationSnippet.ClassOf("dl-prefix"));
        Assert.Equal(RuleClass.Naming, RemediationSnippet.ClassOf("some-other-rule"));
        Assert.Equal(RuleClass.Naming, RemediationSnippet.ClassOf("")); // even the empty id is Naming
    }

    // === (3) Quote() safety: an embedded single quote is doubled; never breaks the literal =======

    /// <summary>
    /// A DN containing an embedded single quote is emitted as a PowerShell single-quoted literal with
    /// the quote DOUBLED (the literal-string escape), so the snippet never breaks out of the quoted
    /// string. Pinned by counting quotes: the doubled DN appears verbatim and the per-DN-line quote
    /// count is even (balanced delimiters — no escape from the literal).
    /// </summary>
    [Fact]
    public void Quote_DoublesEmbeddedSingleQuote_AndNeverBreaksTheLiteral()
    {
        // A DN with an embedded apostrophe (real AD names can contain one, e.g. O'Brien).
        const string trickyDn = "CN=O'Brien Team,OU=Lab,DC=stub,DC=lab";
        var v = Violation(RuleIds.EmptyGroup, RuleSeverity.Info, trickyDn);
        var snippet = RemediationSnippet.For(v, "O'Brien Team");

        // The embedded quote is doubled in the emitted literal => the exact doubled spelling appears.
        const string doubled = "'CN=O''Brien Team,OU=Lab,DC=stub,DC=lab'";
        Assert.Contains(doubled, snippet, StringComparison.Ordinal);
        // The RAW single-quoted spelling (one apostrophe) must NOT appear — that would be a breakout.
        Assert.DoesNotContain("'CN=O'Brien Team,OU=Lab,DC=stub,DC=lab'", snippet, StringComparison.Ordinal);

        // Balanced delimiters: every line that carries the DN has an EVEN number of single quotes, so
        // the PowerShell single-quoted literal is never left open (no escape from the quoted string).
        foreach (var line in SplitLines(snippet).Where(l => l.Contains("CN=O", StringComparison.Ordinal)))
        {
            Assert.Equal(0, line.Count(c => c == '\'') % 2);
        }
    }

    // === (3b) Clean() hardening: a control-char-bearing directory string can't inject a bare line ==

    // A directory-sourced DN carrying an embedded CRLF followed by a fake cmdlet. Without Clean(),
    // the CRLF would split the surrounding `#`-comment line so the injected Remove-ADGroupMember
    // would read as its OWN uncommented line. The trailing ",DC=x" keeps the tail real-DN-shaped.
    private const string InjectedCmdlet = "Remove-ADGroupMember -Identity 'x' -Confirm:$false";
    private const string EvilDn = "CN=Evil\r\n" + InjectedCmdlet + ",DC=x";

    /// <summary>
    /// SECURITY REGRESSION (defence-in-depth, ADR-028 §2 + the <c>Clean</c> hardening): a directory-
    /// sourced DN carrying an embedded <c>\r\n</c> + a fake cmdlet must NOT escape its <c>#</c>-comment
    /// line in the snippet. With <c>Clean</c> stripping control chars before interpolation, EVERY line
    /// stays a single commented line — so even this malicious DN leaves the snippet inert. Pins the
    /// Nesting arm (the DN hits <see cref="RemediationSnippet.For"/>'s <c>Quote</c> cmdlet lines).
    /// </summary>
    [Fact]
    public void For_Nesting_NewlineInDn_CannotInjectAnUncommentedLine()
    {
        // EvilDn is both endpoints so it flows through Quote (the Remove/Add cmdlet lines).
        var v = Violation(RuleIds.Nesting, RuleSeverity.Error, EvilDn, EvilDn);
        var snippet = RemediationSnippet.For(v, "DL_FileShare_RW");

        // EVERY line, trimmed, is either empty or a comment — the control chars never split a line.
        foreach (var line in SplitLines(snippet))
        {
            var trimmed = line.TrimStart();
            Assert.True(
                trimmed.Length == 0 || trimmed.StartsWith('#'),
                $"Non-comment, non-empty line leaked: <{line}>");
        }

        // The injected cmdlet must NOT appear as a bare (uncommented) line anywhere.
        Assert.DoesNotContain(
            SplitLines(snippet),
            line => line.TrimStart().StartsWith(InjectedCmdlet, StringComparison.Ordinal));

        // The embedded control chars are gone from the snippet entirely (Clean stripped CR and LF
        // from the DN; the only newlines left are the template's own line breaks).
        Assert.DoesNotContain('\r', WithoutTemplateNewlines(snippet));

        // The existing inert-text contract still holds even for this malicious input.
        AssertInert(snippet);
    }

    /// <summary>
    /// SECURITY REGRESSION (the <c>FormatCycle</c> comment path): a Circular finding whose cycle DN
    /// carries an embedded <c>\r\n</c> + fake cmdlet must NOT inject an uncommented line. The cycle DN
    /// flows through both <c>Quote</c> (the break command) and <c>FormatCycle</c> (the rendered chain).
    /// </summary>
    [Fact]
    public void For_Circular_NewlineInCycleDn_CannotInjectAnUncommentedLine()
    {
        // Two-DN cycle with the malicious DN as the canonical anchor (it hits Quote + FormatCycle).
        var v = Violation(RuleIds.Circular, RuleSeverity.Error, EvilDn, CircleB);
        var snippet = RemediationSnippet.For(v, "GG_Circle_A");

        foreach (var line in SplitLines(snippet))
        {
            var trimmed = line.TrimStart();
            Assert.True(
                trimmed.Length == 0 || trimmed.StartsWith('#'),
                $"Non-comment, non-empty line leaked: <{line}>");
        }

        Assert.DoesNotContain(
            SplitLines(snippet),
            line => line.TrimStart().StartsWith(InjectedCmdlet, StringComparison.Ordinal));

        AssertInert(snippet);
    }

    /// <summary>
    /// SECURITY REGRESSION (the message-in-comment path): a Naming finding whose rule <c>Message</c>
    /// carries an embedded <c>\r\n</c> + fake cmdlet must NOT inject an uncommented line — the Naming
    /// arm interpolates <c>Message</c> into a <c>#</c>-comment, so <c>Clean</c> must strip it there too.
    /// </summary>
    [Fact]
    public void For_Naming_NewlineInMessage_CannotInjectAnUncommentedLine()
    {
        var v = new RuleViolation
        {
            RuleId = "naming-gg",
            Severity = RuleSeverity.Warning,
            Dns = new[] { SubjectDn },
            Message = "Name violates convention\r\n" + InjectedCmdlet,
        };
        var snippet = RemediationSnippet.For(v, "NotAConventionName");

        foreach (var line in SplitLines(snippet))
        {
            var trimmed = line.TrimStart();
            Assert.True(
                trimmed.Length == 0 || trimmed.StartsWith('#'),
                $"Non-comment, non-empty line leaked: <{line}>");
        }

        Assert.DoesNotContain(
            SplitLines(snippet),
            line => line.TrimStart().StartsWith(InjectedCmdlet, StringComparison.Ordinal));

        AssertInert(snippet);
    }

    /// <summary>
    /// <c>Clean</c> is IDENTITY on genuine input: a real RFC-4514 DN (no bare control chars) round-trips
    /// unchanged into the snippet, and the only newlines present are the template's own line breaks
    /// (so the malicious-input pins above don't over-constrain normal output).
    /// </summary>
    [Fact]
    public void For_Nesting_NormalDn_RoundTripsUnchanged()
    {
        var v = Violation(RuleIds.Nesting, RuleSeverity.Error, ParentDn, MemberDn);
        var snippet = RemediationSnippet.For(v, "DL_FileShare_RW");

        // The real DNs survive verbatim (Clean removed nothing — they carry no control chars).
        Assert.Contains(ParentDn, snippet, StringComparison.Ordinal);
        Assert.Contains(MemberDn, snippet, StringComparison.Ordinal);

        // No naked CR survives once the template's own \r\n line breaks are normalized away — i.e.
        // there were no extra control chars to begin with in a normal DN.
        Assert.DoesNotContain('\r', WithoutTemplateNewlines(snippet));
    }

    /// <summary>Strips the template's own line breaks so a residual control char (one that escaped
    /// <c>Clean</c>) would be the ONLY <c>\r</c>/<c>\n</c> left.</summary>
    private static string WithoutTemplateNewlines(string snippet) =>
        snippet.Replace("\r\n", string.Empty, StringComparison.Ordinal)
               .Replace("\n", string.Empty, StringComparison.Ordinal);

    // === (4) AuditFindingDetail.From projection: header + per-class copy + numbered steps ========

    /// <summary>
    /// <see cref="AuditFindingDetail.From"/> projects the header fields verbatim (label/severity/
    /// objectName/PrimaryDn = <c>Dns[0]</c>), carries the rule's canonical <c>Message</c>, supplies the
    /// per-class why/how static copy (non-empty), numbers the how-to-fix steps 1..n, and embeds the
    /// same inert snippet. Pinned over the Nesting class.
    /// </summary>
    [Fact]
    public void From_ProjectsHeaderSeverityNameDn_PerClassWhyHow_NumberedSteps()
    {
        var v = Violation(RuleIds.Nesting, RuleSeverity.Error, ParentDn, MemberDn);
        var detail = AuditFindingDetail.From(v, "DL_FileShare_RW", "Nesting matrix");

        // Header / structured fields project verbatim.
        Assert.Equal(RuleClass.Nesting, detail.RuleClass);
        Assert.Equal("Nesting matrix", detail.RuleClassLabel);
        Assert.Equal(RuleSeverity.Error, detail.Severity);
        Assert.Equal("DL_FileShare_RW", detail.ObjectName);
        Assert.Equal(ParentDn, detail.PrimaryDn); // PrimaryDn == Dns[0]
        Assert.Equal(v.Message, detail.Message);

        // Per-class static copy is present (why it matters + plain restatement).
        Assert.False(string.IsNullOrWhiteSpace(detail.WhyItMatters));
        Assert.False(string.IsNullOrWhiteSpace(detail.PlainRestatement));

        // How-to-fix steps are numbered 1..n (1-based ordinals, flat list).
        Assert.NotEmpty(detail.HowToFix);
        for (var i = 0; i < detail.HowToFix.Count; i++)
        {
            Assert.StartsWith($"{i + 1}. ", detail.HowToFix[i], StringComparison.Ordinal);
        }

        // The embedded snippet is the same inert remediation text.
        Assert.Equal(RemediationSnippet.For(v, "DL_FileShare_RW"), detail.Snippet);
        AssertInert(detail.Snippet);
    }

    /// <summary>
    /// The per-class why/how copy genuinely DIFFERS by class (it is not a single shared blurb): the
    /// four classes produce four distinct <c>WhyItMatters</c> strings and each has non-empty numbered
    /// steps. Also pins that a naming rule id projects <see cref="RuleClass.Naming"/> copy.
    /// </summary>
    [Fact]
    public void From_PerClassCopy_IsDistinctAcrossClasses()
    {
        var nesting = AuditFindingDetail.From(Violation(RuleIds.Nesting, RuleSeverity.Error, ParentDn, MemberDn), "p", "Nesting");
        var circular = AuditFindingDetail.From(Violation(RuleIds.Circular, RuleSeverity.Error, CircleA, CircleB), "a", "Circular");
        var naming = AuditFindingDetail.From(Violation("naming-gg", RuleSeverity.Warning, SubjectDn), "n", "Naming: agdlp-gg");
        var empty = AuditFindingDetail.From(Violation(RuleIds.EmptyGroup, RuleSeverity.Info, SubjectDn), "e", "Empty group");

        Assert.Equal(RuleClass.Naming, naming.RuleClass); // a kebab id projects Naming copy

        var whys = new[] { nesting.WhyItMatters, circular.WhyItMatters, naming.WhyItMatters, empty.WhyItMatters };
        Assert.Equal(4, whys.Distinct(StringComparer.Ordinal).Count()); // four distinct rationales

        foreach (var d in new[] { nesting, circular, naming, empty })
        {
            Assert.NotEmpty(d.HowToFix);
            Assert.All(d.HowToFix, s => Assert.False(string.IsNullOrWhiteSpace(s)));
        }
    }

    // === (5) AuditViewModel selection separation: detail vs. triage are INDEPENDENT =============

    /// <summary>
    /// Setting <see cref="AuditViewModel.SelectedFinding"/> projects <see cref="AuditViewModel.Detail"/>
    /// and flips <see cref="AuditViewModel.HasDetail"/> true; clearing it (null) returns
    /// <see cref="AuditViewModel.HasDetail"/> to false and <c>Detail</c> to null. The projected detail
    /// is derived from the selected row's full <see cref="RuleViolation"/>.
    /// </summary>
    [Fact]
    public void SelectedFinding_ProjectsDetail_AndGatesHasDetail()
    {
        using var audit = NewAudit();

        // Nothing selected initially => no detail.
        Assert.Null(audit.SelectedFinding);
        Assert.Null(audit.Detail);
        Assert.False(audit.HasDetail);

        var row = audit.Findings[0];
        audit.SelectedFinding = row;

        Assert.NotNull(audit.Detail);
        Assert.True(audit.HasDetail);
        // The detail is the projection of the selected row's violation (PrimaryDn parity is enough).
        Assert.Equal(row.PrimaryDn, audit.Detail!.PrimaryDn);
        Assert.Equal(row.Severity, audit.Detail.Severity);
        Assert.Equal(row.ObjectName, audit.Detail.ObjectName);

        // Clearing returns to the empty state.
        audit.SelectedFinding = null;
        Assert.Null(audit.Detail);
        Assert.False(audit.HasDetail);
    }

    /// <summary>
    /// THE key UX pin (ADR-028 two-channel selection): the detail channel
    /// (<see cref="AuditViewModel.SelectedFinding"/>) and the triage channel
    /// (<see cref="AuditFindingRowModel.IsSelected"/>) are INDEPENDENT. Selecting a row for detail does
    /// NOT toggle any row's <c>IsSelected</c> (triage count stays 0), and toggling a checkbox does NOT
    /// change <c>SelectedFinding</c>.
    /// </summary>
    [Fact]
    public void DetailSelection_AndTriageCheckbox_AreIndependent()
    {
        using var audit = NewAudit();
        Assert.True(audit.Findings.Count >= 2, "need two rows to prove the channels are independent");
        var rowA = audit.Findings[0];
        var rowB = audit.Findings[1];

        // Selecting a row for DETAIL must not touch ANY triage checkbox.
        audit.SelectedFinding = rowA;
        Assert.Same(rowA, audit.SelectedFinding);
        Assert.Equal(0, audit.SelectedCount);
        Assert.All(audit.Findings, r => Assert.False(r.IsSelected));

        // Toggling a triage checkbox (even on a DIFFERENT row) must not change the detail selection.
        rowB.IsSelected = true;
        Assert.Equal(1, audit.SelectedCount);
        Assert.Same(rowA, audit.SelectedFinding); // detail unchanged

        // Toggling the checkbox of the very row shown in detail still leaves SelectedFinding intact.
        rowA.IsSelected = true;
        Assert.Equal(2, audit.SelectedCount);
        Assert.Same(rowA, audit.SelectedFinding);

        // And clearing all triage checkboxes does not clear the detail selection.
        audit.ClearSelectionCommand.Execute(null);
        Assert.Equal(0, audit.SelectedCount);
        Assert.Same(rowA, audit.SelectedFinding);
    }

    /// <summary>
    /// <see cref="AuditViewModel.ApplyRuleset"/> (which calls <c>RebuildFindings</c>) CLEARS
    /// <see cref="AuditViewModel.SelectedFinding"/> — the prior detail row no longer points at a live
    /// row, so the detail pane returns to its empty state.
    /// </summary>
    [Fact]
    public void ApplyRuleset_ClearsSelectedFinding()
    {
        using var audit = NewAudit(out var snapshot, out var ruleset);

        audit.SelectedFinding = audit.Findings[0];
        Assert.NotNull(audit.Detail);
        Assert.True(audit.HasDetail);

        // A re-thread (here: disable nesting) rebuilds the rows; the stale detail selection is dropped.
        var defaults = RulesetLoader.LoadDefault();
        var nestingOff = ruleset with { Nesting = defaults.Nesting with { Enabled = false } };
        audit.ApplyRuleset(nestingOff);

        Assert.Null(audit.SelectedFinding);
        Assert.Null(audit.Detail);
        Assert.False(audit.HasDetail);
    }

    /// <summary>
    /// <see cref="AuditViewModel.MarkSnippetCopied"/> flips the transient
    /// <see cref="AuditViewModel.SnippetCopied"/> affordance true; changing
    /// <see cref="AuditViewModel.SelectedFinding"/> resets it to false (so the "Copied" badge never
    /// lingers onto the next finding). The VM never touches the clipboard or executes the snippet.
    /// </summary>
    [Fact]
    public void MarkSnippetCopied_FlipsTheFlag_AndSwitchingFindingResetsIt()
    {
        using var audit = NewAudit();
        Assert.True(audit.Findings.Count >= 2);

        audit.SelectedFinding = audit.Findings[0];
        Assert.False(audit.SnippetCopied);

        audit.MarkSnippetCopied();
        Assert.True(audit.SnippetCopied);

        // Switching the active finding resets the affordance.
        audit.SelectedFinding = audit.Findings[1];
        Assert.False(audit.SnippetCopied);

        // Re-copy, then clear the selection — also resets it (OnSelectedFindingChanged path).
        audit.MarkSnippetCopied();
        Assert.True(audit.SnippetCopied);
        audit.SelectedFinding = null;
        Assert.False(audit.SnippetCopied);
    }

    // === Helpers ===============================================================================

    /// <summary>Builds a hand-crafted <see cref="RuleViolation"/> honouring the per-rule
    /// <see cref="RuleViolation.Dns"/> invariant — the caller passes the DNs in invariant order.</summary>
    private static RuleViolation Violation(string ruleId, RuleSeverity severity, params string[] dns) => new()
    {
        RuleId = ruleId,
        Severity = severity,
        Dns = dns,
        Message = $"{ruleId} finding on {dns[0]}",
    };

    /// <summary>Asserts the inert-text contract on a snippet: the first line is the preview banner,
    /// and EVERY line bearing an <c>*-AD*</c> cmdlet is <c>#</c>-commented (trimmed) — i.e. there is
    /// NO bare/uncommented cmdlet line. The load-bearing "never executed" guarantee.</summary>
    private static void AssertInert(string snippet)
    {
        var lines = SplitLines(snippet);
        Assert.Equal(RemediationSnippet.PreviewBanner, lines[0]);

        foreach (var line in lines)
        {
            if (AdCmdlets.Any(c => line.Contains(c, StringComparison.Ordinal)))
            {
                Assert.StartsWith("#", line.TrimStart(), StringComparison.Ordinal);
            }
        }

        // Anti-vacuous belt-and-braces: a uncommented cmdlet token would be caught by a direct regex
        // for a line that starts (after trim) with a bare *-AD* cmdlet (no leading '#').
        Assert.DoesNotMatch(new Regex(@"(?m)^\s*(Remove-AD|Add-AD|Rename-AD|Set-AD)\w*"), snippet);
    }

    /// <summary>Splits a snippet into lines on any newline flavor, dropping the universal trailing
    /// empties so <c>lines[0]</c> is the banner.</summary>
    private static string[] SplitLines(string snippet) =>
        snippet.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');

    /// <summary>A directly-constructed <see cref="AuditViewModel"/> over the shared
    /// <see cref="LoadedScopeWithFindings"/> fixture + a real engine report (the same idiom the
    /// WP5d/WP5e fixtures use). No shell, no %APPDATA% touch.</summary>
    private static AuditViewModel NewAudit() => NewAudit(out _, out _);

    private static AuditViewModel NewAudit(out DirectorySnapshot snapshot, out Ruleset ruleset)
    {
        (snapshot, ruleset) = LoadedScopeWithFindings();
        var report = RuleEngine.Evaluate(snapshot, ruleset);
        return new AuditViewModel(snapshot, report, ruleset, RootDn, onBack: () => { });
    }

    /// <summary>The WP5b/WP5c/WP5d/WP5e findings fixture (re-stated so the fixtures stay independent):
    /// a fully-LOADED scope tripping the default ruleset's nesting (a DL with a direct User member) +
    /// naming (a badly-named GG) + empty-group rules — a real Error/Warning/Info mix. Returns the
    /// snapshot + the default ruleset. Matches <see cref="AuditTableTests"/>/<see cref="AuditTriageTests"/>.</summary>
    private static (DirectorySnapshot Snapshot, Ruleset Ruleset) LoadedScopeWithFindings()
    {
        const string dlOk = "CN=DL_FileShare_RW,OU=Lab,DC=stub,DC=lab";
        const string ggMember = "CN=GG_FileShare_Members,OU=Lab,DC=stub,DC=lab";
        const string dlBad = "CN=DL_DirectUser_RW,OU=Lab,DC=stub,DC=lab";
        const string userDn = "CN=alice,OU=Lab,DC=stub,DC=lab";
        const string ggBadName = "CN=NotAConventionName,OU=Lab,DC=stub,DC=lab";
        const string ggEmpty = "CN=GG_Empty_Team,OU=Lab,DC=stub,DC=lab";

        var snapshot = new DirectorySnapshot();
        snapshot.AddObject(Group(dlOk, AdObjectKind.DomainLocalGroup));
        snapshot.AddObject(Group(ggMember, AdObjectKind.GlobalGroup));
        snapshot.AddObject(Group(dlBad, AdObjectKind.DomainLocalGroup));
        snapshot.AddObject(new AdObject { Dn = userDn, Kind = AdObjectKind.User, Name = "alice" });
        snapshot.AddObject(Group(ggBadName, AdObjectKind.GlobalGroup));
        snapshot.AddObject(Group(ggEmpty, AdObjectKind.GlobalGroup));

        snapshot.SetMembers(dlOk, new[] { ggMember });
        snapshot.SetMembers(ggMember, Array.Empty<string>());
        snapshot.SetMembers(dlBad, new[] { userDn });
        snapshot.SetMembers(ggBadName, Array.Empty<string>());
        snapshot.SetMembers(ggEmpty, Array.Empty<string>());

        return (snapshot, RulesetLoader.LoadDefault());
    }

    private static AdObject Group(string dn, AdObjectKind kind) => new()
    {
        Dn = dn,
        Kind = kind,
        Name = dn.Split(',')[0]["CN=".Length..],
    };
}
