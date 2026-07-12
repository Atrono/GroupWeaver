using System;
using System.IO;
using System.Text.Json;

using GroupWeaver.App.Diagnostics;

using Xunit;

namespace GroupWeaver.App.Tests.Diagnostics;

/// <summary>
/// Pins the ADR-037 D9 redaction of the CRASH MARKER (WP10 #249) through the same internal
/// <see cref="Program.WriteCrashMarker"/> seam <c>CrashMarkerTests</c> already uses (the
/// <c>GROUPWEAVER_LOG_DIR</c> env override; this assembly runs sequentially and the scope
/// restores the prior value): the marker's <c>msgScrubbed</c> AND <c>stack</c> both pass
/// through <see cref="Redactor.Scrub"/> — the security review found the marker writer
/// persisting the raw exception message and stack, and a crash artifact users attach to
/// public issues is exactly where a raw DN must never survive. <c>CrashMarkerTests</c> keeps
/// pinning the schema/atomicity; this file pins only the redaction of the two text fields.
/// </summary>
public sealed class CrashMarkerRedactionTests : IDisposable
{
    private const string HostileDn = "CN=GG_Übung\\, Kernteam,OU=AGDLP-Lab,DC=agdlp,DC=lab";

    private static readonly string[] RawDnFragments = ["GG_Übung", "Kernteam", "AGDLP-Lab", "agdlp"];

    private readonly string _dir =
        Directory.CreateTempSubdirectory("groupweaver-crashredact-").FullName;

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

    [Fact]
    public void CrashMarker_ScrubsTheExceptionMessage_AndTheStack()
    {
        var logDir = Path.Combine(_dir, "logs");
        using var scope = new EnvVarScope("GROUPWEAVER_LOG_DIR", logDir);

        var exception = new StackedException(
            $"bind failed for {HostileDn}",
            $"   at GroupWeaver.Providers.LdapProvider.Load({HostileDn})");
        Program.WriteCrashMarker(exception, "gw-test.jsonl");

        var marker = Assert.Single(Directory.GetFiles(logDir, "crash-*.json"));
        using var doc = JsonDocument.Parse(File.ReadAllText(marker));
        var root = doc.RootElement;

        // msgScrubbed: the DN run (end-of-text) is replaced by ITS dn# token — exact pin, so
        // the marker joins the session's log lines that redacted the same DN.
        var token = RedactionTokens.Dn(Redactor.SessionSalt, HostileDn);
        Assert.Equal($"bind failed for {token}", root.GetProperty("msgScrubbed").GetString());

        // stack: scrubbed, not dropped — no raw fragment survives, a dn# token is present,
        // and the non-sensitive frame text stays greppable.
        var stack = root.GetProperty("stack").GetString()!;
        foreach (var fragment in RawDnFragments)
        {
            Assert.DoesNotContain(fragment, stack, StringComparison.OrdinalIgnoreCase);
        }

        Assert.Contains("dn#", stack, StringComparison.Ordinal);
        Assert.Contains("LdapProvider.Load", stack, StringComparison.Ordinal);
    }

    /// <summary>Sets a process environment variable for the scope's lifetime and RESTORES the
    /// prior value on dispose (the <c>CrashMarkerTests</c> idiom).</summary>
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
