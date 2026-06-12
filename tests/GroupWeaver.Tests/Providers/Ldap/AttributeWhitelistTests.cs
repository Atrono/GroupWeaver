using GroupWeaver.Core.Model;
using GroupWeaver.Providers;

using Xunit;

namespace GroupWeaver.Tests.Providers.Ldap;

/// <summary>
/// Pins <see cref="AttributeWhitelist"/>: the exact 13-attribute fetch set
/// (privacy baseline — nothing else ever enters process memory), the per-kind
/// display sets, and <see cref="AttributeWhitelist.BuildAttributes"/> filtering
/// semantics (no structural keys, case-insensitive matching, canonical result
/// casing, "; "-joined multi-values, empty lists skipped).
/// </summary>
public class AttributeWhitelistTests
{
    /// <summary>One value per fetchable attribute, keyed exactly as fetched.</summary>
    private static Dictionary<string, IReadOnlyList<string>> AllFetchedProperties() => new()
    {
        ["distinguishedName"] = ["CN=X,OU=AGDLP-Lab,DC=agdlp,DC=lab"],
        ["name"] = ["X"],
        ["objectClass"] = ["top", "user"],
        ["sAMAccountName"] = ["x.sam"],
        ["groupType"] = ["-2147483646"],
        ["member"] = ["CN=M1", "CN=M2"],
        ["description"] = ["a description"],
        ["whenCreated"] = ["2026-06-12 10:00:00"],
        ["department"] = ["Sales"],
        ["title"] = ["Engineer"],
        ["primaryGroupID"] = ["513"],
        ["operatingSystem"] = ["Windows Server 2022"],
        ["dNSHostName"] = ["x.agdlp.lab"],
    };

    [Fact]
    public void FetchProperties_IsExactlyThePinnedSetOf13()
    {
        // Set equality, both directions: adding OR removing an attribute from
        // the fetch whitelist is a deliberate, reviewed decision (ADR-002 / AP 2.5).
        var expected = new HashSet<string>(StringComparer.Ordinal)
        {
            "distinguishedName",
            "name",
            "objectClass",
            "sAMAccountName",
            "groupType",
            "member",
            "description",
            "whenCreated",
            "department",
            "title",
            "primaryGroupID",
            "operatingSystem",
            "dNSHostName",
        };

        Assert.Equal(13, AttributeWhitelist.FetchProperties.Count);
        Assert.True(expected.SetEquals(AttributeWhitelist.FetchProperties));
    }

    public static TheoryData<AdObjectKind, string[]> DisplaySets() => new()
    {
        { AdObjectKind.User, ["description", "whenCreated", "department", "title", "primaryGroupID"] },
        { AdObjectKind.Computer, ["description", "whenCreated", "operatingSystem", "dNSHostName"] },
        { AdObjectKind.GlobalGroup, ["description", "whenCreated", "groupType"] },
        { AdObjectKind.DomainLocalGroup, ["description", "whenCreated", "groupType"] },
        { AdObjectKind.UniversalGroup, ["description", "whenCreated", "groupType"] },
        { AdObjectKind.OrganizationalUnit, ["description", "whenCreated"] },
        { AdObjectKind.External, ["description", "whenCreated"] },
    };

    [Theory]
    [MemberData(nameof(DisplaySets))]
    public void BuildAttributes_PerKindDisplaySets_ArePinned(AdObjectKind kind, string[] expectedKeys)
    {
        var attributes = AttributeWhitelist.BuildAttributes(kind, AllFetchedProperties());

        Assert.Equal(
            expectedKeys.OrderBy(k => k, StringComparer.Ordinal),
            attributes.Keys.OrderBy(k => k, StringComparer.Ordinal));
    }

    [Theory]
    [InlineData(AdObjectKind.User)]
    [InlineData(AdObjectKind.Computer)]
    [InlineData(AdObjectKind.GlobalGroup)]
    [InlineData(AdObjectKind.DomainLocalGroup)]
    [InlineData(AdObjectKind.UniversalGroup)]
    [InlineData(AdObjectKind.OrganizationalUnit)]
    [InlineData(AdObjectKind.External)]
    public void BuildAttributes_NeverEmitsStructuralKeys(AdObjectKind kind)
    {
        var attributes = AttributeWhitelist.BuildAttributes(kind, AllFetchedProperties());

        // Structural attributes map to typed members / membership edges, never
        // to the detail-panel dictionary. Result is case-insensitive-keyed, so
        // ContainsKey covers all casings.
        Assert.False(attributes.ContainsKey("distinguishedName"));
        Assert.False(attributes.ContainsKey("name"));
        Assert.False(attributes.ContainsKey("objectClass"));
        Assert.False(attributes.ContainsKey("sAMAccountName"));
        Assert.False(attributes.ContainsKey("member"));
    }

    [Fact]
    public void BuildAttributes_MatchesCaseInsensitively_EvenFromCaseSensitiveSource()
    {
        var properties = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
        {
            ["DESCRIPTION"] = ["loud"],
            ["whencreated"] = ["2026-06-12 10:00:00"],
            ["PrimaryGroupId"] = ["513"],
        };

        var attributes = AttributeWhitelist.BuildAttributes(AdObjectKind.User, properties);

        Assert.Equal(3, attributes.Count);
        Assert.Equal("loud", attributes["description"]);
        Assert.Equal("2026-06-12 10:00:00", attributes["whenCreated"]);
        Assert.Equal("513", attributes["primaryGroupID"]);
    }

    [Fact]
    public void BuildAttributes_ResultKeys_UseCanonicalWhitelistCasing()
    {
        var properties = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
        {
            ["whencreated"] = ["2026-06-12 10:00:00"],
            ["DNSHOSTNAME"] = ["x.agdlp.lab"],
            ["OPERATINGSYSTEM"] = ["Windows Server 2022"],
        };

        var attributes = AttributeWhitelist.BuildAttributes(AdObjectKind.Computer, properties);

        // Exact (ordinal) canonical casing in the emitted keys, regardless of
        // how the directory cased them.
        Assert.Equal(
            new[] { "dNSHostName", "operatingSystem", "whenCreated" },
            attributes.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase).ToArray());
    }

    [Fact]
    public void BuildAttributes_MultiValuedAttributes_AreJoinedWithSemicolonSpace()
    {
        var properties = new Dictionary<string, IReadOnlyList<string>>
        {
            ["description"] = ["first", "second", "third"],
        };

        var attributes = AttributeWhitelist.BuildAttributes(AdObjectKind.User, properties);

        Assert.Equal("first; second; third", attributes["description"]);
    }

    [Fact]
    public void BuildAttributes_EmptyValueLists_AreSkipped()
    {
        var properties = new Dictionary<string, IReadOnlyList<string>>
        {
            ["description"] = [],
            ["title"] = ["kept"],
        };

        var attributes = AttributeWhitelist.BuildAttributes(AdObjectKind.User, properties);

        Assert.False(attributes.ContainsKey("description"));
        Assert.Equal("kept", attributes["title"]);
    }
}
