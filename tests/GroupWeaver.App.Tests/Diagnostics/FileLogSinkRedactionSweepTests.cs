using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text.Json;

using GroupWeaver.App.Diagnostics;

using Microsoft.Extensions.Logging;

using Xunit;

namespace GroupWeaver.App.Tests.Diagnostics;

/// <summary>
/// The ADR-037 D9 SWEEP — the guarantee that gates the tagged release: at DEFAULT settings
/// (no <c>--log-plain</c>, the ambient salted <see cref="Redactor"/>) the serialized JSONL
/// contains NO raw DN/name/server data, across every lane a sensitive value can travel —
/// a structured field pre-redacted at the call site (the production idiom), the sink-side
/// <c>ex.msgScrubbed</c>, the sink-side <c>ex.stack</c>, and a connect-time server string.
///
/// <para><b>Sweep mechanics:</b> the emitted lines are PARSED and every DECODED JSON string
/// (values and property names) is checked — a byte-level scan would pass vacuously because
/// the strict encoder <c>\uXXXX</c>-escapes non-ASCII (the umlaut in the hostile DN is
/// invisible in the raw bytes). Raw fragments must be ABSENT, the joinable
/// <c>dn#</c>/<c>host#</c> tokens PRESENT. Runs against the internal test-seam ctor over a
/// temp directory — never the real <c>%APPDATA%</c>.</para>
/// </summary>
public sealed class FileLogSinkRedactionSweepTests : IDisposable
{
    /// <summary>The hostile DN the task pins: umlauts (UTF-8 lane) + an escaped comma
    /// (<c>\,</c> — the run must not split).</summary>
    private const string HostileDn = "CN=GG_Übung\\, Kernteam,OU=AGDLP-Lab,DC=agdlp,DC=lab";

    /// <summary>Distinctive substrings of <see cref="HostileDn"/> — none may survive anywhere
    /// in the decoded output (matched case-insensitively; no sink-generated string — sid, ts,
    /// cat, evt, level names — can collide with them).</summary>
    private static readonly string[] RawDnFragments = ["GG_Übung", "Kernteam", "AGDLP-Lab", "agdlp"];

    private static readonly Session TestSession =
        new("sid00001", new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero));

    private readonly string _dir =
        Directory.CreateTempSubdirectory("groupweaver-sink-sweep-").FullName;

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

    // === 1. The DN lanes: call-site field, ex.msgScrubbed, ex.stack ===========================

    [Fact]
    public void DefaultSink_NeverEmitsARawDn_FieldExMessageAndStackLanes()
    {
        var sink = CreateSink();
        var logger = sink.CreateLogger("Ldap");

        // Lane 1 — a structured field through the call-site typed helper (the production
        // idiom: fields are pre-redacted at the call site, never formatter-guessed).
        logger.LogInformation(new EventId(0, "ScopeLoadStarted"), "loading {rootDn}", Redactor.Dn(HostileDn));

        // Lanes 2 + 3 — an exception whose Message embeds the DN and whose stack frame carries
        // it: the SINK must route BOTH through Scrub (ex.msgScrubbed AND ex.stack).
        logger.LogError(
            new EventId(0, "ScopeLoadFailed"),
            new StackedException(
                $"bind failed for {HostileDn}",
                $"   at GroupWeaver.Providers.LdapProvider.Load({HostileDn})"),
            "failed {kind}",
            "ldap");

        sink.Dispose();

        var strings = AllDecodedStrings(sink.CurrentLogFilePath);
        foreach (var fragment in RawDnFragments)
        {
            Assert.DoesNotContain(strings, s => s.Contains(fragment, StringComparison.OrdinalIgnoreCase));
        }

        // The joinable token IS present: the field lane and the message lane hash the same DN.
        var token = RedactionTokens.Dn(Redactor.SessionSalt, HostileDn);
        Assert.Contains(strings, s => s.Contains(token, StringComparison.Ordinal));
    }

    // === 2. The server lane: Learn at connect, then field + exception message =================

    [Fact]
    public void DefaultSink_LearnedServerStrings_NeverAppearRaw()
    {
        var learn = typeof(Redactor).GetMethod(
            "Learn", BindingFlags.Public | BindingFlags.Static, new[] { typeof(string) });
        Assert.True(
            learn is not null,
            "ADR-037 D9 pins Redactor.Learn(string?) — the connect-time server/baseDn registration.");
        learn!.Invoke(null, new object?[] { "dc01.sweep-lab.example" });

        var sink = CreateSink();
        var logger = sink.CreateLogger("Ldap");
        logger.LogInformation(
            new EventId(0, "LdapConnected"), "server {server}", Redactor.Host("dc01.sweep-lab.example"));
        logger.LogError(
            new EventId(0, "LdapOpFailed"),
            new StackedException("server dc01.sweep-lab.example unreachable", "   at Ldap.Connect()"),
            "failed {kind}",
            "bind");
        sink.Dispose();

        var strings = AllDecodedStrings(sink.CurrentLogFilePath);
        Assert.DoesNotContain(strings, s => s.Contains("dc01", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(strings, s => s.Contains("sweep-lab", StringComparison.OrdinalIgnoreCase));

        var token = RedactionTokens.Host(Redactor.SessionSalt, "dc01.sweep-lab.example");
        Assert.Contains(strings, s => s.Contains(token, StringComparison.Ordinal));
    }

    // === helpers ===============================================================================

    private FileLogSink CreateSink() =>
        new(_dir, LogLevel.Trace, TestSession,
            FileLogSink.DefaultMaxFileBytes, FileLogSink.DefaultChannelCapacity);

    /// <summary>Every DECODED JSON string in the file — values and property names, recursively.</summary>
    private static List<string> AllDecodedStrings(string path)
    {
        var strings = new List<string>();
        foreach (var line in File.ReadAllLines(path))
        {
            using var doc = JsonDocument.Parse(line);
            Collect(doc.RootElement, strings);
        }

        return strings;
    }

    private static void Collect(JsonElement element, List<string> strings)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.String:
                strings.Add(element.GetString()!);
                break;
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    strings.Add(property.Name);
                    Collect(property.Value, strings);
                }

                break;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    Collect(item, strings);
                }

                break;
            default:
                break;
        }
    }
}
