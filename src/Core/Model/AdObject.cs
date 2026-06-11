namespace GroupWeaver.Core.Model;

/// <summary>
/// A single directory object. Identity lives in DN-keyed collections (see
/// <see cref="Dn.Comparer"/>); this type deliberately does not override equality.
/// </summary>
public sealed class AdObject
{
    private static readonly IReadOnlyDictionary<string, string> EmptyAttributes =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    private readonly IReadOnlyDictionary<string, string> _attributes = EmptyAttributes;

    /// <summary>Distinguished name, stored exactly as supplied by the provider.</summary>
    public required string Dn { get; init; }

    /// <summary>Classification of the object (see <see cref="AdObjectKindMapper"/>).</summary>
    public required AdObjectKind Kind { get; init; }

    /// <summary>Display name (typically the CN).</summary>
    public required string Name { get; init; }

    /// <summary>SAM account name, if the object has one.</summary>
    public string? SamAccountName { get; init; }

    /// <summary>
    /// Additional whitelisted attributes for the detail panel. Key lookup is always
    /// case-insensitive: supplied dictionaries are copied into an ordinal
    /// case-insensitive dictionary. Defaults to empty.
    /// </summary>
    public IReadOnlyDictionary<string, string> Attributes
    {
        get => _attributes;
        init => _attributes = new Dictionary<string, string>(value, StringComparer.OrdinalIgnoreCase);
    }
}
