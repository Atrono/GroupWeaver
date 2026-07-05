using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

using Avalonia.Controls;
using Avalonia.Threading;

using GroupWeaver.App.Diagnostics;
using GroupWeaver.App.Graph;
using GroupWeaver.App.ViewModels;

namespace GroupWeaver.App.Automation;

/// <summary>
/// ADR-038 D3.2 (WP6, #245): the <c>--e2e</c> stdio JSONL channel — a demo-only,
/// OBSERVATION-ONLY automation seam (ADR-038 D2: no invoke/click command exists, ever, and
/// none ever will on this channel). Wired at the composition root (<c>App.axaml.cs</c>),
/// alongside where <c>StateDir</c>/logging get wired — a no-op unless
/// <see cref="StartupOptions.E2e"/> is set (<see cref="Program"/> demo-gates the flag before
/// any window, exit 64 without <c>--demo</c>).
///
/// <para><b>stdout</b> — one JSON line per event, mirroring the ADR-037 wire style
/// (<c>ts</c>, <c>evt</c>, fields) but a SEPARATE stream/purpose from the persisted log file
/// (<see cref="FileLogSink"/>): this is the harness's real-time trace, read by the
/// <c>tools/e2e</c> scenario driver. Emitted events: <c>StepChanged{from,to,trigger}</c> —
/// raised from the SAME choke point <see cref="ShellViewModel.StepChanged"/> logs from, never
/// a duplicated log call; <c>LoadError{message}</c> / <c>RendererError{source,message}</c> —
/// the existing <see cref="WorkspaceViewModel.LoadError"/> / <c>GraphRenderer.RendererError</c>
/// plumbing; <c>DemoConnected{groups}</c> — mirrors the <c>--check --demo</c> stdout line, read
/// off the picker's <see cref="RootPickerViewModel.Connection"/> the moment it is reached.</para>
///
/// <para><b>stdin</b> — a background read loop accepting EXACTLY two commands (ADR-038 D2 —
/// never more, never a mutation): <c>{"cmd":"state","seq":N}</c> replies on stdout
/// <c>{"reply":N,"step":…,"nodeCount":…,"edgeCount":…,"selectedDn":…,"isLoading":…,
/// "loadError":…,"zoom":…,"panX":…,"panY":…,"animated":…}</c> — the first seven fields read live
/// off the shell / the active <see cref="WorkspaceViewModel"/>; the last four are MERGED
/// (ADR-038 D3 item 3) from the active step's renderer via
/// <see cref="IGraphRenderer.ProbeStateAsync"/> — <c>null</c> when no renderer is active (e.g.
/// Connect/PickRoot) or the probe times out/faults, and NEVER delaying or failing the reply's
/// VM-level fields on that account; <c>{"cmd":"quit"}</c> closes the window gracefully (UI
/// thread) so a scenario can tell a clean exit from a kill. A malformed line, an unknown
/// <c>cmd</c>, or any exception is silently ignored — NEVER throws, NEVER crashes the app, NEVER
/// touches Active Directory.</para>
/// </summary>
public sealed class E2eChannel : IDisposable
{
    private static readonly JsonWriterOptions WriterOptions = new()
    {
        // The STRICT default encoder (never a relaxed one — the recurring security-finding
        // class, ADR-032/ADR-037 Security notes; the same discipline FileLogSink follows).
        Encoder = JavaScriptEncoder.Default,
    };

    private readonly ShellViewModel _shell;
    private readonly Window _window;
    private readonly TextReader _stdin;
    private readonly TextWriter _stdout;
    private readonly object _writeLock = new();
    private WorkspaceViewModel? _hookedWorkspace;
    private bool _disposed;

    /// <summary><paramref name="stdin"/>/<paramref name="stdout"/> default to the process
    /// console streams; overridable for headless tests (never wired to real stdio there).</summary>
    public E2eChannel(ShellViewModel shell, Window window, TextReader? stdin = null, TextWriter? stdout = null)
    {
        _shell = shell;
        _window = window;
        _stdin = stdin ?? Console.In;
        _stdout = stdout ?? Console.Out;
        _shell.StepChanged += OnStepChanged;
    }

    /// <summary>Starts the background stdin read loop. Kept separate from the ctor so the
    /// composition root wires every event hook first — no command can race in before the
    /// channel is fully armed.</summary>
    public void Start() => _ = Task.Run(ReadLoopAsync);

    /// <summary>The <see cref="ShellViewModel.StepChanged"/> tap: mirrors the log line onto the
    /// trace, then (on the two step transitions that carry extra observable state) emits the
    /// <c>DemoConnected</c> event and hooks the freshly-entered workspace's error plumbing.</summary>
    private void OnStepChanged(object? sender, StepChangedEventArgs e)
    {
        Emit("StepChanged", ("from", e.From), ("to", e.To), ("trigger", e.Trigger));

        if (e.To == "PickRoot" && _shell.IsDemoMode && _shell.CurrentStep is RootPickerViewModel picker)
        {
            // Mirrors the --check --demo stdout line ("connected, N groups loaded") the moment
            // the picker is reached — the earliest point a GroupCount is available.
            Emit("DemoConnected", ("groups", picker.Connection.GroupCount));
        }

        if (e.To == "Workspace" && _shell.CurrentStep is WorkspaceViewModel workspace
            && !ReferenceEquals(workspace, _hookedWorkspace))
        {
            HookWorkspace(workspace);
        }
    }

    /// <summary>Hooks the workspace's EXISTING error plumbing (never a new error surface):
    /// <see cref="WorkspaceViewModel.LoadError"/> property changes (load exceptions AND renderer
    /// errors funneled through it) and the renderer's own <c>RendererError</c> (the structured
    /// source/message split the plain LoadError string loses). Guarded by reference so re-entering
    /// the SAME workspace (Back from Plan/Gap/Audit, ADR-038's never-disposed-on-Back workspace
    /// lifecycle) never double-subscribes.</summary>
    private void HookWorkspace(WorkspaceViewModel workspace)
    {
        _hookedWorkspace = workspace;
        workspace.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(WorkspaceViewModel.LoadError) && workspace.LoadError is { } message)
            {
                Emit("LoadError", ("message", Redactor.Scrub(message)));
            }
        };

        if (workspace.GraphRenderer is { } renderer)
        {
            renderer.RendererError += (_, args) =>
                Emit("RendererError", ("source", args.Source), ("message", Redactor.Scrub(args.Message)));
        }
    }

    private async Task ReadLoopAsync()
    {
        while (true)
        {
            string? line;
            try
            {
                line = await _stdin.ReadLineAsync();
            }
            catch
            {
                return; // never-throw: a dead/faulted stdin ends the loop quietly.
            }

            if (line is null)
            {
                return; // stdin closed (EOF) — nothing more will ever arrive.
            }

            HandleLine(line);
        }
    }

    /// <summary>Parses one stdin line. ADR-038 D2: STRICTLY observe-only — exactly two commands
    /// exist and neither can ever mutate the graph/directory. Anything else (malformed JSON, a
    /// missing/non-string <c>cmd</c>, an unrecognized <c>cmd</c>) is silently ignored.</summary>
    private void HandleLine(string line)
    {
        try
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("cmd", out var cmdProperty)
                || cmdProperty.ValueKind != JsonValueKind.String)
            {
                return;
            }

            switch (cmdProperty.GetString())
            {
                case "state":
                    var seq = root.TryGetProperty("seq", out var seqProperty)
                        && seqProperty.ValueKind == JsonValueKind.Number
                        && seqProperty.TryGetInt64(out var parsedSeq)
                            ? parsedSeq
                            : 0;
                    // VM state is UI-thread-affine (ObservableObject, no thread-safety contract) —
                    // read it on the UI thread, mirroring every other cross-thread bridge hand-off
                    // in this codebase (e.g. CytoscapeGraphRenderer.OnWebMessageReceived).
                    // Fire-and-forget (the ShellViewModel.ApplyCanvasTheme idiom): EmitStateReplyAsync
                    // never throws (the page-truth probe below is wrapped in its own try/catch), so
                    // discarding the task here is safe.
                    Dispatcher.UIThread.Post(() => _ = EmitStateReplyAsync(seq));
                    break;
                case "quit":
                    // Graceful close (UI thread): MainWindow.OnClosed disposes the shell, which
                    // cancels any in-flight load — the same teardown a real window Close takes.
                    Dispatcher.UIThread.Post(() => _window.Close());
                    break;
                    // No default: an unrecognized cmd is silently ignored (never invoke/click, D2).
            }
        }
        catch
        {
            // Malformed line: ignored, never crashes the app (ADR-038 D2 / scope).
        }
    }

    /// <summary>Builds the <c>state</c> command's reply from whatever the CURRENT step (and, if
    /// it is the workspace, the active <see cref="WorkspaceViewModel"/>) exposes today — never
    /// invented fields — MERGED (ADR-038 D3 item 3, the test-engineer WP6 finding this fixes)
    /// with the active step's renderer page truth: <see cref="ShellViewModel.ActiveGraphRenderer"/>
    /// covers Workspace/Plan/Gap identically (whichever the current step is), so a <c>state</c>
    /// probe issued from any of those three steps observes the live camera, not just Workspace's.
    /// A non-workspace step reports the zeroed/idle VM defaults; a step with no renderer (Connect/
    /// PickRoot/Audit) — or a probe that times out, faults, or hits the renderer's single-flight
    /// guard (<see cref="CytoscapeGraphRenderer.EnterSingleFlight"/> mid another command) —
    /// reports the four page-truth fields as JSON <c>null</c>. Either way the VM-level fields are
    /// ALWAYS returned promptly: a slow/absent page truth must never fail or hang the whole
    /// reply.
    /// <para><c>internal</c> (not <c>private</c>): a direct pin for the merge itself, over the
    /// injected in-memory stdin/stdout seam — the same access-for-testing rationale as
    /// <see cref="CytoscapeGraphRenderer.EnterSingleFlight"/>'s existing internal seam. The full
    /// stdio wire framing stays pinned at the process level in <c>E2eChannelCliTests</c>.</para>
    /// </summary>
    internal async Task EmitStateReplyAsync(long seq)
    {
        var workspace = _shell.CurrentStep as WorkspaceViewModel;

        GraphStateReport? probe = null;
        if (_shell.ActiveGraphRenderer is { } renderer)
        {
            try
            {
                probe = await renderer.ProbeStateAsync();
            }
            catch
            {
                // Never-throw: ProbeStateAsync's own contract already maps a page-side timeout/
                // error to a null result, but a renderer command already in flight throws
                // SYNCHRONOUSLY instead (EnterSingleFlight) — folded into the same "no page truth
                // available right now" outcome so the state command can never fail or hang on it.
                probe = null;
            }
        }

        WriteLine(writer =>
        {
            writer.WriteStartObject();
            writer.WriteNumber("reply", seq);
            writer.WriteString("step", _shell.CurrentStepName);
            writer.WriteNumber("nodeCount", workspace?.Graph?.Nodes.Count ?? 0);
            writer.WriteNumber("edgeCount", workspace?.Graph?.Edges.Count ?? 0);
            WriteNullableString(writer, "selectedDn", Redactor.Dn(workspace?.SelectedDn));
            writer.WriteBoolean("isLoading", workspace?.IsLoading ?? false);
            WriteNullableString(writer, "loadError", Redactor.Scrub(workspace?.LoadError));
            if (probe is { } report)
            {
                writer.WriteNumber("zoom", report.Zoom);
                writer.WriteNumber("panX", report.PanX);
                writer.WriteNumber("panY", report.PanY);
                writer.WriteBoolean("animated", report.Animated);
            }
            else
            {
                // Keys always present (matching selectedDn/loadError's always-present-maybe-null
                // shape above) rather than omitted — a fixed reply shape is simpler for a
                // PowerShell scenario driver to consume than an optional-key one.
                writer.WriteNull("zoom");
                writer.WriteNull("panX");
                writer.WriteNull("panY");
                writer.WriteNull("animated");
            }

            writer.WriteEndObject();
        });
    }

    /// <summary>Emits one <c>{"ts":…,"evt":…,fields…}</c> trace line. String/int fields only —
    /// every current event needs no other shape.</summary>
    private void Emit(string evt, params (string Name, object Value)[] fields)
    {
        WriteLine(writer =>
        {
            writer.WriteStartObject();
            writer.WriteString("ts", DateTimeOffset.UtcNow);
            writer.WriteString("evt", evt);
            foreach (var (name, value) in fields)
            {
                switch (value)
                {
                    case string stringValue:
                        WriteNullableString(writer, name, stringValue);
                        break;
                    case int intValue:
                        writer.WriteNumber(name, intValue);
                        break;
                }
            }

            writer.WriteEndObject();
        });
    }

    private static void WriteNullableString(Utf8JsonWriter writer, string name, string? value)
    {
        if (value is null)
        {
            writer.WriteNull(name);
        }
        else
        {
            writer.WriteString(name, value);
        }
    }

    /// <summary>Serializes one JSON object via <paramref name="write"/> and writes it as a single
    /// stdout line. NEVER throws (a broken/full stdout pipe must not crash the app — the same
    /// never-throw discipline <see cref="FileLogSink"/> applies to the log file).</summary>
    private void WriteLine(Action<Utf8JsonWriter> write)
    {
        if (_disposed)
        {
            return;
        }

        try
        {
            using var buffer = new MemoryStream();
            using (var writer = new Utf8JsonWriter(buffer, WriterOptions))
            {
                write(writer);
            }

            var json = Encoding.UTF8.GetString(buffer.ToArray());
            lock (_writeLock)
            {
                _stdout.WriteLine(json);
                _stdout.Flush();
            }
        }
        catch
        {
            // Never-throw: a dead/broken stdout must not become an app failure.
        }
    }

    /// <summary>Unsubscribes the step-change tap. The workspace hooks (PropertyChanged /
    /// RendererError) are per-instance lambdas torn down with the workspace itself; nothing else
    /// to release here (no WebView, no AD, no file handle owned by this channel).</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _shell.StepChanged -= OnStepChanged;
    }
}
