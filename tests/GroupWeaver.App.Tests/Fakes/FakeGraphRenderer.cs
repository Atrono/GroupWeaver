using Avalonia.Controls;

using GroupWeaver.App.Graph;
using GroupWeaver.Core.Diff;
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
    /// <summary>Backs the OPT-IN real-surface mode (<see cref="WithRealSurface"/>): one cached
    /// <see cref="Control"/> instance per renderer, mirroring the production renderer's single
    /// <see cref="IGraphRenderer.View"/>. <c>null</c> in the default mode (the surface is the
    /// settable <see cref="View"/>, which itself defaults to <c>null</c>).</summary>
    private readonly Control? _ownRealSurface;

    /// <summary>Default mode: <see cref="View"/> is a settable property defaulting to <c>null</c>
    /// (no visual surface — the honest headless answer; the existing screenshot fixtures rely on it).
    /// </summary>
    public FakeGraphRenderer()
    {
    }

    private FakeGraphRenderer(bool realSurface)
    {
        if (realSurface)
        {
            // ONE real control per renderer instance, cached for this renderer's whole life — the
            // production-faithful invariant the back-navigation regression depends on: the SAME
            // step VM (re-entered via Back) re-mounts the SAME cached control into a fresh view's
            // GraphHost, which is exactly the double-parent the fix must cure (ADR — #back-nav-crash).
            // A Border is a real, layout-participating Control (it measures/arranges), so the
            // visual-tree conflict surfaces headless on the measure pass — unlike a null View.
            _ownRealSurface = new Border();
        }
    }

    /// <summary>OPT-IN factory for the step-swap regression: a renderer whose <see cref="View"/>
    /// returns its OWN single cached real <see cref="Control"/> (a <see cref="Border"/>), mirroring
    /// the production renderer (one surface per renderer instance, re-mounted on re-attach). Does
    /// NOT change the default <see cref="FakeGraphRenderer"/> behavior — the default ctor still
    /// yields <see cref="View"/> == <c>null</c>, which the screenshot fixtures and other suites
    /// depend on (a null View leaves the GraphHost placeholder in place).</summary>
    public static FakeGraphRenderer WithRealSurface() => new(realSurface: true);

    /// <summary>The renderer's mountable surface. In the default mode this is a settable property
    /// defaulting to <c>null</c> (set a plain control — e.g. a Border — to test a mount manually).
    /// In the OPT-IN <see cref="WithRealSurface"/> mode this returns the renderer's single cached
    /// real control and the setter is inert (the cached surface is the contract under test).</summary>
    public Control? View
    {
        get => _ownRealSurface ?? _settableView;
        set
        {
            if (_ownRealSurface is null)
            {
                _settableView = value;
            }
        }
    }

    private Control? _settableView;

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

    /// <summary>Every (Dn, On) received by <see cref="SetBusyAsync"/>, in call order
    /// (ADR-019 #94). Its OWN channel — never <see cref="FocusCalls"/>. A fetch-expand must
    /// record [(dn,true),(dn,false)]; a cache-hit/focus-only/non-group expand must leave it EMPTY.</summary>
    public List<(string Dn, bool On)> SetBusyCalls { get; } = [];

    /// <summary>The cancellation token observed by each <see cref="SetBusyAsync"/> call.</summary>
    public List<CancellationToken> SetBusyTokens { get; } = [];

    /// <summary>Task returned by <see cref="SetBusyAsync"/>: completed (default), never-completing, or faulted.</summary>
    public Task SetBusyResult { get; set; } = Task.CompletedTask;

    /// <summary>Every DN received by <see cref="SelectAsync"/>, in call order (ADR-020 #96).
    /// Its OWN channel — never <see cref="FocusCalls"/>: the pinned JumpCommand test asserts
    /// FocusCalls increments by EXACTLY 1 per jump, and a jump's SelectedDn set drives a select
    /// dispatch that must land HERE, not there. A null SelectedDn dispatches the empty string.</summary>
    public List<string> SelectCalls { get; } = [];

    /// <summary>The cancellation token observed by each <see cref="SelectAsync"/> call.</summary>
    public List<CancellationToken> SelectTokens { get; } = [];

    /// <summary>Task returned by <see cref="SelectAsync"/>: completed (default), never-completing, or faulted.</summary>
    public Task SelectResult { get; set; } = Task.CompletedTask;

    /// <summary>Every UNION model received by <see cref="ShowDiffGraphAsync"/>, in call order
    /// (ADR-015 Slice 6 / #66 — the Gap step's wholesale destroy+fit gap-topology push; kept
    /// on its OWN channel, never <see cref="ShownGraphs"/>, so the gap render path is
    /// distinguishable from a severity <see cref="ShowGraphAsync"/>).</summary>
    public List<GraphModel> ShownDiffGraphs { get; } = [];

    /// <summary>Every <see cref="SnapshotDiff"/> received by <see cref="ShowDiffGraphAsync"/>,
    /// in call order (the diff supplies the per-element Added/Removed/Common/Unchecked status;
    /// the fake records it so the gap push is pinnable — no <see cref="RuleReport"/> rides
    /// alongside it: the gap view shows the DIFF, not severity).</summary>
    public List<SnapshotDiff> ShownDiffs { get; } = [];

    /// <summary>The cancellation token observed by each <see cref="ShowDiffGraphAsync"/> call.</summary>
    public List<CancellationToken> ShowDiffGraphTokens { get; } = [];

    /// <summary>Task returned by <see cref="ShowDiffGraphAsync"/>: completed (default),
    /// never-completing, or faulted — injected per test (mirrors
    /// <see cref="ShowGraphResult"/>).</summary>
    public Task ShowDiffGraphResult { get; set; } = Task.CompletedTask;

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

    /// <summary>ADR-019 (#94): records the (dn, on) toggle + observed token on the busy
    /// channel (NEVER <see cref="FocusCalls"/>) and returns the injected
    /// <see cref="SetBusyResult"/>. Overrides the <see cref="IGraphRenderer.SetBusyAsync"/>
    /// default no-op so the fetch-path busy ring is pinnable.</summary>
    public Task SetBusyAsync(string dn, bool on, CancellationToken cancellationToken = default)
    {
        SetBusyCalls.Add((dn, on));
        SetBusyTokens.Add(cancellationToken);
        return SetBusyResult;
    }

    /// <summary>ADR-020 (#96): records the DN + observed token on the SELECT channel (NEVER
    /// <see cref="FocusCalls"/>) and returns the injected <see cref="SelectResult"/>. Overrides
    /// the <see cref="IGraphRenderer.SelectAsync"/> default no-op so the reverse sidebar->graph
    /// selection sync is pinnable — every <see cref="ViewModels.WorkspaceViewModel.SelectedDn"/>
    /// change (jump, sidebar row, graph tap, null->clear) must land here, a null SelectedDn as
    /// the empty string.</summary>
    public Task SelectAsync(string dn, CancellationToken cancellationToken = default)
    {
        SelectCalls.Add(dn);
        SelectTokens.Add(cancellationToken);
        return SelectResult;
    }

    /// <summary>ADR-015 Slice 6 (#66): records the union graph + the diff + the observed token
    /// (each on its own channel) and returns the injected <see cref="ShowDiffGraphResult"/>.
    /// Overrides the <see cref="IGraphRenderer.ShowDiffGraphAsync"/> default no-op (mirroring
    /// the <see cref="ShowGraphAsync"/> recording shape) so the Gap step's wholesale gap push
    /// is pinnable.</summary>
    public Task ShowDiffGraphAsync(
        GraphModel union,
        SnapshotDiff diff,
        CancellationToken cancellationToken = default)
    {
        ShownDiffGraphs.Add(union);
        ShownDiffs.Add(diff);
        ShowDiffGraphTokens.Add(cancellationToken);
        return ShowDiffGraphResult;
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
