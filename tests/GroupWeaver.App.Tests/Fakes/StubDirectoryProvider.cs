using GroupWeaver.Core.Model;
using GroupWeaver.Core.Providers;

namespace GroupWeaver.App.Tests.Fakes;

/// <summary>
/// Shell-flow stub for the AP 2.1 step ViewModels (S4 Connect, S6 PickRoot):
/// <see cref="ConnectAsync"/> and <see cref="GetRootCandidatesAsync"/> return whatever
/// task the test injects — completed (success), faulted (error policy), or a
/// <see cref="TaskCompletionSource{TResult}"/>-driven task that stays in flight until the
/// test releases it. <see cref="RootCandidatesResult"/> defaults to an empty list because
/// <c>RootPickerViewModel</c> starts its candidate load in the constructor: a throwing
/// default would leave an unobserved faulted <c>LoadCandidates</c> task behind every
/// Connect-step test that merely advances past Connect. The shared
/// <c>FakeDirectoryProvider</c> in GroupWeaver.Tests is deliberately not reused: it is
/// internal to that assembly and can neither fault nor keep a call open. Every member
/// the shell steps do not own throws — a loud failure pins the step/provider contract.
/// </summary>
internal sealed class StubDirectoryProvider(Task<DirectoryConnection> connectResult) : IDirectoryProvider
{
    /// <summary>Number of <see cref="ConnectAsync"/> invocations observed.</summary>
    public int ConnectCalls { get; private set; }

    /// <summary>Number of <see cref="GetRootCandidatesAsync"/> invocations observed.</summary>
    public int RootCandidatesCalls { get; private set; }

    /// <summary>
    /// Task handed to the PickRoot step; replace it to inject candidates, a fault, or an
    /// in-flight TCS task. Default: completed empty list (a valid "no candidates" answer).
    /// </summary>
    public Task<IReadOnlyList<AdObject>> RootCandidatesResult { get; set; } =
        Task.FromResult<IReadOnlyList<AdObject>>([]);

    public Task<DirectoryConnection> ConnectAsync(CancellationToken cancellationToken = default)
    {
        ConnectCalls++;
        return connectResult;
    }

    public Task<IReadOnlyList<AdObject>> GetRootCandidatesAsync(
        CancellationToken cancellationToken = default)
    {
        RootCandidatesCalls++;
        return RootCandidatesResult;
    }

    public Task<DirectorySnapshot> LoadScopeAsync(
        string baseDn, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("No AP 2.1 shell step may call LoadScopeAsync (scope loading is AP 2.2).");

    public Task<AdObject?> GetObjectAsync(string dn, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("No AP 2.1 shell step may call GetObjectAsync.");

    public Task<IReadOnlyList<AdObject>> GetMembersAsync(
        string groupDn, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("No AP 2.1 shell step may call GetMembersAsync.");
}
