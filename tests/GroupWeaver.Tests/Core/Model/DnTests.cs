using GroupWeaver.Core.Model;

using Xunit;

namespace GroupWeaver.Tests.Core.Model;

/// <summary>
/// Pins the DN comparison policy: ordinal, case-insensitive, never canonicalizing.
/// </summary>
public class DnTests
{
    [Fact]
    public void Comparer_SameDnDifferingOnlyInCase_IsEqualWithSameHash()
    {
        const string lower = "cn=gg_sales_read,ou=groups,ou=agdlp-lab,dc=agdlp,dc=lab";
        const string mixed = "CN=GG_Sales_Read,OU=Groups,OU=AGDLP-Lab,DC=agdlp,DC=lab";

        Assert.True(Dn.Comparer.Equals(lower, mixed));
        Assert.Equal(Dn.Comparer.GetHashCode(lower), Dn.Comparer.GetHashCode(mixed));
    }

    [Fact]
    public void Comparer_DifferentDns_AreNotEqual()
    {
        const string sales = "CN=GG_Sales_Read,OU=Groups,OU=AGDLP-Lab,DC=agdlp,DC=lab";
        const string hr = "CN=GG_HR_Read,OU=Groups,OU=AGDLP-Lab,DC=agdlp,DC=lab";

        Assert.False(Dn.Comparer.Equals(sales, hr));
    }
}
