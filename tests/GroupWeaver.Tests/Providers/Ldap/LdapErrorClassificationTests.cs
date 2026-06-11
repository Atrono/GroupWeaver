using GroupWeaver.Providers;

using Xunit;

namespace GroupWeaver.Tests.Providers.Ldap;

/// <summary>
/// Pins <see cref="LdapErrors.ClassifyHResult"/>: only the two
/// "object genuinely absent" HRESULTs classify as NotFound; known connectivity
/// failures AND every unknown HRESULT classify as Unavailable — an
/// unrecognized error must never silently become "object absent".
/// </summary>
public class LdapErrorClassificationTests
{
    [Theory]
    [InlineData(unchecked((int)0x80072030))] // ERROR_DS_NO_SUCH_OBJECT
    [InlineData(unchecked((int)0x80005000))] // E_ADS_BAD_PATHNAME
    public void ClassifyHResult_ObjectAbsentCodes_AreNotFound(int hresult)
    {
        Assert.Equal(LdapErrorKind.NotFound, LdapErrors.ClassifyHResult(hresult));
    }

    [Theory]
    [InlineData(unchecked((int)0x8007203A))] // server not operational
    [InlineData(unchecked((int)0x8007052E))] // logon failure
    [InlineData(unchecked((int)0x800704CF))] // network unreachable
    [InlineData(unchecked((int)0x8007054B))] // no such domain
    public void ClassifyHResult_ConnectivityCodes_AreUnavailable(int hresult)
    {
        Assert.Equal(LdapErrorKind.Unavailable, LdapErrors.ClassifyHResult(hresult));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(unchecked((int)0x80004005))] // E_FAIL
    public void ClassifyHResult_UnknownHResults_DefaultToUnavailable_ConservativePinned(int hresult)
    {
        // Pinned deliberately: an unknown failure must throw upstream
        // (DirectoryUnavailableException), never fake "object absent" data.
        Assert.Equal(LdapErrorKind.Unavailable, LdapErrors.ClassifyHResult(hresult));
    }
}
