using System.Runtime.Versioning;

using GroupWeaver.Core.Model;
using GroupWeaver.Core.Rules;

using Xunit;

namespace GroupWeaver.Tests.Providers.Ldap;

/// <summary>
/// AP 3.2 S7 (ADR-009): <see cref="RuleEngine.Evaluate"/> against the LIVE
/// <c>OU=AGDLP-Lab</c> fixtures seeded by <c>tools/seed-testad.ps1</c> — the
/// lab mirror of the demo TDD baseline: the same 3 nesting errors / 1 circular
/// error / 3 naming warnings / 12 empty-group infos (19 findings, ZERO
/// DL←External infos) with lab DNs. Lab-specific coverage the demo cannot give:
/// <list type="bullet">
/// <item>The dangling cross-forest FSP edge
/// (<c>DL_App-ERP_RW</c> ← <c>CN=&lt;sid&gt;,CN=ForeignSecurityPrincipals,…</c>):
/// the member lives OUTSIDE the lab OU, so it is a raw DN absent from the
/// snapshot — DL×External info cell, suppressed via the <c>MatchesDn</c>
/// channel by the default <c>*,CN=ForeignSecurityPrincipals,*</c> entry,
/// tested BOTH WAYS (entry present/removed).</item>
/// <item>Orphan users u111–u140: seeded, in-snapshot, and deliberately
/// UNDETECTABLE under the v1 ruleset (explicit ADR-008 gap).</item>
/// <item>The partial-scope pin (load only <c>OU=Groups</c>): out-of-scope users
/// resolve External, so the DL←User Error degrades to the DL←External Info and
/// user DNs surface in <see cref="RuleReport.UncheckedDns"/> — the LIVE proof
/// of the External-column rationale (agdlp-domain skill, issue #14).</item>
/// </list>
/// Violations are compared via PROJECTIONS (RuleId, Severity, Dns sequence) —
/// NEVER via RuleViolation record equality (reference-based over the Dns list).
/// The lab contains the GG_Circle_A ↔ GG_Circle_B cycle, so every Evaluate
/// runs under a Timeout guard plus <c>Task.Run</c> — termination is proven,
/// never trusted (ADR-006 D4 discipline). Excluded in CI via the class-level
/// <c>Category=RequiresAd</c> trait; skipped with a loud warning off the lab
/// DC via <see cref="AdFactAttribute"/>. If one of these tests fails while
/// <see cref="RuleEngineDemoBaselineTests"/> stays green, suspect FIXTURE
/// DRIFT first (rerun the existing RequiresAd suite; reseeding is exclusively
/// ad-fixture-admin's job), not the expectation — every count here is derived
/// from the seed script.
/// </summary>
[SupportedOSPlatform("windows")]
[Trait(TestCategories.Category, TestCategories.RequiresAd)]
public class RuleEngineLabIntegrationTests : IClassFixture<LdapLabFixture>
{
    private const string GroupSuffix = ",OU=Groups,OU=AGDLP-Lab,DC=agdlp,DC=lab";
    private const string UserSuffix = ",OU=Users,OU=AGDLP-Lab,DC=agdlp,DC=lab";

    /// <summary>Partial-scope root: the groups sub-OU only (seed script's <c>$ouGroups</c>).</summary>
    private const string GroupsOuDn = "OU=Groups,OU=AGDLP-Lab,DC=agdlp,DC=lab";

    // Nesting violation endpoints (deliberate AGDLP violations in the seed script).
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

    /// <summary>The FSP edge parent — the lab's ONLY External-edge source.</summary>
    private const string DlAppErpRwDn = "CN=DL_App-ERP_RW" + GroupSuffix;

    /// <summary>Fixed dangling cross-forest SID seeded by Ensure-ForeignSidMember.</summary>
    private const string ForeignSid = "S-1-5-21-1100000001-2200000002-3300000003-1106";

    /// <summary>The FSP the DC system-created for <see cref="ForeignSid"/> — deliberately
    /// OUTSIDE OU=AGDLP-Lab, so it is never an object of any lab-scoped snapshot.</summary>
    private const string ForeignFspDn =
        "CN=" + ForeignSid + ",CN=ForeignSecurityPrincipals,DC=agdlp,DC=lab";

    /// <summary>The default ignore entry whose removal must surface the FSP info.</summary>
    private const string FspIgnoreGlob = "*,CN=ForeignSecurityPrincipals,*";

    private readonly LdapLabFixture _fixture;

    public RuleEngineLabIntegrationTests(LdapLabFixture fixture) => _fixture = fixture;

    /// <summary>The full 19-finding lab subject table in canonical report order —
    /// blocks per <see cref="Ruleset.EnumerateRules"/>, element-wise
    /// OrdinalIgnoreCase over Dns within each block (dl-finance-extra FIRST among
    /// the empty groups: '-' &lt; '_' ordinally). Same CNs as the demo baseline —
    /// the lab mirrors the DemoProvider dataset spec (AP 1.4) by construction.</summary>
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
        (RuleIds.EmptyGroup, RuleSeverity.Info, ["CN=UG_ProjectX" + GroupSuffix]),
    ];

    // --- The lab subject table: every finding, exact lab DNs, report order -----------

    [AdFact(Timeout = 60_000)]
    public async Task Evaluate_FullLabDefaultRuleset_YieldsExactlyTheNineteenBaselineFindings_InReportOrder()
    {
        var report = await EvaluateAsync(_fixture.FullSnapshot, RulesetLoader.LoadDefault());

        Assert.Equal(
            ExpectedBaseline.Select(e => (e.RuleId, e.Severity, string.Join("", e.Dns))).ToArray(),
            report.Violations.Select(v => (v.RuleId, v.Severity, string.Join("", v.Dns))).ToArray());
    }

    [AdFact(Timeout = 60_000)]
    public async Task Evaluate_FullLabDefaultRuleset_PinsCountsPerRuleIdAndSeverity()
    {
        var report = await EvaluateAsync(_fixture.FullSnapshot, RulesetLoader.LoadDefault());
        var violations = report.Violations;

        Assert.Equal(19, violations.Count);
        Assert.Equal(3, violations.Count(v => v.RuleId == RuleIds.Nesting && v.Severity == RuleSeverity.Error));
        Assert.Equal(2, violations.Count(v => v.RuleId == "naming-gg" && v.Severity == RuleSeverity.Warning));
        Assert.Equal(1, violations.Count(v => v.RuleId == "naming-dl" && v.Severity == RuleSeverity.Warning));
        Assert.DoesNotContain(violations, v => v.RuleId == "naming-ug"); // all three UG names conform
        Assert.Equal(1, violations.Count(v => v.RuleId == RuleIds.Circular && v.Severity == RuleSeverity.Error));
        Assert.Equal(12, violations.Count(v => v.RuleId == RuleIds.EmptyGroup && v.Severity == RuleSeverity.Info));

        // ZERO External infos: the lab's only External edge (DL_App-ERP_RW -> the
        // dangling FSP) lands in the DL x External info cell and is pre-suppressed
        // by the default ignore list — under the DEFAULT ruleset no nesting finding
        // may surface below Error.
        Assert.DoesNotContain(violations, v => v.RuleId == RuleIds.Nesting && v.Severity != RuleSeverity.Error);
    }

    // --- FSP edge: both-ways suppression via the MatchesDn channel --------------------

    [AdFact(Timeout = 60_000)]
    public async Task Evaluate_FspEdge_SuppressedByDefault_SurfacesAsExactlyOneDlExternalInfo_WhenItsIgnoreEntryIsRemoved()
    {
        var @default = RulesetLoader.LoadDefault();

        // Suppressed by default: the raw FSP member DN appears in NO finding —
        // exemption comes from the visible, deletable ignore LIST, not the kind.
        // The FSP lives outside OU=AGDLP-Lab, so it is absent from the snapshot
        // and can only have matched via the MatchesDn (raw frontier-DN) channel.
        var suppressed = await EvaluateAsync(_fixture.FullSnapshot, @default);
        Assert.False(_fixture.FullSnapshot.TryGetObject(ForeignFspDn, out _));
        Assert.Equal(19, suppressed.Violations.Count);
        Assert.All(suppressed.Violations, v => Assert.DoesNotContain(ForeignFspDn, v.Dns, Dn.Comparer));

        // Remove exactly the suppressing entry (guard the removal count so a
        // renamed default entry fails HERE, not as a silent vacuous pass).
        var filtered = @default.Ignore.Where(entry => entry.Dn != FspIgnoreGlob).ToArray();
        Assert.Equal(@default.Ignore.Count - 1, filtered.Length);
        var report = await EvaluateAsync(_fixture.FullSnapshot, @default with { Ignore = filtered });

        // Exactly that one DL <- External info appears, with the exact edge Dns in
        // stored spellings (DL x External = info cell, ADR-008 issue-#14 rationale).
        Assert.Equal(20, report.Violations.Count);
        var surfaced = Assert.Single(report.Violations, v => v.RuleId == RuleIds.Nesting && v.Severity == RuleSeverity.Info);
        Assert.Equal(new[] { DlAppErpRwDn, ForeignFspDn }, surfaced.Dns);

        // The other 19 findings are projection-identical to the suppressed
        // baseline: removing the entry added exactly one finding, nothing else.
        Assert.Equal(
            suppressed.Violations.Select(Projection).ToArray(),
            report.Violations.Where(v => !ReferenceEquals(v, surfaced)).Select(Projection).ToArray());
    }

    // --- UncheckedDns: exactly the raw FSP member DN -----------------------------------

    [AdFact(Timeout = 60_000)]
    public async Task Evaluate_FullLabDefaultRuleset_UncheckedDnsIsExactlyTheFspDn()
    {
        var report = await EvaluateAsync(_fixture.FullSnapshot, RulesetLoader.LoadDefault());

        // All 40 lab groups are loaded (LdapProviderIntegrationTests T4), so the
        // load-state scan contributes nothing; the walk frontier is exactly the
        // raw FSP member DN — an IGNORED object still surfaces here (load-state
        // truth, not a judgment; UncheckedDns is never ignore-filtered).
        var only = Assert.Single(report.UncheckedDns);
        Assert.True(Dn.Comparer.Equals(ForeignFspDn, only), $"unexpected unchecked DN: '{only}'");
    }

    // --- Orphan users u111-u140: seeded, loaded, and invisible to the v1 ruleset --------

    [AdFact(Timeout = 60_000)]
    public async Task Evaluate_FullLabDefaultRuleset_OrphanUsersU111ToU140_ProduceZeroFindings()
    {
        // Resolve the orphans from the LIVE snapshot by sAMAccountName so the
        // assertion is named-subject (drift in DN spelling fails loudly here).
        var orphanSams = Enumerable.Range(111, 30)
            .Select(i => $"u{i:d3}")
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var orphanDns = _fixture.FullSnapshot.Objects
            .Where(o => o.Kind == AdObjectKind.User
                && o.SamAccountName is not null
                && orphanSams.Contains(o.SamAccountName))
            .Select(o => o.Dn)
            .ToList();
        Assert.Equal(30, orphanDns.Count); // non-vacuous: all 30 orphans are seeded

        var report = await EvaluateAsync(_fixture.FullSnapshot, RulesetLoader.LoadDefault());
        Assert.Equal(19, report.Violations.Count); // non-vacuous: the full baseline ran

        // Group-less users are undetectable under the v1 ruleset — the explicit
        // ADR-008 gap (a future orphan rule needs a schemaVersion bump). They are
        // users, not fetchable kinds, so they never enter UncheckedDns either.
        Assert.All(orphanDns, dn =>
        {
            Assert.All(report.Violations, v => Assert.DoesNotContain(dn, v.Dns, Dn.Comparer));
            Assert.False(report.MaxSeverityByDn.ContainsKey(dn), $"orphan marked: {dn}");
            Assert.DoesNotContain(dn, report.UncheckedDns, Dn.Comparer);
        });
    }

    // --- Partial scope: the LIVE proof of the External-column rationale -----------------

    [AdFact(Timeout = 60_000)]
    public async Task Evaluate_GroupsOuScopeOnly_DlUserErrorBecomesDlExternalInfo_UserDnsEnterUncheckedDns()
    {
        // Load ONLY the groups sub-OU: every user member is now out of scope.
        var snapshot = await _fixture.Provider.LoadScopeAsync(GroupsOuDn);
        Assert.Equal(AdObjectKind.External, snapshot.GetKind(User001Dn)); // premise (T9)

        var report = await EvaluateAsync(snapshot, RulesetLoader.LoadDefault());
        var nesting = report.Violations.Where(v => v.RuleId == RuleIds.Nesting).ToList();
        Assert.Equal(2, nesting.Count);

        // DL <- DL stays an Error: both endpoints are in scope.
        var dlInDl = Assert.Single(nesting, v => v.Severity == RuleSeverity.Error);
        Assert.Equal(new[] { DlFsFinanceRoDn, DlNestedRoDn }, dlInDl.Dns);

        // The full-scope DL <- User ERROR degrades to the DL x External INFO: u001
        // is a raw out-of-scope DN now, and no default ignore entry matches it —
        // the lab user lives under OU=Users, not CN=Users (the load-bearing
        // near-miss: 'CN=Domain Admins,CN=Users,*' style entries must not hide
        // ordinary users, agdlp-domain skill).
        var dlExternal = Assert.Single(nesting, v => v.Severity == RuleSeverity.Info);
        Assert.Equal(new[] { DlFsSalesRwDn, User001Dn }, dlExternal.Dns);

        // UG x External is ALLOW (mass-flag protection on scoped live loads): the
        // full-scope UG <- User error on u002 vanishes entirely, it does not degrade.
        Assert.DoesNotContain(report.Violations,
            v => v.RuleId == RuleIds.Nesting && Dn.Comparer.Equals(v.PrimaryDn, UgAllStaffDn));

        // Naming, circular, and empty-group are scope-stable: all 40 groups (and
        // both circle members) live inside OU=Groups. 2 + 3 + 1 + 12 = 18 findings.
        Assert.Equal(3, report.Violations.Count(v => v.RuleId is "naming-gg" or "naming-dl" or "naming-ug"));
        Assert.Equal(1, report.Violations.Count(v => v.RuleId == RuleIds.Circular));
        Assert.Equal(12, report.Violations.Count(v => v.RuleId == RuleIds.EmptyGroup));
        Assert.Equal(18, report.Violations.Count);

        // "Unexpanded areas are unchecked": every out-of-scope member surfaces as
        // a raw frontier DN — the 110 distinct group-member users (u001-u100 in
        // GG_*_Staff, u101-u110 in GG_*_Lead; u001/u002 dedup into those) plus the
        // FSP. Orphans u111-u140 are members of nothing and stay absent.
        Assert.Contains(User001Dn, report.UncheckedDns, Dn.Comparer);
        Assert.Contains(User002Dn, report.UncheckedDns, Dn.Comparer);
        Assert.Contains(ForeignFspDn, report.UncheckedDns, Dn.Comparer);
        Assert.All(report.UncheckedDns, dn => Assert.True(
            Dn.Comparer.Equals(ForeignFspDn, dn)
                || dn.EndsWith(UserSuffix, StringComparison.OrdinalIgnoreCase),
            $"unexpected unchecked DN: '{dn}'"));
        Assert.Equal(111, report.UncheckedDns.Count);
    }

    // --- Helpers -------------------------------------------------------------------------

    /// <summary>Evaluate off-thread under the test Timeout: the lab snapshot
    /// contains the GG_Circle_A ↔ GG_Circle_B cycle, and termination is proven,
    /// never trusted (ADR-006 D4).</summary>
    private static Task<RuleReport> EvaluateAsync(DirectorySnapshot snapshot, Ruleset ruleset) =>
        Task.Run(() => RuleEngine.Evaluate(snapshot, ruleset));

    /// <summary>THE comparison contract for violations: structured fields plus the
    /// Dns sequence — never RuleViolation record equality (reference-based over
    /// the Dns list property).</summary>
    private static (string RuleId, RuleSeverity Severity, string Dns, string Message) Projection(RuleViolation v) =>
        (v.RuleId, v.Severity, string.Join("", v.Dns), v.Message);
}
