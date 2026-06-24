using System.Globalization;

using Avalonia.Data.Converters;
using Avalonia.Media;

using GroupWeaver.App.ViewModels;

namespace GroupWeaver.App.Views;

/// <summary>
/// WP5d (#156) findings-table header converters: the sort-direction caret affordance. A header is a
/// <c>MultiBinding</c> over <c>[SortColumn, SortDescending]</c> with the header's own
/// <see cref="AuditSortColumn"/> as the <c>ConverterParameter</c>; <see cref="SortCaret"/> returns the
/// up/down caret glyph for the active column and an empty string for the others, so only the sorted
/// column shows a direction mark (the redundant text channel beside the column label — WCAG 1.4.1,
/// the caret is text, not colour). Compiled-bindings-safe; mirrors the
/// <see cref="SeverityConverters"/> static-palette shape.
/// </summary>
public static class AuditTableConverters
{
    /// <summary>Up caret for the active ascending column, down caret for the active descending column,
    /// empty for every other column. Values: <c>[AuditSortColumn active, bool descending]</c>;
    /// parameter: this header's <see cref="AuditSortColumn"/>.</summary>
    public static readonly IMultiValueConverter SortCaret = new SortCaretConverter();

    /// <summary>WP5e (#158) triage-status pill FILL (ADR-028): Open = transparent (a ghost pill on the
    /// card surface), Acknowledged = the Info blue fill, Suppressed = the Warning amber fill — reusing
    /// the proven severity hues so the triaged states read in the app's traffic-light vocabulary. The
    /// fill is a redundant emphasis cue; the meaning is the always-present status TEXT (WCAG 1.4.1).</summary>
    public static readonly IValueConverter StatusToFill =
        new FuncValueConverter<TriageStatus, IBrush>(status => status switch
        {
            TriageStatus.Acknowledged => BrandTokens.Info,
            TriageStatus.Suppressed => BrandTokens.Warning,
            _ => Brushes.Transparent,
        });

    /// <summary>WP5e (#158) triage-status pill INK (ADR-028): a triaged pill carries
    /// <see cref="BrandTokens.OnLightText"/> dark ink on its light Info/Warning fill — the SAME
    /// WCAG-proven pairing the severity badges use (6.04:1 on Info blue, 8.02:1 on Warning amber; white
    /// fails both, ADR-021). The Open pill is a value DataTrigger swap to the theme secondary brush in
    /// XAML (a transparent fill needs the card-surface ink, not a fixed token), so this converter only
    /// ever colours a triaged pill.</summary>
    public static readonly IValueConverter StatusToInk =
        new FuncValueConverter<TriageStatus, IBrush>(_ => BrandTokens.OnLightText);

    /// <summary>WP5e (#158) row dimming (ADR-028): a triaged row is muted to 0.6 opacity so it reads as
    /// resolved-but-still-listed; an Open row stays fully opaque. The pill (a non-opacity status channel)
    /// carries the actual state, so the dim is a redundant emphasis cue, not the sole signal.</summary>
    public static readonly IValueConverter TriagedToOpacity =
        new FuncValueConverter<bool, double>(triaged => triaged ? 0.6 : 1.0);

    private sealed class SortCaretConverter : IMultiValueConverter
    {
        public object Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture)
        {
            if (values.Count >= 2
                && values[0] is AuditSortColumn active
                && values[1] is bool descending
                && parameter is AuditSortColumn column
                && active == column
                && column != AuditSortColumn.None)
            {
                return descending ? " ▼" : " ▲"; // ▼ / ▲
            }

            return string.Empty;
        }
    }
}
