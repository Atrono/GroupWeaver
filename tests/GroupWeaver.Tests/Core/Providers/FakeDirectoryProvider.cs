using GroupWeaver.Core.Model;
using GroupWeaver.Core.Providers;

namespace GroupWeaver.Tests.Core.Providers;

/// <summary>
/// Minimal in-memory <see cref="IDirectoryProvider"/> for contract tests; pre-validates
/// the DemoProvider shape (AP 1.4). Scope membership is a case-insensitive DN-suffix
/// match on the base DN. Unresolvable DNs are values, never exceptions.
/// </summary>
internal sealed class FakeDirectoryProvider : IDirectoryProvider
{
    private readonly Dictionary<string, AdObject> _objects = new(Dn.Comparer);
    private readonly Dictionary<string, IReadOnlyList<string>> _members = new(Dn.Comparer);

    public void AddObject(AdObject obj) => _objects[obj.Dn] = obj;

    public void SetMembers(string groupDn, params string[] memberDns) =>
        _members[groupDn] = memberDns;

    public Task<DirectoryConnection> ConnectAsync(CancellationToken cancellationToken = default)
    {
        var groupCount = _objects.Values.Count(o => IsGroup(o.Kind));
        return Task.FromResult(new DirectoryConnection("fake in-memory directory", groupCount));
    }

    public Task<IReadOnlyList<AdObject>> GetRootCandidatesAsync(
        CancellationToken cancellationToken = default)
    {
        IReadOnlyList<AdObject> candidates = _objects.Values
            .Where(o => o.Kind == AdObjectKind.OrganizationalUnit || IsGroup(o.Kind))
            .ToList();
        return Task.FromResult(candidates);
    }

    public Task<DirectorySnapshot> LoadScopeAsync(
        string baseDn, CancellationToken cancellationToken = default)
    {
        var snapshot = new DirectorySnapshot();
        foreach (var obj in _objects.Values.Where(o => IsInScope(o.Dn, baseDn)))
        {
            snapshot.AddObject(obj);
            if (IsGroup(obj.Kind))
            {
                snapshot.SetMembers(
                    obj.Dn,
                    _members.TryGetValue(obj.Dn, out var memberDns) ? memberDns : []);
            }
        }

        return Task.FromResult(snapshot);
    }

    public Task<AdObject?> GetObjectAsync(string dn, CancellationToken cancellationToken = default) =>
        Task.FromResult(_objects.TryGetValue(dn, out var obj) ? obj : null);

    public Task<IReadOnlyList<AdObject>> GetMembersAsync(
        string groupDn, CancellationToken cancellationToken = default)
    {
        if (!_members.TryGetValue(groupDn, out var memberDns))
        {
            return Task.FromResult<IReadOnlyList<AdObject>>([]);
        }

        IReadOnlyList<AdObject> members = memberDns
            .Select(dn => _objects.TryGetValue(dn, out var obj) ? obj : MakeExternal(dn))
            .ToList();
        return Task.FromResult(members);
    }

    private static bool IsGroup(AdObjectKind kind) =>
        kind is AdObjectKind.GlobalGroup or AdObjectKind.DomainLocalGroup or AdObjectKind.UniversalGroup;

    private static bool IsInScope(string dn, string baseDn) =>
        Dn.Comparer.Equals(dn, baseDn) ||
        dn.EndsWith("," + baseDn, StringComparison.OrdinalIgnoreCase);

    private static AdObject MakeExternal(string dn) => new()
    {
        Dn = dn,
        Kind = AdObjectKind.External,
        Name = dn,
    };
}
