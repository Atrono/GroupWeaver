using GroupWeaver.Core.Model;
using GroupWeaver.Core.Rules;
using GroupWeaver.Tests.Providers;

using Xunit;

namespace GroupWeaver.Tests.Core.Rules;

/// <summary>
/// THE executable AP 3.2 TDD baseline (ADR-009, .claude/rules/rule-model.md):
/// the FULL demo snapshot under the embedded default ruleset yields exactly
/// 3 nesting errors, 1 circular error, 3 naming warnings, and 12 empty-group
/// infos — 19 findings, ZERO DL←External infos (both builtin edges are
/// pre-suppressed by the default ignore list, tested BOTH WAYS by removing
/// exactly the suppressing entry). Pinned here, dataset-exact:
/// <list type="bullet">
/// <item>Every finding's RuleId, Severity, and Dns, in canonical report order
/// (nesting → naming-gg → naming-dl → naming-ug → circular → empty-group;
/// element-wise OrdinalIgnoreCase over Dns within a block).</item>
/// <item>ONE exact message string per rule family (4 total) — wording churn is
/// deliberate test churn from here on; the other 15 messages stay structured-
/// fields-only.</item>
/// <item><see cref="RuleReport.UncheckedDns"/> == exactly the two raw builtin
/// member DNs, sorted — ignored objects still surface (load-state truth, not
/// a judgment).</item>
/// <item><see cref="RuleReport.MaxSeverityByDn"/> overlap pins: subject sets
/// are NOT disjoint, and both-endpoint attribution escalates member-node
/// severities (DL_Nested_RO renders Error, not its own empty-group Info).</item>
/// <item>Determinism: two Evaluate calls on the same inputs are
/// projection-equal.</item>
/// </list>
/// Violations are compared via PROJECTIONS (RuleId, Severity, Dns sequence,
/// Message) — NEVER via RuleViolation record equality, which is
/// reference-based over the <c>Dns</c> list property. The demo snapshot
/// contains the GG_Circle_A ↔ GG_Circle_B cycle, so every Evaluate here runs
/// under a Timeout guard plus <c>Task.Run</c> — termination is proven, never
/// trusted (ADR-006 D4 discipline). If a count drifts, suspect the dataset or
/// the engine first, never this table (the spec's subject table is
/// authoritative and was re-derived from demo-directory.json).
/// </summary>
public class RuleEngineDemoBaselineTests : IClassFixture<DemoProviderFixture>
{
    private const string GroupSuffix = ",OU=Groups,OU=AGDLP-Demo,DC=weavedemo,DC=example";
    private const string UserSuffix = ",OU=Users,OU=AGDLP-Demo,DC=weavedemo,DC=example";

    // Nesting violation endpoints.
    private const string DlFsFinanceRoDn = "CN=DL_FS-Finance_RO" + GroupSuffix;
    private const string DlNestedRoDn = "CN=DL_Nested_RO" + GroupSuffix;
    private const string DlFsSalesRwDn = "CN=DL_FS-Sales_RW" + GroupSuffix;
    private const string User001Dn = "CN=Anna Acker (u001)" + UserSuffix;
    private const string UgAllStaffDn = "CN=UG_AllStaff" + GroupSuffix;
    private const string User002Dn = "CN=Ben Acker (u002)" + UserSuffix;

    // Naming offenders.
    private const string GgXDn = "CN=GG_X" + GroupSuffix;
    private const string SalesTeamGlobalDn = "CN=SalesTeamGlobal" + GroupSuffix;
    private const string DlFinanceExtraDn = "CN=dl-finance-extra" + GroupSuffix;

    // The circular pair.
    private const string CircleADn = "CN=GG_Circle_A" + GroupSuffix;
    private const string CircleBDn = "CN=GG_Circle_B" + GroupSuffix;

    // Empty-group-only subject for the Info overlap pin.
    private const string UgProjectXDn = "CN=UG_ProjectX" + GroupSuffix;

    // The two builtin-imitation edges: DL parents in the snapshot, raw member
    // DNs absent from it (=> External column, MatchesDn suppression channel).
    private const string DlFsItRwDn = "CN=DL_FS-IT_RW" + GroupSuffix;
    private const string DomainAdminsDn = "CN=Domain Admins,CN=Users,DC=weavedemo,DC=example";
    private const string DlPrintHqRwDn = "CN=DL_Print-HQ_RW" + GroupSuffix;
    private const string PrintOperatorsDn = "CN=Print Operators,CN=Builtin,DC=weavedemo,DC=example";

    private readonly DemoProviderFixture _fixture;

    public RuleEngineDemoBaselineTests(DemoProviderFixture fixture) => _fixture = fixture;

    /// <summary>The full 19-finding subject table in canonical report order —
    /// blocks per <see cref="Ruleset.EnumerateRules"/>, element-wise
    /// OrdinalIgnoreCase over Dns within each block (which puts
    /// dl-finance-extra FIRST among the empty groups: '-' &lt; '_' ordinally).</summary>
    private static readonly (string RuleId, RuleSeverity Severity, string[] Dns)[] ExpectedBaseline =
    [
        (RuleIds.Nesting, RuleSeverity.Error, [DlFsFinanceRoDn, DlNestedRoDn]), // DL <- DL deny
        (RuleIds.Nesting, RuleSeverity.Error, [DlFsSalesRwDn, User001Dn]),      // DL <- User deny
        (RuleIds.Nesting, RuleSeverity.Error, [UgAllStaffDn, User002Dn]),       // UG <- User deny
        ("naming-gg", RuleSeverity.Warning, [GgXDn]),                           // one token; needs (_Token)+
        ("naming-gg", RuleSeverity.Warning, [SalesTeamGlobalDn]),               // no GG_ prefix
        ("naming-dl", RuleSeverity.Warning, [DlFinanceExtraDn]),                // lowercase prefix, case-sensitive
        (RuleIds.Circular, RuleSeverity.Error, [CircleADn, CircleBDn]),         // canonical anchor A; closing edge implied
        (RuleIds.EmptyGroup, RuleSeverity.Info, [DlFinanceExtraDn]),
        (RuleIds.EmptyGroup, RuleSeverity.Info, ["CN=DL_App-CRM_RO" + GroupSuffix]),
        (RuleIds.EmptyGroup, RuleSeverity.Info, ["CN=DL_App-ERP_RO" + GroupSuffix]),
        (RuleIds.EmptyGroup, RuleSeverity.Info, ["CN=DL_FS-Legacy_RO" + GroupSuffix]),
        (RuleIds.EmptyGroup, RuleSeverity.Info, [DlNestedRoDn]),
        (RuleIds.EmptyGroup, RuleSeverity.Info, ["CN=DL_Print-HQ_RO" + GroupSuffix]),
        (RuleIds.EmptyGroup, RuleSeverity.Info, ["CN=GG_Empty_Marketing" + GroupSuffix]),
        (RuleIds.EmptyGroup, RuleSeverity.Info, ["CN=GG_IT_Backup" + GroupSuffix]),
        (RuleIds.EmptyGroup, RuleSeverity.Info, ["CN=GG_IT_Helpdesk" + GroupSuffix]),
        (RuleIds.EmptyGroup, RuleSeverity.Info, [GgXDn]),
        (RuleIds.EmptyGroup, RuleSeverity.Info, [SalesTeamGlobalDn]),
        (RuleIds.EmptyGroup, RuleSeverity.Info, [UgProjectXDn]),
    ];

    // --- The subject table: every finding, exact Dns, report order -------------------

    [Fact(Timeout = 60_000)]
    public async Task Evaluate_FullDemoDefaultRuleset_YieldsExactlyTheNineteenBaselineFindings_InReportOrder()
    {
        var report = await EvaluateDefaultAsync();

        Assert.Equal(
            ExpectedBaseline.Select(e => (e.RuleId, e.Severity, string.Join("", e.Dns))).ToArray(),
            report.Violations.Select(v => (v.RuleId, v.Severity, string.Join("", v.Dns))).ToArray());
    }

    [Fact(Timeout = 60_000)]
    public async Task Evaluate_FullDemoDefaultRuleset_PinsCountsPerRuleIdAndSeverity()
    {
        var report = await EvaluateDefaultAsync();
        var violations = report.Violations;

        Assert.Equal(19, violations.Count);
        Assert.Equal(3, violations.Count(v => v.RuleId == RuleIds.Nesting && v.Severity == RuleSeverity.Error));
        Assert.Equal(2, violations.Count(v => v.RuleId == "naming-gg" && v.Severity == RuleSeverity.Warning));
        Assert.Equal(1, violations.Count(v => v.RuleId == "naming-dl" && v.Severity == RuleSeverity.Warning));
        Assert.DoesNotContain(violations, v => v.RuleId == "naming-ug"); // all three UG names conform
        Assert.Equal(1, violations.Count(v => v.RuleId == RuleIds.Circular && v.Severity == RuleSeverity.Error));
        Assert.Equal(12, violations.Count(v => v.RuleId == RuleIds.EmptyGroup && v.Severity == RuleSeverity.Info));

        // ZERO External infos: both DL <- builtin edges land in the DL x External
        // info cell and are pre-suppressed by the default ignore list — under
        // the DEFAULT ruleset no nesting finding may surface below Error.
        Assert.DoesNotContain(violations, v => v.RuleId == RuleIds.Nesting && v.Severity != RuleSeverity.Error);
    }

    // --- One exact message per rule family (4 pins — wording is API from here) --------

    [Fact(Timeout = 60_000)]
    public async Task Evaluate_FullDemoDefaultRuleset_PinsOneExactMessagePerRuleFamily()
    {
        var report = await EvaluateDefaultAsync();

        Assert.Equal(
            "Domain-local group 'DL_FS-Sales_RW' contains user 'Anna Acker (u001)' - denied by the nesting matrix.",
            Single(report, RuleIds.Nesting, DlFsSalesRwDn).Message);
        Assert.Equal(
            "Name 'dl-finance-extra' does not match pattern '^DL_[A-Z][A-Za-z0-9]*(-[A-Za-z0-9]+)*_(RW|RO)$'.",
            Single(report, "naming-dl", DlFinanceExtraDn).Message);
        Assert.Equal(
            "Circular nesting: GG_Circle_A -> GG_Circle_B -> GG_Circle_A.",
            Single(report, RuleIds.Circular, CircleADn).Message);
        Assert.Equal(
            "Group 'GG_Empty_Marketing' has no members.",
            Single(report, RuleIds.EmptyGroup, "CN=GG_Empty_Marketing" + GroupSuffix).Message);
    }

    // --- Both-ways suppression for BOTH builtin edges -----------------------------------

    [Theory(Timeout = 60_000)]
    [InlineData("CN=Domain Admins,CN=Users,*", DlFsItRwDn, DomainAdminsDn)]
    [InlineData("*,CN=Builtin,*", DlPrintHqRwDn, PrintOperatorsDn)]
    public async Task Evaluate_BuiltinEdge_SuppressedByDefault_SurfacesWhenItsIgnoreEntryIsRemoved(
        string ignoreEntryDn, string parentDn, string memberDn)
    {
        var @default = RulesetLoader.LoadDefault();

        // Suppressed by default: the raw member DN appears in NO finding —
        // exemption comes from the visible, deletable ignore LIST, not the kind.
        var suppressed = await EvaluateAsync(@default);
        Assert.Equal(19, suppressed.Violations.Count);
        Assert.All(suppressed.Violations, v => Assert.DoesNotContain(memberDn, v.Dns, Dn.Comparer));

        // Remove exactly the suppressing entry (guard the removal count so a
        // renamed default entry fails HERE, not as a silent vacuous pass).
        var filtered = @default.Ignore.Where(entry => entry.Dn != ignoreEntryDn).ToArray();
        Assert.Equal(@default.Ignore.Count - 1, filtered.Length);
        var report = await EvaluateAsync(@default with { Ignore = filtered });

        // Exactly that one DL <- External info appears, with the exact edge Dns
        // (raw member matched via the MatchesDn channel; DL x External = info cell).
        Assert.Equal(20, report.Violations.Count);
        var surfaced = Assert.Single(report.Violations, v => v.Severity == RuleSeverity.Info && v.RuleId == RuleIds.Nesting);
        Assert.Equal(new[] { parentDn, memberDn }, surfaced.Dns);

        // The other 19 findings are projection-identical to the suppressed
        // baseline: removing the entry added exactly one finding, nothing else.
        Assert.Equal(
            suppressed.Violations.Select(Projection).ToArray(),
            report.Violations.Where(v => !ReferenceEquals(v, surfaced)).Select(Projection).ToArray());
    }

    // --- UncheckedDns: exactly the two raw builtin member DNs ---------------------------

    [Fact(Timeout = 60_000)]
    public async Task Evaluate_FullDemoDefaultRuleset_UncheckedDnsAreExactlyTheTwoBuiltinMemberDns_Sorted()
    {
        var report = await EvaluateDefaultAsync();

        // All 40 demo groups are loaded, so the load-state scan contributes
        // nothing; the walk frontier is exactly the two raw builtin DNs —
        // IGNORED objects still surface (load-state truth, not a judgment).
        Assert.Equal(new[] { DomainAdminsDn, PrintOperatorsDn }, report.UncheckedDns);
    }

    // --- MaxSeverityByDn overlap pins (subject sets are NOT disjoint) -------------------

    [Theory(Timeout = 60_000)]
    [InlineData(DlNestedRoDn, RuleSeverity.Error)] // member endpoint of the DL<-DL error outranks its own empty-group Info
    [InlineData(DlFinanceExtraDn, RuleSeverity.Warning)] // naming warning outranks its empty-group Info
    [InlineData(User001Dn, RuleSeverity.Error)] // member-endpoint attribution marks the user node too
    [InlineData(UgProjectXDn, RuleSeverity.Info)] // empty-group only: stays Info
    public async Task Evaluate_FullDemoDefaultRuleset_PinsMaxSeverityOverlaps(string dn, RuleSeverity expected)
    {
        var report = await EvaluateDefaultAsync();

        Assert.Equal(expected, report.MaxSeverityByDn[dn]);
    }

    // --- Determinism: two Evaluate calls are projection-equal ---------------------------

    [Fact(Timeout = 60_000)]
    public async Task Evaluate_CalledTwiceOnTheSameInputs_YieldsProjectionEqualReports()
    {
        var first = await EvaluateDefaultAsync();
        var second = await EvaluateDefaultAsync();

        // Projection comparison, NEVER RuleViolation record equality (the Dns
        // list property makes record equality reference-based).
        Assert.Equal(
            first.Violations.Select(Projection).ToArray(),
            second.Violations.Select(Projection).ToArray());
        Assert.Equal(first.UncheckedDns.ToArray(), second.UncheckedDns.ToArray());

        Assert.Equal(19, first.Violations.Count); // non-vacuous: the full baseline ran
    }

    // --- Helpers --------------------------------------------------------------------------

    private Task<RuleReport> EvaluateDefaultAsync() => EvaluateAsync(RulesetLoader.LoadDefault());

    /// <summary>Evaluate off-thread under the test Timeout: the demo snapshot
    /// contains the GG_Circle_A ↔ GG_Circle_B cycle, and termination is proven,
    /// never trusted.</summary>
    private Task<RuleReport> EvaluateAsync(Ruleset ruleset) =>
        Task.Run(() => RuleEngine.Evaluate(_fixture.FullSnapshot, ruleset));

    /// <summary>The single finding of <paramref name="ruleId"/> anchored at
    /// <paramref name="primaryDn"/> — message-pin addressing.</summary>
    private static RuleViolation Single(RuleReport report, string ruleId, string primaryDn) =>
        Assert.Single(report.Violations, v => v.RuleId == ruleId && Dn.Comparer.Equals(v.PrimaryDn, primaryDn));

    /// <summary>THE comparison contract for violations: structured fields plus the
    /// Dns sequence — never RuleViolation record equality.</summary>
    private static (string RuleId, RuleSeverity Severity, string Dns, string Message) Projection(RuleViolation v) =>
        (v.RuleId, v.Severity, string.Join("", v.Dns), v.Message);
}
