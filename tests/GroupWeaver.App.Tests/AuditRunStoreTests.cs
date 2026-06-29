using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

using GroupWeaver.App.Audit;
using GroupWeaver.Core.Audit;
using GroupWeaver.Core.Rules;

using Xunit;

namespace GroupWeaver.App.Tests;

/// <summary>
/// Pins the on-disk persistence contract of <see cref="AuditRunStore"/> (ADR-032 D2 / #190): the
/// <c>%APPDATA%\GroupWeaver\runs\</c> atomic-write / never-throw / injected-base-dir idiom, and the
/// STJ reflection round-trip of a full <see cref="AuditRun"/> (the <see cref="RuleSeverity"/>
/// enum-as-string converter + the <c>IReadOnlyList</c>/<c>IReadOnlyDictionary</c> members).
///
/// <para>Hermetic per the #124 isolation lesson: every store is constructed over a
/// <see cref="Directory.CreateTempSubdirectory(string)"/>-backed base directory — NEVER the real
/// <c>%APPDATA%</c>. The store is a sibling of <c>UiStateStore</c>/<c>RulesetLocator</c>; this fixture
/// uses ONLY the injected-base-dir ctor.</para>
/// </summary>
public sealed class AuditRunStoreTests : IDisposable
{
    private const string RootDn = "OU=AGDLP-Lab,DC=agdlp,DC=lab";
    private readonly string _baseDir = Directory.CreateTempSubdirectory("groupweaver-runstore-tests-").FullName;

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

    // === 1. Save -> Load full round-trip: every persisted field survives =================

    [Fact]
    public void SaveThenLoad_FullRun_RoundTripsEveryPersistedField()
    {
        var store = new AuditRunStore(_baseDir);
        var run = SampleRun(
            timestamp: new DateTimeOffset(2026, 2, 1, 9, 30, 0, TimeSpan.Zero),
            rulesetHash: "deadbeef",
            findings: new[]
            {
                Finding(RuleIds.Nesting, RuleSeverity.Error,
                    "CN=DL_X,OU=AGDLP-Lab,DC=agdlp,DC=lab", "CN=User,OU=AGDLP-Lab,DC=agdlp,DC=lab"),
                Finding("naming-gg", RuleSeverity.Warning, "CN=GG_Bad,OU=AGDLP-Lab,DC=agdlp,DC=lab"),
                Finding(RuleIds.EmptyGroup, RuleSeverity.Info, "CN=GG_Empty,OU=AGDLP-Lab,DC=agdlp,DC=lab"),
            },
            uncheckedDns: new[] { "OU=Sales,OU=AGDLP-Lab,DC=agdlp,DC=lab" });

        var path = store.Save(run);
        var loaded = store.Load(path);

        Assert.NotNull(loaded);

        // Scalar identity fields.
        Assert.Equal(AuditRun.CurrentSchemaVersion, loaded!.SchemaVersion);
        Assert.Equal(run.Timestamp, loaded.Timestamp);
        Assert.Equal(run.RootDn, loaded.RootDn);
        Assert.Equal(run.ConnectionDescription, loaded.ConnectionDescription);
        Assert.Equal(run.RulesetName, loaded.RulesetName);
        Assert.Equal(run.RulesetHash, loaded.RulesetHash);

        // Summary, incl. ByRuleClass (the IReadOnlyDictionary) compared as a sorted projection.
        Assert.Equal(run.Summary.Score, loaded.Summary.Score);
        Assert.Equal(run.Summary.Band, loaded.Summary.Band);
        Assert.Equal(run.Summary.Critical, loaded.Summary.Critical);
        Assert.Equal(SortedByRuleClass(run.Summary), SortedByRuleClass(loaded.Summary));

        // Findings (RuleId / Severity / PrimaryDn / Dns / Message) as a projection, in order.
        Assert.Equal(FindingProjection(run.Findings), FindingProjection(loaded.Findings));

        // UncheckedDns (the IReadOnlyList) survives verbatim.
        Assert.Equal(run.UncheckedDns, loaded.UncheckedDns);
    }

    /// <summary>The enum-as-string contract is on-disk: the persisted file stores the
    /// <see cref="RuleSeverity"/> as a STRING (camelCase), never a raw integer — so a future enum
    /// reorder cannot silently re-map an old run's severities.</summary>
    [Fact]
    public void Save_WritesSeverityAsString_NotAnInteger()
    {
        var store = new AuditRunStore(_baseDir);
        var run = SampleRun(
            timestamp: new DateTimeOffset(2026, 2, 1, 9, 30, 0, TimeSpan.Zero),
            rulesetHash: "h",
            findings: new[] { Finding(RuleIds.Nesting, RuleSeverity.Error, "CN=DL,OU=AGDLP-Lab,DC=agdlp,DC=lab") },
            uncheckedDns: Array.Empty<string>());

        var path = store.Save(run);
        var json = File.ReadAllText(path);

        Assert.Contains("\"error\"", json);           // severity rendered as a camelCase string
        Assert.DoesNotContain("\"severity\": 2", json); // never the raw enum integer
    }

    // === 2. Never-throw on a corrupt JSON file: Load -> null, List skips it ===============

    [Fact]
    public void Load_CorruptJson_ReturnsNull_NeverThrows_AndListSkipsIt()
    {
        var store = new AuditRunStore(_baseDir);
        Directory.CreateDirectory(store.RunsDirectory);
        var corruptPath = Path.Combine(store.RunsDirectory, "20260201T093000Z-corrupt.json");
        File.WriteAllText(corruptPath, "{ this is not valid json", Utf8NoBom);

        // Plus one good run so List has something to keep while skipping the corrupt one.
        store.Save(SampleRun(
            new DateTimeOffset(2026, 2, 2, 0, 0, 0, TimeSpan.Zero), "h", Array.Empty<AuditRunFinding>(), Array.Empty<string>()));

        var loaded = Record.Exception(() => store.Load(corruptPath));
        Assert.Null(loaded); // no exception thrown by Record.Exception means Load did not throw
        Assert.Null(store.Load(corruptPath)); // ...and it returns null

        var list = store.List();
        Assert.Single(list); // the corrupt file is skipped, the good run survives
    }

    // === 3. Never-throw / skip on an unknown or older schemaVersion ======================

    [Fact]
    public void Load_DifferentSchemaVersion_IsSkipped_ReturnsNull()
    {
        var store = new AuditRunStore(_baseDir);
        Directory.CreateDirectory(store.RunsDirectory);

        // A well-formed run JSON whose schemaVersion is one the current build does not support — both
        // an OLDER (0) and a future/unknown (999) value must be skipped, never crash-loaded.
        foreach (var (version, name) in new[] { (0, "older"), (999, "future") })
        {
            var path = Path.Combine(store.RunsDirectory, $"20260201T0930{version:D2}Z-{name}.json");
            File.WriteAllText(path, RunJsonWithSchemaVersion(version), Utf8NoBom);
            Assert.Null(store.Load(path));
        }

        // List skips both unsupported files and returns empty (no good runs saved).
        Assert.Empty(store.List());
    }

    // === 4. List returns saved runs, newest first ========================================

    [Fact]
    public void List_ReturnsSavedRuns_NewestFirst()
    {
        var store = new AuditRunStore(_baseDir);
        var older = SampleRun(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero), "h1", Array.Empty<AuditRunFinding>(), Array.Empty<string>());
        var newer = SampleRun(new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero), "h2", Array.Empty<AuditRunFinding>(), Array.Empty<string>());

        store.Save(older);
        store.Save(newer);

        var list = store.List();
        Assert.Equal(2, list.Count);
        Assert.Equal(newer.Timestamp, list[0].Timestamp); // newest first
        Assert.Equal(older.Timestamp, list[1].Timestamp);
    }

    [Fact]
    public void List_NoRunsDirectory_ReturnsEmpty_NeverThrows()
    {
        var store = new AuditRunStore(_baseDir); // runs dir never created
        Assert.False(Directory.Exists(store.RunsDirectory));
        Assert.Empty(store.List());
    }

    // === 5. MostRecentFor is case-insensitive on rootDn and returns the latest ===========

    [Fact]
    public void MostRecentFor_IsCaseInsensitive_AndReturnsTheLatestForThatRoot()
    {
        var store = new AuditRunStore(_baseDir);
        var otherRoot = "OU=Other,DC=agdlp,DC=lab";

        store.Save(SampleRun(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero), "h", Array.Empty<AuditRunFinding>(), Array.Empty<string>(), RootDn));
        var latest = SampleRun(new DateTimeOffset(2026, 4, 1, 0, 0, 0, TimeSpan.Zero), "h", Array.Empty<AuditRunFinding>(), Array.Empty<string>(), RootDn);
        store.Save(latest);
        store.Save(SampleRun(new DateTimeOffset(2026, 5, 1, 0, 0, 0, TimeSpan.Zero), "h", Array.Empty<AuditRunFinding>(), Array.Empty<string>(), otherRoot));

        // Case-variant root DN still matches (Dn.Comparer) and returns the latest run for that root,
        // never the even-newer run under the OTHER root.
        var found = store.MostRecentFor(RootDn.ToLowerInvariant());

        Assert.NotNull(found);
        Assert.Equal(latest.Timestamp, found!.Timestamp);
        Assert.Equal(RootDn, found.RootDn);
    }

    [Fact]
    public void MostRecentFor_NoRunForRoot_ReturnsNull()
    {
        var store = new AuditRunStore(_baseDir);
        store.Save(SampleRun(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero), "h", Array.Empty<AuditRunFinding>(), Array.Empty<string>(), RootDn));
        Assert.Null(store.MostRecentFor("OU=Nope,DC=agdlp,DC=lab"));
    }

    // === 6. RecentRoots: distinct roots, most-recently-used first ========================

    [Fact]
    public void RecentRoots_AreDistinctByDn_MostRecentlyUsedFirst()
    {
        var store = new AuditRunStore(_baseDir);
        var rootA = "OU=A,DC=agdlp,DC=lab";
        var rootB = "OU=B,DC=agdlp,DC=lab";

        // A (oldest), B (middle), A again (newest) -> A is most-recently-used, deduped by DN.
        store.Save(SampleRun(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero), "h", Array.Empty<AuditRunFinding>(), Array.Empty<string>(), rootA));
        store.Save(SampleRun(new DateTimeOffset(2026, 2, 1, 0, 0, 0, TimeSpan.Zero), "h", Array.Empty<AuditRunFinding>(), Array.Empty<string>(), rootB));
        store.Save(SampleRun(new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero), "h", Array.Empty<AuditRunFinding>(), Array.Empty<string>(), rootA.ToUpperInvariant()));

        var roots = store.RecentRoots();

        // Distinct (A appears once, despite two runs) and most-recently-used first (A before B).
        Assert.Equal(2, roots.Count);
        Assert.Equal(rootA.ToUpperInvariant(), roots[0]); // newest A spelling wins (List is newest-first)
        Assert.Equal(rootB, roots[1]);
    }

    // === helpers =========================================================================

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private static AuditRunFinding Finding(string ruleId, RuleSeverity severity, params string[] dns) =>
        new(ruleId, severity, dns[0], dns, $"{ruleId} on {dns[0]}");

    private static AuditRun SampleRun(
        DateTimeOffset timestamp,
        string rulesetHash,
        IReadOnlyList<AuditRunFinding> findings,
        IReadOnlyList<string> uncheckedDns,
        string rootDn = RootDn) =>
        new(
            AuditRun.CurrentSchemaVersion,
            timestamp,
            rootDn,
            "demo · 7 objects",
            "Strict AGDLP",
            rulesetHash,
            new AuditSummary(
                Score: 55,
                Band: "Action required",
                Critical: findings.Count(f => f.Severity == RuleSeverity.Error),
                Warnings: findings.Count(f => f.Severity == RuleSeverity.Warning),
                Info: findings.Count(f => f.Severity == RuleSeverity.Info),
                Passing: 3,
                CheckedSubjects: 6,
                RuleClasses: 6,
                UncheckedPresent: uncheckedDns.Count > 0,
                ByRuleClass: findings
                    .GroupBy(f => f.RuleId, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase)),
            findings,
            uncheckedDns);

    private static (string, int)[] SortedByRuleClass(AuditSummary summary) =>
        summary.ByRuleClass
            .Select(kvp => (kvp.Key, kvp.Value))
            .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private static (string RuleId, RuleSeverity Severity, string PrimaryDn, string Dns, string Message)[] FindingProjection(
        IReadOnlyList<AuditRunFinding> findings) =>
        findings
            .Select(f => (f.RuleId, f.Severity, f.PrimaryDn, string.Join("|", f.Dns), f.Message))
            .ToArray();

    /// <summary>A well-formed run JSON (the store's camelCase wire shape) with an arbitrary
    /// <paramref name="version"/> — used to prove an unsupported schemaVersion is skipped.</summary>
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
