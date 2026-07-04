using System.Diagnostics;
using System.Security.Cryptography;

using Xunit;

namespace GroupWeaver.App.Tests;

/// <summary>
/// Pins the <c>GROUPWEAVER_LOG_DIR</c>-vs-<c>--state-dir</c> log-directory precedence
/// (ADR-037 D3 x ADR-038 D3.1, #244) at the PROCESS level: an EXPLICIT env var always wins;
/// only when it is unset does the sink follow the hermetic state dir
/// (<c>Program.cs</c>'s precedence comment in the <c>--state-dir</c> block).
///
/// <para><b>Why a full GUI launch:</b> the precedence logic lives inline in
/// <c>Program.Main</c>'s <c>--state-dir</c> block, which is GUI-path-only (the console-only
/// <c>--check</c>/<c>--dump-graph</c> branches return earlier and never reach it) — there is no
/// smaller in-proc seam to pin this at. Every probe here is therefore a bounded POLL for the
/// sink's <c>gw-*.jsonl</c> file to appear (the sink creates its — initially empty — file in its
/// constructor, well before the AppStarted line is ever written, so this resolves in well under a
/// second once the process is past its .NET/Avalonia cold start), followed by an immediate
/// process-tree kill. No sleep-only loop, no UI automation, no window interaction — windowed
/// journeys are already covered by <c>tools/e2e/scenarios/*.ps1</c>.</para>
///
/// <para><b>Real-%APPDATA%-untouched invariant:</b> the real
/// <c>%APPDATA%\GroupWeaver\ui-state.json</c> is hashed before/after every probe here and
/// asserted byte-unchanged — the whole point of the seam (mirrors
/// <c>tools/e2e/scenarios/audit-run-persist.ps1</c>'s invariant, pinned here headlessly instead
/// of through a windowed journey).</para>
/// </summary>
public sealed class StateDirLogPrecedenceTests : IDisposable
{
    private readonly List<string> _tempDirs = [];

    public void Dispose()
    {
        foreach (var dir in _tempDirs)
        {
            try
            {
                Directory.Delete(dir, recursive: true);
            }
            catch (IOException)
            {
                // Covers DirectoryNotFoundException too (an IOException subclass) — the temp
                // dir may already be gone (e.g. the app's own shutdown raced our cleanup).
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    [Fact]
    public async Task ExplicitLogDirEnvVar_WinsOverStateDir_LogsLandInTheEnvDir_NeverUnderStateDir()
    {
        var realUiStatePath = RealUiStatePath();
        var hashBefore = HashIfExists(realUiStatePath);

        var logDir = NewTempDir("logdir-env-");
        var stateDir = NewTempDir("statedir-a-");

        var process = StartApp(
            args: ["--demo", "--state-dir", stateDir, "--verbose-logs"],
            explicitLogDirEnv: logDir);
        try
        {
            var logFile = await PollForLogFileAsync(logDir, TimeSpan.FromSeconds(30));
            Assert.True(logFile is not null, $"no gw-*.jsonl appeared under '{logDir}' within 30s");

            var stateDirLogs = Path.Combine(stateDir, "GroupWeaver", "logs");
            Assert.False(
                Directory.Exists(stateDirLogs) && Directory.GetFiles(stateDirLogs, "gw-*.jsonl").Length > 0,
                $"'{stateDirLogs}' must stay empty of gw-*.jsonl — the explicit env var must win");
        }
        finally
        {
            KillEntireTree(process);
        }

        Assert.Equal(hashBefore, HashIfExists(realUiStatePath));
    }

    [Fact]
    public async Task NoLogDirEnvVar_StateDirDrivesTheSink_UnderStateDirGroupWeaverLogs()
    {
        var realUiStatePath = RealUiStatePath();
        var hashBefore = HashIfExists(realUiStatePath);

        var stateDir = NewTempDir("statedir-b-");
        var stateDirLogs = Path.Combine(stateDir, "GroupWeaver", "logs");

        var process = StartApp(
            args: ["--demo", "--state-dir", stateDir, "--verbose-logs"],
            explicitLogDirEnv: null,
            removeInheritedLogDirEnv: true);
        try
        {
            var logFile = await PollForLogFileAsync(stateDirLogs, TimeSpan.FromSeconds(30));
            Assert.True(logFile is not null, $"no gw-*.jsonl appeared under '{stateDirLogs}' within 30s");
        }
        finally
        {
            KillEntireTree(process);
        }

        Assert.Equal(hashBefore, HashIfExists(realUiStatePath));
    }

    // === helpers ===============================================================================

    private string NewTempDir(string prefix)
    {
        var dir = Directory.CreateTempSubdirectory($"groupweaver-{prefix}").FullName;
        _tempDirs.Add(dir);
        return dir;
    }

    /// <summary>Launches the built binary via <c>dotnet &lt;dll&gt;</c> (the <see cref="AppCliTests"/>/
    /// <see cref="StateDirCliTests"/> idiom) WITHOUT waiting for exit — the caller polls the
    /// filesystem, then kills. Both streams are drained (never awaited to completion) so the
    /// redirected pipes can never fill and deadlock a process that is never waited-on
    /// (tools/test-cli-matrix.ps1's "always redirect both streams" lesson).</summary>
    private static Process StartApp(string[] args, string? explicitLogDirEnv, bool removeInheritedLogDirEnv = false)
    {
        var appDll = FindAppBinary();
        var startInfo = new ProcessStartInfo
        {
            FileName = "dotnet",
            WorkingDirectory = Path.GetDirectoryName(appDll),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add(appDll);
        foreach (var arg in args)
        {
            startInfo.ArgumentList.Add(arg);
        }

        if (removeInheritedLogDirEnv)
        {
            startInfo.Environment.Remove("GROUPWEAVER_LOG_DIR");
        }

        if (explicitLogDirEnv is not null)
        {
            startInfo.Environment["GROUPWEAVER_LOG_DIR"] = explicitLogDirEnv;
        }

        var process = Process.Start(startInfo);
        Assert.NotNull(process);

        _ = process!.StandardOutput.ReadToEndAsync();
        _ = process.StandardError.ReadToEndAsync();
        return process;
    }

    /// <summary>Bounded poll (250 ms steps, no sleep-only loop — bails the instant the file
    /// appears) for a <c>gw-*.jsonl</c> file under <paramref name="directory"/>.</summary>
    private static async Task<string?> PollForLogFileAsync(string directory, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (Directory.Exists(directory))
            {
                var files = Directory.GetFiles(directory, "gw-*.jsonl");
                if (files.Length > 0)
                {
                    return files[0];
                }
            }

            await Task.Delay(250);
        }

        return null;
    }

    private static void KillEntireTree(Process process)
    {
        try
        {
            using (process)
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                    process.WaitForExit(5000);
                }
            }
        }
        catch (InvalidOperationException)
        {
            // Already exited between the check and the kill — fine.
        }
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
