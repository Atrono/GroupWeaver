using GroupWeaver.Core.Model;
using GroupWeaver.Core.Providers;
using GroupWeaver.Providers;

using Xunit;

namespace GroupWeaver.Tests.Providers;

/// <summary>
/// Loads the embedded demo dataset once per test class: the provider plus the
/// fully loaded root-scope snapshot that most assertions run against.
/// </summary>
public sealed class DemoProviderFixture : IAsyncLifetime
{
    /// <summary>Root of the demo directory tree (the dataset's <c>rootDn</c>).</summary>
    public const string RootDn = "OU=AGDLP-Demo,DC=weavedemo,DC=example";

    /// <summary>The provider under test.</summary>
    public DemoProvider Provider { get; } = new();

    /// <summary>Snapshot of the full demo scope (<see cref="RootDn"/>).</summary>
    public DirectorySnapshot FullSnapshot { get; private set; } = null!;

    /// <inheritdoc />
    public async Task InitializeAsync() => FullSnapshot = await Provider.LoadScopeAsync(RootDn);

    /// <inheritdoc />
    public Task DisposeAsync() => Task.CompletedTask;
}

/// <summary>
/// Pins the <see cref="DemoProvider"/> contract AND the demo dataset itself
/// (<c>Demo/demo-directory.json</c>). The dataset is a public, deliberate artifact —
/// its counts and its built-in AGDLP/naming/circular/empty-group violations are the
/// RuleEngine test bed and must not silently change. If one of these tests fails,
/// suspect the dataset/implementation first, not the expectation.
/// </summary>
/// <remarks>
/// The M1 <c>--check</c> stdout process test that used to live here was deliberately
/// relocated to <c>tests/GroupWeaver.App.Tests/AppCliTests.cs</c> (AP 2.1 S2): it pins
/// the App executable's console contract (ADR-003 D4), not the provider contract.
/// </remarks>
public class DemoProviderTests : IClassFixture<DemoProviderFixture>
{
    private const string RootDn = DemoProviderFixture.RootDn;
    private const string UsersOuDn = "OU=Users,OU=AGDLP-Demo,DC=weavedemo,DC=example";

    private const string CircleADn = "CN=GG_Circle_A,OU=Groups,OU=AGDLP-Demo,DC=weavedemo,DC=example";
    private const string CircleBDn = "CN=GG_Circle_B,OU=Groups,OU=AGDLP-Demo,DC=weavedemo,DC=example";
    private const string DlFsSalesRwDn = "CN=DL_FS-Sales_RW,OU=Groups,OU=AGDLP-Demo,DC=weavedemo,DC=example";
    private const string UgAllStaffDn = "CN=UG_AllStaff,OU=Groups,OU=AGDLP-Demo,DC=weavedemo,DC=example";
    private const string DlFsFinanceRoDn = "CN=DL_FS-Finance_RO,OU=Groups,OU=AGDLP-Demo,DC=weavedemo,DC=example";
    private const string DlNestedRoDn = "CN=DL_Nested_RO,OU=Groups,OU=AGDLP-Demo,DC=weavedemo,DC=example";
    private const string DlFsItRwDn = "CN=DL_FS-IT_RW,OU=Groups,OU=AGDLP-Demo,DC=weavedemo,DC=example";
    private const string DlPrintHqRwDn = "CN=DL_Print-HQ_RW,OU=Groups,OU=AGDLP-Demo,DC=weavedemo,DC=example";
    private const string EmptyMarketingDn = "CN=GG_Empty_Marketing,OU=Groups,OU=AGDLP-Demo,DC=weavedemo,DC=example";
    private const string LegacyRoDn = "CN=DL_FS-Legacy_RO,OU=Groups,OU=AGDLP-Demo,DC=weavedemo,DC=example";
    private const string User001Dn = "CN=Anna Acker (u001),OU=Users,OU=AGDLP-Demo,DC=weavedemo,DC=example";
    private const string User002Dn = "CN=Ben Acker (u002),OU=Users,OU=AGDLP-Demo,DC=weavedemo,DC=example";
    private const string DomainAdminsDn = "CN=Domain Admins,CN=Users,DC=weavedemo,DC=example";
    private const string PrintOperatorsDn = "CN=Print Operators,CN=Builtin,DC=weavedemo,DC=example";

    private readonly DemoProviderFixture _fixture;

    public DemoProviderTests(DemoProviderFixture fixture) => _fixture = fixture;

    // --- ConnectAsync -----------------------------------------------------

    [Fact]
    public async Task ConnectAsync_ReportsFortyGroups()
    {
        var connection = await _fixture.Provider.ConnectAsync();

        Assert.Equal(40, connection.GroupCount);
    }

    [Fact]
    public async Task ConnectAsync_DescriptionMentionsDemo()
    {
        var connection = await _fixture.Provider.ConnectAsync();

        Assert.Contains("demo", connection.Description, StringComparison.OrdinalIgnoreCase);
    }

    // --- GetRootCandidatesAsync -------------------------------------------

    [Fact]
    public async Task GetRootCandidatesAsync_Returns44OusAndGroupsOnly()
    {
        var candidates = await _fixture.Provider.GetRootCandidatesAsync();

        Assert.Equal(44, candidates.Count);
        Assert.Equal(4, candidates.Count(c => c.Kind == AdObjectKind.OrganizationalUnit));
        Assert.Equal(40, candidates.Count(c => IsGroupKind(c.Kind)));
        Assert.All(candidates, c =>
            Assert.True(c.Kind == AdObjectKind.OrganizationalUnit || IsGroupKind(c.Kind)));
    }

    // --- LoadScopeAsync: full scope ----------------------------------------

    [Fact]
    public void FullScope_Has194ObjectsAnd141Edges()
    {
        var snapshot = _fixture.FullSnapshot;

        Assert.Equal(194, snapshot.Objects.Count);
        Assert.Equal(141, snapshot.Edges.Count);
    }

    [Theory]
    [InlineData(AdObjectKind.User, 140)]
    [InlineData(AdObjectKind.GlobalGroup, 18)]
    [InlineData(AdObjectKind.DomainLocalGroup, 19)]
    [InlineData(AdObjectKind.UniversalGroup, 3)]
    [InlineData(AdObjectKind.Computer, 10)]
    [InlineData(AdObjectKind.OrganizationalUnit, 4)]
    public void FullScope_PinsPerKindObjectCount(AdObjectKind kind, int expectedCount)
    {
        Assert.Equal(expectedCount, _fixture.FullSnapshot.Objects.Count(o => o.Kind == kind));
    }

    [Fact]
    public void FullScope_EveryGroupIsLoaded()
    {
        var snapshot = _fixture.FullSnapshot;
        var groups = snapshot.Objects.Where(o => IsGroupKind(o.Kind)).ToList();

        Assert.Equal(40, groups.Count);
        Assert.All(groups, g => Assert.True(
            snapshot.IsLoaded(g.Dn),
            $"group '{g.Dn}' was left in not-loaded state"));
    }

    // --- Dataset violation inventory (RuleEngine test bed — must not vanish) ---

    [Fact]
    public void Violations_CircularNesting_BothEdgesPresent()
    {
        var edges = _fixture.FullSnapshot.Edges;

        Assert.Contains(new MembershipEdge(CircleADn, CircleBDn), edges);
        Assert.Contains(new MembershipEdge(CircleBDn, CircleADn), edges);
    }

    [Fact]
    public void Violations_CircularNesting_BoundedMemberWalkTerminates()
    {
        var snapshot = _fixture.FullSnapshot;
        var visited = new HashSet<string>(Dn.Comparer);
        var pending = new Stack<string>();
        pending.Push(CircleADn);

        // Hard bound: a visited-set walk can pop each DN at most once per edge,
        // so anything beyond objects + edges means the cycle did not terminate.
        var bound = snapshot.Objects.Count + snapshot.Edges.Count + 1;
        var steps = 0;
        while (pending.Count > 0)
        {
            Assert.True(++steps <= bound, "membership walk exceeded bound — circular nesting did not terminate");
            var dn = pending.Pop();
            if (!visited.Add(dn))
            {
                continue;
            }

            IReadOnlyList<string> members = snapshot.GetMembers(dn) ?? [];
            foreach (var member in members)
            {
                pending.Push(member);
            }
        }

        Assert.Contains(CircleADn, visited);
        Assert.Contains(CircleBDn, visited);
    }

    [Theory]
    [InlineData(DlFsSalesRwDn, User001Dn)] // user directly in a DL group
    [InlineData(UgAllStaffDn, User002Dn)] // user directly in a UG group
    [InlineData(DlFsFinanceRoDn, DlNestedRoDn)] // DL nested in DL
    public void Violations_AgdlpEdgeIsPresent(string parentDn, string childDn)
    {
        Assert.Contains(new MembershipEdge(parentDn, childDn), _fixture.FullSnapshot.Edges);
    }

    [Theory]
    [InlineData("CN=SalesTeamGlobal,OU=Groups,OU=AGDLP-Demo,DC=weavedemo,DC=example", "SalesTeamGlobal", AdObjectKind.GlobalGroup)]
    [InlineData("CN=dl-finance-extra,OU=Groups,OU=AGDLP-Demo,DC=weavedemo,DC=example", "dl-finance-extra", AdObjectKind.DomainLocalGroup)]
    [InlineData("CN=GG_X,OU=Groups,OU=AGDLP-Demo,DC=weavedemo,DC=example", "GG_X", AdObjectKind.GlobalGroup)]
    public void Violations_NamingViolationGroupExists(string dn, string name, AdObjectKind kind)
    {
        Assert.True(_fixture.FullSnapshot.TryGetObject(dn, out var obj));
        Assert.Equal(name, obj.Name);
        Assert.Equal(kind, obj.Kind);
    }

    [Fact]
    public void Violations_ExactlyTwelveEmptyGroups()
    {
        var snapshot = _fixture.FullSnapshot;
        var emptyGroupDns = snapshot.Objects
            .Where(o => IsGroupKind(o.Kind))
            .Where(o => snapshot.IsLoaded(o.Dn) && snapshot.GetMembers(o.Dn)!.Count == 0)
            .Select(o => o.Dn)
            .ToList();

        Assert.Equal(12, emptyGroupDns.Count);
        Assert.Contains(EmptyMarketingDn, emptyGroupDns, Dn.Comparer);
        Assert.Contains(LegacyRoDn, emptyGroupDns, Dn.Comparer);
    }

    [Theory]
    [InlineData(DlFsItRwDn, DomainAdminsDn)]
    [InlineData(DlPrintHqRwDn, PrintOperatorsDn)]
    public void Violations_BuiltinMemberDnIsExternalInSnapshot(string groupDn, string externalDn)
    {
        var snapshot = _fixture.FullSnapshot;
        var members = snapshot.GetMembers(groupDn);

        Assert.NotNull(members);
        Assert.Contains(externalDn, members);
        Assert.Equal(AdObjectKind.External, snapshot.GetKind(externalDn));
    }

    // --- LoadScopeAsync: sub-scope ------------------------------------------

    [Fact]
    public async Task SubScope_UsersOu_ReturnsOnlyThatSubtree()
    {
        var snapshot = await _fixture.Provider.LoadScopeAsync(UsersOuDn);

        Assert.Equal(141, snapshot.Objects.Count); // the OU itself + 140 users
        Assert.All(snapshot.Objects, o =>
            Assert.EndsWith(UsersOuDn, o.Dn, StringComparison.OrdinalIgnoreCase));
    }

    // --- GetObjectAsync ------------------------------------------------------

    [Fact]
    public async Task GetObjectAsync_KnownDn_RoundTrips()
    {
        var obj = await _fixture.Provider.GetObjectAsync(User001Dn);

        Assert.NotNull(obj);
        Assert.Equal(AdObjectKind.User, obj.Kind);
        Assert.Equal("Anna Acker (u001)", obj.Name);
        Assert.Equal("u001", obj.SamAccountName);
    }

    [Fact]
    public async Task GetObjectAsync_UnknownDn_ReturnsNull()
    {
        var obj = await _fixture.Provider.GetObjectAsync(
            "CN=Ghost,OU=Groups,OU=AGDLP-Demo,DC=weavedemo,DC=example");

        Assert.Null(obj);
    }

    // --- GetMembersAsync ------------------------------------------------------

    [Fact]
    public async Task GetMembersAsync_ExternalMember_IsSynthesizedAsExternalKind()
    {
        var members = await _fixture.Provider.GetMembersAsync(DlFsItRwDn);

        var external = Assert.Single(members, m => m.Kind == AdObjectKind.External);
        Assert.Equal(DomainAdminsDn, external.Dn);
        Assert.Equal(DomainAdminsDn, external.Name);
    }

    [Fact]
    public async Task GetMembersAsync_UnknownParent_ReturnsEmptyListWithoutThrowing()
    {
        var members = await _fixture.Provider.GetMembersAsync(
            "CN=Ghost,OU=Groups,OU=AGDLP-Demo,DC=weavedemo,DC=example");

        Assert.NotNull(members);
        Assert.Empty(members);
    }

    // --- AdObject.Attributes ---------------------------------------------------

    [Fact]
    public async Task Attributes_LookupIsCaseInsensitive_AndKeysAreWhitelistShaped()
    {
        var group = await _fixture.Provider.GetObjectAsync(DlFsSalesRwDn);

        Assert.NotNull(group);
        Assert.Equal("Resource access: FS-Sales (read-write)", group.Attributes["DESCRIPTION"]);

        // The provider owns whitelist enforcement: every attribute key in the
        // dataset must stay within this modest detail-panel shape.
        var whitelist = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "description",
            "department",
            "title",
            "operatingSystem",
        };
        Assert.All(
            _fixture.FullSnapshot.Objects.SelectMany(o => o.Attributes.Keys),
            key => Assert.Contains(key, whitelist));
    }

    private static bool IsGroupKind(AdObjectKind kind) =>
        kind is AdObjectKind.GlobalGroup or AdObjectKind.DomainLocalGroup or AdObjectKind.UniversalGroup;
}
