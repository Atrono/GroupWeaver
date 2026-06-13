using System.Text.RegularExpressions;

using GroupWeaver.Core.Model;
using GroupWeaver.Core.Rules;

using Xunit;

namespace GroupWeaver.Tests.Core.Rules;

/// <summary>
/// Pins the embedded strict-AGDLP default ruleset (ADR-008, AP 3.1 slice 6)
/// against the verification matrix — ENGINE-FREE: every assertion calls
/// <see cref="NestingRule.Cell"/>, a naming rule's pattern compiled exactly as
/// the engine will (NonBacktracking | CultureInvariant, case-sensitive,
/// anchored as written, evaluated vs SamAccountName ?? Name), or
/// <see cref="MatchEntry"/>/<see cref="GlobMatcher"/> directly. No snapshot is
/// ever walked; rule evaluation is AP 3.2.
/// </summary>
public class DefaultRulesetTests
{
    private const string DemoGroupSuffix = ",OU=Groups,OU=AGDLP-Demo,DC=weavedemo,DC=example";

    // --- LoadDefault -----------------------------------------------------------

    [Fact]
    public void LoadDefault_Succeeds()
    {
        var ruleset = RulesetLoader.LoadDefault();

        Assert.NotNull(ruleset);
        Assert.Equal(1, ruleset.SchemaVersion);
        Assert.False(string.IsNullOrWhiteSpace(ruleset.Name));
    }

    [Fact]
    public void LoadDefault_AllRulesEnabled_AtTheDocumentedSeverities()
    {
        var ruleset = RulesetLoader.LoadDefault();

        Assert.True(ruleset.Nesting.Enabled);
        Assert.Equal(RuleSeverity.Error, ruleset.Nesting.Severity);

        Assert.True(ruleset.Circular.Enabled);
        Assert.Equal(RuleSeverity.Error, ruleset.Circular.Severity);

        Assert.True(ruleset.EmptyGroup.Enabled);
        Assert.Equal(RuleSeverity.Info, ruleset.EmptyGroup.Severity);

        Assert.Equal(new[] { "naming-gg", "naming-dl", "naming-ug" }, ruleset.Naming.Select(r => r.Id));
        Assert.Equal(
            new[] { AdObjectKind.GlobalGroup, AdObjectKind.DomainLocalGroup, AdObjectKind.UniversalGroup },
            ruleset.Naming.Select(r => r.Kind));
        Assert.All(ruleset.Naming, r =>
        {
            Assert.True(r.Enabled);
            Assert.Equal(RuleSeverity.Warning, r.Severity);
        });

        Assert.NotEmpty(ruleset.Ignore);
    }

    // --- naming patterns vs EVERY demo group name (verification matrix D) -------

    /// <summary>All 40 demo group names with the naming rule that judges their
    /// kind and the expected verdict: 37 pass, exactly SalesTeamGlobal + GG_X
    /// fail naming-gg and dl-finance-extra fails naming-dl.</summary>
    public static readonly (string RuleId, string Name, bool Conforms)[] AllDemoGroupNames =
    [
        // GlobalGroup (18) vs naming-gg
        ("naming-gg", "GG_Sales_Staff", true),
        ("naming-gg", "GG_Sales_Lead", true),
        ("naming-gg", "GG_IT_Staff", true),
        ("naming-gg", "GG_IT_Lead", true),
        ("naming-gg", "GG_HR_Staff", true),
        ("naming-gg", "GG_HR_Lead", true),
        ("naming-gg", "GG_Finance_Staff", true),
        ("naming-gg", "GG_Finance_Lead", true),
        ("naming-gg", "GG_Ops_Staff", true),
        ("naming-gg", "GG_Ops_Lead", true),
        ("naming-gg", "GG_IT_Admins", true),
        ("naming-gg", "GG_IT_Helpdesk", true),
        ("naming-gg", "GG_IT_Backup", true),
        ("naming-gg", "GG_Circle_A", true),         // 1-char second token is legal
        ("naming-gg", "GG_Circle_B", true),
        ("naming-gg", "GG_Empty_Marketing", true),
        ("naming-gg", "SalesTeamGlobal", false),    // no GG_ prefix
        ("naming-gg", "GG_X", false),               // one token; needs (_Token)+

        // DomainLocalGroup (19) vs naming-dl
        ("naming-dl", "DL_FS-Sales_RW", true),
        ("naming-dl", "DL_FS-Sales_RO", true),
        ("naming-dl", "DL_FS-IT_RW", true),
        ("naming-dl", "DL_FS-IT_RO", true),
        ("naming-dl", "DL_FS-HR_RW", true),
        ("naming-dl", "DL_FS-HR_RO", true),
        ("naming-dl", "DL_FS-Finance_RW", true),
        ("naming-dl", "DL_FS-Finance_RO", true),
        ("naming-dl", "DL_FS-Ops_RW", true),
        ("naming-dl", "DL_FS-Ops_RO", true),
        ("naming-dl", "DL_Print-HQ_RW", true),
        ("naming-dl", "DL_Print-HQ_RO", true),
        ("naming-dl", "DL_App-CRM_RW", true),
        ("naming-dl", "DL_App-CRM_RO", true),
        ("naming-dl", "DL_App-ERP_RW", true),
        ("naming-dl", "DL_App-ERP_RO", true),
        ("naming-dl", "DL_Nested_RO", true),        // hyphen group is optional
        ("naming-dl", "DL_FS-Legacy_RO", true),
        ("naming-dl", "dl-finance-extra", false),   // case-sensitive ^DL_ fails

        // UniversalGroup (3) vs naming-ug
        ("naming-ug", "UG_AllStaff", true),
        ("naming-ug", "UG_Managers", true),
        ("naming-ug", "UG_ProjectX", true),
    ];

    public static IEnumerable<object[]> DemoGroupNameData() =>
        AllDemoGroupNames.Select(t => new object[] { t.RuleId, t.Name, t.Conforms });

    [Fact]
    public void DemoGroupNameInventory_Covers40DistinctNames_37Pass3Fail()
    {
        Assert.Equal(40, AllDemoGroupNames.Length);
        Assert.Equal(40, AllDemoGroupNames.Select(t => t.Name).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(
            new[] { "GG_X", "SalesTeamGlobal", "dl-finance-extra" },
            AllDemoGroupNames.Where(t => !t.Conforms).Select(t => t.Name).Order(StringComparer.Ordinal));
    }

    [Theory]
    [MemberData(nameof(DemoGroupNameData))]
    public void DefaultNamingPattern_JudgesDemoGroupName(string ruleId, string name, bool conforms)
    {
        // Demo groups have samAccountName == name, so the engine's
        // SamAccountName ?? Name input is the name itself.
        Assert.Equal(conforms, EngineCompile(Naming(ruleId)).IsMatch(name));
    }

    // --- nesting matrix Cell() verdicts (verification matrix A/B) ----------------

    [Theory]
    [InlineData(AdObjectKind.DomainLocalGroup, AdObjectKind.User)]            // DL_FS-Sales_RW <- u001
    [InlineData(AdObjectKind.UniversalGroup, AdObjectKind.User)]              // UG_AllStaff <- u002
    [InlineData(AdObjectKind.DomainLocalGroup, AdObjectKind.DomainLocalGroup)] // DL_FS-Finance_RO <- DL_Nested_RO
    public void DefaultMatrix_ViolationLane_IsDenyAtRuleSeverity(AdObjectKind parent, AdObjectKind member)
    {
        var nesting = RulesetLoader.LoadDefault().Nesting;

        // deny = disallowed with NO per-cell override: the effective severity
        // is the rule's, and the default rule severity is error.
        Assert.Equal(new NestingCell(false, null), nesting.Cell(parent, member));
        Assert.Equal(RuleSeverity.Error, nesting.Severity);
    }

    [Theory]
    [InlineData(AdObjectKind.GlobalGroup, AdObjectKind.User)]             // A -> G
    [InlineData(AdObjectKind.GlobalGroup, AdObjectKind.Computer)]
    [InlineData(AdObjectKind.GlobalGroup, AdObjectKind.GlobalGroup)]      // role-group nesting
    [InlineData(AdObjectKind.DomainLocalGroup, AdObjectKind.GlobalGroup)] // G -> DL
    [InlineData(AdObjectKind.UniversalGroup, AdObjectKind.GlobalGroup)]   // G -> U
    [InlineData(AdObjectKind.DomainLocalGroup, AdObjectKind.UniversalGroup)] // AGUDLP: U -> DL
    public void DefaultMatrix_ConformantLane_IsAllowed(AdObjectKind parent, AdObjectKind member)
    {
        Assert.True(RulesetLoader.LoadDefault().Nesting.Cell(parent, member).Allowed);
    }

    [Fact]
    public void DefaultMatrix_DlExternal_IsInfoSeverityCell()
    {
        // Built-ins/FSPs surface on the DL row: visible, not judged hard.
        Assert.Equal(
            new NestingCell(false, RuleSeverity.Info),
            RulesetLoader.LoadDefault().Nesting.Cell(AdObjectKind.DomainLocalGroup, AdObjectKind.External));
    }

    [Fact]
    public void DefaultMatrix_GgExternal_IsAllowed()
    {
        // Scoped live loads resolve every out-of-scope member to External -
        // a non-allow default on the GG row would mass-flag healthy forests.
        Assert.True(
            RulesetLoader.LoadDefault().Nesting.Cell(AdObjectKind.GlobalGroup, AdObjectKind.External).Allowed);
    }

    [Fact]
    public void DefaultMatrix_UgUg_IsWarningOverride()
    {
        // Legal in AD, outside the canonical lane: per-cell severity override.
        Assert.Equal(
            new NestingCell(false, RuleSeverity.Warning),
            RulesetLoader.LoadDefault().Nesting.Cell(AdObjectKind.UniversalGroup, AdObjectKind.UniversalGroup));
    }

    [Fact]
    public void DefaultMatrix_UnlistedFallback_IsDeny()
    {
        var nesting = RulesetLoader.LoadDefault().Nesting;

        Assert.Equal(new NestingCell(false, null), nesting.Unlisted);
        // Missing column on a present row, and a missing row entirely - future
        // kinds fail closed without breaking v1 files.
        Assert.Equal(nesting.Unlisted, nesting.Cell(AdObjectKind.GlobalGroup, AdObjectKind.OrganizationalUnit));
        Assert.Equal(nesting.Unlisted, nesting.Cell(AdObjectKind.External, AdObjectKind.User));
    }

    // --- default ignore list (verification matrix A: the suppressed rows) -------

    [Theory]
    [InlineData("CN=Domain Admins,CN=Users,DC=weavedemo,DC=example")]
    [InlineData("CN=Print Operators,CN=Builtin,DC=weavedemo,DC=example")]
    [InlineData("CN=S-1-5-21-1100000001-2200000002-3300000003-1106,CN=ForeignSecurityPrincipals,DC=agdlp,DC=lab")]
    public void DefaultIgnore_MatchesKnownExemptMemberDn(string dn)
    {
        // These raw member DNs are absent from the snapshot: MatchesDn is the
        // channel that must suppress their DL <- External info findings.
        Assert.Contains(RulesetLoader.LoadDefault().Ignore, entry => entry.MatchesDn(dn));
    }

    /// <summary>The 12 loaded-and-empty demo groups (verification matrix C):
    /// their empty-group infos must NOT be eaten by the default ignore list.</summary>
    public static IEnumerable<object[]> EmptyGroupDns() =>
        new[]
        {
            "GG_IT_Helpdesk",
            "GG_IT_Backup",
            "DL_Print-HQ_RO",
            "DL_App-CRM_RO",
            "DL_App-ERP_RO",
            "UG_ProjectX",
            "DL_Nested_RO",
            "SalesTeamGlobal",
            "dl-finance-extra",
            "GG_X",
            "GG_Empty_Marketing",
            "DL_FS-Legacy_RO",
        }.Select(name => new object[] { $"CN={name}{DemoGroupSuffix}" });

    [Theory]
    [MemberData(nameof(EmptyGroupDns))]
    public void DefaultIgnore_MatchesNoEmptyGroupDn(string dn)
    {
        Assert.All(RulesetLoader.LoadDefault().Ignore, entry => Assert.False(entry.MatchesDn(dn)));
    }

    [Theory]
    [InlineData("SalesTeamGlobal", AdObjectKind.GlobalGroup)]
    [InlineData("GG_X", AdObjectKind.GlobalGroup)]
    [InlineData("dl-finance-extra", AdObjectKind.DomainLocalGroup)]
    public void DefaultIgnore_MatchesNoNamingOffender(string name, AdObjectKind kind)
    {
        // Full-object matching (Dn AND Name/SamAccountName channels): the three
        // naming warnings must survive the default ignore list.
        var offender = new AdObject
        {
            Dn = $"CN={name}{DemoGroupSuffix}",
            Kind = kind,
            Name = name,
            SamAccountName = name,
        };

        Assert.All(RulesetLoader.LoadDefault().Ignore, entry => Assert.False(entry.Matches(offender)));
    }

    // --- synthetic pin: suppression comes from the LIST, not the kind -----------

    [Fact]
    public void InSnapshotBuiltin_FailsNamingDl_AndIsMatchedByTheBuiltinIgnoreGlob()
    {
        // A builtin DL that made it INTO the snapshot (kind DomainLocalGroup,
        // not External) is still naming-nonconformant - nothing about its kind
        // excuses it. Its exemption must come from the visible, deletable
        // '*,CN=Builtin,*' default ignore entry.
        var snapshot = new DirectorySnapshot();
        snapshot.AddObject(new AdObject
        {
            Dn = "CN=Administrators,CN=Builtin,DC=x",
            Kind = AdObjectKind.DomainLocalGroup,
            Name = "Administrators",
            SamAccountName = "Administrators",
        });
        Assert.True(snapshot.TryGetObject("CN=Administrators,CN=Builtin,DC=x", out var builtin));

        Assert.DoesNotMatch(EngineCompile(Naming("naming-dl")), builtin!.SamAccountName ?? builtin.Name);

        var builtinEntry = RulesetLoader.LoadDefault().Ignore.Single(entry => entry.Dn == "*,CN=Builtin,*");
        Assert.True(builtinEntry.Matches(builtin));
    }

    // --- helpers -----------------------------------------------------------------

    private static NamingRule Naming(string id) =>
        RulesetLoader.LoadDefault().Naming.Single(rule => rule.Id == id);

    /// <summary>Compiles a naming pattern exactly as the AP 3.2 engine will:
    /// anchored as written, case-sensitive, NonBacktracking | CultureInvariant.</summary>
    private static Regex EngineCompile(NamingRule rule) =>
        new(rule.Pattern, RegexOptions.NonBacktracking | RegexOptions.CultureInvariant);
}
