using GroupWeaver.Core.Model;

namespace GroupWeaver.Providers;

/// <summary>
/// The single source of truth for which directory attributes GroupWeaver ever
/// touches (PLANNING.md AP 1.5; privacy baseline for the detail panel, AP 2.5).
/// <see cref="FetchProperties"/> is the literal <c>PropertiesToLoad</c> of every
/// LDAP search — nothing outside that list is ever requested, so nothing else
/// enters process memory. Per-kind display sets decide which fetched values land
/// in <see cref="AdObject.Attributes"/>; the structural attributes
/// (<c>distinguishedName</c>, <c>name</c>, <c>objectClass</c>,
/// <c>sAMAccountName</c>, <c>member</c>) never do — they map to typed members
/// and membership edges instead. <c>primaryGroupID</c> is displayed but never
/// becomes an edge (ADR-002).
/// </summary>
public static class AttributeWhitelist
{
    /// <summary>
    /// Every attribute any LDAP search may load — exactly these 13, nothing else.
    /// </summary>
    public static IReadOnlyList<string> FetchProperties { get; } =
    [
        "distinguishedName",
        "name",
        "objectClass",
        "sAMAccountName",
        "groupType",
        "member",
        "description",
        "whenCreated",
        "department",
        "title",
        "primaryGroupID",
        "operatingSystem",
        "dNSHostName",
    ];

    /// <summary>Shown on every kind.</summary>
    private static readonly IReadOnlyDictionary<string, string> CommonDisplay =
        BuildSet("description", "whenCreated");

    /// <summary>Common plus user-specific attributes (incl. <c>primaryGroupID</c>, ADR-002).</summary>
    private static readonly IReadOnlyDictionary<string, string> UserDisplay =
        BuildSet("description", "whenCreated", "department", "title", "primaryGroupID");

    /// <summary>Common plus computer-specific attributes.</summary>
    private static readonly IReadOnlyDictionary<string, string> ComputerDisplay =
        BuildSet("description", "whenCreated", "operatingSystem", "dNSHostName");

    /// <summary>Common plus <c>groupType</c> (raw integer as an invariant string, as supplied).</summary>
    private static readonly IReadOnlyDictionary<string, string> GroupDisplay =
        BuildSet("description", "whenCreated", "groupType");

    /// <summary>
    /// Filters raw directory values down to the display whitelist for
    /// <paramref name="kind"/>. Matching is case-insensitive regardless of the
    /// key comparer of <paramref name="properties"/>; result keys use the
    /// canonical whitelist casing (e.g. <c>whenCreated</c>), so the result never
    /// contains case-duplicate keys and is safe for <see cref="AdObject.Attributes"/>.
    /// Absent attributes and empty value lists are skipped (LDAP omits empty
    /// attributes entirely); multi-valued attributes are joined with <c>"; "</c>.
    /// Values are passed through verbatim — the caller is responsible for any
    /// normalization (e.g. invariant stringification, <c>whenCreated</c> formatting).
    /// </summary>
    /// <param name="kind">The object kind whose display set applies.</param>
    /// <param name="properties">Raw attribute name → values, as fetched.</param>
    public static Dictionary<string, string> BuildAttributes(
        AdObjectKind kind, IReadOnlyDictionary<string, IReadOnlyList<string>> properties)
    {
        var display = DisplaySetFor(kind);
        var attributes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach ((string name, IReadOnlyList<string> values) in properties)
        {
            if (display.TryGetValue(name, out string? canonical) && values.Count > 0)
            {
                attributes[canonical] = string.Join("; ", values);
            }
        }

        return attributes;
    }

    private static IReadOnlyDictionary<string, string> DisplaySetFor(AdObjectKind kind) => kind switch
    {
        AdObjectKind.User => UserDisplay,
        AdObjectKind.Computer => ComputerDisplay,
        AdObjectKind.GlobalGroup or AdObjectKind.DomainLocalGroup or AdObjectKind.UniversalGroup
            => GroupDisplay,
        _ => CommonDisplay,
    };

    private static IReadOnlyDictionary<string, string> BuildSet(params string[] names) =>
        names.ToDictionary(n => n, n => n, StringComparer.OrdinalIgnoreCase);
}
