using GroupWeaver.Core.Model;
using GroupWeaver.Core.Providers;

namespace GroupWeaver.App.Tests.Fakes;

/// <summary>
/// Shell-flow stub for the step ViewModels (AP 2.1 S4 Connect, S6 PickRoot; AP 2.2 S6
/// Workspace scope load): <see cref="ConnectAsync"/>, <see cref="GetRootCandidatesAsync"/>
/// and <see cref="LoadScopeAsync"/> return whatever task the test injects — completed
/// (success), faulted (error policy), or a <see cref="TaskCompletionSource{TResult}"/>-driven
/// task that stays in flight until the test releases it. <see cref="RootCandidatesResult"/>
/// defaults to an empty list and <see cref="LoadScopeResult"/> to an empty snapshot
/// because the picker/workspace start their loads in the constructor: a throwing default
/// would leave an unobserved faulted task behind every test that merely advances past
/// the step. The shared <c>FakeDirectoryProvider</c> in GroupWeaver.Tests is deliberately
/// not reused: it is internal to that assembly and can neither fault nor keep a call open.
/// Every member the shell steps do not own throws — a loud failure pins the step/provider
/// contract.
/// </summary>
internal sealed class StubDirectoryProvider(Task<DirectoryConnection> connectResult) : IDirectoryProvider
{
    /// <summary>Number of <see cref="ConnectAsync"/> invocations observed.</summary>
    public int ConnectCalls { get; private set; }

    /// <summary>Number of <see cref="GetRootCandidatesAsync"/> invocations observed.</summary>
    public int RootCandidatesCalls { get; private set; }

    /// <summary>Number of <see cref="LoadScopeAsync"/> invocations observed.</summary>
    public int LoadScopeCalls { get; private set; }

    /// <summary>Base DN of the most recent <see cref="LoadScopeAsync"/> call.</summary>
    public string? LoadScopeBaseDn { get; private set; }

    /// <summary>Cancellation token observed by the most recent <see cref="LoadScopeAsync"/>
    /// call — the workspace Dispose test asserts it gets cancelled (AP 2.2 S6).</summary>
    public CancellationToken LoadScopeToken { get; private set; }

    /// <summary>
    /// Task handed to the PickRoot step; replace it to inject candidates, a fault, or an
    /// in-flight TCS task. Default: completed empty list (a valid "no candidates" answer).
    /// </summary>
    public Task<IReadOnlyList<AdObject>> RootCandidatesResult { get; set; } =
        Task.FromResult<IReadOnlyList<AdObject>>([]);

    /// <summary>
    /// Task handed to the Workspace step's scope load (AP 2.2 S6); replace it to inject
    /// a snapshot, a fault, or an in-flight TCS task. Default: completed EMPTY snapshot
    /// (a valid "nothing in scope" answer that keeps pass-through tests load-complete).
    /// </summary>
    public Task<DirectorySnapshot> LoadScopeResult { get; set; } =
        Task.FromResult(new DirectorySnapshot());

    /// <summary>
    /// When set, wins over <see cref="LoadScopeResult"/>: builds the result task from the
    /// observed token, so TCS-gated tests can e.g. complete-as-cancelled on cancellation.
    /// </summary>
    public Func<CancellationToken, Task<DirectorySnapshot>>? LoadScopeOverride { get; set; }

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
        string baseDn, CancellationToken cancellationToken = default)
    {
        LoadScopeCalls++;
        LoadScopeBaseDn = baseDn;
        LoadScopeToken = cancellationToken;
        return LoadScopeOverride?.Invoke(cancellationToken) ?? LoadScopeResult;
    }

    public Task<AdObject?> GetObjectAsync(string dn, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("No AP 2.1 shell step may call GetObjectAsync.");

    public Task<IReadOnlyList<AdObject>> GetMembersAsync(
        string groupDn, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("No AP 2.1 shell step may call GetMembersAsync.");
}
