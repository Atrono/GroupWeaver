using System.Text.Json;

using Avalonia.Controls;
using Avalonia.Headless.XUnit;

using GroupWeaver.App.Automation;
using GroupWeaver.App.Graph;
using GroupWeaver.App.Settings;
using GroupWeaver.App.Startup;
using GroupWeaver.App.Tests.Fakes;
using GroupWeaver.App.ViewModels;
using GroupWeaver.Core.Model;
using GroupWeaver.Core.Providers;

using Xunit;

namespace GroupWeaver.App.Tests;

/// <summary>
/// Pins the ADR-038 D3 item 3 merge the WP6 test-engineer review flagged as unfinished
/// (issue #245): <c>E2eChannel</c>'s <c>state</c> command reply MUST merge the active step's
/// renderer page truth (<see cref="IGraphRenderer.ProbeStateAsync"/> — <c>zoom</c>/<c>panX</c>/
/// <c>panY</c>/<c>animated</c>) alongside the VM-level fields (<c>nodeCount</c>/<c>edgeCount</c>/
/// <c>selectedDn</c>/<c>isLoading</c>/<c>loadError</c>/<c>step</c>) it already returned.
///
/// <para>Exercises the merge IN-PROCESS by calling the internal <c>EmitStateReplyAsync</c> seam
/// directly (the same access-for-testing rationale as <c>CytoscapeGraphRenderer.
/// EnterSingleFlight</c>'s existing internal seam) over a <see cref="FakeGraphRenderer"/> —
/// never the full <c>dotnet</c>-process stdio plumbing <c>E2eChannelCliTests</c> pins, which never
/// advances past PickRoot (no UI-automation input desktop, lab-environment.md) and so could never
/// observe a live renderer merging into the reply. A temp-dir <see cref="UiStateStore"/> is
/// injected (the #124 rule) — never real <c>%APPDATA%</c>.</para>
/// </summary>
public sealed class E2eChannelStateProbeMergeTests
{
    private const string RootDn = "OU=Lab,DC=stub,DC=lab";

    // === 1. No active renderer (PickRoot, before any workspace exists): page-truth fields null,
    //        VM-level fields still the honest idle/zeroed shape =================================

    [AvaloniaFact]
    public async Task StateCommand_NoActiveRenderer_PageTruthFieldsAreNull_VmFieldsStillPresent()
    {
        using var dir = new TempDir();
        var provider = NewWorkspaceProvider();
        var shell = NewShell(provider, graphRendererFactory: null, dir.Path);

        var connect = Assert.IsType<ConnectionViewModel>(shell.CurrentStep);
        await connect.ConnectDemoCommand.ExecuteAsync(null);
        Assert.IsType<RootPickerViewModel>(shell.CurrentStep); // PickRoot: no workspace yet
        Assert.Null(shell.ActiveGraphRenderer);

        // Channel constructed AFTER the drive above (mirrors DriveToWorkspaceAsync's ordering in
        // tests 2-4): E2eChannel's ctor subscribes ShellViewModel.StepChanged, which would
        // otherwise mirror those step transitions as extra trace lines into the SAME stdout this
        // test reads the state reply back from.
        var window = new Window();
        var channel = NewChannel(shell, window, out var stdout);

        using var reply = await EmitAndParseAsync(channel, stdout, seq: 1);
        var root = reply.RootElement;

        Assert.Equal(1, root.GetProperty("reply").GetInt64());
        Assert.Equal("PickRoot", root.GetProperty("step").GetString());
        Assert.Equal(0, root.GetProperty("nodeCount").GetInt32());
        Assert.Equal(0, root.GetProperty("edgeCount").GetInt32());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("selectedDn").ValueKind);
        Assert.False(root.GetProperty("isLoading").GetBoolean());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("loadError").ValueKind);
        Assert.Equal(JsonValueKind.Null, root.GetProperty("zoom").ValueKind);
        Assert.Equal(JsonValueKind.Null, root.GetProperty("panX").ValueKind);
        Assert.Equal(JsonValueKind.Null, root.GetProperty("panY").ValueKind);
        Assert.Equal(JsonValueKind.Null, root.GetProperty("animated").ValueKind);

        shell.Dispose();
        channel.Dispose();
        window.Close();
    }

    // === 2. Active renderer + a successful probe: zoom/panX/panY/animated merge in verbatim, and
    //        the VM-level nodeCount/edgeCount/selectedDn stay the WORKSPACE's own — never the
    //        probe's OWN Nodes/Edges/Selected (those three are deliberately not merged) =========

    [AvaloniaFact]
    public async Task StateCommand_ActiveRendererWithSuccessfulProbe_MergesPageTruth_VmFieldsUntouched()
    {
        using var dir = new TempDir();
        var (shell, workspace) = await DriveToWorkspaceAsync(dir.Path);
        var window = new Window();
        var channel = NewChannel(shell, window, out var stdout);
        var fakeRenderer = Assert.IsType<FakeGraphRenderer>(workspace.GraphRenderer);

        // Nodes/Edges/Selected deliberately differ from the workspace's own truth — a failure
        // here can only mean the merge pulled the WRONG fields off the probe result.
        fakeRenderer.ProbeStateResult = Task.FromResult<GraphStateReport?>(
            new GraphStateReport(
                Nodes: 999, Edges: 999, Zoom: 1.75, PanX: 12.5, PanY: -7,
                Selected: "CN=Bogus,DC=x", Animated: true));

        using var reply = await EmitAndParseAsync(channel, stdout, seq: 2);
        var root = reply.RootElement;

        Assert.Equal(2, root.GetProperty("reply").GetInt64());
        Assert.Equal("Workspace", root.GetProperty("step").GetString());
        Assert.Equal(workspace.Graph!.Nodes.Count, root.GetProperty("nodeCount").GetInt32());
        Assert.Equal(workspace.Graph!.Edges.Count, root.GetProperty("edgeCount").GetInt32());
        Assert.NotEqual(999, root.GetProperty("nodeCount").GetInt32());
        Assert.NotEqual(999, root.GetProperty("edgeCount").GetInt32());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("selectedDn").ValueKind); // nothing selected
        Assert.Equal(1.75, root.GetProperty("zoom").GetDouble());
        Assert.Equal(12.5, root.GetProperty("panX").GetDouble());
        Assert.Equal(-7, root.GetProperty("panY").GetDouble());
        Assert.True(root.GetProperty("animated").GetBoolean());

        shell.Dispose();
        channel.Dispose();
        window.Close();
    }

    // === 3. Active renderer + the probe returns null (the never-throw timeout contract): the four
    //        page-truth fields go null, but the VM-level fields still land promptly ==============

    [AvaloniaFact]
    public async Task StateCommand_ProbeReturnsNull_PageTruthFieldsAreNull_VmFieldsStillPresent()
    {
        using var dir = new TempDir();
        var (shell, workspace) = await DriveToWorkspaceAsync(dir.Path);
        var window = new Window();
        var channel = NewChannel(shell, window, out var stdout);
        var fakeRenderer = Assert.IsType<FakeGraphRenderer>(workspace.GraphRenderer);
        fakeRenderer.ProbeStateResult = Task.FromResult<GraphStateReport?>(null);

        using var reply = await EmitAndParseAsync(channel, stdout, seq: 3);
        var root = reply.RootElement;

        Assert.Equal(3, root.GetProperty("reply").GetInt64());
        Assert.Equal("Workspace", root.GetProperty("step").GetString());
        Assert.Equal(workspace.Graph!.Nodes.Count, root.GetProperty("nodeCount").GetInt32());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("zoom").ValueKind);
        Assert.Equal(JsonValueKind.Null, root.GetProperty("panX").ValueKind);
        Assert.Equal(JsonValueKind.Null, root.GetProperty("panY").ValueKind);
        Assert.Equal(JsonValueKind.Null, root.GetProperty("animated").ValueKind);

        shell.Dispose();
        channel.Dispose();
        window.Close();
    }

    // === 4. Active renderer + the probe FAULTS (models the renderer's single-flight guard, which
    //        throws SYNCHRONOUSLY instead of resolving to null): the state command must still
    //        reply promptly with the page-truth fields null — never fail, never hang ============

    [AvaloniaFact]
    public async Task StateCommand_ProbeFaults_NeverFailsTheReply_PageTruthFieldsAreNull()
    {
        using var dir = new TempDir();
        var (shell, workspace) = await DriveToWorkspaceAsync(dir.Path);
        var window = new Window();
        var channel = NewChannel(shell, window, out var stdout);
        var fakeRenderer = Assert.IsType<FakeGraphRenderer>(workspace.GraphRenderer);
        fakeRenderer.ProbeStateResult = Task.FromException<GraphStateReport?>(
            new InvalidOperationException(
                "ProbeStateAsync while another renderer call is in flight - the renderer runs one command at a time."));

        using var reply = await EmitAndParseAsync(channel, stdout, seq: 4);
        var root = reply.RootElement;

        Assert.Equal(4, root.GetProperty("reply").GetInt64());
        Assert.Equal("Workspace", root.GetProperty("step").GetString());
        Assert.Equal(workspace.Graph!.Nodes.Count, root.GetProperty("nodeCount").GetInt32());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("zoom").ValueKind);
        Assert.Equal(JsonValueKind.Null, root.GetProperty("panX").ValueKind);
        Assert.Equal(JsonValueKind.Null, root.GetProperty("panY").ValueKind);
        Assert.Equal(JsonValueKind.Null, root.GetProperty("animated").ValueKind);

        shell.Dispose();
        channel.Dispose();
        window.Close();
    }

    // === helpers ================================================================================

    /// <summary>Constructs a channel wired to an in-memory stdin (never consumed — every test here
    /// calls <see cref="E2eChannel.EmitStateReplyAsync"/> directly, bypassing the stdin read loop
    /// entirely) and an in-memory <paramref name="stdout"/> this file reads the reply back from.</summary>
    private static E2eChannel NewChannel(ShellViewModel shell, Window window, out StringWriter stdout)
    {
        stdout = new StringWriter();
        return new E2eChannel(shell, window, stdin: new StringReader(string.Empty), stdout: stdout);
    }

    /// <summary>Calls the merge seam once and parses the single JSON line it wrote.</summary>
    private static async Task<JsonDocument> EmitAndParseAsync(E2eChannel channel, StringWriter stdout, long seq)
    {
        await channel.EmitStateReplyAsync(seq);
        return JsonDocument.Parse(stdout.ToString().TrimEnd('\r', '\n'));
    }

    /// <summary>Builds a shell whose workspace rail state persists to <paramref name="baseDir"/>
    /// (never real <c>%APPDATA%</c>) with an explicit WebView2-present status (the ctor default
    /// probes the live registry — per-machine flakiness a VM test must not inherit). Mirrors
    /// <c>ShellFocusModeTests.NewShell</c>.</summary>
    private static ShellViewModel NewShell(
        StubDirectoryProvider provider, Func<IGraphRenderer>? graphRendererFactory, string baseDir) =>
        new(
            _ => provider,
            new StartupOptions(Demo: false),
            new WebView2RuntimeStatus(IsInstalled: true, Version: "test"),
            graphRendererFactory: graphRendererFactory,
            ruleset: null,
            locator: null,
            uiStateStore: new UiStateStore(baseDir));

    /// <summary>The stub provider driven through Connect → PickRoot → Workspace: one OU root
    /// candidate and an empty scope snapshot (mirrors <c>ShellFocusModeTests</c>'s
    /// <c>NewWorkspaceProvider</c>).</summary>
    private static StubDirectoryProvider NewWorkspaceProvider() =>
        new(Task.FromResult(new DirectoryConnection("stub directory", 0)))
        {
            RootCandidatesResult = Task.FromResult<IReadOnlyList<AdObject>>([
                new AdObject { Dn = RootDn, Kind = AdObjectKind.OrganizationalUnit, Name = "Lab" },
            ]),
            LoadScopeResult = Task.FromResult(new DirectorySnapshot()),
        };

    /// <summary>Drives Connect → PickRoot → Workspace with a <see cref="FakeGraphRenderer"/> wired
    /// as the active renderer, and settles the scope load — landing on a loaded
    /// <see cref="WorkspaceViewModel"/> whose <see cref="ShellViewModel.ActiveGraphRenderer"/> is
    /// that fake.</summary>
    private static async Task<(ShellViewModel Shell, WorkspaceViewModel Workspace)> DriveToWorkspaceAsync(
        string baseDir)
    {
        var provider = NewWorkspaceProvider();
        var shell = NewShell(provider, () => new FakeGraphRenderer(), baseDir);

        var connect = Assert.IsType<ConnectionViewModel>(shell.CurrentStep);
        await connect.ConnectDemoCommand.ExecuteAsync(null);
        var picker = Assert.IsType<RootPickerViewModel>(shell.CurrentStep);
        await picker.LoadCandidates;

        picker.SelectedCandidate = picker.Candidates[0];
        picker.LoadRootCommand.Execute(null);

        var workspace = Assert.IsType<WorkspaceViewModel>(shell.CurrentStep);
        await workspace.Initialization;
        return (shell, workspace);
    }

    private sealed class TempDir : IDisposable
    {
        public string Path { get; } =
            Directory.CreateTempSubdirectory("groupweaver-e2echannel-stateprobe-").FullName;

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch (IOException)
            {
                // Best-effort cleanup; never fail a test over temp-dir teardown.
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}
