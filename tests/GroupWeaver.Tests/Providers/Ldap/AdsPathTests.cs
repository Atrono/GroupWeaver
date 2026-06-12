using GroupWeaver.Providers;

using Xunit;

namespace GroupWeaver.Tests.Providers.Ldap;

/// <summary>
/// Pins <see cref="AdsPath"/> (issue #16): ADsPath assembly for serverless and
/// server binding, and the '/' → <c>\/</c> escaping ADSI requires inside the DN
/// part — '/' is legal in an RDN, the directory returns it unescaped (RFC 2253
/// never escapes it), yet ADSI treats a raw '/' as its path separator. Without
/// the escaping, slash-RDN objects degrade to not-found and a crafted DN like
/// <c>evilhost/DC=x</c> can terminate the host segment under serverless binding.
/// </summary>
public class AdsPathTests
{
    private const string SlashOuDn = "OU=Research/Development,OU=AGDLP-Lab,DC=agdlp,DC=lab";
    private const string SlashOuDnEscaped = "OU=Research\\/Development,OU=AGDLP-Lab,DC=agdlp,DC=lab";

    [Fact]
    public void For_NullServer_UsesServerlessBinding()
    {
        Assert.Equal("LDAP://DC=agdlp,DC=lab", AdsPath.For(null, "DC=agdlp,DC=lab"));
    }

    [Fact]
    public void For_WithServer_PrefixesHostSegment()
    {
        Assert.Equal("LDAP://localhost/DC=agdlp,DC=lab", AdsPath.For("localhost", "DC=agdlp,DC=lab"));
    }

    [Fact]
    public void For_SlashInRdn_Serverless_EscapesTheSlash()
    {
        // The live lab regression fixture's DN, exactly as AD returns it (raw '/').
        Assert.Equal("LDAP://" + SlashOuDnEscaped, AdsPath.For(null, SlashOuDn));
    }

    [Fact]
    public void For_SlashInRdn_WithServer_EscapesOnlyTheDnPart()
    {
        // The host/DN separator slash must stay raw; only the DN's '/' is escaped.
        Assert.Equal("LDAP://localhost/" + SlashOuDnEscaped, AdsPath.For("localhost", SlashOuDn));
    }

    [Fact]
    public void For_MultipleSlashes_AllEscaped()
    {
        Assert.Equal(
            "LDAP://OU=a\\/b\\/c,DC=agdlp,DC=lab",
            AdsPath.For(null, "OU=a/b/c,DC=agdlp,DC=lab"));
    }

    [Fact]
    public void EscapeDn_HostInjectionShape_CannotTerminateHostSegment()
    {
        // Issue #16: under serverless binding, "LDAP://evilhost/DC=x" would have
        // redirected the integrated-auth bind to evilhost. Escaped, the whole
        // string stays inside the DN position and is merely unresolvable.
        Assert.Equal("evilhost\\/DC=x", AdsPath.EscapeDn("evilhost/DC=x"));
    }

    [Fact]
    public void EscapeDn_NoSlash_ReturnsDnUnchanged()
    {
        const string dn = "CN=GG_Sales_Staff,OU=Groups,OU=AGDLP-Lab,DC=agdlp,DC=lab";

        Assert.Equal(dn, AdsPath.EscapeDn(dn));
    }
}
