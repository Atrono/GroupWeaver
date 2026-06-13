using GroupWeaver.Core.Model;

namespace GroupWeaver.Core.Diff;

/// <summary>
/// What KIND of gap a <see cref="GapFinding"/> reports (ADR-015 D6). The declared
/// member order IS the canonical block order of a <see cref="GapReport"/>'s findings:
/// <see cref="NodeAdded"/>, <see cref="NodeRemoved"/>, <see cref="EdgeAdded"/>,
/// <see cref="EdgeRemoved"/>, <see cref="UnverifiableArea"/>.
/// </summary>
public enum GapKind
{
    /// <summary>A node present only in the Plan projection
    /// (<see cref="DiffStatus.Added"/>): the plan introduces it.</summary>
    NodeAdded,

    /// <summary>A node present only in the live Ist snapshot
    /// (<see cref="DiffStatus.Removed"/>): the plan drops it.</summary>
    NodeRemoved,

    /// <summary>A membership edge present only in the Plan projection
    /// (<see cref="DiffStatus.Added"/>): the plan adds this <c>parent → child</c>
    /// membership.</summary>
    EdgeAdded,

    /// <summary>A membership edge present only in the live Ist snapshot
    /// (<see cref="DiffStatus.Removed"/>): the plan drops this <c>parent → child</c>
    /// membership.</summary>
    EdgeRemoved,

    /// <summary>A KNOWN-but-unloaded Ist parent that hosts a union edge
    /// (<see cref="SnapshotDiff.UncheckedParents"/>): the plan touches its membership,
    /// but the Ist side was never expanded, so the change is unverifiable — never a
    /// false <see cref="EdgeAdded"/>/<see cref="EdgeRemoved"/> (ADR-015 D5/D6).</summary>
    UnverifiableArea,
}

/// <summary>
/// One synthesized gap finding (ADR-015 D6): a pure value derived from a
/// <see cref="SnapshotDiff"/> plus the two snapshots it diffed. Shaped identically to
/// <see cref="Rules.RuleViolation"/> so the App reuses the sidebar/<c>FocusAsync</c>
/// machinery via a thin adapter, but it is a SEPARATE Core type that never pollutes the
/// pinned <c>RuleReport</c> contract nor mints <c>RuleId</c>s.
/// </summary>
/// <param name="Kind">Which gap this finding reports.</param>
/// <param name="Dns">The DNs this finding attaches to, in stored spellings (never
/// canonicalized). <c>Dns[0]</c> is the jump anchor: a single subject DN for node and
/// <see cref="GapKind.UnverifiableArea"/> findings; <c>[parentDn, childDn]</c> for edge
/// findings (the anchor frames the GROUP whose membership changed, mirroring nesting's
/// <c>[parent, member]</c> in <c>RuleViolation</c>).</param>
/// <param name="Message">Deterministic, culture-invariant, English one-liner. Presentation
/// aid only; identity lives in the structured fields (<see cref="Kind"/>/<see cref="Dns"/>).</param>
public sealed record GapFinding(GapKind Kind, IReadOnlyList<string> Dns, string Message);

/// <summary>
/// The synthesized "what changed" report (ADR-015 Slice 3, #66, D6): a flat,
/// deterministically ordered list of <see cref="GapFinding"/> over a
/// <see cref="SnapshotDiff"/>. Mirrors the record-with-static-factory idiom of its
/// <c>Diff/</c> siblings (<see cref="SnapshotDiff.Compute"/>, <c>GapSummary.From</c>).
///
/// <para><see cref="Build"/> shares <see cref="SnapshotDiff.Compute"/>'s discipline: pure,
/// deterministic, total, UI-free, never throws on directory CONTENT, calls no provider, and
/// mutates neither snapshot. It is traversal-free — it merely enumerates the diff maps — so a
/// cycle in the inputs cannot make it loop. Findings are ordered by a fixed
/// <see cref="GapKind"/> block order (the enum's declared order), then element-wise
/// <see cref="StringComparer.OrdinalIgnoreCase"/> over <see cref="GapFinding.Dns"/>
/// (shorter-prefix-first), mirroring <c>RuleViolationComparer</c> — independent of dictionary
/// or insertion order.</para>
/// </summary>
/// <param name="Findings">All gap findings in canonical block order.</param>
public sealed record GapReport(IReadOnlyList<GapFinding> Findings)
{
    /// <summary>The canonical empty report — no findings. A stable singleton (a static readonly
    /// field, not a computed property) so the App's <c>GapViewModel.Report</c> can start at it and
    /// reference-compare back to it (the "no gap computed yet" sentinel), mirroring
    /// <see cref="Rules.RuleReport.Empty"/>.</summary>
    public static readonly GapReport Empty = new([]);

    /// <summary>
    /// Synthesizes the gap report from <paramref name="diff"/> (the classification) and the two
    /// snapshots it diffed (<paramref name="ist"/> live, <paramref name="plan"/> proposed; read
    /// only for presentation name resolution). Nodes: <see cref="DiffStatus.Added"/> ⇒
    /// <see cref="GapKind.NodeAdded"/>, <see cref="DiffStatus.Removed"/> ⇒
    /// <see cref="GapKind.NodeRemoved"/>, <see cref="DiffStatus.Common"/> ⇒ nothing. Edges:
    /// <see cref="DiffStatus.Added"/> ⇒ <see cref="GapKind.EdgeAdded"/>,
    /// <see cref="DiffStatus.Removed"/> ⇒ <see cref="GapKind.EdgeRemoved"/>,
    /// <see cref="DiffStatus.Common"/> ⇒ nothing, <see cref="DiffStatus.Unchecked"/> ⇒ nothing
    /// (the parent surfaces once as a <see cref="GapKind.UnverifiableArea"/> instead).
    /// <see cref="SnapshotDiff.UncheckedParents"/> ⇒ one <see cref="GapKind.UnverifiableArea"/>
    /// each. Name resolution for <see cref="GapFinding.Message"/> uses the matching side
    /// (Added/plan-side via <paramref name="plan"/>, Removed/ist-side via <paramref name="ist"/>,
    /// unresolvable ⇒ the raw DN).
    /// </summary>
    public static GapReport Build(SnapshotDiff diff, DirectorySnapshot ist, DirectorySnapshot plan)
    {
        var findings = new List<GapFinding>();

        // Nodes: Added (plan-side name) / Removed (ist-side name); Common -> nothing.
        foreach (var (dn, status) in diff.NodeStatus)
        {
            switch (status)
            {
                case DiffStatus.Added:
                    findings.Add(new GapFinding(
                        GapKind.NodeAdded,
                        [dn],
                        $"Object '{ResolveName(plan, dn)}' exists in the plan but not in the directory."));
                    break;
                case DiffStatus.Removed:
                    findings.Add(new GapFinding(
                        GapKind.NodeRemoved,
                        [dn],
                        $"Object '{ResolveName(ist, dn)}' exists in the directory but not in the plan."));
                    break;
            }
        }

        // Edges: Added (plan-side names) / Removed (ist-side names); Common AND Unchecked ->
        // no per-edge finding (Unchecked is represented by an UnverifiableArea instead).
        foreach (var (edge, status) in diff.EdgeStatus)
        {
            switch (status)
            {
                case DiffStatus.Added:
                    findings.Add(new GapFinding(
                        GapKind.EdgeAdded,
                        [edge.ParentDn, edge.ChildDn],
                        $"Membership '{ResolveName(plan, edge.ParentDn)} ← {ResolveName(plan, edge.ChildDn)}' "
                            + "exists in the plan but not in the directory."));
                    break;
                case DiffStatus.Removed:
                    findings.Add(new GapFinding(
                        GapKind.EdgeRemoved,
                        [edge.ParentDn, edge.ChildDn],
                        $"Membership '{ResolveName(ist, edge.ParentDn)} ← {ResolveName(ist, edge.ChildDn)}' "
                            + "exists in the directory but not in the plan."));
                    break;
            }
        }

        // Unverifiable: one finding per known-but-unloaded Ist parent (already sorted by
        // SnapshotDiff). The parent IS an Ist object (the Unchecked arm is gated on
        // ist.TryGetObject) but may carry no Name; try Ist then Plan, else the raw DN.
        foreach (var parentDn in diff.UncheckedParents)
        {
            findings.Add(new GapFinding(
                GapKind.UnverifiableArea,
                [parentDn],
                $"Area '{ResolveName(ist, plan, parentDn)}' is known in the directory but was never "
                    + "expanded, so its membership cannot be compared against the plan."));
        }

        // Canonical order: GapKind block order (the enum's declared order), then element-wise
        // OrdinalIgnoreCase over Dns (shorter-prefix-first) — mirrors RuleViolationComparer,
        // independent of dictionary/insertion order. List.Sort is unstable, but ties cannot occur
        // (one finding per Dn.Comparer-distinct subject/edge/area within its block), so the result
        // is fully deterministic.
        findings.Sort(CompareFindings);
        return new GapReport(findings);
    }

    /// <summary>Block index then element-wise OrdinalIgnoreCase over <see cref="GapFinding.Dns"/>,
    /// shorter-prefix-first — the literal mirror of <c>RuleViolationComparer.Compare</c> with the
    /// <see cref="GapKind"/> declared order standing in for the rule-block order.</summary>
    private static int CompareFindings(GapFinding x, GapFinding y)
    {
        var byBlock = ((int)x.Kind).CompareTo((int)y.Kind);
        if (byBlock != 0)
        {
            return byBlock;
        }

        var shared = Math.Min(x.Dns.Count, y.Dns.Count);
        for (var i = 0; i < shared; i++)
        {
            var byElement = StringComparer.OrdinalIgnoreCase.Compare(x.Dns[i], y.Dns[i]);
            if (byElement != 0)
            {
                return byElement;
            }
        }

        return x.Dns.Count.CompareTo(y.Dns.Count);
    }

    /// <summary>The friendly <see cref="AdObject.Name"/> for <paramref name="dn"/> from
    /// <paramref name="snapshot"/>, or the raw DN when no object resolves (presentation only).</summary>
    private static string ResolveName(DirectorySnapshot snapshot, string dn) =>
        snapshot.TryGetObject(dn, out var obj) ? obj.Name : dn;

    /// <summary>Name resolution preferring <paramref name="primary"/>, then
    /// <paramref name="fallback"/>, else the raw DN — used for an UnverifiableArea parent (try Ist
    /// then Plan).</summary>
    private static string ResolveName(DirectorySnapshot primary, DirectorySnapshot fallback, string dn)
    {
        if (primary.TryGetObject(dn, out var obj))
        {
            return obj.Name;
        }

        return fallback.TryGetObject(dn, out var planObj) ? planObj.Name : dn;
    }
}
