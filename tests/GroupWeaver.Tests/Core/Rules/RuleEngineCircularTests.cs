using GroupWeaver.Core.Model;
using GroupWeaver.Core.Rules;

using Xunit;

namespace GroupWeaver.Tests.Core.Rules;

/// <summary>
/// Pins the AP 3.2 S5 circular-check semantics of <c>RuleEngine.Evaluate</c>
/// (ADR-009) over hand-built snapshots:
/// <list type="bullet">
/// <item>Sweep: starts are the DISTINCT EDGE PARENTS (never derived from
/// <c>Objects</c> — a cycle among loaded parents absent from Objects must
/// still be found), sorted OrdinalIgnoreCase, walked via
/// <c>MembershipTraversal.Walk</c> with one cumulative seen set.</item>
/// <item>Canonical cycle identity: each Walk path slice is ROTATED so the
/// <see cref="Dn.Comparer"/>-minimal DN comes first — rotation only, NEVER
/// reversal (membership direction is meaningful); self-membership <c>[A]</c>
/// canonicalizes to itself. Dedup across all walks is element-wise under
/// <see cref="Dn.Comparer"/>: case-variant spellings of the same cycle
/// collapse, the FIRST-FOUND spelling wins (deterministic via sorted starts).</item>
/// <item>Findings are deduped canonical back-edge cycles, ONE per distinct
/// canonical cycle (Walk's pinned decomposition) — not an exhaustive
/// simple-cycle enumeration. <c>Dns</c> = the canonical rotation, closing edge
/// implied last→first (no repeated element); severity =
/// <c>Circular.Severity</c>; RuleId = <see cref="RuleIds.Circular"/>.</item>
/// <item>Suppression: global ignore OR <c>Circular.Exceptions</c> matching ANY
/// cycle DN suppresses the WHOLE cycle, through the dual channel — in-snapshot
/// DNs as objects (dn and name globs), raw DNs via
/// <see cref="MatchEntry.MatchesDn"/> only (name globs never match raw DNs).
/// Tested BOTH WAYS.</item>
/// <item><c>Circular.Enabled == false</c> ⇒ zero cycle findings, but the sweep
/// still runs: <see cref="RuleReport.UncheckedDns"/> stays populated.</item>
/// </list>
/// Every cyclic snapshot runs under a Timeout guard plus <c>Task.Run</c> —
/// termination is proven, never trusted (ADR-006 D4 discipline). Violations are
/// filtered by rule id and compared field-wise — never via RuleViolation record
/// equality, which is reference-based over the <c>Dns</c> list property.
/// </summary>
public class RuleEngineCircularTests
{
    private const string CircleADn = "CN=GG_Circle_A,OU=Cycles,DC=lab";
    private const string CircleBDn = "CN=GG_Circle_B,OU=Cycles,DC=lab";

    // --- A<->B: one finding, canonical rotation, severity flows from the rule ---------

    [Fact(Timeout = 60_000)]
    public async Task Evaluate_TwoNodeCycle_YieldsExactlyOneCanonicalFinding_AtCircularRuleSeverity()
    {
        // The lab/demo pair shape: GG_Circle_A <-> GG_Circle_B. Both nodes are
        // edge parents and therefore starts — without cross-start dedup the
        // cycle would surface once per start.
        var snapshot = Snapshot(
            Obj(CircleADn, AdObjectKind.GlobalGroup, name: "GG_Circle_A"),
            Obj(CircleBDn, AdObjectKind.GlobalGroup, name: "GG_Circle_B"));
        snapshot.SetMembers(CircleADn, [CircleBDn]);
        snapshot.SetMembers(CircleBDn, [CircleADn]);

        // Non-default severity pins that Circular.Severity flows into the
        // finding — a hard-coded Error would pass the default config unnoticed.
        var ruleset = DefaultsWithoutIgnore();
        ruleset = ruleset with { Circular = ruleset.Circular with { Severity = RuleSeverity.Warning } };

        var report = await Task.Run(() => RuleEngine.Evaluate(snapshot, ruleset));

        var finding = Assert.Single(CircularFindings(report));
        Assert.Equal(RuleIds.Circular, finding.RuleId);
        Assert.Equal(RuleSeverity.Warning, finding.Severity);

        // Canonical rotation: Dn.Comparer-minimal DN first, closing edge B->A
        // implied last->first — the Dns list never repeats the anchor.
        Assert.Equal(new[] { CircleADn, CircleBDn }, finding.Dns);

        // Message prose names the cycle by object Names and repeats the anchor
        // at the END in prose only. (The exact template is pinned by the S6
        // baseline; here the load-bearing fragments suffice.)
        Assert.Contains("Circular nesting:", finding.Message, StringComparison.Ordinal);
        Assert.Contains("GG_Circle_A -> GG_Circle_B -> GG_Circle_A", finding.Message, StringComparison.Ordinal);
    }

    // --- Canonical rotation: min-DN first, rotation only — NEVER reversal --------------

    [Fact(Timeout = 60_000)]
    public async Task Evaluate_CycleEnteredAtANonMinimalNode_IsRotatedToTheMinimalDn_NeverReversed()
    {
        // Lead -> Zed -> Bot -> Mid -> Zed. Sorted starts walk Lead first
        // ("Aaa" sorts before the cycle nodes), so the Walk slice is
        // [Zed, Bot, Mid] — anchored at the entry node, NOT at the minimal DN.
        const string leadDn = "CN=GG_Aaa_Lead,OU=Cycles,DC=lab";
        const string botDn = "CN=GG_Bot,OU=Cycles,DC=lab";
        const string midDn = "CN=GG_Mid,OU=Cycles,DC=lab";
        const string zedDn = "CN=GG_Zed,OU=Cycles,DC=lab";
        var snapshot = Snapshot(
            Obj(leadDn, AdObjectKind.GlobalGroup),
            Obj(botDn, AdObjectKind.GlobalGroup),
            Obj(midDn, AdObjectKind.GlobalGroup),
            Obj(zedDn, AdObjectKind.GlobalGroup));
        snapshot.SetMembers(leadDn, [zedDn]);
        snapshot.SetMembers(zedDn, [botDn]);
        snapshot.SetMembers(botDn, [midDn]);
        snapshot.SetMembers(midDn, [zedDn]);

        var report = await Task.Run(() => RuleEngine.Evaluate(snapshot, DefaultsWithoutIgnore()));

        // Rotating [Zed, Bot, Mid] to the minimal DN (Bot) gives [Bot, Mid, Zed]
        // — membership direction preserved (Bot contains Mid, Mid contains Zed,
        // closing Zed->Bot). A REVERSAL from the minimal DN would read
        // [Bot, Zed, Mid] and must be rejected: membership direction is meaningful.
        var finding = Assert.Single(CircularFindings(report));
        Assert.Equal(new[] { botDn, midDn, zedDn }, finding.Dns);
    }

    // --- Cross-start dedup: cumulative seen + element-wise sequence dedup ---------------

    [Fact(Timeout = 60_000)]
    public async Task Evaluate_CycleReachableFromTwoSeparateUnseenStarts_YieldsStillOneFinding()
    {
        // Ent1 -> Mem1 <-> Mem2 <- Ent2. Sorted starts: Ent1, Ent2, Mem1, Mem2.
        // Walk(Ent1) reports the cycle as [Mem1, Mem2]; Ent2 is still UNSEEN
        // (cumulative seen gates STARTS, Walk itself is memoryless), so
        // Walk(Ent2) re-enters at Mem2 and reports the rotation-variant
        // [Mem2, Mem1]. Canonicalization + element-wise Dn.Comparer sequence
        // dedup must collapse all of it into ONE finding.
        const string entOneDn = "CN=GG_Ent1,OU=Cycles,DC=lab";
        const string entTwoDn = "CN=GG_Ent2,OU=Cycles,DC=lab";
        const string memOneDn = "CN=GG_Mem1,OU=Cycles,DC=lab";
        const string memTwoDn = "CN=GG_Mem2,OU=Cycles,DC=lab";
        var snapshot = Snapshot(
            Obj(entOneDn, AdObjectKind.GlobalGroup),
            Obj(entTwoDn, AdObjectKind.GlobalGroup),
            Obj(memOneDn, AdObjectKind.GlobalGroup),
            Obj(memTwoDn, AdObjectKind.GlobalGroup));
        snapshot.SetMembers(entOneDn, [memOneDn]);
        snapshot.SetMembers(entTwoDn, [memTwoDn]);
        snapshot.SetMembers(memOneDn, [memTwoDn]);
        snapshot.SetMembers(memTwoDn, [memOneDn]);

        var report = await Task.Run(() => RuleEngine.Evaluate(snapshot, DefaultsWithoutIgnore()));

        var finding = Assert.Single(CircularFindings(report));
        Assert.Equal(new[] { memOneDn, memTwoDn }, finding.Dns);
    }

    // --- Case-variant spellings of the same cycle collapse ------------------------------

    [Fact(Timeout = 60_000)]
    public async Task Evaluate_CaseVariantSpellingsOfTheSameCycle_CollapseToTheFirstFoundSpelling()
    {
        // The cycle CaseA <-> CaseB is stored in exact spellings; two leads
        // reach it through case-variant member spellings (lowercase A,
        // UPPERCASE B). Walk reports first-encountered spellings per walk, so
        // the lead walks yield Dn.Comparer-equal but ordinally different
        // sequences — they must collapse, and the FIRST-found spelling (from
        // the sorted-first start CaseA, i.e. the exact one) must win.
        const string caseADn = "CN=GG_CaseA,OU=Cycles,DC=lab";
        const string caseBDn = "CN=GG_CaseB,OU=Cycles,DC=lab";
        const string leadOneDn = "CN=GG_Lead1,OU=Cycles,DC=lab";
        const string leadTwoDn = "CN=GG_Lead2,OU=Cycles,DC=lab";
        var snapshot = Snapshot(
            Obj(caseADn, AdObjectKind.GlobalGroup),
            Obj(caseBDn, AdObjectKind.GlobalGroup),
            Obj(leadOneDn, AdObjectKind.GlobalGroup),
            Obj(leadTwoDn, AdObjectKind.GlobalGroup));
        snapshot.SetMembers(caseADn, [caseBDn]);
        snapshot.SetMembers(caseBDn, [caseADn]);
        snapshot.SetMembers(leadOneDn, [caseADn.ToLowerInvariant()]);
        snapshot.SetMembers(leadTwoDn, [caseBDn.ToUpperInvariant()]);

        var report = await Task.Run(() => RuleEngine.Evaluate(snapshot, DefaultsWithoutIgnore()));

        // Ordinal sequence equality rejects the lowercase/UPPERCASE variants.
        var finding = Assert.Single(CircularFindings(report));
        Assert.Equal(new[] { caseADn, caseBDn }, finding.Dns);
    }

    // --- Self-membership: the single-element canonical cycle ----------------------------

    [Fact(Timeout = 60_000)]
    public async Task Evaluate_SelfMembership_YieldsTheSingleElementCanonicalCycle()
    {
        const string selfDn = "CN=GG_Self,OU=Cycles,DC=lab";
        var snapshot = Snapshot(Obj(selfDn, AdObjectKind.GlobalGroup, name: "GG_Self"));
        snapshot.SetMembers(selfDn, [selfDn]); // GG<-GG is an allow CELL: no nesting noise

        var report = await Task.Run(() => RuleEngine.Evaluate(snapshot, DefaultsWithoutIgnore()));

        // [A] canonicalizes to itself: one element, closing edge A->A implied.
        var finding = Assert.Single(CircularFindings(report));
        Assert.Equal(new[] { selfDn }, finding.Dns);
        Assert.Equal(RuleSeverity.Error, finding.Severity); // default Circular.Severity
        Assert.Contains("Circular nesting:", finding.Message, StringComparison.Ordinal);
        Assert.Contains("GG_Self", finding.Message, StringComparison.Ordinal);
    }

    // --- Interlocking cycles: one finding per distinct canonical back-edge cycle ----------

    [Fact(Timeout = 60_000)]
    public async Task Evaluate_InterlockingCycles_YieldOneFindingPerDistinctCanonicalBackEdgeCycle()
    {
        // The MembershipTraversalTests 4-cycles shape: A->B->C->A, B->C->D->B,
        // C->D->E->C and the full loop A->B->C->D->E->A over 8 edges. Walk from
        // the sorted-first start A yields exactly the four pinned back-edge
        // slices — all already min-DN-first, all distinct under the element-wise
        // sequence comparer: four findings, never more (no exhaustive
        // simple-cycle enumeration), never fewer.
        const string aDn = "CN=GG_A,OU=Cycles,DC=lab";
        const string bDn = "CN=GG_B,OU=Cycles,DC=lab";
        const string cDn = "CN=GG_C,OU=Cycles,DC=lab";
        const string dDn = "CN=GG_D,OU=Cycles,DC=lab";
        const string eDn = "CN=GG_E,OU=Cycles,DC=lab";
        var snapshot = Snapshot(
            Obj(aDn, AdObjectKind.GlobalGroup),
            Obj(bDn, AdObjectKind.GlobalGroup),
            Obj(cDn, AdObjectKind.GlobalGroup),
            Obj(dDn, AdObjectKind.GlobalGroup),
            Obj(eDn, AdObjectKind.GlobalGroup));
        snapshot.SetMembers(aDn, [bDn]);
        snapshot.SetMembers(bDn, [cDn]);
        snapshot.SetMembers(cDn, [aDn, dDn]);
        snapshot.SetMembers(dDn, [bDn, eDn]);
        snapshot.SetMembers(eDn, [cDn, aDn]);

        var report = await Task.Run(() => RuleEngine.Evaluate(snapshot, DefaultsWithoutIgnore()));

        // Report order within the circular block: element-wise OrdinalIgnoreCase
        // over Dns, shorter prefix first — [A,B,C] precedes [A,B,C,D,E].
        var findings = CircularFindings(report);
        Assert.Equal(4, findings.Length);
        Assert.Equal(new[] { aDn, bDn, cDn }, findings[0].Dns);
        Assert.Equal(new[] { aDn, bDn, cDn, dDn, eDn }, findings[1].Dns);
        Assert.Equal(new[] { bDn, cDn, dDn }, findings[2].Dns);
        Assert.Equal(new[] { cDn, dDn, eDn }, findings[3].Dns);
        Assert.All(findings, v => Assert.Equal(RuleSeverity.Error, v.Severity));
    }

    // --- Starts derive from edge parents, not Objects --------------------------------------

    [Fact(Timeout = 60_000)]
    public async Task Evaluate_CycleAmongLoadedParentsAbsentFromObjects_IsStillDetected()
    {
        // SetMembers does not require the parent in Objects (data-model
        // contract; the WorkspaceViewModel vanished-parent expand arm produces
        // exactly this). An Objects-derived start set would never root this
        // cycle — starts MUST derive from the edge parents.
        const string ghostADn = "CN=GG_Ghost_A,OU=Cycles,DC=lab";
        const string ghostBDn = "CN=GG_Ghost_B,OU=Cycles,DC=lab";
        var snapshot = new DirectorySnapshot();
        snapshot.SetMembers(ghostADn, [ghostBDn]);
        snapshot.SetMembers(ghostBDn, [ghostADn]);

        var report = await Task.Run(() => RuleEngine.Evaluate(snapshot, DefaultsWithoutIgnore()));

        var finding = Assert.Single(CircularFindings(report));
        Assert.Equal(new[] { ghostADn, ghostBDn }, finding.Dns);

        // Neither DN resolves in Objects: the message names them by their raw
        // DNs verbatim, never by a fabricated Name.
        Assert.Contains(ghostADn, finding.Message, StringComparison.Ordinal);
        Assert.Contains(ghostBDn, finding.Message, StringComparison.Ordinal);

        // Both ghosts are LOADED parents: nothing here is unchecked.
        Assert.Empty(report.UncheckedDns);
    }

    // --- Suppression: global ignore and Circular.Exceptions, any cycle DN, both ways --------

    [Theory(Timeout = 60_000)]
    [InlineData(true, true)] // global ignore, dn glob
    [InlineData(true, false)] // global ignore, name glob
    [InlineData(false, true)] // Circular.Exceptions, dn glob
    [InlineData(false, false)] // Circular.Exceptions, name glob
    public async Task Evaluate_EntryMatchingAnyCycleDn_SuppressesTheWholeCycle_BothWays(
        bool viaGlobalIgnore, bool viaDnGlob)
    {
        var snapshot = Snapshot(
            Obj(CircleADn, AdObjectKind.GlobalGroup, name: "GG_Circle_A"),
            Obj(CircleBDn, AdObjectKind.GlobalGroup, name: "GG_Circle_B"));
        snapshot.SetMembers(CircleADn, [CircleBDn]);
        snapshot.SetMembers(CircleBDn, [CircleADn]);

        // The entry matches ONLY the B node (object channel: dn or name glob);
        // one matching cycle DN must take the WHOLE cycle out — including the
        // unmatched A node.
        var entry = viaDnGlob
            ? new MatchEntry { Dn = "CN=GG_Circle_B,*" }
            : new MatchEntry { Name = "GG_Circle_B" };
        var baseline = DefaultsWithoutIgnore();
        var suppressing = viaGlobalIgnore
            ? baseline with { Ignore = new[] { entry } }
            : baseline with { Circular = baseline.Circular with { Exceptions = new[] { entry } } };

        // Entry present => the whole cycle is suppressed ...
        var suppressed = await Task.Run(() => RuleEngine.Evaluate(snapshot, suppressing));
        Assert.Empty(CircularFindings(suppressed));

        // ... entry removed => exactly this finding appears (both-ways discipline).
        var unsuppressed = await Task.Run(() => RuleEngine.Evaluate(snapshot, baseline));
        var finding = Assert.Single(CircularFindings(unsuppressed));
        Assert.Equal(new[] { CircleADn, CircleBDn }, finding.Dns);
    }

    [Theory(Timeout = 60_000)]
    [InlineData(true)] // global ignore
    [InlineData(false)] // Circular.Exceptions
    public async Task Evaluate_RawCycleDn_IsSuppressedThroughTheMatchesDnChannel_BothWays(bool viaGlobalIgnore)
    {
        // The whole cycle lives outside Objects (loaded ghost parents): cycle
        // DNs are raw, so suppression can only travel the MatchesDn channel.
        const string ghostADn = "CN=GG_Ghost_A,OU=Cycles,DC=lab";
        const string ghostBDn = "CN=GG_Ghost_B,OU=Cycles,DC=lab";
        var snapshot = new DirectorySnapshot();
        snapshot.SetMembers(ghostADn, [ghostBDn]);
        snapshot.SetMembers(ghostBDn, [ghostADn]);

        var entry = new MatchEntry { Dn = "CN=GG_Ghost_B,*" }; // matches one raw cycle DN
        var baseline = DefaultsWithoutIgnore();
        var suppressing = viaGlobalIgnore
            ? baseline with { Ignore = new[] { entry } }
            : baseline with { Circular = baseline.Circular with { Exceptions = new[] { entry } } };

        var suppressed = await Task.Run(() => RuleEngine.Evaluate(snapshot, suppressing));
        Assert.Empty(CircularFindings(suppressed));

        var unsuppressed = await Task.Run(() => RuleEngine.Evaluate(snapshot, baseline));
        var finding = Assert.Single(CircularFindings(unsuppressed));
        Assert.Equal(new[] { ghostADn, ghostBDn }, finding.Dns);
    }

    [Fact(Timeout = 60_000)]
    public async Task Evaluate_NameGlobEntries_NeverMatchARawCycleDn()
    {
        const string ghostADn = "CN=GG_Ghost_A,OU=Cycles,DC=lab";
        const string ghostBDn = "CN=GG_Ghost_B,OU=Cycles,DC=lab";
        var snapshot = new DirectorySnapshot();
        snapshot.SetMembers(ghostADn, [ghostBDn]);
        snapshot.SetMembers(ghostBDn, [ghostADn]);

        // This glob WOULD match the raw DN string if name entries were wrongly
        // globbed against DNs ('*' crosses commas; glob matching is
        // case-insensitive). A raw DN has no name: only the MatchesDn channel
        // applies there, and name entries never match it — stacked in BOTH
        // suppression lists, the finding must survive.
        var entry = new MatchEntry { Name = "*Ghost_B*" };
        var ruleset = DefaultsWithoutIgnore();
        ruleset = ruleset with
        {
            Ignore = new[] { entry },
            Circular = ruleset.Circular with { Exceptions = new[] { entry } },
        };

        var report = await Task.Run(() => RuleEngine.Evaluate(snapshot, ruleset));

        var finding = Assert.Single(CircularFindings(report));
        Assert.Equal(new[] { ghostADn, ghostBDn }, finding.Dns);
    }

    // --- Enabled gate: zero findings, but the sweep still feeds the frontier -----------------

    [Fact(Timeout = 60_000)]
    public async Task Evaluate_CircularDisabled_YieldsZeroCycleFindings_ButUncheckedDnsStaysPopulated()
    {
        const string unloadedDn = "CN=GG_Unloaded,OU=Cycles,DC=lab";
        var snapshot = Snapshot(
            Obj(CircleADn, AdObjectKind.GlobalGroup),
            Obj(CircleBDn, AdObjectKind.GlobalGroup),
            Obj(unloadedDn, AdObjectKind.GlobalGroup));
        snapshot.SetMembers(CircleADn, [CircleBDn, unloadedDn]);
        snapshot.SetMembers(CircleBDn, [CircleADn]);

        var ruleset = DefaultsWithoutIgnore();
        ruleset = ruleset with { Circular = ruleset.Circular with { Enabled = false } };

        // Disabled gates only the cycle-to-finding CONVERSION — the sweep
        // always runs (it feeds UncheckedDns) and must still terminate on the
        // cyclic input.
        var report = await Task.Run(() => RuleEngine.Evaluate(snapshot, ruleset));
        Assert.Empty(CircularFindings(report));
        Assert.Equal(new[] { unloadedDn }, report.UncheckedDns);

        // Control: the same cycle fires when enabled — the silence above comes
        // from the gate, not from a missing evaluator.
        var enabled = await Task.Run(() => RuleEngine.Evaluate(snapshot, DefaultsWithoutIgnore()));
        var finding = Assert.Single(CircularFindings(enabled));
        Assert.Equal(new[] { CircleADn, CircleBDn }, finding.Dns);
    }

    // --- Helpers ----------------------------------------------------------------------------

    /// <summary>The circular block of the report, in report order. Tests filter by
    /// rule id so co-firing blocks (naming, empty-group) never leak in.</summary>
    private static RuleViolation[] CircularFindings(RuleReport report) =>
        report.Violations.Where(v => v.RuleId == RuleIds.Circular).ToArray();

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
}
