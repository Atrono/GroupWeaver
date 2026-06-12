using Avalonia.Data.Converters;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using GroupWeaver.Core.Model;

namespace GroupWeaver.App.Views;

/// <summary>
/// Compiled-bindings-safe converters turning an <see cref="AdObjectKind"/> into the
/// badge label and badge background used by list items (root picker, later sidebars).
///
/// THE per-kind color palette — AP 2.2 must reuse these hex values for the graph node
/// colors (docs/ui-checklist.md A: node types visually distinct, labels legible).
/// All colors are Fluent-palette-adjacent and dark enough for white badge text
/// (>= ~4.5:1 contrast), so they read on both theme variants:
///
///   User              "U"    #038387 (teal)
///   GlobalGroup       "GG"   #107C10 (green)
///   DomainLocalGroup  "DL"   #A14000 (rust)
///   UniversalGroup    "UG"   #744DA9 (purple)
///   OrganizationalUnit"OU"   #0F6CBD (blue)
///   Computer          "PC"   #556070 (slate)
///   External          "EXT"  #757575 (gray)
/// </summary>
public static class AdObjectKindConverters
{
    /// <summary>Short badge label per kind ("OU", "GG", "DL", "UG", ...).</summary>
    public static readonly IValueConverter ToBadgeLabel =
        new FuncValueConverter<AdObjectKind, string>(kind => For(kind).Label);

    /// <summary>Badge background brush per kind (palette above).</summary>
    public static readonly IValueConverter ToBadgeBrush =
        new FuncValueConverter<AdObjectKind, IBrush>(kind => For(kind).Brush);

    private static (string Label, ImmutableSolidColorBrush Brush) For(AdObjectKind kind) =>
        kind switch
        {
            AdObjectKind.User => ("U", UserBrush),
            AdObjectKind.GlobalGroup => ("GG", GlobalGroupBrush),
            AdObjectKind.DomainLocalGroup => ("DL", DomainLocalGroupBrush),
            AdObjectKind.UniversalGroup => ("UG", UniversalGroupBrush),
            AdObjectKind.OrganizationalUnit => ("OU", OrganizationalUnitBrush),
            AdObjectKind.Computer => ("PC", ComputerBrush),
            _ => ("EXT", ExternalBrush),
        };

    private static readonly ImmutableSolidColorBrush UserBrush = new(Color.Parse("#038387"));
    private static readonly ImmutableSolidColorBrush GlobalGroupBrush = new(Color.Parse("#107C10"));
    private static readonly ImmutableSolidColorBrush DomainLocalGroupBrush = new(Color.Parse("#A14000"));
    private static readonly ImmutableSolidColorBrush UniversalGroupBrush = new(Color.Parse("#744DA9"));
    private static readonly ImmutableSolidColorBrush OrganizationalUnitBrush = new(Color.Parse("#0F6CBD"));
    private static readonly ImmutableSolidColorBrush ComputerBrush = new(Color.Parse("#556070"));
    private static readonly ImmutableSolidColorBrush ExternalBrush = new(Color.Parse("#757575"));
}
