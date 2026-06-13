using GroupWeaver.Core.Rules;

using Xunit;

namespace GroupWeaver.Tests.Core.Rules;

/// <summary>
/// Pins the AP 3.2 S1 result model (ADR-009): <c>RuleViolation</c> shape
/// (required RuleId/Severity/Dns/Message; <c>PrimaryDn == Dns[0]</c>),
/// <c>RuleReport</c> (<c>Empty</c>, Dn.Comparer-keyed per-DN indexes,
/// <c>ViolationsFor</c>/<c>ViolationsAmong</c> semantics) and the canonical
/// report-ordering comparer (EnumerateRules block order — nesting, naming in
/// file order, circular, empty-group — then element-wise OrdinalIgnoreCase
/// over Dns, shorter prefix first).
///
/// Violations here are HAND-BUILT and exercise report mechanics, not engine
/// semantics — the shapes need not be directory-consistent. All comparisons
/// of violations use instance identity or PROJECTIONS (RuleId, Severity, Dns
/// sequence, Message), NEVER RuleViolation record equality: record equality
/// is reference-based over the <c>Dns</c> list property, so two structurally
/// identical findings compare unequal.
/// </summary>
public class RuleReportTests
{
    private const string ParentDn = "CN=DL_FS-Finance_RO,OU=Groups,OU=AGDLP-Demo,DC=weavedemo,DC=example";
    private const string MemberDn = "CN=DL_Nested_RO,OU=Groups,OU=AGDLP-Demo,DC=weavedemo,DC=example";
    private const string SubjectDn = "CN=GG_X,OU=Groups,OU=AGDLP-Demo,DC=weavedemo,DC=example";
    private const string OtherDn = "CN=dl-finance-extra,OU=Groups,OU=AGDLP-Demo,DC=weavedemo,DC=example";
    private const string CycleADn = "CN=GG_Circle_A,OU=Groups,OU=AGDLP-Demo,DC=weavedemo,DC=example";
    private const string CycleBDn = "CN=GG_Circle_B,OU=Groups,OU=AGDLP-Demo,DC=weavedemo,DC=example";
    private const string UnknownDn = "CN=Nowhere,OU=Groups,OU=AGDLP-Demo,DC=weavedemo,DC=example";

    // ---- RuleViolation shape -------------------------------------------------

    [Fact]
    public void PrimaryDn_IsAlwaysTheFirstDn()
    {
        // Nesting: Dns = [parent, member] => the anchor is the parent.
        var nesting = V(RuleIds.Nesting, RuleSeverity.Error, ParentDn, MemberDn);
        Assert.Equal(ParentDn, nesting.PrimaryDn);

        // Single-subject rules: Dns = [subject] => the anchor is the subject.
        var empty = V(RuleIds.EmptyGroup, RuleSeverity.Info, SubjectDn);
        Assert.Equal(SubjectDn, empty.PrimaryDn);

        // Circular: Dns = canonical rotation => the anchor is the minimal DN.
        var circular = V(RuleIds.Circular, RuleSeverity.Error, CycleADn, CycleBDn);
        Assert.Equal(CycleADn, circular.PrimaryDn);
    }

    // ---- RuleReport.Empty ----------------------------------------------------

    [Fact]
    public void Empty_HasNoFindings_NoFrontier_NoSeverities()
    {
        var report = RuleReport.Empty;

        Assert.NotNull(report);
        Assert.Empty(report.Violations);
        Assert.Empty(report.UncheckedDns);
        Assert.Empty(report.MaxSeverityByDn);
    }

    [Fact]
    public void Empty_Lookups_ReturnEmpty_NeverThrow()
    {
        var report = RuleReport.Empty;

        Assert.Empty(report.ViolationsFor(SubjectDn));
        Assert.Empty(report.ViolationsAmong(new[] { ParentDn, MemberDn }));
        Assert.Empty(report.ViolationsAmong(Array.Empty<string>()));
    }

    // ---- MaxSeverityByDn -----------------------------------------------------

    [Fact]
    public void MaxSeverityByDn_TakesTheMaxOverAllAttachedFindings()
    {
        // MemberDn: Error (nesting member endpoint) beats its own Info
        // (the DL_Nested_RO overlap pin); SubjectDn: Warning beats Info;
        // OtherDn: Info only stays Info; ParentDn: Error only.
        var report = new RuleReport(
            new[]
            {
                V(RuleIds.Nesting, RuleSeverity.Error, ParentDn, MemberDn),
                V("naming-gg", RuleSeverity.Warning, SubjectDn),
                V(RuleIds.EmptyGroup, RuleSeverity.Info, MemberDn),
                V(RuleIds.EmptyGroup, RuleSeverity.Info, SubjectDn),
                V(RuleIds.EmptyGroup, RuleSeverity.Info, OtherDn),
            },
            Array.Empty<string>());

        Assert.Equal(RuleSeverity.Error, report.MaxSeverityByDn[ParentDn]);
        Assert.Equal(RuleSeverity.Error, report.MaxSeverityByDn[MemberDn]);
        Assert.Equal(RuleSeverity.Warning, report.MaxSeverityByDn[SubjectDn]);
        Assert.Equal(RuleSeverity.Info, report.MaxSeverityByDn[OtherDn]);

        // Every DN occurring in any Violations[i].Dns is keyed — including
        // member endpoints that never appear as a subject — and nothing else.
        Assert.Equal(4, report.MaxSeverityByDn.Count);
    }

    [Fact]
    public void MaxSeverityByDn_AggregatesCaseVariantSpellings_UnderOneKey()
    {
        // The same DN identity in two findings under different spellings must
        // aggregate under ONE Dn.Comparer key, not split per spelling.
        var report = new RuleReport(
            new[]
            {
                V(RuleIds.Nesting, RuleSeverity.Error, ParentDn, MemberDn),
                V(RuleIds.EmptyGroup, RuleSeverity.Info, MemberDn.ToUpperInvariant()),
            },
            Array.Empty<string>());

        Assert.Equal(2, report.MaxSeverityByDn.Count);

        // Lookup with a third spelling still hits (Dn.Comparer keying).
        Assert.True(report.MaxSeverityByDn.TryGetValue(MemberDn.ToLowerInvariant(), out var severity));
        Assert.Equal(RuleSeverity.Error, severity);
    }

    // ---- ViolationsFor -------------------------------------------------------

    [Fact]
    public void ViolationsFor_UnknownDn_ReturnsEmpty_NeverNull()
    {
        var report = new RuleReport(
            new[] { V(RuleIds.EmptyGroup, RuleSeverity.Info, SubjectDn) },
            Array.Empty<string>());

        var result = report.ViolationsFor(UnknownDn);

        Assert.NotNull(result);
        Assert.Empty(result);
    }

    [Fact]
    public void ViolationsFor_CaseVariantQuery_Hits()
    {
        var finding = V(RuleIds.EmptyGroup, RuleSeverity.Info, SubjectDn);
        var report = new RuleReport(new[] { finding }, Array.Empty<string>());

        var hit = Assert.Single(report.ViolationsFor(SubjectDn.ToUpperInvariant()));

        Assert.Same(finding, hit);
    }

    [Fact]
    public void ViolationsFor_ReturnsAllAttachedFindings_InReportOrder()
    {
        // MemberDn appears as nesting member, nesting parent, and empty-group
        // subject; ViolationsFor must yield the findings in report order.
        var asMember = V(RuleIds.Nesting, RuleSeverity.Error, ParentDn, MemberDn);
        var asParent = V(RuleIds.Nesting, RuleSeverity.Error, MemberDn, OtherDn);
        var asSubject = V(RuleIds.EmptyGroup, RuleSeverity.Info, MemberDn);
        var report = new RuleReport(new[] { asMember, asParent, asSubject }, Array.Empty<string>());

        Assert.Collection(
            report.ViolationsFor(MemberDn),
            v => Assert.Same(asMember, v),
            v => Assert.Same(asParent, v),
            v => Assert.Same(asSubject, v));
    }

    // ---- ViolationsAmong -----------------------------------------------------

    [Fact]
    public void ViolationsAmong_ReturnsDistinctFindings_InReportOrder()
    {
        var nesting = V(RuleIds.Nesting, RuleSeverity.Error, ParentDn, MemberDn);
        var naming = V("naming-gg", RuleSeverity.Warning, SubjectDn);
        var empty = V(RuleIds.EmptyGroup, RuleSeverity.Info, MemberDn);
        var report = new RuleReport(new[] { nesting, naming, empty }, Array.Empty<string>());

        // Query order is scrambled and contains a case variant, a duplicate,
        // and an unknown DN. The nesting finding attaches to BOTH queried
        // endpoints but must appear exactly once; the naming finding on the
        // unqueried SubjectDn must not appear; results keep report order.
        var result = report.ViolationsAmong(new[]
        {
            MemberDn.ToUpperInvariant(),
            UnknownDn,
            ParentDn,
            MemberDn,
        });

        Assert.Collection(
            result,
            v => Assert.Same(nesting, v),
            v => Assert.Same(empty, v));
    }

    [Fact]
    public void ViolationsAmong_ProjectionIdenticalFindings_AreTwoDistinctFindings()
    {
        // Distinctness is per finding INSTANCE (violations are singletons
        // within a report) — never via record equality, which is
        // reference-based over the Dns list property anyway.
        var first = V(RuleIds.EmptyGroup, RuleSeverity.Info, SubjectDn);
        var second = V(RuleIds.EmptyGroup, RuleSeverity.Info, SubjectDn);
        var report = new RuleReport(new[] { first, second }, Array.Empty<string>());

        var result = report.ViolationsAmong(new[] { SubjectDn });

        Assert.Equal(2, result.Count);
        Assert.Same(first, result[0]);
        Assert.Same(second, result[1]);
    }

    // ---- Report order --------------------------------------------------------

    [Fact]
    public void Violations_PreserveTheConstructedOrder()
    {
        // The engine hands violations already in canonical order; the report
        // never reshuffles (AP 3.3 diffs the list, the AP 3.4 sidebar binds it).
        var violations = BuildCanonicalViolations();
        var report = new RuleReport(violations, Array.Empty<string>());

        Assert.Equal(violations.Length, report.Violations.Count);
        for (var i = 0; i < violations.Length; i++)
        {
            Assert.Same(violations[i], report.Violations[i]);
        }
    }

    // ---- Ordering comparer (the canonical report order) ----------------------

    [Fact]
    public void Comparer_OrdersBlocks_NestingNamingCircularEmptyGroup()
    {
        var scrambled = new[]
        {
            V(RuleIds.EmptyGroup, RuleSeverity.Info, SubjectDn),
            V("naming-dl", RuleSeverity.Warning, OtherDn),
            V(RuleIds.Circular, RuleSeverity.Error, CycleADn, CycleBDn),
            V(RuleIds.Nesting, RuleSeverity.Error, ParentDn, MemberDn),
            V("naming-gg", RuleSeverity.Warning, SubjectDn),
        };

        var sorted = scrambled.OrderBy(v => v, DefaultOrderComparer()).ToArray();

        Assert.Equal(
            new[] { RuleIds.Nesting, "naming-gg", "naming-dl", RuleIds.Circular, RuleIds.EmptyGroup },
            sorted.Select(v => v.RuleId).ToArray());
    }

    [Fact]
    public void Comparer_NamingBlocks_FollowFileOrder_NotAlphabeticalOrder()
    {
        var comparer = new RuleViolationComparer(
            new[] { RuleIds.Nesting, "naming-zz", "naming-aa", RuleIds.Circular, RuleIds.EmptyGroup });
        var zz = V("naming-zz", RuleSeverity.Warning, SubjectDn);
        var aa = V("naming-aa", RuleSeverity.Warning, SubjectDn);

        Assert.True(comparer.Compare(zz, aa) < 0);
        Assert.True(comparer.Compare(aa, zz) > 0);
    }

    [Fact]
    public void Comparer_WithinBlock_ComparesDnsElementWise_OrdinalIgnoreCase()
    {
        var comparer = DefaultOrderComparer();

        // Element 0 ties under OrdinalIgnoreCase despite the spelling
        // difference, so element 1 decides ('MemberA' < 'MemberB'). A
        // case-SENSITIVE ordinal comparison would decide at element 0 the
        // other way around ('C' 0x43 < 'c' 0x63).
        var first = V(
            RuleIds.Nesting, RuleSeverity.Error,
            "cn=parent,ou=groups,dc=x", "CN=MemberA,OU=Groups,DC=x");
        var second = V(
            RuleIds.Nesting, RuleSeverity.Error,
            "CN=Parent,OU=Groups,DC=x", "CN=MemberB,OU=Groups,DC=x");

        Assert.True(comparer.Compare(first, second) < 0);
        Assert.True(comparer.Compare(second, first) > 0);
    }

    [Fact]
    public void Comparer_WithinBlock_FirstElementOrdersCaseInsensitively()
    {
        var comparer = DefaultOrderComparer();

        // OrdinalIgnoreCase: 'alpha' < 'Beta'; case-sensitive ordinal would
        // put "CN=Beta" first ('C' < 'c').
        var alpha = V(RuleIds.EmptyGroup, RuleSeverity.Info, "cn=alpha,ou=groups,dc=x");
        var beta = V(RuleIds.EmptyGroup, RuleSeverity.Info, "CN=Beta,OU=Groups,DC=x");

        Assert.True(comparer.Compare(alpha, beta) < 0);
        Assert.True(comparer.Compare(beta, alpha) > 0);
    }

    [Fact]
    public void Comparer_WithinBlock_ShorterDnsPrefixSortsFirst()
    {
        var comparer = DefaultOrderComparer();

        // A self-cycle [A] is an exact case-insensitive prefix of [A, B]:
        // the shorter Dns list sorts first.
        var selfCycle = V(RuleIds.Circular, RuleSeverity.Error, CycleADn);
        var pairCycle = V(RuleIds.Circular, RuleSeverity.Error, CycleADn.ToLowerInvariant(), CycleBDn);

        Assert.True(comparer.Compare(selfCycle, pairCycle) < 0);
        Assert.True(comparer.Compare(pairCycle, selfCycle) > 0);
    }

    // ---- Determinism (projection comparison, the S6 pattern) ------------------

    [Fact]
    public void IndependentlyBuiltIdenticalReports_AreProjectionIdentical()
    {
        // Determinism is asserted via PROJECTIONS (RuleId, Severity, Dns
        // sequence, Message). RuleViolation record equality is reference-based
        // over the Dns list property — two structurally identical findings
        // compare UNEQUAL — so record equality must never carry this assert.
        var first = new RuleReport(
            BuildCanonicalViolations(),
            new[] { "CN=A,DC=weavedemo,DC=example", "CN=B,DC=weavedemo,DC=example" });
        var second = new RuleReport(
            BuildCanonicalViolations(),
            new[] { "CN=A,DC=weavedemo,DC=example", "CN=B,DC=weavedemo,DC=example" });

        Assert.Equal(
            first.Violations.Select(Projection).ToArray(),
            second.Violations.Select(Projection).ToArray());
        Assert.Equal(first.UncheckedDns.ToArray(), second.UncheckedDns.ToArray());

        Assert.Equal(first.MaxSeverityByDn.Count, second.MaxSeverityByDn.Count);
        foreach (var pair in first.MaxSeverityByDn)
        {
            Assert.True(second.MaxSeverityByDn.TryGetValue(pair.Key, out var severity));
            Assert.Equal(pair.Value, severity);
        }
    }

    // ---- helpers ---------------------------------------------------------------

    private static RuleViolation V(string ruleId, RuleSeverity severity, params string[] dns) =>
        new()
        {
            RuleId = ruleId,
            Severity = severity,
            Dns = dns,
            Message = $"{ruleId}: {string.Join(" -> ", dns)}.",
        };

    /// <summary>Fresh instances per call, already in canonical report order.</summary>
    private static RuleViolation[] BuildCanonicalViolations() => new[]
    {
        V(RuleIds.Nesting, RuleSeverity.Error, ParentDn, MemberDn),
        V("naming-gg", RuleSeverity.Warning, SubjectDn),
        V(RuleIds.Circular, RuleSeverity.Error, CycleADn, CycleBDn),
        V(RuleIds.EmptyGroup, RuleSeverity.Info, OtherDn),
    };

    /// <summary>The default-ruleset block order: nesting, naming rules in file
    /// order, circular, empty-group (= Ruleset.EnumerateRules() id order).</summary>
    private static RuleViolationComparer DefaultOrderComparer() =>
        new(new[] { RuleIds.Nesting, "naming-gg", "naming-dl", RuleIds.Circular, RuleIds.EmptyGroup });

    /// <summary>THE comparison contract for violations: structured fields plus
    /// the Dns sequence — never RuleViolation record equality.</summary>
    private static (string RuleId, RuleSeverity Severity, string Dns, string Message) Projection(RuleViolation v) =>
        (v.RuleId, v.Severity, string.Join("\u001f", v.Dns), v.Message);
}
