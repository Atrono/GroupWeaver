using Avalonia.Data.Converters;

namespace GroupWeaver.App.Views;

/// <summary>
/// Compiled-bindings-safe converters over raw DN strings. The root picker shows a
/// candidate's leaf name on the primary line and its PARENT path on the secondary line,
/// so two same-named OUs under different parents (issue #196) stay distinguishable even
/// under right-side <c>CharacterEllipsis</c> trimming (parents differ at the START).
/// </summary>
public static class DnConverters
{
    /// <summary>
    /// The parent path of a DN = everything after the first UNESCAPED comma. A comma
    /// escaped as <c>\,</c> is part of an RDN value, not a separator, so the split skips
    /// commas preceded by a backslash. A single-RDN / root DN (no unescaped comma) yields
    /// the literal placeholder; null/empty passes through unchanged. Pure, ordinal.
    /// </summary>
    public static readonly IValueConverter ToParentPath =
        new FuncValueConverter<string?, string?>(ParentPath);

    private const string DirectoryRoot = "(directory root)";

    private static string? ParentPath(string? dn)
    {
        if (string.IsNullOrEmpty(dn))
        {
            return dn;
        }

        for (var i = 0; i < dn.Length; i++)
        {
            if (dn[i] == ',')
            {
                var backslashes = 0;
                for (var j = i - 1; j >= 0 && dn[j] == '\\'; j--)
                {
                    backslashes++;
                }

                // An even run (0, 2, 4, …) of preceding backslashes are all self-escaped,
                // so this comma is a real separator; an odd run escapes the comma itself.
                if (backslashes % 2 == 0)
                {
                    return dn[(i + 1)..];
                }
            }
        }

        return DirectoryRoot;
    }
}
