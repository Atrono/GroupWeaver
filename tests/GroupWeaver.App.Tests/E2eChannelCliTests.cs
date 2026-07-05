using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

using Xunit;

namespace GroupWeaver.App.Tests;

/// <summary>
/// Pins the <c>--e2e</c> stdio JSONL channel's wire framing (ADR-038 D3.2, WP6, #245) at the
/// PROCESS level. <c>Automation.E2eChannel</c> is constructed at the composition root over a
/// live Avalonia <c>Window</c> and (by default) the process-global <c>Console.In</c>/
/// <c>Console.Out</c> — there is no smaller in-proc seam that exercises the REAL stdin/stdout
/// plumbing end-to-end (mirrors <see cref="StateDirLogPrecedenceTests"/>'s rationale for the
/// log-directory precedence seam: the composition-root wiring is GUI-path-only, so a full
/// launch is the only way to reach it). The <c>ShellViewModel</c>/<c>WorkspaceViewModel</c> data
/// the "state" reply actually reads (<c>CurrentStepName</c>, <c>Graph</c>, <c>SelectedDn</c>,
/// <c>IsLoading</c>, <c>LoadError</c>) is separately pinned in-proc, headlessly, in
/// <c>Diagnostics.ShellStepChangedLogTests</c> / <see cref="ShellDemoModeTests"/> /
/// <see cref="WorkspaceLoadTests"/> — this file is the WIRE FRAMING only.
///
/// <para>Every probe launches <c>--demo --e2e --state-dir &lt;tmp&gt;</c> (hermetic — the
/// real-<c>%APPDATA%</c>-untouched invariant is checked in test 1, mirroring
/// <see cref="StateDirLogPrecedenceTests"/>) and interacts purely over stdin/stdout: no UI
/// automation, no window interaction, no mouse/keyboard injection (this agent context has no
/// interactive input desktop, lab-environment.md). Every probe ends with EITHER a graceful
/// <c>{"cmd":"quit"}</c> (asserted to exit the process on its own) or, on any assertion failure,
/// a bounded kill via <see cref="Dispose"/> — no probe here may ever leave a zombie window
/// behind a red run.</para>
/// </summary>
public sealed class E2eChannelCliTests : IDisposable
{
    /// <summary>The BOM-less UTF-8 instance — see <see cref="StartApp"/>'s remarks.</summary>
    private static readonly UTF8Encoding NoBomUtf8 = new(encoderShouldEmitUTF8Identifier: false);

    private readonly List<Process> _processes = [];
    private readonly List<string> _tempDirs = [];

    public void Dispose()
    {
        foreach (var process in _processes)
        {
            KillIfAlive(process);
        }

        foreach (var dir in _tempDirs)
        {
            try
            {
                Directory.Delete(dir, recursive: true);
            }
            catch (IOException)
            {
                // Covers DirectoryNotFoundException too — best-effort cleanup.
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    // === 1. "state" right after launch: echoed seq + the zeroed/idle workspace shape ==========

    /// <summary>
    /// <c>--demo</c> auto-connects at construction (<c>ShellViewModel</c>'s ctor kicks off
    /// <c>ConnectDemoCommand.ExecuteAsync</c> synchronously, before <c>App.axaml.cs</c> even
    /// constructs <c>MainWindow</c>/wires the channel) — so by the time this file's very first
    /// "state" command can possibly land, <c>CurrentStepName</c> is ALREADY <c>"PickRoot"</c>,
    /// never <c>"Connect"</c> (found empirically while writing this file; <c>DemoProvider</c>'s
    /// connect is in-memory and effectively synchronous). No workspace exists yet either way, so
    /// every workspace-derived field must still report the honest idle/zeroed shape.
    /// </summary>
    [Fact]
    public async Task StateCommand_RightAfterLaunch_RepliesWithEchoedSeqAndZeroedWorkspaceFields()
    {
        var realUiStatePath = RealUiStatePath();
        var hashBefore = HashIfExists(realUiStatePath);

        var process = StartApp("--demo", "--e2e", "--state-dir", NewTempDir());

        await WriteRawLineAsync(process, """{"cmd":"state","seq":1}""");
        using var reply = await ReadJsonLineAsync(process, TimeSpan.FromSeconds(30));

        Assert.NotNull(reply);
        var root = reply!.RootElement;
        Assert.Equal(1, root.GetProperty("reply").GetInt64());
        Assert.Equal("PickRoot", root.GetProperty("step").GetString());
        Assert.Equal(0, root.GetProperty("nodeCount").GetInt32());
        Assert.Equal(0, root.GetProperty("edgeCount").GetInt32());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("selectedDn").ValueKind);
        Assert.False(root.GetProperty("isLoading").GetBoolean());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("loadError").ValueKind);

        await QuitGracefullyAsync(process);

        // The hermetic --state-dir seam's whole point: the real profile is never touched.
        Assert.Equal(hashBefore, HashIfExists(realUiStatePath));
    }

    // === 2. distinct seq values round-trip independently ========================================

    [Fact]
    public async Task StateCommand_TwoCallsWithDifferentSeq_EachEchoesItsOwnSeq()
    {
        var process = StartApp("--demo", "--e2e", "--state-dir", NewTempDir());

        await WriteRawLineAsync(process, """{"cmd":"state","seq":7}""");
        using (var first = await ReadJsonLineAsync(process, TimeSpan.FromSeconds(30)))
        {
            Assert.NotNull(first);
            Assert.Equal(7, first!.RootElement.GetProperty("reply").GetInt64());
        }

        await WriteRawLineAsync(process, """{"cmd":"state","seq":8}""");
        using (var second = await ReadJsonLineAsync(process, TimeSpan.FromSeconds(30)))
        {
            Assert.NotNull(second);
            Assert.Equal(8, second!.RootElement.GetProperty("reply").GetInt64());
        }

        await QuitGracefullyAsync(process);
    }

    // === 3. missing seq defaults to 0, never crashes =============================================

    [Fact]
    public async Task StateCommand_MissingSeq_DefaultsToZero_NeverCrashes()
    {
        var process = StartApp("--demo", "--e2e", "--state-dir", NewTempDir());

        await WriteRawLineAsync(process, """{"cmd":"state"}""");
        using var reply = await ReadJsonLineAsync(process, TimeSpan.FromSeconds(30));

        Assert.NotNull(reply);
        Assert.Equal(0, reply!.RootElement.GetProperty("reply").GetInt64());

        await QuitGracefullyAsync(process);
    }

    // === 4. "quit" closes the window: a GRACEFUL exit, never a kill ============================

    [Fact]
    public async Task QuitCommand_ClosesTheWindow_ProcessExitsOnItsOwn_WithinBoundedTime()
    {
        var process = StartApp("--demo", "--e2e", "--state-dir", NewTempDir());

        // Prove the channel is live first — a "quit" landing before the channel is armed would
        // exit "gracefully" for the wrong reason (the read loop never having started at all).
        await WriteRawLineAsync(process, """{"cmd":"state","seq":1}""");
        using (var reply = await ReadJsonLineAsync(process, TimeSpan.FromSeconds(30)))
        {
            Assert.NotNull(reply);
        }

        await QuitGracefullyAsync(process);

        Assert.True(process.HasExited, "MainWindow.Close() must leave the process exited, not merely closing the window");
    }

    // === 5. observe-only vocabulary (black-box): unrecognized/malformed input is silently
    //        ignored — never crashes the app, never wedges the channel for the NEXT command ======

    [Theory]
    [InlineData("""{"cmd":"click","target":"CN=GG_Sales,OU=Groups,DC=weavedemo,DC=example"}""")] // invented mutating verb
    [InlineData("""{"cmd":"expand","dn":"CN=GG_Sales,OU=Groups,DC=weavedemo,DC=example"}""")] // invented mutating verb
    [InlineData("""{"cmd":"connect","demo":true}""")] // invented mutating verb
    [InlineData("""{"cmd":"select","dn":"CN=X,DC=x"}""")] // invented mutating verb
    [InlineData("not json at all")]
    [InlineData("""{}""")] // object, no "cmd"
    [InlineData("42")] // valid JSON, not an object
    [InlineData("""{"cmd":123}""")] // "cmd" present but not a string
    [InlineData("""{"cmd":"state" """)] // truncated mid-object
    public async Task UnrecognizedOrMalformedCommand_IsSilentlyIgnored_ChannelStaysHealthy(string badLine)
    {
        var process = StartApp("--demo", "--e2e", "--state-dir", NewTempDir());

        await WriteRawLineAsync(process, badLine);

        // The bad line must never crash the app or wedge the read loop — a legitimate "state"
        // sent right behind it must still get a reply within the same bound as the happy path,
        // and the process must still be alive to answer it (ADR-038 D2: never-throw, no mutation,
        // no crash — ever, on any input).
        await WriteRawLineAsync(process, """{"cmd":"state","seq":99}""");
        using var reply = await ReadJsonLineAsync(process, TimeSpan.FromSeconds(30));

        Assert.NotNull(reply);
        Assert.Equal(99, reply!.RootElement.GetProperty("reply").GetInt64());
        Assert.False(process.HasExited, $"a bad line ('{badLine}') on the channel must never crash the app");

        await QuitGracefullyAsync(process);
    }

    // === helpers ================================================================================

    /// <summary>Launches the built binary via <c>dotnet &lt;dll&gt;</c> (the
    /// <see cref="AppCliTests"/>/<see cref="StateDirCliTests"/> idiom) with stdin ALSO
    /// redirected (the new surface this file drives) and UTF-8 pinned on both interactive
    /// streams — the channel's JSON is ASCII-only in every probe here, but pinning the codepage
    /// removes any ambient-console-codepage variable (lab-environment.md's CP850 default).
    /// Deliberately the NO-BOM <see cref="UTF8Encoding"/> instance (<c>Program.Utf8NoBom</c>'s
    /// own idiom): the default <c>Encoding.UTF8</c> singleton emits a 3-byte BOM preamble
    /// (U+FEFF, bytes EF BB BF) on the FIRST write through the <see cref="StreamWriter"/> .NET
    /// builds over stdin, which lands before the JSON text on the wire — <c>E2eChannel</c>'s
    /// <c>JsonDocument.Parse</c> then throws on that leading byte-order mark and the line is
    /// silently swallowed as malformed (ADR-038 D2's never-throw contract working exactly as
    /// designed), so the FIRST command ever sent would appear to hang forever. Found
    /// empirically while writing this file.
    /// stderr is drained in the background (never awaited to completion, tools/test-cli-matrix.ps1's
    /// "always drain every redirected stream" lesson) so it can never fill and deadlock a
    /// process this file interacts with over stdin/stdout instead of waiting-then-reading.</summary>
    private Process StartApp(params string[] args)
    {
        var appDll = FindAppBinary();
        var startInfo = new ProcessStartInfo
        {
            FileName = "dotnet",
            WorkingDirectory = Path.GetDirectoryName(appDll),
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = NoBomUtf8,
            StandardOutputEncoding = NoBomUtf8,
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add(appDll);
        foreach (var arg in args)
        {
            startInfo.ArgumentList.Add(arg);
        }

        var process = Process.Start(startInfo);
        Assert.NotNull(process);
        _processes.Add(process!);

        _ = process!.StandardError.ReadToEndAsync();

        return process;
    }

    private static async Task WriteRawLineAsync(Process process, string line)
    {
        await process.StandardInput.WriteLineAsync(line);
        await process.StandardInput.FlushAsync();
    }

    /// <summary>Bounded read of one stdout line, parsed as JSON. <c>null</c> on timeout — the
    /// caller's <see cref="Assert.NotNull{T}(T?)"/> turns a silent hang into a clear failure
    /// message rather than the test runner's own indefinite-wait timeout.</summary>
    private static async Task<JsonDocument?> ReadJsonLineAsync(Process process, TimeSpan timeout)
    {
        var lineTask = process.StandardOutput.ReadLineAsync();
        var winner = await Task.WhenAny(lineTask, Task.Delay(timeout));
        if (winner != lineTask)
        {
            return null;
        }

        var line = await lineTask;
        return line is null ? null : JsonDocument.Parse(line);
    }

    /// <summary>Sends <c>{"cmd":"quit"}</c> and bounded-waits for the process to exit ON ITS
    /// OWN (never killed here — <see cref="Dispose"/> is the only place that ever calls
    /// <see cref="Process.Kill(bool)"/>, and only for a process this helper's wait already
    /// failed on).</summary>
    private static async Task QuitGracefullyAsync(Process process)
    {
        await WriteRawLineAsync(process, """{"cmd":"quit"}""");

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            Assert.Fail("the process did not exit within 30s of {\"cmd\":\"quit\"} — MainWindow.Close() never fired");
        }
    }

    private static void KillIfAlive(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(5000);
            }
        }
        catch (InvalidOperationException)
        {
            // Already exited between the check and the kill — fine.
        }
        finally
        {
            process.Dispose();
        }
    }

    private string NewTempDir()
    {
        var dir = Directory.CreateTempSubdirectory("groupweaver-e2echannel-").FullName;
        _tempDirs.Add(dir);
        return dir;
    }

    private static string RealUiStatePath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "GroupWeaver", "ui-state.json");

    private static string? HashIfExists(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    /// <summary>
    /// Locates the App assembly built in the same configuration as this test run by walking up
    /// from the test output directory to the repo root (where <c>GroupWeaver.sln</c> lives) —
    /// the <see cref="AppCliTests"/>/<see cref="StateDirCliTests"/> idiom, duplicated per the
    /// codebase's per-file-helper convention.
    /// </summary>
    private static string FindAppBinary()
    {
#if DEBUG
        const string configuration = "Debug";
#else
        const string configuration = "Release";
#endif
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "GroupWeaver.sln")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        var appDll = Path.Combine(
            dir.FullName, "src", "App", "bin", configuration, "net8.0-windows", "GroupWeaver.App.dll");
        Assert.True(
            File.Exists(appDll),
            $"'{appDll}' not found — build the full solution first (pwsh tools/build.ps1).");
        return appDll;
    }
}
