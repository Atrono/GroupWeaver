using System;
using System.IO;
using System.Text;
using System.Text.Json;

using GroupWeaver.App.Audit;
using GroupWeaver.App.Diagnostics;

using Microsoft.Extensions.Logging;

using Xunit;

namespace GroupWeaver.App.Tests.Diagnostics;

/// <summary>
/// Pins the <c>AuditRunSkipped{reason,file}</c> event (ADR-037 D5 — the machine-readable
/// replacement for <see cref="AuditRunStore"/>'s old <c>Debug.WriteLine</c> trio): the never-throw
/// skip stays a skip (null / empty result), but each skip now logs ONE Warn whose <c>reason</c>
/// carries the pinned vocabulary — <c>schema</c> (well-formed run, unsupported/older
/// <c>schemaVersion</c>, incl. a JSON <c>null</c> body), <c>corrupt</c> (undeserializable JSON),
/// <c>io</c> (unreadable file, and the runs DIRECTORY itself when the listing fails). The
/// <c>file</c> field routes through the TYPED redaction helpers (ADR-037 D9, WP10 #249):
/// <c>Redactor.RunFile</c> for a run-file name — its slug is a slugified DN, so the field is
/// <c>&lt;utcstamp&gt;-run#&lt;hash8&gt;.json</c> (timestamp joinable, slug hashed) — and
/// <c>Redactor.Path</c> for the runs directory (a full user path ⇒ <c>path#&lt;hash8&gt;</c>).
/// WHICH helper each call site uses is now OBSERVABLE through the distinct token shapes, so
/// these assertions pin the helper choice too (expected tokens via <see cref="RedactionTokens"/>
/// over <see cref="Redactor.SessionSalt"/> — never by calling the redactor itself).
///
/// <para>Hermetic per the #124 lesson: the store is built over a
/// <see cref="Directory.CreateTempSubdirectory(string)"/> base with an injected
/// <see cref="CapturingLoggerFactory"/> logger — never the real <c>%APPDATA%</c>.</para>
/// </summary>
public sealed class AuditRunSkippedLogTests : IDisposable
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private readonly string _baseDir =
        Directory.CreateTempSubdirectory("groupweaver-runskip-log-").FullName;

    private readonly CapturingLoggerFactory _capture = new();
    private readonly AuditRunStore _store;

    public AuditRunSkippedLogTests()
    {
        _store = new AuditRunStore(_baseDir, _capture.CreateLogger("Store.AuditRuns"));
        Directory.CreateDirectory(_store.RunsDirectory);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_baseDir, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    // === 1. corrupt: undeserializable JSON =====================================================

    [Fact]
    public void CorruptJson_SkipsWithReasonCorrupt_CarryingTheJsonException()
    {
        var path = WriteRunFile("20260101T000000Z-corrupt.json", "{ this is not valid json");

        Assert.Null(_store.Load(path));

        var entry = Assert.Single(_capture.EntriesNamed("AuditRunSkipped"));
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.Equal("corrupt", entry.Fields["reason"]);
        // via Redactor.RunFile: timestamp joinable, slug hashed (D9).
        Assert.Equal(ExpectedRunFileToken("corrupt", "20260101T000000Z"), entry.Fields["file"]);
        Assert.IsAssignableFrom<JsonException>(entry.Exception);
    }

    // === 2. schema: well-formed run, unsupported version (and a JSON null body) ================

    /// <summary>Older (0), future (999) and a literal JSON <c>null</c> body all take the SCHEMA
    /// arm: no exception attached (nothing threw — the content is well-formed, just unsupported).</summary>
    [Fact]
    public void UnsupportedSchemaVersion_OrNullBody_SkipsWithReasonSchema_NoException()
    {
        var older = WriteRunFile("20260101T000001Z-older.json", RunJsonWithSchemaVersion(0));
        var future = WriteRunFile("20260101T000002Z-future.json", RunJsonWithSchemaVersion(999));
        var nullBody = WriteRunFile("20260101T000003Z-null.json", "null");

        Assert.Null(_store.Load(older));
        Assert.Null(_store.Load(future));
        Assert.Null(_store.Load(nullBody));

        var entries = _capture.EntriesNamed("AuditRunSkipped");
        Assert.Equal(3, entries.Count);
        Assert.All(entries, e =>
        {
            Assert.Equal(LogLevel.Warning, e.Level);
            Assert.Equal("schema", e.Fields["reason"]);
            Assert.Null(e.Exception);
        });
        // file = the run-file NAME via Redactor.RunFile: timestamp joinable, slug hashed (D9).
        Assert.Equal(ExpectedRunFileToken("older", "20260101T000001Z"), entries[0].Fields["file"]);
        Assert.Equal(ExpectedRunFileToken("future", "20260101T000002Z"), entries[1].Fields["file"]);
        Assert.Equal(ExpectedRunFileToken("null", "20260101T000003Z"), entries[2].Fields["file"]);
    }

    // === 3. io: an unreadable run FILE (locked exclusively) ====================================

    [Fact]
    public void ExclusivelyLockedRunFile_SkipsWithReasonIo_CarryingTheIOException()
    {
        var path = WriteRunFile("20260101T000004Z-locked.json", "irrelevant — the open fails first");
        using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Assert.Null(_store.Load(path)); // sharing violation inside the never-throw read
        }

        var entry = Assert.Single(_capture.EntriesNamed("AuditRunSkipped"));
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.Equal("io", entry.Fields["reason"]);
        // via Redactor.RunFile: timestamp joinable, slug hashed (D9).
        Assert.Equal(ExpectedRunFileToken("locked", "20260101T000004Z"), entry.Fields["file"]);
        Assert.IsAssignableFrom<IOException>(entry.Exception);
    }

    // === 4. io: the runs DIRECTORY itself is unlistable ========================================

    /// <summary>When the runs directory exists but cannot be ENUMERATED (an IOException or
    /// UnauthorizedAccessException out of the listing call — the "unreadable directory" arm),
    /// <c>List()</c> degrades to empty with ONE io-skip naming the DIRECTORY, not a file.
    ///
    /// <para>Drives the failure through the internal <see cref="AuditRunStore"/> listing seam
    /// (issue #254) rather than a deny ACL: this lab box's session runs as the elevated built-in
    /// domain Administrator, which silently bypasses a directory-listing Deny ACE, so the
    /// ACL-simulation version of this test flaked (empty capture — no skip ever fired). The seam
    /// proves the exact same catch clause (<c>ex is IOException or UnauthorizedAccessException</c>)
    /// deterministically, for BOTH exception types the clause accepts.</para></summary>
    [Theory]
    [InlineData(typeof(IOException))]
    [InlineData(typeof(UnauthorizedAccessException))]
    public void UnlistableRunsDirectory_ListsEmpty_WithOneIoSkipNamingTheDirectory(Type exceptionType)
    {
        var thrown = (Exception)Activator.CreateInstance(exceptionType, "simulated unlistable runs directory (#254)")!;
        var store = new AuditRunStore(
            _baseDir,
            _capture.CreateLogger("Store.AuditRuns"),
            listRunFiles: _ => throw thrown);
        Directory.CreateDirectory(store.RunsDirectory);

        Assert.Empty(store.List());

        var entry = Assert.Single(_capture.EntriesNamed("AuditRunSkipped"));
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.Equal("io", entry.Fields["reason"]);
        // via Redactor.Path: the full user path hashes wholly to path#<hash8> (D9).
        Assert.Equal(
            RedactionTokens.Path(Redactor.SessionSalt, store.RunsDirectory),
            entry.Fields["file"]);
        Assert.Same(thrown, entry.Exception);
    }

    // === helpers ===============================================================================

    /// <summary>The expected <c>file</c> field for a canonical run-file name
    /// <c>&lt;stamp&gt;-&lt;slug&gt;.json</c>: the timestamp survives joinable, the slug (a
    /// slugified root DN) hashes to <c>run#&lt;hash8&gt;</c> (ADR-037 D9, WP10 #249).</summary>
    private static string ExpectedRunFileToken(string slug, string stamp) =>
        $"{stamp}-{RedactionTokens.Run(Redactor.SessionSalt, slug)}.json";

    private string WriteRunFile(string name, string content)
    {
        var path = Path.Combine(_store.RunsDirectory, name);
        File.WriteAllText(path, content, Utf8NoBom);
        return path;
    }

    /// <summary>A well-formed run JSON (the store's camelCase wire shape — the
    /// <c>AuditRunStoreTests</c> idiom) with an arbitrary <paramref name="version"/>.</summary>
    private static string RunJsonWithSchemaVersion(int version) => $$"""
        {
          "schemaVersion": {{version}},
          "timestamp": "2026-02-01T09:30:00+00:00",
          "rootDn": "OU=AGDLP-Lab,DC=agdlp,DC=lab",
          "connectionDescription": "demo",
          "rulesetName": "Strict AGDLP",
          "rulesetHash": "h",
          "summary": {
            "score": 100,
            "band": "Excellent",
            "critical": 0,
            "warnings": 0,
            "info": 0,
            "passing": 0,
            "checkedSubjects": 0,
            "ruleClasses": 0,
            "uncheckedPresent": false,
            "byRuleClass": {}
          },
          "findings": [],
          "uncheckedDns": []
        }
        """;
}
