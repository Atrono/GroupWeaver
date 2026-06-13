using Avalonia.Controls;

using GroupWeaver.App.Graph;
using GroupWeaver.Core.Graph;
using GroupWeaver.Core.Rules;

namespace GroupWeaver.App.Tests.Fakes;

/// <summary>
/// Renderer-seam fake for the AP 2.2 S6 workspace load flow (ADR-004 D5), grown for
/// the AP 2.3 lazy-expand seam (ADR-005 D2): records every <see cref="ShowGraphAsync"/>,
/// <see cref="UpdateGraphAsync"/>, and <see cref="FocusAsync"/> call (the argument and
/// the observed token, each on its own channel) and returns whatever task the test
/// injects via <see cref="ShowGraphResult"/>/<see cref="UpdateGraphResult"/>/
/// <see cref="FocusResult"/> — completed (default), never-completing (a TCS task),
/// or faulted. The three renderer events are raised on demand through the
/// <c>Raise*</c> methods. <see cref="View"/> defaults to <c>null</c> (no visual
/// surface — the honest headless answer); mount tests set a plain control to assert
/// the GraphHost hand-off without any WebView.
/// AP 3.4 S5 (ADR-010 §3): the renderer seam grew a <see cref="RuleReport"/> + the
/// VM-computed below-map alongside each graph push — the fake records them on their
/// own channels (<see cref="ShownReports"/>/<see cref="UpdatedReports"/> +
/// <see cref="ShownBelowMaps"/>/<see cref="UpdatedBelowMaps"/>) so WorkspaceViolationsTests
/// can pin that the re-Evaluated report actually reaches the surface.
/// </summary>
internal sealed class FakeGraphRenderer : IGraphRenderer
{
    /// <summary>Default <c>null</c>; set a plain control (e.g. a Border) to test the mount.</summary>
    public Control? View { get; set; }

    /// <summary>Every model received by <see cref="ShowGraphAsync"/>, in call order.</summary>
    public List<GraphModel> ShownGraphs { get; } = [];

    /// <summary>Every report received by <see cref="ShowGraphAsync"/>, in call order
    /// (AP 3.4 S5 severity push — the VM evaluates BEFORE the show and hands the report
    /// to the renderer; the fake captures it so the load-time evaluation is pinnable).</summary>
    public List<RuleReport> ShownReports { get; } = [];

    /// <summary>Every below-map received by <see cref="ShowGraphAsync"/>, in call order
    /// (the VM-computed roll-up; <c>null</c> when no roll-up applies).</summary>
    public List<IReadOnlyDictionary<string, (int Count, RuleSeverity Sev)>?> ShownBelowMaps { get; } = [];

    /// <summary>The cancellation token observed by each <see cref="ShowGraphAsync"/> call.</summary>
    public List<CancellationToken> ShowGraphTokens { get; } = [];

    /// <summary>Task returned by <see cref="ShowGraphAsync"/>: completed (default),
    /// never-completing, or faulted — injected per test.</summary>
    public Task ShowGraphResult { get; set; } = Task.CompletedTask;

    /// <summary>Every model received by <see cref="UpdateGraphAsync"/>, in call order
    /// (ADR-005 D2 — kept separate from <see cref="ShownGraphs"/>: show and update
    /// have different post-conditions, tests must see which path was taken).</summary>
    public List<GraphModel> UpdatedGraphs { get; } = [];

    /// <summary>Every report received by <see cref="UpdateGraphAsync"/>, in call order
    /// (AP 3.4 S5: the ExpandAsync re-Evaluate pushes the fresh report through the
    /// replace-in-place update — the fake records it to pin the re-evaluation).</summary>
    public List<RuleReport> UpdatedReports { get; } = [];

    /// <summary>Every below-map received by <see cref="UpdateGraphAsync"/>, in call order.</summary>
    public List<IReadOnlyDictionary<string, (int Count, RuleSeverity Sev)>?> UpdatedBelowMaps { get; } = [];

    /// <summary>The cancellation token observed by each <see cref="UpdateGraphAsync"/> call.</summary>
    public List<CancellationToken> UpdateGraphTokens { get; } = [];

    /// <summary>Task returned by <see cref="UpdateGraphAsync"/>: completed (default),
    /// never-completing, or faulted — injected per test.</summary>
    public Task UpdateGraphResult { get; set; } = Task.CompletedTask;

    /// <summary>Every DN collection received by <see cref="FocusAsync"/>, in call order.</summary>
    public List<IReadOnlyCollection<string>> FocusCalls { get; } = [];

    /// <summary>The cancellation token observed by each <see cref="FocusAsync"/> call.</summary>
    public List<CancellationToken> FocusTokens { get; } = [];

    /// <summary>Task returned by <see cref="FocusAsync"/>: completed (default),
    /// never-completing, or faulted — injected per test.</summary>
    public Task FocusResult { get; set; } = Task.CompletedTask;

    /// <summary>The eight-byte PNG file signature (89 50 4E 47 0D 0A 1A 0A) — the canned
    /// "image bytes" the AP 4.1 S8 graph-image export tests pin: a non-null
    /// <see cref="ExportPngAsync"/> result the VM must write VERBATIM to the picked
    /// <c>.png</c> path (the real renderer returns the decoded <c>cy.png</c> base64; the
    /// fake stands in with a recognisable, byte-checkable magic-number payload).</summary>
    public static readonly byte[] PngMagicBytes = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    /// <summary>How many times <see cref="ExportPngAsync"/> was invoked (the round-trip is
    /// dispatched at most once per command run — pins "no double export", and that a
    /// gated-out command never reaches the renderer at all).</summary>
    public int ExportPngCalls { get; private set; }

    /// <summary>The cancellation token observed by each <see cref="ExportPngAsync"/> call.</summary>
    public List<CancellationToken> ExportPngTokens { get; } = [];

    /// <summary>Result returned by <see cref="ExportPngAsync"/>: the canned
    /// <see cref="PngMagicBytes"/> by default (a successful raster). Set to <c>null</c> to
    /// model the never-throw timeout/error contract (<see cref="IGraphRenderer.ExportPngAsync"/>
    /// returns <c>null</c>, the VM must then write NOTHING). Set to a never-completing TCS
    /// task to model an in-flight export.</summary>
    public Task<byte[]?> ExportPngResult { get; set; } = Task.FromResult<byte[]?>(PngMagicBytes);

    public event EventHandler<GraphNodeEventArgs>? NodeClicked;

    public event EventHandler<GraphNodeEventArgs>? NodeExpandRequested;

    public event EventHandler<GraphErrorEventArgs>? RendererError;

    public Task ShowGraphAsync(
        GraphModel graph,
        RuleReport report,
        IReadOnlyDictionary<string, (int Count, RuleSeverity Sev)>? belowMap,
        CancellationToken cancellationToken = default)
    {
        ShownGraphs.Add(graph);
        ShownReports.Add(report);
        ShownBelowMaps.Add(belowMap);
        ShowGraphTokens.Add(cancellationToken);
        return ShowGraphResult;
    }

    public Task UpdateGraphAsync(
        GraphModel graph,
        RuleReport report,
        IReadOnlyDictionary<string, (int Count, RuleSeverity Sev)>? belowMap,
        CancellationToken cancellationToken = default)
    {
        UpdatedGraphs.Add(graph);
        UpdatedReports.Add(report);
        UpdatedBelowMaps.Add(belowMap);
        UpdateGraphTokens.Add(cancellationToken);
        return UpdateGraphResult;
    }

    public Task FocusAsync(IReadOnlyCollection<string> dns, CancellationToken cancellationToken = default)
    {
        FocusCalls.Add(dns);
        FocusTokens.Add(cancellationToken);
        return FocusResult;
    }

    /// <summary>AP 4.1 S8 (ADR-013): records the call + observed token and returns the
    /// injected <see cref="ExportPngResult"/> — the canned <see cref="PngMagicBytes"/> by
    /// default, or <c>null</c> to exercise the never-throw timeout contract. Image bytes
    /// only; the outbound command carries no untrusted tokens.</summary>
    public Task<byte[]?> ExportPngAsync(CancellationToken cancellationToken = default)
    {
        ExportPngCalls++;
        ExportPngTokens.Add(cancellationToken);
        return ExportPngResult;
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
