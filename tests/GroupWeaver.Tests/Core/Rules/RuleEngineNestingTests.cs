using GroupWeaver.Core.Model;
using GroupWeaver.Core.Rules;

using Xunit;

namespace GroupWeaver.Tests.Core.Rules;

/// <summary>
/// Pins the AP 3.2 S4 nesting-matrix semantics of <c>RuleEngine.Evaluate</c>
/// (ADR-009) over hand-built snapshots:
/// <list type="bullet">
/// <item>Judged domain: only edges whose PARENT kind is GG/DL/UG. OU, User,
/// Computer, in-snapshot External, and loaded-but-absent-from-Objects parents
/// (GetKind == External) are never judged.</item>
/// <item>A member DN absent from the snapshot is kind External by definition
/// and is judged under the External COLUMN — the raw builtin/FSP edge is never
/// skipped; the message names it by its raw DN verbatim.</item>
/// <item>A missing matrix row AND a missing column both fall back to
/// <c>Nesting.Unlisted</c> (fails closed); effective severity is
/// <c>Cell.SeverityOverride ?? Nesting.Severity</c>.</item>
/// <item>Finding: <c>Dns == [ParentDn, ChildDn]</c> in the EDGE's stored
/// spellings (never the object respellings, never canonicalized).</item>
/// <item>Suppression: global ignore exempts the edge when EITHER endpoint
/// matches, through the dual channel — in-snapshot endpoints as objects
/// (dn and name globs), raw DNs via <see cref="MatchEntry.MatchesDn"/> only
/// (a name glob NEVER matches a raw DN). <c>Nesting.Exceptions</c> honor
/// endpoint narrowing: Parent tests only ParentDn, Member only ChildDn,
/// Any either. Each suppression is tested BOTH WAYS.</item>
/// <item>A self-edge A→A is a normal cell lookup, not a special case —
/// circularity belongs to the separate circular rule.</item>
/// <item><c>Enabled == false</c> ⇒ zero nesting findings (control: the same
/// edge fires when enabled).</item>
/// </list>
/// Violations are filtered by rule id / compared field-wise — never via
/// RuleViolation record equality, which is reference-based over <c>Dns</c>.
/// </summary>
public class RuleEngineNestingTests
{
    private const string DlParentDn = "CN=DL_Edge_RW,OU=Rules,DC=lab";
    private const string MemberUserDn = "CN=Mem User,OU=Members,DC=lab";
    private const string RawMemberDn = "CN=Raw Member,OU=Members,DC=lab";

    private static readonly IReadOnlyDictionary<AdObjectKind, IReadOnlyDictionary<AdObjectKind, NestingCell>> EmptyMatrix =
        new Dictionary<AdObjectKind, IReadOnlyDictionary<AdObjectKind, NestingCell>>();

    // --- Deny cell fires: [parent, member], severity flows from the rule -------------

    [Fact]
    public void Evaluate_DenyCellWithoutOverride_Fires_AtNestingRuleSeverity_WithParentThenMemberDns()
    {
        var snapshot = Snapshot(
            Obj(DlParentDn, AdObjectKind.DomainLocalGroup, name: "DL_Edge_RW"),
            Obj(MemberUserDn, AdObjectKind.User, name: "Mem User"));
        snapshot.SetMembers(DlParentDn, [MemberUserDn]); // DL<-User: default cell deny, no override

        // Non-default severity pins that Nesting.Severity flows into no-override
        // deny cells — a hard-coded Error would pass the default config unnoticed.
        var ruleset = DefaultsWithoutIgnore();
        ruleset = ruleset with { Nesting = ruleset.Nesting with { Severity = RuleSeverity.Warning } };

        var finding = Assert.Single(NestingFindings(RuleEngine.Evaluate(snapshot, ruleset)));

        Assert.Equal(RuleIds.Nesting, finding.RuleId);
        Assert.Equal(RuleSeverity.Warning, finding.Severity);
        Assert.Equal(new[] { DlParentDn, MemberUserDn }, finding.Dns); // [parent, member], never reversed

        // Both endpoints resolve in the snapshot: the message names them by
        // Name, never by raw DN. (The exact template is pinned by the S6
        // baseline; here the load-bearing fragments suffice.)
        Assert.Contains("'DL_Edge_RW'", finding.Message, StringComparison.Ordinal);
        Assert.Contains("'Mem User'", finding.Message, StringComparison.Ordinal);
        Assert.Contains("denied by the nesting matrix.", finding.Message, StringComparison.Ordinal);
    }

    // --- Effective severity: Cell.SeverityOverride ?? Nesting.Severity -----------------

    [Fact]
    public void Evaluate_PerCellSeverityOverride_WinsOverRuleSeverity_NoOverrideFlowsFromRule()
    {
        const string ugParentDn = "CN=UG_Outer,OU=Rules,DC=lab";
        const string ugMemberDn = "CN=UG_Inner,OU=Rules,DC=lab";
        const string dlParentDn = "CN=DL_Outer_RW,OU=Rules,DC=lab";
        const string dlMemberDn = "CN=DL_Inner_RW,OU=Rules,DC=lab";
        var snapshot = Snapshot(
            Obj(ugParentDn, AdObjectKind.UniversalGroup),
            Obj(ugMemberDn, AdObjectKind.UniversalGroup),
            Obj(dlParentDn, AdObjectKind.DomainLocalGroup),
            Obj(dlMemberDn, AdObjectKind.DomainLocalGroup));
        snapshot.SetMembers(ugParentDn, [ugMemberDn]); // UG<-UG: default cell "warning" (per-cell override)
        snapshot.SetMembers(dlParentDn, [dlMemberDn]); // DL<-DL: default cell "deny" (no override)

        // Rule severity Info: the no-override cell must follow it while the
        // UG<-UG cell keeps its own Warning — SeverityOverride ?? Nesting.Severity.
        var ruleset = DefaultsWithoutIgnore();
        ruleset = ruleset with { Nesting = ruleset.Nesting with { Severity = RuleSeverity.Info } };

        var findings = NestingFindings(RuleEngine.Evaluate(snapshot, ruleset));

        Assert.Equal(2, findings.Length); // element-wise OrdinalIgnoreCase Dns order: DL edge first
        Assert.Equal(new[] { dlParentDn, dlMemberDn }, findings[0].Dns);
        Assert.Equal(RuleSeverity.Info, findings[0].Severity);
        Assert.Equal(new[] { ugParentDn, ugMemberDn }, findings[1].Dns);
        Assert.Equal(RuleSeverity.Warning, findings[1].Severity);
    }

    // --- Judged domain: only GG/DL/UG parents ------------------------------------------

    [Fact]
    public void Evaluate_OuUserComputerExternalAndAbsentParents_AreNeverJudged()
    {
        const string ggParentDn = "CN=GG_Control,OU=Rules,DC=lab";
        const string ouParentDn = "OU=Unit,OU=Rules,DC=lab";
        const string userParentDn = "CN=User Parent,OU=Rules,DC=lab";
        const string computerParentDn = "CN=PC-001,OU=Rules,DC=lab";
        const string externalParentDn = "CN=S-1-5-21-0-0-0-1106,CN=ForeignSecurityPrincipals,DC=lab";
        const string absentParentDn = "CN=GG_Vanished,OU=Rules,DC=lab";
        var snapshot = Snapshot(
            Obj(ggParentDn, AdObjectKind.GlobalGroup),
            Obj(ouParentDn, AdObjectKind.OrganizationalUnit),
            Obj(userParentDn, AdObjectKind.User),
            Obj(computerParentDn, AdObjectKind.Computer),
            Obj(externalParentDn, AdObjectKind.External),
            Obj(MemberUserDn, AdObjectKind.User));
        snapshot.SetMembers(ggParentDn, [MemberUserDn]);
        snapshot.SetMembers(ouParentDn, [MemberUserDn]);
        snapshot.SetMembers(userParentDn, [MemberUserDn]);
        snapshot.SetMembers(computerParentDn, [MemberUserDn]);
        snapshot.SetMembers(externalParentDn, [MemberUserDn]);
        // Loaded-with-members but absent from Objects: GetKind == External, out
        // of the judged domain (the WorkspaceViewModel vanished-parent expand arm).
        snapshot.SetMembers(absentParentDn, [MemberUserDn]);

        // Empty matrix + deny Unlisted: EVERY judged edge would fire — the one
        // finding coming back proves exactly which parents are in the judged
        // domain, and the GG control proves the machinery ran at all.
        var ruleset = WithNesting(EmptyMatrix, unlisted: new NestingCell(false, null));

        var finding = Assert.Single(NestingFindings(RuleEngine.Evaluate(snapshot, ruleset)));
        Assert.Equal(new[] { ggParentDn, MemberUserDn }, finding.Dns);
    }

    // --- Raw member DN: External COLUMN, judged — never skipped --------------------------

    [Fact]
    public void Evaluate_RawMemberDnAbsentFromSnapshot_IsJudgedUnderTheExternalColumn()
    {
        // The demo builtin-imitation edge: an in-snapshot DL whose member DN
        // resolves to nothing. Kind External by definition — judged under the
        // DL row's External column (default cell: disallowed, Info override),
        // NEVER skipped.
        const string rawBuiltinDn = "CN=Print Operators,CN=Builtin,DC=lab";
        var snapshot = Snapshot(Obj(DlParentDn, AdObjectKind.DomainLocalGroup, name: "DL_Edge_RW"));
        snapshot.SetMembers(DlParentDn, [rawBuiltinDn]);

        var finding = Assert.Single(NestingFindings(RuleEngine.Evaluate(snapshot, DefaultsWithoutIgnore())));

        Assert.Equal(RuleSeverity.Info, finding.Severity); // the DL<-External per-cell override
        Assert.Equal(new[] { DlParentDn, rawBuiltinDn }, finding.Dns);

        // An unresolvable member is named by its raw DN verbatim in the message;
        // the resolving parent stays Name-addressed.
        Assert.Contains($"'{rawBuiltinDn}'", finding.Message, StringComparison.Ordinal);
        Assert.Contains("'DL_Edge_RW'", finding.Message, StringComparison.Ordinal);
    }

    // --- Unlisted fails closed: missing row AND missing column ----------------------------

    [Fact]
    public void Evaluate_MissingRowAndMissingColumn_BothFailClosedThroughTheUnlistedCell()
    {
        const string ggParentDn = "CN=GG_Control,OU=Rules,DC=lab";
        const string ggMemberDn = "CN=GG_Inner,OU=Rules,DC=lab";
        var snapshot = Snapshot(
            Obj(ggParentDn, AdObjectKind.GlobalGroup),
            Obj(ggMemberDn, AdObjectKind.GlobalGroup),
            Obj(DlParentDn, AdObjectKind.DomainLocalGroup),
            Obj(MemberUserDn, AdObjectKind.User));
        snapshot.SetMembers(ggParentDn, [MemberUserDn, ggMemberDn]); // User column listed allow; GG column MISSING
        snapshot.SetMembers(DlParentDn, [MemberUserDn]); // DL row MISSING entirely

        // One row (GG), one column (User: allow). The Unlisted cell carries its
        // own severity override, distinct from Nesting.Severity — both findings
        // landing at Info proves they fell back to Unlisted rather than hitting
        // any listed cell.
        var matrix = new Dictionary<AdObjectKind, IReadOnlyDictionary<AdObjectKind, NestingCell>>
        {
            [AdObjectKind.GlobalGroup] = new Dictionary<AdObjectKind, NestingCell>
            {
                [AdObjectKind.User] = new NestingCell(true, null),
            },
        };
        var ruleset = WithNesting(matrix, unlisted: new NestingCell(false, RuleSeverity.Info), severity: RuleSeverity.Error);

        var findings = NestingFindings(RuleEngine.Evaluate(snapshot, ruleset));

        Assert.Equal(2, findings.Length); // the listed GG<-User allow edge stays silent
        Assert.Equal(new[] { DlParentDn, MemberUserDn }, findings[0].Dns); // missing row
        Assert.Equal(new[] { ggParentDn, ggMemberDn }, findings[1].Dns); // missing column
        Assert.All(findings, v => Assert.Equal(RuleSeverity.Info, v.Severity));
    }

    // --- Dns carry the EDGE's stored spellings ----------------------------------------------

    [Fact]
    public void Evaluate_FindingCarriesTheEdgeSpellings_NeverTheObjectRespellings()
    {
        // Objects stored in one case, the edge loaded under case-variant
        // spellings: kind resolution and suppression key via Dn.Comparer, but
        // the finding must carry the EDGE spellings verbatim — DN strings are
        // stored as-given and never canonicalized (data-model contract).
        var parentEdgeSpelling = DlParentDn.ToLowerInvariant();
        var memberEdgeSpelling = MemberUserDn.ToUpperInvariant();
        var snapshot = Snapshot(
            Obj(DlParentDn, AdObjectKind.DomainLocalGroup),
            Obj(MemberUserDn, AdObjectKind.User));
        snapshot.SetMembers(parentEdgeSpelling, [memberEdgeSpelling]);

        var finding = Assert.Single(NestingFindings(RuleEngine.Evaluate(snapshot, DefaultsWithoutIgnore())));

        // Ordinal sequence equality rejects the object spellings.
        Assert.Equal(new[] { parentEdgeSpelling, memberEdgeSpelling }, finding.Dns);
    }

    // --- Global ignore: either endpoint, dual channel, both ways ------------------------------

    [Theory]
    [InlineData("*,OU=Rules,*", null, false)] // dn glob on the PARENT (object channel)
    [InlineData(null, "DL_Edge_R?", false)] // name glob on the PARENT (object channel)
    [InlineData("CN=Mem User,*", null, false)] // dn glob on the in-snapshot MEMBER (object channel)
    [InlineData(null, "Mem Use?", false)] // name glob on the in-snapshot MEMBER (object channel)
    [InlineData("CN=Raw Member,*", null, true)] // dn glob on a RAW member DN (MatchesDn channel)
    public void Evaluate_GlobalIgnore_ExemptsTheEdgeOnEitherEndpoint_ThroughBothChannels_BothWays(
        string? dnGlob, string? nameGlob, bool rawMember)
    {
        var snapshot = Snapshot(
            Obj(DlParentDn, AdObjectKind.DomainLocalGroup, name: "DL_Edge_RW"),
            Obj(MemberUserDn, AdObjectKind.User, name: "Mem User"));
        var memberDn = rawMember ? RawMemberDn : MemberUserDn;
        snapshot.SetMembers(DlParentDn, [memberDn]); // DL<-User deny / DL<-External info: fires either way

        var entry = new MatchEntry { Dn = dnGlob, Name = nameGlob };
        var baseline = DefaultsWithoutIgnore();
        var suppressing = baseline with { Ignore = new[] { entry } };

        // Entry present => the whole edge is exempt (ONE matching endpoint suffices) ...
        Assert.Empty(NestingFindings(RuleEngine.Evaluate(snapshot, suppressing)));

        // ... entry removed => exactly this finding appears (both-ways discipline).
        var finding = Assert.Single(NestingFindings(RuleEngine.Evaluate(snapshot, baseline)));
        Assert.Equal(new[] { DlParentDn, memberDn }, finding.Dns);
    }

    [Fact]
    public void Evaluate_NameGlobIgnoreEntry_NeverMatchesARawMemberDn()
    {
        var snapshot = Snapshot(Obj(DlParentDn, AdObjectKind.DomainLocalGroup, name: "DL_Edge_RW"));
        snapshot.SetMembers(DlParentDn, [RawMemberDn]);

        // This glob WOULD match the raw DN string if name entries were wrongly
        // globbed against DNs ('*' crosses commas; glob matching is
        // case-insensitive). A raw DN has no name: only the MatchesDn channel
        // applies, and name entries never match there — the finding stays.
        var ruleset = DefaultsWithoutIgnore() with { Ignore = new[] { new MatchEntry { Name = "*Raw Member*" } } };

        var finding = Assert.Single(NestingFindings(RuleEngine.Evaluate(snapshot, ruleset)));
        Assert.Equal(new[] { DlParentDn, RawMemberDn }, finding.Dns);
    }

    // --- Nesting.Exceptions: endpoint narrowing, all three values ------------------------------

    [Theory]
    [InlineData(MatchEndpoint.Parent, true, true)] // Parent-narrowed, glob hits the parent => suppressed
    [InlineData(MatchEndpoint.Parent, false, false)] // glob hits ONLY the member: Parent-narrowed must NOT suppress
    [InlineData(MatchEndpoint.Member, false, true)] // Member-narrowed, glob hits the member => suppressed
    [InlineData(MatchEndpoint.Member, true, false)] // glob hits ONLY the parent: Member-narrowed must NOT suppress
    [InlineData(MatchEndpoint.Any, true, true)] // Any: either endpoint suffices ...
    [InlineData(MatchEndpoint.Any, false, true)] // ... on both sides
    public void Evaluate_NestingExceptions_HonorEndpointNarrowing(
        MatchEndpoint endpoint, bool globTargetsParent, bool expectSuppressed)
    {
        var snapshot = Snapshot(
            Obj(DlParentDn, AdObjectKind.DomainLocalGroup, name: "DL_Edge_RW"),
            Obj(MemberUserDn, AdObjectKind.User, name: "Mem User"));
        snapshot.SetMembers(DlParentDn, [MemberUserDn]); // DL<-User: deny

        var entry = new MatchEntry
        {
            Dn = globTargetsParent ? "CN=DL_Edge_RW,*" : "CN=Mem User,*",
            Endpoint = endpoint,
        };
        var baseline = DefaultsWithoutIgnore();
        var excepted = baseline with { Nesting = baseline.Nesting with { Exceptions = new[] { entry } } };

        var findings = NestingFindings(RuleEngine.Evaluate(snapshot, excepted));
        if (expectSuppressed)
        {
            Assert.Empty(findings);
        }
        else
        {
            // The narrowed entry matches the WRONG endpoint: no suppression.
            var finding = Assert.Single(findings);
            Assert.Equal(new[] { DlParentDn, MemberUserDn }, finding.Dns);
        }

        // Without the exception the deny edge always fires (both-ways discipline).
        var unexcepted = Assert.Single(NestingFindings(RuleEngine.Evaluate(snapshot, baseline)));
        Assert.Equal(new[] { DlParentDn, MemberUserDn }, unexcepted.Dns);
    }

    // --- The 'OU=Users' vs 'CN=Users' near-miss ---------------------------------------------------

    [Fact]
    public void Evaluate_OuUsersMemberDns_AreNotMatchedByTheDefaultCnUsersIgnoreGlobs()
    {
        // The load-bearing demo near-miss: demo/lab user DNs live under
        // OU=Users, one RDN type away from the default ignore globs'
        // CN=Users container. Same leaf CN as a default entry; only the
        // container RDN type differs.
        const string ouUsersMemberDn = "CN=Administrator,OU=Users,DC=weavedemo,DC=example";
        const string cnUsersMemberDn = "CN=Administrator,CN=Users,DC=weavedemo,DC=example";
        var snapshot = Snapshot(
            Obj(DlParentDn, AdObjectKind.DomainLocalGroup, name: "DL_Edge_RW"),
            Obj(ouUsersMemberDn, AdObjectKind.User, name: "Administrator"),
            Obj(cnUsersMemberDn, AdObjectKind.User, name: "Administrator"));
        snapshot.SetMembers(DlParentDn, [ouUsersMemberDn, cnUsersMemberDn]);

        // FULL default ruleset, ignore list intact: 'CN=Administrator,CN=Users,*'
        // suppresses the real default-account spelling and must NOT touch the
        // OU=Users near-miss — the demo DL<-User errors depend on exactly this.
        var finding = Assert.Single(NestingFindings(RuleEngine.Evaluate(snapshot, RulesetLoader.LoadDefault())));

        Assert.Equal(new[] { DlParentDn, ouUsersMemberDn }, finding.Dns);
        Assert.Equal(RuleSeverity.Error, finding.Severity); // DL<-User deny at default rule severity
    }

    // --- Self-edge: a normal cell lookup, never a special case ------------------------------------

    [Fact]
    public void Evaluate_SelfEdge_IsJudgedAsANormalCellLookup()
    {
        const string ggSelfDn = "CN=GG_Self,OU=Rules,DC=lab";
        const string dlSelfDn = "CN=DL_Self_RW,OU=Rules,DC=lab";
        var snapshot = Snapshot(
            Obj(ggSelfDn, AdObjectKind.GlobalGroup),
            Obj(dlSelfDn, AdObjectKind.DomainLocalGroup));
        snapshot.SetMembers(ggSelfDn, [ggSelfDn]); // GG<-GG: default cell allow => nothing
        snapshot.SetMembers(dlSelfDn, [dlSelfDn]); // DL<-DL: default cell deny => fires

        // Self-membership is NOT special-cased away (circularity belongs to the
        // separate circular rule): the GG self-edge is silent because GG<-GG is
        // an allow CELL, and the DL self-edge fires with the DN at BOTH positions.
        var finding = Assert.Single(NestingFindings(RuleEngine.Evaluate(snapshot, DefaultsWithoutIgnore())));
        Assert.Equal(new[] { dlSelfDn, dlSelfDn }, finding.Dns);
        Assert.Equal(RuleSeverity.Error, finding.Severity);
    }

    // --- Enabled gate ---------------------------------------------------------------------------------

    [Fact]
    public void Evaluate_NestingDisabled_YieldsZeroNestingFindings()
    {
        var snapshot = Snapshot(
            Obj(DlParentDn, AdObjectKind.DomainLocalGroup),
            Obj(MemberUserDn, AdObjectKind.User));
        snapshot.SetMembers(DlParentDn, [MemberUserDn]); // DL<-User: deny

        var ruleset = DefaultsWithoutIgnore();
        ruleset = ruleset with { Nesting = ruleset.Nesting with { Enabled = false } };

        Assert.Empty(NestingFindings(RuleEngine.Evaluate(snapshot, ruleset)));

        // Control: the same edge fires when enabled — the silence above comes
        // from the gate, not from a missing evaluator.
        var enabled = Assert.Single(NestingFindings(RuleEngine.Evaluate(snapshot, DefaultsWithoutIgnore())));
        Assert.Equal(new[] { DlParentDn, MemberUserDn }, enabled.Dns);
    }

    // --- Helpers ----------------------------------------------------------------------------------------

    /// <summary>The nesting block of the report, in report order. Tests filter by
    /// rule id so co-firing blocks (naming, empty-group) never leak in.</summary>
    private static RuleViolation[] NestingFindings(RuleReport report) =>
        report.Violations.Where(v => v.RuleId == RuleIds.Nesting).ToArray();

    /// <summary>The embedded default ruleset with the global ignore list cleared —
    /// suppression is opt-in per test, asserted both ways.</summary>
    private static Ruleset DefaultsWithoutIgnore() =>
        RulesetLoader.LoadDefault() with { Ignore = Array.Empty<MatchEntry>() };

    /// <summary>The defaults (no global ignore) with a hand-built nesting matrix.</summary>
    private static Ruleset WithNesting(
        IReadOnlyDictionary<AdObjectKind, IReadOnlyDictionary<AdObjectKind, NestingCell>> matrix,
        NestingCell unlisted,
        RuleSeverity severity = RuleSeverity.Error)
    {
        var baseline = DefaultsWithoutIgnore();
        return baseline with
        {
            Nesting = baseline.Nesting with { Matrix = matrix, Unlisted = unlisted, Severity = severity },
        };
    }

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
