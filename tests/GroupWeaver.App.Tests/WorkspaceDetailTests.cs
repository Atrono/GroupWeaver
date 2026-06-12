using GroupWeaver.App.Graph;
using GroupWeaver.App.Tests.Fakes;
using GroupWeaver.App.ViewModels;
using GroupWeaver.Core.Model;
using GroupWeaver.Core.Providers;
using GroupWeaver.Providers;

using Xunit;

namespace GroupWeaver.App.Tests;

/// <summary>
/// Pins the AP 2.5 S2 detail-panel projection of <see cref="WorkspaceViewModel"/>
/// (ADR-007): <c>DetailPanelModel.Build(snapshot, dn)</c> is the SINGLE choke point
/// producing an immutable projection — header (the four typed members Dn/Kind/Name/
/// SamAccountName) plus rows that mirror <see cref="AdObject.Attributes"/> VERBATIM
/// (the UI never re-filters; a provider whitelist bug must become visible, not
/// masked — ADR-007 D2). Selection reads ONLY the snapshot: clicks never call the
/// provider and are never busy-gated (D1); the projection is recomputed at exactly
/// three points — SelectedDn change, load-pipeline finally, expand-pipeline finally
/// — so a refreshed/expanded object updates the open panel automatically and a stale
/// panel is impossible by construction. Load-state honesty from snapshot state alone
/// (D3): DN absent or External ∧ ¬IsLoaded → <c>NotLoaded</c>; External ∧ IsLoaded →
/// <c>Unresolvable</c> (FSP — attributes genuinely unavailable); everything else →
/// <c>Loaded</c> (a group whose MEMBERS are unloaded still has loaded attributes).
/// Row order (D4): known keys in <see cref="AttributeWhitelist.FetchProperties"/>
/// declaration order, unknown keys appended alphabetically. DNs flow verbatim
/// (data-model rule — never canonicalized), escaped characters included.
/// VM-only behavior — plain facts, no visual tree.
/// </summary>
public sealed class WorkspaceDetailTests
{
    private const string RootDn = "OU=Lab,DC=stub,DC=lab";
    private const string AdaDn = "CN=Ada Lovelace,OU=Lab,DC=stub,DC=lab";

    // ADSI-escaped forward slash on purpose: DN strings are stored as-given, never
    // canonicalized — the backslash escape must survive into the header untouched.
    private const string SlashDn = "CN=R&D\\/Ops Liaison,OU=Lab,DC=stub,DC=lab";

    private const string SalesDn = "CN=GG_Sales,OU=Lab,DC=stub,DC=lab";
    private const string EdgeGroupDn = "CN=GG_Edge,OU=Lab,DC=stub,DC=lab";
    private const string RowenaDn = "CN=Rowena Order,OU=Lab,DC=stub,DC=lab";

    /// <summary>A member-edge endpoint that is NOT in Snapshot.Objects (frontier).</summary>
    private const string FrontierDn = "CN=Frontier,DC=elsewhere,DC=lab";

    /// <summary>External kind, IN Objects, NOT members-loaded (resolved FSP pre-expand).</summary>
    private const string ExtStubDn = "CN=ExtStub,DC=elsewhere,DC=lab";

    /// <summary>External kind, IN Objects AND members-loaded — the expanded-FSP shape.</summary>
    private const string FspDn =
        "CN=S-1-5-21-1111111111-2222222222-3333333333-1106,CN=ForeignSecurityPrincipals,DC=stub,DC=lab";

    // --- (a) click on an in-snapshot user: header exact, rows mirror Attributes --------

    [Fact]
    public async Task NodeClick_OnAnInSnapshotUser_ProjectsTheHeaderExactly_AndRowsMirrorAttributes()
    {
        var snapshot = DetailScope();
        var provider = Provider(snapshot);
        var fake = new FakeGraphRenderer();
        var vm = Workspace(provider, () => fake);
        await vm.Initialization;
        Assert.Null(vm.DetailPanel); // nothing selected yet

        var changed = new List<string?>();
        vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        fake.RaiseNodeClicked(AdaDn, "User");

        // The projection is observable for the XAML binding (ObservableProperty).
        Assert.Contains(nameof(WorkspaceViewModel.DetailPanel), changed);

        var model = vm.DetailPanel;
        Assert.NotNull(model);
        Assert.Equal(DetailPanelState.Loaded, model.State);

        // Header equals EXACTLY the four typed members of the snapshot object —
        // Dn verbatim (ordinal, not just Dn.Comparer-equal: never canonicalized).
        Assert.Equal(AdaDn, model.Dn);
        Assert.Equal(AdObjectKind.User, model.Kind);
        Assert.Equal("Ada Lovelace", model.Name);
        Assert.Equal("ada.lovelace", model.SamAccountName);

        // Rows mirror Attributes EXACTLY — same count, same pairs (ADR-007 D2: the
        // UI never re-filters; the whitelist mirror is the provider's contract).
        Assert.True(snapshot.TryGetObject(AdaDn, out var ada));
        AssertRowsMirror(ada!.Attributes, model.Rows);

        // (h) selection is snapshot-only: ZERO provider traffic on click (ADR-007 D1).
        AssertZeroProviderTraffic(provider);
        Assert.False(vm.IsLoading, "a click must never engage the busy gate");
    }

    [Fact]
    public async Task NodeClick_OnAnEscapedSlashDn_CarriesTheDnVerbatimIntoTheHeader()
    {
        var snapshot = DetailScope();
        var provider = Provider(snapshot);
        var fake = new FakeGraphRenderer();
        var vm = Workspace(provider, () => fake);
        await vm.Initialization;

        fake.RaiseNodeClicked(SlashDn, "User");

        var model = vm.DetailPanel;
        Assert.NotNull(model);
        Assert.Equal(DetailPanelState.Loaded, model.State);
        Assert.Equal(SlashDn, model.Dn); // ordinal — the \/ escape survives untouched
        Assert.Equal(AdObjectKind.User, model.Kind);
        Assert.Equal("R&D/Ops Liaison", model.Name);
        Assert.True(snapshot.TryGetObject(SlashDn, out var liaison));
        AssertRowsMirror(liaison!.Attributes, model.Rows);
        AssertZeroProviderTraffic(provider);
    }

    // --- (b) row order: FetchProperties declaration order, unknown keys alphabetical ---

    [Fact]
    public void Build_OrdersKnownRowsByFetchPropertiesDeclaration_AndAppendsUnknownKeysAlphabetically()
    {
        // Two NON-whitelisted keys on purpose (ADR-007 D2): a provider whitelist bug
        // must become VISIBLE in the panel, never be masked by UI-side re-filtering.
        var rowena = new AdObject
        {
            Dn = RowenaDn,
            Kind = AdObjectKind.User,
            Name = "Rowena Order",
            SamAccountName = "rowena.order",
            Attributes = new Dictionary<string, string>
            {
                // Scrambled insertion order on purpose: row order must come from the
                // whitelist declaration, never from dictionary enumeration order.
                ["title"] = "Archivist",
                ["extensionAttribute7"] = "leaked-by-a-buggy-provider",
                ["department"] = "Records",
                ["adminCount"] = "1",
                ["description"] = "row order probe",
                ["whenCreated"] = "2023-05-05T12:00:00Z",
                ["primaryGroupID"] = "513",
            },
        };
        var snapshot = new DirectorySnapshot();
        snapshot.AddObject(rowena);

        // Guard: the expected known-key order IS the FetchProperties declaration order
        // restricted to this object — if the whitelist evolves, this line explains why.
        Assert.Equal(
            new[] { "description", "whenCreated", "department", "title", "primaryGroupID" },
            AttributeWhitelist.FetchProperties.Where(p => rowena.Attributes.ContainsKey(p)));

        var model = DetailPanelModel.Build(snapshot, RowenaDn);

        Assert.NotNull(model);
        Assert.Equal(DetailPanelState.Loaded, model.State);
        Assert.Equal(
            new[]
            {
                "description", "whenCreated", "department", "title", "primaryGroupID",
                "adminCount", "extensionAttribute7", // unknown keys: appended, alphabetical
            },
            model.Rows.Select(r => r.Label));

        // One full-row pin including the positional record shape and value equality.
        Assert.Equal(new DetailRow("description", "row order probe"), model.Rows[0]);
        AssertRowsMirror(rowena.Attributes, model.Rows);
    }

    // --- (c) null selection: null projection --------------------------------------------

    [Fact]
    public async Task NullSelectedDn_ProjectsNull_AndBuildWithNullDnReturnsNull()
    {
        var snapshot = DetailScope();
        var provider = Provider(snapshot);
        var fake = new FakeGraphRenderer();
        var vm = Workspace(provider, () => fake);
        await vm.Initialization;

        Assert.Null(vm.SelectedDn);
        Assert.Null(vm.DetailPanel);

        fake.RaiseNodeClicked(AdaDn, "User");
        Assert.NotNull(vm.DetailPanel);

        // Deselection projects null again — the panel empties, it never goes stale.
        vm.SelectedDn = null;
        Assert.Null(vm.DetailPanel);

        // The static choke point agrees: no DN, no projection.
        Assert.Null(DetailPanelModel.Build(snapshot, null));
        AssertZeroProviderTraffic(provider);
    }

    // --- (d)/(e) load-state honesty from snapshot state ALONE (ADR-007 D3) -------------

    [Theory]
    [InlineData(FrontierDn, DetailPanelState.NotLoaded)] // DN absent from Objects
    [InlineData(ExtStubDn, DetailPanelState.NotLoaded)] // External ∧ ¬IsLoaded
    [InlineData(FspDn, DetailPanelState.Unresolvable)] // External ∧ IsLoaded (FSP)
    [InlineData(AdaDn, DetailPanelState.Loaded)] // plain in-snapshot user
    [InlineData(SalesDn, DetailPanelState.Loaded)] // group with UNLOADED members: attributes ARE loaded
    public async Task Selection_ProjectsTheLoadState_FromSnapshotStateAlone(
        string dn, DetailPanelState expected)
    {
        var snapshot = DetailScope();
        var provider = Provider(snapshot);
        var vm = RendererlessWorkspace(provider); // selection exists without a renderer (the seam)
        await vm.Initialization;

        vm.SelectedDn = dn;

        var model = vm.DetailPanel;
        Assert.NotNull(model);
        Assert.Equal(expected, model.State);
        Assert.Equal(dn, model.Dn); // header shows the DN verbatim in EVERY state
        AssertZeroProviderTraffic(provider);
    }

    [Fact]
    public async Task FrontierSelection_ProjectsNotLoaded_DnVerbatim_ExternalKind_NoRows()
    {
        var snapshot = DetailScope();
        var provider = Provider(snapshot);
        var fake = new FakeGraphRenderer();
        var vm = Workspace(provider, () => fake);
        await vm.Initialization;

        fake.RaiseNodeClicked(FrontierDn, "External");

        var model = vm.DetailPanel;
        Assert.NotNull(model);
        Assert.Equal(DetailPanelState.NotLoaded, model.State);
        Assert.Equal(FrontierDn, model.Dn); // verbatim — never canonicalized
        Assert.Equal(AdObjectKind.External, model.Kind); // GetKind contract: absent → External
        Assert.Empty(model.Rows); // nothing fetched, nothing to show — honesty, no fabrication
        AssertZeroProviderTraffic(provider);
    }

    [Fact]
    public async Task ExpandedFspSelection_ExternalAndLoaded_ProjectsUnresolvable()
    {
        var snapshot = DetailScope();
        var provider = Provider(snapshot);
        var fake = new FakeGraphRenderer();
        var vm = Workspace(provider, () => fake);
        await vm.Initialization;

        fake.RaiseNodeClicked(FspDn, "External");

        var model = vm.DetailPanel;
        Assert.NotNull(model);
        // External ∧ IsLoaded → Unresolvable (ADR-007 D3): the FSP was fetched and
        // genuinely has no resolvable attributes — NOT the "expand to resolve" hint.
        Assert.Equal(DetailPanelState.Unresolvable, model.State);
        Assert.Equal(FspDn, model.Dn);
        Assert.Equal(AdObjectKind.External, model.Kind);
        Assert.Equal("S-1-5-21-1111111111-2222222222-3333333333-1106", model.Name);
        Assert.Null(model.SamAccountName);
        Assert.Empty(model.Rows);
        AssertZeroProviderTraffic(provider);
    }

    // --- (f) pipeline completion recomputes under an UNCHANGED SelectedDn --------------

    [Fact]
    public async Task ExpandUpsertingTheSelectedObject_RecomputesTheProjection_UnderUnchangedSelectedDn()
    {
        var snapshot = DetailScope();
        var provider = Provider(snapshot);
        var fake = new FakeGraphRenderer();
        var vm = Workspace(provider, () => fake);
        await vm.Initialization;

        fake.RaiseNodeClicked(AdaDn, "User");
        var before = vm.DetailPanel;
        Assert.NotNull(before);
        Assert.Equal("pre-expand description", Assert.Single(
            before.Rows, r => r.Label == "description").Value);

        // Expanding GG_Sales upserts Ada (AddObject: latest wins) with CHANGED attributes.
        provider.GetMembersHandler = (_, _) => Task.FromResult<IReadOnlyList<AdObject>>(
        [
            new AdObject
            {
                Dn = AdaDn,
                Kind = AdObjectKind.User,
                Name = "Ada Lovelace",
                SamAccountName = "ada.lovelace",
                Attributes = new Dictionary<string, string>
                {
                    ["description"] = "post-expand description",
                    ["title"] = "Principal Engineer",
                },
            },
        ]);

        fake.RaiseNodeExpandRequested(SalesDn, "GlobalGroup");
        await vm.Expansion;

        // SelectedDn never moved — yet the panel reflects the UPSERTED object: the
        // expand-pipeline finally re-projects (ADR-007 D1; a stale panel is impossible).
        Assert.Equal(AdaDn, vm.SelectedDn);
        var after = vm.DetailPanel;
        Assert.NotNull(after);
        Assert.NotSame(before, after); // immutable projection: recompute = new instance
        Assert.Equal(DetailPanelState.Loaded, after.State);
        Assert.Equal(AdaDn, after.Dn);

        Assert.True(snapshot.TryGetObject(AdaDn, out var upserted));
        AssertRowsMirror(upserted!.Attributes, after.Rows);
        Assert.Equal("post-expand description", Assert.Single(
            after.Rows, r => r.Label == "description").Value);
        Assert.DoesNotContain(after.Rows, r => r.Label == "department"); // replaced, never merged

        Assert.Equal(1, provider.GetMembersCalls);
        Assert.Equal(0, provider.GetObjectCalls);
    }

    [Fact]
    public async Task RefreshResolvingTheSelectedFrontier_RecomputesFromNotLoadedToLoaded()
    {
        var snapshot = DetailScope();
        var provider = Provider(snapshot);
        var fake = new FakeGraphRenderer();
        var vm = Workspace(provider, () => fake);
        await vm.Initialization;

        fake.RaiseNodeClicked(FrontierDn, "External");
        Assert.Equal(DetailPanelState.NotLoaded, vm.DetailPanel!.State);

        provider.GetObjectHandler = (_, _) => Task.FromResult<AdObject?>(new AdObject
        {
            Dn = FrontierDn,
            Kind = AdObjectKind.User,
            Name = "Frontier",
            SamAccountName = "frontier",
            Attributes = new Dictionary<string, string> { ["description"] = "resolved at last" },
        });
        provider.GetMembersHandler = (_, _) => Task.FromResult<IReadOnlyList<AdObject>>([]);

        Assert.True(vm.RefreshCommand.CanExecute(null)); // External IS fetchable (ADR-005 D3)
        vm.RefreshCommand.Execute(null);
        await vm.Expansion;

        // Same recompute point, Refresh flavor (ADR-005 D4 = the expand pipeline): the
        // frontier resolved to its true kind and the panel followed — full header swap.
        Assert.Equal(FrontierDn, vm.SelectedDn);
        var model = vm.DetailPanel;
        Assert.NotNull(model);
        Assert.Equal(DetailPanelState.Loaded, model.State);
        Assert.Equal(FrontierDn, model.Dn);
        Assert.Equal(AdObjectKind.User, model.Kind);
        Assert.Equal("Frontier", model.Name);
        Assert.Equal("frontier", model.SamAccountName);
        var row = Assert.Single(model.Rows);
        Assert.Equal(new DetailRow("description", "resolved at last"), row);

        Assert.Equal(1, provider.GetObjectCalls);
        Assert.Equal(1, provider.GetMembersCalls);
    }

    [Fact]
    public async Task SelectionMadeDuringTheInitialScopeLoad_ProjectsOnceTheLoadCompletes()
    {
        var loadGate = new TaskCompletionSource<DirectorySnapshot>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var provider = Provider(new DirectorySnapshot());
        provider.LoadScopeResult = loadGate.Task;
        var fake = new FakeGraphRenderer();
        var vm = Workspace(provider, () => fake);
        Assert.True(vm.IsLoading);

        // The public-setter seam: a selection can exist before any snapshot does —
        // nothing to read yet, so nothing is projected (snapshot-only reads, D1) …
        vm.SelectedDn = AdaDn;
        Assert.Null(vm.DetailPanel);

        loadGate.SetResult(DetailScope());
        await vm.Initialization;

        // … and the LOAD-pipeline finally is the third recompute point (ADR-007 D1):
        // the held selection projects without any further gesture.
        var model = vm.DetailPanel;
        Assert.NotNull(model);
        Assert.Equal(DetailPanelState.Loaded, model.State);
        Assert.Equal(AdaDn, model.Dn);
        Assert.Equal(AdObjectKind.User, model.Kind);
        AssertZeroProviderTraffic(provider);
    }

    // --- (g) clicks are NEVER busy-gated: re-selection mid-expand still projects -------

    [Fact]
    public async Task ReSelection_DuringAnInFlightGatedExpand_StillProjects_ClicksAreNeverBusyGated()
    {
        var snapshot = DetailScope();
        var provider = Provider(snapshot);
        var fake = new FakeGraphRenderer();
        var vm = Workspace(provider, () => fake);
        await vm.Initialization;

        fake.RaiseNodeClicked(AdaDn, "User");
        var before = vm.DetailPanel;
        Assert.NotNull(before);

        // Hold the busy gate open on the focus-only expand path (GG_Edge is loaded).
        var focusGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fake.FocusResult = focusGate.Task;
        fake.RaiseNodeExpandRequested(EdgeGroupDn, "GlobalGroup");
        Assert.False(vm.Expansion.IsCompleted);
        Assert.True(vm.IsLoading, "the focus-only branch holds the ONE busy gate (ADR-005 D3)");

        // Clicks are never busy-gated (ADR-007 D1): the re-selection projects NOW,
        // while the gate is held — selection stays responsive during any pipeline.
        fake.RaiseNodeClicked(FspDn, "External");

        Assert.Equal(FspDn, vm.SelectedDn);
        var midFlight = vm.DetailPanel;
        Assert.NotNull(midFlight);
        Assert.NotSame(before, midFlight);
        Assert.Equal(FspDn, midFlight.Dn);
        Assert.Equal(DetailPanelState.Unresolvable, midFlight.State);
        AssertZeroProviderTraffic(provider); // (h) the focus path AND the clicks stay offline

        focusGate.SetResult();
        await vm.Expansion;

        // The pipeline-finally recompute re-projects the CURRENT selection — it must
        // never clobber the mid-flight click back to the pre-expand selection.
        Assert.Equal(FspDn, vm.SelectedDn);
        Assert.NotNull(vm.DetailPanel);
        Assert.Equal(FspDn, vm.DetailPanel.Dn);
        Assert.Equal(DetailPanelState.Unresolvable, vm.DetailPanel.State);
        Assert.False(vm.IsLoading);
        AssertZeroProviderTraffic(provider);
    }

    // --- helpers ------------------------------------------------------------------------

    private static AdObject Obj(
        string name, string dn, AdObjectKind kind = AdObjectKind.GlobalGroup) =>
        new() { Dn = dn, Kind = kind, Name = name };

    /// <summary>
    /// The AP 2.5 fixture: root OU; Ada (full user display set, scrambled insertion
    /// order); an escaped-slash-DN user; GG_Sales IN Objects but NOT members-loaded;
    /// GG_Edge LOADED with [Ada, FrontierDn] (makes the frontier a real member-edge
    /// endpoint); ExtStub (External, in Objects, NOT loaded) and an FSP (External,
    /// in Objects, LOADED-and-empty — the post-expand FSP shape).
    /// </summary>
    private static DirectorySnapshot DetailScope()
    {
        var snapshot = new DirectorySnapshot();
        snapshot.AddObject(Obj("Lab", RootDn, AdObjectKind.OrganizationalUnit));
        snapshot.AddObject(new AdObject
        {
            Dn = AdaDn,
            Kind = AdObjectKind.User,
            Name = "Ada Lovelace",
            SamAccountName = "ada.lovelace",
            Attributes = new Dictionary<string, string>
            {
                // Scrambled on purpose — display order is the whitelist's, not the dict's.
                ["title"] = "Engineer",
                ["department"] = "R&D",
                ["description"] = "pre-expand description",
                ["primaryGroupID"] = "513",
                ["whenCreated"] = "2024-01-01T00:00:00Z",
            },
        });
        snapshot.AddObject(new AdObject
        {
            Dn = SlashDn,
            Kind = AdObjectKind.User,
            Name = "R&D/Ops Liaison",
            SamAccountName = "rd.ops.liaison",
            Attributes = new Dictionary<string, string>
            {
                ["description"] = "escaped-slash DN fixture",
            },
        });
        snapshot.AddObject(new AdObject
        {
            Dn = SalesDn,
            Kind = AdObjectKind.GlobalGroup,
            Name = "GG_Sales",
            SamAccountName = "GG_Sales",
            Attributes = new Dictionary<string, string>
            {
                ["description"] = "sales group",
                ["groupType"] = "-2147483646",
            },
        });
        snapshot.AddObject(Obj("GG_Edge", EdgeGroupDn));
        snapshot.SetMembers(EdgeGroupDn, [AdaDn, FrontierDn]);
        snapshot.AddObject(Obj("ExtStub", ExtStubDn, AdObjectKind.External));
        snapshot.AddObject(new AdObject
        {
            Dn = FspDn,
            Kind = AdObjectKind.External,
            Name = "S-1-5-21-1111111111-2222222222-3333333333-1106",
        });
        snapshot.SetMembers(FspDn, []); // expanded FSP: External ∧ IsLoaded (empty, never null)
        return snapshot;
    }

    /// <summary>Stub whose scope load yields <paramref name="snapshot"/>.</summary>
    private static StubDirectoryProvider Provider(DirectorySnapshot snapshot) =>
        new(Task.FromResult(new DirectoryConnection("stub directory", 5)))
        {
            LoadScopeResult = Task.FromResult(snapshot),
        };

    /// <summary>Workspace VM rooted at <see cref="RootDn"/> (AP 2.2 S6 ctor shape).</summary>
    private static WorkspaceViewModel Workspace(
        StubDirectoryProvider provider, Func<IGraphRenderer> rendererFactory) =>
        new(
            provider,
            Obj("Lab", RootDn, AdObjectKind.OrganizationalUnit),
            new DirectoryConnection("stub directory", 5),
            webView2Missing: false,
            rendererFactory);

    /// <summary>Workspace VM constructed HEADLESS — null renderer factory: selection
    /// arrives through the public SelectedDn setter (the declared AP 2.5 seam).</summary>
    private static WorkspaceViewModel RendererlessWorkspace(StubDirectoryProvider provider) =>
        new(
            provider,
            Obj("Lab", RootDn, AdObjectKind.OrganizationalUnit),
            new DirectoryConnection("stub directory", 5),
            webView2Missing: false,
            graphRendererFactory: null);

    /// <summary>The D2 mirror pin: rows = the Attributes dictionary VERBATIM — same
    /// count, distinct labels, every (Label, Value) pair sourced from Attributes.</summary>
    private static void AssertRowsMirror(
        IReadOnlyDictionary<string, string> attributes, IReadOnlyList<DetailRow> rows)
    {
        Assert.Equal(attributes.Count, rows.Count);
        Assert.Equal(
            rows.Count,
            rows.Select(r => r.Label).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.All(rows, row =>
        {
            Assert.True(
                attributes.TryGetValue(row.Label, out var value),
                $"row '{row.Label}' has no source in Attributes — the mirror must be exact");
            Assert.Equal(value, row.Value);
        });
    }

    /// <summary>The (h) pin: selection-only flows never touch the provider (ADR-007 D1).</summary>
    private static void AssertZeroProviderTraffic(StubDirectoryProvider provider)
    {
        Assert.Equal(0, provider.GetObjectCalls);
        Assert.Equal(0, provider.GetMembersCalls);
    }
}
