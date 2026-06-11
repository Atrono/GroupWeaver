using GroupWeaver.Core.Model;

using Xunit;

namespace GroupWeaver.Tests.Core.Model;

/// <summary>
/// Pins <see cref="AdObjectKindMapper.Map"/>: objectClass-chain precedence
/// (case-insensitive), groupType scope bits with the security bit ignored,
/// builtin forcing DomainLocal, and External for everything unrecognized.
/// </summary>
public class AdObjectKindMapperTests
{
    [Fact]
    public void Map_UserChain_IsUser()
    {
        var kind = AdObjectKindMapper.Map(
            ["top", "person", "organizationalPerson", "user"], groupType: null);

        Assert.Equal(AdObjectKind.User, kind);
    }

    [Fact]
    public void Map_ChainWithUserAndComputer_IsComputer_PrecedencePinned()
    {
        // A computer's objectClass chain includes "user"; "computer" must win.
        var kind = AdObjectKindMapper.Map(
            ["top", "person", "organizationalPerson", "user", "computer"], groupType: null);

        Assert.Equal(AdObjectKind.Computer, kind);
    }

    [Theory]
    [InlineData(new[] { "top", "GROUP" }, 2, AdObjectKind.GlobalGroup)]
    [InlineData(new[] { "top", "person", "organizationalPerson", "user", "Computer" }, null, AdObjectKind.Computer)]
    public void Map_ObjectClassNames_AreCaseInsensitive(
        string[] objectClasses, int? groupType, AdObjectKind expected)
    {
        Assert.Equal(expected, AdObjectKindMapper.Map(objectClasses, groupType));
    }

    [Fact]
    public void Map_OrganizationalUnitChain_IsOrganizationalUnit()
    {
        var kind = AdObjectKindMapper.Map(["top", "organizationalUnit"], groupType: null);

        Assert.Equal(AdObjectKind.OrganizationalUnit, kind);
    }

    [Theory]
    [InlineData(-2147483646, AdObjectKind.GlobalGroup)] // 0x80000002 security global
    [InlineData(2, AdObjectKind.GlobalGroup)] // distribution global: security bit ignored
    public void Map_GlobalGroupType_IsGlobalGroup_SecurityBitIgnored(
        int groupType, AdObjectKind expected)
    {
        Assert.Equal(expected, AdObjectKindMapper.Map(["top", "group"], groupType));
    }

    [Theory]
    [InlineData(-2147483644, AdObjectKind.DomainLocalGroup)] // 0x80000004 security domain local
    [InlineData(4, AdObjectKind.DomainLocalGroup)] // distribution domain local
    [InlineData(-2147483640, AdObjectKind.UniversalGroup)] // 0x80000008 security universal
    [InlineData(8, AdObjectKind.UniversalGroup)] // distribution universal
    [InlineData(-2147483643, AdObjectKind.DomainLocalGroup)] // 0x80000005 builtin forces DL
    [InlineData(0, AdObjectKind.External)] // no scope bit: meaningless groupType
    [InlineData(16, AdObjectKind.External)] // 0x10 APP_BASIC only, still no scope bit
    public void Map_GroupTypeScopeBits_MapToExpectedScope(int groupType, AdObjectKind expected)
    {
        Assert.Equal(expected, AdObjectKindMapper.Map(["top", "group"], groupType));
    }

    [Fact]
    public void Map_GroupWithNullGroupType_IsExternal()
    {
        var kind = AdObjectKindMapper.Map(["top", "group"], groupType: null);

        Assert.Equal(AdObjectKind.External, kind);
    }

    [Theory]
    [InlineData(new object[] { new[] { "foreignSecurityPrincipal" } })]
    [InlineData(new object[] { new[] { "top", "contact" } })]
    public void Map_ForeignSecurityPrincipalOrUnrecognizedChain_IsExternal(string[] objectClasses)
    {
        Assert.Equal(AdObjectKind.External, AdObjectKindMapper.Map(objectClasses, groupType: null));
    }
}
