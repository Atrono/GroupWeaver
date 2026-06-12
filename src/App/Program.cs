using System.Reflection;
using System.Runtime.InteropServices;
using Avalonia;
using GroupWeaver.App.Graph;
using GroupWeaver.Core.Graph;
using GroupWeaver.Core.Providers;
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
            File.WriteAllText(path, GraphJson.SerializeFlat(GraphBuilder.Build(snapshot, rootDn)));
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
    }

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
