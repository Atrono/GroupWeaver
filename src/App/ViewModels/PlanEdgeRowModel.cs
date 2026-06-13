namespace GroupWeaver.App.ViewModels;

/// <summary>
/// One authored-membership row of the Plan Mode editor (AP 4.2.3 / ADR-014): the immutable
/// projection of a <c>MembershipEdge</c> the Memberships list binds, with both endpoints'
/// DNs and resolved display names captured at refresh time. <see cref="Display"/> reads
/// "parent ← child" (the parent lists the child) — the same direction as the graph legend's
/// "is member of" (the child is a member of the parent). The per-row Remove binds this whole
/// row as the command parameter (the VM reads <see cref="ParentDn"/>/<see cref="ChildDn"/>).
/// </summary>
public sealed class PlanEdgeRowModel
{
    public PlanEdgeRowModel(string parentDn, string parentName, string childDn, string childName)
    {
        ParentDn = parentDn;
        ParentName = parentName;
        ChildDn = childDn;
        ChildName = childName;
    }

    /// <summary>The parent (group) endpoint DN — the one that lists the child as a member.</summary>
    public string ParentDn { get; }

    /// <summary>The parent's resolved display name (falls back to the DN if somehow absent).</summary>
    public string ParentName { get; }

    /// <summary>The child (member) endpoint DN.</summary>
    public string ChildDn { get; }

    /// <summary>The child's resolved display name (falls back to the DN if somehow absent).</summary>
    public string ChildName { get; }

    /// <summary>The list label: <c>"&lt;parent&gt; ← &lt;child&gt;"</c> — the parent lists the
    /// child, matching the graph legend "is member of" reading.</summary>
    public string Display => $"{ParentName} ← {ChildName}";
}
