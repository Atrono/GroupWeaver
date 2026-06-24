using System;
using System.Text;
using Avalonia.Data.Converters;

namespace GroupWeaver.App.Views;

/// <summary>
/// Renders the Advanced (JSONC) editor's line-number gutter (WP6a): maps the editor
/// text to a newline-joined <c>"1\n2\n…\nN"</c> string a single right-aligned monospace
/// <c>TextBlock</c> displays beside the editor. N = the editor text's line count =
/// newline count + 1 (an empty document is one line; a trailing newline opens a final
/// blank line — matching the <c>TextBox</c>'s own line layout, so the numbers stay
/// row-aligned). No new dependency (no AvaloniaEdit); scroll-sync is handled in XAML by
/// sharing ONE outer ScrollViewer over the gutter + editor (see SettingsWindow.axaml),
/// so the gutter and the text rows scroll as a single surface.
/// </summary>
public static class LineNumberGutterConverter
{
    // A defensive ceiling: an absurdly large pasted blob would otherwise build a
    // multi-megabyte gutter string. Beyond this we still number up to the cap (the
    // editor remains usable; the gate still validates the full text on Apply).
    private const int MaxLines = 100_000;

    /// <summary>Editor text → the gutter's line-number column string.</summary>
    public static readonly IValueConverter ToLineNumbers =
        new FuncValueConverter<string?, string>(BuildGutter);

    private static string BuildGutter(string? text)
    {
        int lines = LineCount(text);
        var builder = new StringBuilder(lines * 4);
        for (int i = 1; i <= lines; i++)
        {
            if (i > 1)
            {
                builder.Append('\n');
            }

            builder.Append(i);
        }

        return builder.ToString();
    }

    private static int LineCount(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return 1;
        }

        int count = 1;
        foreach (char c in text)
        {
            if (c == '\n')
            {
                count++;
                if (count >= MaxLines)
                {
                    break;
                }
            }
        }

        return count;
    }
}
