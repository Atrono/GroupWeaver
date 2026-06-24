using System.Globalization;

using Avalonia.Data.Converters;

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
