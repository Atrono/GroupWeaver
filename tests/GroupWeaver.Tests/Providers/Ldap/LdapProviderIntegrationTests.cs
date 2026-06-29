using System.Runtime.Versioning;

using GroupWeaver.Core.Model;
using GroupWeaver.Core.Providers;
using GroupWeaver.Providers;

using Xunit;

namespace GroupWeaver.Tests.Providers.Ldap;

/// <summary>
/// Eagerly loads the full live AGDLP-Lab scope ONCE per test class; the
/// read-only assertions share this snapshot to keep integration wall time low.
/// Loading is guarded by <see cref="AdFactAttribute.IsLabReachable"/>: when the
/// lab OU is unreachable the snapshot stays unloaded — every test using it is
/// then skipped by <see cref="AdFactAttribute"/> anyway.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class LdapLabFixture : IAsyncLifetime
{
    /// <summary>The lab DC; this box (CLAUDE.md environment).</summary>
    public const string Server = "localhost";

    /// <summary>Root of the seeded fixture tree (tools/seed-testad.ps1).</summary>
    public const string LabDn = "OU=AGDLP-Lab,DC=agdlp,DC=lab";

    /// <summary>The provider under test, pinned to the lab OU.</summary>
    public LdapProvider Provider { get; } = new(Server, LabDn);

    /// <summary>Snapshot of the full lab scope (<see cref="LabDn"/>).</summary>
    public DirectorySnapshot FullSnapshot { get; private set; } = null!;

    /// <inheritdoc />
    public async Task InitializeAsync()
    {
        if (AdFactAttribute.IsLabReachable)
        {
            FullSnapshot = await Provider.LoadScopeAsync(LabDn);
        }
    }

    /// <inheritdoc />
    public Task DisposeAsync() => Task.CompletedTask;
}

/// <summary>
/// Integration tests for <see cref="LdapProvider"/> against the live
/// <c>OU=AGDLP-Lab</c> fixtures seeded by <c>tools/seed-testad.ps1</c> (195
/// objects: 140 users, 18 GG + 19 DL + 3 UG groups, 10 computers, 5 OUs — one
/// of them the empty slash-RDN OU <c>OU=Research/Development</c>, the issue-#16
/// ADsPath-escaping regression fixture; 140 membership edges — one targets the
/// foreign-domain FSP outside the OU; 12 empty groups; the
/// GG_Circle_A↔GG_Circle_B circular nesting). Excluded in
/// CI via the class-level <c>Category=RequiresAd</c>
/// trait; skipped with a loud warning off the lab DC via <see cref="AdFactAttribute"/>.
/// If one of these tests fails, suspect the provider or a drifted fixture
/// first, not the expectation — counts here are derived from the seed script.
/// </summary>
[SupportedOSPlatform("windows")]
[Trait(TestCategories.Category, TestCategories.RequiresAd)]
public class LdapProviderIntegrationTests : IClassFixture<LdapLabFixture>
{
    private const string LabDn = LdapLabFixture.LabDn;
    private const string GroupsOuDn = "OU=Groups,OU=AGDLP-Lab,DC=agdlp,DC=lab";

    /// <summary>The issue-#16 regression fixture: an empty OU whose RDN contains a raw
    /// '/' exactly as AD returns it (`name` = <c>Research/Development</c>). Resolvable
    /// only with ADsPath '/'-escaping — pre-fix it silently degraded to not-found.</summary>
    private const string SlashOuDn = "OU=Research/Development,OU=AGDLP-Lab,DC=agdlp,DC=lab";

    private const string CircleADn = "CN=GG_Circle_A,OU=Groups,OU=AGDLP-Lab,DC=agdlp,DC=lab";
    private const string CircleBDn = "CN=GG_Circle_B,OU=Groups,OU=AGDLP-Lab,DC=agdlp,DC=lab";
    private const string GgSalesStaffDn = "CN=GG_Sales_Staff,OU=Groups,OU=AGDLP-Lab,DC=agdlp,DC=lab";
    private const string DlFsSalesRwDn = "CN=DL_FS-Sales_RW,OU=Groups,OU=AGDLP-Lab,DC=agdlp,DC=lab";
    private const string EmptyMarketingDn = "CN=GG_Empty_Marketing,OU=Groups,OU=AGDLP-Lab,DC=agdlp,DC=lab";
    private const string LegacyRoDn = "CN=DL_FS-Legacy_RO,OU=Groups,OU=AGDLP-Lab,DC=agdlp,DC=lab";
    private const string User001Dn = "CN=Anna Acker (u001),OU=Users,OU=AGDLP-Lab,DC=agdlp,DC=lab";
    private const string DlAppErpRwDn = "CN=DL_App-ERP_RW,OU=Groups,OU=AGDLP-Lab,DC=agdlp,DC=lab";
    private const string UgManagersDn = "CN=UG_Managers,OU=Groups,OU=AGDLP-Lab,DC=agdlp,DC=lab";

    /// <summary>Fixed dangling cross-forest SID seeded by Ensure-ForeignSidMember.</summary>
    private const string ForeignSid = "S-1-5-21-1100000001-2200000002-3300000003-1106";

    /// <summary>The FSP the DC system-created for <see cref="ForeignSid"/> — deliberately
    /// OUTSIDE OU=AGDLP-Lab (FSPs always live in CN=ForeignSecurityPrincipals).</summary>
    private const string ForeignFspDn =
        "CN=" + ForeignSid + ",CN=ForeignSecurityPrincipals,DC=agdlp,DC=lab";

    private readonly LdapLabFixture _fixture;

    public LdapProviderIntegrationTests(LdapLabFixture fixture) => _fixture = fixture;

    private static bool IsGroup(AdObjectKind kind) =>
        kind is AdObjectKind.GlobalGroup or AdObjectKind.DomainLocalGroup or AdObjectKind.UniversalGroup;

    // --- T1: ConnectAsync ---------------------------------------------------

    [AdFact]
    public async Task ConnectAsync_ReportsFortyGroups()
    {
        var connection = await _fixture.Provider.ConnectAsync();

        Assert.Equal(40, connection.GroupCount);
    }

    [AdFact]
    public async Task ConnectAsync_DescriptionNamesServerAndLabBase()
    {
        var connection = await _fixture.Provider.ConnectAsync();

        Assert.Contains("agdlp", connection.Description, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("LDAP localhost — OU=AGDLP-Lab,DC=agdlp,DC=lab", connection.Description);
    }

    // --- T2/T3: full-scope load ----------------------------------------------

    [AdFact]
    public void LoadScope_Lab_Loads195Objects_WithExpectedKindBreakdown()
    {
        var snapshot = _fixture.FullSnapshot;
        var byKind = snapshot.Objects
            .GroupBy(o => o.Kind)
            .ToDictionary(g => g.Key, g => g.Count());

        // 195/5 (was 194/4) since the empty slash-RDN OU regression fixture
        // landed with issue #16; everything else is unchanged.
        Assert.Equal(195, snapshot.Objects.Count);
        Assert.Equal(140, byKind[AdObjectKind.User]);
        Assert.Equal(18, byKind[AdObjectKind.GlobalGroup]);
        Assert.Equal(19, byKind[AdObjectKind.DomainLocalGroup]);
        Assert.Equal(3, byKind[AdObjectKind.UniversalGroup]);
        Assert.Equal(10, byKind[AdObjectKind.Computer]);
        Assert.Equal(5, byKind[AdObjectKind.OrganizationalUnit]);
        Assert.Equal(6, byKind.Count); // nothing unclassified / External in scope
    }

    [AdFact]
    public void LoadScope_Lab_Has140MembershipEdges()
    {
        // 139 in-OU edges + the DL_App-ERP_RW → foreign-domain FSP edge added by
        // the seed script's Ensure-ForeignSidMember (commit 264ce7d). The demo
        // dataset has 141: no FSP edge but two built-in member edges instead
        // (divergence documented in src/Providers/Demo/README.md).
        Assert.Equal(140, _fixture.FullSnapshot.Edges.Count);
    }

    // --- T4: loaded-vs-empty semantics ----------------------------------------

    [AdFact]
    public void LoadScope_Lab_EveryGroupIsLoaded_ExactlyTwelveAreEmpty()
    {
        var snapshot = _fixture.FullSnapshot;
        var groups = snapshot.Objects.Where(o => IsGroup(o.Kind)).ToList();

        Assert.Equal(40, groups.Count);
        Assert.All(groups, g => Assert.True(snapshot.IsLoaded(g.Dn), $"group not marked loaded: {g.Dn}"));
        Assert.Equal(12, groups.Count(g => snapshot.GetMembers(g.Dn)!.Count == 0));
    }

    [AdFact]
    public void LoadScope_Lab_SeededEmptyGroups_AreLoadedAndEmpty()
    {
        var snapshot = _fixture.FullSnapshot;

        // Loaded-but-empty (empty list) is distinct from never-loaded (null).
        var marketing = snapshot.GetMembers(EmptyMarketingDn);
        Assert.NotNull(marketing);
        Assert.Empty(marketing);

        var legacy = snapshot.GetMembers(LegacyRoDn);
        Assert.NotNull(legacy);
        Assert.Empty(legacy);
    }

    // --- T5: circular nesting (provider-side termination proof) ----------------

    [AdFact]
    public void LoadScope_Lab_CircularNesting_BothDirectionsPresent()
    {
        // That LoadScopeAsync completed at all is the provider-side termination
        // proof for the GG_Circle_A <-> GG_Circle_B cycle; both edges must survive.
        var edges = _fixture.FullSnapshot.Edges;

        Assert.Contains(new MembershipEdge(CircleADn, CircleBDn), edges);
        Assert.Contains(new MembershipEdge(CircleBDn, CircleADn), edges);
    }

    // --- T6: paging -------------------------------------------------------------

    [AdFact]
    public async Task LoadScope_PageSize50_YieldsIdentical195Objects()
    {
        // 195 objects (194 + the issue-#16 slash-OU fixture) / page size 50
        // forces 4 server pages (RFC 2696).
        var provider = new LdapProvider(LdapLabFixture.Server, LabDn, pageSize: 50);

        var snapshot = await provider.LoadScopeAsync(LabDn);

        Assert.Equal(195, snapshot.Objects.Count);
    }

    // --- T7: attribute whitelist enforcement -------------------------------------

    [AdFact]
    public void LoadScope_User001_AttributesAreWhitelistFiltered()
    {
        Assert.True(_fixture.FullSnapshot.TryGetObject(User001Dn, out var user));

        Assert.Equal(AdObjectKind.User, user!.Kind);
        Assert.Equal("u001", user.SamAccountName);
        Assert.Equal("513", user.Attributes["primaryGroupID"]);

        // Seeded but fetched-never (outside AttributeWhitelist.FetchProperties):
        Assert.False(user.Attributes.ContainsKey("givenName"));
        Assert.False(user.Attributes.ContainsKey("sn"));

        // Fetched but structural — must never leak into Attributes:
        Assert.False(user.Attributes.ContainsKey("member"));
        Assert.False(user.Attributes.ContainsKey("objectClass"));
        Assert.False(user.Attributes.ContainsKey("distinguishedName"));
        Assert.False(user.Attributes.ContainsKey("name"));
        Assert.False(user.Attributes.ContainsKey("sAMAccountName"));

        // whenCreated is normalized to invariant UTC.
        Assert.Matches(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}Z$", user.Attributes["whenCreated"]);
    }

    // --- T8: unavailable directory -------------------------------------------------

    [AdFact]
    public async Task ConnectAsync_ClosedPort_ThrowsDirectoryUnavailable()
    {
        var provider = new LdapProvider("localhost:50000"); // nothing listens there

        await Assert.ThrowsAsync<DirectoryUnavailableException>(() => provider.ConnectAsync());
    }

    // --- T9: narrow scope, out-of-scope members --------------------------------------

    [AdFact]
    public async Task LoadScope_GroupsOuOnly_OutOfScopeMembersAreExternal_EdgesKept()
    {
        var snapshot = await _fixture.Provider.LoadScopeAsync(GroupsOuDn);

        Assert.Equal(41, snapshot.Objects.Count); // the Groups OU itself + 40 groups
        Assert.Equal(40, snapshot.Objects.Count(o => IsGroup(o.Kind)));

        // u001 sits in OU=Users — outside this scope — so its kind resolves External,
        // but the user->group membership edges must still be present.
        Assert.Equal(AdObjectKind.External, snapshot.GetKind(User001Dn));
        var edges = snapshot.Edges;
        Assert.Contains(new MembershipEdge(DlFsSalesRwDn, User001Dn), edges);
        Assert.Contains(new MembershipEdge(GgSalesStaffDn, User001Dn), edges);
    }

    // --- Scope-base probe semantics (issues #16/#17) ---------------------------------

    [AdFact]
    public async Task LoadScope_SlashRdnOuAsBase_ReturnsExactlyThatOu()
    {
        // The AP 2.1 picker scenario: the user picks the slash-named OU itself as
        // scope root. Pre-#16 the base probe could not even resolve it; post-fix
        // the (deliberately empty) OU is the snapshot's single object.
        var snapshot = await _fixture.Provider.LoadScopeAsync(SlashOuDn);

        var only = Assert.Single(snapshot.Objects);
        Assert.Equal(AdObjectKind.OrganizationalUnit, only.Kind);
        Assert.True(Dn.Comparer.Equals(SlashOuDn, only.Dn), $"unexpected object in scope: '{only.Dn}'");
    }

    [AdFact]
    public async Task LoadScope_NonexistentBase_ReturnsEmptySnapshot()
    {
        // #17's preserved base-probe semantics: an unknown/vanished base is a
        // value (empty snapshot), never an exception — probed up front, no
        // whole-operation NotFound fallback anymore.
        var snapshot = await _fixture.Provider.LoadScopeAsync(
            "OU=DoesNotExist,OU=AGDLP-Lab,DC=agdlp,DC=lab");

        Assert.Empty(snapshot.Objects);
    }

    [AdFact]
    public async Task LoadScope_GarbageBase_ReturnsEmptySnapshot_DoesNotThrow()
    {
        // "not a dn" is server-rejected with ERROR_DS_INVALID_DN_SYNTAX — a name
        // that can never denote an object, i.e. "absent", not "unavailable".
        var snapshot = await _fixture.Provider.LoadScopeAsync("not a dn");

        Assert.Empty(snapshot.Objects);
    }

    // --- T10: root candidates ------------------------------------------------------

    [AdFact]
    public async Task GetRootCandidates_Returns46_WholeDomainSyntheticPlusOusAndGroups()
    {
        var candidates = await _fixture.Provider.GetRootCandidatesAsync();

        // ADR-031 D3 (deliberate update of the pre-ADR-031 "Returns45" pin): the count is now
        // 46, not 45 — GetRootCandidatesAsync PREPENDS one synthesized whole-domain candidate
        // (an OrganizationalUnit-kind container at the effective base DN) ahead of the queried
        // OUs + groups. Justification: the production change is a candidate-LIST addition only
        // (LoadScopeAsync already accepts any baseDn), so the directory query result is
        // unchanged — 5 OUs (4 + the issue-#16 slash-OU fixture) + 40 groups = the same 45 the
        // old pin counted; the +1 is exactly the synthetic, asserted on its own below.
        Assert.Equal(46, candidates.Count);
        // 6 OUs now: the 5 real ones + the synthetic whole-domain container (OU-kind so it reads
        // as a scope and passes the picker's OU/group filter).
        Assert.Equal(6, candidates.Count(c => c.Kind == AdObjectKind.OrganizationalUnit));
        Assert.Equal(40, candidates.Count(c => IsGroup(c.Kind))); // 6 + 40 = 46: no other kinds
    }

    [AdFact]
    public async Task GetRootCandidates_PrependsWholeDomainCandidate_AtEffectiveBaseDn()
    {
        var candidates = await _fixture.Provider.GetRootCandidatesAsync();

        // ADR-031 D3: the synthesized whole-domain entry is FIRST (prepended), built from the
        // effective base DN (here the lab-pinned base, since this provider is new(localhost,
        // LabDn)). Modeled as an OrganizationalUnit so it reads as a scope, not a group.
        var first = candidates[0];
        Assert.True(
            Dn.Comparer.Equals(LabDn, first.Dn),
            $"whole-domain candidate Dn should be the effective base DN '{LabDn}', was '{first.Dn}'");
        Assert.Equal(AdObjectKind.OrganizationalUnit, first.Kind);
        Assert.Equal($"Whole domain ({LabDn})", first.Name);
    }

    // --- T11: GetObjectAsync ---------------------------------------------------------

    [AdFact]
    public async Task GetObject_KnownGroupDn_ReturnsCorrectKindAndName()
    {
        var obj = await _fixture.Provider.GetObjectAsync(GgSalesStaffDn);

        Assert.NotNull(obj);
        Assert.Equal(AdObjectKind.GlobalGroup, obj.Kind);
        Assert.Equal("GG_Sales_Staff", obj.Name);
        Assert.Equal("GG_Sales_Staff", obj.SamAccountName);
    }

    [AdFact]
    public async Task GetObject_SlashRdnOu_Resolves_DnRoundTripsAsGiven()
    {
        // Issue #16 regression proof: AD returns the '/' in this DN unescaped, and
        // pre-fix the raw ADsPath interpolation made the object come back null.
        var obj = await _fixture.Provider.GetObjectAsync(SlashOuDn);

        Assert.NotNull(obj);
        Assert.Equal(AdObjectKind.OrganizationalUnit, obj.Kind);
        Assert.Equal("Research/Development", obj.Name);

        // DNs are stored and passed as-given, never canonicalized (data-model
        // rule) — the ADsPath escaping must not leak into the returned DN.
        Assert.True(Dn.Comparer.Equals(SlashOuDn, obj.Dn), $"DN did not round-trip: '{obj.Dn}'");
    }

    [AdFact]
    public async Task GetObject_NonexistentDn_ReturnsNull()
    {
        var obj = await _fixture.Provider.GetObjectAsync("CN=DoesNotExist,OU=AGDLP-Lab,DC=agdlp,DC=lab");

        Assert.Null(obj);
    }

    [AdFact]
    public async Task GetObject_MalformedPathname_ReturnsNull()
    {
        // "CN=" is locally rejected by ADSI path parsing (E_ADS_BAD_PATHNAME,
        // 0x80005000) — classified NotFound, so it must come back as a value.
        var obj = await _fixture.Provider.GetObjectAsync("CN=");

        Assert.Null(obj);
    }

    [AdFact]
    public async Task GetObject_GarbageString_ReturnsNull_DoesNotThrow()
    {
        // Contract (IDirectoryProvider + data-model rules): an unresolvable DN is a
        // value, never an exception. "not a dn" reaches the server and is rejected
        // with ERROR_DS_INVALID_DN_SYNTAX (0x80072032) — a name that can never
        // denote an object, i.e. "absent", not "directory unavailable".
        var obj = await _fixture.Provider.GetObjectAsync("not a dn");

        Assert.Null(obj);
    }

    // --- T12: GetMembersAsync -----------------------------------------------------------

    [AdFact]
    public async Task GetMembers_DlFsSalesRw_ContainsNestedGgAndDirectUser_CorrectlyKinded()
    {
        var members = await _fixture.Provider.GetMembersAsync(DlFsSalesRwDn);

        Assert.Equal(2, members.Count);
        var gg = Assert.Single(members, m => Dn.Comparer.Equals(m.Dn, GgSalesStaffDn));
        Assert.Equal(AdObjectKind.GlobalGroup, gg.Kind);

        // u001 directly inside a DL is the seeded AGDLP violation — it must
        // surface as a real, correctly kinded user.
        var user = Assert.Single(members, m => Dn.Comparer.Equals(m.Dn, User001Dn));
        Assert.Equal(AdObjectKind.User, user.Kind);
    }

    [AdFact]
    public async Task GetMembers_VanishedParent_ReturnsEmptyList()
    {
        var members = await _fixture.Provider.GetMembersAsync("CN=Vanished,OU=AGDLP-Lab,DC=agdlp,DC=lab");

        Assert.Empty(members);
    }

    // --- T13: FSP member resolution (cross-forest AGDLP shape) -------------------------

    [AdFact]
    public async Task GetMembers_DlAppErpRw_ResolvesFspAsExternal_AndUgManagersAsUniversal()
    {
        // Seed script: DL_App-ERP_RW = UG_Managers (Universal) + the dangling
        // foreign-domain FSP added via Ensure-ForeignSidMember (commit 264ce7d).
        var members = await _fixture.Provider.GetMembersAsync(DlAppErpRwDn);

        Assert.Equal(2, members.Count);

        var ug = Assert.Single(members, m => Dn.Comparer.Equals(m.Dn, UgManagersDn));
        Assert.Equal(AdObjectKind.UniversalGroup, ug.Kind);

        // The FSP object exists (the DC system-created it outside the lab OU), so
        // GetMembersAsync resolves it LIVE: objectClass foreignSecurityPrincipal
        // maps to External via AdObjectKindMapper. Name = the SID (its `name`
        // attribute) proves the live-resolution path was taken — the unresolvable
        // fallback (MakeExternal) would have set Name to the full DN instead.
        var fsp = Assert.Single(members, m => Dn.Comparer.Equals(m.Dn, ForeignFspDn));
        Assert.Equal(AdObjectKind.External, fsp.Kind);
        Assert.Equal(ForeignSid, fsp.Name);
    }

    // --- T14: FSP in the full-scope snapshot (out-of-scope edge target) ----------------

    [AdFact]
    public void LoadScope_Lab_FspEdgePresent_TargetResolvesExternal()
    {
        var snapshot = _fixture.FullSnapshot;

        // The FSP lives in CN=ForeignSecurityPrincipals — outside OU=AGDLP-Lab — so
        // it is never an object of the scoped snapshot (195 stays 195) and GetKind
        // falls back to External; the membership edge pointing at it must survive.
        Assert.False(snapshot.TryGetObject(ForeignFspDn, out _));
        Assert.Equal(AdObjectKind.External, snapshot.GetKind(ForeignFspDn));
        Assert.Contains(new MembershipEdge(DlAppErpRwDn, ForeignFspDn), snapshot.Edges);
    }

    // --- Cancellation smoke ----------------------------------------------------------------

    [AdFact]
    public async Task LoadScope_PreCancelledToken_ThrowsOperationCanceled()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => _fixture.Provider.LoadScopeAsync(LabDn, cts.Token));
    }

    // --- Constructor guard (no directory needed, but lab-only by class trait) ---------------

    [AdFact]
    public void Constructor_PageSizeOutOfRange_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new LdapProvider(pageSize: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new LdapProvider(pageSize: 1001));
    }
}
