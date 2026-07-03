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

    // --- --dump-graph carries severity (AP 3.4 S2, ADR-010 D3) ----------------

    /// <summary>
    /// AP 3.4 S2: the demo dump runs <c>RuleEngine.Evaluate(snapshot, default-ruleset)</c>
    /// and joins the report into the wire (ADR-010 D2/D3), so the Playwright fixture
    /// carries severity. The AP 3.2 demo baseline (rule-engine.md) is exactly 19 findings —
    /// 3 nesting + 1 cycle ERRORS, 3 naming WARNINGS, 12 empty-group INFOS — so all three
    /// <c>sev</c> tokens are present by construction, and the nesting parents (whose loaded
    /// transitive members are flagged) carry a <c>below</c> roll-up. Pre-S2 this is RED:
    /// <c>Program.cs</c> calls the no-report <c>SerializeFlat</c> overload, which delegates
    /// with <see cref="RuleReport.Empty"/> and a null below-map — zero <c>sev</c>/<c>below</c>
    /// keys reach the wire.
    /// </summary>
    [Fact]
    public async Task DemoDumpGraph_NodesCarrySeverityAndRollup()
    {
        var path = TempDumpPath();
        try
        {
            var (exitCode, _, stderr) = await RunAppAsync("--demo", "--dump-graph", path);

            Assert.True(exitCode == 0, $"app exited with {exitCode}; stderr: {stderr}");

            using var document = JsonDocument.Parse(await File.ReadAllTextAsync(path));
            var nodes = document.RootElement.GetProperty("nodes").EnumerateArray().ToList();

            // The camelCase wire field is "sev" with lowercase tokens (GraphJson.SeverityWire).
            int SevCount(string token) => nodes.Count(n =>
                n.TryGetProperty("sev", out var sev)
                && sev.ValueKind == JsonValueKind.String
                && sev.GetString() == token);

            // The demo baseline guarantees every severity band is represented on the graph.
            Assert.True(SevCount("error") >= 1, "expected >= 1 node with sev:\"error\"");
            Assert.True(SevCount("warning") >= 1, "expected >= 1 node with sev:\"warning\"");
            Assert.True(SevCount("info") >= 1, "expected >= 1 node with sev:\"info\"");

            // At least one loaded group hides flagged descendants -> a "below" roll-up
            // count (emitted only when > 0; int-valued, camelCased from NodeDto.Below).
            var belowNodes = nodes
                .Where(n => n.TryGetProperty("below", out var below) && below.ValueKind == JsonValueKind.Number)
                .ToList();
            Assert.True(belowNodes.Count >= 1, "expected >= 1 node carrying a \"below\" roll-up field");
            Assert.All(belowNodes, n => Assert.True(
                n.GetProperty("below").GetInt32() > 0,
                "a \"below\" field must never be emitted as <= 0"));
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// The demo-only guard (ADR-004 D7) is unchanged by the S2 severity join: severity is
    /// computed only on the demo path, and live-AD structure still never reaches an
    /// artifact — without <c>--demo</c>, <c>--dump-graph</c> exits 64 and writes nothing.
    /// </summary>
    [Fact]
    public async Task DumpGraph_WithoutDemo_StillExits64_AfterSeverityJoin()
    {
        var path = TempDumpPath();
        try
        {
            var (exitCode, _, _) = await RunAppAsync("--dump-graph", path);

            Assert.Equal(64, exitCode);
            Assert.False(File.Exists(path), $"'{path}' must never be written without --demo");
        }
        finally
        {
            File.Delete(path);
        }
    }

    // --- --dump-graph determinism (WP3 / #242, ADR-038) -----------------------

    /// <summary>
    /// WP3 (#242): two independent runs must produce byte-identical dumps. The wire is
    /// derived purely from the embedded demo dataset, the embedded default ruleset, and
    /// pinned deterministic ordering (GraphBuilder's ordinal sorts, the RuleEngine
    /// determinism contract) — no timestamps, GUIDs, or hash-order dependence may ever
    /// leak in. Pinned at the PROCESS level in the house AppCliTests idiom (the app is
    /// WinExe; <c>Main</c> is <c>[STAThread]</c> with process-global console state, so
    /// in-proc invocation is not the sanctioned surface); the same byte-identity check
    /// runs process-level in the <c>tools/test-cli-matrix.ps1</c> gate.
    /// </summary>
    [Fact]
    public async Task DemoDumpGraph_TwoRuns_AreByteIdentical()
    {
        var first = TempDumpPath();
        var second = TempDumpPath();
        try
        {
            var (exitFirst, _, stderrFirst) = await RunAppAsync("--demo", "--dump-graph", first);
            var (exitSecond, _, stderrSecond) = await RunAppAsync("--demo", "--dump-graph", second);

            Assert.True(exitFirst == 0, $"first run exited with {exitFirst}; stderr: {stderrFirst}");
            Assert.True(exitSecond == 0, $"second run exited with {exitSecond}; stderr: {stderrSecond}");

            var firstBytes = await File.ReadAllBytesAsync(first);
            var secondBytes = await File.ReadAllBytesAsync(second);
            Assert.True(firstBytes.Length > 0, "the dump must not be empty");
            Assert.Equal(firstBytes, secondBytes);
        }
        finally
        {
            File.Delete(first);
            File.Delete(second);
        }
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
