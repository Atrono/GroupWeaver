using GroupWeaver.Core.Model;

using Xunit;

namespace GroupWeaver.Tests.Core.Model;

/// <summary>
/// Pins the <see cref="AdObject"/> construction contract and the case-insensitive
/// attribute lookup guarantee.
/// </summary>
public class AdObjectTests
{
    [Fact]
    public void Construction_ExposesProperties_AndAttributesDefaultToEmpty()
    {
        var obj = new AdObject
        {
            Dn = "CN=GG_Sales_Read,OU=Groups,OU=AGDLP-Lab,DC=agdlp,DC=lab",
            Kind = AdObjectKind.GlobalGroup,
            Name = "GG_Sales_Read",
            SamAccountName = "GG_Sales_Read",
        };

        Assert.Equal("CN=GG_Sales_Read,OU=Groups,OU=AGDLP-Lab,DC=agdlp,DC=lab", obj.Dn);
        Assert.Equal(AdObjectKind.GlobalGroup, obj.Kind);
        Assert.Equal("GG_Sales_Read", obj.Name);
        Assert.Equal("GG_Sales_Read", obj.SamAccountName);
        Assert.NotNull(obj.Attributes);
        Assert.Empty(obj.Attributes);
    }

    [Fact]
    public void Attributes_LookupIsCaseInsensitive_EvenWhenInitializedFromCaseSensitiveDictionary()
    {
        // Ordinary Dictionary<string, string> uses the case-SENSITIVE ordinal comparer;
        // AdObject must still guarantee case-insensitive lookup after init.
        var caseSensitiveSource = new Dictionary<string, string>
        {
            ["description"] = "Sales read access",
        };

        var obj = new AdObject
        {
            Dn = "CN=GG_Sales_Read,OU=Groups,OU=AGDLP-Lab,DC=agdlp,DC=lab",
            Kind = AdObjectKind.GlobalGroup,
            Name = "GG_Sales_Read",
            Attributes = caseSensitiveSource,
        };

        Assert.True(obj.Attributes.ContainsKey("description"));
        Assert.True(obj.Attributes.ContainsKey("Description"));
        Assert.Equal("Sales read access", obj.Attributes["DESCRIPTION"]);
    }
}
