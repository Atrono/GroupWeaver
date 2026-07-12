using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;

using GroupWeaver.App.Diagnostics;

using Microsoft.Extensions.Logging;

using Xunit;

namespace GroupWeaver.App.Tests.Diagnostics;

/// <summary>
/// Pins the ADR-037 D9 <c>--log-plain</c> SINK contract (WP10 #249): a sink built over
/// <c>Redaction.Identity</c> suffixes its file name <c>-PLAIN</c>
/// (<c>gw-&lt;utcstamp&gt;-&lt;pid&gt;-PLAIN.jsonl</c> — the visible "do not attach publicly"
/// marker the D10 issue-template line and the future <c>--diag</c> exclusion key off) and
/// writes a FIRST-LINE warning (presence pinned, prose free; it must still parse as one JSON
/// object so line-based JSONL consumers survive, and must mention plain-ness) — while the
/// DEFAULT sink keeps today's exact file name and writes NO warning line.
///
/// <para><b>The pinned seam:</b> the internal test-seam ctor gains a defaulted
/// <c>Redaction</c> parameter (existing call sites and tests compile unchanged); discovered
/// here by reflection because neither the type nor the parameter exists on the WP1 stub —
/// that absence is the red assertion. Program's <c>--log-plain</c> path passes
/// <c>Redaction.Identity</c> here AND installs it as <c>Redactor.Current</c> (the call-site
/// helpers); the Main wiring itself is not unit-testable and stays E2E-covered.</para>
/// </summary>
public sealed class FileLogSinkPlainModeTests : IDisposable
{
    private static readonly Session TestSession =
        new("sid00001", new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero));

    private readonly string _dir =
        Directory.CreateTempSubdirectory("groupweaver-sink-plain-").FullName;

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

    // === 1. Plain sink: -PLAIN file name + first-line warning =================================

    [Fact]
    public void PlainSink_SuffixesThePlainFileName_AndWritesAFirstLineWarning()
    {
        var sink = CreatePlainSink();
        var logger = sink.CreateLogger("App.Shell");
        logger.LogInformation(new EventId(0, "AppStarted"), "started {mode}", "demo");
        sink.Dispose();

        Assert.Equal(
            $"gw-20260101T120000Z-{Environment.ProcessId}-PLAIN.jsonl",
            Path.GetFileName(sink.CurrentLogFilePath));

        var lines = File.ReadAllLines(sink.CurrentLogFilePath);
        Assert.Equal(2, lines.Length);

        // The warning line comes FIRST, parses as one JSON object (line-based consumers
        // survive), and mentions plain-ness; its exact prose is deliberately unpinned.
        using (var first = JsonDocument.Parse(lines[0]))
        {
            Assert.Equal(JsonValueKind.Object, first.RootElement.ValueKind);
        }

        Assert.Contains("plain", lines[0], StringComparison.OrdinalIgnoreCase);

        using var second = JsonDocument.Parse(lines[1]);
        Assert.Equal("AppStarted", second.RootElement.GetProperty("evt").GetString());
    }

    // === 2. Plain sink: the identity redactor end-to-end ======================================

    [Fact]
    public void PlainSink_PassesTheExceptionMessageThroughVerbatim()
    {
        const string message = "bind failed for CN=GG_Circle_A,OU=AGDLP-Lab,DC=agdlp,DC=lab";

        var sink = CreatePlainSink();
        var logger = sink.CreateLogger("Ldap");
        logger.LogError(
            new EventId(0, "ScopeLoadFailed"),
            new StackedException(message, "   at Ldap.Bind()"),
            "failed {kind}",
            "ldap");
        sink.Dispose();

        var eventLine = File.ReadAllLines(sink.CurrentLogFilePath).Last();
        using var doc = JsonDocument.Parse(eventLine);
        Assert.Equal(
            message,
            doc.RootElement.GetProperty("ex").GetProperty("msgScrubbed").GetString());
    }

    // === 3. Default sink: NO -PLAIN, NO warning line ===========================================

    /// <summary>The default half of the item-4 contract (already true on the WP1 stub and must
    /// STAY true post-WP10): no flag ⇒ today's exact file name, and the first line is the first
    /// EVENT — no warning line sneaks into default output.</summary>
    [Fact]
    public void DefaultSink_HasNoPlainSuffix_AndNoWarningLine()
    {
        var sink = new FileLogSink(
            _dir, LogLevel.Trace, TestSession,
            FileLogSink.DefaultMaxFileBytes, FileLogSink.DefaultChannelCapacity);
        var logger = sink.CreateLogger("App.Shell");
        logger.LogInformation(new EventId(0, "Ping"), "ping");
        sink.Dispose();

        Assert.DoesNotContain(
            "-PLAIN", Path.GetFileName(sink.CurrentLogFilePath), StringComparison.OrdinalIgnoreCase);

        var line = Assert.Single(File.ReadAllLines(sink.CurrentLogFilePath));
        using var doc = JsonDocument.Parse(line);
        Assert.Equal("Ping", doc.RootElement.GetProperty("evt").GetString());
    }

    // === helpers ===============================================================================

    /// <summary>Builds a sink over <c>Redaction.Identity</c> through the pinned defaulted-param
    /// seam; arguments are mapped by parameter TYPE so the pin does not depend on parameter
    /// order or on additional defaulted parameters the implementer may add.</summary>
    private FileLogSink CreatePlainSink()
    {
        var redactionType = RedactionSurface.Require();
        var identity = RedactionSurface.Identity(redactionType);

        var ctor = typeof(FileLogSink)
            .GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .FirstOrDefault(c => c.GetParameters().Any(p => p.ParameterType == redactionType));
        Assert.True(
            ctor is not null,
            "WP10 pins a FileLogSink test-seam ctor accepting the Redaction instance (a defaulted "
            + "parameter on the existing internal seam — existing call sites compile unchanged).");

        object? Argument(ParameterInfo parameter) => parameter.ParameterType switch
        {
            var t when t == typeof(string) => _dir,
            var t when t == typeof(LogLevel) => LogLevel.Trace,
            var t when t == typeof(Session) => TestSession,
            var t when t == typeof(long) => FileLogSink.DefaultMaxFileBytes,
            var t when t == typeof(int) => FileLogSink.DefaultChannelCapacity,
            var t when t == redactionType => identity,
            _ => null,
        };

        return (FileLogSink)ctor!.Invoke(ctor.GetParameters().Select(Argument).ToArray());
    }
}
