using System.Text.RegularExpressions;

using GroupWeaver.Core.Model;
using GroupWeaver.Core.Rules;

using Xunit;

namespace GroupWeaver.Tests.Core.Rules;

/// <summary>
/// Pins the AP 3.2 S3 naming semantics of <c>RuleEngine.Evaluate</c> (ADR-009)
/// over hand-built snapshots:
/// <list type="bullet">
/// <item>The evaluated string is <c>SamAccountName ?? Name</c> — a conforming
/// sam wins over a nonconforming Name and vice versa; Name is judged only when
/// sam is null. The message names the EVALUATED string.</item>
/// <item>Patterns are case-SENSITIVE and compiled per Evaluate call with
/// <c>NonBacktracking | CultureInvariant</c> — a hand-built ruleset with a
/// NonBacktracking-unsupported pattern lets the Regex constructor exception
/// PROPAGATE (programming error, not input error); a DISABLED rule's pattern
/// is never compiled (skipped work).</item>
/// <item>Subjects are exactly the snapshot objects of <c>rule.Kind</c>; multiple
/// enabled rules on one kind judge independently (one finding each); an
/// individually disabled rule yields zero while its siblings still fire.</item>
/// <item>Suppression pipeline: global ignore then <c>rule.Exceptions</c>, both
/// via <see cref="MatchEntry.Matches"/> on the subject object, each tested BOTH
/// WAYS. The in-snapshot <c>CN=Administrators,CN=Builtin,DC=x</c> DL fails
/// naming-dl and is exempted ONLY by the <c>*,CN=Builtin,*</c> default ignore
/// glob, never by its kind (ADR-008).</item>
/// <item>Findings carry <c>Dns == [subjectDn]</c> in the object's stored
/// spelling and <c>Severity == rule.Severity</c> (flows from the rule, never
/// hard-coded).</item>
/// </list>
/// Violations are filtered by rule id / compared via projections — never via
/// RuleViolation record equality, which is reference-based over <c>Dns</c>.
/// </summary>
public class RuleEngineNamingTests
{
    private const string SubjectDn = "CN=Subject,OU=Rules,DC=lab";

    /// <summary>Single-token GG pattern (the default naming-gg demands two
    /// tokens — too strict for fallback-row fixtures).</summary>
    private const string GgPattern = "^GG_[A-Za-z0-9]+$";

    /// <summary>A backreference: valid .NET regex, rejected by the
    /// NonBacktracking engine's constructor.</summary>
    private const string NonBacktrackingUnsupportedPattern = @"^(.)\1$";

    // --- Evaluated string: SamAccountName ?? Name --------------------------------------

    [Theory]
    [InlineData("GG_Conformant", "not a match", false)] // sam judged: conforming sam wins despite nonconforming Name
    [InlineData("offending sam", "GG_Conformant", true)] // sam judged: nonconforming sam fires despite conforming Name
    [InlineData(null, "GG_Conformant", false)] // sam null: falls back to Name, conforms
    [InlineData(null, "offending name", true)] // sam null: falls back to Name, fires
    public void Evaluate_EvaluatedStringIsSamAccountName_ThenNameFallback(
        string? sam, string name, bool expectFinding)
    {
        var snapshot = Snapshot(Obj(SubjectDn, AdObjectKind.GlobalGroup, name: name, sam: sam));
        // Non-default severity pins that rule.Severity flows into the finding —
        // a hard-coded Warning would pass the default config unnoticed.
        var ruleset = WithNaming(Rule("gg-shape", AdObjectKind.GlobalGroup, GgPattern, severity: RuleSeverity.Error));

        var findings = NamingFindings(RuleEngine.Evaluate(snapshot, ruleset));

        if (!expectFinding)
        {
            Assert.Empty(findings);
            return;
        }

        var finding = Assert.Single(findings);
        Assert.Equal("gg-shape", finding.RuleId);
        Assert.Equal(RuleSeverity.Error, finding.Severity);
        Assert.Equal(new[] { SubjectDn }, finding.Dns);

        // The message names the EVALUATED string (sam when present, Name only as
        // fallback) and the offended pattern — never the un-judged other field.
        var evaluated = sam ?? name;
        Assert.Contains($"'{evaluated}'", finding.Message, StringComparison.Ordinal);
        Assert.Contains("does not match pattern", finding.Message, StringComparison.Ordinal);
        Assert.Contains($"'{GgPattern}'", finding.Message, StringComparison.Ordinal);
        if (sam is not null)
        {
            Assert.DoesNotContain($"'{name}'", finding.Message, StringComparison.Ordinal);
        }
    }

    // --- Case-sensitivity ----------------------------------------------------------------

    [Fact]
    public void Evaluate_PatternsAreCaseSensitive_LowercaseDlPrefixFails()
    {
        const string offenderDn = "CN=dl-finance-extra,OU=Rules,DC=lab";
        const string caseOnlyDn = "CN=dl_Finance_RW,OU=Rules,DC=lab";
        const string conformingDn = "CN=DL_Finance_RW,OU=Rules,DC=lab";
        var snapshot = Snapshot(
            Obj(conformingDn, AdObjectKind.DomainLocalGroup, name: "DL_Finance_RW"),
            // The demo dataset's deliberate violation: lowercase prefix, hyphens, no suffix.
            Obj(offenderDn, AdObjectKind.DomainLocalGroup, name: "dl-finance-extra"),
            // Differs from the conforming name ONLY in prefix case — fires iff
            // the engine compiles case-sensitively (glob matching is the
            // case-INsensitive layer; regexes are not globs).
            Obj(caseOnlyDn, AdObjectKind.DomainLocalGroup, name: "dl_Finance_RW"));

        var findings = NamingFindings(RuleEngine.Evaluate(snapshot, DefaultsWithoutIgnore()))
            .Where(v => v.RuleId == "naming-dl")
            .ToArray();

        // Exactly the two case offenders, element-wise OrdinalIgnoreCase order
        // ('-' < '_' ordinally), each Dns == [subjectDn]; the conforming DL is silent.
        Assert.Equal(
            new[] { offenderDn, caseOnlyDn },
            findings.Select(v => Assert.Single(v.Dns)).ToArray());
        Assert.All(findings, v => Assert.Equal(RuleSeverity.Warning, v.Severity)); // default naming-dl severity
        Assert.Contains("'dl-finance-extra'", findings[0].Message, StringComparison.Ordinal);
    }

    // --- Kind selection --------------------------------------------------------------------

    [Fact]
    public void Evaluate_OnlyObjectsOfTheRuleKind_AreJudged()
    {
        const string ggDn = "CN=Offender,OU=Rules,DC=lab";
        var snapshot = Snapshot(
            Obj(ggDn, AdObjectKind.GlobalGroup, name: "Offender"),
            // Every other kind carries the SAME failing name — none may fire:
            // subject selection is by rule.Kind, never by name shape.
            Obj("CN=DL,OU=Rules,DC=lab", AdObjectKind.DomainLocalGroup, name: "Offender"),
            Obj("CN=UG,OU=Rules,DC=lab", AdObjectKind.UniversalGroup, name: "Offender"),
            Obj("CN=User,OU=Rules,DC=lab", AdObjectKind.User, name: "Offender"),
            Obj("CN=PC,OU=Rules,DC=lab", AdObjectKind.Computer, name: "Offender"),
            Obj("OU=Unit,OU=Rules,DC=lab", AdObjectKind.OrganizationalUnit, name: "Offender"),
            Obj("CN=S-1-5-21-0-0-0-1106,CN=ForeignSecurityPrincipals,DC=lab", AdObjectKind.External, name: "Offender"));

        var findings = NamingFindings(RuleEngine.Evaluate(
            snapshot,
            WithNaming(Rule("gg-shape", AdObjectKind.GlobalGroup, GgPattern))));

        var finding = Assert.Single(findings);
        Assert.Equal(new[] { ggDn }, finding.Dns);
    }

    // --- Two enabled rules on one kind; individual disable ----------------------------------

    [Fact]
    public void Evaluate_TwoEnabledRulesOnTheSameKind_YieldTwoIndependentFindingsOnOneObject()
    {
        var snapshot = Snapshot(Obj(SubjectDn, AdObjectKind.GlobalGroup, name: "Tangle"));
        // File order ("ring-b" before "ring-a") deliberately disagrees with both
        // id sort and severity sort — the report block order is EnumerateRules()
        // file order, nothing else.
        var ruleset = WithNaming(
            Rule("ring-b", AdObjectKind.GlobalGroup, "^B_[A-Za-z0-9]+$", severity: RuleSeverity.Error),
            Rule("ring-a", AdObjectKind.GlobalGroup, "^A_[A-Za-z0-9]+$", severity: RuleSeverity.Warning));

        var findings = NamingFindings(RuleEngine.Evaluate(snapshot, ruleset));

        Assert.Equal(new[] { "ring-b", "ring-a" }, findings.Select(v => v.RuleId).ToArray());
        Assert.Equal(RuleSeverity.Error, findings[0].Severity);
        Assert.Equal(RuleSeverity.Warning, findings[1].Severity);
        Assert.All(findings, v => Assert.Equal(new[] { SubjectDn }, v.Dns));
    }

    [Fact]
    public void Evaluate_IndividuallyDisabledRule_YieldsZero_WhileTheSiblingStillFires()
    {
        var snapshot = Snapshot(Obj(SubjectDn, AdObjectKind.GlobalGroup, name: "Tangle"));
        var ruleset = WithNaming(
            Rule("ring-b", AdObjectKind.GlobalGroup, "^B_[A-Za-z0-9]+$", enabled: false),
            Rule("ring-a", AdObjectKind.GlobalGroup, "^A_[A-Za-z0-9]+$"));

        var finding = Assert.Single(NamingFindings(RuleEngine.Evaluate(snapshot, ruleset)));

        Assert.Equal("ring-a", finding.RuleId);
        Assert.Equal(new[] { SubjectDn }, finding.Dns);
    }

    // --- Suppression: global ignore and rule.Exceptions, dn and name globs, both ways -------

    [Theory]
    [InlineData(true, true)] // global ignore, dn glob
    [InlineData(true, false)] // global ignore, name glob
    [InlineData(false, true)] // rule.Exceptions, dn glob
    [InlineData(false, false)] // rule.Exceptions, name glob
    public void Evaluate_GlobalIgnoreAndRuleExceptions_Suppress_BothWays(bool viaGlobalIgnore, bool viaDnGlob)
    {
        const string subjectDn = "CN=Offender,OU=Suppressed,DC=lab";
        var snapshot = Snapshot(Obj(subjectDn, AdObjectKind.GlobalGroup, name: "Offender"));

        var entry = viaDnGlob
            ? new MatchEntry { Dn = "*,OU=Suppressed,*" }
            : new MatchEntry { Name = "Offend??" };
        var rule = Rule("gg-shape", AdObjectKind.GlobalGroup, GgPattern);
        var baseline = WithNaming(rule);
        var suppressing = viaGlobalIgnore
            ? baseline with { Ignore = new[] { entry } }
            : WithNaming(rule with { Exceptions = new[] { entry } });

        // Entry present => the subject is suppressed ...
        Assert.Empty(NamingFindings(RuleEngine.Evaluate(snapshot, suppressing)));

        // ... entry removed => exactly this finding appears (both-ways discipline).
        var finding = Assert.Single(NamingFindings(RuleEngine.Evaluate(snapshot, baseline)));
        Assert.Equal(new[] { subjectDn }, finding.Dns);
        Assert.Equal("gg-shape", finding.RuleId);
    }

    [Fact]
    public void Evaluate_InSnapshotBuiltinDl_IsSuppressedOnlyByTheBuiltinIgnoreGlob_BothWays()
    {
        // A builtin DL that made it INTO the snapshot (kind DomainLocalGroup,
        // not External) fails naming-dl — nothing about its kind excuses it
        // (ADR-008: suppression comes from the visible, deletable list).
        const string builtinDn = "CN=Administrators,CN=Builtin,DC=x";
        var snapshot = Snapshot(Obj(builtinDn, AdObjectKind.DomainLocalGroup, name: "Administrators", sam: "Administrators"));
        var defaults = RulesetLoader.LoadDefault();

        // Entry present => silent.
        Assert.Empty(NamingFindings(RuleEngine.Evaluate(snapshot, defaults)));

        // Remove ONLY the builtin glob — every other default ignore entry stays.
        // The finding appearing proves that single entry was the sole suppressor.
        var builtinRemoved = defaults with
        {
            Ignore = defaults.Ignore.Where(entry => entry.Dn != "*,CN=Builtin,*").ToArray(),
        };
        Assert.Equal(defaults.Ignore.Count - 1, builtinRemoved.Ignore.Count); // the glob existed, exactly once

        var finding = Assert.Single(NamingFindings(RuleEngine.Evaluate(snapshot, builtinRemoved)));
        Assert.Equal("naming-dl", finding.RuleId);
        Assert.Equal(RuleSeverity.Warning, finding.Severity);
        Assert.Equal(new[] { builtinDn }, finding.Dns);
    }

    // --- Pattern compilation contract ---------------------------------------------------------

    [Fact]
    public void Evaluate_NonBacktrackingUnsupportedPattern_RegexConstructorExceptionPropagates()
    {
        // The fixture pattern is valid .NET regex (compiles WITHOUT the option) and
        // rejected by the NonBacktracking constructor — so the propagated exception
        // can only come from the engine compiling NonBacktracking, per contract.
        Assert.Null(Record.Exception(() => new Regex(NonBacktrackingUnsupportedPattern)));
        var ctorException = Record.Exception(() => new Regex(
            NonBacktrackingUnsupportedPattern,
            RegexOptions.NonBacktracking | RegexOptions.CultureInvariant));
        Assert.NotNull(ctorException);

        var snapshot = Snapshot(Obj(SubjectDn, AdObjectKind.GlobalGroup, name: "GG_Subject"));
        var ruleset = WithNaming(Rule("gg-bad", AdObjectKind.GlobalGroup, NonBacktrackingUnsupportedPattern));

        // A hand-built ruleset bypassed loader validation: programming error, not
        // input error — Evaluate lets the Regex constructor exception PROPAGATE
        // unwrapped (same type as the bare construction above).
        var evaluateException = Record.Exception(() => RuleEngine.Evaluate(snapshot, ruleset));
        Assert.NotNull(evaluateException);
        Assert.IsType(ctorException.GetType(), evaluateException);
    }

    [Fact]
    public void Evaluate_DisabledRuleWithUnsupportedPattern_IsNeverCompiled_SiblingStillFires()
    {
        // Disabled => zero findings AND skipped work (ADR-009): the regex is
        // compiled per ENABLED naming rule only, so the unsupported pattern
        // must not throw while disabled.
        var snapshot = Snapshot(Obj(SubjectDn, AdObjectKind.GlobalGroup, name: "Offender"));
        var ruleset = WithNaming(
            Rule("gg-bad", AdObjectKind.GlobalGroup, NonBacktrackingUnsupportedPattern, enabled: false),
            Rule("gg-shape", AdObjectKind.GlobalGroup, GgPattern));

        var finding = Assert.Single(NamingFindings(RuleEngine.Evaluate(snapshot, ruleset)));

        Assert.Equal("gg-shape", finding.RuleId);
        Assert.Equal(new[] { SubjectDn }, finding.Dns);
    }

    // --- Helpers -------------------------------------------------------------------------------

    /// <summary>The naming blocks of the report, in report order: everything
    /// that is not one of the three fixed rule ids. Keeps co-firing blocks
    /// (empty-group etc.) from leaking in without enumerating user-chosen ids.</summary>
    private static RuleViolation[] NamingFindings(RuleReport report) =>
        report.Violations
            .Where(v => v.RuleId is not (RuleIds.Nesting or RuleIds.Circular or RuleIds.EmptyGroup))
            .ToArray();

    /// <summary>The embedded default ruleset with the global ignore list cleared —
    /// suppression is opt-in per test, asserted both ways.</summary>
    private static Ruleset DefaultsWithoutIgnore() =>
        RulesetLoader.LoadDefault() with { Ignore = Array.Empty<MatchEntry>() };

    /// <summary>A hand-built ruleset: the defaults with no global ignore and
    /// EXACTLY the given naming rules (replacing naming-gg/dl/ug entirely).</summary>
    private static Ruleset WithNaming(params NamingRule[] rules) =>
        DefaultsWithoutIgnore() with { Naming = rules };

    private static NamingRule Rule(
        string id,
        AdObjectKind kind,
        string pattern,
        bool enabled = true,
        RuleSeverity severity = RuleSeverity.Warning) => new()
        {
            Id = id,
            Enabled = enabled,
            Severity = severity,
            Kind = kind,
            Pattern = pattern,
            Exceptions = Array.Empty<MatchEntry>(),
        };

    private static DirectorySnapshot Snapshot(params AdObject[] objects)
    {
        var snapshot = new DirectorySnapshot();
        foreach (var obj in objects)
        {
            snapshot.AddObject(obj);
        }

        return snapshot;
    }

    private static AdObject Obj(string dn, AdObjectKind kind, string? name = null, string? sam = null) => new()
    {
        Dn = dn,
        Kind = kind,
        Name = name ?? dn,
        SamAccountName = sam,
    };
}
