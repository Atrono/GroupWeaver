using GroupWeaver.Core.Model;
using GroupWeaver.Core.Rules;
using GroupWeaver.Tests.Providers;

using Xunit;

namespace GroupWeaver.Tests.Core.Rules;

/// <summary>
/// Pins <see cref="AuditSummary.Compute"/> (WP5 dashboard roll-up): the count
/// tiles, the weighted 0-100 score and its bands, the tri-state-honest
/// CheckedSubjects denominator, and determinism. Two layers of fixture:
/// <list type="bullet">
/// <item><b>Demo baseline</b> — the SAME full demo snapshot + embedded default
/// ruleset the AP 3.2 baseline pins (<see cref="RuleEngineDemoBaselineTests"/>),
/// reused via <see cref="DemoProviderFixture"/>. The 19-finding baseline rolls
/// up to Critical=4 / Warnings=3 / Info=12 over CheckedSubjects=40, Score=55
/// "Fair". If a number drifts, suspect the dataset or the engine, never this
/// table — the AP 3.2 baseline is authoritative.</item>
/// <item><b>Hand-built</b> — for clean/disabled/tri-state/weight/band cases, a
/// minimal <see cref="DirectorySnapshot"/> plus a directly constructed
/// <see cref="RuleReport"/> (Compute reads only report counts + snapshot
/// load-state + ruleset, never re-evaluates), so each arithmetic property is
/// pinned in isolation.</item>
/// </list>
/// The demo snapshot contains the GG_Circle_A &lt;-&gt; GG_Circle_B cycle, so its
/// Evaluate runs off-thread under a Timeout (termination proven, never trusted).
/// ByRuleClass is compared as a SORTED projection, never dictionary identity
/// (records do not override Equals over the dictionary).
/// </summary>
public class AuditSummaryTests : IClassFixture<DemoProviderFixture>
{
    private readonly DemoProviderFixture _fixture;

    public AuditSummaryTests(DemoProviderFixture fixture) => _fixture = fixture;

    // --- 1. Demo baseline -----------------------------------------------------------------

    [Fact(Timeout = 60_000)]
    public async Task Compute_FullDemoDefaultRuleset_PinsTheBaselineRollUp()
    {
        var snapshot = _fixture.FullSnapshot;
        var ruleset = RulesetLoader.LoadDefault();
        var report = await Task.Run(() => RuleEngine.Evaluate(snapshot, ruleset));

        var summary = AuditSummary.Compute(report, snapshot, ruleset);

        // Counts: the 19-finding AP 3.2 baseline split by severity
        // (3 nesting Error + 1 circular Error = 4 Critical; 3 naming Warning;
        // 12 empty-group Info).
        Assert.Equal(4, summary.Critical);
        Assert.Equal(3, summary.Warnings);
        Assert.Equal(12, summary.Info);

        // All 40 demo groups are loaded group kinds and the default ruleset has the
        // loaded-group rules enabled => CheckedSubjects = 40. 16 distinct primary DNs
        // carry a finding, so Passing = 40 - 16 = 24.
        Assert.Equal(40, summary.CheckedSubjects);
        Assert.Equal(24, summary.Passing);

        // 6 enabled rule blocks: nesting + naming-gg/dl/ug + circular + empty-group.
        Assert.Equal(6, summary.RuleClasses);

        // The two ignored builtin member DNs sit in UncheckedDns => the caveat shows.
        Assert.True(summary.UncheckedPresent);

        // penalty = 3*4 + 1*3 + 0.25*12 = 18; raw = 100 - 18/40*100 = 55.
        Assert.Equal(55, summary.Score);
        Assert.Equal("Fair", summary.Band);

        // ByRuleClass: RuleId -> finding count, compared as a sorted projection.
        // naming-ug has zero findings => absent from the map (only findings count).
        Assert.Equal(
            new[]
            {
                ("circular", 1),
                ("empty-group", 12),
                ("naming-dl", 1),
                ("naming-gg", 2),
                ("nesting", 3),
            },
            SortedByRuleClass(summary));

        // Cross-check: the by-class counts sum to the 19-finding baseline and to
        // Critical+Warnings+Info.
        Assert.Equal(19, summary.ByRuleClass.Values.Sum());
        Assert.Equal(summary.Critical + summary.Warnings + summary.Info, summary.ByRuleClass.Values.Sum());
    }

    [Fact(Timeout = 60_000)]
    public async Task Compute_FullDemoDefaultRuleset_IsDeterministic()
    {
        var snapshot = _fixture.FullSnapshot;
        var ruleset = RulesetLoader.LoadDefault();
        var first = AuditSummary.Compute(await Task.Run(() => RuleEngine.Evaluate(snapshot, ruleset)), snapshot, ruleset);
        var second = AuditSummary.Compute(await Task.Run(() => RuleEngine.Evaluate(snapshot, ruleset)), snapshot, ruleset);

        // The record's scalar fields compare by value; ByRuleClass is compared as a
        // sorted projection (the dictionary is not part of record value equality
        // in a way tests should rely on).
        Assert.Equal(first with { ByRuleClass = second.ByRuleClass }, second);
        Assert.Equal(SortedByRuleClass(first), SortedByRuleClass(second));
    }

    // --- 2. Clean report: nothing wrong, everything passing -------------------------------

    [Fact]
    public void Compute_NoFindingsWithRulesEnabled_IsAPerfectScore()
    {
        // Three loaded GG groups, default (rules-enabled) ruleset, no findings.
        var snapshot = LoadedGroups(3);
        var ruleset = RulesetLoader.LoadDefault();
        var report = new RuleReport(Array.Empty<RuleViolation>(), Array.Empty<string>());

        var summary = AuditSummary.Compute(report, snapshot, ruleset);

        Assert.Equal(0, summary.Critical);
        Assert.Equal(0, summary.Warnings);
        Assert.Equal(0, summary.Info);
        Assert.Equal(3, summary.CheckedSubjects);
        Assert.Equal(summary.CheckedSubjects, summary.Passing); // every checked subject passes
        Assert.False(summary.UncheckedPresent);
        Assert.Equal(100, summary.Score);
        Assert.Equal("Excellent", summary.Band);
        Assert.Empty(summary.ByRuleClass);
    }

    // --- 3. All rules disabled: the max(1, CheckedSubjects) guard --------------------------

    [Fact]
    public void Compute_AllRulesDisabled_NothingChecked_GuardYieldsPerfectScore()
    {
        // Loaded groups exist, but with every rule disabled NONE is in a judged
        // domain => CheckedSubjects = 0. Score = clamp(round(100 - 0/max(1,0)*100))
        // = 100: the max(1,0) guard prevents a divide-by-zero, not a real 100% pass.
        var snapshot = LoadedGroups(5);
        var ruleset = AllRulesDisabled();
        var report = new RuleReport(Array.Empty<RuleViolation>(), Array.Empty<string>());

        var summary = AuditSummary.Compute(report, snapshot, ruleset);

        Assert.Equal(0, summary.CheckedSubjects);
        Assert.Equal(0, summary.Passing);
        Assert.Equal(0, summary.RuleClasses);
        Assert.Equal(100, summary.Score);
        Assert.Equal("Excellent", summary.Band);
    }

    // --- 4. Tri-state honesty: a known-but-unloaded group is NOT checked -------------------

    [Fact]
    public void Compute_KnownButUnloadedGroupParent_IsNotCounted_AndUncheckedSurfaces()
    {
        // Two GG groups: one loaded (members set), one KNOWN but never loaded
        // (in Objects, no SetMembers => !IsLoaded). The unloaded one is the
        // tri-state's unchecked arm: it must NOT count toward CheckedSubjects and
        // therefore can never be tallied as Passing.
        const string loadedDn = "CN=GG_Loaded,OU=Groups,DC=lab";
        const string unloadedDn = "CN=GG_Unloaded,OU=Groups,DC=lab";
        var snapshot = new DirectorySnapshot();
        snapshot.AddObject(Group(loadedDn));
        snapshot.AddObject(Group(unloadedDn));
        snapshot.SetMembers(loadedDn, Array.Empty<string>()); // loaded & genuinely empty
        // unloadedDn: deliberately NOT loaded.

        // Naming rules are DISABLED so the ONLY judged domain is the loaded-group
        // rules: this isolates the tri-state. (With the default naming-gg enabled,
        // a GG is "checked" by naming regardless of load state — that path is the
        // separate leaf-kind test below; here we pin the loaded-group arm.)
        var defaults = RulesetLoader.LoadDefault();
        var ruleset = defaults with { Naming = defaults.Naming.Select(r => r with { Enabled = false }).ToArray() };
        // The unloaded group is the literal "unexpanded area is unchecked".
        var report = new RuleReport(Array.Empty<RuleViolation>(), new[] { unloadedDn });

        var summary = AuditSummary.Compute(report, snapshot, ruleset);

        // Only the loaded group counts; the unloaded one is excluded.
        Assert.Equal(1, summary.CheckedSubjects);
        Assert.Equal(1, summary.Passing); // the loaded group passes; the unloaded is NOT counted as passing
        Assert.True(summary.UncheckedPresent);
    }

    [Fact]
    public void Compute_NamingRuleTargetsLeafKind_LoadStateIrrelevant()
    {
        // Naming judges by KIND regardless of load state (a User has no members to
        // load). A User counts when an enabled naming rule targets users, even
        // though IsLoaded is never true for a leaf.
        const string userDn = "CN=u001,OU=Users,DC=lab";
        var snapshot = new DirectorySnapshot();
        snapshot.AddObject(new AdObject { Dn = userDn, Kind = AdObjectKind.User, Name = "u001" });

        var ruleset = RulesetLoader.LoadDefault() with
        {
            // Disable loaded-group rules so the ONLY judged domain is the user naming rule.
            Nesting = RulesetLoader.LoadDefault().Nesting with { Enabled = false },
            Circular = RulesetLoader.LoadDefault().Circular with { Enabled = false },
            EmptyGroup = RulesetLoader.LoadDefault().EmptyGroup with { Enabled = false },
            Naming = new[] { Naming("naming-user", AdObjectKind.User, "^u[0-9]+$") },
        };
        var report = new RuleReport(Array.Empty<RuleViolation>(), Array.Empty<string>());

        var summary = AuditSummary.Compute(report, snapshot, ruleset);

        Assert.Equal(1, summary.CheckedSubjects); // the leaf user IS checked by naming
        Assert.Equal(1, summary.RuleClasses);
    }

    // --- 5. Score monotonicity / the 3 / 1 / 0.25 weighting -------------------------------

    [Fact]
    public void Compute_AnErrorLowersScoreMoreThanAWarning_WhichLowersMoreThanAnInfo()
    {
        // CheckedSubjects = 4 so a single Info's 0.25 weight crosses a whole point
        // (at CS=100 a lone Info is 0.25pt and rounds away to 100 — not observable).
        // raw = 100 - weight/4*100: Info 0.25 -> 93.75 -> 94; Warning 1 -> 75;
        // Error 3 -> 25. Strictly monotone, and all below the clean 100.
        var snapshot = LoadedGroups(4);
        var ruleset = RulesetLoader.LoadDefault();

        var clean = ScoreOf(snapshot, ruleset, ReportWith());
        var withInfo = ScoreOf(snapshot, ruleset, ReportWith(Finding(RuleSeverity.Info)));
        var withWarning = ScoreOf(snapshot, ruleset, ReportWith(Finding(RuleSeverity.Warning)));
        var withError = ScoreOf(snapshot, ruleset, ReportWith(Finding(RuleSeverity.Error)));

        // Strictly monotone drop: clean > Info > Warning > Error.
        Assert.True(clean > withInfo, $"info should drop the score (clean={clean}, info={withInfo})");
        Assert.True(withInfo > withWarning, $"warning should drop more than info ({withInfo} vs {withWarning})");
        Assert.True(withWarning > withError, $"error should drop more than warning ({withWarning} vs {withError})");

        // Exact magnitudes at CS=4.
        Assert.Equal(100, clean);
        Assert.Equal(94, withInfo);    // 100 - 0.25/4*100 = 93.75, AwayFromZero -> 94
        Assert.Equal(75, withWarning); // 100 - 1/4*100
        Assert.Equal(25, withError);   // 100 - 3/4*100

        // The per-unit magnitudes at CS=100 (each penalty unit removes exactly that
        // many whole points): Warning 1 -> -1, Error 3 -> -3.
        var snapshot100 = LoadedGroups(100);
        Assert.Equal(99, ScoreOf(snapshot100, ruleset, ReportWith(Finding(RuleSeverity.Warning))));
        Assert.Equal(97, ScoreOf(snapshot100, ruleset, ReportWith(Finding(RuleSeverity.Error))));
    }

    [Fact]
    public void Compute_WeightsAreThreeOneQuarter_PinnedViaCommensurableCounts()
    {
        // 12 Info == 3 Warning == 1 Error in penalty weight (0.25*12 = 1*3 = 3*1 = 3).
        var snapshot = LoadedGroups(100);
        var ruleset = RulesetLoader.LoadDefault();

        var twelveInfo = ScoreOf(snapshot, ruleset, ReportWith(Repeat(RuleSeverity.Info, 12)));
        var threeWarning = ScoreOf(snapshot, ruleset, ReportWith(Repeat(RuleSeverity.Warning, 3)));
        var oneError = ScoreOf(snapshot, ruleset, ReportWith(Repeat(RuleSeverity.Error, 1)));

        // All three penalties equal 3 weight units => raw 100 - 3/100*100 = 97.
        Assert.Equal(97, twelveInfo);
        Assert.Equal(97, threeWarning);
        Assert.Equal(97, oneError);

        // And the documented constants are exactly 3 / 1 / 0.25.
        Assert.Equal(3.0, AuditSummary.ErrorWeight);
        Assert.Equal(1.0, AuditSummary.WarningWeight);
        Assert.Equal(0.25, AuditSummary.InfoWeight);
    }

    // --- 6. Band boundaries: exact thresholds ---------------------------------------------

    [Theory]
    // CheckedSubjects pinned at 100 so penalty (weight units) maps 1:1 to points removed.
    [InlineData(0, 100, "Excellent")] // perfect
    [InlineData(10, 90, "Excellent")] // 90 is the Excellent floor (>=90)
    [InlineData(11, 89, "Good")]      // 89 falls to Good
    [InlineData(25, 75, "Good")]      // 75 is the Good floor (>=75)
    [InlineData(26, 74, "Fair")]      // 74 falls to Fair
    [InlineData(50, 50, "Fair")]      // 50 is the Fair floor (>=50)
    [InlineData(51, 49, "Poor")]      // 49 falls to Poor
    [InlineData(100, 0, "Poor")]      // worst (clamped to 0)
    public void Compute_BandThresholds_ArePinnedExactly(int warnings, int expectedScore, string expectedBand)
    {
        // Warnings (weight 1.0) over CS=100: penalty == warnings, raw == 100-warnings.
        var snapshot = LoadedGroups(100);
        var ruleset = RulesetLoader.LoadDefault();
        var report = ReportWith(Repeat(RuleSeverity.Warning, warnings));

        var summary = AuditSummary.Compute(report, snapshot, ruleset);

        Assert.Equal(expectedScore, summary.Score);
        Assert.Equal(expectedBand, summary.Band);
    }

    [Fact]
    public void Compute_PenaltyBeyondMax_ScoreClampsAtZero_NeverNegative()
    {
        // Penalty far exceeding 100% of the denominator must clamp at 0, not wrap.
        var snapshot = LoadedGroups(2);
        var ruleset = RulesetLoader.LoadDefault();
        var report = ReportWith(Repeat(RuleSeverity.Error, 50)); // penalty 150 vs CS=2

        var summary = AuditSummary.Compute(report, snapshot, ruleset);

        Assert.Equal(0, summary.Score);
        Assert.Equal("Poor", summary.Band);
        Assert.True(summary.Passing >= 0); // Passing is never negative
    }

    // --- Helpers --------------------------------------------------------------------------

    private static int ScoreOf(DirectorySnapshot snapshot, Ruleset ruleset, RuleReport report) =>
        AuditSummary.Compute(report, snapshot, ruleset).Score;

    /// <summary>The ByRuleClass map as a sorted (ruleId, count) projection — the
    /// comparison contract; never dictionary identity.</summary>
    private static (string, int)[] SortedByRuleClass(AuditSummary summary) =>
        summary.ByRuleClass
            .Select(kvp => (kvp.Key, kvp.Value))
            .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    /// <summary>A snapshot of <paramref name="count"/> loaded (empty) GG groups —
    /// CheckedSubjects = count under any loaded-group-rule-enabled ruleset.</summary>
    private static DirectorySnapshot LoadedGroups(int count)
    {
        var snapshot = new DirectorySnapshot();
        for (var i = 0; i < count; i++)
        {
            var dn = $"CN=GG_{i:D4},OU=Groups,DC=lab";
            snapshot.AddObject(Group(dn));
            snapshot.SetMembers(dn, Array.Empty<string>());
        }

        return snapshot;
    }

    private static AdObject Group(string dn) => new()
    {
        Dn = dn,
        Kind = AdObjectKind.GlobalGroup,
        Name = dn,
    };

    /// <summary>A hand-built report carrying exactly <paramref name="findings"/>
    /// and an empty frontier (UncheckedPresent == false).</summary>
    private static RuleReport ReportWith(params RuleViolation[] findings) =>
        new(findings, Array.Empty<string>());

    private static RuleViolation[] Repeat(RuleSeverity severity, int n) =>
        Enumerable.Range(0, n).Select(i => Finding(severity, $"CN=Subj_{i:D4},OU=X,DC=lab")).ToArray();

    private static RuleViolation Finding(RuleSeverity severity, string dn = "CN=Subj,OU=X,DC=lab") => new()
    {
        RuleId = severity switch
        {
            RuleSeverity.Error => RuleIds.Nesting,
            RuleSeverity.Warning => "naming-gg",
            _ => RuleIds.EmptyGroup,
        },
        Severity = severity,
        Dns = new[] { dn },
        Message = "synthetic",
    };

    private static NamingRule Naming(string id, AdObjectKind kind, string pattern) => new()
    {
        Id = id,
        Enabled = true,
        Severity = RuleSeverity.Warning,
        Kind = kind,
        Pattern = pattern,
        Exceptions = Array.Empty<MatchEntry>(),
    };

    /// <summary>The default ruleset with every rule block DISABLED — RuleClasses = 0,
    /// no judged domain, CheckedSubjects = 0.</summary>
    private static Ruleset AllRulesDisabled()
    {
        var d = RulesetLoader.LoadDefault();
        return d with
        {
            Nesting = d.Nesting with { Enabled = false },
            Circular = d.Circular with { Enabled = false },
            EmptyGroup = d.EmptyGroup with { Enabled = false },
            Naming = d.Naming.Select(r => r with { Enabled = false }).ToArray(),
        };
    }
}
