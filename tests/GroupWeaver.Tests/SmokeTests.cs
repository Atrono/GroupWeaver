using System.Reflection;

using Xunit;

namespace GroupWeaver.Tests;

/// <summary>
/// Day-1 smoke tests that pin scaffold contracts. Real TDD starts with AP 1.3.
/// </summary>
public class SmokeTests
{
    [Fact]
    public void CoreAssembly_InformationalVersion_StartsWithPinnedVersion()
    {
        // Directory.Build.props pins <Version>0.4.4</Version> (patch bump from
        // 0.4.3 — the 2026-07-01 fit-audit follow-through: glyph-control
        // accessible names + F1 help, secondary-surface action hierarchy,
        // graph-overlay ARIA completion (ADR-035), brand-purple selection +
        // kind-badge lift-ring, and Audit ring/tile legibility); the App
        // banner (src/App/Program.cs) prints the informational version derived from
        // it. The SDK appends "+<commit>" via SourceLink, hence StartsWith.
        var core = Assembly.Load("GroupWeaver.Core");
        var informationalVersion = core
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion;

        Assert.NotNull(informationalVersion);
        Assert.StartsWith("0.4.4", informationalVersion);
    }

    [Fact]
    public void BuildGate_SkipAdTestsFilter_MatchesTraitConvention()
    {
        // tools/build.ps1 -SkipAdTests must exclude exactly the trait that AD-dependent
        // tests carry (see TestCategories). This guard fails if either side of the
        // contract is renamed without the other.
        var buildScript = Path.Combine(FindRepoRoot(), "tools", "build.ps1");
        var content = File.ReadAllText(buildScript);

        Assert.Contains($"{TestCategories.Category}!={TestCategories.RequiresAd}", content);
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "GroupWeaver.sln")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException(
            "GroupWeaver.sln not found in any parent of the test output directory.");
    }
}
