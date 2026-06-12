using GroupWeaver.Core.Model;
using GroupWeaver.Providers;

namespace GroupWeaver.App.ViewModels;

/// <summary>
/// Load-state of the selected object, derived from snapshot state ALONE
/// (ADR-007 D3): DN absent or External ∧ ¬IsLoaded → <see cref="NotLoaded"/>
/// ("expand/Refresh to resolve"); External ∧ IsLoaded → <see cref="Unresolvable"/>
/// (FSP — attributes genuinely unavailable); everything else → <see cref="Loaded"/>
/// (a group whose MEMBERS are unloaded still has loaded attributes).
/// </summary>
public enum DetailPanelState
{
    /// <summary>The object's attributes are in the snapshot.</summary>
    Loaded,

    /// <summary>Never fetched — expand or Refresh resolves it.</summary>
    NotLoaded,

    /// <summary>Fetched and genuinely unresolvable (FSP, AP 1.5).</summary>
    Unresolvable,
}

/// <summary>One attribute row of the detail panel: label and value, verbatim.</summary>
public sealed record DetailRow(string Label, string Value);

/// <summary>
/// The immutable detail-panel projection (ADR-007 D2): <see cref="Build"/> is the
/// SINGLE choke point between the snapshot and the view — the panel binds this
/// type and nothing else, so binding a domain object into XAML is structurally
/// impossible without editing <see cref="Build"/>. Header = the four typed members
/// of the data-model contract; <see cref="Rows"/> mirror
/// <see cref="AdObject.Attributes"/> VERBATIM — the UI never re-filters, a provider
/// whitelist bug must become visible, not masked. Pinned by
/// <c>tests/GroupWeaver.App.Tests/WorkspaceDetailTests.cs</c>.
/// </summary>
public sealed record DetailPanelModel
{
    /// <summary>The selected DN, verbatim — never canonicalized (data-model rule).</summary>
    public required string Dn { get; init; }

    /// <summary>Kind per the <see cref="DirectorySnapshot.GetKind"/> contract:
    /// a DN absent from the snapshot is <see cref="AdObjectKind.External"/>.</summary>
    public required AdObjectKind Kind { get; init; }

    /// <summary>Display name of the snapshot object; <c>null</c> when the DN is
    /// absent from the snapshot (nothing fetched, nothing to fabricate).</summary>
    public required string? Name { get; init; }

    /// <summary>SAM account name of the snapshot object, if it has one.</summary>
    public required string? SamAccountName { get; init; }

    /// <summary>Load-state honesty per ADR-007 D3 (see <see cref="DetailPanelState"/>).</summary>
    public required DetailPanelState State { get; init; }

    /// <summary>The <see cref="AdObject.Attributes"/> mirror — same count, same pairs,
    /// NO re-filtering. Known keys in <see cref="AttributeWhitelist.FetchProperties"/>
    /// declaration order, unknown keys appended alphabetically (ADR-007 D4); empty
    /// when the DN is absent from the snapshot or the object has no attributes.</summary>
    public required IReadOnlyList<DetailRow> Rows { get; init; }

    /// <summary>
    /// Projects <paramref name="dn"/> from <paramref name="snapshot"/> — a pure,
    /// synchronous snapshot read: never calls a provider, never touches the busy
    /// gate (ADR-007 D1). Returns <c>null</c> iff <paramref name="dn"/> is
    /// <c>null</c> (no selection, no panel).
    /// </summary>
    public static DetailPanelModel? Build(DirectorySnapshot snapshot, string? dn)
    {
        if (dn is null)
        {
            return null;
        }

        if (!snapshot.TryGetObject(dn, out var obj))
        {
            // Frontier DN (member-edge endpoint outside Objects): never fetched —
            // an honest NotLoaded header with no fabricated name or rows.
            return new DetailPanelModel
            {
                Dn = dn,
                Kind = AdObjectKind.External,
                Name = null,
                SamAccountName = null,
                State = DetailPanelState.NotLoaded,
                Rows = [],
            };
        }

        var state = obj.Kind is not AdObjectKind.External
            ? DetailPanelState.Loaded
            : snapshot.IsLoaded(dn) ? DetailPanelState.Unresolvable : DetailPanelState.NotLoaded;

        return new DetailPanelModel
        {
            Dn = dn,
            Kind = obj.Kind,
            Name = obj.Name,
            SamAccountName = obj.SamAccountName,
            State = state,
            Rows = BuildRows(obj.Attributes),
        };
    }

    /// <summary>The D2 mirror: one row per attribute, values verbatim. Known labels
    /// use the whitelist's canonical casing (the provider's casing by contract).</summary>
    private static IReadOnlyList<DetailRow> BuildRows(
        IReadOnlyDictionary<string, string> attributes)
    {
        if (attributes.Count == 0)
        {
            return [];
        }

        var rows = new List<DetailRow>(attributes.Count);
        var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in AttributeWhitelist.FetchProperties)
        {
            if (attributes.TryGetValue(property, out var value))
            {
                rows.Add(new DetailRow(property, value));
                known.Add(property);
            }
        }

        foreach (var (label, value) in attributes
            .Where(pair => !known.Contains(pair.Key))
            .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))
        {
            rows.Add(new DetailRow(label, value));
        }

        return rows;
    }
}
