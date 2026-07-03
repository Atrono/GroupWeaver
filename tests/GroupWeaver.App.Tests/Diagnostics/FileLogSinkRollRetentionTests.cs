using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;

using GroupWeaver.App.Diagnostics;

using Microsoft.Extensions.Logging;

using Xunit;

namespace GroupWeaver.App.Tests.Diagnostics;

/// <summary>
/// Pins <see cref="FileLogSink"/>'s size-cap roll (<c>-part2</c>/<c>-part3</c>…, ADR-037 D3) and
/// the startup retention prune (<see cref="FileLogSink.PruneOldLogs"/>: newest-first keep, both a
/// file-count and a total-bytes cap, gw-*.jsonl ONLY — crash markers survive). Caps are injected
/// through the INTERNAL test-seam ctor / method over a temp directory — never the real
/// <c>%APPDATA%</c>, never the production 5 MB.
/// </summary>
public sealed class FileLogSinkRollRetentionTests : IDisposable
{
    private static readonly Session TestSession =
        new("sid00001", new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero));

    private readonly string _dir =
        Directory.CreateTempSubdirectory("groupweaver-sink-roll-").FullName;

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

    // === 1. Roll: -partN appears, CurrentLogFilePath advances, no torn/interleaved lines =======

    /// <summary>With a tiny 1 KB cap the sink rolls to <c>-part2</c> (and beyond):
    /// <see cref="FileLogSink.CurrentLogFilePath"/> advances off the base file, the part files
    /// exist, EVERY line in EVERY file parses as one JSON object (no torn writes), and the
    /// sequence payloads concatenated in part order are exactly 0..N-1 (no loss, no reorder,
    /// no interleave across the roll boundary).</summary>
    [Fact]
    public void TinyMaxFileBytes_RollsToPartFiles_AdvancesCurrentLogFilePath_NoTornLines()
    {
        const int EventCount = 60;
        var sink = new FileLogSink(
            _dir, LogLevel.Information, TestSession, maxFileBytes: 1024, channelCapacity: 4096);
        var basePath = sink.CurrentLogFilePath;

        var logger = sink.CreateLogger("Roll");
        var payload = new string('x', 100);
        for (var i = 0; i < EventCount; i++)
        {
            logger.LogInformation(new EventId(0, "RollFill"), "{seq} {payload}", i, payload);
        }

        sink.Dispose();

        // CurrentLogFilePath advanced to a -partN file (N >= 2) — the crash marker's logFile field
        // always names the file actually being written.
        Assert.NotEqual(basePath, sink.CurrentLogFilePath);
        Assert.Matches(@"-part\d+\.jsonl$", Path.GetFileName(sink.CurrentLogFilePath));
        Assert.True(PartIndex(sink.CurrentLogFilePath) >= 2);

        // The base file rolled because it CROSSED the cap (roll is size-triggered, not time).
        Assert.True(File.Exists(basePath));
        Assert.True(new FileInfo(basePath).Length >= 1024);
        var part2 = basePath[..^".jsonl".Length] + "-part2.jsonl";
        Assert.True(File.Exists(part2), "-part2 must appear after the first roll");

        // Whole-line integrity across ALL parts: every line parses, and the seq payloads
        // concatenated in part order are exactly 0..N-1.
        var seqs = new List<int>();
        foreach (var file in Directory.GetFiles(_dir, "gw-*.jsonl").OrderBy(PartIndex))
        {
            foreach (var line in File.ReadAllLines(file))
            {
                using var doc = JsonDocument.Parse(line);
                Assert.Equal("RollFill", doc.RootElement.GetProperty("evt").GetString());
                seqs.Add(doc.RootElement.GetProperty("seq").GetInt32());
            }
        }

        Assert.Equal(Enumerable.Range(0, EventCount), seqs);
    }

    // === 2. Retention: file-count cap, oldest deleted first ====================================

    [Fact]
    public void PruneOldLogs_KeepsOnlyTheNewestN_DeletesOldestFirst()
    {
        var names = SeedLogFiles(count: 5, sizeBytes: 10);

        FileLogSink.PruneOldLogs(_dir, retainFiles: 2, retainTotalBytes: long.MaxValue);

        var remaining = Directory.GetFiles(_dir, "gw-*.jsonl").Select(Path.GetFileName).ToHashSet();
        Assert.Equal(2, remaining.Count);
        Assert.Contains(names[4], remaining); // newest
        Assert.Contains(names[3], remaining); // second-newest
    }

    // === 3. Retention: total-bytes cap =========================================================

    [Fact]
    public void PruneOldLogs_EnforcesTheTotalBytesCap_NewestFirstWithinBudget()
    {
        var names = SeedLogFiles(count: 3, sizeBytes: 100);

        // 150-byte budget: the newest (100 B) fits; the second pushes the running total to 200 —
        // over budget, so it and everything older is deleted.
        FileLogSink.PruneOldLogs(_dir, retainFiles: 10, retainTotalBytes: 150);

        var remaining = Directory.GetFiles(_dir, "gw-*.jsonl").Select(Path.GetFileName).ToArray();
        Assert.Equal(new[] { names[2] }, remaining);
    }

    // === 4. Retention scope: gw-*.jsonl ONLY — crash markers and foreign files survive ==========

    /// <summary>The prune touches ONLY <c>gw-*.jsonl</c>: crash markers are the user's
    /// issue-attachment evidence (ADR-037 D7 — the next start reports them) and must survive
    /// even the tightest caps.</summary>
    [Fact]
    public void PruneOldLogs_NeverTouchesCrashMarkersOrForeignFiles()
    {
        SeedLogFiles(count: 2, sizeBytes: 10);
        var marker = Path.Combine(_dir, "crash-abcd1234-20260101T120000Z.json");
        File.WriteAllText(marker, "{}");
        var foreign = Path.Combine(_dir, "notes.txt");
        File.WriteAllText(foreign, "keep me");

        FileLogSink.PruneOldLogs(_dir, retainFiles: 0, retainTotalBytes: 0);

        Assert.Empty(Directory.GetFiles(_dir, "gw-*.jsonl"));
        Assert.True(File.Exists(marker), "crash markers must never be pruned");
        Assert.True(File.Exists(foreign));
    }

    // === 5. Retention never throws =============================================================

    [Fact]
    public void PruneOldLogs_MissingDirectory_NeverThrows()
    {
        var missing = Path.Combine(_dir, "does-not-exist");
        var ex = Record.Exception(() => FileLogSink.PruneOldLogs(missing, 10, 1024));
        Assert.Null(ex);
    }

    // === helpers ===============================================================================

    /// <summary>Seeds <paramref name="count"/> fake log files <c>gw-000.jsonl</c>… with staggered
    /// <c>LastWriteTimeUtc</c> (index 0 = oldest). Returns the file NAMES in seed order.</summary>
    private string[] SeedLogFiles(int count, int sizeBytes)
    {
        var baseTime = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var names = new string[count];
        for (var i = 0; i < count; i++)
        {
            names[i] = $"gw-{i:D3}.jsonl";
            var path = Path.Combine(_dir, names[i]);
            File.WriteAllText(path, new string('x', sizeBytes));
            File.SetLastWriteTimeUtc(path, baseTime.AddMinutes(i));
        }

        return names;
    }

    /// <summary>1 for the base file, N for <c>-partN</c> — the roll's file order.</summary>
    private static int PartIndex(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        var marker = name.LastIndexOf("-part", StringComparison.Ordinal);
        return marker < 0 ? 1 : int.Parse(name[(marker + "-part".Length)..], CultureInfo.InvariantCulture);
    }
}
