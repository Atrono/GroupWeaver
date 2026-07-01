using GroupWeaver.App.Graph;
using GroupWeaver.App.Rules;
using GroupWeaver.App.Settings;
using GroupWeaver.App.Tests.Fakes;
using GroupWeaver.App.ViewModels;
using GroupWeaver.Core.Model;
using GroupWeaver.Core.Plan;
using GroupWeaver.Core.Providers;
using GroupWeaver.Core.Rules;
using GroupWeaver.Providers;

using Xunit;

namespace GroupWeaver.App.Tests;

/// <summary>
/// The #198 "reuse-not-reauthor, never-drift" proof: surfacing the per-rule-class
/// <em>Why it matters</em> + <em>How to fix</em> copy on the explore→drill path (the workspace
/// violations sidebar row's "Why?" flyout, and the Plan sidebar) must show the SAME static copy
/// the Audit screen shows — sourced from the ONE <see cref="AuditFindingDetail.From"/> choke
/// point, never a second hand-authored blurb that could silently drift.
///
/// <para>The load-bearing pin: for EVERY <see cref="ViolationRowModel"/> the VM projects,
/// <see cref="ViolationRowModel.WhyItMatters"/> equals <c>AuditFindingDetail.From(sameViolation,
/// resolvedName, ruleClassLabel).WhyItMatters</c> and <see cref="ViolationRowModel.HowToFix"/>
/// sequence-equals the same <c>From(...).HowToFix</c> — reproducing the VM's EXACT resolution
/// (the <c>ruleId → DisplayName</c> map from <see cref="Ruleset.EnumerateRules"/> and
/// <see cref="SubjectNameResolver.Resolve"/>). Because that copy is keyed on the finding's rule
/// class (via <see cref="RemediationSnippet.ClassOf"/>), a divergence can only come from the VM
/// re-authoring the strings instead of routing through <c>From</c> — exactly what this catches.
///
/// <para>Driven over the REAL <see cref="DemoProvider"/> 19-finding baseline (the same authoritative
/// dataset <see cref="WorkspaceViolationsTests"/> pins: 3 nesting errors, 1 circular error, 3 naming
/// warnings, 12 empty-group infos), so ALL FOUR rule classes present on the drill path
/// (nesting / circular / naming / empty-group) are covered — including the GG_Circle_A ↔ GG_Circle_B
/// cycle, so every traversal over the dataset must terminate. A temp-dir <see cref="UiStateStore"/>
/// seam (#124) keeps the workspace ctor off real <c>%APPDATA%</c>. A lighter Plan-sidebar arm proves
/// the same copy rides the Plan step's shared row template (<see cref="PlanViewModel.OnReportChanged"/>).
///
/// <para>Compares PROJECTIONS, never record identity (rule-engine.md / data-model.md).</para>
/// </summary>
public sealed class ViolationRowWhyItMattersTests
{
    private const string DemoRootDn = "OU=AGDLP-Demo,DC=weavedemo,DC=example";
    private const string PlanBaseOuDn = "OU=AGDLP-Lab,DC=agdlp,DC=lab";

    // --- (1) the workspace drill path: every row's copy IS the Audit copy, by construction -------

    [Fact(Timeout = 60_000)]
    public async Task EveryWorkspaceRow_CarriesTheSameWhyAndHowCopy_AsAuditFindingDetail_From()
    {
        using var dir = new TempDir();
        var (vm, snapshot, ruleset) = await DemoWorkspaceAsync(dir);

        // Reproduce the VM's EXACT rule-id → human-class-label resolution (OnReportChanged):
        // canonical EnumerateRules order, naming rules carry their id, unknown ids fall back raw.
        var ruleClassById = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var rule in ruleset.EnumerateRules())
        {
            ruleClassById[rule.Id] = rule.DisplayName;
        }

        // The baseline actually surfaced its findings on the drill path (not an empty sidebar).
        Assert.Equal(19, vm.Violations.Count);
        Assert.Equal(vm.Report.Violations.Count, vm.Violations.Count);

        // Rows are projected in canonical report order, so index i pairs row i with violation i
        // (the WorkspaceViolationsTests projection-order contract) — the join key for the copy check.
        for (var i = 0; i < vm.Violations.Count; i++)
        {
            var row = vm.Violations[i];
            var violation = vm.Report.Violations[i];
            Assert.Equal(violation.PrimaryDn, row.PrimaryDn, Dn.Comparer); // the rows really are paired

            // The one Audit choke point, fed the SAME inputs the VM feeds it.
            var subject = SubjectNameResolver.Resolve(snapshot, violation.PrimaryDn);
            var ruleClassLabel = ruleClassById.TryGetValue(violation.RuleId, out var label)
                ? label
                : violation.RuleId;
            var expected = AuditFindingDetail.From(violation, subject, ruleClassLabel);

            // The reuse pin: identical copy on both surfaces.
            Assert.Equal(expected.WhyItMatters, row.WhyItMatters);
            Assert.Equal(expected.HowToFix, row.HowToFix); // sequence equality over the numbered steps

            // The copy is actually populated on the drill path (not a blank/whitespace placeholder).
            Assert.False(string.IsNullOrWhiteSpace(row.WhyItMatters),
                $"row for '{row.PrimaryDn}' ({violation.RuleId}) must carry non-empty WhyItMatters");
            Assert.NotEmpty(row.HowToFix);
            Assert.All(row.HowToFix, step => Assert.False(string.IsNullOrWhiteSpace(step)));
        }

        vm.Dispose();
    }

    [Fact(Timeout = 60_000)]
    public async Task AllFourBaselineRuleClasses_AreCovered_AndCarryDistinctWhyCopy()
    {
        using var dir = new TempDir();
        var (vm, _, _) = await DemoWorkspaceAsync(dir);

        // The 19-finding baseline spans all four drill-path rule classes; pair each row with its
        // violation's class (index parity, canonical report order) to prove coverage.
        var classByRow = vm.Violations
            .Select((row, i) => (row, ruleClass: RemediationSnippet.ClassOf(vm.Report.Violations[i].RuleId)))
            .ToList();

        foreach (var expectedClass in new[]
                 { RuleClass.Nesting, RuleClass.Circular, RuleClass.Naming, RuleClass.EmptyGroup })
        {
            Assert.Contains(classByRow, pair => pair.ruleClass == expectedClass);
        }

        // The per-class copy genuinely differs by class on the sidebar rows too (not one shared blurb):
        // the four classes' WhyItMatters strings are four distinct values (parity with
        // AuditFindingDetailTests.From_PerClassCopy_IsDistinctAcrossClasses, now proven on the drill path).
        var whyByClass = classByRow
            .GroupBy(pair => pair.ruleClass)
            .ToDictionary(g => g.Key, g => g.First().row.WhyItMatters, EqualityComparer<RuleClass>.Default);
        Assert.Equal(4, whyByClass.Values.Distinct(StringComparer.Ordinal).Count());

        vm.Dispose();
    }

    // --- (2) the Plan drill path: the same copy rides the shared row template --------------------

    /// <summary>
    /// The lighter Plan-sidebar arm: a Plan self-membership (A→A) yields a real circular finding whose
    /// <see cref="ViolationRowModel"/> — built by <see cref="PlanViewModel.OnReportChanged"/> — carries
    /// the SAME <c>AuditFindingDetail.From</c> copy as the Audit screen. Proves the #198 reuse holds on
    /// the Plan step's shared row template, not only the workspace's. The self-cycle must terminate.
    /// </summary>
    [Fact(Timeout = 60_000)]
    public async Task PlanRow_CarriesTheSameWhyAndHowCopy_AsAuditFindingDetail_From()
    {
        var effective = new EffectiveRuleset(RulesetLoader.LoadDefault(), FromUserFile: false, []);
        var plan = new PlanViewModel(PlanBaseOuDn, effective);

        // Author A→A: the default ruleset reports a circular error anchored on A. The name passes the
        // default naming rule (^GG_<Token>_<Token>) so the ONLY finding on this DN is the cycle.
        plan.NewObjectKind = PlanCreatableKind.GlobalGroup;
        plan.NewObjectName = "GG_Why_Self";
        await plan.AddObjectCommand.ExecuteAsync(null);
        Assert.Null(plan.EditError);
        var groupDn = plan.Plan.FormDn("GG_Why_Self");

        plan.MemberParentRow = plan.GroupNodes.Single(r => Dn.Comparer.Equals(r.Dn, groupDn));
        plan.MemberChildRow = plan.Nodes.Single(r => Dn.Comparer.Equals(r.Dn, groupDn));
        await plan.AddMemberCommand.ExecuteAsync(null);
        Assert.True(plan.HasViolations);

        var row = Assert.Single(plan.Violations, r => Dn.Comparer.Equals(r.PrimaryDn, groupDn));
        var violation = Assert.Single(
            plan.Report.Violations, v => Dn.Comparer.Equals(v.PrimaryDn, groupDn));
        Assert.Equal(RuleClass.Circular, RemediationSnippet.ClassOf(violation.RuleId));

        // Reproduce the Plan VM's resolution: the EnumerateRules label map (same default ruleset) +
        // the snapshot-resolved subject name (WhyItMatters/HowToFix are keyed on rule class, so the
        // subject only affects other fields, but feed the faithful inputs regardless).
        var ruleClassById = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var rule in effective.Ruleset.EnumerateRules())
        {
            ruleClassById[rule.Id] = rule.DisplayName;
        }

        var subject = SubjectNameResolver.Resolve(plan.Snapshot, violation.PrimaryDn);
        var ruleClassLabel = ruleClassById.TryGetValue(violation.RuleId, out var label)
            ? label
            : violation.RuleId;
        var expected = AuditFindingDetail.From(violation, subject, ruleClassLabel);

        Assert.Equal(expected.WhyItMatters, row.WhyItMatters);
        Assert.Equal(expected.HowToFix, row.HowToFix);
        Assert.False(string.IsNullOrWhiteSpace(row.WhyItMatters));
        Assert.NotEmpty(row.HowToFix);

        plan.Dispose();
    }

    // --- helpers ---------------------------------------------------------------------------------

    /// <summary>A workspace over the REAL <see cref="DemoProvider"/> rooted at the demo OU (the full
    /// 19-finding scope), Initialization awaited, with a temp-dir <see cref="UiStateStore"/> seam (#124)
    /// so the ctor never reads real <c>%APPDATA%</c>. Returns the VM plus the loaded snapshot + the
    /// default ruleset it evaluated against (so the test can reproduce the VM's exact copy resolution).</summary>
    private static async Task<(WorkspaceViewModel Vm, DirectorySnapshot Snapshot, Ruleset Ruleset)> DemoWorkspaceAsync(
        TempDir dir)
    {
        var provider = new DemoProvider();
        var root = await provider.GetObjectAsync(DemoRootDn);
        Assert.NotNull(root);
        var ruleset = RulesetLoader.LoadDefault();
        var fake = new FakeGraphRenderer();
        var vm = new WorkspaceViewModel(
            provider, root!, await provider.ConnectAsync(),
            webView2Missing: false,
            graphRendererFactory: () => fake,
            ruleset: new EffectiveRuleset(ruleset, FromUserFile: false, []),
            exportDialogs: null,
            uiStateStore: new UiStateStore(dir.Path));
        await vm.Initialization;
        Assert.NotNull(vm.Snapshot);
        return (vm, vm.Snapshot!, ruleset);
    }

    private sealed class TempDir : IDisposable
    {
        public string Path { get; } =
            Directory.CreateTempSubdirectory("groupweaver-why-it-matters-tests-").FullName;

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch (IOException)
            {
                // Best-effort cleanup; never fail a test over temp-dir teardown.
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}
