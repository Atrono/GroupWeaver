using System;
using System.IO;
using System.Linq;
using System.Text.Json;

using GroupWeaver.App.Diagnostics;

using Microsoft.Extensions.Logging;

using Xunit;

namespace GroupWeaver.App.Tests.Diagnostics;

/// <summary>
/// Pins the JSONL WIRE contract of <see cref="FileLogSink"/> (ADR-037 D3/D4 — the machine contract
/// the #239 E2E triager greps): one strict-STJ object per line; the fixed leading fields
/// <c>ts, lvl, cat, evt, sid</c> in exactly that ORDER, then the event's structured payload (in
/// template order), then optional <c>msg</c>, then optional <c>ex {type, msgScrubbed, stack}</c>;
/// the pinned <c>lvl</c> strings <c>trace|debug|info|warn|error|critical</c>; <c>evt</c> from
/// <see cref="EventId.Name"/> with the nameless fallback <c>evt:"Message"</c> + <c>msg</c>; and the
/// <c>gw-&lt;utcstamp&gt;-&lt;pid&gt;.jsonl</c> file name. Everything runs against the INTERNAL
/// test-seam ctor over a temp directory — never the real <c>%APPDATA%</c>.
/// </summary>
public sealed class FileLogSinkWireTests : IDisposable
{
    /// <summary>Deterministic session so the <c>sid</c> value and the file-name timestamp are pinned.</summary>
    private static readonly Session TestSession =
        new("sid00001", new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero));

    private readonly string _dir =
        Directory.CreateTempSubdirectory("groupweaver-sink-wire-").FullName;

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

    // === 1. Leading-field ORDER + named-event shape ===========================================

    /// <summary>A NAMED event writes the five leading fields in the pinned order, then the payload
    /// pairs in template order — and carries NO <c>msg</c> (the structured fields ARE the data; the
    /// template is transport only, ADR-037 D4).</summary>
    [Fact]
    public void NamedEvent_WritesLeadingFieldsInPinnedOrder_ThenPayload_AndNoMsg()
    {
        var sink = CreateSink();
        var logger = sink.CreateLogger("App.Shell");
        logger.LogInformation(
            new EventId(0, "ScopeLoadCompleted"), "loaded {objects} in {durationMs} ms", 42, 731);
        sink.Dispose();

        var line = Assert.Single(File.ReadAllLines(sink.CurrentLogFilePath));
        using var doc = JsonDocument.Parse(line);
        var root = doc.RootElement;

        // Property ORDER by enumeration — the wire pin, not just presence.
        Assert.Equal(
            new[] { "ts", "lvl", "cat", "evt", "sid", "objects", "durationMs" },
            root.EnumerateObject().Select(p => p.Name).ToArray());

        Assert.Equal("info", root.GetProperty("lvl").GetString());
        Assert.Equal("App.Shell", root.GetProperty("cat").GetString());
        Assert.Equal("ScopeLoadCompleted", root.GetProperty("evt").GetString());
        Assert.Equal("sid00001", root.GetProperty("sid").GetString());
        Assert.Equal(42, root.GetProperty("objects").GetInt64());
        Assert.Equal(731, root.GetProperty("durationMs").GetInt64());

        // ts is ISO 8601 UTC with the trailing Z.
        Assert.EndsWith("Z", root.GetProperty("ts").GetString(), StringComparison.Ordinal);
        Assert.Equal(TimeSpan.Zero, root.GetProperty("ts").GetDateTimeOffset().Offset);
    }

    // === 2. Nameless fallback: evt:"Message" + msg ============================================

    /// <summary>A NAMELESS event (no <see cref="EventId.Name"/>) becomes <c>evt:"Message"</c> and
    /// carries the formatted text as <c>msg</c> AFTER the payload — the escape hatch for ad-hoc
    /// logging that must still parse under the same wire shape.</summary>
    [Fact]
    public void NamelessEvent_BecomesEvtMessage_AndCarriesTheFormattedMsg()
    {
        var sink = CreateSink();
        var logger = sink.CreateLogger("Adhoc");
        logger.LogInformation("plain message {n}", 5);
        sink.Dispose();

        var line = Assert.Single(File.ReadAllLines(sink.CurrentLogFilePath));
        using var doc = JsonDocument.Parse(line);
        var root = doc.RootElement;

        Assert.Equal(
            new[] { "ts", "lvl", "cat", "evt", "sid", "n", "msg" },
            root.EnumerateObject().Select(p => p.Name).ToArray());
        Assert.Equal("Message", root.GetProperty("evt").GetString());
        Assert.Equal("plain message 5", root.GetProperty("msg").GetString());
        Assert.Equal(5, root.GetProperty("n").GetInt64());
    }

    // === 3. Exception block: ex {type, msgScrubbed, stack}, last property ======================

    /// <summary>A logged exception appends <c>ex {type, msgScrubbed, stack}</c> as the LAST
    /// property, in that inner order. In WP1 the <see cref="Redactor"/> is the identity stub
    /// (<c>Mode == "identity"</c>), so <c>msgScrubbed</c> equals the raw message — WP10 (#249)
    /// changes the VALUE, never this SHAPE.</summary>
    [Fact]
    public void LoggedException_AppendsTheExBlock_TypeMsgScrubbedStack_AsTheLastProperty()
    {
        Assert.Equal("identity", Redactor.Mode); // the WP1 stub premise of the msgScrubbed assert

        var sink = CreateSink();
        var logger = sink.CreateLogger("App.Shell");
        logger.LogError(new EventId(0, "ScopeLoadFailed"), ThrownException(), "failed {kind}", "io");
        sink.Dispose();

        var line = Assert.Single(File.ReadAllLines(sink.CurrentLogFilePath));
        using var doc = JsonDocument.Parse(line);
        var root = doc.RootElement;

        Assert.Equal(
            new[] { "ts", "lvl", "cat", "evt", "sid", "kind", "ex" },
            root.EnumerateObject().Select(p => p.Name).ToArray());
        Assert.Equal("error", root.GetProperty("lvl").GetString());

        var ex = root.GetProperty("ex");
        Assert.Equal(
            new[] { "type", "msgScrubbed", "stack" },
            ex.EnumerateObject().Select(p => p.Name).ToArray());
        Assert.Equal("System.InvalidOperationException", ex.GetProperty("type").GetString());
        Assert.Equal("boom", ex.GetProperty("msgScrubbed").GetString());
        Assert.False(
            string.IsNullOrWhiteSpace(ex.GetProperty("stack").GetString()),
            "a thrown exception must carry its stack trace");
    }

    // === 4. Pinned lvl wire strings ============================================================

    /// <summary>The <c>lvl</c> strings are the pinned lowercase wire names
    /// <c>trace|debug|info|warn|error|critical</c> — the E2E triager's grep contract.</summary>
    [Fact]
    public void LevelNames_AreThePinnedLowercaseWireStrings()
    {
        var sink = CreateSink(minLevel: LogLevel.Trace);
        var logger = sink.CreateLogger("Levels");
        logger.LogTrace(new EventId(0, "E1"), "m");
        logger.LogDebug(new EventId(0, "E2"), "m");
        logger.LogInformation(new EventId(0, "E3"), "m");
        logger.LogWarning(new EventId(0, "E4"), "m");
        logger.LogError(new EventId(0, "E5"), "m");
        logger.LogCritical(new EventId(0, "E6"), "m");
        sink.Dispose();

        var levels = File.ReadAllLines(sink.CurrentLogFilePath)
            .Select(line =>
            {
                using var doc = JsonDocument.Parse(line);
                return doc.RootElement.GetProperty("lvl").GetString();
            })
            .ToArray();

        Assert.Equal(new[] { "trace", "debug", "info", "warn", "error", "critical" }, levels);
    }

    // === 5. Reserved-key collision: one strict object per line, never a duplicate key ==========

    /// <summary>A payload key colliding with a leading field (here <c>{sid}</c>) is SKIPPED — the
    /// line stays a STRICT single JSON object (no duplicate keys, which would break strict parsers)
    /// and the session's own <c>sid</c> wins.</summary>
    [Fact]
    public void PayloadKeyCollidingWithALeadingField_IsSkipped_NoDuplicateKeys()
    {
        var sink = CreateSink();
        var logger = sink.CreateLogger("Collide");
        logger.LogInformation(new EventId(0, "Collision"), "x {sid} {value}", "impostor", 7);
        sink.Dispose();

        var line = Assert.Single(File.ReadAllLines(sink.CurrentLogFilePath));
        using var doc = JsonDocument.Parse(line);
        var root = doc.RootElement;

        Assert.Equal(1, root.EnumerateObject().Count(p => p.Name == "sid"));
        Assert.Equal("sid00001", root.GetProperty("sid").GetString()); // the session's, not "impostor"
        Assert.Equal(7, root.GetProperty("value").GetInt64());
    }

    // === 6. File name: gw-<utcstamp>-<pid>.jsonl ===============================================

    /// <summary>The log file name is <c>gw-&lt;utc yyyyMMddTHHmmssZ&gt;-&lt;pid&gt;.jsonl</c> under
    /// the sink's directory — the shape the E2E harness and the retention glob depend on.</summary>
    [Fact]
    public void LogFileName_IsGwUtcstampPidJsonl_UnderTheSinkDirectory()
    {
        var sink = CreateSink();
        sink.Dispose();

        Assert.Equal(
            $"gw-20260101T120000Z-{Environment.ProcessId}.jsonl",
            Path.GetFileName(sink.CurrentLogFilePath));
        Assert.Equal(_dir, Path.GetDirectoryName(sink.CurrentLogFilePath));
        Assert.True(File.Exists(sink.CurrentLogFilePath));
    }

    // === helpers ===============================================================================

    private FileLogSink CreateSink(LogLevel minLevel = LogLevel.Trace) =>
        new(_dir, minLevel, TestSession,
            FileLogSink.DefaultMaxFileBytes, FileLogSink.DefaultChannelCapacity);

    /// <summary>An exception that was genuinely THROWN, so its <see cref="Exception.StackTrace"/>
    /// is populated (a merely-constructed exception has a null stack).</summary>
    private static Exception ThrownException()
    {
        try
        {
            throw new InvalidOperationException("boom");
        }
        catch (InvalidOperationException ex)
        {
            return ex;
        }
    }
}
