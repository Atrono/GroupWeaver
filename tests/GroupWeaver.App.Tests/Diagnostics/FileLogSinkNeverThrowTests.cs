using System;
using System.IO;
using System.Text.Json;

using GroupWeaver.App.Diagnostics;

using Microsoft.Extensions.Logging;

using Xunit;

namespace GroupWeaver.App.Tests.Diagnostics;

/// <summary>
/// Pins the NEVER-THROW contract of <see cref="FileLogSink"/> (ADR-037 D3): creation failure yields
/// <c>null</c> (<see cref="FileLogSink.TryCreate"/>), a mid-run write failure turns the sink into a
/// silent discard — a logging failure must NEVER surface as an app failure. Also pins the
/// <see cref="FileLogSink.ResolveLogDirectory"/> seam (<c>GROUPWEAVER_LOG_DIR</c> else
/// <c>%APPDATA%\GroupWeaver\logs</c> — the #239 harness contract).
///
/// <para><b>Isolation:</b> every test that touches the <c>GROUPWEAVER_LOG_DIR</c> process
/// environment variable lives in THIS class only (xUnit runs one class's tests sequentially), and
/// each restores the prior value via <see cref="EnvVarScope"/> — no other test in the suite reads
/// that variable.</para>
///
/// <para><b>Mid-run failure choice:</b> the sink opens its file with <c>FileShare.Read</c> and
/// holds it for its lifetime, so an external exclusive LOCK cannot be taken and the directory
/// cannot be DELETED while the handle is open (Windows). The reachable mid-run failure of the same
/// class is a failing ROLL: a directory squatting on the <c>-part2</c> path makes the roll's
/// file-open throw, which must disable the writer silently.</para>
/// </summary>
public sealed class FileLogSinkNeverThrowTests : IDisposable
{
    private readonly string _dir =
        Directory.CreateTempSubdirectory("groupweaver-sink-neverthrow-").FullName;

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

    // === 1. TryCreate against an uncreatable directory: null, never a throw ====================

    /// <summary>Pointing the log directory at an existing FILE makes directory creation fail —
    /// <see cref="FileLogSink.TryCreate"/> returns <c>null</c> and never throws (the app runs
    /// unlogged rather than not at all).</summary>
    [Fact]
    public void TryCreate_LogDirectoryPathIsAFile_ReturnsNull_NeverThrows()
    {
        var filePath = Path.Combine(_dir, "occupied-by-a-file");
        File.WriteAllText(filePath, "not a directory");
        using var scope = new EnvVarScope("GROUPWEAVER_LOG_DIR", filePath);

        FileLogSink? sink = null;
        var ex = Record.Exception(() => sink = FileLogSink.TryCreate(LogLevel.Information, Session.Create()));

        Assert.Null(ex);
        Assert.Null(sink);
    }

    // === 2. TryCreate honors the GROUPWEAVER_LOG_DIR override ==================================

    /// <summary>The env-var override (the E2E harness seam) wins over <c>%APPDATA%</c>:
    /// <see cref="FileLogSink.ResolveLogDirectory"/> returns it verbatim and
    /// <see cref="FileLogSink.TryCreate"/> creates + writes there.</summary>
    [Fact]
    public void TryCreate_HonorsTheLogDirOverride_CreatesAndWritesThere()
    {
        var overrideDir = Path.Combine(_dir, "override-logs");
        using var scope = new EnvVarScope("GROUPWEAVER_LOG_DIR", overrideDir);

        Assert.Equal(overrideDir, FileLogSink.ResolveLogDirectory());

        var sink = FileLogSink.TryCreate(LogLevel.Information, Session.Create());
        Assert.NotNull(sink);
        sink!.CreateLogger("T").LogInformation(new EventId(0, "Ping"), "ping");
        sink.Dispose();

        Assert.Equal(overrideDir, Path.GetDirectoryName(sink.CurrentLogFilePath));
        var line = Assert.Single(File.ReadAllLines(sink.CurrentLogFilePath));
        using var doc = JsonDocument.Parse(line);
        Assert.Equal("Ping", doc.RootElement.GetProperty("evt").GetString());
    }

    // === 3. ResolveLogDirectory default ========================================================

    /// <summary>Unset or whitespace-only override falls back to
    /// <c>%APPDATA%\GroupWeaver\logs</c> — the repo-wide user-persistence convention (ADR-008).
    /// Pure path computation; nothing is created.</summary>
    [Fact]
    public void ResolveLogDirectory_UnsetOrBlank_FallsBackToAppDataGroupWeaverLogs()
    {
        var expected = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "GroupWeaver",
            "logs");

        using (new EnvVarScope("GROUPWEAVER_LOG_DIR", null))
        {
            Assert.Equal(expected, FileLogSink.ResolveLogDirectory());
        }

        using (new EnvVarScope("GROUPWEAVER_LOG_DIR", "   "))
        {
            Assert.Equal(expected, FileLogSink.ResolveLogDirectory());
        }
    }

    // === 4. Mid-run write failure: silent discard, writer disabled, no torn lines ==============

    /// <summary>A mid-run write failure (here: the roll's file-open fails because a DIRECTORY
    /// squats on the <c>-part2</c> path) disables the writer SILENTLY: no exception ever reaches
    /// the logging caller or <see cref="FileLogSink.Dispose"/>, later calls degrade to discards,
    /// and the already-written file holds only whole, parseable lines.</summary>
    [Fact]
    public void MidRunRollFailure_DisablesTheWriterSilently_NoExceptionReachesTheCaller()
    {
        var session = new Session("sid00002", new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero));
        var sink = new FileLogSink(_dir, LogLevel.Information, session, maxFileBytes: 512, channelCapacity: 4096);
        var firstPath = sink.CurrentLogFilePath;
        var part2Path = firstPath[..^".jsonl".Length] + "-part2.jsonl";
        Directory.CreateDirectory(part2Path); // the roll target is now un-openable

        var logger = sink.CreateLogger("Degrade");
        var pad = new string('x', 100);
        var floodEx = Record.Exception(() =>
        {
            for (var i = 0; i < 40; i++)
            {
                logger.LogInformation(new EventId(0, "DegradeFill"), "{seq} {pad}", i, pad);
            }
        });
        Assert.Null(floodEx);

        // Logging after the writer broke must stay silent too — even at Warning (the flush-now path).
        var afterEx = Record.Exception(() => logger.LogWarning(new EventId(0, "AfterBreak"), "after"));
        Assert.Null(afterEx);

        Assert.Null(Record.Exception(sink.Dispose));

        // The squatting directory is untouched, and the first file holds ONLY whole lines.
        Assert.True(Directory.Exists(part2Path));
        var lines = File.ReadAllLines(firstPath);
        Assert.NotEmpty(lines);
        foreach (var line in lines)
        {
            using var doc = JsonDocument.Parse(line);
            Assert.Equal("DegradeFill", doc.RootElement.GetProperty("evt").GetString());
        }
    }

    // === helpers ===============================================================================

    /// <summary>Sets a process environment variable for the scope's lifetime and RESTORES the
    /// prior value on dispose — keeps the env-var seam hermetic within this class.</summary>
    private sealed class EnvVarScope : IDisposable
    {
        private readonly string _name;
        private readonly string? _previous;

        public EnvVarScope(string name, string? value)
        {
            _name = name;
            _previous = Environment.GetEnvironmentVariable(name);
            Environment.SetEnvironmentVariable(name, value);
        }

        public void Dispose() => Environment.SetEnvironmentVariable(_name, _previous);
    }
}
