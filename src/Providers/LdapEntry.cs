using System.Globalization;
using GroupWeaver.Core.Model;

namespace GroupWeaver.Providers;

/// <summary>
/// Provider-internal adapter for one LDAP search result: the DN plus a
/// case-insensitive attribute name → values map. A thin shim in LdapProvider
/// builds instances from <c>SearchResult</c> (stringifying values invariantly);
/// everything from here on is pure and unit-testable without a directory.
/// </summary>
internal sealed class LdapEntry
{
    private static readonly IReadOnlyList<string> NoValues = [];

    /// <summary>
    /// Copies <paramref name="properties"/> into an ordinal case-insensitive map
    /// (LDAP returns lowercased keys; lookups must not depend on casing).
    /// Throws <see cref="ArgumentException"/> on case-duplicate keys — a real
    /// directory never produces them.
    /// </summary>
    public LdapEntry(string dn, IReadOnlyDictionary<string, IReadOnlyList<string>> properties)
    {
        Dn = dn;
        Properties = new Dictionary<string, IReadOnlyList<string>>(
            properties, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Distinguished name, exactly as returned by the directory.</summary>
    public string Dn { get; }

    /// <summary>Attribute name → values, ordinal case-insensitive keys. Absent
    /// attributes have no entry at all (LDAP omits empty attributes entirely).</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<string>> Properties { get; }

    /// <summary>
    /// Maps an entry to an <see cref="AdObject"/>:
    /// <list type="bullet">
    /// <item><see cref="AdObject.Kind"/> via <see cref="AdObjectKindMapper"/> from the
    /// <c>objectClass</c> values plus <c>groupType</c> parsed as an invariant integer
    /// (absent or unparsable → <c>null</c>).</item>
    /// <item><see cref="AdObject.Name"/> from <c>name</c>; falls back to the DN if absent.</item>
    /// <item><see cref="AdObject.SamAccountName"/> from <c>sAMAccountName</c>, <c>null</c> if absent.</item>
    /// <item><see cref="AdObject.Attributes"/> via
    /// <see cref="AttributeWhitelist.BuildAttributes"/>; a <c>whenCreated</c> value that
    /// parses as a <see cref="DateTime"/> (invariant culture; no offset info → assumed
    /// UTC, as AD stores it) is normalized to invariant UTC
    /// <c>yyyy-MM-ddTHH:mm:ssZ</c>, otherwise kept verbatim.</item>
    /// </list>
    /// All reads tolerate absent properties.
    /// </summary>
    public static AdObject Map(LdapEntry entry)
    {
        int? groupType = null;
        if (entry.FirstValue("groupType") is string rawGroupType &&
            int.TryParse(rawGroupType, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed))
        {
            groupType = parsed;
        }

        var kind = AdObjectKindMapper.Map(entry.ValuesOf("objectClass"), groupType);

        var attributes = AttributeWhitelist.BuildAttributes(kind, entry.Properties);
        if (attributes.TryGetValue("whenCreated", out string? whenCreated) &&
            DateTime.TryParse(
                whenCreated,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal,
                out DateTime createdUtc))
        {
            attributes["whenCreated"] =
                createdUtc.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
        }

        return new AdObject
        {
            Dn = entry.Dn,
            Kind = kind,
            Name = entry.FirstValue("name") ?? entry.Dn,
            SamAccountName = entry.FirstValue("sAMAccountName"),
            Attributes = attributes,
        };
    }

    private IReadOnlyList<string> ValuesOf(string name) =>
        Properties.TryGetValue(name, out var values) ? values : NoValues;

    private string? FirstValue(string name) =>
        Properties.TryGetValue(name, out var values) && values.Count > 0 ? values[0] : null;
}
