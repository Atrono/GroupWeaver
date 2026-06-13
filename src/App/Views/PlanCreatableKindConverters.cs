using Avalonia.Data.Converters;

using GroupWeaver.Core.Plan;

namespace GroupWeaver.App.Views;

/// <summary>
/// Compiled-bindings-safe converter turning a <see cref="PlanCreatableKind"/> into the
/// friendly, spaced label the Plan Mode add-object kind selector shows (AP 4.2.3) — so the
/// combo speaks the product's vocabulary ("Domain-local group") instead of leaking the raw
/// camel-case enum name ("DomainLocalGroup"). Parity with
/// <see cref="GroupWeaver.App.ViewModels.PlanNodeRowModel.KindLabel"/>, which uses the same
/// mapping for the Objects-row tooltip; this is the single rendering seam for the kind combo
/// whose items are bare <see cref="PlanCreatableKind"/> values.
/// </summary>
public static class PlanCreatableKindConverters
{
    /// <summary>The friendly, spaced label per kind (parity with <c>PlanNodeRowModel.KindLabel</c>).</summary>
    public static readonly IValueConverter ToFriendlyLabel =
        new FuncValueConverter<PlanCreatableKind, string>(FriendlyLabel);

    /// <summary>The one mapping both the combo converter and the row tooltip resolve through.</summary>
    public static string FriendlyLabel(PlanCreatableKind kind) => kind switch
    {
        PlanCreatableKind.User => "User",
        PlanCreatableKind.GlobalGroup => "Global group",
        PlanCreatableKind.DomainLocalGroup => "Domain-local group",
        PlanCreatableKind.UniversalGroup => "Universal group",
        _ => kind.ToString(),
    };
}
