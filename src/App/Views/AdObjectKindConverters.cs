using Avalonia.Data.Converters;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using GroupWeaver.Core.Model;

namespace GroupWeaver.App.Views;

/// <summary>
/// Compiled-bindings-safe converters turning an <see cref="AdObjectKind"/> into the
/// badge label and badge background used by list items (root picker, later sidebars).
///
/// THE per-kind color palette — AP 2.2 reuses these hex values (via <see cref="BrandTokens"/>,
/// THE source of truth, ADR-021) for the graph node colors (docs/ui-checklist.md A: node types
/// visually distinct, labels legible). All fills are Fluent-palette-adjacent and dark enough for
/// white badge text (>= ~4.5:1 contrast), so they read on both theme variants:
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
            AdObjectKind.User => ("U", BrandTokens.User),
            AdObjectKind.GlobalGroup => ("GG", BrandTokens.GlobalGroup),
            AdObjectKind.DomainLocalGroup => ("DL", BrandTokens.DomainLocalGroup),
            AdObjectKind.UniversalGroup => ("UG", BrandTokens.UniversalGroup),
            AdObjectKind.OrganizationalUnit => ("OU", BrandTokens.OrganizationalUnit),
            AdObjectKind.Computer => ("PC", BrandTokens.Computer),
            _ => ("EXT", BrandTokens.External),
        };
}
