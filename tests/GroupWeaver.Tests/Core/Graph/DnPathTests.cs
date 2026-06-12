using GroupWeaver.Core.Graph;

using Xunit;

namespace GroupWeaver.Tests.Core.Graph;

/// <summary>
/// Pins the escape-aware DN ancestry contract (ADR-004): RDNs are separated by
/// UNESCAPED commas only (<c>\,</c> inside an RDN value does not separate; a
/// comma after an escaped backslash <c>\\</c> does), base matching is
/// case-insensitive via <see cref="GroupWeaver.Core.Model.Dn.Comparer"/>, and a
/// non-descendant is a value (-1), never an exception.
/// </summary>
public class DnPathTests
{
    // --- Parent -------------------------------------------------------------

    [Theory]
    [InlineData("CN=Anna Acker (u001),OU=Users,DC=lab", "OU=Users,DC=lab")]
    [InlineData(@"CN=Doe\, John,OU=X,DC=lab", "OU=X,DC=lab")] // escaped comma stays inside the RDN
    [InlineData(@"CN=Trailing\\,OU=X,DC=lab", "OU=X,DC=lab")] // escaped backslash: the comma DOES separate
    public void Parent_StripsExactlyTheLeadingRdn(string dn, string expectedParent)
    {
        Assert.Equal(expectedParent, DnPath.Parent(dn));
    }

    [Theory]
    [InlineData("DC=lab")]
    [InlineData(@"CN=Doe\, John")] // single RDN despite the (escaped) comma
    public void Parent_SingleRdn_ReturnsNull(string dn)
    {
        Assert.Null(DnPath.Parent(dn));
    }

    // --- RelativeDepth: depth counting ---------------------------------------

    [Fact]
    public void RelativeDepth_SameDn_IsZero()
    {
        Assert.Equal(0, DnPath.RelativeDepth("OU=X,DC=lab", "OU=X,DC=lab"));
    }

    [Fact]
    public void RelativeDepth_SameDnDifferingOnlyInCase_IsZero()
    {
        Assert.Equal(0, DnPath.RelativeDepth("ou=x,dc=lab", "OU=X,DC=LAB"));
    }

    [Fact]
    public void RelativeDepth_DirectChild_IsOne()
    {
        Assert.Equal(1, DnPath.RelativeDepth("CN=A,OU=X,DC=lab", "OU=X,DC=lab"));
    }

    [Fact]
    public void RelativeDepth_CaseTwistedBase_StillMatches()
    {
        Assert.Equal(1, DnPath.RelativeDepth("cn=a,ou=x,dc=lab", "OU=X,DC=LAB"));
    }

    [Fact]
    public void RelativeDepth_GrandChild_IsTwo()
    {
        Assert.Equal(2, DnPath.RelativeDepth("CN=A,OU=Sub,OU=X,DC=lab", "OU=X,DC=lab"));
    }

    [Fact]
    public void RelativeDepth_EscapedCommaInRdn_CountsAsOneLevel()
    {
        // The spec example: the escaped comma is part of the CN value, so the
        // whole RDN is ONE level below the base — not two.
        Assert.Equal(1, DnPath.RelativeDepth(@"CN=Doe\, John,OU=X,DC=lab", "OU=X,DC=lab"));
    }

    [Fact]
    public void RelativeDepth_EscapedBackslashBeforeComma_CommaSeparates()
    {
        // @"CN=Trailing\\" ends in an ESCAPED backslash, so the following comma
        // is a real separator: one level below OU=X,DC=lab.
        Assert.Equal(1, DnPath.RelativeDepth(@"CN=Trailing\\,OU=X,DC=lab", "OU=X,DC=lab"));
    }

    // --- RelativeDepth: non-descendants ---------------------------------------

    [Fact]
    public void RelativeDepth_SiblingSubtree_IsMinusOne()
    {
        Assert.Equal(-1, DnPath.RelativeDepth("CN=A,OU=Y,DC=lab", "OU=X,DC=lab"));
    }

    [Fact]
    public void RelativeDepth_BaseDeeperThanDn_IsMinusOne()
    {
        Assert.Equal(-1, DnPath.RelativeDepth("OU=X,DC=lab", "CN=A,OU=X,DC=lab"));
    }

    [Fact]
    public void RelativeDepth_EscapedCommaSuffixLookalike_IsNotADescendant()
    {
        // The raw string ends with ",OU=X,DC=lab", but that comma is escaped:
        // the DN is the single RDN @"CN=Doe\,OU=X" under DC=lab. A naive
        // suffix match would wrongly report depth 1 below OU=X,DC=lab.
        Assert.Equal(-1, DnPath.RelativeDepth(@"CN=Doe\,OU=X,DC=lab", "OU=X,DC=lab"));
    }
}
