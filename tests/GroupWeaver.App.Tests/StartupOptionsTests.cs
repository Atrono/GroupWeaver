using Xunit;

namespace GroupWeaver.App.Tests;

/// <summary>
/// Pins the <see cref="StartupOptions"/> record shape (ADR-038 D3.1, #244): three positional
/// members (<c>Demo</c>, <c>Flags</c>, <c>StateDir</c>), with <c>StateDir</c> defaulting to
/// <c>null</c> — the headless-construction default (call sites that build a
/// <see cref="StartupOptions"/> without going through <see cref="Program"/>.<c>Main</c>, e.g.
/// <see cref="App"/>'s own static default) means "no state-dir seam, run against the real
/// <c>%APPDATA%</c>". Trivial pin, plain unit test — no process spawn, no headless UI.
/// </summary>
public sealed class StartupOptionsTests
{
    [Fact]
    public void DemoOnlyCtor_LeavesFlagsAndStateDirNull()
    {
        var options = new StartupOptions(Demo: true);

        Assert.True(options.Demo);
        Assert.Null(options.Flags);
        Assert.Null(options.StateDir);
    }

    [Fact]
    public void FullCtor_CarriesAllThreeMembersVerbatim()
    {
        string[] flags = ["--demo", "--verbose-logs"];

        var options = new StartupOptions(Demo: true, Flags: flags, StateDir: @"C:\tmp\gw-state");

        Assert.True(options.Demo);
        Assert.Same(flags, options.Flags);
        Assert.Equal(@"C:\tmp\gw-state", options.StateDir);
    }

    [Fact]
    public void FalseDemo_WithNoOtherArguments_IsTheAllFalseNullShape()
    {
        var options = new StartupOptions(Demo: false);

        Assert.False(options.Demo);
        Assert.Null(options.Flags);
        Assert.Null(options.StateDir);
    }

    /// <summary>
    /// <see cref="App.StartupOptions"/>'s static field initializer (<c>new(Demo: false)</c>) is
    /// the shape every headless test harness that constructs <see cref="App"/> without going
    /// through <c>Main</c> inherits — it must keep <c>StateDir</c> null (real <c>%APPDATA%</c>),
    /// exactly like <see cref="Program"/> leaves it when <c>--state-dir</c> was never passed.
    /// No test in this assembly reassigns <see cref="App.StartupOptions"/> (grep-verified), so
    /// this reads the untouched static default rather than a per-test seam.
    /// </summary>
    [Fact]
    public void App_StaticDefaultStartupOptions_HasStateDirNull()
    {
        Assert.False(App.StartupOptions.Demo);
        Assert.Null(App.StartupOptions.StateDir);
    }
}
