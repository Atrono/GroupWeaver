using GroupWeaver.Core.Model;
using GroupWeaver.Core.Rules;

using Xunit;

namespace GroupWeaver.Tests.Core.Rules;

/// <summary>
/// Pins <see cref="NestingCell"/> (pure value: allowed + optional per-cell
/// severity override) and <see cref="NestingRule.Cell"/>: dictionary lookup
/// over the matrix with the <see cref="NestingRule.Unlisted"/> cell as the
/// fallback for a missing row OR a missing column (ADR-008: future kinds
/// fail closed without breaking v1 files).
/// </summary>
public class NestingRuleTests
{
    private static readonly NestingCell Allow = new(true, null);
    private static readonly NestingCell Deny = new(false, null);
    private static readonly NestingCell InfoOverride = new(false, RuleSeverity.Info);

    // Distinguishable from every listed cell so fallback hits are unambiguous.
    private static readonly NestingCell UnlistedCell = new(false, RuleSeverity.Warning);

    // --- NestingCell is a pure value ------------------------------------------

    [Fact]
    public void NestingCell_CarriesAllowedAndOptionalOverride()
    {
        var cell = new NestingCell(false, RuleSeverity.Info);

        Assert.False(cell.Allowed);
        Assert.Equal(RuleSeverity.Info, cell.SeverityOverride);
        Assert.Null(new NestingCell(true, null).SeverityOverride);
    }

    [Fact]
    public void NestingCell_HasValueEquality()
    {
        Assert.Equal(new NestingCell(false, RuleSeverity.Info), new NestingCell(false, RuleSeverity.Info));
        Assert.NotEqual(new NestingCell(false, RuleSeverity.Info), new NestingCell(false, null));
        Assert.NotEqual(new NestingCell(true, null), new NestingCell(false, null));
    }

    // --- Cell lookup -------------------------------------------------------------

    [Fact]
    public void Cell_PresentCell_IsReturned()
    {
        var rule = Rule();

        Assert.Equal(Deny, rule.Cell(AdObjectKind.DomainLocalGroup, AdObjectKind.User));
        Assert.Equal(Allow, rule.Cell(AdObjectKind.DomainLocalGroup, AdObjectKind.GlobalGroup));
    }

    [Fact]
    public void Cell_PresentCell_KeepsItsSeverityOverride()
    {
        // The DL <- External "info" cell of the default ruleset is modeled as
        // a disallowed cell with a severity override - it must survive lookup.
        Assert.Equal(
            InfoOverride,
            Rule().Cell(AdObjectKind.DomainLocalGroup, AdObjectKind.External));
    }

    [Fact]
    public void Cell_MissingColumn_FallsBackToUnlisted()
    {
        // Row exists, column does not: Computer is absent from the DL row.
        Assert.Equal(
            UnlistedCell,
            Rule().Cell(AdObjectKind.DomainLocalGroup, AdObjectKind.Computer));
    }

    [Fact]
    public void Cell_MissingRow_FallsBackToUnlisted()
    {
        // The matrix has only a DomainLocalGroup row.
        Assert.Equal(
            UnlistedCell,
            Rule().Cell(AdObjectKind.GlobalGroup, AdObjectKind.User));
    }

    [Fact]
    public void Cell_MissingRowAndColumn_FallsBackToUnlisted()
    {
        Assert.Equal(
            UnlistedCell,
            Rule().Cell(AdObjectKind.UniversalGroup, AdObjectKind.OrganizationalUnit));
    }

    // --- shape ----------------------------------------------------------------------

    [Fact]
    public void NestingRule_PropertiesRoundTripThroughInit()
    {
        var rule = Rule();

        Assert.True(rule.Enabled);
        Assert.Equal(RuleSeverity.Error, rule.Severity);
        Assert.Equal(UnlistedCell, rule.Unlisted);
        Assert.Single(rule.Matrix);
        Assert.Empty(rule.Exceptions);
    }

    // --- helpers -----------------------------------------------------------------------

    private static NestingRule Rule() => new()
    {
        Enabled = true,
        Severity = RuleSeverity.Error,
        Unlisted = UnlistedCell,
        Matrix = new Dictionary<AdObjectKind, IReadOnlyDictionary<AdObjectKind, NestingCell>>
        {
            [AdObjectKind.DomainLocalGroup] = new Dictionary<AdObjectKind, NestingCell>
            {
                [AdObjectKind.User] = Deny,
                [AdObjectKind.GlobalGroup] = Allow,
                [AdObjectKind.External] = InfoOverride,
            },
        },
        Exceptions = Array.Empty<MatchEntry>(),
    };
}
