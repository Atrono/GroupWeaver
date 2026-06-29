using System.Security.Cryptography;
using System.Text;

using GroupWeaver.Core.Model;
using GroupWeaver.Core.Rules;

namespace GroupWeaver.Core.Audit;

/// <summary>
/// A persisted point-in-time audit result (ADR-032 / #190): the findings-level evidence a
/// recurring audit compares across runs — NOT a copy of the directory. Records the
/// <see cref="AuditSummary"/> (score / band / counts), the deterministic-order
/// <see cref="Findings"/> (<see cref="RuleViolationComparer"/> order), the
/// <see cref="UncheckedDns"/> tri-state truth, and the identity needed to compare two runs
/// honestly: <see cref="RootDn"/>, <see cref="RulesetName"/>, and a stable
/// <see cref="RulesetHash"/> (so drift under a CHANGED ruleset is labelled, not blended).
///
/// <para>Pure data — no provider, no clock, no I/O. The <see cref="Timestamp"/> is INJECTED
/// by the caller (Core never reads an ambient clock); App-side <c>AuditRunStore</c> owns the
/// on-disk serialization (the hardened reader + atomic / never-throw <c>%APPDATA%</c> idiom).</para>
/// </summary>
/// <param name="SchemaVersion">The on-disk format version (currently <see cref="CurrentSchemaVersion"/>);
/// a stored run with a different version is skipped by the store's never-throw read.</param>
/// <param name="Timestamp">When the run was taken — INJECTED by the caller, never an ambient clock.</param>
/// <param name="RootDn">The scope root the audit ran over (the recent-scopes memory key, ADR-032 D5).</param>
/// <param name="ConnectionDescription">The human connection line (the same string the export header carries).</param>
/// <param name="RulesetName">The active ruleset's <see cref="Ruleset.Name"/> at run time.</param>
/// <param name="RulesetHash">A stable content hash of the active ruleset (<see cref="ComputeRulesetHash"/>):
/// a comparison whose two runs disagree here is drift under a CHANGED ruleset (ADR-032 D4) — labelled, not blended.</param>
/// <param name="Summary">The <see cref="AuditSummary"/> roll-up at run time (score / band / counts).</param>
/// <param name="Findings">The findings in canonical <see cref="RuleViolationComparer"/> order.</param>
/// <param name="UncheckedDns">The run's "unexpanded areas are unchecked" DNs (<see cref="RuleReport.UncheckedDns"/>):
/// the honest tri-state carried into drift so a finding that vanished only because its area was not expanded this
/// run is NEVER counted as Fixed (ADR-032 D3).</param>
public sealed record AuditRun(
    int SchemaVersion,
    DateTimeOffset Timestamp,
    string RootDn,
    string ConnectionDescription,
    string RulesetName,
    string RulesetHash,
    AuditSummary Summary,
    IReadOnlyList<AuditRunFinding> Findings,
    IReadOnlyList<string> UncheckedDns)
{
    /// <summary>The current on-disk run schema version (ADR-032 D2). A stored run with a
    /// different version is skipped by the store's never-throw read, never crash-loaded.</summary>
    public const int CurrentSchemaVersion = 1;

    /// <summary>The stable ruleset content-hash algorithm (ADR-032 D1): the LOWERCASE hex SHA-256 of
    /// the deterministic <see cref="RulesetSerializer.Serialize(Ruleset)"/> bytes (UTF-8, no BOM). The
    /// serializer maps onto the loader's DTO layer and is a <c>Save→Load→Save</c> fixed point, so two
    /// content-equal rulesets hash identically and any rule change flips the hash. Pure / total.</summary>
    public static string ComputeRulesetHash(Ruleset ruleset)
    {
        ArgumentNullException.ThrowIfNull(ruleset);
        var bytes = Encoding.UTF8.GetBytes(RulesetSerializer.Serialize(ruleset));
        return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    }

    /// <summary>Projects one canonical-order <see cref="RuleViolation"/> into its persisted finding form.</summary>
    public static AuditRunFinding ToFinding(RuleViolation violation)
    {
        ArgumentNullException.ThrowIfNull(violation);
        return new AuditRunFinding(
            violation.RuleId,
            violation.Severity,
            violation.PrimaryDn,
            violation.Dns.ToArray(),
            violation.Message);
    }
}

/// <summary>
/// One persisted finding inside an <see cref="AuditRun"/> (ADR-032 D1): the flat projection of a
/// <see cref="RuleViolation"/> — its identity (<see cref="RuleId"/> + <see cref="PrimaryDn"/>, the
/// <see cref="AuditRunDiff"/> bucket key), the canonical <see cref="Dns"/> endpoints, the
/// <see cref="Severity"/>, and the presentation <see cref="Message"/>. Record equality is structural,
/// but consumers and tests compare PROJECTIONS (identity tuples), never whole records — the
/// <see cref="RuleViolation"/> determinism discipline.
/// </summary>
/// <param name="RuleId">The rule id (nesting / circular / empty-group, or a naming rule id); the
/// <see cref="RuleViolation.RuleId"/>. Half of the bucket identity (OrdinalIgnoreCase).</param>
/// <param name="Severity">The effective <see cref="RuleSeverity"/> of the finding.</param>
/// <param name="PrimaryDn">The jump anchor <c>Dns[0]</c> (<see cref="RuleViolation.PrimaryDn"/>); the other
/// half of the bucket identity (<see cref="Dn.Comparer"/>).</param>
/// <param name="Dns">The full canonical endpoint list (nesting <c>[parent, member]</c>, naming /
/// empty-group <c>[subject]</c>, circular = the canonical cycle rotation).</param>
/// <param name="Message">The deterministic, culture-invariant presentation message.</param>
public sealed record AuditRunFinding(
    string RuleId,
    RuleSeverity Severity,
    string PrimaryDn,
    IReadOnlyList<string> Dns,
    string Message);
