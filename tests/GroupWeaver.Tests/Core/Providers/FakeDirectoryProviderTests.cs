using GroupWeaver.Core.Model;
using GroupWeaver.Core.Providers;

using Xunit;

namespace GroupWeaver.Tests.Core.Providers;

/// <summary>
/// Pins the <see cref="IDirectoryProvider"/> contract via the minimal in-memory fake:
/// connect summary (M1 DoD line), eager scope load semantics, and the
/// values-not-exceptions error model for unresolvable DNs.
/// </summary>
public class FakeDirectoryProviderTests
{
    private const string BaseDn = "OU=AGDLP-Lab,DC=agdlp,DC=lab";
    private const string SalesGroupDn = "CN=GG_Sales_Read,OU=Groups,OU=AGDLP-Lab,DC=agdlp,DC=lab";
    private const string EmptyGroupDn = "CN=GG_Empty,OU=Groups,OU=AGDLP-Lab,DC=agdlp,DC=lab";
    private const string DomainLocalDn = "CN=DL_Sales_Read,OU=Groups,OU=AGDLP-Lab,DC=agdlp,DC=lab";
    private const string UserDn = "CN=Ada Lovelace,OU=Users,OU=AGDLP-Lab,DC=agdlp,DC=lab";
    private const string OutOfScopeDn = "CN=Domain Admins,CN=Users,DC=agdlp,DC=lab";

    [Fact]
    public async Task ConnectAsync_ReturnsConnection_GroupCountProducesM1DoDLine()
    {
        var provider = CreateProvider();

        var connection = await provider.ConnectAsync();

        Assert.IsType<DirectoryConnection>(connection);
        Assert.Equal(
            "connected, 3 groups loaded",
            $"connected, {connection.GroupCount} groups loaded");
    }

    [Fact]
    public async Task LoadScopeAsync_AllInScopeGroupsLoaded_OutOfScopeMemberIsExternal()
    {
        var provider = CreateProvider();

        var snapshot = await provider.LoadScopeAsync(BaseDn);

        Assert.True(snapshot.IsLoaded(SalesGroupDn));
        Assert.True(snapshot.IsLoaded(DomainLocalDn));
        Assert.True(snapshot.IsLoaded(EmptyGroupDn));

        var emptyMembers = snapshot.GetMembers(EmptyGroupDn);
        Assert.NotNull(emptyMembers);
        Assert.Empty(emptyMembers);

        // DL_Sales_Read has a member DN outside the loaded scope: the object is
        // absent from the snapshot, so its kind resolves to External.
        var dlMembers = snapshot.GetMembers(DomainLocalDn);
        Assert.NotNull(dlMembers);
        Assert.Contains(OutOfScopeDn, dlMembers);
        Assert.Equal(AdObjectKind.External, snapshot.GetKind(OutOfScopeDn));
    }

    [Fact]
    public async Task UnknownDns_AreValuesNotExceptions()
    {
        var provider = CreateProvider();
        const string unknownDn = "CN=Ghost,OU=Groups,OU=AGDLP-Lab,DC=agdlp,DC=lab";

        var obj = await provider.GetObjectAsync(unknownDn);
        var members = await provider.GetMembersAsync(unknownDn);

        Assert.Null(obj);
        Assert.NotNull(members);
        Assert.Empty(members);
    }

    private static FakeDirectoryProvider CreateProvider()
    {
        var provider = new FakeDirectoryProvider();
        provider.AddObject(MakeObject(SalesGroupDn, AdObjectKind.GlobalGroup));
        provider.AddObject(MakeObject(EmptyGroupDn, AdObjectKind.GlobalGroup));
        provider.AddObject(MakeObject(DomainLocalDn, AdObjectKind.DomainLocalGroup));
        provider.AddObject(MakeObject(UserDn, AdObjectKind.User));

        provider.SetMembers(SalesGroupDn, UserDn);
        provider.SetMembers(DomainLocalDn, SalesGroupDn, OutOfScopeDn);
        provider.SetMembers(EmptyGroupDn);

        return provider;
    }

    private static AdObject MakeObject(string dn, AdObjectKind kind) => new()
    {
        Dn = dn,
        Kind = kind,
        Name = dn.Split(',')[0]["CN=".Length..],
    };
}
