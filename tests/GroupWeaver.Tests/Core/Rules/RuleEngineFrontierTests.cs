using GroupWeaver.Core.Model;
using GroupWeaver.Core.Rules;

using Xunit;

namespace GroupWeaver.Tests.Core.Rules;

/// <summary>
/// Pins the AP 3.2 S5 frontier semantics of <c>RuleEngine.Evaluate</c>
/// (ADR-009): <see cref="RuleReport.UncheckedDns"/> — "unexpanded areas are
/// unchecked" — over hand-built snapshots:
/// <list type="bullet">
/// <item>Source (a), walk frontiers: raw member DNs absent from
/// <c>Objects</c> (⇒ External ⇒ fetchable) and reachable in-snapshot
/// unloaded groups, exactly as <c>MembershipTraversal.Walk</c> reports them.</item>
/// <item>Source (b), the load-state scan: in-snapshot objects of fetchable
/// kind (GG/DL/UG/External) with <c>GetMembers == null</c> that NO walk
/// reaches (LdapProvider's vanished-group arm produces exactly these). A scan,
/// not a walk.</item>
/// <item>Never in it: loaded-and-empty parents (null ≠ empty — the tri-state),
/// and User/Computer/OU objects (leaves; not fetchable kinds) — neither as
/// walk-reached members nor as scan candidates.</item>
/// <item>Shape: deduped under <see cref="Dn.Comparer"/> (first-found spelling
/// wins, deterministic via sorted starts), sorted OrdinalIgnoreCase.</item>
/// <item>NEVER filtered by ignore/exceptions — load-state truth, not a
/// judgment — and ALWAYS computed, even with every rule disabled.</item>
/// <item>Insertion-order determinism: the same logical snapshot built in two
/// different AddObject/SetMembers call orders evaluates to projection-equal
/// reports (Violations projections + UncheckedDns) — never insertion or
/// dictionary order.</item>
/// </list>
/// Violations are compared via PROJECTIONS (RuleId, Severity, Dns sequence,
/// Message) — never via RuleViolation record equality, which is
/// reference-based over the <c>Dns</c> list property. Cyclic snapshots run
/// under Timeout guards plus <c>Task.Run</c> (termination is proven, never
/// trusted).
/// </summary>
public class RuleEngineFrontierTests
{
    private const string ParentDn = "CN=GG_Parent,OU=Frontier,DC=lab";

    // --- Source (a): walk frontiers — raw DNs and reachable unloaded fetchables -------

    [Fact]
    public void Evaluate_RawMemberDnsAndReachableUnloadedFetchables_ComposeUncheckedDns_Sorted()
    {
        const string ggUnloadedDn = "CN=GG_Unloaded,OU=Frontier,DC=lab";
        const string dlUnloadedDn = "CN=DL_Unloaded,OU=Frontier,DC=lab";
        const string ugUnloadedDn = "CN=UG_Unloaded,OU=Frontier,DC=lab";
        const string rawMemberDn = "CN=Domain Admins,CN=Users,DC=elsewhere"; // absent from Objects => External => fetchable
        var snapshot = Snapshot(
            Obj(ParentDn, AdObjectKind.GlobalGroup),
            Obj(ggUnloadedDn, AdObjectKind.GlobalGroup),
            Obj(dlUnloadedDn, AdObjectKind.DomainLocalGroup),
            Obj(ugUnloadedDn, AdObjectKind.UniversalGroup));
        snapshot.SetMembers(ParentDn, [ggUnloadedDn, dlUnloadedDn, ugUnloadedDn, rawMemberDn]);

        var report = RuleEngine.Evaluate(snapshot, DefaultsWithoutIgnore());

        // Exactly the four never-loaded fetchables, sorted OrdinalIgnoreCase
        // ('L' < 'o' puts DL_Unloaded before Domain Admins). The LOADED parent
        // is excluded, and the three in-snapshot groups — hit by walk frontier
        // AND load-state scan alike — appear exactly once each.
        Assert.Equal(
            new[] { dlUnloadedDn, rawMemberDn, ggUnloadedDn, ugUnloadedDn },
            report.UncheckedDns);
    }

    // --- Source (b): the load-state scan — unloaded fetchables no walk reaches ----------

    [Fact]
    public void Evaluate_UnloadedInSnapshotFetchables_ReachableFromNoLoadedParent_AreStillUnchecked()
    {
        // The four orphans are members of NOTHING and have no out-edges: no
        // walk can reach them (the loaded anchor walks elsewhere). Only the
        // O(V) load-state scan of Objects surfaces them — LdapProvider's
        // vanished-group arm leaves groups in exactly this state.
        const string anchorUserDn = "CN=Anchor User,OU=Frontier,DC=lab";
        const string dlOrphanDn = "CN=DL_Orphan,OU=Frontier,DC=lab";
        const string ggOrphanDn = "CN=GG_Orphan,OU=Frontier,DC=lab";
        const string ugOrphanDn = "CN=UG_Orphan,OU=Frontier,DC=lab";
        const string externalOrphanDn = "CN=S-1-5-21-0-0-0-2222,CN=ForeignSecurityPrincipals,DC=lab";
        var snapshot = Snapshot(
            Obj(ParentDn, AdObjectKind.GlobalGroup),
            Obj(anchorUserDn, AdObjectKind.User),
            Obj(dlOrphanDn, AdObjectKind.DomainLocalGroup),
            Obj(ggOrphanDn, AdObjectKind.GlobalGroup),
            Obj(ugOrphanDn, AdObjectKind.UniversalGroup),
            Obj(externalOrphanDn, AdObjectKind.External));
        snapshot.SetMembers(ParentDn, [anchorUserDn]);

        var report = RuleEngine.Evaluate(snapshot, DefaultsWithoutIgnore());

        // All four fetchable kinds — including the in-snapshot External FSP —
        // sorted OrdinalIgnoreCase.
        Assert.Equal(
            new[] { dlOrphanDn, ggOrphanDn, externalOrphanDn, ugOrphanDn },
            report.UncheckedDns);
    }

    // --- Never unchecked: loaded-and-empty, User, Computer, OU ----------------------------

    [Fact]
    public void Evaluate_LoadedAndEmptyParentsAndNonFetchableKinds_AreNeverUnchecked()
    {
        const string probeDn = "CN=GG_Probe,OU=Frontier,DC=lab"; // unloaded GG: the positive control
        const string emptyLeafDn = "CN=GG_EmptyLeaf,OU=Frontier,DC=lab";
        const string loadedExternalDn = "CN=S-1-5-21-0-0-0-1106,CN=ForeignSecurityPrincipals,DC=lab";
        const string memberUserDn = "CN=Walked User,OU=Frontier,DC=lab";
        const string memberComputerDn = "CN=PC-001,OU=Frontier,DC=lab";
        const string memberOuDn = "OU=Walked,OU=Frontier,DC=lab";
        const string strayUserDn = "CN=Stray User,OU=Frontier,DC=lab";
        const string strayComputerDn = "CN=PC-002,OU=Frontier,DC=lab";
        const string strayOuDn = "OU=Stray,OU=Frontier,DC=lab";
        var snapshot = Snapshot(
            Obj(ParentDn, AdObjectKind.GlobalGroup),
            Obj(probeDn, AdObjectKind.GlobalGroup),
            Obj(emptyLeafDn, AdObjectKind.GlobalGroup),
            Obj(loadedExternalDn, AdObjectKind.External),
            Obj(memberUserDn, AdObjectKind.User),
            Obj(memberComputerDn, AdObjectKind.Computer),
            Obj(memberOuDn, AdObjectKind.OrganizationalUnit),
            Obj(strayUserDn, AdObjectKind.User),
            Obj(strayComputerDn, AdObjectKind.Computer),
            Obj(strayOuDn, AdObjectKind.OrganizationalUnit));
        snapshot.SetMembers(
            ParentDn,
            [memberUserDn, memberComputerDn, memberOuDn, emptyLeafDn, loadedExternalDn, probeDn]);
        snapshot.SetMembers(emptyLeafDn, []); // loaded-and-empty: null != empty (tri-state)
        snapshot.SetMembers(loadedExternalDn, []); // loaded External: fetchable kind, but already fetched

        var report = RuleEngine.Evaluate(snapshot, DefaultsWithoutIgnore());

        // ONLY the probe comes back: walk-reached users/computers/OUs are
        // unloaded but not fetchable; the strays prove the scan applies the
        // same kind filter; loaded-and-empty parents are checked, not unchecked.
        // The probe doubles as the positive control that the machinery ran.
        Assert.Equal(new[] { probeDn }, report.UncheckedDns);
    }

    // --- Dedup under Dn.Comparer: first-found spelling wins ---------------------------------

    [Fact]
    public void Evaluate_CaseVariantSpellingsOfTheSameUncheckedDn_DedupToTheFirstWalkedSpelling()
    {
        // The same raw member DN (absent from Objects) is reachable from two
        // loaded parents under case-variant spellings. The walks run in sorted
        // start order (FirstParent before SecondParent), so the lowercase
        // spelling is encountered first and must win the Dn.Comparer dedup —
        // ONE entry, never two.
        const string firstParentDn = "CN=GG_FirstParent,OU=Frontier,DC=lab";
        const string secondParentDn = "CN=GG_SecondParent,OU=Frontier,DC=lab";
        const string rawLowerDn = "cn=ghost member,dc=elsewhere";
        const string rawUpperDn = "CN=GHOST MEMBER,DC=ELSEWHERE";
        var snapshot = Snapshot(
            Obj(firstParentDn, AdObjectKind.GlobalGroup),
            Obj(secondParentDn, AdObjectKind.GlobalGroup));
        snapshot.SetMembers(firstParentDn, [rawLowerDn]);
        snapshot.SetMembers(secondParentDn, [rawUpperDn]);

        var report = RuleEngine.Evaluate(snapshot, DefaultsWithoutIgnore());

        // Ordinal equality pins both the dedup AND the deterministic
        // first-found spelling choice.
        Assert.Equal(new[] { rawLowerDn }, report.UncheckedDns);
    }

    // --- Ignore-independence: load-state truth, not a judgment --------------------------------

    [Fact]
    public void Evaluate_IgnoreEntriesAndCircularExceptions_NeverFilterUncheckedDns()
    {
        const string hiddenGroupDn = "CN=GG_Hidden,OU=Frontier,DC=lab";
        const string hiddenRawDn = "CN=Hidden Raw,DC=elsewhere";
        var snapshot = Snapshot(
            Obj(ParentDn, AdObjectKind.GlobalGroup),
            Obj(hiddenGroupDn, AdObjectKind.GlobalGroup));
        snapshot.SetMembers(ParentDn, [hiddenGroupDn, hiddenRawDn]);

        // A match-everything dn glob in the global ignore AND the circular
        // exceptions: every FINDING is suppressed (control below), but
        // UncheckedDns is load-state truth and must come back identical.
        var baseline = DefaultsWithoutIgnore();
        var allIgnoring = baseline with
        {
            Ignore = new[] { new MatchEntry { Dn = "*" } },
            Circular = baseline.Circular with { Exceptions = new[] { new MatchEntry { Dn = "*" } } },
        };

        var ignored = RuleEngine.Evaluate(snapshot, allIgnoring);
        Assert.Empty(ignored.Violations); // everything matched => every finding suppressed
        Assert.Equal(new[] { hiddenGroupDn, hiddenRawDn }, ignored.UncheckedDns);

        // Control: without the entries the snapshot DOES produce findings
        // (GG_Parent violates naming-gg) — the empty Violations above came from
        // suppression, and suppression left the frontier alone.
        var unfiltered = RuleEngine.Evaluate(snapshot, baseline);
        Assert.NotEmpty(unfiltered.Violations);
        Assert.Equal(ignored.UncheckedDns.ToArray(), unfiltered.UncheckedDns.ToArray());
    }

    // --- Always computed: every rule disabled ---------------------------------------------------

    [Fact(Timeout = 60_000)]
    public async Task Evaluate_AllRulesDisabled_StillComputesUncheckedDns()
    {
        const string cycleADn = "CN=GG_Cyc_A,OU=Frontier,DC=lab";
        const string cycleBDn = "CN=GG_Cyc_B,OU=Frontier,DC=lab";
        const string unloadedDn = "CN=GG_Unloaded,OU=Frontier,DC=lab";
        var snapshot = Snapshot(
            Obj(cycleADn, AdObjectKind.GlobalGroup),
            Obj(cycleBDn, AdObjectKind.GlobalGroup),
            Obj(unloadedDn, AdObjectKind.GlobalGroup));
        snapshot.SetMembers(cycleADn, [cycleBDn, unloadedDn]);
        snapshot.SetMembers(cycleBDn, [cycleADn]);

        var baseline = DefaultsWithoutIgnore();
        var allDisabled = baseline with
        {
            Nesting = baseline.Nesting with { Enabled = false },
            Naming = baseline.Naming.Select(rule => rule with { Enabled = false }).ToArray(),
            Circular = baseline.Circular with { Enabled = false },
            EmptyGroup = baseline.EmptyGroup with { Enabled = false },
        };

        // The sweep is no rule's servant: zero findings of any kind, yet the
        // frontier is computed — and the cyclic input must still terminate.
        var report = await Task.Run(() => RuleEngine.Evaluate(snapshot, allDisabled));

        Assert.Empty(report.Violations);
        Assert.Equal(new[] { unloadedDn }, report.UncheckedDns);
    }

    // --- Insertion-order determinism ---------------------------------------------------------------

    [Fact(Timeout = 60_000)]
    public async Task Evaluate_SameLogicalSnapshotBuiltInDifferentInsertionOrders_YieldsProjectionEqualReports()
    {
        var first = await Task.Run(() => RuleEngine.Evaluate(DeterminismSnapshot(scrambled: false), DefaultsWithoutIgnore()));
        var second = await Task.Run(() => RuleEngine.Evaluate(DeterminismSnapshot(scrambled: true), DefaultsWithoutIgnore()));

        // Projection comparison, never RuleViolation record equality (the Dns
        // list property makes record equality reference-based).
        Assert.Equal(
            first.Violations.Select(Projection).ToArray(),
            second.Violations.Select(Projection).ToArray());
        Assert.Equal(first.UncheckedDns.ToArray(), second.UncheckedDns.ToArray());

        // Non-vacuous: the snapshot exercises every block, in the pinned
        // canonical report order — two trivially empty reports could not pass.
        Assert.Equal(
            new[] { RuleIds.Nesting, RuleIds.Nesting, "naming-gg", RuleIds.Circular, RuleIds.EmptyGroup },
            first.Violations.Select(v => v.RuleId).ToArray());
        Assert.Equal(new[] { DetCycleADn, DetCycleBDn }, first.Violations[3].Dns);
        Assert.Equal(new[] { DetRawDn, DetBadNameDn }, first.UncheckedDns);
    }

    // --- Determinism fixture -------------------------------------------------------------------------

    private const string DetDlParentDn = "CN=DL_Det_RW,OU=Frontier,DC=lab";
    private const string DetUserDn = "CN=Det User,OU=Frontier,DC=lab";
    private const string DetCycleADn = "CN=GG_Det_A,OU=Frontier,DC=lab";
    private const string DetCycleBDn = "CN=GG_Det_B,OU=Frontier,DC=lab";
    private const string DetEmptyDn = "CN=GG_Det_Empty,OU=Frontier,DC=lab";
    private const string DetBadNameDn = "CN=SalesGlobal,OU=Frontier,DC=lab"; // fails naming-gg AND is unloaded
    private const string DetRawDn = "CN=Raw External,DC=elsewhere";

    /// <summary>One logical snapshot — a DL←User deny edge, a DL←External info
    /// edge, a GG cycle, a loaded-empty group, an unloaded naming violator —
    /// built in two different AddObject/SetMembers call orders. The member
    /// LISTS are identical (stored member order is part of the logical
    /// snapshot); only the call order varies, permuting Objects and Edges
    /// enumeration order underneath the engine.</summary>
    private static DirectorySnapshot DeterminismSnapshot(bool scrambled)
    {
        var objects = new[]
        {
            Obj(DetDlParentDn, AdObjectKind.DomainLocalGroup, name: "DL_Det_RW"),
            Obj(DetUserDn, AdObjectKind.User, name: "Det User"),
            Obj(DetCycleADn, AdObjectKind.GlobalGroup, name: "GG_Det_A"),
            Obj(DetCycleBDn, AdObjectKind.GlobalGroup, name: "GG_Det_B"),
            Obj(DetEmptyDn, AdObjectKind.GlobalGroup, name: "GG_Det_Empty"),
            Obj(DetBadNameDn, AdObjectKind.GlobalGroup, name: "SalesGlobal"),
        };

        var snapshot = new DirectorySnapshot();
        foreach (var obj in scrambled ? objects.Reverse() : objects)
        {
            snapshot.AddObject(obj);
        }

        if (scrambled)
        {
            snapshot.SetMembers(DetEmptyDn, []);
            snapshot.SetMembers(DetCycleBDn, [DetCycleADn]);
            snapshot.SetMembers(DetCycleADn, [DetCycleBDn]);
            snapshot.SetMembers(DetDlParentDn, [DetUserDn, DetRawDn]);
        }
        else
        {
            snapshot.SetMembers(DetDlParentDn, [DetUserDn, DetRawDn]);
            snapshot.SetMembers(DetCycleADn, [DetCycleBDn]);
            snapshot.SetMembers(DetCycleBDn, [DetCycleADn]);
            snapshot.SetMembers(DetEmptyDn, []);
        }

        return snapshot;
    }

    // --- Helpers ----------------------------------------------------------------------------------------

    /// <summary>The embedded default ruleset with the global ignore list cleared —
    /// suppression is opt-in per test, asserted both ways.</summary>
    private static Ruleset DefaultsWithoutIgnore() =>
        RulesetLoader.LoadDefault() with { Ignore = Array.Empty<MatchEntry>() };

    private static DirectorySnapshot Snapshot(params AdObject[] objects)
    {
        var snapshot = new DirectorySnapshot();
        foreach (var obj in objects)
        {
            snapshot.AddObject(obj);
        }

        return snapshot;
    }

    private static AdObject Obj(string dn, AdObjectKind kind, string? name = null) => new()
    {
        Dn = dn,
        Kind = kind,
        Name = name ?? dn,
    };

    /// <summary>THE comparison contract for violations: structured fields plus the
    /// Dns sequence — never RuleViolation record equality.</summary>
    private static (string RuleId, RuleSeverity Severity, string Dns, string Message) Projection(RuleViolation v) =>
        (v.RuleId, v.Severity, string.Join("\u001f", v.Dns), v.Message);
}
