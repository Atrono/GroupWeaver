using GroupWeaver.Core.Model;
using GroupWeaver.Core.Rules;

using Xunit;

namespace GroupWeaver.Tests.Core.Rules;

/// <summary>
/// Pins the AP 3.2 S2 empty-group semantics of <c>RuleEngine.Evaluate</c>
/// (ADR-009) over hand-built snapshots:
/// <list type="bullet">
/// <item>Subjects are snapshot OBJECTS with kind GG/DL/UG whose
/// <see cref="DirectorySnapshot.GetMembers"/> returns a NON-NULL EMPTY list.
/// <c>null</c> (never loaded) is the tri-state's unchecked arm and never a
/// finding; non-group kinds (User/Computer/OU/External) are never subjects;
/// a loaded-and-empty parent absent from <c>Objects</c> has unknown kind and
/// is never a subject.</item>
/// <item>A finding carries <c>RuleId == RuleIds.EmptyGroup</c>,
/// <c>Severity == EmptyGroup.Severity</c> (flows from the rule, never
/// hard-coded), and <c>Dns == [subjectDn]</c> in the OBJECT's stored spelling
/// (ordinal pins below).</item>
/// <item>Suppression pipeline: global ignore then <c>EmptyGroup.Exceptions</c>,
/// both via <see cref="MatchEntry.Matches"/> on the subject object — dn globs
/// AND name globs, each tested BOTH WAYS (entry present =&gt; suppressed;
/// removed =&gt; exactly that finding appears).</item>
/// <item><c>Enabled == false</c> =&gt; zero empty-group findings; an empty
/// snapshot evaluates projection-equal to <see cref="RuleReport.Empty"/>.</item>
/// </list>
/// Violations are compared via PROJECTIONS (RuleId, Severity, Dns sequence,
/// Message) or filtered by rule id — never via RuleViolation record equality,
/// which is reference-based over the <c>Dns</c> list property.
/// </summary>
public class RuleEngineEmptyGroupTests
{
    private const string GgDn = "CN=GG_Empty,OU=Rules,DC=lab";
    private const string DlDn = "CN=DL_Empty,OU=Rules,DC=lab";
    private const string UgDn = "CN=UG_Empty,OU=Rules,DC=lab";
    private const string FilledDn = "CN=GG_Filled,OU=Rules,DC=lab";
    private const string UserDn = "CN=Ada Lovelace,OU=Rules,DC=lab";

    // --- Subjects: loaded-and-empty group kinds fire ---------------------------------

    [Fact]
    public void Evaluate_LoadedAndEmptyGroups_FireForAllThreeGroupKinds_AtRuleSeverity()
    {
        // Insertion order deliberately scrambled (UG, GG, DL) — the block must
        // come back element-wise OrdinalIgnoreCase-sorted, never insertion-ordered.
        var snapshot = Snapshot(
            Obj(UgDn, AdObjectKind.UniversalGroup, name: "UG_Empty"),
            Obj(GgDn, AdObjectKind.GlobalGroup, name: "GG_Empty"),
            Obj(DlDn, AdObjectKind.DomainLocalGroup, name: "DL_Empty"),
            Obj(FilledDn, AdObjectKind.GlobalGroup, name: "GG_Filled"),
            Obj(UserDn, AdObjectKind.User, name: "Ada Lovelace"));
        snapshot.SetMembers(UgDn, []);
        snapshot.SetMembers(GgDn, []);
        snapshot.SetMembers(DlDn, []);
        snapshot.SetMembers(FilledDn, [UserDn]); // loaded WITH members: never a subject

        // Non-default severity pins that EmptyGroup.Severity flows into the
        // finding — a hard-coded Info would pass the default config unnoticed.
        var ruleset = DefaultsWithoutIgnore();
        ruleset = ruleset with { EmptyGroup = ruleset.EmptyGroup with { Severity = RuleSeverity.Warning } };

        var findings = EmptyGroupFindings(RuleEngine.Evaluate(snapshot, ruleset));

        // Exactly the three group-kind subjects, Dns == [subjectDn] each, sorted.
        Assert.Equal(
            new[] { DlDn, GgDn, UgDn },
            findings.Select(v => Assert.Single(v.Dns)).ToArray());
        Assert.All(findings, v =>
        {
            Assert.Equal(RuleIds.EmptyGroup, v.RuleId);
            Assert.Equal(RuleSeverity.Warning, v.Severity);
            Assert.Contains("has no members", v.Message, StringComparison.Ordinal);
        });

        // The subject is named by its Name in the message, never by its raw DN.
        Assert.Contains("'DL_Empty'", findings[0].Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Evaluate_MembersLoadedUnderCaseVariantParentSpelling_StillFires_WithObjectSpelling()
    {
        var snapshot = Snapshot(Obj(GgDn, AdObjectKind.GlobalGroup, name: "GG_Empty"));
        snapshot.SetMembers(GgDn.ToLowerInvariant(), []); // Dn.Comparer keying, not string equality

        var finding = Assert.Single(EmptyGroupFindings(RuleEngine.Evaluate(snapshot, DefaultsWithoutIgnore())));

        // Fires despite the case-variant load, and the finding carries the
        // OBJECT's stored spelling (ordinal equality rejects the lowercase variant).
        Assert.Equal(new[] { GgDn }, finding.Dns);
    }

    // --- Non-subjects ------------------------------------------------------------------

    [Fact]
    public void Evaluate_NeverLoadedGroups_NullMembers_NeverFire()
    {
        var snapshot = Snapshot(
            Obj(GgDn, AdObjectKind.GlobalGroup),
            Obj(DlDn, AdObjectKind.DomainLocalGroup),
            Obj(UgDn, AdObjectKind.UniversalGroup));
        // No SetMembers anywhere: GetMembers == null means "never loaded" —
        // the data-model tri-state's unchecked arm, NEVER an empty-group finding.

        Assert.Empty(EmptyGroupFindings(RuleEngine.Evaluate(snapshot, DefaultsWithoutIgnore())));
    }

    [Fact]
    public void Evaluate_LoadedAndEmptyNonGroupKinds_NeverFire()
    {
        const string computerDn = "CN=PC-001,OU=Rules,DC=lab";
        const string ouDn = "OU=Nested,OU=Rules,DC=lab";
        const string externalDn = "CN=S-1-5-21-0-0-0-1106,CN=ForeignSecurityPrincipals,DC=lab";
        var snapshot = Snapshot(
            Obj(UserDn, AdObjectKind.User),
            Obj(computerDn, AdObjectKind.Computer),
            Obj(ouDn, AdObjectKind.OrganizationalUnit),
            Obj(externalDn, AdObjectKind.External));
        snapshot.SetMembers(UserDn, []);
        snapshot.SetMembers(computerDn, []);
        snapshot.SetMembers(ouDn, []);
        snapshot.SetMembers(externalDn, []);

        // All four are loaded-and-empty, but the subject kind set is exactly
        // {GG, DL, UG} — External is fetchable yet never a rule subject (ADR-008).
        Assert.Empty(EmptyGroupFindings(RuleEngine.Evaluate(snapshot, DefaultsWithoutIgnore())));
    }

    [Fact]
    public void Evaluate_LoadedAndEmptyParentAbsentFromObjects_NeverFires()
    {
        var snapshot = new DirectorySnapshot();
        snapshot.SetMembers("CN=GG_Vanished,OU=Rules,DC=lab", []);
        // Loaded-and-empty, but absent from Objects: unknown kind, not a subject
        // (the WorkspaceViewModel vanished-parent expand arm produces this state).

        var report = RuleEngine.Evaluate(snapshot, DefaultsWithoutIgnore());

        Assert.Empty(report.Violations); // no rule has any subject or edge here
    }

    // --- Enabled gate --------------------------------------------------------------------

    [Fact]
    public void Evaluate_EmptyGroupDisabled_YieldsZeroEmptyGroupFindings()
    {
        var snapshot = Snapshot(Obj(GgDn, AdObjectKind.GlobalGroup));
        snapshot.SetMembers(GgDn, []);

        var ruleset = DefaultsWithoutIgnore();
        ruleset = ruleset with { EmptyGroup = ruleset.EmptyGroup with { Enabled = false } };

        Assert.Empty(EmptyGroupFindings(RuleEngine.Evaluate(snapshot, ruleset)));
    }

    // --- Suppression: global ignore and EmptyGroup.Exceptions, dn and name globs ----------

    [Theory]
    [InlineData(true, true)] // global ignore, dn glob
    [InlineData(true, false)] // global ignore, name glob
    [InlineData(false, true)] // EmptyGroup.Exceptions, dn glob
    [InlineData(false, false)] // EmptyGroup.Exceptions, name glob
    public void Evaluate_DnAndNameGlobs_SuppressViaIgnoreAndExceptions_BothWays(
        bool viaGlobalIgnore, bool viaDnGlob)
    {
        const string subjectDn = "CN=GG_Empty,OU=Suppressed,DC=lab";
        var snapshot = Snapshot(Obj(subjectDn, AdObjectKind.GlobalGroup, name: "GG_Empty"));
        snapshot.SetMembers(subjectDn, []);

        // The name glob is anchored and matches the Name "GG_Empty" only — it
        // cannot accidentally swallow the DN (name entries never match raw DNs).
        var entry = viaDnGlob
            ? new MatchEntry { Dn = "*,OU=Suppressed,*" }
            : new MatchEntry { Name = "GG_Empt?" };
        var baseline = DefaultsWithoutIgnore();
        var suppressing = viaGlobalIgnore
            ? baseline with { Ignore = new[] { entry } }
            : baseline with { EmptyGroup = baseline.EmptyGroup with { Exceptions = new[] { entry } } };

        // Entry present => the subject is suppressed ...
        Assert.Empty(EmptyGroupFindings(RuleEngine.Evaluate(snapshot, suppressing)));

        // ... entry removed => exactly this finding appears (both-ways discipline).
        var finding = Assert.Single(EmptyGroupFindings(RuleEngine.Evaluate(snapshot, baseline)));
        Assert.Equal(new[] { subjectDn }, finding.Dns);
        Assert.Equal(RuleIds.EmptyGroup, finding.RuleId);
    }

    // --- Empty snapshot --------------------------------------------------------------------

    [Fact]
    public void Evaluate_EmptySnapshot_IsProjectionEqualToRuleReportEmpty()
    {
        var report = RuleEngine.Evaluate(new DirectorySnapshot(), RulesetLoader.LoadDefault());

        // Projection comparison, never RuleViolation record equality (the Dns
        // list property makes record equality reference-based).
        Assert.Equal(
            RuleReport.Empty.Violations.Select(Projection).ToArray(),
            report.Violations.Select(Projection).ToArray());
        Assert.Equal(RuleReport.Empty.UncheckedDns.ToArray(), report.UncheckedDns.ToArray());
        Assert.Empty(report.MaxSeverityByDn);
    }

    // --- Helpers -----------------------------------------------------------------------------

    /// <summary>The empty-group block of the report, in report order. Tests filter
    /// by rule id so co-firing rules from other blocks (naming etc.) never leak in.</summary>
    private static RuleViolation[] EmptyGroupFindings(RuleReport report) =>
        report.Violations.Where(v => v.RuleId == RuleIds.EmptyGroup).ToArray();

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
