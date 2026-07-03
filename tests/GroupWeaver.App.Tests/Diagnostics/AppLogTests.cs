using System;
using System.Linq;
using System.Threading.Tasks;

using GroupWeaver.App.Diagnostics;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using Xunit;

namespace GroupWeaver.App.Tests.Diagnostics;

/// <summary>
/// Pins the <see cref="AppLog"/> composition seam (ADR-037 D2/D4): the default factory is
/// <see cref="NullLoggerFactory.Instance"/> (headless tests and CLI paths run exactly as before),
/// <see cref="AppLog.Install"/> is set-once FIRST-WINS (a later call is ignored, returns false,
/// never throws), the <see cref="Session"/> sid is 8 lowercase-hex chars, and
/// <see cref="AppLog.NextOp"/> is a monotonic thread-safe counter.
///
/// <para><b>Process-wide discipline:</b> <see cref="AppLog"/> is a set-once static shared by the
/// whole test process (only <c>Program.Main</c>'s GUI path installs in production — never in
/// tests). The ONE test that exercises <see cref="AppLog.Install"/> installs
/// <see cref="NullLoggerFactory.Instance"/> — behaviorally identical to the default — so every
/// other test in the run (including <c>ShellViewModel</c>'s defaulted <c>loggerFactory</c> path)
/// still sees a no-op factory. No other test may call Install.</para>
/// </summary>
public sealed class AppLogTests
{
    // === 1. Default + set-once first-wins ======================================================

    /// <summary>Default-before-install and first-wins in ONE method (the assertions are ordered;
    /// splitting them across tests would race the set-once static).</summary>
    [Fact]
    public void Factory_DefaultsToNullLogger_AndInstallIsFirstWins_SecondInstallIgnored()
    {
        // BEFORE any install: the D2 default — headless tests run unlogged.
        Assert.Same(NullLoggerFactory.Instance, AppLog.Factory);
        Assert.Equal(LogLevel.None, AppLog.MinLevel);

        // First install wins (returns true) and publishes the min level. Installing the SAME
        // NullLoggerFactory instance keeps this process-wide test benign for the rest of the run.
        Assert.True(AppLog.Install(NullLoggerFactory.Instance, LogLevel.Information));
        Assert.Same(NullLoggerFactory.Instance, AppLog.Factory);
        Assert.Equal(LogLevel.Information, AppLog.MinLevel);

        // A second install is IGNORED: false, never throws, Factory + MinLevel unchanged — a
        // misbehaving double-install can never tear running loggers away.
        using var second = new CapturingLoggerFactory();
        Assert.False(AppLog.Install(second, LogLevel.Trace));
        Assert.Same(NullLoggerFactory.Instance, AppLog.Factory);
        Assert.Equal(LogLevel.Information, AppLog.MinLevel);
    }

    // === 2. CreateLogger without a real install is a no-op logger ==============================

    /// <summary>Without a real sink installed (the permanent state of the test process),
    /// <see cref="AppLog.CreateLogger"/> yields a disabled no-op logger — logging through it is
    /// free and safe. This is why headless suites run untouched by ADR-037.</summary>
    [Fact]
    public void CreateLogger_WithoutARealInstall_IsADisabledNoOpLogger()
    {
        var logger = AppLog.CreateLogger("Test.Category");
        Assert.False(logger.IsEnabled(LogLevel.Critical));
        Assert.Null(Record.Exception(() => logger.LogInformation(new EventId(0, "Ping"), "ping")));
    }

    // === 3. Session identity ===================================================================

    /// <summary>The process session sid is 8 lowercase-hex chars (the join key between every log
    /// line, the banner, and the crash-marker file name) with a UTC start time; fresh sessions
    /// mint the same shape.</summary>
    [Fact]
    public void Session_SidIsEightLowercaseHexChars_AndStartedUtc()
    {
        Assert.Matches("^[0-9a-f]{8}$", AppLog.Session.Sid);
        Assert.Equal(TimeSpan.Zero, AppLog.Session.StartedUtc.Offset);

        var fresh = Session.Create();
        Assert.Matches("^[0-9a-f]{8}$", fresh.Sid);
        Assert.Equal(TimeSpan.Zero, fresh.StartedUtc.Offset);
    }

    // === 4. NextOp: monotonic and concurrency-safe =============================================

    /// <summary>The op counter (joins Started/Completed pairs per pipeline) is strictly increasing
    /// sequentially and hands out DISTINCT values under parallel load.</summary>
    [Fact]
    public void NextOp_IsStrictlyIncreasing_AndConcurrentlyDistinct()
    {
        var first = AppLog.NextOp();
        var second = AppLog.NextOp();
        Assert.True(second > first, "NextOp must be strictly increasing");

        var ops = new long[512];
        Parallel.For(0, ops.Length, i => ops[i] = AppLog.NextOp());
        Assert.Equal(ops.Length, ops.Distinct().Count());
        Assert.All(ops, op => Assert.True(op > second));
    }
}
