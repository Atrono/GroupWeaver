using System.Text.Json;
using GroupWeaver.Core.Graph;
using GroupWeaver.Core.Model;
using GroupWeaver.Core.Providers;

namespace GroupWeaver.Providers;

/// <summary>
/// In-memory <see cref="IDirectoryProvider"/> backed by the embedded demo dataset
/// (<c>Demo/demo-directory.json</c>, mirroring <c>tools/seed-testad.ps1</c>). The
/// dataset is parsed once on first use and validated strictly: it is our own
/// embedded file, so any malformation fails loud with <see cref="InvalidDataException"/>.
/// Unresolvable DNs are values (<c>null</c> / External / empty list), never exceptions.
/// </summary>
public sealed class DemoProvider : IDirectoryProvider
{
    private const string ResourceName = "GroupWeaver.Providers.Demo.demo-directory.json";

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly Lazy<DemoDataset> _dataset = new(LoadDataset);

    /// <inheritdoc />
    public Task<DirectoryConnection> ConnectAsync(CancellationToken cancellationToken = default)
    {
        var dataset = _dataset.Value;
        return Task.FromResult(new DirectoryConnection(
            $"demo mode: embedded fake directory '{dataset.Domain}' ({dataset.GroupCount} groups, {dataset.Objects.Count} objects)",
            dataset.GroupCount));
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<AdObject>> GetRootCandidatesAsync(
        CancellationToken cancellationToken = default)
    {
        IReadOnlyList<AdObject> candidates = _dataset.Value.Objects.Values
            .Where(o => o.Kind == AdObjectKind.OrganizationalUnit || IsGroup(o.Kind))
            .ToList();
        return Task.FromResult(candidates);
    }

    /// <inheritdoc />
    public Task<DirectorySnapshot> LoadScopeAsync(
        string baseDn, CancellationToken cancellationToken = default)
    {
        var dataset = _dataset.Value;
        var snapshot = new DirectorySnapshot();
        foreach (var obj in dataset.Objects.Values.Where(o => IsInScope(o.Dn, baseDn)))
        {
            snapshot.AddObject(obj);
            if (IsGroup(obj.Kind))
            {
                // Every in-scope group is marked loaded — including the empty ones.
                // Out-of-scope member DNs stay as DNs; GetKind resolves them as External.
                snapshot.SetMembers(obj.Dn, dataset.Members[obj.Dn]);
            }
        }

        return Task.FromResult(snapshot);
    }

    /// <inheritdoc />
    public Task<AdObject?> GetObjectAsync(string dn, CancellationToken cancellationToken = default) =>
        Task.FromResult(_dataset.Value.Objects.TryGetValue(dn, out var obj) ? obj : null);

    /// <inheritdoc />
    public Task<IReadOnlyList<AdObject>> GetMembersAsync(
        string groupDn, CancellationToken cancellationToken = default)
    {
        var dataset = _dataset.Value;
        if (!dataset.Members.TryGetValue(groupDn, out var memberDns))
        {
            return Task.FromResult<IReadOnlyList<AdObject>>([]);
        }

        IReadOnlyList<AdObject> members = memberDns
            .Select(dn => dataset.Objects.TryGetValue(dn, out var obj) ? obj : MakeExternal(dn))
            .ToList();
        return Task.FromResult(members);
    }

    private static DemoDataset LoadDataset()
    {
        using var stream = typeof(DemoProvider).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidDataException($"demo dataset: embedded resource '{ResourceName}' is missing.");

        DatasetDto? dto;
        try
        {
            dto = JsonSerializer.Deserialize<DatasetDto>(stream, SerializerOptions);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"demo dataset: malformed JSON: {ex.Message}", ex);
        }

        if (dto is null || string.IsNullOrWhiteSpace(dto.Domain) ||
            string.IsNullOrWhiteSpace(dto.RootDn) || dto.Objects is null)
        {
            throw new InvalidDataException(
                "demo dataset: 'domain', 'rootDn' and 'objects' are required at the top level.");
        }

        var objects = new Dictionary<string, AdObject>(Dn.Comparer);
        var members = new Dictionary<string, IReadOnlyList<string>>(Dn.Comparer);
        var groupCount = 0;
        foreach (var entry in dto.Objects)
        {
            var obj = ToAdObject(entry);
            if (!objects.TryAdd(obj.Dn, obj))
            {
                throw new InvalidDataException($"demo dataset: duplicate DN '{obj.Dn}'.");
            }

            if (IsGroup(obj.Kind))
            {
                groupCount++;
                members[obj.Dn] = entry.Members ?? [];
            }
            else if (entry.Members is not null)
            {
                throw new InvalidDataException(
                    $"demo dataset: '{obj.Dn}' has a member list but kind '{obj.Kind}' is not a group.");
            }
        }

        return new DemoDataset(dto.Domain, dto.RootDn, objects, members, groupCount);
    }

    private static AdObject ToAdObject(ObjectDto entry)
    {
        if (string.IsNullOrWhiteSpace(entry.Dn) || string.IsNullOrWhiteSpace(entry.Name))
        {
            throw new InvalidDataException(
                $"demo dataset: every object needs 'dn' and 'name' (offending dn: '{entry.Dn}').");
        }

        if (!Enum.TryParse<AdObjectKind>(entry.Kind, ignoreCase: false, out var kind)
            || !Enum.IsDefined(kind))
        {
            throw new InvalidDataException(
                $"demo dataset: '{entry.Dn}' has unknown kind '{entry.Kind}'.");
        }

        var attributes = entry.Attributes ?? [];
        var duplicateKey = attributes.Keys
            .GroupBy(k => k, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(g => g.Count() > 1);
        if (duplicateKey is not null)
        {
            throw new InvalidDataException(
                $"demo dataset: '{entry.Dn}' has case-duplicate attribute key '{duplicateKey.Key}'.");
        }

        return new AdObject
        {
            Dn = entry.Dn,
            Kind = kind,
            Name = entry.Name,
            SamAccountName = entry.SamAccountName,
            Attributes = attributes,
        };
    }

    private static bool IsGroup(AdObjectKind kind) =>
        kind is AdObjectKind.GlobalGroup or AdObjectKind.DomainLocalGroup or AdObjectKind.UniversalGroup;

    // Escape-aware ancestry via DnPath (#29): a textual ",{baseDn}" suffix is NOT
    // enough — an escaped comma (\,) inside an RDN value never separates.
    private static bool IsInScope(string dn, string baseDn) =>
        DnPath.RelativeDepth(dn, baseDn) >= 0;

    private static AdObject MakeExternal(string dn) => new()
    {
        Dn = dn,
        Kind = AdObjectKind.External,
        Name = dn,
    };

    private sealed record DemoDataset(
        string Domain,
        string RootDn,
        IReadOnlyDictionary<string, AdObject> Objects,
        IReadOnlyDictionary<string, IReadOnlyList<string>> Members,
        int GroupCount);

    private sealed class DatasetDto
    {
        public string? Domain { get; set; }

        public string? RootDn { get; set; }

        public List<ObjectDto>? Objects { get; set; }
    }

    private sealed class ObjectDto
    {
        public string? Dn { get; set; }

        public string? Kind { get; set; }

        public string? Name { get; set; }

        public string? SamAccountName { get; set; }

        public Dictionary<string, string>? Attributes { get; set; }

        public List<string>? Members { get; set; }
    }
}
