using GroupWeaver.Core.Model;

namespace GroupWeaver.Core.Providers;

/// <summary>
/// Read-only directory access. Implementations must never write.
/// Error model: directory unreachable / bind failure throws
/// <see cref="DirectoryUnavailableException"/>; unresolvable DNs are values
/// (<c>null</c> / <see cref="AdObjectKind.External"/> / empty list), never exceptions.
/// </summary>
public interface IDirectoryProvider
{
    /// <summary>Connectivity probe + summary; caller logs "connected, X groups loaded" (M1 DoD).</summary>
    Task<DirectoryConnection> ConnectAsync(CancellationToken cancellationToken = default);

    /// <summary>OUs and groups offered by the entry-filter picker (AP 2.1).</summary>
    Task<IReadOnlyList<AdObject>> GetRootCandidatesAsync(CancellationToken cancellationToken = default);

    /// <summary>Eagerly loads the subtree under <paramref name="baseDn"/> (paged) into a snapshot:
    /// objects + membership edges; every group found in scope is marked members-loaded
    /// (rare exception: a group vanishing mid-load stays unloaded — <c>null</c>,
    /// never a fabricated empty list).</summary>
    Task<DirectorySnapshot> LoadScopeAsync(string baseDn, CancellationToken cancellationToken = default);

    /// <summary>Single object by DN; <c>null</c> if unresolvable (not an exception).</summary>
    Task<AdObject?> GetObjectAsync(string dn, CancellationToken cancellationToken = default);

    /// <summary>One level of direct members for lazy expand beyond the loaded scope.
    /// Unresolvable children come back as <see cref="AdObjectKind.External"/>;
    /// unknown/vanished parent → empty list.</summary>
    Task<IReadOnlyList<AdObject>> GetMembersAsync(string groupDn, CancellationToken cancellationToken = default);
}
