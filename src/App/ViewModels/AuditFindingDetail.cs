using GroupWeaver.Core.Rules;

namespace GroupWeaver.App.ViewModels;

/// <summary>
/// The read-only detail projection of the ONE ListBox-selected audit finding (WP5f / #160): the
/// header (rule class + severity + object name/DN), the "what's wrong" message + plain restatement,
/// the per-rule-class "why it matters" rationale + numbered "how to fix" steps, and the copy-only
/// PowerShell remediation snippet (<see cref="RemediationSnippet"/>). Built purely from a
/// <see cref="RuleViolation"/> + the snapshot-resolved subject name — NO arbitrary AD attributes
/// (whitelist discipline: only the structured finding fields + the resolved Name).
///
/// <para><b>This is the detail of the table's <c>SelectedItem</c> — NOT the triage checkboxes.</b>
/// Selecting a row for detail never changes <see cref="AuditFindingRowModel.IsSelected"/>; the two
/// selection channels are independent (ADR-028: triage is the multi-select, detail is the single
/// active row). The snippet is INERT TEXT — never executed.</para>
/// </summary>
public sealed record AuditFindingDetail
{
    private AuditFindingDetail(
        RuleClass ruleClass,
        string ruleClassLabel,
        RuleSeverity severity,
        string objectName,
        string primaryDn,
        string message,
        string plainRestatement,
        string whyItMatters,
        IReadOnlyList<string> howToFix,
        string snippet)
    {
        RuleClass = ruleClass;
        RuleClassLabel = ruleClassLabel;
        Severity = severity;
        ObjectName = objectName;
        PrimaryDn = primaryDn;
        Message = message;
        PlainRestatement = plainRestatement;
        WhyItMatters = whyItMatters;
        HowToFix = howToFix;
        Snippet = snippet;
    }

    /// <summary>The finding's rule class (drives the static copy + snippet template).</summary>
    public RuleClass RuleClass { get; }

    /// <summary>The human rule-class label for the header (e.g. "Nesting matrix", "Naming: agdlp-gg").</summary>
    public string RuleClassLabel { get; }

    /// <summary>The finding severity — drives the header glyph color/letter via the severity palette.</summary>
    public RuleSeverity Severity { get; }

    /// <summary>The anchor object's snapshot-resolved display name (DN fallback).</summary>
    public string ObjectName { get; }

    /// <summary>The anchor DN (<see cref="RuleViolation.PrimaryDn"/>), shown mono under the name.</summary>
    public string PrimaryDn { get; }

    /// <summary>The finding's canonical engine message.</summary>
    public string Message { get; }

    /// <summary>A short plain-language restatement of what is wrong (per rule class).</summary>
    public string PlainRestatement { get; }

    /// <summary>The per-rule-class "why it matters" rationale (static copy).</summary>
    public string WhyItMatters { get; }

    /// <summary>The per-rule-class numbered "how to fix" steps (static copy).</summary>
    public IReadOnlyList<string> HowToFix { get; }

    /// <summary>The read-only PowerShell remediation snippet — DISPLAY + COPY ONLY, never executed.</summary>
    public string Snippet { get; }

    /// <summary>Projects the detail for <paramref name="violation"/> with its
    /// <paramref name="objectName"/> (snapshot-resolved) and <paramref name="ruleClassLabel"/> (from
    /// <see cref="Ruleset.EnumerateRules"/>). Pure — no provider, no AD, no mutation.</summary>
    public static AuditFindingDetail From(RuleViolation violation, string objectName, string ruleClassLabel)
    {
        ArgumentNullException.ThrowIfNull(violation);

        var ruleClass = RemediationSnippet.ClassOf(violation.RuleId);
        return new AuditFindingDetail(
            ruleClass,
            ruleClassLabel,
            violation.Severity,
            objectName,
            violation.PrimaryDn,
            violation.Message,
            PlainRestatementFor(ruleClass),
            WhyItMattersFor(ruleClass),
            Number(HowToFixFor(ruleClass)),
            RemediationSnippet.For(violation, objectName));
    }

    /// <summary>Prefixes each step with its 1-based ordinal so the view can list them flat (Avalonia
    /// item templates have no index seam). Display text only.</summary>
    private static IReadOnlyList<string> Number(IReadOnlyList<string> steps)
    {
        var numbered = new string[steps.Count];
        for (var i = 0; i < steps.Count; i++)
        {
            numbered[i] = $"{i + 1}. {steps[i]}";
        }

        return numbered;
    }

    private static string PlainRestatementFor(RuleClass ruleClass) => ruleClass switch
    {
        RuleClass.Nesting => "A group membership skips or reverses the intended A-G-DL-P layering — "
            + "the member sits at the wrong level of the account → global → domain-local chain.",
        RuleClass.Circular => "Groups are nested in a loop, so a group is ultimately a member of itself "
            + "(directly or through intermediates).",
        RuleClass.Naming => "This object's name does not match the naming convention this rule enforces.",
        _ => "This group has no members — it is defined but empty.",
    };

    private static string WhyItMattersFor(RuleClass ruleClass) => ruleClass switch
    {
        RuleClass.Nesting =>
            "A-G-DL-P keeps access manageable: accounts go in Global groups (by role), Global groups go "
            + "in Domain-Local groups (by resource), and only Domain-Local groups receive permissions. "
            + "Skipping a layer scatters permission grants, makes access reviews unreliable, and breaks "
            + "the one place an admin expects to change who can do what.",
        RuleClass.Circular =>
            "A membership cycle makes the effective member set ambiguous and can confuse tools that walk "
            + "nesting (including token evaluation and reporting). It is almost always an accident and "
            + "signals that the group structure has drifted from its intended design.",
        RuleClass.Naming =>
            "Consistent names are how an operator tells at a glance what a group is for and which layer it "
            + "belongs to. An off-convention name hides the group's role, slows audits, and is a frequent "
            + "sign the group was created ad-hoc outside the intended process.",
        _ =>
            "An empty group grants nothing and clutters the directory. It is often a leftover from a "
            + "decommissioned resource or an unfinished change — and an empty Domain-Local group can mask "
            + "a permission that was meant to be delegated through it.",
    };

    private static IReadOnlyList<string> HowToFixFor(RuleClass ruleClass) => ruleClass switch
    {
        RuleClass.Nesting =>
        [
            "Confirm the correct layer for the member (account, Global, or Domain-Local).",
            "Remove the disallowed direct nesting.",
            "Re-add the member through the correct intermediate group so the chain is "
                + "Account → Global → Domain-Local.",
        ],
        RuleClass.Circular =>
        [
            "Trace the cycle shown below and decide which membership edge is the unintended one.",
            "Remove that one edge to break the loop (removing any single edge ends the cycle).",
            "Re-check the structure — the remaining nesting should form a tree, not a loop.",
        ],
        RuleClass.Naming =>
        [
            "Check the convention this rule enforces (see the message).",
            "Choose a new name that matches the convention.",
            "Rename the object — and review downstream references first, as a rename is disruptive.",
        ],
        _ =>
        [
            "Decide whether the group is still needed.",
            "If needed: add its intended member(s).",
            "If unused: remove the group after confirming nothing references it.",
        ],
    };
}
