using GroupWeaver.App.Views;
using GroupWeaver.Core.Model;
using GroupWeaver.Core.Plan;

namespace GroupWeaver.App.ViewModels;

/// <summary>
/// One authored-object row of the Plan Mode editor (AP 4.2.3 / ADR-014): the immutable
/// projection of a <see cref="PlanObject"/> the Objects list AND the membership combos bind.
/// Identity is the <see cref="Dn"/> (the combos hold THESE instances, so selection re-resolves
/// by DN under <c>Dn.Comparer</c> on every <c>RefreshAuthoredCollections</c>). The kind badge
/// renders over <see cref="EngineKind"/> via <c>AdObjectKindConverters</c> — THE one kind
/// palette ("same kind = same color everywhere", ui-checklist) — never a second palette.
/// </summary>
public sealed class PlanNodeRowModel
{
    public PlanNodeRowModel(string dn, string name, PlanCreatableKind kind)
    {
        Dn = dn;
        Name = name;
        Kind = kind;
    }

    /// <summary>The authored object's formed DN — the row identity (selection match key,
    /// compared under <c>Dn.Comparer</c>) and the command parameter source.</summary>
    public string Dn { get; }

    /// <summary>The display name (the CN RDN value) the list + combos show beside the badge.</summary>
    public string Name { get; }

    /// <summary>The authored plan kind — drives the group-vs-account split (only a group can
    /// be a membership parent, so <c>GroupNodes</c> is the <c>PlanKindMap.IsGroup</c> subset).</summary>
    public PlanCreatableKind Kind { get; }

    /// <summary>The engine kind the kind badge renders over (via
    /// <c>AdObjectKindConverters.ToBadgeBrush</c>/<c>ToBadgeLabel</c>) — the SAME palette the
    /// root picker, graph nodes, and detail panel use.</summary>
    public AdObjectKind EngineKind => PlanKindMap.ToAdObjectKind(Kind);

    /// <summary>The friendly, spaced scope label the Objects-row tooltip shows (the badge only
    /// shows the short "DL"/"UG" code) — the SAME mapping the add-object kind combo renders
    /// through, resolved via the single <see cref="PlanCreatableKindConverters.FriendlyLabel"/>
    /// source so the tooltip and the combo can never drift.</summary>
    public string KindLabel => PlanCreatableKindConverters.FriendlyLabel(Kind);
}
