using System;
using System.IO;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;

using GroupWeaver.App.Audit;

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
/// <c>file</c> field routes through the TYPED redaction stubs (ADR-037 D9 redaction-correct-now):
/// <c>Redactor.RunFile</c> for a run-file name (its slug is a slugified DN),
/// <c>Redactor.Path</c> for the runs directory (a full user path) — both identity in WP1, so the
/// equality assertions below hold verbatim; WP10 changes the VALUES (hash the slug / reduce the
/// path), not this shape. WHICH helper a call site uses is not observable through identity stubs
/// (no reflection here) — it is pinned by the call-site comments in <see cref="AuditRunStore"/>
/// and becomes observable (and re-pinned) when WP10's real implementations land.
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
        Assert.Equal(Path.GetFileName(path), entry.Fields["file"]); // via Redactor.RunFile (identity in WP1)
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
        // file = the run-file NAME, via Redactor.RunFile (identity in WP1).
        Assert.Equal(Path.GetFileName(older), entries[0].Fields["file"]);
        Assert.Equal(Path.GetFileName(future), entries[1].Fields["file"]);
        Assert.Equal(Path.GetFileName(nullBody), entries[2].Fields["file"]);
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
        Assert.Equal(Path.GetFileName(path), entry.Fields["file"]); // via Redactor.RunFile (identity in WP1)
        Assert.IsAssignableFrom<IOException>(entry.Exception);
    }

    // === 4. io: the runs DIRECTORY itself is unlistable ========================================

    /// <summary>When the runs directory exists but cannot be ENUMERATED (an ACL denying
    /// list-directory to the current user — the "unreadable directory" arm), <c>List()</c>
    /// degrades to empty with ONE io-skip naming the DIRECTORY, not a file.</summary>
    [Fact]
    public void UnlistableRunsDirectory_ListsEmpty_WithOneIoSkipNamingTheDirectory()
    {
        var dirInfo = new DirectoryInfo(_store.RunsDirectory);
        var user = WindowsIdentity.GetCurrent().User!;
        var deny = new FileSystemAccessRule(user, FileSystemRights.ListDirectory, AccessControlType.Deny);
        var acl = dirInfo.GetAccessControl();
        acl.AddAccessRule(deny);
        dirInfo.SetAccessControl(acl);
        try
        {
            Assert.Empty(_store.List());
        }
        finally
        {
            var restore = dirInfo.GetAccessControl();
            restore.RemoveAccessRule(deny);
            dirInfo.SetAccessControl(restore);
        }

        var entry = Assert.Single(_capture.EntriesNamed("AuditRunSkipped"));
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.Equal("io", entry.Fields["reason"]);
        Assert.Equal(_store.RunsDirectory, entry.Fields["file"]); // via Redactor.Path (identity in WP1)
        Assert.True(
            entry.Exception is UnauthorizedAccessException or IOException,
            $"expected an access/IO failure, got {entry.Exception?.GetType().Name ?? "null"}");
    }

    // === helpers ===============================================================================

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
