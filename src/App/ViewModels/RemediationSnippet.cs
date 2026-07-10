using System.Globalization;
using System.Linq;
using System.Text;

using GroupWeaver.Core.Plan;
using GroupWeaver.Core.Rules;

namespace GroupWeaver.App.ViewModels;

/// <summary>The rule CLASS of an audit finding (WP5f / #160): the detail pane's static why/how copy
/// and the <see cref="RemediationSnippet"/> template are keyed off this, not the raw
/// <see cref="RuleViolation.RuleId"/>. The three fixed ids map one-to-one
/// (<see cref="RuleIds.Nesting"/>/<see cref="RuleIds.Circular"/>/<see cref="RuleIds.EmptyGroup"/>);
/// every OTHER id is a user-defined naming rule (kebab id), so it folds into <see cref="Naming"/>.</summary>
public enum RuleClass
{
    /// <summary>The A-G-DL-P nesting-matrix rule (<see cref="RuleIds.Nesting"/>): <c>Dns = [parent, member]</c>.</summary>
    Nesting,

    /// <summary>The circular-membership rule (<see cref="RuleIds.Circular"/>): <c>Dns =</c> the canonical cycle.</summary>
    Circular,

    /// <summary>A naming-convention rule (any non-fixed id): <c>Dns = [subject]</c>.</summary>
    Naming,

    /// <summary>The empty-group rule (<see cref="RuleIds.EmptyGroup"/>): <c>Dns = [subject]</c>.</summary>
    EmptyGroup,
}

/// <summary>
/// THE pure builder of the audit detail pane's read-only PowerShell remediation snippet (WP5f / #160,
/// ADR-028 §2): given a <see cref="RuleViolation"/> it returns a per-rule-class template filled with
/// the finding's actual DNs (the <see cref="RuleViolation.Dns"/> invariant — nesting parent=<c>Dns[0]</c>/
/// member=<c>Dns[1]</c>, naming/empty subject=<c>Dns[0]</c>, circular the cycle DNs).
///
/// <para><b>The snippet is INERT TEXT — copy-only, NEVER executed.</b> It is GUIDANCE the operator
/// reviews and runs in their OWN shell; GroupWeaver only DISPLAYS it and (on Copy) writes it to the
/// clipboard. There is no <c>Process.Start</c>, no shell launch, no AD call anywhere on this path —
/// the AD membership/rename remediation text is SOURCE STRING CONTENT, not an execution path. Every
/// snippet opens with a "Preview only — GroupWeaver runs nothing; review before running" comment, and
/// every cmdlet line is commented out (<c>#</c>) so even a careless paste-and-run is a no-op until the
/// operator deliberately un-comments it.</para>
///
/// <para>Pure + UI-free + deterministic: no provider, no AD, no mutation. DN values are inserted as
/// single-quoted PowerShell strings with embedded single quotes doubled (the PowerShell literal-string
/// escape) — defence-in-depth for snippet text that is never executed by us.</para>
/// </summary>
public static class RemediationSnippet
{
    /// <summary>The mandatory first line of every snippet — the copy-only contract, restated inside the
    /// text itself so it travels with a paste into another editor.</summary>
    public const string PreviewBanner = "# Preview only — GroupWeaver runs nothing; review before running.";

    /// <summary>Classifies <paramref name="ruleId"/> into its <see cref="RuleClass"/>: the three fixed
    /// ids map directly; anything else is a user naming rule.</summary>
    public static RuleClass ClassOf(string ruleId) => ruleId switch
    {
        RuleIds.Nesting => RuleClass.Nesting,
        RuleIds.Circular => RuleClass.Circular,
        RuleIds.EmptyGroup => RuleClass.EmptyGroup,
        _ => RuleClass.Naming,
    };

    /// <summary>Builds the read-only remediation snippet for <paramref name="violation"/>. The
    /// <paramref name="subjectName"/> (the snapshot-resolved primary-DN name, or the DN itself) is woven
    /// into the leading comment for readability; the cmdlet lines use the raw DNs. NEVER executed.</summary>
    public static string For(RuleViolation violation, string subjectName)
    {
        ArgumentNullException.ThrowIfNull(violation);

        // Defence-in-depth: the snippet is display/clipboard text we NEVER execute, but a directory-
        // sourced DN/name carrying a control char (CR/LF) embedded in a `#`-comment line could escape
        // the comment and read as an uncommented line. Strip control chars from every directory string
        // before it enters the snippet so every line stays a single, commented line (identity on real
        // RFC-4514 DNs / names — they contain no bare control chars).
        return ClassOf(violation.RuleId) switch
        {
            RuleClass.Nesting => Nesting(violation, Clean(subjectName)),
            RuleClass.Circular => Circular(violation, Clean(subjectName)),
            RuleClass.EmptyGroup => EmptyGroup(violation, Clean(subjectName)),
            _ => Naming(violation, Clean(subjectName)),
        };
    }

    // --- Per-class templates. Every cmdlet line is COMMENTED OUT (#) — guidance, not an auto-fix. -----

    private static string Nesting(RuleViolation violation, string subjectName)
    {
        // Dns invariant: [parentDn, memberDn]. The fix is to drop the disallowed direct nesting and
        // re-add the member through the correct A-G-DL-P layer.
        var parent = Quote(violation.Dns[0]);
        var member = violation.Dns.Count > 1 ? Quote(violation.Dns[1]) : Quote(violation.Dns[0]);

        var sb = NewSnippet();
        sb.AppendLine(CultureInfo.InvariantCulture, $"# Nesting violation under '{subjectName}'.");
        sb.AppendLine("# This member is nested at the wrong A-G-DL-P layer (e.g. a user/global group");
        sb.AppendLine("# directly in a domain-local group, or a domain-local group in a global group).");
        sb.AppendLine("# Step 1 — remove the disallowed direct nesting:");
        sb.AppendLine(CultureInfo.InvariantCulture, $"# Remove-ADGroupMember -Identity {parent} -Members {member} -Confirm:$false");
        sb.AppendLine("# Step 2 — re-add the member through the correct intermediate group so the");
        sb.AppendLine("#          chain is Account -> Global -> DomainLocal -> (Permission):");
        sb.AppendLine(CultureInfo.InvariantCulture, $"# Add-ADGroupMember -Identity <correct-intermediate-group> -Members {member}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"# Add-ADGroupMember -Identity {parent} -Members <correct-intermediate-group>");
        return sb.ToString().TrimEnd();
    }

    private static string Circular(RuleViolation violation, string subjectName)
    {
        // Dns invariant: the canonical cycle rotation (min-DN first; closing edge implied last->first).
        // Breaking ANY one edge on the cycle ends the loop; the simplest is the closing back-edge.
        var anchor = Quote(violation.Dns[0]);
        var last = Quote(violation.Dns[^1]);

        var sb = NewSnippet();
        sb.AppendLine(CultureInfo.InvariantCulture, $"# Circular nesting through '{subjectName}'.");
        sb.AppendLine("# Cycle (membership direction parent -> member):");
        sb.AppendLine(CultureInfo.InvariantCulture, $"#   {FormatCycle(violation.Dns)}");
        sb.AppendLine("# Break ONE edge on the cycle to end the loop. The closing back-edge");
        sb.AppendLine("# (last -> first) is usually the safest to remove:");
        sb.AppendLine(CultureInfo.InvariantCulture, $"# Remove-ADGroupMember -Identity {last} -Members {anchor} -Confirm:$false");
        return sb.ToString().TrimEnd();
    }

    private static string Naming(RuleViolation violation, string subjectName)
    {
        // Dns invariant: [subjectDn]. Renames are disruptive (SID stays, but the CN/sAMAccountName the
        // member text references changes) — show a clearly-marked example, NOT a ready-to-run command.
        var subject = Quote(violation.Dns[0]);

        var sb = NewSnippet();
        sb.AppendLine(CultureInfo.InvariantCulture, $"# Naming-convention violation on '{subjectName}'.");
        sb.AppendLine(CultureInfo.InvariantCulture, $"# Rule message: {Clean(violation.Message)}");
        sb.AppendLine("# REVIEW CAREFULLY — a rename is disruptive: downstream scripts, GPO filters and");
        sb.AppendLine("# documentation may reference the current name. Confirm the convention first, then");
        sb.AppendLine("# rename to a name that matches it (example only — substitute the real new name):");
        sb.AppendLine(CultureInfo.InvariantCulture, $"# Rename-ADObject -Identity {subject} -NewName '<New-Conforming-Name>'");
        sb.AppendLine("# If the sAMAccountName must also change, set it explicitly:");
        sb.AppendLine(CultureInfo.InvariantCulture, $"# Set-ADGroup -Identity {subject} -SamAccountName '<New-Conforming-Name>'");
        return sb.ToString().TrimEnd();
    }

    private static string EmptyGroup(RuleViolation violation, string subjectName)
    {
        // Dns invariant: [subjectDn]. Either populate the group or remove it if it is genuinely unused.
        var subject = Quote(violation.Dns[0]);

        var sb = NewSnippet();
        sb.AppendLine(CultureInfo.InvariantCulture, $"# Empty group '{subjectName}'.");
        sb.AppendLine("# Decide: does this group serve a purpose? If yes, populate it; if it is dead, remove it.");
        sb.AppendLine("# Option A — populate it with its intended member(s):");
        sb.AppendLine(CultureInfo.InvariantCulture, $"# Add-ADGroupMember -Identity {subject} -Members <member-to-add>");
        sb.AppendLine("# Option B — remove the unused group (only after confirming nothing references it):");
        sb.AppendLine(CultureInfo.InvariantCulture, $"# Remove-ADGroup -Identity {subject} -Confirm:$false");
        return sb.ToString().TrimEnd();
    }

    private static StringBuilder NewSnippet()
    {
        var sb = new StringBuilder();
        sb.AppendLine(PreviewBanner);
        sb.AppendLine();
        return sb;
    }

    /// <summary>Joins the cycle DNs into a readable <c>A -> B -> C -> A</c> chain (the closing
    /// back-edge to the anchor made explicit). Display text only.</summary>
    private static string FormatCycle(IReadOnlyList<string> dns)
    {
        if (dns.Count == 1)
        {
            return $"{Clean(dns[0])} -> {Clean(dns[0])} (self-membership)";
        }

        return string.Join(" -> ", dns.Select(Clean)) + " -> " + Clean(dns[0]);
    }

    /// <summary>Wraps a DN as a PowerShell single-quoted literal, doubling any embedded single quote
    /// (the PowerShell literal-string escape) after stripping control chars. Defence-in-depth on text
    /// that is never executed by us.</summary>
    private static string Quote(string dn) =>
        string.Create(CultureInfo.InvariantCulture, $"'{Clean(dn).Replace("'", "''", StringComparison.Ordinal)}'");

    /// <summary>Strips control characters (CR/LF/tab and other C0/C1 controls) so a directory-sourced
    /// DN, name, or rule message can never break out of its single `#`-comment line in the snippet.
    /// Identity on genuine RFC-4514 DNs and display names (which carry no bare control chars).</summary>
    private static string Clean(string text) =>
        string.IsNullOrEmpty(text) ? text : new string(text.Where(c => !PlanText.IsUnsafe(c)).ToArray());
}
