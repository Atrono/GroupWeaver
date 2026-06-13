using System.Reflection;
using System.Runtime.InteropServices;
using Avalonia;
using GroupWeaver.App.Graph;
using GroupWeaver.Core.Graph;
using GroupWeaver.Core.Model;
using GroupWeaver.Core.Providers;
using GroupWeaver.Core.Rules;
using GroupWeaver.Providers;

namespace GroupWeaver.App;

internal static class Program
{
    /// <summary>
    /// Synchronous on purpose: <c>[STAThread]</c> on an async <c>Main</c> silently loses
    /// the STA apartment (the await machinery resumes on thread-pool MTA threads).
    /// </summary>
    [STAThread]
    internal static int Main(string[] args)
    {
        var demo = args.Contains("--demo");
        if (args.Contains("--check"))
        {
            return RunCheck(demo);
        }

        var dumpGraphIndex = Array.IndexOf(args, "--dump-graph");
        if (dumpGraphIndex >= 0)
        {
            return RunDumpGraph(demo, dumpGraphIndex + 1 < args.Length ? args[dumpGraphIndex + 1] : null);
        }

        App.StartupOptions = new StartupOptions(Demo: demo);
        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    /// <summary>
    /// AppBuilder seam referenced by the headless test harness (ADR-003 D6) — keep this
    /// conventional shape.
    /// </summary>
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>().UsePlatformDetect().LogToTrace();

    /// <summary>
    /// Headless connectivity probe — the permanent M1 smoke/diagnostic command (ADR-003 D4).
    /// Never initializes Avalonia; output lines are pinned by the M1 stdout test.
    /// </summary>
    private static int RunCheck(bool demo)
    {
        EnsureConsole();

        var version = Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion ?? "unknown";

        Console.WriteLine($"GroupWeaver {version}");

        if (demo)
        {
            try
            {
                var provider = new DemoProvider();
                var connection = provider.ConnectAsync().GetAwaiter().GetResult();
                Console.WriteLine(connection.Description);
                Console.WriteLine($"connected, {connection.GroupCount} groups loaded");
                return 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(ex.Message);
                return 1;
            }
        }

        if (!OperatingSystem.IsWindows())
        {
            Console.Error.WriteLine("the LDAP provider requires Windows - try --demo for the embedded demo directory");
            return 1;
        }

        try
        {
            var provider = new LdapProvider();
            var connection = provider.ConnectAsync().GetAwaiter().GetResult();
            Console.WriteLine(connection.Description);
            Console.WriteLine($"connected, {connection.GroupCount} groups loaded");
            return 0;
        }
        catch (DirectoryUnavailableException ex)
        {
            Console.Error.WriteLine(ex.Message);
            Console.Error.WriteLine("no domain reachable in this user context - try --demo for the embedded demo directory");
            return 1;
        }
    }

    /// <summary>
    /// Headless flat graph dump — the permanent fixture/diagnostic command (ADR-004 D7).
    /// Demo-only by design: without <c>--demo</c> it exits 64, because live-AD structure
    /// must never reach artifacts (public-media rule). Never initializes Avalonia;
    /// contract pinned by the <c>--dump-graph</c> tests in <c>AppCliTests</c>.
    /// </summary>
    private static int RunDumpGraph(bool demo, string? path)
    {
        EnsureConsole();

        if (!demo)
        {
            Console.Error.WriteLine(
                "--dump-graph is demo-only: live-AD structure must never reach artifacts - re-run with --demo");
            return 64;
        }

        if (string.IsNullOrEmpty(path))
        {
            Console.Error.WriteLine("usage: GroupWeaver --demo --dump-graph <path>");
            return 64;
        }

        try
        {
            var provider = new DemoProvider();
            provider.ConnectAsync().GetAwaiter().GetResult();

            // The demo dataset root: the candidate closest to the directory root,
            // i.e. with the fewest RDN components (escape-aware), ordinal tie-break.
            var rootDn = provider.GetRootCandidatesAsync().GetAwaiter().GetResult()
                .OrderBy(candidate => RdnComponentCount(candidate.Dn))
                .ThenBy(candidate => candidate.Dn, StringComparer.Ordinal)
                .First().Dn;

            var snapshot = provider.LoadScopeAsync(rootDn).GetAwaiter().GetResult();

            // AP 3.4 S2 (ADR-010 D3): run the pure engine with the embedded default
            // ruleset and join the report into the wire, so the Playwright fixture
            // carries severity. Demo-only path — live-AD structure never reaches here.
            var report = RuleEngine.Evaluate(snapshot, RulesetLoader.LoadDefault());
            var belowMap = ComputeBelow(snapshot, report);

            File.WriteAllText(
                path, GraphJson.SerializeFlat(GraphBuilder.Build(snapshot, rootDn), report, belowMap));
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
    }

    /// <summary>
    /// The roll-up "n below" map (ADR-010 D4): for every LOADED fetchable-kind node, the
    /// distinct findings among its transitive LOADED descendants — count + max severity.
    /// The ONLY sanctioned transitive walk is <see cref="MembershipTraversal.Walk"/>
    /// (data-model.md); <c>Skip(1)</c> drops the node itself (its own severity is the
    /// per-node <c>sev</c>, not a roll-up). Loaded-only by construction — Walk reads
    /// <c>GetMembers</c> per node, so a never-loaded child is excluded, never fetched.
    /// </summary>
    private static IReadOnlyDictionary<string, (int Count, RuleSeverity Sev)> ComputeBelow(
        DirectorySnapshot snapshot, RuleReport report)
    {
        var map = new Dictionary<string, (int Count, RuleSeverity Sev)>(Dn.Comparer);
        if (report.Violations.Count == 0)
        {
            return map; // cheap exit: no findings means no roll-ups anywhere.
        }

        foreach (var obj in snapshot.Objects)
        {
            if (!IsFetchableKind(obj.Kind) || !snapshot.IsLoaded(obj.Dn))
            {
                continue;
            }

            var below = report.ViolationsAmong(MembershipTraversal.Walk(snapshot, obj.Dn).Visited.Skip(1));
            if (below.Count == 0)
            {
                continue;
            }

            map[obj.Dn] = (below.Count, below.Max(v => v.Severity));
        }

        return map;
    }

    /// <summary>ADR-005's fetchable kinds — the group scopes plus External (what a
    /// double-click would fetch). The roll-up anchors are exactly the nodes whose
    /// transitive membership can be loaded; leaves never carry a "below".</summary>
    private static bool IsFetchableKind(AdObjectKind kind) => kind
        is AdObjectKind.GlobalGroup
        or AdObjectKind.DomainLocalGroup
        or AdObjectKind.UniversalGroup
        or AdObjectKind.External;

    /// <summary>How many RDN components <paramref name="dn"/> has — unescaped-comma
    /// separated, counted via the escape-aware <see cref="DnPath"/> walker.</summary>
    private static int RdnComponentCount(string dn)
    {
        var count = 1;
        for (var ancestor = DnPath.Parent(dn); ancestor is not null; ancestor = DnPath.Parent(ancestor))
        {
            count++;
        }

        return count;
    }

    // ---------- console attachment shim (WinExe has no console of its own) ----------

    private const int StdOutputHandle = -11;
    private const int StdErrorHandle = -12;
    private const uint AttachParentProcess = uint.MaxValue; // ATTACH_PARENT_PROCESS

    private static readonly IntPtr InvalidHandleValue = new(-1);

    /// <summary>
    /// OutputType WinExe means an interactively launched process starts with no std
    /// handles at all. Attach to the parent's console in that case so `--check` output
    /// reaches the terminal. Redirected pipes (tests/CI) and console hosts (`dotnet`)
    /// already have valid handles and are left untouched.
    /// </summary>
    private static void EnsureConsole()
    {
        var stdoutMissing = IsHandleMissing(GetStdHandle(StdOutputHandle));
        var stderrMissing = IsHandleMissing(GetStdHandle(StdErrorHandle));
        if (!stdoutMissing && !stderrMissing)
        {
            return; // inherited console or redirected — writing already works
        }

        if (!AttachConsole(AttachParentProcess))
        {
            return; // no parent console (e.g. double-clicked) — nowhere to write
        }

        // The runtime cached the NUL handles at process start; re-bind only the
        // streams that were missing (a partially redirected stream keeps its pipe).
        if (stdoutMissing)
        {
            Console.SetOut(new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true });
        }

        if (stderrMissing)
        {
            Console.SetError(new StreamWriter(Console.OpenStandardError()) { AutoFlush = true });
        }
    }

    private static bool IsHandleMissing(IntPtr handle) =>
        handle == IntPtr.Zero || handle == InvalidHandleValue;

    // Classic DllImport (not LibraryImport): the source-generated marshalling would
    // force AllowUnsafeBlocks onto the whole App project for two trivial signatures.
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AttachConsole(uint dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GetStdHandle(int nStdHandle);
}
