using System.Diagnostics.CodeAnalysis;

namespace GroupWeaver.Core.Model;

/// <summary>
/// In-memory snapshot of a loaded directory scope: objects plus directed membership
/// edges. Mutable container; by contract NOT thread-safe — load and mutate from a
/// single thread. All DN lookups use <see cref="Dn.Comparer"/>. Cyclic memberships
/// (A→B→A) are stored as-is; cycle handling is the consumer's concern.
/// </summary>
public sealed class DirectorySnapshot
{
    private readonly Dictionary<string, AdObject> _objects = new(Dn.Comparer);
    private readonly Dictionary<string, List<string>> _members = new(Dn.Comparer);

    /// <summary>All objects currently in the snapshot.</summary>
    public IReadOnlyCollection<AdObject> Objects => _objects.Values;

    /// <summary>All membership edges of all loaded parents (computed on access).</summary>
    public IReadOnlyCollection<MembershipEdge> Edges =>
        _members
            .SelectMany(pair => pair.Value.Select(child => new MembershipEdge(pair.Key, child)))
            .ToList();

    /// <summary>Adds or replaces an object, keyed by its DN (latest wins).</summary>
    public void AddObject(AdObject obj) => _objects[obj.Dn] = obj;

    /// <summary>Looks up an object by DN, case-insensitively.</summary>
    public bool TryGetObject(string dn, [MaybeNullWhen(false)] out AdObject obj) =>
        _objects.TryGetValue(dn, out obj);

    /// <summary>
    /// Records the direct members of <paramref name="parentDn"/> and marks it loaded.
    /// REPLACES any previously recorded members of that parent (refresh semantics),
    /// removing stale edges. Member DNs are de-duplicated case-insensitively, first
    /// occurrence wins. The parent does not need to be present in <see cref="Objects"/>.
    /// </summary>
    public void SetMembers(string parentDn, IEnumerable<string> memberDns)
    {
        var members = new List<string>();
        var seen = new HashSet<string>(Dn.Comparer);
        foreach (var memberDn in memberDns)
        {
            if (seen.Add(memberDn))
            {
                members.Add(memberDn);
            }
        }

        _members[parentDn] = members;
    }

    /// <summary>Whether <see cref="SetMembers"/> has been called for this parent.</summary>
    public bool IsLoaded(string parentDn) => _members.ContainsKey(parentDn);

    /// <summary>
    /// Direct member DNs of <paramref name="parentDn"/>. <c>null</c> means the parent
    /// was never loaded; an empty list means it was loaded and is genuinely empty.
    /// </summary>
    public IReadOnlyList<string>? GetMembers(string parentDn) =>
        _members.TryGetValue(parentDn, out var members) ? members : null;

    /// <summary>Kind of the object with the given DN; <see cref="AdObjectKind.External"/>
    /// when the DN is not in the snapshot.</summary>
    public AdObjectKind GetKind(string dn) =>
        _objects.TryGetValue(dn, out var obj) ? obj.Kind : AdObjectKind.External;
}
