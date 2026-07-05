using GroupWeaver.App.Graph;
using GroupWeaver.App.Tests.Fakes;
using GroupWeaver.Core.Graph;
using GroupWeaver.Core.Model;

using Xunit;

namespace GroupWeaver.App.Tests.Graph;

/// <summary>
/// Pins the AP 2.3 growth of the renderer seam (ADR-005 D2):
/// <c>Task UpdateGraphAsync(GraphModel, CancellationToken = default)</c>,
/// <c>Task FocusAsync(IReadOnlyCollection&lt;string&gt;, CancellationToken = default)</c>, and
/// (ADR-038 D3.2, WP6, #245) <c>Task&lt;GraphStateReport?&gt; ProbeStateAsync(CancellationToken =
/// default)</c> on <see cref="IGraphRenderer"/>. Every call goes through an
/// <see cref="IGraphRenderer"/>-TYPED reference, so these tests are a compile-time
/// pin of the exact interface signatures (including the optional-token defaults,
/// which only bind through the interface declaration) plus a runtime pin of the
/// <see cref="FakeGraphRenderer"/> recording channels the workspace VM tests build
/// on. The production <c>CytoscapeGraphRenderer</c> is WebView-bound and has no
/// unit-testable surface — its JS half is covered by the Playwright harness
/// (<c>tests/graph-bundle</c>, ADR-004 D6).
///
/// <para><b>ProbeStateAsync note:</b> the <c>--e2e</c> channel's <c>state</c> command DOES call
/// it now (the WP6 test-engineer finding this comment used to document — fixed):
/// <c>E2eChannel.EmitStateReplyAsync</c> merges its <c>zoom</c>/<c>panX</c>/<c>panY</c>/
/// <c>animated</c> page truth into the reply alongside the VM-level fields
/// <c>ShellViewModel</c>/<c>WorkspaceViewModel</c> still supply directly (see
/// <c>E2eChannelStateProbeMergeTests</c> for that merge, in-process over a
/// <see cref="FakeGraphRenderer"/>; <c>E2eChannelCliTests</c> pins the wire framing at the
/// process level but never advances past PickRoot, so it never observes a live renderer).
/// These tests still pin the SEAM itself (interface shape + fake recording) at the unit
/// level, the way every other renderer method is pinned here.</para>
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

    // --- ProbeStateAsync (the --e2e page-truth probe, ADR-038 D3.2 / WP6, #245) ------

    [Fact]
    public async Task ProbeStateAsync_ThroughTheSeam_RecordsTheTokenOnItsOwnChannel()
    {
        var fake = new FakeGraphRenderer();
        IGraphRenderer renderer = fake;
        using var cts = new CancellationTokenSource();

        await renderer.ProbeStateAsync(cts.Token);

        Assert.Equal(1, fake.ProbeStateCalls);
        Assert.Equal(cts.Token, Assert.Single(fake.ProbeStateTokens));

        // Its OWN channel — never conflated with Focus/UpdateGraph (different post-conditions).
        Assert.Empty(fake.FocusCalls);
        Assert.Empty(fake.UpdatedGraphs);
    }

    [Fact]
    public async Task ProbeStateAsync_CancellationTokenIsOptional()
    {
        var fake = new FakeGraphRenderer();
        IGraphRenderer renderer = fake;

        // Compiles only if the INTERFACE declares `CancellationToken = default`.
        var report = await renderer.ProbeStateAsync();

        Assert.Equal(CancellationToken.None, Assert.Single(fake.ProbeStateTokens));
        Assert.NotNull(report);
    }

    [Fact]
    public async Task ProbeStateAsync_ReturnsTheInjectedResult_ScalarsOnly()
    {
        var fake = new FakeGraphRenderer
        {
            ProbeStateResult = Task.FromResult<GraphStateReport?>(
                new GraphStateReport(196, 337, 1.5, -12.25, 40, "CN=GG_Sales,OU=X,DC=x", true)),
        };
        IGraphRenderer renderer = fake;

        var report = await renderer.ProbeStateAsync();

        Assert.NotNull(report);
        Assert.Equal(196, report!.Nodes);
        Assert.Equal(337, report.Edges);
        Assert.Equal(1.5, report.Zoom);
        Assert.Equal(-12.25, report.PanX);
        Assert.Equal(40, report.PanY);
        Assert.Equal("CN=GG_Sales,OU=X,DC=x", report.Selected);
        Assert.True(report.Animated);
    }

    /// <summary>The never-throw renderer contract (mirrors <see cref="IGraphRenderer.ExportPngAsync"/>):
    /// <c>null</c> models the real <c>CytoscapeGraphRenderer</c>'s timeout/error path — callers
    /// must treat it as "no page truth available" rather than a fault.</summary>
    [Fact]
    public async Task ProbeStateAsync_NullResult_ModelsTheTimeoutOrErrorPath()
    {
        var fake = new FakeGraphRenderer { ProbeStateResult = Task.FromResult<GraphStateReport?>(null) };
        IGraphRenderer renderer = fake;

        var report = await renderer.ProbeStateAsync();

        Assert.Null(report);
    }

    // --- helpers -----------------------------------------------------------------------

    private static GraphModel OneNodeModel() =>
        new(
            [new GraphNode("CN=N0,OU=X,DC=x", "N0", AdObjectKind.User, X: 0, Y: 0, Ring: 1, IsRoot: false)],
            []);
}
