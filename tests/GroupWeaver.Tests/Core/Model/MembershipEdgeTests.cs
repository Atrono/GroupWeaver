using GroupWeaver.Core.Model;

using Xunit;

namespace GroupWeaver.Tests.Core.Model;

/// <summary>
/// Pins <see cref="MembershipEdge"/> equality: case-insensitive on both DNs,
/// hash-consistent, but strictly direction-sensitive.
/// </summary>
public class MembershipEdgeTests
{
    private const string ParentDn = "CN=DL_Sales_Read,OU=Groups,OU=AGDLP-Lab,DC=agdlp,DC=lab";
    private const string ChildDn = "CN=GG_Sales_Read,OU=Groups,OU=AGDLP-Lab,DC=agdlp,DC=lab";

    [Fact]
    public void Equality_IdenticalEdges_AreEqual()
    {
        var a = new MembershipEdge(ParentDn, ChildDn);
        var b = new MembershipEdge(ParentDn, ChildDn);

        Assert.Equal(a, b);
    }

    [Fact]
    public void Equality_DnCaseDifferentEdges_AreEqualWithEqualHashes()
    {
        var a = new MembershipEdge(ParentDn, ChildDn);
        var b = new MembershipEdge(ParentDn.ToUpperInvariant(), ChildDn.ToLowerInvariant());

        Assert.Equal(a, b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());

        // The pair must collapse to a single entry in hash-based collections.
        var set = new HashSet<MembershipEdge> { a, b };
        Assert.Single(set);
    }

    [Fact]
    public void Equality_IsDirectionSensitive()
    {
        var forward = new MembershipEdge(ParentDn, ChildDn);
        var reversed = new MembershipEdge(ChildDn, ParentDn);

        Assert.NotEqual(forward, reversed);
    }
}
