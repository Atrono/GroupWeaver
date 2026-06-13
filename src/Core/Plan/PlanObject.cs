namespace GroupWeaver.Core.Plan;

/// <summary>
/// A mutable authored object in a <see cref="PlanModel"/> — the editable mirror of the
/// read-only <c>AdObject</c>'s typed fields (ADR-014). Identity is the
/// <see cref="Dn"/>, owned by the <see cref="PlanModel"/> and keyed via
/// <c>Dn.Comparer</c>; like <c>AdObject</c>, this type deliberately does not override
/// equality. The <see cref="SamAccountName"/> is settable so the editor (and the
/// exporter's own defense-in-depth tests) can mutate it after creation.
/// </summary>
public sealed class PlanObject
{
    /// <summary>The formed DN (<c>CN=&lt;escaped name&gt;,&lt;BaseOuDn&gt;</c>), stored
    /// as-formed and never re-canonicalized. Identity is owned by the model.</summary>
    public required string Dn { get; init; }

    /// <summary>The authored kind (settable: <see cref="PlanModel.SetKind"/>).</summary>
    public required PlanCreatableKind Kind { get; set; }

    /// <summary>The display name = the value of the single CN RDN.</summary>
    public required string Name { get; set; }

    /// <summary>The SAM account name, if any.</summary>
    public string? SamAccountName { get; set; }
}
