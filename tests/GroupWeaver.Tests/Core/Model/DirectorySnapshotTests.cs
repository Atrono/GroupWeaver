using GroupWeaver.Core.Model;

using Xunit;

namespace GroupWeaver.Tests.Core.Model;

/// <summary>
/// Pins <see cref="DirectorySnapshot"/> semantics: case-insensitive DN keying,
/// upsert on AddObject, loaded-vs-empty distinction, refresh-replace SetMembers,
/// de-duplication, cycle tolerance, and External fallback for unknown DNs.
/// </summary>
public class DirectorySnapshotTests
{
    private const string GroupADn = "CN=GG_Sales_Read,OU=Groups,OU=AGDLP-Lab,DC=agdlp,DC=lab";
    private const string GroupBDn = "CN=GG_HR_Read,OU=Groups,OU=AGDLP-Lab,DC=agdlp,DC=lab";
    private const string UserDn = "CN=Ada Lovelace,OU=Users,OU=AGDLP-Lab,DC=agdlp,DC=lab";

    [Fact]
    public void AddObject_TryGetObject_RoundTrips_CaseInsensitively()
    {
        var snapshot = new DirectorySnapshot();
        var group = MakeObject(GroupADn, AdObjectKind.GlobalGroup);

        snapshot.AddObject(group);

        Assert.True(snapshot.TryGetObject(GroupADn, out var exact));
        Assert.Same(group, exact);

        Assert.True(snapshot.TryGetObject(GroupADn.ToUpperInvariant(), out var caseVariant));
        Assert.Same(group, caseVariant);
    }

    [Fact]
    public void AddObject_SameDnDifferentCasing_Upserts_LatestWins()
    {
        var snapshot = new DirectorySnapshot();
        var first = MakeObject(GroupADn, AdObjectKind.GlobalGroup);
        var second = MakeObject(GroupADn.ToUpperInvariant(), AdObjectKind.UniversalGroup);

        snapshot.AddObject(first);
        snapshot.AddObject(second);

        Assert.Single(snapshot.Objects);
        Assert.True(snapshot.TryGetObject(GroupADn, out var stored));
        Assert.Same(second, stored);
    }

    [Fact]
    public void GetMembers_NeverLoadedDn_ReturnsNull_AndIsLoadedFalse()
    {
        var snapshot = new DirectorySnapshot();

        Assert.Null(snapshot.GetMembers(GroupADn));
        Assert.False(snapshot.IsLoaded(GroupADn));
    }

    [Fact]
    public void SetMembers_EmptyList_MarksLoaded_AndReturnsEmptyNotNull()
    {
        var snapshot = new DirectorySnapshot();

        snapshot.SetMembers(GroupADn, []);

        Assert.True(snapshot.IsLoaded(GroupADn));
        var members = snapshot.GetMembers(GroupADn);
        Assert.NotNull(members);
        Assert.Empty(members);
    }

    [Fact]
    public void SetMembers_RecordsParentToChildEdges()
    {
        var snapshot = new DirectorySnapshot();

        snapshot.SetMembers(GroupADn, [UserDn, GroupBDn]);

        Assert.Contains(new MembershipEdge(GroupADn, UserDn), snapshot.Edges);
        Assert.Contains(new MembershipEdge(GroupADn, GroupBDn), snapshot.Edges);
        Assert.Equal(2, snapshot.Edges.Count);
    }

    [Fact]
    public void SetMembers_DuplicateMemberDns_IncludingCaseVariants_AreDeduplicated()
    {
        var snapshot = new DirectorySnapshot();

        snapshot.SetMembers(GroupADn, [UserDn, UserDn, UserDn.ToUpperInvariant()]);

        var members = snapshot.GetMembers(GroupADn);
        Assert.NotNull(members);
        Assert.Single(members);
        Assert.Single(snapshot.Edges);
    }

    [Fact]
    public void SetMembers_CalledTwice_ReplacesMembers_NoStaleEdges()
    {
        var snapshot = new DirectorySnapshot();
        snapshot.SetMembers(GroupADn, [UserDn]);

        snapshot.SetMembers(GroupADn, [GroupBDn]);

        var members = snapshot.GetMembers(GroupADn);
        Assert.NotNull(members);
        Assert.Equal([GroupBDn], members);
        Assert.DoesNotContain(new MembershipEdge(GroupADn, UserDn), snapshot.Edges);
        Assert.Contains(new MembershipEdge(GroupADn, GroupBDn), snapshot.Edges);
    }

    [Fact]
    public void GetKind_UnknownDnIsExternal_KnownDnIsObjectsKind()
    {
        var snapshot = new DirectorySnapshot();

        Assert.Equal(AdObjectKind.External, snapshot.GetKind(UserDn));

        snapshot.AddObject(MakeObject(UserDn, AdObjectKind.User));

        Assert.Equal(AdObjectKind.User, snapshot.GetKind(UserDn));
    }

    [Fact]
    public void SetMembers_CircularNesting_StoresBothEdges_NoThrow()
    {
        // Mirrors the GG_Circle_A <-> GG_Circle_B lab fixture: the snapshot stores
        // cycles as-is; traversal termination is the consumer's concern.
        const string circleA = "CN=GG_Circle_A,OU=Groups,OU=AGDLP-Lab,DC=agdlp,DC=lab";
        const string circleB = "CN=GG_Circle_B,OU=Groups,OU=AGDLP-Lab,DC=agdlp,DC=lab";
        var snapshot = new DirectorySnapshot();

        snapshot.SetMembers(circleA, [circleB]);
        snapshot.SetMembers(circleB, [circleA]);

        Assert.True(snapshot.IsLoaded(circleA));
        Assert.True(snapshot.IsLoaded(circleB));
        Assert.Contains(new MembershipEdge(circleA, circleB), snapshot.Edges);
        Assert.Contains(new MembershipEdge(circleB, circleA), snapshot.Edges);
    }

    [Fact]
    public void SetMembers_ParentNotInObjects_IsAllowed_EdgesRetrievable()
    {
        var snapshot = new DirectorySnapshot();

        snapshot.SetMembers(GroupADn, [UserDn]);

        Assert.False(snapshot.TryGetObject(GroupADn, out _));
        Assert.True(snapshot.IsLoaded(GroupADn));
        Assert.Contains(new MembershipEdge(GroupADn, UserDn), snapshot.Edges);
    }

    private static AdObject MakeObject(string dn, AdObjectKind kind) => new()
    {
        Dn = dn,
        Kind = kind,
        Name = dn.Split(',')[0]["CN=".Length..],
    };
}
