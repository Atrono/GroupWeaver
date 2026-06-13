using GroupWeaver.Core.Model;
using GroupWeaver.Core.Rules;

using Xunit;

namespace GroupWeaver.Tests.Core.Rules;

/// <summary>
/// Pins the <see cref="NamingRule"/>/<see cref="SimpleRule"/>/<see cref="Ruleset"/>
/// shapes and <see cref="Ruleset.EnumerateRules"/> (ADR-008): the AP 3.3
/// settings binding enumerates nesting, each naming rule (in file order),
/// circular, and empty-group as <see cref="RuleSummary"/> values whose
/// Id/Enabled/Severity flow through from the underlying rules.
/// </summary>
public class RulesetTests
{
    // --- NamingRule / SimpleRule shapes ----------------------------------------

    [Fact]
    public void NamingRule_PropertiesRoundTripThroughInit()
    {
        var rule = new NamingRule
        {
            Id = "naming-gg",
            Enabled = true,
            Severity = RuleSeverity.Warning,
            Kind = AdObjectKind.GlobalGroup,
            Pattern = "^GG_([A-Z][A-Za-z0-9]*)(_[A-Z][A-Za-z0-9]*)+$",
            Description = "GG_<Token>_<Token>...",
            Exceptions = Array.Empty<MatchEntry>(),
        };

        Assert.Equal("naming-gg", rule.Id);
        Assert.True(rule.Enabled);
        Assert.Equal(RuleSeverity.Warning, rule.Severity);
        Assert.Equal(AdObjectKind.GlobalGroup, rule.Kind);
        Assert.Equal("^GG_([A-Z][A-Za-z0-9]*)(_[A-Z][A-Za-z0-9]*)+$", rule.Pattern);
        Assert.Equal("GG_<Token>_<Token>...", rule.Description);
        Assert.Empty(rule.Exceptions);
    }

    [Fact]
    public void NamingRule_DescriptionIsOptional()
    {
        Assert.Null(Naming("naming-ug", AdObjectKind.UniversalGroup).Description);
    }

    [Fact]
    public void SimpleRule_PropertiesRoundTripThroughInit()
    {
        var exception = new MatchEntry { Name = "UG_ProjectX", Note = "placeholder" };
        var rule = new SimpleRule
        {
            RuleId = RuleIds.EmptyGroup,
            Enabled = true,
            Severity = RuleSeverity.Info,
            Exceptions = new[] { exception },
        };

        Assert.Equal("empty-group", rule.RuleId);
        Assert.True(rule.Enabled);
        Assert.Equal(RuleSeverity.Info, rule.Severity);
        Assert.Equal(exception, Assert.Single(rule.Exceptions));
    }

    // --- Ruleset shape -------------------------------------------------------------

    [Fact]
    public void Ruleset_PropertiesRoundTripThroughInit()
    {
        var ruleset = Build(Naming("naming-gg", AdObjectKind.GlobalGroup));

        Assert.Equal(1, ruleset.SchemaVersion);
        Assert.Equal("test ruleset", ruleset.Name);
        Assert.True(ruleset.Nesting.Enabled);
        Assert.Single(ruleset.Naming);
        Assert.Equal(RuleIds.Circular, ruleset.Circular.RuleId);
        Assert.Equal(RuleIds.EmptyGroup, ruleset.EmptyGroup.RuleId);
        Assert.Empty(ruleset.Ignore);
    }

    [Fact]
    public void Ruleset_DescriptionAndAuthorAreOptional()
    {
        var ruleset = Build();

        Assert.Null(ruleset.Description);
        Assert.Null(ruleset.Author);
    }

    // --- EnumerateRules ----------------------------------------------------------------

    [Fact]
    public void EnumerateRules_YieldsNestingNamingCircularEmptyGroup_InOrder()
    {
        var ruleset = Build(
            Naming("naming-gg", AdObjectKind.GlobalGroup),
            Naming("naming-dl", AdObjectKind.DomainLocalGroup));

        var ids = ruleset.EnumerateRules().Select(s => s.Id).ToList();

        Assert.Equal(
            new[] { RuleIds.Nesting, "naming-gg", "naming-dl", RuleIds.Circular, RuleIds.EmptyGroup },
            ids);
    }

    [Fact]
    public void EnumerateRules_NoNamingRules_YieldsExactlyTheThreeFixedRules()
    {
        var ids = Build().EnumerateRules().Select(s => s.Id).ToList();

        Assert.Equal(new[] { RuleIds.Nesting, RuleIds.Circular, RuleIds.EmptyGroup }, ids);
    }

    [Fact]
    public void EnumerateRules_EnabledAndSeverityFlowThroughFromEachRule()
    {
        // Circular is deliberately disabled and naming-dl set to Error in the
        // fixture so flow-through is distinguishable from defaults.
        var ruleset = Build(
            Naming("naming-gg", AdObjectKind.GlobalGroup),
            Naming("naming-dl", AdObjectKind.DomainLocalGroup, RuleSeverity.Error, enabled: false));

        var byId = ruleset.EnumerateRules().ToDictionary(s => s.Id);

        Assert.True(byId[RuleIds.Nesting].Enabled);
        Assert.Equal(RuleSeverity.Error, byId[RuleIds.Nesting].Severity);

        Assert.True(byId["naming-gg"].Enabled);
        Assert.Equal(RuleSeverity.Warning, byId["naming-gg"].Severity);

        Assert.False(byId["naming-dl"].Enabled);
        Assert.Equal(RuleSeverity.Error, byId["naming-dl"].Severity);

        Assert.False(byId[RuleIds.Circular].Enabled);
        Assert.Equal(RuleSeverity.Error, byId[RuleIds.Circular].Severity);

        Assert.True(byId[RuleIds.EmptyGroup].Enabled);
        Assert.Equal(RuleSeverity.Info, byId[RuleIds.EmptyGroup].Severity);
    }

    [Fact]
    public void EnumerateRules_DisplayNamesAreHumanRenderable()
    {
        // AP 3.3 binds DisplayName directly to the settings list - it must
        // never be null/blank for any rule.
        var ruleset = Build(Naming("naming-gg", AdObjectKind.GlobalGroup));

        Assert.All(
            ruleset.EnumerateRules(),
            summary => Assert.False(string.IsNullOrWhiteSpace(summary.DisplayName)));
    }

    // --- RuleSummary ---------------------------------------------------------------------

    [Fact]
    public void RuleSummary_IsAPositionalValueRecord()
    {
        var summary = new RuleSummary("naming-gg", true, RuleSeverity.Warning, "GG naming");

        Assert.Equal("naming-gg", summary.Id);
        Assert.True(summary.Enabled);
        Assert.Equal(RuleSeverity.Warning, summary.Severity);
        Assert.Equal("GG naming", summary.DisplayName);
        Assert.Equal(new RuleSummary("naming-gg", true, RuleSeverity.Warning, "GG naming"), summary);
    }

    // --- helpers ------------------------------------------------------------------------------

    private static NamingRule Naming(
        string id,
        AdObjectKind kind,
        RuleSeverity severity = RuleSeverity.Warning,
        bool enabled = true) => new()
        {
            Id = id,
            Enabled = enabled,
            Severity = severity,
            Kind = kind,
            Pattern = "^.+$",
            Exceptions = Array.Empty<MatchEntry>(),
        };

    private static Ruleset Build(params NamingRule[] naming) => new()
    {
        SchemaVersion = 1,
        Name = "test ruleset",
        Nesting = new NestingRule
        {
            Enabled = true,
            Severity = RuleSeverity.Error,
            Unlisted = new NestingCell(false, null),
            Matrix = new Dictionary<AdObjectKind, IReadOnlyDictionary<AdObjectKind, NestingCell>>
            {
                [AdObjectKind.GlobalGroup] = new Dictionary<AdObjectKind, NestingCell>
                {
                    [AdObjectKind.User] = new NestingCell(true, null),
                },
            },
            Exceptions = Array.Empty<MatchEntry>(),
        },
        Naming = naming,
        Circular = new SimpleRule
        {
            RuleId = RuleIds.Circular,
            Enabled = false,
            Severity = RuleSeverity.Error,
            Exceptions = Array.Empty<MatchEntry>(),
        },
        EmptyGroup = new SimpleRule
        {
            RuleId = RuleIds.EmptyGroup,
            Enabled = true,
            Severity = RuleSeverity.Info,
            Exceptions = Array.Empty<MatchEntry>(),
        },
        Ignore = Array.Empty<MatchEntry>(),
    };
}
