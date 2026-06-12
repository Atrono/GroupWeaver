using GroupWeaver.Core.Model;
using GroupWeaver.Core.Providers;

namespace GroupWeaver.App.Tests.Fakes;

/// <summary>
/// Connect-step stub for the shell-navigation tests (AP 2.1 S4): <see cref="ConnectAsync"/>
/// returns whatever task the test injects — completed (success), faulted (error policy),
/// or a <see cref="TaskCompletionSource{TResult}"/>-driven task that stays in flight until
/// the test releases it. The shared <c>FakeDirectoryProvider</c> in GroupWeaver.Tests is
/// deliberately not reused: it is internal to that assembly and can neither fault nor keep
/// a connect attempt open. Every other member throws — the Connect step owns only
/// <see cref="ConnectAsync"/>, and a loud failure pins that.
/// </summary>
internal sealed class StubDirectoryProvider(Task<DirectoryConnection> connectResult) : IDirectoryProvider
{
    /// <summary>Number of <see cref="ConnectAsync"/> invocations observed.</summary>
    public int ConnectCalls { get; private set; }

    public Task<DirectoryConnection> ConnectAsync(CancellationToken cancellationToken = default)
    {
        ConnectCalls++;
        return connectResult;
    }

    public Task<IReadOnlyList<AdObject>> GetRootCandidatesAsync(
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("The Connect step must not call GetRootCandidatesAsync.");

    public Task<DirectorySnapshot> LoadScopeAsync(
        string baseDn, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("The Connect step must not call LoadScopeAsync.");

    public Task<AdObject?> GetObjectAsync(string dn, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("The Connect step must not call GetObjectAsync.");

    public Task<IReadOnlyList<AdObject>> GetMembersAsync(
        string groupDn, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("The Connect step must not call GetMembersAsync.");
}
