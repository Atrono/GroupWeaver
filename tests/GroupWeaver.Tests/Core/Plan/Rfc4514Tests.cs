using GroupWeaver.Core.Graph;
using GroupWeaver.Core.Plan;

using Xunit;

namespace GroupWeaver.Tests.Core.Plan;

/// <summary>
/// Pins <see cref="Rfc4514.EscapeRdnValue"/> (ADR-014): a node display name becomes
/// the value of a SINGLE RDN in the formed DN <c>CN=&lt;escaped&gt;,&lt;BaseOuDn&gt;</c>.
/// The load-bearing property (consumed by GraphBuilder/DnPath downstream) is that an
/// authored child sits EXACTLY one RDN level under the base OU — i.e.
/// <see cref="DnPath.RelativeDepth"/> of the formed child DN against the base OU is 1,
/// even when the name contains DN metacharacters (comma, plus, quote, backslash,
/// angle brackets, semicolon, equals) or a leading/trailing space or leading hash
/// that RFC 4514 requires escaping. DnPath is escape-AWARE on read, so a correctly
/// escaped comma must NOT split the RDN. RED until <c>src/Core/Plan</c> exists.
/// </summary>
public class Rfc4514Tests
{
    private const string BaseOu = "OU=AGDLP-Lab,DC=agdlp,DC=lab";

    // --- The headline case: "Sales, EU" is ONE RDN, depth 1 under the base OU ----------

    [Fact]
    public void EscapeRdnValue_NameWithComma_ProducesOneRdn_ChildIsDepthOneUnderBaseOu()
    {
        var plan = new PlanModel(BaseOu);

        var group = plan.AddNode(PlanCreatableKind.GlobalGroup, "Sales, EU");

        // The comma is escaped INSIDE the RDN value, so the DN has exactly one
        // RDN above the base OU. If the comma leaked unescaped, RelativeDepth
        // would be 2 (an extra phantom level) — this is the whole point.
        Assert.Equal(1, DnPath.RelativeDepth(group.Dn, BaseOu));
        Assert.Equal(@"CN=Sales\, EU," + BaseOu, group.Dn);

        // And the parent of the child DN is exactly the base OU (escape-aware).
        Assert.Equal(BaseOu, DnPath.Parent(group.Dn));
    }

    [Theory]
    [InlineData("Sales, EU")] // comma
    [InlineData("A+B Team")] // plus
    [InlineData("Quote\"Name")] // double quote
    [InlineData(@"Back\slash")] // backslash
    [InlineData("Less<Greater>")] // angle brackets
    [InlineData("Semi;Colon")] // semicolon
    [InlineData("Eq=uals")] // equals
    [InlineData("#Leading")] // leading hash
    [InlineData(" LeadingSpace")] // leading space
    [InlineData("TrailingSpace ")] // trailing space
    [InlineData("GG_Plain")] // nothing special: still one RDN
    public void EscapeRdnValue_AnyName_FormsExactlyOneRdnUnderTheBaseOu(string name)
    {
        var plan = new PlanModel(BaseOu);

        var node = plan.AddNode(PlanCreatableKind.GlobalGroup, name);

        // Regardless of the metacharacters in the name, the formed child DN is
        // exactly one RDN level below the base OU (escaping kept it a single RDN).
        Assert.Equal(1, DnPath.RelativeDepth(node.Dn, BaseOu));
        Assert.Equal(BaseOu, DnPath.Parent(node.Dn));
    }

    // --- Specific escaping outputs (RFC 4514 §2.4) -------------------------------------

    [Theory]
    [InlineData("Sales, EU", @"Sales\, EU")]
    [InlineData("A+B", @"A\+B")]
    [InlineData("a\"b", "a\\\"b")]
    [InlineData(@"a\b", @"a\\b")]
    [InlineData("a<b", @"a\<b")]
    [InlineData("a>b", @"a\>b")]
    [InlineData("a;b", @"a\;b")]
    [InlineData("a=b", @"a\=b")]
    [InlineData("#hash", @"\#hash")] // hash ONLY when leading
    [InlineData("no#mid", "no#mid")] // a non-leading hash is NOT escaped
    [InlineData(" lead", @"\ lead")] // leading space
    [InlineData("trail ", @"trail\ ")] // trailing space
    [InlineData("mid space", "mid space")] // an interior space is NOT escaped
    [InlineData("Plain", "Plain")]
    public void EscapeRdnValue_EscapesExactlyTheSpecialPositionsAndCharacters(string raw, string expected)
    {
        Assert.Equal(expected, Rfc4514.EscapeRdnValue(raw));
    }
}
