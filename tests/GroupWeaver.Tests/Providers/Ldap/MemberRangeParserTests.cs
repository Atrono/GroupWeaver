using GroupWeaver.Providers;

using Xunit;

namespace GroupWeaver.Tests.Providers.Ldap;

/// <summary>
/// Pins <see cref="MemberRangeParser"/>: the exact accepted shape
/// <c>member;range=&lt;low&gt;-&lt;high|*&gt;</c> (case-insensitive prefix,
/// inclusive bounds as returned by AD), false-with-defaults for everything
/// else, and the next-range request builder.
/// </summary>
public class MemberRangeParserTests
{
    [Fact]
    public void TryParse_BoundedRange_ReturnsInclusiveBoundsAsReturnedByAd()
    {
        bool ok = MemberRangeParser.TryParse("member;range=0-1499", out int start, out int? end);

        Assert.True(ok);
        Assert.Equal(0, start);
        Assert.Equal(1499, end);
    }

    [Fact]
    public void TryParse_StarHighBound_ReturnsNullEnd()
    {
        bool ok = MemberRangeParser.TryParse("member;range=1500-*", out int start, out int? end);

        Assert.True(ok);
        Assert.Equal(1500, start);
        Assert.Null(end);
    }

    [Fact]
    public void TryParse_PrefixIsCaseInsensitive()
    {
        bool ok = MemberRangeParser.TryParse("MEMBER;Range=0-10", out int start, out int? end);

        Assert.True(ok);
        Assert.Equal(0, start);
        Assert.Equal(10, end);
    }

    [Theory]
    [InlineData("member")] // plain attribute, not ranged
    [InlineData("memberOf;range=0-10")] // other attribute name
    [InlineData("description;range=0-10")] // other attribute name
    [InlineData("member;range=x-y")] // non-numeric bounds
    [InlineData("member;range0-10")] // missing '='
    [InlineData("member;range=010")] // missing '-'
    [InlineData("member;range=-1-10")] // negative low bound
    [InlineData("member;range=0--5")] // negative high bound
    [InlineData("member;range=-10")] // empty low bound
    [InlineData("member;range=10-")] // empty high bound
    [InlineData("member;range=-")] // both bounds empty
    [InlineData("member;range=0-10;binary")] // trailing options
    [InlineData("member;range=10-5")] // high < low
    [InlineData("member;range=0-99999999999999999999")] // overflow
    [InlineData("member;range=99999999999999999999-*")] // overflow in low bound
    [InlineData("member;range=")] // no range expression at all
    public void TryParse_InvalidShapes_ReturnFalseWithDefaultOuts(string attributeName)
    {
        bool ok = MemberRangeParser.TryParse(attributeName, out int start, out int? end);

        Assert.False(ok);
        Assert.Equal(0, start);
        Assert.Null(end);
    }

    [Theory]
    [InlineData(0, "member;range=0-*")]
    [InlineData(1500, "member;range=1500-*")]
    public void NextRangeAttribute_BuildsOpenEndedRequest(int nextStart, string expected)
    {
        Assert.Equal(expected, MemberRangeParser.NextRangeAttribute(nextStart));
    }

    [Fact]
    public void NextRangeAttribute_NegativeStart_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => MemberRangeParser.NextRangeAttribute(-1));
    }
}
