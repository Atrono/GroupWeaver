using System.Diagnostics;

using GroupWeaver.Tests;
using GroupWeaver.Tests.Providers.Ldap;

using Xunit;

namespace GroupWeaver.App.Tests;

/// <summary>
/// Executes the built App binary and pins the permanent <c>--check</c> console contract
/// (ADR-003 D4): the M1 DoD stdout lines and exit code 0. The demo-mode test was
/// deliberately relocated here from <c>DemoProviderTests</c> (AP 2.1 S2) — it exercises
/// the App executable's CLI surface, not the provider contract.
/// </summary>
public sealed class AppCliTests
{
    [Fact]
    public async Task DemoCheck_PrintsM1DoDLinesAndExitsZero()
    {
        var (exitCode, stdout, stderr) = await RunAppAsync("--demo", "--check");

        Assert.True(exitCode == 0, $"app exited with {exitCode}; stderr: {stderr}");
        Assert.StartsWith("GroupWeaver ", stdout, StringComparison.Ordinal);
        Assert.Contains(
            "demo mode: embedded fake directory 'weavedemo.example' (40 groups, 194 objects)",
            stdout,
            StringComparison.Ordinal);
        Assert.Contains("connected, 40 groups loaded", stdout, StringComparison.Ordinal);
    }

    /// <summary>
    /// Plain <c>--check</c> against the live lab DC. Deliberately loose assertions:
    /// exit code plus the invariant <c>"connected, "</c> prefix only — the DC is
    /// German-localized and its group count drifts with reseeds, so nothing localized
    /// or counted is pinned.
    /// </summary>
    [AdFact]
    [Trait(TestCategories.Category, TestCategories.RequiresAd)]
    public async Task LiveCheck_ConnectsAndExitsZero()
    {
        var (exitCode, stdout, stderr) = await RunAppAsync("--check");

        Assert.True(exitCode == 0, $"app exited with {exitCode}; stderr: {stderr}");
        Assert.Contains("connected, ", stdout, StringComparison.Ordinal);
    }

    // --- helpers -------------------------------------------------------------

    /// <summary>
    /// Runs the already-built App binary (no nested <c>dotnet run</c>/build —
    /// <c>tools/build.ps1</c> builds the full solution before testing) and collects
    /// exit code, stdout, and stderr.
    /// </summary>
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
        await process.WaitForExitAsync(timeout.Token);

        return (process.ExitCode, await stdoutTask, await stderrTask);
    }

    /// <summary>
    /// Locates the App assembly built in the same configuration as this test run
    /// by walking up from the test output directory to the repo root (where
    /// <c>GroupWeaver.sln</c> lives).
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
