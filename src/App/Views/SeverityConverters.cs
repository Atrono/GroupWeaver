using System.Collections;
using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using GroupWeaver.App.ViewModels;
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

    /// <summary>
    /// Counts the <see cref="ViolationRowModel"/> rows of one severity. Used in a
    /// <c>MultiBinding</c> whose values are <c>[Violations, Violations.Count]</c> — value[0]
    /// is the collection to tally, value[1] (the <c>Count</c>) is purely the change trigger
    /// (binding the collection reference alone never re-fires on in-place Clear/Add, the way
    /// the projection repopulates). The <c>ConverterParameter</c> is the
    /// <see cref="RuleSeverity"/> to tally. Drives the sidebar header's per-severity summary
    /// chips (E n · W n · i n): the three severity glyphs sit ABOVE the scroll fold regardless
    /// of report order, so every severity's color+letter is evidenced in a static frame (the
    /// canonical errors-first order otherwise pushes Warning/Info below the fold). A chip whose
    /// count is 0 is hidden via <see cref="HasSeverity"/>.
    /// </summary>
    public static readonly IMultiValueConverter CountForSeverity = new SeverityCountConverter(asBool: false);

    /// <summary>Same tally as <see cref="CountForSeverity"/>, reduced to a bool (count &gt; 0):
    /// hides a summary chip whose severity has no findings (so a non-demo scope with only
    /// errors shows one chip, not three empty ones).</summary>
    public static readonly IMultiValueConverter HasSeverity = new SeverityCountConverter(asBool: true);

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

    /// <summary>Counts the rows of the parameter severity in the bound violations
    /// collection (a <c>MultiBinding</c>: value[0] = collection, value[1] = its Count, the
    /// in-place-mutation change trigger). <paramref name="asBool"/> selects count &gt; 0 (chip
    /// visibility, a bool) vs. the count rendered as a string (chip text — returning a string
    /// because the <c>TextBlock.Text</c> binding target doesn't coerce a boxed int), so both
    /// header chip channels share one tally.</summary>
    private sealed class SeverityCountConverter(bool asBool) : IMultiValueConverter
    {
        public object Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture)
        {
            var count = 0;
            if (values.Count > 0 && values[0] is IEnumerable rows && parameter is RuleSeverity severity)
            {
                foreach (var row in rows)
                {
                    if (row is ViolationRowModel { Severity: var s } && s == severity)
                    {
                        count++;
                    }
                }
            }

            return asBool ? count > 0 : count.ToString(CultureInfo.InvariantCulture);
        }
    }
}
