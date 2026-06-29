using System.DirectoryServices;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using GroupWeaver.Core.Model;
using GroupWeaver.Core.Providers;

namespace GroupWeaver.Providers;

/// <summary>
/// Strictly read-only <see cref="IDirectoryProvider"/> over ADSI
/// (<see cref="DirectorySearcher"/> with Integrated Windows Auth, no credentials
/// in code). Every operation opens fresh, disposed handles; searches are paged
/// (RFC 2696) and load only <see cref="AttributeWhitelist.FetchProperties"/> —
/// nothing else ever enters process memory. There is no write path: no property
/// assignments, no <c>CommitChanges</c>, no <c>Invoke</c> (project rule #1).
/// Error model per <see cref="IDirectoryProvider"/>: unresolvable DNs are values,
/// only unreachable/bind failure throws <see cref="DirectoryUnavailableException"/>;
/// conservatively, any unexpected non-COM failure from the ADSI plumbing is also
/// surfaced as <see cref="DirectoryUnavailableException"/> — an unrecognized error
/// must never silently become "object absent".
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class LdapProvider : IDirectoryProvider
{
    private const string AnyObjectFilter = "(objectClass=*)";

    private readonly string? _server;
    private readonly string? _baseDn;
    private readonly int _pageSize;
    private readonly object _baseDnLock = new();
    private string? _defaultNamingContext;

    /// <summary>
    /// Creates a provider for the given server and search base.
    /// </summary>
    /// <param name="server">Host to bind to; <c>null</c> uses serverless binding
    /// (the joined domain's locator picks a DC).</param>
    /// <param name="baseDn">Search base for <see cref="ConnectAsync"/> and
    /// <see cref="GetRootCandidatesAsync"/>; <c>null</c> reads
    /// <c>defaultNamingContext</c> from RootDSE on first use (cached).</param>
    /// <param name="pageSize">Server-side page size, 1..1000 (AD's default
    /// MaxPageSize silently truncates unpaged result sets).</param>
    public LdapProvider(string? server = null, string? baseDn = null, int pageSize = 500)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(pageSize, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(pageSize, 1000);
        _server = server;
        _baseDn = baseDn;
        _pageSize = pageSize;
    }

    /// <inheritdoc />
    public Task<DirectoryConnection> ConnectAsync(CancellationToken cancellationToken = default) =>
        RunAsync("connect", cancellationToken, () =>
        {
            try
            {
                using var rootDse = new DirectoryEntry(RootDsePath());
                rootDse.RefreshCache(); // forces the actual bind
            }
            catch (Exception ex)
            {
                throw new DirectoryUnavailableException(
                    $"cannot bind to '{RootDsePath()}': {ex.Message}", ex);
            }

            string baseDn = EffectiveBaseDn();
            int groupCount = 0;
            using (var root = new DirectoryEntry(PathFor(baseDn)))
            using (var searcher = CreateSearcher(
                root, SearchScope.Subtree, "(objectCategory=group)", ["distinguishedName"]))
            using (SearchResultCollection results = searcher.FindAll())
            {
                foreach (SearchResult _ in results)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    groupCount++;
                }
            }

            return new DirectoryConnection(
                $"LDAP {_server ?? "default domain"} — {baseDn}", groupCount);
        });

    /// <inheritdoc />
    public Task<IReadOnlyList<AdObject>> GetRootCandidatesAsync(
        CancellationToken cancellationToken = default) =>
        RunAsync<IReadOnlyList<AdObject>>(
            "enumerate root candidates", cancellationToken, () =>
        {
            string baseDn = EffectiveBaseDn();

            // ADR-031 D3: the domain root is a first-class scope candidate, synthesized from
            // the effective base DN and prepended to the OU+group candidates. LoadScopeAsync
            // already accepts any baseDn (incl. the domain DN), so this is a candidate-LIST
            // addition only — no load-path change. Modeled as a container (OrganizationalUnit
            // kind) so it reads as a scope, not a group, and passes the picker's kind filter.
            var candidates = new List<AdObject>
            {
                new()
                {
                    Dn = baseDn,
                    Kind = AdObjectKind.OrganizationalUnit,
                    Name = $"Whole domain ({baseDn})",
                },
            };

            using var root = new DirectoryEntry(PathFor(baseDn));
            using var searcher = CreateSearcher(
                root,
                SearchScope.Subtree,
                "(|(objectClass=organizationalUnit)(objectClass=group))",
                AttributeWhitelist.FetchProperties);
            using SearchResultCollection results = searcher.FindAll();
            foreach (SearchResult result in results)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var obj = LdapEntry.Map(ToLdapEntry(result));
                if (obj.Kind == AdObjectKind.OrganizationalUnit || IsGroupKind(obj.Kind))
                {
                    candidates.Add(obj);
                }
            }

            return candidates;
        });

    /// <inheritdoc />
    public Task<DirectorySnapshot> LoadScopeAsync(
        string baseDn, CancellationToken cancellationToken = default) =>
        RunAsync($"load scope '{baseDn}'", cancellationToken, () =>
        {
            var snapshot = new DirectorySnapshot();
            if (FindEntry(baseDn) is null)
            {
                // Unknown/vanished/garbage base → empty snapshot, the documented
                // value. After this successful probe, a NotFound from the paged
                // search itself is unexpected and surfaces as
                // DirectoryUnavailableException — never as an empty result.
                return snapshot;
            }

            using var root = new DirectoryEntry(PathFor(baseDn));
            using var searcher = CreateSearcher(
                root, SearchScope.Subtree, AnyObjectFilter, AttributeWhitelist.FetchProperties);
            using SearchResultCollection results = searcher.FindAll();
            foreach (SearchResult result in results)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var entry = ToLdapEntry(result);
                var obj = LdapEntry.Map(entry);
                snapshot.AddObject(obj);
                if (IsGroupKind(obj.Kind))
                {
                    try
                    {
                        // Every in-scope group is marked loaded — including empty ones
                        // (which return no member key at all, → empty list).
                        snapshot.SetMembers(obj.Dn, CollectMembers(obj.Dn, entry.Properties, cancellationToken));
                    }
                    catch (COMException ex) when (
                        LdapErrors.ClassifyHResult(ex.HResult) == LdapErrorKind.NotFound)
                    {
                        // Group vanished between the paged search and its ranged
                        // member fetch: leave its members UNLOADED — null = "never
                        // loaded" is the honest value (data-model rule: null ≠ empty;
                        // an empty list would claim "loaded and genuinely empty").
                    }
                }
            }

            return snapshot;
        });

    /// <inheritdoc />
    public Task<AdObject?> GetObjectAsync(string dn, CancellationToken cancellationToken = default) =>
        RunAsync<AdObject?>($"get object '{dn}'", cancellationToken, () =>
            FindEntry(dn) is { } entry ? LdapEntry.Map(entry) : null);

    /// <inheritdoc />
    public Task<IReadOnlyList<AdObject>> GetMembersAsync(
        string groupDn, CancellationToken cancellationToken = default) =>
        RunAsync<IReadOnlyList<AdObject>>(
            $"get members of '{groupDn}'", cancellationToken, () =>
        {
            if (FindEntry(groupDn) is not { } group)
            {
                return []; // unknown/vanished parent → empty list, never an exception
            }

            IReadOnlyList<string> memberDns;
            try
            {
                memberDns = CollectMembers(groupDn, group.Properties, cancellationToken);
            }
            catch (COMException ex) when (
                LdapErrors.ClassifyHResult(ex.HResult) == LdapErrorKind.NotFound)
            {
                // Group vanished between FindEntry and the ranged follow-up fetch
                // → the documented vanished-parent value.
                return [];
            }

            var members = new List<AdObject>(memberDns.Count);
            foreach (string memberDn in memberDns)
            {
                cancellationToken.ThrowIfCancellationRequested();
                members.Add(FindEntry(memberDn) is { } member
                    ? LdapEntry.Map(member)
                    : MakeExternal(memberDn));
            }

            return members;
        });

    /// <summary>
    /// Wraps a synchronous ADSI body: runs it on the thread pool and maps failures
    /// onto the provider error model. Every COM failure — and, conservatively, any
    /// unexpected non-COM failure from the ADSI plumbing — becomes
    /// <see cref="DirectoryUnavailableException"/>; the expected
    /// <see cref="LdapErrorKind.NotFound"/> cases are handled where they arise
    /// (the body knows which value the contract demands), never wholesale.
    /// Cancellation flows through.
    /// </summary>
    private static Task<T> RunAsync<T>(
        string operation, CancellationToken cancellationToken, Func<T> body) =>
        Task.Run(
            () =>
            {
                try
                {
                    return body();
                }
                catch (COMException ex)
                {
                    throw new DirectoryUnavailableException(
                        $"directory unavailable ({operation}): {ex.Message}", ex);
                }
                catch (Exception ex) when (
                    ex is not OperationCanceledException and not DirectoryUnavailableException)
                {
                    throw new DirectoryUnavailableException(
                        $"directory unavailable ({operation}): {ex.Message}", ex);
                }
            },
            cancellationToken);

    /// <summary>
    /// Base-scope read of one object with the full fetch whitelist.
    /// <c>null</c> when the DN does not resolve (no result, nonexistent object,
    /// or garbage pathname); connectivity failures propagate as COM exceptions
    /// for <see cref="RunAsync"/> to classify.
    /// </summary>
    private LdapEntry? FindEntry(string dn)
    {
        try
        {
            using var entry = new DirectoryEntry(PathFor(dn));
            using var searcher = CreateSearcher(
                entry, SearchScope.Base, AnyObjectFilter, AttributeWhitelist.FetchProperties);
            using SearchResultCollection results = searcher.FindAll();
            foreach (SearchResult result in results)
            {
                return ToLdapEntry(result);
            }

            return null;
        }
        catch (COMException ex) when (
            LdapErrors.ClassifyHResult(ex.HResult) == LdapErrorKind.NotFound)
        {
            return null;
        }
    }

    /// <summary>
    /// Full member DN list of one group via <see cref="MemberCollector"/>; each
    /// ranged follow-up is a fresh base-scope read requesting only the range
    /// attribute, and the returned attribute name is found by its <c>member</c>
    /// prefix (AD answers e.g. <c>member;range=1500-2999</c>).
    /// </summary>
    private IReadOnlyList<string> CollectMembers(
        string groupDn,
        IReadOnlyDictionary<string, IReadOnlyList<string>> initialProperties,
        CancellationToken cancellationToken) =>
        MemberCollector.CollectAllMembers(groupDn, initialProperties, rangeAttribute =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var entry = new DirectoryEntry(PathFor(groupDn));
            using var searcher = CreateSearcher(
                entry, SearchScope.Base, AnyObjectFilter, [rangeAttribute]);
            using SearchResultCollection results = searcher.FindAll();
            foreach (SearchResult result in results)
            {
                foreach (string name in result.Properties.PropertyNames.Cast<string>())
                {
                    if (name.StartsWith("member", StringComparison.OrdinalIgnoreCase))
                    {
                        return (name, StringifyValues(result.Properties[name]));
                    }
                }
            }

            return (rangeAttribute, []); // no result/values left → collector terminates
        });

    private string EffectiveBaseDn()
    {
        if (_baseDn is not null)
        {
            return _baseDn;
        }

        lock (_baseDnLock)
        {
            return _defaultNamingContext ??= ReadDefaultNamingContext();
        }
    }

    private string ReadDefaultNamingContext()
    {
        using var rootDse = new DirectoryEntry(RootDsePath());
        return rootDse.Properties["defaultNamingContext"].Value as string
            ?? throw new DirectoryUnavailableException(
                $"'{RootDsePath()}' did not expose defaultNamingContext.");
    }

    private string RootDsePath() =>
        _server is null ? "LDAP://RootDSE" : $"LDAP://{_server}/RootDSE";

    private string PathFor(string dn) => AdsPath.For(_server, dn);

    private DirectorySearcher CreateSearcher(
        DirectoryEntry searchRoot, SearchScope scope, string filter, IEnumerable<string> propertiesToLoad)
    {
        var searcher = new DirectorySearcher(searchRoot)
        {
            Filter = filter,
            SearchScope = scope,
            PageSize = _pageSize,
        };
        searcher.PropertiesToLoad.AddRange(propertiesToLoad.ToArray());
        return searcher;
    }

    /// <summary>
    /// Shim from <see cref="SearchResult"/> to the pure <see cref="LdapEntry"/>:
    /// property names as returned (lowercased by AD; <see cref="LdapEntry"/> is
    /// case-insensitive), values stringified invariantly. Binary values cannot
    /// occur — nothing outside <see cref="AttributeWhitelist.FetchProperties"/>
    /// is ever requested.
    /// </summary>
    private static LdapEntry ToLdapEntry(SearchResult result)
    {
        var properties = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (string name in result.Properties.PropertyNames.Cast<string>())
        {
            properties[name] = StringifyValues(result.Properties[name]);
        }

        if (!properties.TryGetValue("distinguishedName", out var dns) || dns.Count == 0)
        {
            throw new InvalidDataException($"search result without a distinguishedName: '{result.Path}'.");
        }

        return new LdapEntry(dns[0], properties);
    }

    private static IReadOnlyList<string> StringifyValues(ResultPropertyValueCollection values)
    {
        var list = new List<string>(values.Count);
        foreach (object value in values)
        {
            list.Add(Stringify(value));
        }

        return list;
    }

    private static string Stringify(object value) => value switch
    {
        string text => text,
        // ADSI surfaces generalized-time attributes (whenCreated) as local DateTime;
        // emit invariant UTC so LdapEntry.Map's AssumeUniversal parse round-trips.
        DateTime dateTime => dateTime.ToUniversalTime()
            .ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? string.Empty,
    };

    private static bool IsGroupKind(AdObjectKind kind) =>
        kind is AdObjectKind.GlobalGroup or AdObjectKind.DomainLocalGroup or AdObjectKind.UniversalGroup;

    private static AdObject MakeExternal(string dn) => new()
    {
        Dn = dn,
        Kind = AdObjectKind.External,
        Name = dn,
    };
}
