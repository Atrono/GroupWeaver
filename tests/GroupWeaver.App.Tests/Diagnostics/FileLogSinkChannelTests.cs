using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

using GroupWeaver.App.Diagnostics;

using Microsoft.Extensions.Logging;

using Xunit;

namespace GroupWeaver.App.Tests.Diagnostics;

/// <summary>
/// Pins <see cref="FileLogSink"/>'s channel semantics (ADR-037 D3/D4): min-level gating
/// (<c>IsEnabled</c> false below the floor, nothing below it ever reaches the file), bounded-channel
/// backpressure (overflow DROPS with a counter — accounted for by <c>LogBackpressureDropped</c>
/// Warns, NEVER silent loss), and <see cref="FileLogSink.Dispose"/> (bounded drain+flush of
/// everything enqueued; idempotent; post-dispose calls are discarded). Internal test-seam ctor over
/// a temp directory — never the real <c>%APPDATA%</c>.
/// </summary>
public sealed class FileLogSinkChannelTests : IDisposable
{
    private static readonly Session TestSession =
        new("sid00001", new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero));

    private readonly string _dir =
        Directory.CreateTempSubdirectory("groupweaver-sink-channel-").FullName;

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    // === 1. Level gating =======================================================================

    /// <summary>A sink created at Information reports <c>IsEnabled(Trace/Debug) == false</c> (the
    /// zero-work guard the LoggerMessage fast path relies on) and the file contains NO trace/debug
    /// line — only the info one.</summary>
    [Fact]
    public void InformationSink_DiscardsTraceAndDebug_IsEnabledFalseBelowTheFloor()
    {
        var sink = CreateSink(minLevel: LogLevel.Information);
        var logger = sink.CreateLogger("Gate");

        Assert.False(logger.IsEnabled(LogLevel.Trace));
        Assert.False(logger.IsEnabled(LogLevel.Debug));
        Assert.True(logger.IsEnabled(LogLevel.Information));
        Assert.True(logger.IsEnabled(LogLevel.Warning));
        Assert.False(logger.IsEnabled(LogLevel.None)); // None is never writable

        logger.LogTrace(new EventId(0, "T"), "trace");
        logger.LogDebug(new EventId(0, "D"), "debug");
        logger.LogInformation(new EventId(0, "I"), "info");
        sink.Dispose();

        var line = Assert.Single(File.ReadAllLines(sink.CurrentLogFilePath));
        using var doc = JsonDocument.Parse(line);
        Assert.Equal("info", doc.RootElement.GetProperty("lvl").GetString());
        Assert.Equal("I", doc.RootElement.GetProperty("evt").GetString());
    }

    // === 2. Backpressure: drops are COUNTED, never silent ======================================

    /// <summary>Flooding a tiny (capacity 2) channel faster than the writer drains must never lose
    /// events SILENTLY: every flooded event either lands in the file or is accounted for by a
    /// <c>LogBackpressureDropped</c> Warn with a positive <c>dropped</c> count —
    /// landed + Σ dropped == flooded, exactly. (Whether drops occur at all is a race; the
    /// ACCOUNTING invariant is what this pins, both outcomes included.)</summary>
    [Fact]
    public void FloodingATinyChannel_AccountsForEveryEvent_LandedPlusDroppedEqualsFlooded()
    {
        const int Flooded = 500;
        var sink = CreateSink(channelCapacity: 2);
        var logger = sink.CreateLogger("Flood");
        for (var i = 0; i < Flooded; i++)
        {
            logger.LogInformation(new EventId(0, "FloodEvent"), "{seq}", i);
        }

        sink.Dispose();

        var landedSeqs = new List<int>();
        long droppedTotal = 0;
        var dropReports = 0;
        foreach (var line in File.ReadAllLines(sink.CurrentLogFilePath))
        {
            using var doc = JsonDocument.Parse(line);
            var evt = doc.RootElement.GetProperty("evt").GetString();
            if (evt == "FloodEvent")
            {
                landedSeqs.Add(doc.RootElement.GetProperty("seq").GetInt32());
            }
            else if (evt == "LogBackpressureDropped")
            {
                dropReports++;
                Assert.Equal("warn", doc.RootElement.GetProperty("lvl").GetString());
                Assert.Equal("App.Diagnostics", doc.RootElement.GetProperty("cat").GetString());
                var dropped = doc.RootElement.GetProperty("dropped").GetInt64();
                Assert.True(dropped > 0, "a backpressure report must carry a positive dropped count");
                droppedTotal += dropped;
            }
            else
            {
                Assert.Fail($"unexpected event in the flood log: {evt}");
            }
        }

        // The no-silent-loss ledger: every event landed at most once, and the drop Warns account
        // for exactly the difference.
        Assert.Equal(landedSeqs.Count, landedSeqs.Distinct().Count());
        Assert.Equal(Flooded, landedSeqs.Count + droppedTotal);
        if (landedSeqs.Count < Flooded)
        {
            Assert.True(dropReports > 0, "lost events without a LogBackpressureDropped report = silent loss");
        }
    }

    // === 3. Dispose: bounded drain+flush of everything enqueued ================================

    /// <summary>Everything enqueued before <see cref="FileLogSink.Dispose"/> reaches the disk —
    /// Dispose drains and flushes within its bounded window (the D7 crash-flush deadline), so a
    /// clean exit never loses buffered lines.</summary>
    [Fact]
    public void Dispose_FlushesEverythingEnqueued_ToDisk()
    {
        const int EventCount = 100;
        var sink = CreateSink();
        var logger = sink.CreateLogger("Drain");
        for (var i = 0; i < EventCount; i++)
        {
            logger.LogInformation(new EventId(0, "DrainEvent"), "{seq}", i);
        }

        sink.Dispose(); // immediately — no settling sleep: Dispose itself must drain

        var seqs = File.ReadAllLines(sink.CurrentLogFilePath)
            .Select(line =>
            {
                using var doc = JsonDocument.Parse(line);
                Assert.Equal("DrainEvent", doc.RootElement.GetProperty("evt").GetString());
                return doc.RootElement.GetProperty("seq").GetInt32();
            })
            .ToArray();

        Assert.Equal(Enumerable.Range(0, EventCount), seqs);
    }

    // === 4. Dispose is idempotent; logging after Dispose is a silent no-op =====================

    [Fact]
    public void SecondDispose_NoOps_AndPostDisposeLogging_IsSilentlyDiscarded()
    {
        var sink = CreateSink();
        var logger = sink.CreateLogger("Idem");
        logger.LogInformation(new EventId(0, "BeforeDispose"), "before");
        sink.Dispose();

        var lengthAfterFirst = new FileInfo(sink.CurrentLogFilePath).Length;

        // Second Dispose: no throw, file untouched.
        Assert.Null(Record.Exception(sink.Dispose));
        Assert.Equal(lengthAfterFirst, new FileInfo(sink.CurrentLogFilePath).Length);

        // Logging after Dispose: disabled, silent, nothing appended.
        Assert.False(logger.IsEnabled(LogLevel.Critical));
        Assert.Null(Record.Exception(() => logger.LogCritical(new EventId(0, "AfterDispose"), "late")));
        Assert.Equal(lengthAfterFirst, new FileInfo(sink.CurrentLogFilePath).Length);

        var line = Assert.Single(File.ReadAllLines(sink.CurrentLogFilePath));
        using var doc = JsonDocument.Parse(line);
        Assert.Equal("BeforeDispose", doc.RootElement.GetProperty("evt").GetString());
    }

    // === helpers ===============================================================================

    private FileLogSink CreateSink(
        LogLevel minLevel = LogLevel.Trace,
        int channelCapacity = FileLogSink.DefaultChannelCapacity) =>
        new(_dir, minLevel, TestSession, FileLogSink.DefaultMaxFileBytes, channelCapacity);
}
