using GroupWeaver.Core.Model;

namespace GroupWeaver.Core.Audit;

/// <summary>
/// THE audit run drift diff (ADR-032 D3 / #190): compares a <c>previous</c> saved
/// <see cref="AuditRun"/> against a <c>current</c> one over their FINDINGS (not directory
/// topology — that is <see cref="Diff.SnapshotDiff"/>'s altitude), keyed by finding identity
/// <c>(RuleId, PrimaryDn)</c>. Mirrors <see cref="Diff.SnapshotDiff"/>'s discipline:
/// <see cref="Compute"/> is static, pure, total, deterministic and UI-free — it calls no
/// provider, mutates neither input, never throws on directory CONTENT, and reads only the two
/// runs' projected fields.
///
/// <para><b>Buckets</b> (every finding lands in exactly one): <see cref="New"/> (current only),
/// <see cref="StillOpen"/> (both), and the previous-only split by the honest tri-state — a
/// previous finding whose <c>PrimaryDn</c> sits UNDER a parent unchecked in <c>current</c>
/// (DN-containment over <c>current.UncheckedDns</c>) is <see cref="NowUnchecked"/>, NEVER
/// <see cref="Fixed"/>: a finding that vanished only because its area was not expanded this run
/// is not remediation. <see cref="RulesetHashMismatch"/> flags drift under a CHANGED ruleset
/// (ordinal hash inequality) so it is labelled, not blended (ADR-032 D4).</para>
///
/// <para>The result lists do NOT override <c>Equals</c> — consumers compare PROJECTIONS
/// (sorted <c>(identity, bucket)</c> pairs), never record/list identity (the determinism
/// discipline of <see cref="Diff.SnapshotDiff"/> and <see cref="Rules.RuleEngine"/>).</para>
/// </summary>
/// <param name="Fixed">In <c>previous</c>, absent in <c>current</c>, AND in a CHECKED area of
/// <c>current</c> — genuine remediation. Each is the <c>previous</c> finding.</param>
/// <param name="New">In <c>current</c>, absent in <c>previous</c> — newly surfaced. Each is the
/// <c>current</c> finding.</param>
/// <param name="StillOpen">Present in BOTH runs (same <c>(RuleId, PrimaryDn)</c> identity) — the
/// <c>current</c> finding (its message/severity may have shifted; identity is what persists).</param>
/// <param name="NowUnchecked">In <c>previous</c>, absent in <c>current</c>, but its subject sits
/// under a parent unchecked in <c>current</c> — vanished only because the area was not expanded;
/// NEVER <see cref="Fixed"/>. Each is the <c>previous</c> finding.</param>
/// <param name="RulesetHashMismatch"><c>previous.RulesetHash != current.RulesetHash</c> (ordinal):
/// the drift spans a ruleset change and must be read as such, not as pure remediation/regression.</param>
public sealed record AuditRunDiff(
    IReadOnlyList<AuditRunFinding> Fixed,
    IReadOnlyList<AuditRunFinding> New,
    IReadOnlyList<AuditRunFinding> StillOpen,
    IReadOnlyList<AuditRunFinding> NowUnchecked,
    bool RulesetHashMismatch)
{
    /// <summary>Diffs <paramref name="previous"/> against <paramref name="current"/> over their
    /// findings. Two calls on content-equal inputs yield projection-equal results (deterministic;
    /// each bucket preserves the order the findings already arrive in — canonical
    /// <see cref="Rules.RuleViolationComparer"/> order from the run that supplied them).</summary>
    public static AuditRunDiff Compute(AuditRun previous, AuditRun current)
    {
        ArgumentNullException.ThrowIfNull(previous);
        ArgumentNullException.ThrowIfNull(current);

        // Identity keys: (RuleId, PrimaryDn). Both halves are case-insensitive (rule ids are unique
        // case-insensitively, loader-enforced; DNs compare via Dn.Comparer), so the composite key
        // uses an OrdinalIgnoreCase set over a canonical NUL-joined key (see IdentityKey): a NUL char
        // can occur in neither a DN nor a rule id, so the join is unambiguous.
        var currentKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var finding in current.Findings)
        {
            currentKeys.Add(IdentityKey(finding));
        }

        var previousKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var finding in previous.Findings)
        {
            previousKeys.Add(IdentityKey(finding));
        }

        var @fixed = new List<AuditRunFinding>();
        var nowUnchecked = new List<AuditRunFinding>();

        // Previous-only findings split by the honest tri-state: a subject under a parent that is
        // unchecked in CURRENT is Now-unchecked (vanished only because unexpanded), never Fixed.
        foreach (var finding in previous.Findings)
        {
            if (currentKeys.Contains(IdentityKey(finding)))
            {
                continue; // present in both -> Still-open (emitted from current below).
            }

            if (IsUnderAnyUnchecked(finding.PrimaryDn, current.UncheckedDns))
            {
                nowUnchecked.Add(finding);
            }
            else
            {
                @fixed.Add(finding);
            }
        }

        // Current findings split into New (current-only) and Still-open (in both); each emitted as
        // the CURRENT finding so a message/severity shift on a persisting identity shows current text.
        var @new = new List<AuditRunFinding>();
        var stillOpen = new List<AuditRunFinding>();
        foreach (var finding in current.Findings)
        {
            if (previousKeys.Contains(IdentityKey(finding)))
            {
                stillOpen.Add(finding);
            }
            else
            {
                @new.Add(finding);
            }
        }

        var hashMismatch = !string.Equals(previous.RulesetHash, current.RulesetHash, StringComparison.Ordinal);

        return new AuditRunDiff(@fixed, @new, stillOpen, nowUnchecked, hashMismatch);
    }

    /// <summary>The composite bucket identity for one finding: <c>RuleId\0PrimaryDn</c>. The NUL
    /// separator can occur in neither a DN nor a rule id, so the join is unambiguous; the whole key
    /// compares OrdinalIgnoreCase (rule ids unique case-insensitively; DNs via <see cref="Dn.Comparer"/>).</summary>
    private static string IdentityKey(AuditRunFinding finding) => finding.RuleId + "\0" + finding.PrimaryDn;

    /// <summary>True iff <paramref name="dn"/> sits at or below ANY DN in
    /// <paramref name="uncheckedDns"/> — DN-containment consistent with <see cref="Dn.Comparer"/>:
    /// <c>X</c> is under <c>P</c> iff <c>X</c> equals <c>P</c> (case-insensitively) or ends with
    /// <c>"," + P</c> (case-insensitively, so a same-level prefix like <c>CN=AB,...</c> vs
    /// <c>CN=B,...</c> never matches). The parent's own finding (subject == the unchecked parent
    /// itself) is included by the equality arm.</summary>
    private static bool IsUnderAnyUnchecked(string dn, IReadOnlyList<string> uncheckedDns)
    {
        foreach (var parent in uncheckedDns)
        {
            if (string.Equals(dn, parent, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            // Descendant: dn = "...,P". The leading "," guards against matching a longer RDN value
            // that merely shares P's suffix text (e.g. "CN=XB,DC=lab" is NOT under "CN=B,DC=lab").
            if (dn.Length > parent.Length + 1
                && dn.EndsWith(parent, StringComparison.OrdinalIgnoreCase)
                && dn[dn.Length - parent.Length - 1] == ',')
            {
                return true;
            }
        }

        return false;
    }
}
