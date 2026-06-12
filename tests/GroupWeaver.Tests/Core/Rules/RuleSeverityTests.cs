using GroupWeaver.Core.Rules;

using Xunit;

namespace GroupWeaver.Tests.Core.Rules;

/// <summary>
/// Pins the <see cref="RuleSeverity"/> enum ORDER (ADR-008): Info=0 &lt;
/// Warning=1 &lt; Error=2. AP 3.4's traffic-light roll-up takes the max()
/// over findings — reordering or renumbering this enum silently flips the
/// roll-up, so the numeric values are pinned, not just the names.
/// </summary>
public class RuleSeverityTests
{
    [Theory]
    [InlineData(RuleSeverity.Info, 0)]
    [InlineData(RuleSeverity.Warning, 1)]
    [InlineData(RuleSeverity.Error, 2)]
    public void NumericValues_ArePinned(RuleSeverity severity, int expected)
    {
        Assert.Equal(expected, (int)severity);
    }

    [Fact]
    public void Ordering_InfoBelowWarningBelowError()
    {
        Assert.True(RuleSeverity.Info < RuleSeverity.Warning);
        Assert.True(RuleSeverity.Warning < RuleSeverity.Error);
    }

    [Fact]
    public void MaxRollUp_PicksTheSeverestFinding()
    {
        // The AP 3.4 aggregation pattern: severity of a node = max of its findings.
        Assert.Equal(
            RuleSeverity.Error,
            new[] { RuleSeverity.Info, RuleSeverity.Error, RuleSeverity.Warning }.Max());
        Assert.Equal(
            RuleSeverity.Warning,
            new[] { RuleSeverity.Info, RuleSeverity.Warning }.Max());
        Assert.Equal(
            RuleSeverity.Info,
            new[] { RuleSeverity.Info, RuleSeverity.Info }.Max());
    }

    [Fact]
    public void ExactlyThreeSeverities_NoSilentAdditions()
    {
        // A fourth severity would change the max() semantics and the schema;
        // adding one must be a deliberate, reviewed change (schemaVersion bump).
        Assert.Equal(
            new[] { RuleSeverity.Info, RuleSeverity.Warning, RuleSeverity.Error },
            Enum.GetValues<RuleSeverity>());
    }
}
