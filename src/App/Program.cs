using System.Reflection;
using GroupWeaver.Providers;

namespace GroupWeaver.App;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        var version = Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion ?? "unknown";

        Console.WriteLine($"GroupWeaver {version}");

        if (!args.Contains("--demo"))
        {
            Console.WriteLine("no provider selected: the LDAP provider lands with AP 1.5 — use --demo for the embedded demo directory");
            return 0;
        }

        try
        {
            var provider = new DemoProvider();
            var connection = await provider.ConnectAsync();
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
}
