using Avalonia.Data.Converters;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using GroupWeaver.Core.Rules;

namespace GroupWeaver.App.Views;

/// <summary>
/// THE App-side severity palette (AP 3.4 S4, ADR-010 §1/§5): the violations sidebar
/// glyphs read these — in lock-step with the graph overlay halos (graph.js
/// <c>node[sev=…]</c> rules) and the verify.mjs <c>SEVERITY</c> tripwire. The hex
/// values are LOAD-BEARING: Error #D13438 / Warning #F7A30B / Info #4FA3E3, the
/// pinned "Final rendering decision" table — they MUST equal the JS map and the
/// stylesheet <c>overlay-color</c> rules (C#↔JS parity is the whole point of
/// pinning them). <see cref="ToGlyph"/> adds the colorblind-redundant letter
/// (E / W / i) the row shows beside the colored square.
///
/// Compiled-bindings-safe; mirrors the AP 2.2 <see cref="AdObjectKindConverters"/>
/// shape (one converter palette reused by the XAML binding seam and the tests).
/// </summary>
public static class SeverityConverters
{
    /// <summary>Severity → its pinned ADR-010 overlay color (the glyph square fill).</summary>
    public static readonly IValueConverter ToBrush =
        new FuncValueConverter<RuleSeverity, IBrush>(BrushFor);

    /// <summary>Severity → the redundant, colorblind-safe letter (E / W / i).</summary>
    public static readonly IValueConverter ToGlyph =
        new FuncValueConverter<RuleSeverity, string>(GlyphFor);

    private static IBrush BrushFor(RuleSeverity severity) => severity switch
    {
        RuleSeverity.Error => ErrorBrush,
        RuleSeverity.Warning => WarningBrush,
        _ => InfoBrush,
    };

    private static string GlyphFor(RuleSeverity severity) => severity switch
    {
        RuleSeverity.Error => "E",
        RuleSeverity.Warning => "W",
        _ => "i",
    };

    private static readonly ImmutableSolidColorBrush ErrorBrush = new(Color.Parse("#D13438"));
    private static readonly ImmutableSolidColorBrush WarningBrush = new(Color.Parse("#F7A30B"));
    private static readonly ImmutableSolidColorBrush InfoBrush = new(Color.Parse("#4FA3E3"));
}
