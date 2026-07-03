using System;
using System.IO;
using System.Linq;
using System.Text.Json;

using GroupWeaver.App.Diagnostics;

using Xunit;

namespace GroupWeaver.App.Tests.Diagnostics;

/// <summary>
/// Pins the crash-marker artifact (ADR-037 D7 — the #239 E2E harness's crash evidence) through
/// the internal <see cref="Program.WriteCrashMarker"/> seam (made internal exactly for this pin;
/// it resolves its directory via <see cref="FileLogSink.ResolveLogDirectory"/>, so the
/// <c>GROUPWEAVER_LOG_DIR</c> env seam applies): file name
/// <c>crash-&lt;sid&gt;-&lt;utc yyyyMMddTHHmmssZ&gt;.json</c> with the CURRENT
/// <see cref="AppLog.Session"/> sid, body schema <c>{schemaVersion:1, sid, utc, exType,
/// msgScrubbed, stack, version, logFile}</c> in that property order, atomic temp+move (no
/// <c>*.groupweaver-tmp</c> orphan), never throws.
///
/// <para><b>Null-field truth:</b> the marker writer serializes with
/// <c>JsonIgnoreCondition.WhenWritingNull</c> (the house STJ writer convention), so in the
/// null-exception arm <c>exType</c>/<c>msgScrubbed</c>/<c>stack</c>/<c>logFile</c> are ABSENT
/// properties, not JSON nulls — the harness must treat a missing <c>exType</c> as "no exception
/// detail", never as a parse failure.</para>
///
/// <para>Env-var isolation: this assembly runs sequentially
/// (<c>DisableTestParallelization</c> in <c>TestAppBuilder</c>), and every scope restores the
/// prior value — no cross-test bleed with <c>FileLogSinkNeverThrowTests</c>.</para>
/// </summary>
public sealed class CrashMarkerTests : IDisposable
{
    private readonly string _dir =
        Directory.CreateTempSubdirectory("groupweaver-crashmarker-").FullName;

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

    // === 1. Thrown exception: the full pinned schema ===========================================

    [Fact]
    public void ThrownException_WritesOneMarker_WithThePinnedNameAndSchema()
    {
        var logDir = Path.Combine(_dir, "logs");
        using var scope = new EnvVarScope("GROUPWEAVER_LOG_DIR", logDir);

        Assert.Null(Record.Exception(() => Program.WriteCrashMarker(ThrownException(), "gw-test.jsonl")));

        // Exactly one marker, named crash-<CURRENT sid>-<utcstamp>Z.json; the atomic temp+move
        // left no orphan behind.
        var marker = Assert.Single(Directory.GetFiles(logDir, "crash-*.json"));
        Assert.Matches(
            $"^crash-{AppLog.Session.Sid}-\\d{{8}}T\\d{{6}}Z\\.json$",
            Path.GetFileName(marker));
        Assert.Empty(Directory.GetFiles(logDir, "*.groupweaver-tmp"));

        using var doc = JsonDocument.Parse(File.ReadAllText(marker));
        var root = doc.RootElement;

        // The pinned schema, in property order.
        Assert.Equal(
            new[] { "schemaVersion", "sid", "utc", "exType", "msgScrubbed", "stack", "version", "logFile" },
            root.EnumerateObject().Select(p => p.Name).ToArray());

        Assert.Equal(1, root.GetProperty("schemaVersion").GetInt32());
        Assert.Equal(AppLog.Session.Sid, root.GetProperty("sid").GetString());
        Assert.Equal(TimeSpan.Zero, root.GetProperty("utc").GetDateTimeOffset().Offset);
        Assert.Equal("System.InvalidOperationException", root.GetProperty("exType").GetString());
        Assert.Equal("boom", root.GetProperty("msgScrubbed").GetString()); // identity Redactor in WP1
        Assert.False(
            string.IsNullOrWhiteSpace(root.GetProperty("stack").GetString()),
            "a thrown exception must carry its stack trace");

        var version = root.GetProperty("version").GetString();
        Assert.False(string.IsNullOrWhiteSpace(version));
        Assert.NotEqual("unknown", version); // the real informational version, not the fallback

        Assert.Equal("gw-test.jsonl", root.GetProperty("logFile").GetString());
    }

    // === 2. Null exception/logFile: null fields are OMITTED, marker still lands ================

    /// <summary>The degenerate crash (no exception object — e.g. a non-Exception
    /// <c>ExceptionObject</c>; no sink, so no log file) still persists a valid marker; the
    /// null-valued fields are OMITTED per <c>WhenWritingNull</c>.</summary>
    [Fact]
    public void NullExceptionAndLogFile_WritesTheMarker_OmittingTheNullFields()
    {
        var logDir = Path.Combine(_dir, "logs-null");
        using var scope = new EnvVarScope("GROUPWEAVER_LOG_DIR", logDir);

        Assert.Null(Record.Exception(() => Program.WriteCrashMarker(null, null)));

        var marker = Assert.Single(Directory.GetFiles(logDir, "crash-*.json"));
        Assert.Matches(
            $"^crash-{AppLog.Session.Sid}-\\d{{8}}T\\d{{6}}Z\\.json$",
            Path.GetFileName(marker));
        Assert.Empty(Directory.GetFiles(logDir, "*.groupweaver-tmp"));

        using var doc = JsonDocument.Parse(File.ReadAllText(marker));
        var root = doc.RootElement;

        Assert.Equal(
            new[] { "schemaVersion", "sid", "utc", "version" },
            root.EnumerateObject().Select(p => p.Name).ToArray());
        Assert.Equal(1, root.GetProperty("schemaVersion").GetInt32());
        Assert.Equal(AppLog.Session.Sid, root.GetProperty("sid").GetString());
        Assert.Equal(TimeSpan.Zero, root.GetProperty("utc").GetDateTimeOffset().Offset);
    }

    // === helpers ===============================================================================

    /// <summary>An exception that was genuinely THROWN, so <see cref="Exception.StackTrace"/> is
    /// populated (a merely-constructed exception has a null stack).</summary>
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

    /// <summary>Sets a process environment variable for the scope's lifetime and RESTORES the
    /// prior value on dispose (the <c>FileLogSinkNeverThrowTests</c> idiom).</summary>
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
