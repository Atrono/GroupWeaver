using Xunit;

namespace GroupWeaver.App.Tests;

/// <summary>
/// Pins the <see cref="StartupOptions"/> record shape (ADR-038 D3.1, #244; grown by D3.2, WP6
/// #245): FOUR positional members (<c>Demo</c>, <c>Flags</c>, <c>StateDir</c>, <c>E2e</c>), with
/// <c>StateDir</c> defaulting to <c>null</c> and <c>E2e</c> defaulting to <c>false</c> — the
/// headless-construction default (call sites that build a <see cref="StartupOptions"/> without
/// going through <see cref="Program"/>.<c>Main</c>, e.g. <see cref="App"/>'s own static default)
/// means "no state-dir seam, run against the real <c>%APPDATA%</c>" / "no --e2e channel wired".
/// Trivial pin, plain unit test — no process spawn, no headless UI.
/// </summary>
public sealed class StartupOptionsTests
{
    [Fact]
    public void DemoOnlyCtor_LeavesFlagsAndStateDirNull_AndE2eFalse()
    {
        var options = new StartupOptions(Demo: true);

        Assert.True(options.Demo);
        Assert.Null(options.Flags);
        Assert.Null(options.StateDir);
        Assert.False(options.E2e);
    }

    [Fact]
    public void FullCtor_CarriesAllFourMembersVerbatim()
    {
        string[] flags = ["--demo", "--verbose-logs", "--e2e"];

        var options = new StartupOptions(
            Demo: true, Flags: flags, StateDir: @"C:\tmp\gw-state", E2e: true);

        Assert.True(options.Demo);
        Assert.Same(flags, options.Flags);
        Assert.Equal(@"C:\tmp\gw-state", options.StateDir);
        Assert.True(options.E2e);
    }

    [Fact]
    public void FalseDemo_WithNoOtherArguments_IsTheAllFalseNullShape()
    {
        var options = new StartupOptions(Demo: false);

        Assert.False(options.Demo);
        Assert.Null(options.Flags);
        Assert.Null(options.StateDir);
        Assert.False(options.E2e);
    }

    /// <summary>
    /// <see cref="App.StartupOptions"/>'s static field initializer (<c>new(Demo: false)</c>) is
    /// the shape every headless test harness that constructs <see cref="App"/> without going
    /// through <c>Main</c> inherits — it must keep <c>StateDir</c> null (real <c>%APPDATA%</c>)
    /// and <c>E2e</c> false (no channel wired), exactly like <see cref="Program"/> leaves them
    /// when <c>--state-dir</c>/<c>--e2e</c> were never passed. No test in this assembly
    /// reassigns <see cref="App.StartupOptions"/> (grep-verified), so this reads the untouched
    /// static default rather than a per-test seam.
    /// </summary>
    [Fact]
    public void App_StaticDefaultStartupOptions_HasStateDirNull_AndE2eFalse()
    {
        Assert.False(App.StartupOptions.Demo);
        Assert.Null(App.StartupOptions.StateDir);
        Assert.False(App.StartupOptions.E2e);
    }
}
