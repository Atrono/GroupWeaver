using GroupWeaver.App.Tests.Fakes;
using GroupWeaver.App.ViewModels;
using GroupWeaver.Core.Model;
using GroupWeaver.Core.Providers;
using GroupWeaver.Providers;
using GroupWeaver.Tests;
using GroupWeaver.Tests.Providers.Ldap;

using Xunit;

namespace GroupWeaver.App.Tests;

/// <summary>
/// Builds ONE <see cref="WorkspaceViewModel"/> over the REAL <see cref="LdapProvider"/>,
/// rooted at the live lab OU, shared by the selection-only detail-panel tests (selection
/// reads ONLY the snapshot — ADR-007 D1 — so sharing is safe). Guarded by
/// <see cref="AdFactAttribute.IsLabReachable"/>: off the lab DC nothing is constructed —
/// every test using this fixture is skipped by <see cref="AdFactAttribute"/> anyway.
/// </summary>
public sealed class LiveLabWorkspaceFixture : IAsyncLifetime
{
    /// <summary>The lab DC; this box (CLAUDE.md environment).</summary>
    public const string Server = "localhost";

    /// <summary>Root of the seeded fixture tree (tools/seed-testad.ps1).</summary>
    public const string LabDn = "OU=AGDLP-Lab,DC=agdlp,DC=lab";

    /// <summary>The real provider; also reused by the DL-rooted FSP choreography test.</summary>
    public LdapProvider Provider { get; } = new(Server, LabDn);

    /// <summary>The live connection summary handed to every workspace VM.</summary>
    public DirectoryConnection Connection { get; private set; } = null!;

    /// <summary>The lab-rooted workspace, fully loaded (Initialization awaited).</summary>
    public WorkspaceViewModel Workspace { get; private set; } = null!;

    /// <inheritdoc />
    public async Task InitializeAsync()
    {
        if (!AdFactAttribute.IsLabReachable)
        {
            return;
        }

        Connection = await Provider.ConnectAsync();
        var root = await Provider.GetObjectAsync(LabDn)
            ?? throw new InvalidOperationException($"lab root unresolvable: {LabDn}");
        Workspace = new WorkspaceViewModel(
            Provider, root, Connection, webView2Missing: false, () => new FakeGraphRenderer());
        await Workspace.Initialization;
    }

    /// <inheritdoc />
    public Task DisposeAsync()
    {
        Workspace?.Dispose();
        return Task.CompletedTask;
    }
}

/// <summary>
/// AP 2.5 S5: live-AD whitelist evidence for the detail panel —
/// <see cref="DetailPanelModel"/> projections of a <see cref="WorkspaceViewModel"/> over
/// the REAL <see cref="LdapProvider"/> against the live AGDLP-Lab fixtures. The center
/// pin: <see cref="DetailPanelModel.Rows"/> mirror <see cref="AdObject.Attributes"/>
/// VERBATIM (ADR-007 D2 — the UI never re-filters), so every row label must fall inside
/// the per-kind display set derived from the PUBLIC
/// <see cref="AttributeWhitelist.BuildAttributes"/> contract: if the provider ever leaks
/// an attribute past the whitelist, it becomes VISIBLE here, in the panel — the live
/// leak-detection pin. Fixture facts (the u001 header, the groupType raw value, the
/// DL_App-ERP_RW member list, the FSP DN and its whenCreated) were verified READ-ONLY
/// against the live DC (<c>Get-ADUser/Get-ADGroup/Get-ADObject -Server localhost</c>,
/// 2026-06-12) before pinning. NOTE: <c>seed-testad.ps1</c>'s <c>Ensure-User</c> sets NO
/// <c>department</c>/<c>title</c>/<c>description</c> (verified across all 140 lab users),
/// so those user rows cannot be pinned PRESENT until the fixture set is extended via the
/// <c>ad-fixture-admin</c> agent; the subset pin is fixture-independent and is the
/// enforceable live guarantee. Excluded in CI via the class-level
/// <c>Category=RequiresAd</c> trait; skipped with a loud warning off the lab DC via
/// <see cref="AdFactAttribute"/>.
/// </summary>
[Trait(TestCategories.Category, TestCategories.RequiresAd)]
public sealed class WorkspaceDetailLiveAdTests : IClassFixture<LiveLabWorkspaceFixture>
{
    private const string User001Dn = "CN=Anna Acker (u001),OU=Users,OU=AGDLP-Lab,DC=agdlp,DC=lab";
    private const string GgSalesStaffDn = "CN=GG_Sales_Staff,OU=Groups,OU=AGDLP-Lab,DC=agdlp,DC=lab";
    private const string DlAppErpRwDn = "CN=DL_App-ERP_RW,OU=Groups,OU=AGDLP-Lab,DC=agdlp,DC=lab";

    /// <summary>Fixed dangling cross-forest SID seeded by Ensure-ForeignSidMember; the DC
    /// system-created its FSP OUTSIDE the lab OU (see LdapProviderIntegrationTests T13/T14).</summary>
    private const string ForeignSid = "S-1-5-21-1100000001-2200000002-3300000003-1106";

    private const string ForeignFspDn =
        "CN=" + ForeignSid + ",CN=ForeignSecurityPrincipals,DC=agdlp,DC=lab";

    /// <summary>The invariant-UTC shape LdapEntry normalizes <c>whenCreated</c> to.</summary>
    private const string WhenCreatedUtcPattern = @"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}Z$";

    private readonly LiveLabWorkspaceFixture _fixture;

    public WorkspaceDetailLiveAdTests(LiveLabWorkspaceFixture fixture) => _fixture = fixture;

    // --- (a) seeded user: rows present AND every label inside the USER display set -------

    [AdFact]
    public void SelectSeededUser_WhenCreatedAndPrimaryGroupIdRowsPresent_EveryLabelWithinTheUserDisplaySet()
    {
        var vm = _fixture.Workspace;
        Assert.Null(vm.LoadError);

        vm.SelectedDn = User001Dn; // the declared AP 2.5 seam — a click sets exactly this

        var model = vm.DetailPanel;
        Assert.NotNull(model);
        Assert.Equal(DetailPanelState.Loaded, model.State);
        Assert.Equal(User001Dn, model.Dn);
        Assert.Equal(AdObjectKind.User, model.Kind);
        Assert.Equal("Anna Acker (u001)", model.Name);
        Assert.Equal("u001", model.SamAccountName);

        // The user attributes the fixture genuinely carries (verified live; Ensure-User
        // seeds no department/title/description — see the class doc for the gap report).
        var whenCreated = Assert.Single(model.Rows, r => r.Label == "whenCreated");
        Assert.Matches(WhenCreatedUtcPattern, whenCreated.Value);
        var primaryGroupId = Assert.Single(model.Rows, r => r.Label == "primaryGroupID");
        Assert.Equal("513", primaryGroupId.Value);

        // THE live leak-detection pin: rows mirror Attributes verbatim, so every label
        // must sit inside the USER display set read from AttributeWhitelist.
        AssertLabelsWithinDisplaySet(AdObjectKind.User, model.Rows);
        AssertRowsMirrorTheSnapshotObject(vm, User001Dn, model.Rows);
        Assert.False(vm.IsLoading, "a selection must never engage the busy gate (ADR-007 D1)");
    }

    // --- (b) seeded group: groupType present, labels subset of the GROUP display set -----

    [AdFact]
    public void SelectSeededGroup_GroupTypeRowPresent_EveryLabelWithinTheGroupDisplaySet()
    {
        var vm = _fixture.Workspace;
        Assert.Null(vm.LoadError);

        vm.SelectedDn = GgSalesStaffDn;

        var model = vm.DetailPanel;
        Assert.NotNull(model);
        Assert.Equal(DetailPanelState.Loaded, model.State);
        Assert.Equal(GgSalesStaffDn, model.Dn);
        Assert.Equal(AdObjectKind.GlobalGroup, model.Kind);
        Assert.Equal("GG_Sales_Staff", model.Name);

        // groupType reaches the panel as the raw invariant integer, as supplied
        // (verified live: global security group = 0x80000002).
        var groupType = Assert.Single(model.Rows, r => r.Label == "groupType");
        Assert.Equal("-2147483646", groupType.Value);
        Assert.Contains(model.Rows, r => r.Label == "whenCreated");

        AssertLabelsWithinDisplaySet(AdObjectKind.GlobalGroup, model.Rows);
        AssertRowsMirrorTheSnapshotObject(vm, GgSalesStaffDn, model.Rows);
    }

    // --- (c) FSP frontier, group-rooted at the holding DL: NotLoaded → Refresh → Unresolvable

    [AdFact]
    public async Task FspFrontier_GroupRootedAtTheHoldingDl_NotLoadedThenRefreshProjectsUnresolvable()
    {
        // Group-rooted scope at the DL holding the FSP: LoadScopeAsync on a group DN
        // yields exactly that group (no DN children), members [FSP, UG_Managers] —
        // verified live; the FSP is a frontier member-edge endpoint outside Objects.
        var root = await _fixture.Provider.GetObjectAsync(DlAppErpRwDn);
        Assert.NotNull(root);
        Assert.Equal(AdObjectKind.DomainLocalGroup, root.Kind);

        var renderer = new FakeGraphRenderer();
        using var vm = new WorkspaceViewModel(
            _fixture.Provider, root, _fixture.Connection, webView2Missing: false, () => renderer);
        await vm.Initialization;
        Assert.Null(vm.LoadError);

        var snapshot = vm.Snapshot;
        Assert.NotNull(snapshot);
        Assert.False(snapshot.TryGetObject(ForeignFspDn, out _));
        var memberDns = snapshot.GetMembers(DlAppErpRwDn);
        Assert.NotNull(memberDns);
        Assert.Contains(ForeignFspDn, memberDns, Dn.Comparer);

        // Selecting the frontier DN projects an honest NotLoaded header (ADR-007 D3):
        // External by the GetKind contract, nothing fetched, nothing fabricated.
        vm.SelectedDn = ForeignFspDn;
        var before = vm.DetailPanel;
        Assert.NotNull(before);
        Assert.Equal(DetailPanelState.NotLoaded, before.State);
        Assert.Equal(ForeignFspDn, before.Dn); // verbatim — never canonicalized
        Assert.Equal(AdObjectKind.External, before.Kind);
        Assert.Null(before.Name);
        Assert.Empty(before.Rows);

        // The refresh choreography (ADR-005 D4 = the expand pipeline, forced): resolves
        // the FSP live (objectClass foreignSecurityPrincipal → External stays its TRUE
        // kind) and marks it loaded-and-empty; the pipeline finally re-projects.
        Assert.True(vm.RefreshCommand.CanExecute(null), "External must be fetchable (ADR-005 D3)");
        vm.RefreshCommand.Execute(null);
        await vm.Expansion;
        Assert.Null(vm.LoadError);

        var after = vm.DetailPanel;
        Assert.NotNull(after);
        Assert.Equal(DetailPanelState.Unresolvable, after.State); // External ∧ IsLoaded (D3)
        Assert.Equal(ForeignFspDn, after.Dn);
        Assert.Equal(AdObjectKind.External, after.Kind);
        Assert.Equal(ForeignSid, after.Name); // live resolution proof, not the MakeExternal fallback
        Assert.Null(after.SamAccountName); // an FSP has none (verified live)

        // Even the unresolvable FSP surfaces ONLY common-display attributes: its real
        // whenCreated flows through, and no label escapes the External display set.
        var whenCreated = Assert.Single(after.Rows, r => r.Label == "whenCreated");
        Assert.Matches(WhenCreatedUtcPattern, whenCreated.Value);
        AssertLabelsWithinDisplaySet(AdObjectKind.External, after.Rows);
        AssertRowsMirrorTheSnapshotObject(vm, ForeignFspDn, after.Rows);

        Assert.True(snapshot.IsLoaded(ForeignFspDn)); // loaded-and-empty, never null
        Assert.False(vm.IsLoading);
    }

    // --- helpers --------------------------------------------------------------------------

    /// <summary>
    /// The authoritative per-kind display set, read from <see cref="AttributeWhitelist"/>
    /// through its PUBLIC contract: probe every fetchable attribute through
    /// <see cref="AttributeWhitelist.BuildAttributes"/> — the keys it lets through ARE
    /// the display set (no duplication of the private sets, no drift).
    /// </summary>
    private static HashSet<string> DisplaySetFor(AdObjectKind kind)
    {
        var probe = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in AttributeWhitelist.FetchProperties)
        {
            probe[property] = ["probe"];
        }

        return AttributeWhitelist.BuildAttributes(kind, probe).Keys
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>The leak-detection pin: every row label sits inside the display set for
    /// <paramref name="kind"/> — anything else is a provider whitelist leak made visible
    /// by the panel's verbatim mirror (ADR-007 D2).</summary>
    private static void AssertLabelsWithinDisplaySet(AdObjectKind kind, IReadOnlyList<DetailRow> rows)
    {
        var display = DisplaySetFor(kind);
        Assert.All(rows, row => Assert.True(
            display.Contains(row.Label),
            $"row '{row.Label}' is outside the {kind} display set — the provider leaked an attribute past the whitelist"));
    }

    /// <summary>The D2 mirror over LIVE data: rows = the snapshot object's Attributes
    /// verbatim — same count, every (Label, Value) pair sourced from Attributes.</summary>
    private static void AssertRowsMirrorTheSnapshotObject(
        WorkspaceViewModel vm, string dn, IReadOnlyList<DetailRow> rows)
    {
        var snapshot = vm.Snapshot;
        Assert.NotNull(snapshot);
        Assert.True(snapshot.TryGetObject(dn, out var obj));
        Assert.Equal(obj!.Attributes.Count, rows.Count);
        Assert.All(rows, row =>
        {
            Assert.True(
                obj.Attributes.TryGetValue(row.Label, out var value),
                $"row '{row.Label}' has no source in Attributes — the mirror must be exact");
            Assert.Equal(value, row.Value);
        });
    }
}
