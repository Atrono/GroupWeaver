using System.Diagnostics;

using Xunit;

namespace GroupWeaver.App.Tests;

/// <summary>
/// Pins the <c>--state-dir</c> CLI seam (ADR-038 D3.1, #244) at the PROCESS level, mirroring
/// <see cref="AppCliTests"/>'s idiom exactly (spawn the already-built binary via <c>dotnet
/// &lt;dll&gt;</c>, redirect BOTH streams, bounded 60 s wait, kill the whole tree on timeout —
/// an app that ignored the flag and fell through to the GUI lifetime must never leave a zombie
/// window behind a red run).
///
/// <para><b>Refusal cases never touch the path at all.</b> <c>Program.cs</c>'s <c>--state-dir</c>
/// block returns 64 BEFORE <c>Path.GetFullPath</c>/<c>Directory.CreateDirectory</c> ever run on
/// the value (the demo gate and the missing-value check both short-circuit first) — so "the tmp
/// dir stays empty" is pinned here as the STRONGER "the tmp dir is never even created"
/// (<c>Directory.Exists</c> is false), not "created then emptied".</para>
///
/// <para><b>--check wins over --state-dir.</b> <c>Program.Main</c> tests <c>args.Contains("--check")</c>
/// and returns from <see cref="Program"/>'s check branch BEFORE the <c>--state-dir</c>-parsing
/// block is even reached — bare <c>--check</c> would bind the LIVE lab DC (a different, non-hermetic
/// outcome depending on DC reachability), so every probe here that exercises the check branch adds
/// <c>--demo</c> to stay hermetic, per the WP5 brief.</para>
/// </summary>
public sealed class StateDirCliTests
{
    // === 1. demo gate: refuses WITHOUT --demo, in both flag forms ============================

    [Fact]
    public async Task StateDir_SpaceForm_WithoutDemo_Exits64_MentionsDemoOnly_NeverCreatesTheDir()
    {
        var stateDir = TempStateDirPath();

        var (exitCode, _, stderr) = await RunAppAsync("--state-dir", stateDir);

        Assert.Equal(64, exitCode);
        Assert.Contains("demo-only", stderr, StringComparison.OrdinalIgnoreCase);
        Assert.False(Directory.Exists(stateDir), $"'{stateDir}' must never be created without --demo");
    }

    [Fact]
    public async Task StateDir_EqualsForm_WithoutDemo_Exits64_MentionsDemoOnly_NeverCreatesTheDir()
    {
        var stateDir = TempStateDirPath();

        var (exitCode, _, stderr) = await RunAppAsync($"--state-dir={stateDir}");

        Assert.Equal(64, exitCode);
        Assert.Contains("demo-only", stderr, StringComparison.OrdinalIgnoreCase);
        Assert.False(Directory.Exists(stateDir), $"'{stateDir}' must never be created without --demo");
    }

    // === 2. missing value: usage error, exit 64, no window/hang ===============================

    [Fact]
    public async Task StateDir_AsLastArgWithoutValue_EvenWithDemo_Exits64WithUsageLine()
    {
        var (exitCode, _, stderr) = await RunAppAsync("--demo", "--state-dir");

        Assert.Equal(64, exitCode);
        Assert.Contains("usage", stderr, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("--state-dir", stderr, StringComparison.Ordinal);
    }

    // === 3. --check wins over --state-dir: byte-identical stdout, hermetic ====================

    /// <summary>
    /// <c>--check --demo --state-dir &lt;tmp&gt;</c>: the <c>--check</c> branch wins outright —
    /// exit 0, and the pinned demo-check stdout (<see cref="AppCliTests.DemoCheck_PrintsM1DoDLinesAndExitsZero"/>'s
    /// contract) is BYTE-UNCHANGED by the extra flag, because <c>--state-dir</c> is parsed only
    /// on the GUI path, which the check branch never reaches. CAREFUL (lab-environment.md):
    /// bare <c>--check</c> binds the live lab DC — <c>--demo</c> is mandatory here to stay hermetic.
    /// </summary>
    [Fact]
    public async Task CheckDemo_WithStateDir_ExitsZero_ByteIdenticalToPlainDemoCheck_AndNeverCreatesTheDir()
    {
        var stateDir = TempStateDirPath();

        var (baselineExit, baselineStdout, baselineStderr) = await RunAppAsync("--demo", "--check");
        var (exitCode, stdout, stderr) = await RunAppAsync("--check", "--demo", "--state-dir", stateDir);

        Assert.True(baselineExit == 0, $"baseline exited with {baselineExit}; stderr: {baselineStderr}");
        Assert.True(exitCode == 0, $"app exited with {exitCode}; stderr: {stderr}");
        Assert.Equal(baselineStdout, stdout);
        Assert.Equal(baselineStderr, stderr);

        Assert.False(
            Directory.Exists(stateDir),
            $"'{stateDir}' must never be created by a --check run — the --check branch returns before " +
            "the --state-dir block is ever reached");
    }

    // === helpers (mirrors AppCliTests' idiom verbatim) ========================================

    private static string TempStateDirPath() =>
        Path.Combine(Path.GetTempPath(), $"groupweaver-statedir-cli-{Guid.NewGuid():N}");

    private static async Task<(int ExitCode, string StdOut, string StdErr)> RunAppAsync(params string[] args)
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

        using var process = Process.Start(startInfo);
        Assert.NotNull(process);

        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            // An app that ignores a flag falls through to the GUI lifetime and never exits —
            // kill the whole tree so a red run leaves no zombie windows behind.
            process.Kill(entireProcessTree: true);
            Assert.Fail($"app did not exit within 60s (args: {string.Join(' ', args)})");
        }

        return (process.ExitCode, await stdoutTask, await stderrTask);
    }

    /// <summary>
    /// Locates the App assembly built in the same configuration as this test run by walking up
    /// from the test output directory to the repo root (where <c>GroupWeaver.sln</c> lives).
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
