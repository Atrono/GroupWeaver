using System.DirectoryServices;
using System.Runtime.Versioning;

using Xunit;

namespace GroupWeaver.Tests.Providers.Ldap;

/// <summary>
/// <see cref="FactAttribute"/> for tests that need the live AGDLP-Lab fixtures:
/// sets <see cref="FactAttribute.Skip"/> when the lab OU is unreachable, so the
/// suite degrades to a loud skip instead of a failure cascade off the lab DC.
/// The probe runs at most once per test run: a base-scope
/// <see cref="DirectorySearcher"/> hit on <c>OU=AGDLP-Lab,DC=agdlp,DC=lab</c>
/// via server <c>localhost</c> with a short client timeout; any failure — not
/// Windows, no DC, closed port, missing OU — counts as unreachable. Tests
/// carrying this attribute must also carry the <c>Category=RequiresAd</c>
/// trait so CI excludes them regardless of the probe.
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
internal sealed class AdFactAttribute : FactAttribute
{
    /// <summary>Loud skip reason — on the lab DC these tests are mandatory.</summary>
    internal const string UnreachableSkipReason =
        "WARNING: OU=AGDLP-Lab unreachable from this box - RequiresAd integration tests " +
        "SKIPPED; they are mandatory on the lab DC (CLAUDE.md).";

    private static readonly Lazy<bool> Probe = new(ProbeLab);

    public AdFactAttribute()
    {
        if (!IsLabReachable)
        {
            Skip = UnreachableSkipReason;
        }
    }

    /// <summary>Cached result of the once-per-run reachability probe. Shared with
    /// fixtures so they do not try to eager-load an unreachable directory (their
    /// tests are all skipped anyway).</summary>
    internal static bool IsLabReachable => Probe.Value;

    private static bool ProbeLab() => OperatingSystem.IsWindows() && ProbeLabOnWindows();

    [SupportedOSPlatform("windows")]
    private static bool ProbeLabOnWindows()
    {
        try
        {
            using var root = new DirectoryEntry("LDAP://localhost/OU=AGDLP-Lab,DC=agdlp,DC=lab");
            using var searcher = new DirectorySearcher(root)
            {
                Filter = "(objectClass=*)",
                SearchScope = SearchScope.Base,
                ClientTimeout = TimeSpan.FromSeconds(3),
            };
            searcher.PropertiesToLoad.Add("distinguishedName");
            return searcher.FindOne() is not null;
        }
        catch
        {
            // Any failure means unreachable; the catch-all is deliberate — a probe
            // must never take the whole test run down.
            return false;
        }
    }
}
