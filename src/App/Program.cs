using System.Reflection;
using GroupWeaver.Core.Providers;
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

        if (args.Contains("--demo"))
        {
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

        if (!OperatingSystem.IsWindows())
        {
            Console.Error.WriteLine("the LDAP provider requires Windows - try --demo for the embedded demo directory");
            return 1;
        }

        try
        {
            var provider = new LdapProvider();
            var connection = await provider.ConnectAsync();
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
}
