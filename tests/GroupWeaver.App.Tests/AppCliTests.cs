using System.Diagnostics;
using System.Text.Json;

using GroupWeaver.Tests;
using GroupWeaver.Tests.Providers.Ldap;

using Xunit;

namespace GroupWeaver.App.Tests;

/// <summary>
/// Executes the built App binary and pins the permanent <c>--check</c> console contract
/// (ADR-003 D4): the M1 DoD stdout lines and exit code 0. The demo-mode test was
/// deliberately relocated here from <c>DemoProviderTests</c> (AP 2.1 S2) — it exercises
/// the App executable's CLI surface, not the provider contract. AP 2.2 S4 adds the
/// <c>--dump-graph</c> contract (ADR-004 D7): demo-only flat graph JSON dumps — without
/// <c>--demo</c> the app exits 64, because live-AD structure must never reach artifacts.
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

    // --- --dump-graph (AP 2.2 S4, ADR-004 D7) ---------------------------------

    /// <summary>The demo dataset root (fewest RDN components), pinned against
    /// <c>src/Providers/Demo/demo-directory.json</c>'s <c>rootDn</c>.</summary>
    private const string DemoRootDn = "OU=AGDLP-Demo,DC=weavedemo,DC=example";

    [Fact]
    public async Task DemoDumpGraph_WritesFlatGraphJsonForTheDemoDirectory()
    {
        var path = TempDumpPath();
        try
        {
            var (exitCode, _, stderr) = await RunAppAsync("--demo", "--dump-graph", path);

            Assert.True(exitCode == 0, $"app exited with {exitCode}; stderr: {stderr}");
            Assert.True(File.Exists(path), $"'{path}' was not written");

            using var document = JsonDocument.Parse(await File.ReadAllTextAsync(path));
            var nodes = document.RootElement.GetProperty("nodes");
            var edges = document.RootElement.GetProperty("edges");

            // 194 demo objects plus the externally referenced member DNs.
            Assert.True(
                nodes.GetArrayLength() >= 190,
                $"expected >= 190 nodes, got {nodes.GetArrayLength()}");

            // Exactly one "root":true - the demo root OU, at the exact origin.
            var roots = nodes.EnumerateArray()
                .Where(n => n.TryGetProperty("root", out var root) && root.ValueKind == JsonValueKind.True)
                .ToList();
            var demoRoot = Assert.Single(roots);
            Assert.Equal(DemoRootDn, demoRoot.GetProperty("id").GetString());
            Assert.Equal(0d, demoRoot.GetProperty("x").GetDouble());
            Assert.Equal(0d, demoRoot.GetProperty("y").GetDouble());

            // The seeded GG_Circle_A <-> GG_Circle_B cycle must surface as at least
            // one antiparallel membership pair: some s/t and t/s both present.
            var membership = edges.EnumerateArray()
                .Where(e => e.GetProperty("rel").GetString() == "member")
                .Select(e => (S: e.GetProperty("s").GetString()!, T: e.GetProperty("t").GetString()!))
                .ToList();
            var pairs = membership.ToHashSet();
            Assert.Contains(membership, e => pairs.Contains((e.T, e.S)));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task DumpGraph_WithoutDemo_Exits64MentioningDemo_AndWritesNothing()
    {
        var path = TempDumpPath();
        try
        {
            var (exitCode, _, stderr) = await RunAppAsync("--dump-graph", path);

            // ADR-004 D7: live-AD structure must never reach artifacts - usage error 64.
            Assert.Equal(64, exitCode);
            Assert.Contains("demo", stderr, StringComparison.OrdinalIgnoreCase);
            Assert.False(File.Exists(path), $"'{path}' must never be written without --demo");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task DumpGraph_AsLastArgWithoutPath_ExitsNonzero()
    {
        var (exitCode, _, _) = await RunAppAsync("--demo", "--dump-graph");

        Assert.NotEqual(0, exitCode);
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
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            // An app that ignores a flag falls through to the GUI lifetime and never
            // exits - kill the whole tree so a red run leaves no zombie windows behind.
            process.Kill(entireProcessTree: true);
            Assert.Fail($"app did not exit within 60s (args: {string.Join(' ', args)})");
        }

        return (process.ExitCode, await stdoutTask, await stderrTask);
    }

    /// <summary>A non-existing temp path for a graph dump (never pre-created — the
    /// file-exists assertions must observe the app's own write).</summary>
    private static string TempDumpPath() =>
        Path.Combine(Path.GetTempPath(), $"groupweaver-dumpgraph-{Guid.NewGuid():N}.json");

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
