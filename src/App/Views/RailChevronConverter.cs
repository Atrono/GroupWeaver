using Avalonia.Data.Converters;
using Avalonia.Media;

namespace GroupWeaver.App.Views;

/// <summary>
/// The ADR-022 D3 rail collapse-toggle glyph: maps <c>IsRailCollapsed</c> to the chevron
/// the seam button shows. Collapsed ⇒ ▸ ("expand the rail back in"); open ⇒ ◂ ("collapse
/// it away") — the persistent ▸ chevron is the always-clickable exit affordance when the
/// rail is hidden (ADR-022 Consequences). Compiled-bindings-safe; mirrors the
/// one-converter-palette shape of <see cref="SeverityConverters"/>.
/// </summary>
public static class RailChevronConverter
{
    // Vector triangle chevrons (~9px): a filled <see cref="Geometry"/> gets clean grayscale
    // edge AA, dodging the subpixel-antialiasing colour fringe a triangle font glyph shows at
    // small size on the near-black seam. ◂ = apex on the left; ▸ = apex on the right.
    private static readonly Geometry LeftChevron = Geometry.Parse("M9,0 L0,4.5 L9,9 Z");
    private static readonly Geometry RightChevron = Geometry.Parse("M0,0 L9,4.5 L0,9 Z");

    /// <summary><c>true</c> (collapsed) → ▸ ; <c>false</c> (open) → ◂.</summary>
    public static readonly IValueConverter ToGeometry =
        new FuncValueConverter<bool, Geometry>(collapsed => collapsed ? RightChevron : LeftChevron);

    /// <summary>
    /// Self-documenting tooltip for the seam toggle: collapsed ⇒ "Expand rail (Ctrl+B)"
    /// (the ▸ brings the rail back), open ⇒ "Collapse rail (Ctrl+B)" (the ◂ tucks it away).
    /// </summary>
    public static readonly IValueConverter ToTooltip =
        new FuncValueConverter<bool, string>(collapsed =>
            collapsed ? "Expand rail (Ctrl+B)" : "Collapse rail (Ctrl+B)");
}
