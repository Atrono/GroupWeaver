using System.Reflection;

namespace GroupWeaver.App;

internal static class Program
{
    private static int Main(string[] args)
    {
        var version = Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion ?? "unknown";

        Console.WriteLine($"GroupWeaver {version}");

        if (args.Contains("--demo"))
        {
            Console.WriteLine("demo mode: provider not yet implemented (AP 1.4)");
        }

        return 0;
    }
}
