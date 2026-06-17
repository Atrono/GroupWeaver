using System.Globalization;
using System.Text;

using GroupWeaver.Core.Model;

namespace GroupWeaver.Core.Plan;

/// <summary>
/// PURE-Core, inert-STRING generator for Plan Mode's PowerShell export (ADR-014). It
/// NEVER invokes anything — no <c>Process.Start</c>, no <c>Invoke-*</c>, no <c>pwsh</c>,
/// no runspace, no provider call. It only BUILDS the text of a <c>.ps1</c> the operator
/// runs themselves; GroupWeaver stays read-only toward AD. The directory
/// group-creation / member-add commands appear in the output ONLY as string literals
/// this builder concatenates.
///
/// Injection safety is the one existential correctness property: every untrusted token
/// (Name, DN, SAM) is emitted ONLY inside a PowerShell SINGLE-quoted literal, with an
/// embedded single quote doubled (<c>O'Brien</c> → <c>'O''Brien'</c>). A single-quoted
/// literal expands no <c>$</c>, no backtick, and no subexpression — doubling the quote
/// is the whole defense. A token carrying a character that is unsafe to embed in the
/// exported script (an ASCII/C1 control, a non-ASCII line break, or a Unicode smart-quote
/// delimiter — see <see cref="Guard"/>) is REJECTED with
/// <see cref="PlanScriptException"/>, never escaped into the output. Output is
/// deterministic and culture-invariant: objects are emitted in a stable
/// <c>Dn.Comparer</c> order and the timestamp is injected via
/// <see cref="PlanScriptHeader.GeneratedAt"/> (never the wall clock).
/// </summary>
public static class PlanScriptExporter
{
    private const string Eol = "\r\n";

    /// <summary>Builds the inert <c>.ps1</c> text for <paramref name="plan"/>.</summary>
    public static string ToPowerShell(PlanModel plan, PlanScriptHeader header)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(header);

        // Stable, authoring-order-independent emission: sort every object by DN.
        var objects = plan.Nodes.OrderBy(n => n.Dn, Dn.Comparer).ToList();
        var groups = objects.Where(n => PlanKindMap.IsGroup(n.Kind)).ToList();
        var users = objects.Where(n => n.Kind == PlanCreatableKind.User).ToList();

        // Memberships sorted by (parent DN, child DN) for byte-determinism.
        var memberships = plan.Edges
            .OrderBy(e => e.ParentDn, Dn.Comparer)
            .ThenBy(e => e.ChildDn, Dn.Comparer)
            .ToList();

        // The identity (SAM, else Name) of each object, keyed by DN — the token a
        // membership line references. Resolving here also runs every emitted token
        // through the control-char gate exactly once.
        var identity = new Dictionary<string, string>(Dn.Comparer);
        foreach (var node in objects)
        {
            identity[node.Dn] = node.SamAccountName ?? node.Name;
        }

        var sb = new StringBuilder();

        AppendHeader(sb, header, groups.Count, users.Count, memberships.Count);

        sb.Append("# --- Groups (created only if absent) ---").Append(Eol);
        foreach (var group in groups)
        {
            AppendGroupCreation(sb, group);
        }

        sb.Append(Eol);
        sb.Append("# --- Users (created only if absent; disabled, no password) ---").Append(Eol);
        foreach (var user in users)
        {
            AppendUserCreation(sb, user);
        }

        sb.Append(Eol);
        sb.Append("# --- Memberships (idempotent) ---").Append(Eol);
        foreach (var edge in memberships)
        {
            AppendMembership(sb, identity[edge.ParentDn], identity[edge.ChildDn]);
        }

        return sb.ToString();
    }

    private static void AppendHeader(
        StringBuilder sb,
        PlanScriptHeader header,
        int groupCount,
        int userCount,
        int membershipCount)
    {
        sb.Append("# GroupWeaver plan export - GENERATED, and NOT executed by GroupWeaver.")
            .Append(Eol);
        sb.Append("# GroupWeaver is a read-only product and never contacts Active Directory.")
            .Append(Eol);
        sb.Append("# This file is inert text: review every line, then run it yourself in a")
            .Append(Eol);
        sb.Append("# session that holds the rights to do so. It is idempotent - existence")
            .Append(Eol);
        sb.Append("# checks guard every create and every membership add.").Append(Eol);
        sb.Append("# Base OU   : ").Append(Ps1(header.BaseOuDn)).Append(Eol);
        sb.Append("# Generated : ")
            .Append(FormatTimestamp(header.GeneratedAt))
            .Append(" (GroupWeaver ")
            .Append(Guard(header.ToolVersion))
            .Append(')')
            .Append(Eol);
        sb.Append("# Objects   : ")
            .Append(Count(groupCount))
            .Append(" group(s), ")
            .Append(Count(userCount))
            .Append(" user(s), ")
            .Append(Count(membershipCount))
            .Append(" membership(s)")
            .Append(Eol);
        sb.Append(Eol);
        sb.Append("Set-StrictMode -Version Latest").Append(Eol);
        sb.Append("$ErrorActionPreference = 'Stop'").Append(Eol);
        sb.Append("Import-Module ActiveDirectory").Append(Eol);
        sb.Append("$BaseOU = ").Append(Ps1(header.BaseOuDn)).Append(Eol);
        sb.Append(Eol);
    }

    private static void AppendGroupCreation(StringBuilder sb, PlanObject group)
    {
        var sam = Ps1(group.SamAccountName ?? group.Name);
        var name = Ps1(group.Name);

        // Bind the token to a local first, then reference the variable in the
        // AD -Filter: the untrusted value never leaves a single-quoted literal.
        sb.Append("$sam = ").Append(sam).Append(Eol);
        sb.Append("if (-not (Get-ADGroup -Filter 'SamAccountName -eq $sam'")
            .Append(" -ErrorAction SilentlyContinue)) {")
            .Append(Eol);
        sb.Append("    New-ADGroup -Name ")
            .Append(name)
            .Append(" -SamAccountName ")
            .Append(sam)
            .Append(" -GroupScope ")
            .Append(ScopeKeyword(group.Kind))
            .Append(" -GroupCategory Security -Path $BaseOU")
            .Append(Eol);
        sb.Append('}').Append(Eol);
    }

    private static void AppendUserCreation(StringBuilder sb, PlanObject user)
    {
        var sam = Ps1(user.SamAccountName ?? user.Name);
        var name = Ps1(user.Name);

        sb.Append("$sam = ").Append(sam).Append(Eol);
        sb.Append("if (-not (Get-ADUser -Filter 'SamAccountName -eq $sam'")
            .Append(" -ErrorAction SilentlyContinue)) {")
            .Append(Eol);
        sb.Append("    New-ADUser -Name ")
            .Append(name)
            .Append(" -SamAccountName ")
            .Append(sam)
            .Append(" -Enabled $false -Path $BaseOU")
            .Append(Eol);
        sb.Append('}').Append(Eol);
    }

    private static void AppendMembership(StringBuilder sb, string parentToken, string childToken)
    {
        var parent = Ps1(parentToken);
        var child = Ps1(childToken);
        sb.Append("if (-not (Get-ADGroupMember -Identity ")
            .Append(parent)
            .Append(" | Where-Object { $_.SamAccountName -eq ")
            .Append(child)
            .Append(" })) {")
            .Append(Eol);
        sb.Append("    Add-ADGroupMember -Identity ")
            .Append(parent)
            .Append(" -Members ")
            .Append(child)
            .Append(Eol);
        sb.Append('}').Append(Eol);
    }

    /// <summary>
    /// THE choke point: wraps <paramref name="raw"/> as a PowerShell single-quoted
    /// literal with the embedded ASCII quote (U+0027) doubled, after running it through
    /// <see cref="Guard"/>. A character that is unsafe to embed in the exported script
    /// is rejected (<see cref="PlanModel.AddNode"/> rejects the same unsafe set at author
    /// time as a first line; this Guard is the complete boundary), never emitted. This is the only way an untrusted token reaches
    /// the output. The ASCII apostrophe stays the SAFE case — doubled here, never rejected.
    /// </summary>
    private static string Ps1(string raw) =>
        "'" + Guard(raw).Replace("'", "''", StringComparison.Ordinal) + "'";

    /// <summary>Rejects a token carrying a character that is unsafe to embed in the
    /// exported script — the last gate every emitted token passes through. The unsafe
    /// set lives in <see cref="PlanText.IsUnsafe(char)"/>, shared verbatim with the
    /// author-time guards in <see cref="PlanModel.AddNode"/>/<see cref="PlanModel.RenameNode"/>
    /// (#77: they no longer drift). It closes the single-quote BREAKOUT the 0.2 audit
    /// reproduced — PowerShell's tokenizer treats U+2018..U+201F as string delimiters, so
    /// a near-invisible smart quote (e.g. U+2019) would terminate the single-quoted literal
    /// early and inject code — plus all control chars (incl. U+0085 NEL / the C1 range) and
    /// U+2028/U+2029. The ASCII apostrophe U+0027 is NOT rejected; it is the safe doubled
    /// case in <see cref="Ps1"/>.</summary>
    private static string Guard(string raw)
    {
        if (PlanText.ContainsUnsafe(raw))
        {
            throw new PlanScriptException(
                "A plan token carries a character that is unsafe to embed in the exported script "
                + "and cannot be exported.");
        }

        return raw;
    }

    private static string ScopeKeyword(PlanCreatableKind kind) => kind switch
    {
        PlanCreatableKind.DomainLocalGroup => "DomainLocal",
        PlanCreatableKind.UniversalGroup => "Universal",
        _ => "Global",
    };

    private static string FormatTimestamp(DateTimeOffset value) =>
        value.ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture);

    private static string Count(int value) => value.ToString(CultureInfo.InvariantCulture);
}

/// <summary>
/// The injected, deterministic inputs of a plan export (ADR-014): the base OU, the tool
/// version, and the generation timestamp. The timestamp is injected — never the ambient
/// clock — so two exports of the same plan are byte-identical.
/// </summary>
public sealed record PlanScriptHeader(string BaseOuDn, string ToolVersion, DateTimeOffset GeneratedAt);

/// <summary>
/// Thrown by <see cref="PlanScriptExporter.ToPowerShell"/> when a token cannot be safely
/// emitted (a character that is unsafe to embed in the exported script) — the exporter
/// rejects rather than escape.
/// </summary>
public sealed class PlanScriptException : Exception
{
    /// <summary>Creates the exception with a human-readable message.</summary>
    public PlanScriptException(string message)
        : base(message)
    {
    }
}
