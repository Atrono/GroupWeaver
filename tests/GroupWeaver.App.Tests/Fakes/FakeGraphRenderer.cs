using Avalonia.Controls;

using GroupWeaver.App.Graph;
using GroupWeaver.Core.Graph;

namespace GroupWeaver.App.Tests.Fakes;

/// <summary>
/// Renderer-seam fake for the AP 2.2 S6 workspace load flow (ADR-004 D5): records
/// every <see cref="ShowGraphAsync"/> call (the model and the observed token) and
/// returns whatever task the test injects via <see cref="ShowGraphResult"/> —
/// completed (default), never-completing (a TCS task), or faulted. The three
/// renderer events are raised on demand through the <c>Raise*</c> methods.
/// <see cref="View"/> defaults to <c>null</c> (no visual surface — the honest
/// headless answer); mount tests set a plain control to assert the GraphHost
/// hand-off without any WebView.
/// </summary>
internal sealed class FakeGraphRenderer : IGraphRenderer
{
    /// <summary>Default <c>null</c>; set a plain control (e.g. a Border) to test the mount.</summary>
    public Control? View { get; set; }

    /// <summary>Every model received by <see cref="ShowGraphAsync"/>, in call order.</summary>
    public List<GraphModel> ShownGraphs { get; } = [];

    /// <summary>The cancellation token observed by each <see cref="ShowGraphAsync"/> call.</summary>
    public List<CancellationToken> ShowGraphTokens { get; } = [];

    /// <summary>Task returned by <see cref="ShowGraphAsync"/>: completed (default),
    /// never-completing, or faulted — injected per test.</summary>
    public Task ShowGraphResult { get; set; } = Task.CompletedTask;

    public event EventHandler<GraphNodeEventArgs>? NodeClicked;

    public event EventHandler<GraphNodeEventArgs>? NodeExpandRequested;

    public event EventHandler<GraphErrorEventArgs>? RendererError;

    public Task ShowGraphAsync(GraphModel graph, CancellationToken cancellationToken = default)
    {
        ShownGraphs.Add(graph);
        ShowGraphTokens.Add(cancellationToken);
        return ShowGraphResult;
    }

    /// <summary>Simulates a node tap arriving from the graph surface.</summary>
    public void RaiseNodeClicked(string dn, string kind) =>
        NodeClicked?.Invoke(this, new GraphNodeEventArgs(dn, kind));

    /// <summary>Simulates a node expand gesture (ignored by the VM until AP 2.3).</summary>
    public void RaiseNodeExpandRequested(string dn, string kind) =>
        NodeExpandRequested?.Invoke(this, new GraphNodeEventArgs(dn, kind));

    /// <summary>Simulates a renderer failure report (ready timeout, JS error, …).</summary>
    public void RaiseRendererError(string source, string message) =>
        RendererError?.Invoke(this, new GraphErrorEventArgs(source, message));
}
