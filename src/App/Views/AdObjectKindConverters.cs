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

    /// <summary>Badge BORDER brush per kind — the WCAG 1.4.11 contrast-lift ring (#8A93A3,
    /// <see cref="BrandTokens.NodeLiftRing"/>) on the DL/UG/Computer fills whose graphical-object
    /// contrast vs the dark page falls below 3:1 (DL 2.55 / UG 2.66 / Computer 2.59); every OTHER
    /// kind fill already clears 3:1 and gets a transparent (invisible) border, so the badge
    /// geometry stays uniform. The EXACT mirror of graph.js's per-kind <c>nodeLiftRing</c> lift
    /// (the fills themselves stay unchanged) — see <c>src/App/web/graph.js</c>.</summary>
    public static readonly IValueConverter ToBadgeBorderBrush =
        new FuncValueConverter<AdObjectKind, IBrush>(BorderBrushFor);

    private static IBrush BorderBrushFor(AdObjectKind kind) =>
        kind is AdObjectKind.DomainLocalGroup or AdObjectKind.UniversalGroup or AdObjectKind.Computer
            ? BrandTokens.NodeLiftRing
            : TransparentBorder;

    private static readonly ImmutableSolidColorBrush TransparentBorder = new(Colors.Transparent);

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
