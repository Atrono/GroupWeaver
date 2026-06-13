using GroupWeaver.Core.Model;

namespace GroupWeaver.Core.Plan;

/// <summary>
/// Pure <see cref="PlanModel"/> → <see cref="DirectorySnapshot"/> projection (ADR-014):
/// the reuse seam that lets the UNCHANGED <c>RuleEngine.Evaluate</c> and
/// <c>GraphBuilder.Build</c> consume an authored plan. Every authored object becomes an
/// <see cref="AdObject"/> with no <c>Attributes</c> (the detail-panel whitelist stays
/// minimal: Dn/Kind/Name/SAM only). <c>SetMembers</c> is called for EVERY group, even an
/// empty one — that becomes the loaded-empty <c>[]</c> arm (never <c>null</c>), so the
/// empty-group rule fires and <c>RuleReport.UncheckedDns</c> is empty by construction (a
/// plan is fully authored: nothing is unexpanded). Users are never <c>SetMembers</c>'d
/// (they are never parents), so they stay in the never-loaded (<c>null</c>) arm.
/// </summary>
public static class PlanProjection
{
    /// <summary>Projects <paramref name="plan"/> to a fresh snapshot.</summary>
    public static DirectorySnapshot ToSnapshot(PlanModel plan)
    {
        ArgumentNullException.ThrowIfNull(plan);

        var snapshot = new DirectorySnapshot();

        foreach (var node in plan.Nodes)
        {
            snapshot.AddObject(new AdObject
            {
                Dn = node.Dn,
                Kind = PlanKindMap.ToAdObjectKind(node.Kind),
                Name = node.Name,
                SamAccountName = node.SamAccountName,
            });
        }

        foreach (var node in plan.Nodes)
        {
            if (PlanKindMap.IsGroup(node.Kind))
            {
                snapshot.SetMembers(node.Dn, plan.ChildrenOf(node.Dn));
            }
        }

        return snapshot;
    }
}
