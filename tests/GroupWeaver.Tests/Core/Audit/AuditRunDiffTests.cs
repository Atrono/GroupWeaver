using GroupWeaver.Core.Audit;
using GroupWeaver.Core.Rules;

using Xunit;

namespace GroupWeaver.Tests.Core.Audit;

/// <summary>
/// Pins the honesty-critical drift diff of ADR-032 D3 (#190):
/// <see cref="AuditRunDiff.Compute(AuditRun, AuditRun)"/> buckets every finding of two saved
/// <see cref="AuditRun"/>s into <see cref="AuditRunDiff.Fixed"/> / <see cref="AuditRunDiff.New"/> /
/// <see cref="AuditRunDiff.StillOpen"/> / <see cref="AuditRunDiff.NowUnchecked"/> keyed by finding
/// identity <c>(RuleId, PrimaryDn)</c> (OrdinalIgnoreCase RuleId, <c>Dn.Comparer</c> PrimaryDn).
///
/// <para>Mirrors <c>SnapshotDiff</c>'s discipline: <c>Compute</c> is static, pure, total,
/// deterministic, UI-free — never a provider, never a mutation, never a throw on directory CONTENT.
/// The KEYSTONE is the tri-state: a previous finding whose subject sits UNDER a parent unchecked in
/// the CURRENT run is <see cref="AuditRunDiff.NowUnchecked"/>, NEVER <see cref="AuditRunDiff.Fixed"/>
/// (a finding that vanished only because its area was not expanded this run is not remediation).</para>
///
/// <para>EQUALITY DISCIPLINE (the rule-engine / gap-diff "compare PROJECTIONS, never whole records"):
/// every assertion compares the SORTED <c>(identity, bucket)</c> pairs (and per-bucket counts/order),
/// never record/list identity. All <see cref="AuditRun"/>s are deterministic hand-built fixtures with
/// an INJECTED fixed <see cref="AuditRun.Timestamp"/> (Core never reads an ambient clock).</para>
/// </summary>
public sealed class AuditRunDiffTests
{
    // A fixed instant — Core takes the timestamp as a value; nothing here reads a clock.
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset T1 = new(2026, 2, 1, 0, 0, 0, TimeSpan.Zero);

    private const string RootDn = "OU=Lab,DC=agdlp,DC=lab";
    private const string HashA = "aaaa";
    private const string HashB = "bbbb";

    // === 1. New / Still-open / Fixed: the three checked-area buckets ======================

    /// <summary>
    /// The core three-way split over a CHECKED current run (no unchecked DNs): a current-only
    /// finding is <see cref="AuditRunDiff.New"/>, a both-runs finding is
    /// <see cref="AuditRunDiff.StillOpen"/>, and a previous-only finding in a checked area is
    /// <see cref="AuditRunDiff.Fixed"/>. Compared as sorted <c>(identity, bucket)</c> projections.
    /// </summary>
    [Fact]
    public void Compute_CheckedRun_SplitsNewStillOpenAndFixed()
    {
        var shared = Finding(RuleIds.Nesting, "CN=DL_Shared,OU=Lab,DC=agdlp,DC=lab", RuleSeverity.Error);
        var onlyPrevious = Finding("naming-gg", "CN=GG_Renamed,OU=Lab,DC=agdlp,DC=lab", RuleSeverity.Warning);
        var onlyCurrent = Finding(RuleIds.EmptyGroup, "CN=GG_NowEmpty,OU=Lab,DC=agdlp,DC=lab", RuleSeverity.Info);

        var previous = Run(T0, HashA, uncheckedDns: [], shared, onlyPrevious);
        var current = Run(T1, HashA, uncheckedDns: [], shared, onlyCurrent);

        var diff = AuditRunDiff.Compute(previous, current);

        // Each finding lands in exactly one bucket; assert the whole partition as a projection.
        Assert.Equal(
            new[]
            {
                (Id(onlyCurrent), "New"),
                (Id(onlyPrevious), "Fixed"),
                (Id(shared), "StillOpen"),
            },
            BucketedProjection(diff));

        // Carrier discipline: Fixed/NowUnchecked carry the PREVIOUS finding, New/StillOpen the CURRENT.
        Assert.Equal(new[] { Id(onlyCurrent) }, diff.New.Select(Id).ToArray());
        Assert.Equal(new[] { Id(shared) }, diff.StillOpen.Select(Id).ToArray());
        Assert.Equal(new[] { Id(onlyPrevious) }, diff.Fixed.Select(Id).ToArray());
        Assert.Empty(diff.NowUnchecked);
        Assert.False(diff.RulesetHashMismatch);
    }

    // === 2. Now-unchecked: exact-equal parent + descendant; NEVER Fixed ==================

    /// <summary>
    /// THE honesty pin (ADR-032 D3): a previous finding absent in the current run is
    /// <see cref="AuditRunDiff.NowUnchecked"/> — NEVER <see cref="AuditRunDiff.Fixed"/> — when its
    /// subject sits under a parent unchecked in the current run, both for the EXACT-EQUAL case
    /// (PrimaryDn == the unchecked parent DN) and for a DESCENDANT case. The carrier is the PREVIOUS
    /// finding (the current run never had it).
    /// </summary>
    [Fact]
    public void Compute_PreviousFindingUnderCurrentUncheckedParent_IsNowUnchecked_NeverFixed()
    {
        const string uncheckedParent = "OU=Sales,OU=Lab,DC=agdlp,DC=lab";

        // (a) exact-equal: the finding's subject IS the unchecked parent DN.
        var onParent = Finding(RuleIds.EmptyGroup, uncheckedParent, RuleSeverity.Info);
        // (b) descendant: subject sits below the unchecked parent ("...,P").
        var descendant = Finding(RuleIds.Nesting, "CN=DL_X,OU=Sales,OU=Lab,DC=agdlp,DC=lab", RuleSeverity.Error);
        // A genuinely-fixed control: previous-only AND in a checked area (not under any unchecked DN).
        var genuinelyFixed = Finding("naming-gg", "CN=GG_Clean,OU=Eng,OU=Lab,DC=agdlp,DC=lab", RuleSeverity.Warning);

        var previous = Run(T0, HashA, uncheckedDns: [], onParent, descendant, genuinelyFixed);
        // The current run expanded LESS: OU=Sales is unchecked, so its prior findings must not be Fixed.
        var current = Run(T1, HashA, uncheckedDns: [uncheckedParent]);

        var diff = AuditRunDiff.Compute(previous, current);

        // Sorted by identity (OrdinalIgnoreCase): empty-group < naming-gg < nesting.
        Assert.Equal(
            new[]
            {
                (Id(onParent), "NowUnchecked"),       // empty-group|OU=Sales,...
                (Id(genuinelyFixed), "Fixed"),        // naming-gg|CN=GG_Clean,...
                (Id(descendant), "NowUnchecked"),     // nesting|CN=DL_X,OU=Sales,...
            },
            BucketedProjection(diff));

        // The keystone, stated directly: the two vanished-under-unexpanded findings are NEVER Fixed.
        Assert.DoesNotContain(Id(onParent), diff.Fixed.Select(Id));
        Assert.DoesNotContain(Id(descendant), diff.Fixed.Select(Id));
        Assert.Equal(new[] { Id(genuinelyFixed) }, diff.Fixed.Select(Id).ToArray());
    }

    // === 3. Boundary: a same-level RDN prefix is NOT "under" the unchecked parent =========

    /// <summary>
    /// The DN-containment boundary (ADR-032): <c>CN=XB,DC=lab</c> is NOT under <c>CN=B,DC=lab</c> —
    /// the descendant arm requires the char before the matched suffix to be a comma, so a longer RDN
    /// VALUE that merely shares the parent's suffix text does not match. The "XB" finding is therefore
    /// in a checked area => <see cref="AuditRunDiff.Fixed"/>, while the true child "CN=Y,CN=B,DC=lab"
    /// is <see cref="AuditRunDiff.NowUnchecked"/>.
    /// </summary>
    [Fact]
    public void Compute_DnContainmentBoundary_PrefixShareIsNotUnder_TrueChildIs()
    {
        const string uncheckedParent = "CN=B,DC=lab";
        var prefixShare = Finding(RuleIds.EmptyGroup, "CN=XB,DC=lab", RuleSeverity.Info);      // NOT under P
        var trueChild = Finding(RuleIds.EmptyGroup, "CN=Y,CN=B,DC=lab", RuleSeverity.Info);    // under P

        var previous = Run(T0, HashA, uncheckedDns: [], prefixShare, trueChild);
        var current = Run(T1, HashA, uncheckedDns: [uncheckedParent]);

        var diff = AuditRunDiff.Compute(previous, current);

        // The same-suffix-text prefix is in a CHECKED area -> Fixed; only the real child is NowUnchecked.
        Assert.Equal(new[] { Id(prefixShare) }, diff.Fixed.Select(Id).ToArray());
        Assert.Equal(new[] { Id(trueChild) }, diff.NowUnchecked.Select(Id).ToArray());
        Assert.Empty(diff.New);
        Assert.Empty(diff.StillOpen);
    }

    // === 4. RulesetHashMismatch both ways =================================================

    [Fact]
    public void Compute_EqualRulesetHash_MismatchIsFalse()
    {
        var f = Finding(RuleIds.Nesting, "CN=DL,OU=Lab,DC=agdlp,DC=lab", RuleSeverity.Error);
        var diff = AuditRunDiff.Compute(Run(T0, HashA, uncheckedDns: [], f), Run(T1, HashA, uncheckedDns: [], f));
        Assert.False(diff.RulesetHashMismatch);
    }

    [Fact]
    public void Compute_DifferingRulesetHash_MismatchIsTrue()
    {
        var f = Finding(RuleIds.Nesting, "CN=DL,OU=Lab,DC=agdlp,DC=lab", RuleSeverity.Error);
        var diff = AuditRunDiff.Compute(Run(T0, HashA, uncheckedDns: [], f), Run(T1, HashB, uncheckedDns: [], f));
        Assert.True(diff.RulesetHashMismatch);
    }

    // === 5. Identity keying: case-variant RuleId/PrimaryDn collapses (Still-open) =========

    /// <summary>
    /// Identity is <c>(RuleId, PrimaryDn)</c> with OrdinalIgnoreCase RuleId and <c>Dn.Comparer</c>
    /// PrimaryDn: a current finding whose RuleId AND PrimaryDn differ from the previous one ONLY in
    /// case is the SAME finding — it collapses to <see cref="AuditRunDiff.StillOpen"/>, never the
    /// New+Fixed pair a case-sensitive key would produce.
    /// </summary>
    [Fact]
    public void Compute_CaseVariantIdentity_CollapsesToStillOpen_NotNewPlusFixed()
    {
        var previous = Run(
            T0, HashA, uncheckedDns: [],
            Finding("Nesting", "CN=DL_Shared,OU=Lab,DC=agdlp,DC=lab", RuleSeverity.Error));
        var current = Run(
            T1, HashA, uncheckedDns: [],
            // Same finding, only-case-different RuleId AND PrimaryDn.
            Finding("NESTING", "cn=dl_shared,ou=lab,dc=agdlp,dc=lab", RuleSeverity.Error));

        var diff = AuditRunDiff.Compute(previous, current);

        Assert.Single(diff.StillOpen);
        Assert.Empty(diff.New);
        Assert.Empty(diff.Fixed);
        Assert.Empty(diff.NowUnchecked);
        // The carried Still-open finding is the CURRENT one (current spelling preserved).
        Assert.Equal("cn=dl_shared,ou=lab,dc=agdlp,dc=lab", diff.StillOpen[0].PrimaryDn);
    }

    // === 6. Bucket ORDER follows RuleViolationComparer (the order the runs supply) ========

    /// <summary>
    /// Each bucket preserves the order the findings already arrive in — the canonical
    /// <see cref="RuleViolationComparer"/> order from the run that supplied them. With the previous
    /// run's findings ALREADY in canonical order, the Fixed bucket (which carries the previous-run
    /// findings) is in that same order; the New bucket likewise follows the current run's order.
    /// </summary>
    [Fact]
    public void Compute_BucketsPreserveCanonicalSupplyingOrder()
    {
        // Build canonical-ordered finding lists by running the comparer over a hand mix.
        var previousFindings = Canonical(
            Finding(RuleIds.EmptyGroup, "CN=GG_Z,OU=Lab,DC=agdlp,DC=lab", RuleSeverity.Info),
            Finding(RuleIds.Nesting, "CN=DL_A,OU=Lab,DC=agdlp,DC=lab", RuleSeverity.Error),
            Finding("naming-gg", "CN=GG_M,OU=Lab,DC=agdlp,DC=lab", RuleSeverity.Warning));
        var currentFindings = Canonical(
            Finding(RuleIds.EmptyGroup, "CN=GG_Q,OU=Lab,DC=agdlp,DC=lab", RuleSeverity.Info),
            Finding(RuleIds.Nesting, "CN=DL_B,OU=Lab,DC=agdlp,DC=lab", RuleSeverity.Error));

        var previous = RunFrom(T0, HashA, [], previousFindings);
        var current = RunFrom(T1, HashA, [], currentFindings);

        var diff = AuditRunDiff.Compute(previous, current);

        // All previous findings are previous-only & checked -> Fixed, in the previous run's order.
        Assert.Equal(previousFindings.Select(Id).ToArray(), diff.Fixed.Select(Id).ToArray());
        // All current findings are current-only -> New, in the current run's order.
        Assert.Equal(currentFindings.Select(Id).ToArray(), diff.New.Select(Id).ToArray());
    }

    // === 7. Purity / determinism: inputs unmutated, two calls project equal ===============

    [Fact]
    public void Compute_IsPure_NeverMutatesInputs_AndIsDeterministic()
    {
        var shared = Finding(RuleIds.Nesting, "CN=DL_Shared,OU=Lab,DC=agdlp,DC=lab", RuleSeverity.Error);
        var prevOnly = Finding(RuleIds.EmptyGroup, "OU=Sales,OU=Lab,DC=agdlp,DC=lab", RuleSeverity.Info);
        var previous = Run(T0, HashA, uncheckedDns: [], shared, prevOnly);
        var current = Run(T1, HashB, uncheckedDns: ["OU=Sales,OU=Lab,DC=agdlp,DC=lab"], shared);

        var prevFindingsBefore = previous.Findings.Select(Id).ToArray();
        var currUncheckedBefore = current.UncheckedDns.ToArray();

        var first = AuditRunDiff.Compute(previous, current);
        var second = AuditRunDiff.Compute(previous, current);

        // Two calls on the same inputs project equal (no insertion/dictionary-order dependence).
        Assert.Equal(BucketedProjection(first), BucketedProjection(second));
        Assert.Equal(first.RulesetHashMismatch, second.RulesetHashMismatch);

        // Inputs are untouched.
        Assert.Equal(prevFindingsBefore, previous.Findings.Select(Id).ToArray());
        Assert.Equal(currUncheckedBefore, current.UncheckedDns.ToArray());
    }

    // === helpers =========================================================================

    /// <summary>The <c>(RuleId, PrimaryDn)</c> identity string for projection asserts (the bucket key).</summary>
    private static string Id(AuditRunFinding f) => $"{f.RuleId}|{f.PrimaryDn}";

    private static AuditRunFinding Finding(string ruleId, string primaryDn, RuleSeverity severity) =>
        new(ruleId, severity, primaryDn, new[] { primaryDn }, $"{ruleId} on {primaryDn}");

    /// <summary>Sorts the given findings into the canonical <see cref="RuleViolationComparer"/> order
    /// (so a bucket-order assertion uses the same ordering the engine would produce) — the comparer is
    /// seeded with the default ruleset's <see cref="Ruleset.EnumerateRules"/> block order, exactly as
    /// <see cref="RuleEngine.Evaluate"/> seeds it.</summary>
    private static AuditRunFinding[] Canonical(params AuditRunFinding[] findings)
    {
        var order = new RuleViolationComparer(RulesetLoader.LoadDefault().EnumerateRules().Select(r => r.Id));
        var violations = findings
            .Select(f => new RuleViolation
            {
                RuleId = f.RuleId,
                Severity = f.Severity,
                Dns = f.Dns,
                Message = f.Message,
            })
            .ToList();
        violations.Sort(order);
        return violations.Select(AuditRun.ToFinding).ToArray();
    }

    private static AuditRun Run(
        DateTimeOffset timestamp, string rulesetHash, string[] uncheckedDns, params AuditRunFinding[] findings) =>
        RunFrom(timestamp, rulesetHash, uncheckedDns, findings);

    private static AuditRun RunFrom(
        DateTimeOffset timestamp, string rulesetHash, IReadOnlyList<string> uncheckedDns, IReadOnlyList<AuditRunFinding> findings) =>
        new(
            AuditRun.CurrentSchemaVersion,
            timestamp,
            RootDn,
            "demo · 1 object",
            "Strict AGDLP",
            rulesetHash,
            SummaryFor(findings),
            findings,
            uncheckedDns);

    /// <summary>A minimal but consistent <see cref="AuditSummary"/> for the run (the diff reads only
    /// the findings + unchecked DNs + hash, never the summary — so any consistent value is fine).</summary>
    private static AuditSummary SummaryFor(IReadOnlyList<AuditRunFinding> findings) => new(
        Score: 100,
        Band: "Excellent",
        Critical: findings.Count(f => f.Severity == RuleSeverity.Error),
        Warnings: findings.Count(f => f.Severity == RuleSeverity.Warning),
        Info: findings.Count(f => f.Severity == RuleSeverity.Info),
        Passing: 0,
        CheckedSubjects: 0,
        RuleClasses: 0,
        UncheckedPresent: false,
        ByRuleClass: new Dictionary<string, int>());

    /// <summary>Every finding as a sorted <c>(identity, bucket)</c> projection — the whole partition,
    /// compared by VALUE, never by record/list identity (the determinism discipline).</summary>
    private static (string Identity, string Bucket)[] BucketedProjection(AuditRunDiff diff) =>
        diff.Fixed.Select(f => (Id(f), "Fixed"))
            .Concat(diff.New.Select(f => (Id(f), "New")))
            .Concat(diff.StillOpen.Select(f => (Id(f), "StillOpen")))
            .Concat(diff.NowUnchecked.Select(f => (Id(f), "NowUnchecked")))
            .OrderBy(pair => pair.Item1, StringComparer.OrdinalIgnoreCase)
            .ThenBy(pair => pair.Item2, StringComparer.Ordinal)
            .ToArray();
}
