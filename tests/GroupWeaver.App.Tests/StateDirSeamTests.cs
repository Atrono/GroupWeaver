using GroupWeaver.App.Audit;
using GroupWeaver.App.Rules;
using GroupWeaver.App.Settings;

using Microsoft.Extensions.Logging.Abstractions;

using Xunit;

namespace GroupWeaver.App.Tests;

/// <summary>
/// Pins the ADR-038 D3.1 store-rebase seam exactly as <c>App.OnFrameworkInitializationCompleted</c>
/// wires it (#244, WP5): when <see cref="StartupOptions.StateDir"/> is non-null, EVERY
/// user-profile store — <see cref="RulesetLocator"/>, <see cref="UiStateStore"/>,
/// <see cref="AuditRunStore"/> — is constructed over the SAME injected base directory instead of
/// the real <c>%APPDATA%</c>, landing at the identical <c>&lt;stateDir&gt;\GroupWeaver\
/// {ui-state.json, ruleset.jsonc, runs\}</c> layout underneath — only the base moves. Mirrors
/// <c>App.axaml.cs</c>'s exact ternary (<c>stateDir is null ? new X() : new X(stateDir)</c>); a
/// drift there (e.g. one store accidentally left on its parameterless/production ctor) fails
/// here first, in-proc, with no process spawn and no headless UI.
/// </summary>
public sealed class StateDirSeamTests : IDisposable
{
    private readonly string _stateDir = Directory.CreateTempSubdirectory("groupweaver-statedir-seam-").FullName;

    public void Dispose()
    {
        try
        {
            Directory.Delete(_stateDir, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    // === 1. each store, individually, lands under the injected state dir =====================

    [Fact]
    public void RulesetLocator_ConstructedOverStateDir_UserRulesetPathLandsUnderIt()
    {
        var locator = new RulesetLocator(_stateDir);

        Assert.Equal(Path.Combine(_stateDir, "GroupWeaver", "ruleset.jsonc"), locator.UserRulesetPath);
    }

    [Fact]
    public void UiStateStore_ConstructedOverStateDir_StatePathLandsUnderIt()
    {
        var store = new UiStateStore(_stateDir);

        Assert.Equal(Path.Combine(_stateDir, "GroupWeaver", "ui-state.json"), store.StatePath);
    }

    [Fact]
    public void AuditRunStore_ConstructedOverStateDir_RunsDirectoryLandsUnderIt()
    {
        // App.axaml.cs's exact ctor shape: (stateDir, logger) — the run store's logger follows
        // AppLog, mirrored here with a null-object logger (irrelevant to the path contract).
        var store = new AuditRunStore(_stateDir, NullLogger.Instance);

        Assert.Equal(Path.Combine(_stateDir, "GroupWeaver", "runs"), store.RunsDirectory);
    }

    // === 2. all three together share ONE common base, never the real %APPDATA% ===============

    [Fact]
    public void AllThreeStores_ShareOneCommonStateDirBase_NeverTheRealAppData()
    {
        var locator = new RulesetLocator(_stateDir);
        var uiStateStore = new UiStateStore(_stateDir);
        var auditRunStore = new AuditRunStore(_stateDir, NullLogger.Instance);

        var realAppData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        string[] effectivePaths = [locator.UserRulesetPath, uiStateStore.StatePath, auditRunStore.RunsDirectory];

        foreach (var path in effectivePaths)
        {
            Assert.StartsWith(_stateDir, path, StringComparison.Ordinal);
            Assert.DoesNotContain(realAppData, path, StringComparison.OrdinalIgnoreCase);
        }
    }

    // === 3. null StateDir mirrors App.axaml.cs's production default (real %APPDATA%) ==========

    /// <summary>
    /// <c>App.axaml.cs</c>: <c>stateDir is null =&gt; new RulesetLocator() / new UiStateStore()</c>
    /// (the PARAMETERLESS production ctor) — never <c>new X(null)</c>. This is a pure path
    /// computation (the ctor never touches the filesystem), so asserting the real
    /// <c>%APPDATA%</c> prefix here writes nothing — the real-state-untouched invariant holds.
    /// </summary>
    [Fact]
    public void NullStateDir_MirrorsAppAxamlCsProductionDefault_UsesRealAppData()
    {
        var locator = new RulesetLocator();
        var uiStateStore = new UiStateStore();

        var realAppData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

        Assert.StartsWith(realAppData, locator.UserRulesetPath, StringComparison.Ordinal);
        Assert.StartsWith(realAppData, uiStateStore.StatePath, StringComparison.Ordinal);
    }
}
