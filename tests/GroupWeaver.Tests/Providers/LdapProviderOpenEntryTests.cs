using System.DirectoryServices;
using System.Runtime.Versioning;

using GroupWeaver.Providers;

using Xunit;

namespace GroupWeaver.Tests.Providers;

/// <summary>
/// Pins <see cref="LdapProvider.OpenEntry"/> (ADR-040 D1) — every
/// <see cref="DirectoryEntry"/> this provider ever constructs must request
/// Kerberos sealing (encryption) and signing (integrity) on top of secure
/// (integrated) authentication, never just <see cref="AuthenticationTypes.Secure"/>
/// alone (.NET's silent default). Pure unit test, no AD needed:
/// <see cref="DirectoryEntry"/>'s constructor with an unbound path plus explicit
/// null credentials does not eagerly bind to ADSI — only <c>AuthenticationType</c>
/// is read back, a plain in-memory field.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class LdapProviderOpenEntryTests
{
    [Fact]
    public void OpenEntry_SetsSealingAndSigningOnTopOfSecure()
    {
        using DirectoryEntry entry = LdapProvider.OpenEntry("LDAP://irrelevant-does-not-need-to-resolve");

        Assert.Equal(
            AuthenticationTypes.Secure | AuthenticationTypes.Sealing | AuthenticationTypes.Signing,
            entry.AuthenticationType);
    }

    [Fact]
    public void OpenEntry_UsesIntegratedAuth_NoStoredUsername()
    {
        using DirectoryEntry entry = LdapProvider.OpenEntry("LDAP://irrelevant-does-not-need-to-resolve");

        Assert.Null(entry.Username);
    }
}
