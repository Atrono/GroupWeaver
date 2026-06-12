using GroupWeaver.App.Graph;
using GroupWeaver.App.Tests.Fakes;
using GroupWeaver.Core.Graph;
using GroupWeaver.Core.Model;

using Xunit;

namespace GroupWeaver.App.Tests.Graph;

/// <summary>
/// Pins the AP 2.3 growth of the renderer seam (ADR-005 D2):
/// <c>Task UpdateGraphAsync(GraphModel, CancellationToken = default)</c> and
/// <c>Task FocusAsync(IReadOnlyCollection&lt;string&gt;, CancellationToken = default)</c>
/// on <see cref="IGraphRenderer"/>. Every call goes through an
/// <see cref="IGraphRenderer"/>-TYPED reference, so these tests are a compile-time
/// pin of the exact interface signatures (including the optional-token defaults,
/// which only bind through the interface declaration) plus a runtime pin of the
/// <see cref="FakeGraphRenderer"/> recording channels the workspace VM tests build
/// on. The production <c>CytoscapeGraphRenderer</c> is WebView-bound and has no
/// unit-testable surface — its JS half is covered by the Playwright harness
/// (<c>tests/graph-bundle</c>, ADR-004 D6).
/// </summary>
public sealed class GraphRendererSeamTests
{
    // --- UpdateGraphAsync (replace-in-place, ADR-005 D1/D2) ---------------------------

    [Fact]
    public async Task UpdateGraphAsync_ThroughTheSeam_RecordsModelAndToken_OnItsOwnChannel()
    {
        var fake = new FakeGraphRenderer();
        IGraphRenderer renderer = fake;
        var model = OneNodeModel();
        using var cts = new CancellationTokenSource();

        await renderer.UpdateGraphAsync(model, cts.Token);

        Assert.Same(model, Assert.Single(fake.UpdatedGraphs));
        Assert.Equal(cts.Token, Assert.Single(fake.UpdateGraphTokens));

        // Update is NOT a show (different post-conditions, ADR-005): the channels
        // must stay separate so VM tests can tell which path was taken.
        Assert.Empty(fake.ShownGraphs);
    }

    [Fact]
    public void UpdateGraphAsync_CancellationTokenIsOptional()
    {
        var fake = new FakeGraphRenderer();
        IGraphRenderer renderer = fake;

        // Compiles only if the INTERFACE declares `CancellationToken = default`.
        var task = renderer.UpdateGraphAsync(OneNodeModel());

        Assert.True(task.IsCompletedSuccessfully);
        Assert.Equal(CancellationToken.None, Assert.Single(fake.UpdateGraphTokens));
    }

    [Fact]
    public void UpdateGraphAsync_ReturnsTheInjectedTaskUnwrapped()
    {
        var fake = new FakeGraphRenderer();
        var pending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fake.UpdateGraphResult = pending.Task;
        IGraphRenderer renderer = fake;

        var task = renderer.UpdateGraphAsync(OneNodeModel());

        // Injectable result: tests control completion (never-completing/faulted/late).
        Assert.Same(pending.Task, task);
        Assert.False(task.IsCompleted);
        pending.SetResult();
        Assert.True(task.IsCompletedSuccessfully);
    }

    // --- FocusAsync (camera move + focused confirmation, ADR-005 D2) ------------------

    [Fact]
    public async Task FocusAsync_ThroughTheSeam_RecordsTheDnCollectionAndToken()
    {
        var fake = new FakeGraphRenderer();
        IGraphRenderer renderer = fake;

        // Comma-containing DNs verbatim — the ids the VM sends (parent + members).
        string[] dns =
        [
            "CN=GG_Sales,OU=Groups,OU=AGDLP-Demo,DC=weavedemo,DC=example",
            "CN=Ada Lovelace,OU=People,OU=AGDLP-Demo,DC=weavedemo,DC=example",
        ];
        using var cts = new CancellationTokenSource();

        await renderer.FocusAsync(dns, cts.Token);

        Assert.Same(dns, Assert.Single(fake.FocusCalls));
        Assert.Equal(cts.Token, Assert.Single(fake.FocusTokens));
    }

    [Fact]
    public void FocusAsync_CancellationTokenIsOptional()
    {
        var fake = new FakeGraphRenderer();
        IGraphRenderer renderer = fake;

        // Compiles only if the INTERFACE declares `CancellationToken = default`.
        var task = renderer.FocusAsync(["CN=GG_Sales,OU=X,DC=x"]);

        Assert.True(task.IsCompletedSuccessfully);
        Assert.Equal(CancellationToken.None, Assert.Single(fake.FocusTokens));
        var dns = Assert.Single(fake.FocusCalls);
        Assert.Equal("CN=GG_Sales,OU=X,DC=x", Assert.Single(dns));
    }

    [Fact]
    public void FocusAsync_ReturnsTheInjectedTaskUnwrapped()
    {
        var fake = new FakeGraphRenderer();
        var pending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fake.FocusResult = pending.Task;
        IGraphRenderer renderer = fake;

        var task = renderer.FocusAsync(["CN=GG_Sales,OU=X,DC=x"]);

        Assert.Same(pending.Task, task);
        Assert.False(task.IsCompleted);
        pending.SetResult();
        Assert.True(task.IsCompletedSuccessfully);
    }

    // --- helpers -----------------------------------------------------------------------

    private static GraphModel OneNodeModel() =>
        new(
            [new GraphNode("CN=N0,OU=X,DC=x", "N0", AdObjectKind.User, X: 0, Y: 0, Ring: 1, IsRoot: false)],
            []);
}
