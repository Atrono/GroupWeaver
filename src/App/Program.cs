using System.Reflection;
using System.Runtime.InteropServices;
using Avalonia;
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
