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
/// For the AP 2.3 lazy-expand pipeline, <see cref="GetObjectAsync"/> and
/// <see cref="GetMembersAsync"/> are served by the injectable
/// <see cref="GetObjectHandler"/>/<see cref="GetMembersHandler"/> delegates; while a
/// handler is NOT injected the call keeps THROWING — a loud failure pins that no step
/// fetches what it does not own (e.g. the focus-only expand paths must stay offline).
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

    /// <summary>Number of <see cref="GetObjectAsync"/> invocations observed (recorded
    /// even when the call throws because no handler was injected).</summary>
    public int GetObjectCalls { get; private set; }

    /// <summary>DN of every <see cref="GetObjectAsync"/> call, in call order.</summary>
    public List<string> GetObjectDns { get; } = [];

    /// <summary>Cancellation token observed by the most recent <see cref="GetObjectAsync"/> call.</summary>
    public CancellationToken GetObjectToken { get; private set; }

    /// <summary>
    /// Serves <see cref="GetObjectAsync"/> (AP 2.3 expand: frontier-DN resolution).
    /// <c>null</c> (default) keeps the call throwing <see cref="NotSupportedException"/>.
    /// </summary>
    public Func<string, CancellationToken, Task<AdObject?>>? GetObjectHandler { get; set; }

    /// <summary>Number of <see cref="GetMembersAsync"/> invocations observed (recorded
    /// even when the call throws because no handler was injected).</summary>
    public int GetMembersCalls { get; private set; }

    /// <summary>Group DN of every <see cref="GetMembersAsync"/> call, in call order.</summary>
    public List<string> GetMembersDns { get; } = [];

    /// <summary>Cancellation token observed by the most recent <see cref="GetMembersAsync"/>
    /// call — the Dispose-mid-expand test asserts it gets cancelled (AP 2.3).</summary>
    public CancellationToken GetMembersToken { get; private set; }

    /// <summary>
    /// Serves <see cref="GetMembersAsync"/> (AP 2.3 expand: one-level member fetch).
    /// <c>null</c> (default) keeps the call throwing <see cref="NotSupportedException"/>.
    /// </summary>
    public Func<string, CancellationToken, Task<IReadOnlyList<AdObject>>>? GetMembersHandler { get; set; }

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

    public Task<AdObject?> GetObjectAsync(string dn, CancellationToken cancellationToken = default)
    {
        GetObjectCalls++;
        GetObjectDns.Add(dn);
        GetObjectToken = cancellationToken;
        return GetObjectHandler?.Invoke(dn, cancellationToken)
            ?? throw new NotSupportedException(
                "GetObjectAsync was not injected — only the AP 2.3 expand fetch path may call it.");
    }

    public Task<IReadOnlyList<AdObject>> GetMembersAsync(
        string groupDn, CancellationToken cancellationToken = default)
    {
        GetMembersCalls++;
        GetMembersDns.Add(groupDn);
        GetMembersToken = cancellationToken;
        return GetMembersHandler?.Invoke(groupDn, cancellationToken)
            ?? throw new NotSupportedException(
                "GetMembersAsync was not injected — only the AP 2.3 expand fetch path may call it.");
    }
}
