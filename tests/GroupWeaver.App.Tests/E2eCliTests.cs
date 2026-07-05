using System.Diagnostics;

using Xunit;

namespace GroupWeaver.App.Tests;

/// <summary>
/// Pins the <c>--e2e</c> CLI demo gate (ADR-038 D3.2, #245) at the PROCESS level, mirroring
/// <see cref="StateDirCliTests"/>'s idiom exactly (spawn the already-built binary via
/// <c>dotnet &lt;dll&gt;</c>, redirect BOTH streams, bounded 60 s wait, kill the whole tree on
/// timeout — an app that ignored the flag and fell through to the GUI lifetime must never leave
/// a zombie window behind a red run).
///
/// <para><b>A bare flag, no value form.</b> Unlike <c>--state-dir</c>, <c>--e2e</c> takes no
/// value (<c>Program.cs</c>: <c>args.Contains("--e2e")</c>) — there is no <c>--e2e=</c> form and
/// no "missing value" usage error to pin, so this file has exactly one refusal shape.</para>
///
/// <para>The channel's actual wire framing (once <c>--demo --e2e</c> IS accepted) is pinned in
/// <see cref="E2eChannelCliTests"/>, not here — this file is the demo-gate refusal only.</para>
/// </summary>
public sealed class E2eCliTests
{
    // === the demo gate: refuses WITHOUT --demo ================================================

    [Fact]
    public async Task E2e_WithoutDemo_Exits64_MentionsDemoOnly()
    {
        var (exitCode, _, stderr) = await RunAppAsync("--e2e");

        Assert.Equal(64, exitCode);
        Assert.Contains("demo-only", stderr, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("--e2e", stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task E2e_WithoutDemo_AlongsideOtherFlags_StillExits64()
    {
        // The gate reads args.Contains("--e2e") directly - order/position among other flags
        // (here --verbose-logs) must not matter.
        var (exitCode, _, stderr) = await RunAppAsync("--verbose-logs", "--e2e");

        Assert.Equal(64, exitCode);
        Assert.Contains("demo-only", stderr, StringComparison.OrdinalIgnoreCase);
    }

    // === --check wins outright: --e2e is GUI-path-only, never reached ==========================

    /// <summary>
    /// <c>--check --e2e</c> (no <c>--demo</c>): <c>Program.Main</c>'s <c>--check</c> branch
    /// returns BEFORE the <c>--e2e</c> demo-gate block is ever reached (it lives in the
    /// GUI-path-only section below the dump-graph/check early-returns), so this must exit
    /// EXACTLY like plain <c>--check</c> against the live lab DC would attempt to (never the
    /// demo-only refusal) — pinned here with <c>--demo</c> added so the assertion stays
    /// hermetic (lab-environment.md: bare <c>--check</c> binds the live DC).
    /// </summary>
    [Fact]
    public async Task CheckDemo_WithE2e_NeverHitsTheE2eGate_ExitsZeroLikePlainDemoCheck()
    {
        var (baselineExit, baselineStdout, _) = await RunAppAsync("--demo", "--check");
        var (exitCode, stdout, stderr) = await RunAppAsync("--check", "--demo", "--e2e");

        Assert.True(baselineExit == 0, $"baseline exited with {baselineExit}");
        Assert.True(exitCode == 0, $"app exited with {exitCode}; stderr: {stderr}");
        Assert.Equal(baselineStdout, stdout);
    }

    // === helpers (mirrors StateDirCliTests'/AppCliTests' idiom verbatim) =======================

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
