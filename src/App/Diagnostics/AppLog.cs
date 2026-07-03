using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace GroupWeaver.App.Diagnostics;

/// <summary>
/// Per-process logging session identity (ADR-037 D4): the 8-char crypto-random
/// <see cref="Sid"/> joins every log line to the <c>AppStarted</c> banner and the crash
/// marker — stable within a session, unlinkable across sessions.
/// </summary>
public sealed record Session(string Sid, DateTimeOffset StartedUtc)
{
    /// <summary>A fresh session: 8 lowercase-hex chars from a crypto RNG + the UTC start time.</summary>
    public static Session Create() =>
        new(Convert.ToHexString(RandomNumberGenerator.GetBytes(4)).ToLowerInvariant(), DateTimeOffset.UtcNow);
}

/// <summary>
/// The set-once composition seam for logging (ADR-037 D2): <see cref="Program"/>'s GUI path
/// builds the <see cref="FileLogSink"/> and installs it here; everything constructed off the
/// composition root (views, defaulted VM ctor params) reads <see cref="Factory"/>. The default
/// is <see cref="NullLoggerFactory.Instance"/>, so every headless test and the CLI paths run
/// exactly as before — zero logging, zero I/O, zero output.
/// </summary>
public static class AppLog
{
    private static ILoggerFactory _factory = NullLoggerFactory.Instance;
    private static int _installed;
    private static long _opCounter;

    /// <summary>The process-wide logging session (sid on every line, in the crash marker's name).</summary>
    public static Session Session { get; } = Session.Create();

    /// <summary>The installed factory, or <see cref="NullLoggerFactory.Instance"/> before/without
    /// <see cref="Install"/> (headless tests, CLI paths).</summary>
    public static ILoggerFactory Factory => _factory;

    /// <summary>The minimum level the installed sink writes (the <c>AppStarted</c> banner's
    /// <c>logLevel</c> field, ADR-037 D6). <see cref="LogLevel.None"/> until installed.</summary>
    public static LogLevel MinLevel { get; private set; } = LogLevel.None;

    /// <summary>Installs the factory ONCE per process; later calls are ignored (first wins,
    /// never throws) so a misbehaving double-install can never tear running loggers away.</summary>
    public static bool Install(ILoggerFactory factory, LogLevel minLevel)
    {
        if (Interlocked.CompareExchange(ref _installed, 1, 0) != 0)
        {
            return false;
        }

        _factory = factory;
        MinLevel = minLevel;
        return true;
    }

    /// <summary>Creates a logger from the installed factory (a no-op logger when none is).</summary>
    public static ILogger CreateLogger(string categoryName) => _factory.CreateLogger(categoryName);

    /// <summary>The monotonic per-process operation counter (ADR-037 D4): one <c>op</c> value
    /// joins a pipeline's Started/Completed/Failed events. Thread-safe.</summary>
    public static long NextOp() => Interlocked.Increment(ref _opCounter);
}
