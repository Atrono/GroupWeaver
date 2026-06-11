using GroupWeaver.Core.Model;
using GroupWeaver.Providers;

using Xunit;

namespace GroupWeaver.Tests.Providers.Ldap;

/// <summary>
/// Pins <see cref="LdapEntry"/> construction (case-insensitive property map,
/// throws on case-duplicate keys) and <see cref="LdapEntry.Map"/>: kind via the
/// objectClass chain + invariant groupType, name/sAMAccountName fallbacks,
/// whenCreated UTC normalization, and the per-kind whitelisted Attributes
/// (incl. <c>primaryGroupID</c> per ADR-002 — inspectable, never an edge).
/// </summary>
public class LdapEntryMapTests
{
    private static LdapEntry Entry(string dn, Dictionary<string, IReadOnlyList<string>> properties) =>
        new(dn, properties);

    [Fact]
    public void Map_ComputerAndUserChain_IsComputer()
    {
        var entry = Entry("CN=PC01,OU=AGDLP-Lab,DC=agdlp,DC=lab", new()
        {
            ["objectClass"] = ["top", "person", "organizationalPerson", "user", "computer"],
            ["name"] = ["PC01"],
            ["sAMAccountName"] = ["PC01$"],
        });

        var obj = LdapEntry.Map(entry);

        Assert.Equal(AdObjectKind.Computer, obj.Kind);
    }

    [Fact]
    public void Map_GroupWithGroupTypeString_ParsesInvariantly_IsGlobalGroup()
    {
        var entry = Entry("CN=GG_Sales,OU=AGDLP-Lab,DC=agdlp,DC=lab", new()
        {
            ["objectClass"] = ["top", "group"],
            ["name"] = ["GG_Sales"],
            ["sAMAccountName"] = ["GG_Sales"],
            ["groupType"] = ["-2147483646"], // 0x80000002 security global, as the raw string AD returns
        });

        var obj = LdapEntry.Map(entry);

        Assert.Equal(AdObjectKind.GlobalGroup, obj.Kind);
    }

    [Fact]
    public void Map_ForeignSecurityPrincipal_IsExternal()
    {
        var entry = Entry("CN=S-1-5-21-1-2-3-1234,CN=ForeignSecurityPrincipals,DC=agdlp,DC=lab", new()
        {
            ["objectClass"] = ["top", "foreignSecurityPrincipal"],
            ["name"] = ["S-1-5-21-1-2-3-1234"],
        });

        var obj = LdapEntry.Map(entry);

        Assert.Equal(AdObjectKind.External, obj.Kind);
    }

    [Fact]
    public void Map_MissingSamAccountName_IsNull()
    {
        var entry = Entry("CN=NoSam,OU=AGDLP-Lab,DC=agdlp,DC=lab", new()
        {
            ["objectClass"] = ["top", "organizationalUnit"],
            ["name"] = ["NoSam"],
        });

        var obj = LdapEntry.Map(entry);

        Assert.Null(obj.SamAccountName);
    }

    [Fact]
    public void Map_MissingName_FallsBackToDn()
    {
        const string dn = "CN=Nameless,OU=AGDLP-Lab,DC=agdlp,DC=lab";
        var entry = Entry(dn, new()
        {
            ["objectClass"] = ["top", "user"],
        });

        var obj = LdapEntry.Map(entry);

        Assert.Equal(dn, obj.Name);
    }

    [Fact]
    public void Map_ParsableWhenCreated_IsNormalizedToInvariantUtc()
    {
        var entry = Entry("CN=U1,OU=AGDLP-Lab,DC=agdlp,DC=lab", new()
        {
            ["objectClass"] = ["top", "person", "organizationalPerson", "user"],
            ["name"] = ["U1"],
            ["whenCreated"] = ["2026-06-12 10:30:05"], // round-trippable, no offset → assumed UTC
        });

        var obj = LdapEntry.Map(entry);

        Assert.Equal("2026-06-12T10:30:05Z", obj.Attributes["whenCreated"]);
    }

    [Fact]
    public void Map_UnparsableWhenCreated_IsKeptVerbatim()
    {
        var entry = Entry("CN=U1,OU=AGDLP-Lab,DC=agdlp,DC=lab", new()
        {
            ["objectClass"] = ["top", "person", "organizationalPerson", "user"],
            ["name"] = ["U1"],
            ["whenCreated"] = ["not-a-date"],
        });

        var obj = LdapEntry.Map(entry);

        Assert.Equal("not-a-date", obj.Attributes["whenCreated"]);
    }

    [Fact]
    public void Map_Group_GetsRawGroupTypeStringInAttributes()
    {
        var entry = Entry("CN=DL_FS,OU=AGDLP-Lab,DC=agdlp,DC=lab", new()
        {
            ["objectClass"] = ["top", "group"],
            ["name"] = ["DL_FS"],
            ["groupType"] = ["-2147483644"],
        });

        var obj = LdapEntry.Map(entry);

        Assert.Equal(AdObjectKind.DomainLocalGroup, obj.Kind);
        Assert.Equal("-2147483644", obj.Attributes["groupType"]);
    }

    [Fact]
    public void Map_User_GetsPrimaryGroupIdInAttributes_ButNoStructuralKeys()
    {
        var entry = Entry("CN=U1,OU=AGDLP-Lab,DC=agdlp,DC=lab", new()
        {
            ["objectClass"] = ["top", "person", "organizationalPerson", "user"],
            ["name"] = ["U1"],
            ["sAMAccountName"] = ["u1"],
            ["primaryGroupID"] = ["513"],
            ["member"] = ["CN=ShouldNeverShow"],
        });

        var obj = LdapEntry.Map(entry);

        // ADR-002: the primaryGroupID blind spot stays inspectable on the node...
        Assert.Equal("513", obj.Attributes["primaryGroupID"]);
        // ...but structural attributes never leak into the detail-panel dictionary.
        Assert.False(obj.Attributes.ContainsKey("member"));
        Assert.False(obj.Attributes.ContainsKey("objectClass"));
    }

    [Fact]
    public void Ctor_CaseDuplicatePropertyKeys_ThrowsArgumentException()
    {
        var caseSensitiveDuplicates = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
        {
            ["member"] = ["CN=A"],
            ["MEMBER"] = ["CN=B"],
        };

        Assert.Throws<ArgumentException>(() =>
            new LdapEntry("CN=Dup,OU=AGDLP-Lab,DC=agdlp,DC=lab", caseSensitiveDuplicates));
    }
}
