namespace GroupWeaver.Core.Model;

/// <summary>
/// Distinguished-name comparison policy. THE RULE: every DN-keyed collection in the
/// codebase (dictionaries, sets, lookups, equality checks) uses <see cref="Comparer"/>.
/// DN strings are stored as given by the directory — never canonicalized — and only
/// ever COMPARED case-insensitively.
/// </summary>
public static class Dn
{
    /// <summary>The single comparer for DN strings: ordinal, case-insensitive.</summary>
    public static StringComparer Comparer => StringComparer.OrdinalIgnoreCase;
}
