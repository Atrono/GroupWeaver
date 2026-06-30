using GroupWeaver.App.ViewModels;
using GroupWeaver.Core.Model;
using GroupWeaver.Core.Rules;
using GroupWeaver.Providers;

using Xunit;

namespace GroupWeaver.App.Tests;

/// <summary>
/// Pins the WP4 (#148) detail-panel AUDIT-CHIP projection of <see cref="DetailPanelModel"/>:
/// <c>Build(snapshot, dn, report)</c> derives <see cref="DetailPanelModel.AuditChips"/>
/// ENGINE-SIDE from <see cref="RuleReport.ViolationsFor"/> alone — rule CLASS + max
/// severity + finding count, NEVER an attribute read — so the privacy whitelist baseline
/// (the <see cref="DetailPanelModel.Rows"/> D2 mirror) is byte-identical with and without
/// a report. The chip set is:
/// <list type="bullet">
/// <item>one chip per finding-bearing rule class (<c>nesting</c>/<c>circular</c>/<c>empty-group</c>
/// to their names; EVERY naming kebab-id collapses to "Naming"), carrying that class's MAX
/// severity and its finding count, emitted in the fixed <c>ClassOrder</c>
/// (Nesting → Circular → Naming → Empty groups);</item>
/// <item>a single green <c>HasFindings:false</c> "No findings" chip for a clean DN under a real report;</item>
/// <item>an EMPTY list when <c>Build</c> is called without a report (Plan/Gap/frontier paths) — no chips, no throw.</item>
/// </list>
/// The demo snapshot under the embedded default ruleset is the same fixture as the AP 3.2
/// baseline (3 nesting errors, 1 circular error, 3 naming warnings, 12 empty-group infos =
/// 19 findings). The DemoProvider holds the GG_Circle_A ↔ GG_Circle_B cycle, so every
/// Evaluate here runs under a Timeout guard plus <c>Task.Run</c> — termination is proven,
/// never trusted (ADR-006 D4). Chips are compared via PROJECTIONS
/// (label/severity/count/HasFindings tuples), NEVER record identity (rule-engine rule:
/// record equality is reference-based).
/// </summary>
public sealed class DetailPanelAuditChipTests
{
    private const string DemoRootDn = "OU=AGDLP-Demo,DC=weavedemo,DC=example";
    private const string GroupSuffix = ",OU=Groups,OU=AGDLP-Demo,DC=weavedemo,DC=example";
    private const string UserSuffix = ",OU=Users,OU=AGDLP-Demo,DC=weavedemo,DC=example";

    // Demo DNs with KNOWN baseline findings (see RuleEngineDemoBaselineTests.ExpectedBaseline).
    // DL_Nested_RO is BOTH the member endpoint of a DL<-DL nesting Error AND an empty-group Info
    // subject => a two-class chip set (Nesting Error + Empty groups Info). The cleanest
    // multi-class flagged DN in the fixture.
    private const string DlNestedRoDn = "CN=DL_Nested_RO" + GroupSuffix;

    // DL_FS-Sales_RW is the PARENT of a DL<-User nesting Error and nothing else => a single
    // Nesting chip.
    private const string DlFsSalesRwDn = "CN=DL_FS-Sales_RW" + GroupSuffix;

    // GG_X: naming-gg Warning + empty-group Info => Naming + Empty groups chips (the single-naming
    // demo case; the >=2-naming collapse is proven below on a hand-built report — no demo DN
    // carries two naming findings).
    private const string GgXDn = "CN=GG_X" + GroupSuffix;

    // A KNOWN-CLEAN, loaded GG: conforming name (two tokens), non-empty, in no baseline finding.
    private const string CleanGroupDn = "CN=GG_Sales_Staff" + GroupSuffix;

    // --- (1) flagged DN: class grouping, MAX severity, count, fixed ClassOrder ----------

    [Fact(Timeout = 60_000)]
    public async Task FlaggedDn_WithTwoClasses_ProjectsOneChipPerClass_MaxSeverity_Count_InClassOrder()
    {
        var (snapshot, report) = await DemoEvaluateAsync();

        // Guard: DL_Nested_RO really is the two-finding shape this case asserts over (a
        // nesting Error + an empty-group Info) — if the fixture drifts, fail HERE.
        var findings = report.ViolationsFor(DlNestedRoDn);
        Assert.Equal(2, findings.Count);

        var model = DetailPanelModel.Build(snapshot, DlNestedRoDn, report);
        Assert.NotNull(model);

        // Two finding-bearing classes, in the fixed presentation order Nesting then Empty groups
        // (Circular/Naming absent here): max severity per class + a count of 1 each.
        Assert.Equal(
            new[]
            {
                ("Nesting", RuleSeverity.Error, 1, true),
                ("Empty groups", RuleSeverity.Info, 1, true),
            },
            Projection(model.AuditChips));
    }

    [Fact(Timeout = 60_000)]
    public async Task FlaggedParentDn_WithOneNestingError_ProjectsASingleNestingChip()
    {
        var (snapshot, report) = await DemoEvaluateAsync();
        Assert.Single(report.ViolationsFor(DlFsSalesRwDn)); // guard: parent-only nesting error

        var model = DetailPanelModel.Build(snapshot, DlFsSalesRwDn, report);
        Assert.NotNull(model);

        Assert.Equal(
            new[] { ("Nesting", RuleSeverity.Error, 1, true) },
            Projection(model.AuditChips));
    }

    [Fact(Timeout = 60_000)]
    public async Task FlaggedDn_WithASingleNamingWarning_CollapsesUnderTheNamingChip()
    {
        var (snapshot, report) = await DemoEvaluateAsync();

        // GG_X: a naming-gg Warning AND an empty-group Info — its user-chosen "naming-gg" id maps
        // to the "Naming" class label (NEVER the raw rule id), beside the Empty groups chip.
        var findings = report.ViolationsFor(GgXDn);
        Assert.Equal(2, findings.Count);
        Assert.Contains(findings, v => v.RuleId == "naming-gg" && v.Severity == RuleSeverity.Warning);

        var model = DetailPanelModel.Build(snapshot, GgXDn, report);
        Assert.NotNull(model);

        Assert.Equal(
            new[]
            {
                ("Naming", RuleSeverity.Warning, 1, true),
                ("Empty groups", RuleSeverity.Info, 1, true),
            },
            Projection(model.AuditChips));

        // The raw kebab id never leaks into a chip label — the class label is "Naming".
        Assert.DoesNotContain(model.AuditChips, c => c.Label.Contains("naming-gg", StringComparison.Ordinal));
    }

    /// <summary>The >=2-naming collapse: no demo DN carries two naming findings, so this pins
    /// it on a HAND-BUILT report — two distinct user kebab-ids (naming-gg, naming-len) on the
    /// SAME subject collapse into ONE "Naming" chip whose count is the SUM (2) and whose
    /// severity is the MAX across them (Error, beating the Warning). Pure finding structure,
    /// no attributes touched.</summary>
    [Fact]
    public void MultipleNamingKebabIds_OnOneSubject_CollapseToOneNamingChip_SummedCount_MaxSeverity()
    {
        const string subjectDn = "CN=GG_x,OU=Groups,DC=stub,DC=lab";
        var snapshot = new DirectorySnapshot();
        snapshot.AddObject(new AdObject { Dn = subjectDn, Kind = AdObjectKind.GlobalGroup, Name = "GG_x" });

        var report = new RuleReport(
            new[]
            {
                Violation("naming-gg", RuleSeverity.Warning, subjectDn),
                Violation("naming-len", RuleSeverity.Error, subjectDn), // a SECOND naming rule, higher severity
            },
            Array.Empty<string>());

        var model = DetailPanelModel.Build(snapshot, subjectDn, report);
        Assert.NotNull(model);

        // Two naming findings -> exactly ONE Naming chip: count = 2 (summed), severity = Error (max).
        Assert.Equal(
            new[] { ("Naming", RuleSeverity.Error, 2, true) },
            Projection(model.AuditChips));
    }

    // --- (2) clean DN under a real report: exactly one green "No findings" chip ----------

    [Fact(Timeout = 60_000)]
    public async Task CleanLoadedDn_UnderARealReport_ProjectsASingleNoFindingsPassChip()
    {
        var (snapshot, report) = await DemoEvaluateAsync();

        // Guard: this DN genuinely carries no findings under the default ruleset.
        Assert.Empty(report.ViolationsFor(CleanGroupDn));
        Assert.True(snapshot.TryGetObject(CleanGroupDn, out _)); // and it IS a loaded object

        var model = DetailPanelModel.Build(snapshot, CleanGroupDn, report);
        Assert.NotNull(model);

        var chip = Assert.Single(model.AuditChips);
        Assert.False(chip.HasFindings);          // the pass chip — green, not a severity hue
        Assert.Equal("No findings", chip.Label); // count is NOT appended on a pass chip
        Assert.Equal(0, chip.Count);
    }

    // --- (3) null/absent report (Plan/Gap/frontier path): empty, no throw ----------------

    [Fact(Timeout = 60_000)]
    public async Task BuildWithoutAReport_YieldsNoChips_AndNeverThrows()
    {
        var (snapshot, _) = await DemoEvaluateAsync();

        // The default overload (Plan/Gap contexts hold no report): a flagged DN, a clean DN,
        // and a frontier DN ALL project an empty chip list — degrade gracefully, never throw.
        Assert.Empty(DetailPanelModel.Build(snapshot, DlNestedRoDn)!.AuditChips);
        Assert.Empty(DetailPanelModel.Build(snapshot, CleanGroupDn)!.AuditChips);

        var frontier = DetailPanelModel.Build(snapshot, "CN=Nowhere,DC=elsewhere,DC=lab");
        Assert.NotNull(frontier);
        Assert.Equal(DetailPanelState.NotLoaded, frontier.State); // a frontier DN is still honest
        Assert.Empty(frontier.AuditChips);

        // Explicit null report behaves identically to omitting it.
        Assert.Empty(DetailPanelModel.Build(snapshot, DlNestedRoDn, report: null)!.AuditChips);
    }

    // --- (4) no attribute leak: chips carry NO attribute value; Rows are baseline-identical

    [Fact(Timeout = 60_000)]
    public async Task AuditChips_CarryNoAttributeValue_AndRowsAreByteIdenticalToThePreWp4Projection()
    {
        var (snapshot, report) = await DemoEvaluateAsync();

        // DL_Nested_RO has a description attribute in the demo data — the perfect leak probe.
        Assert.True(snapshot.TryGetObject(DlNestedRoDn, out var obj));
        var attributeValues = obj!.Attributes.Values.ToArray();
        Assert.NotEmpty(attributeValues); // non-vacuous: there IS a value that could leak

        var withReport = DetailPanelModel.Build(snapshot, DlNestedRoDn, report);
        var withoutReport = DetailPanelModel.Build(snapshot, DlNestedRoDn); // the pre-WP4 projection
        Assert.NotNull(withReport);
        Assert.NotNull(withoutReport);

        // No chip Label (the only string a chip carries) contains ANY attribute value — chips are
        // pure RuleId-derived class + severity + count, never attribute data.
        Assert.All(withReport.AuditChips, chip =>
            Assert.All(attributeValues, value =>
                Assert.DoesNotContain(value, chip.Label, StringComparison.Ordinal)));

        // The whitelist baseline is intact: adding the report changed ONLY AuditChips; the Rows
        // D2 mirror is identical pair-for-pair to the report-less (pre-WP4) projection.
        Assert.Equal(
            withoutReport.Rows.Select(r => (r.Label, r.Value)).ToArray(),
            withReport.Rows.Select(r => (r.Label, r.Value)).ToArray());

        // And the Rows still mirror the live Attributes verbatim (the privacy baseline itself).
        Assert.Equal(obj.Attributes.Count, withReport.Rows.Count);
    }

    // --- helpers ------------------------------------------------------------------------

    /// <summary>Evaluate the FULL demo snapshot under the embedded default ruleset off-thread
    /// under the Timeout — the demo data holds the GG_Circle_A ↔ GG_Circle_B cycle, so
    /// termination is proven, never trusted.</summary>
    private static async Task<(DirectorySnapshot Snapshot, RuleReport Report)> DemoEvaluateAsync()
    {
        var snapshot = await new DemoProvider().LoadScopeAsync(DemoRootDn);
        var report = await Task.Run(() => RuleEngine.Evaluate(snapshot, RulesetLoader.LoadDefault()));
        Assert.Equal(19, report.Violations.Count); // non-vacuous guard: the full baseline ran
        return (snapshot, report);
    }

    private static RuleViolation Violation(string ruleId, RuleSeverity severity, string subjectDn) =>
        new()
        {
            RuleId = ruleId,
            Severity = severity,
            Dns = new[] { subjectDn },
            Message = $"{ruleId} on {subjectDn}",
        };

    /// <summary>THE chip comparison contract: the structured projection (label/severity/count/
    /// HasFindings) in order — NEVER AuditChip record identity.</summary>
    private static (string Label, RuleSeverity Severity, int Count, bool HasFindings)[] Projection(
        IEnumerable<AuditChip> chips) =>
        chips.Select(c => (c.Label, c.Severity, c.Count, c.HasFindings)).ToArray();
}
