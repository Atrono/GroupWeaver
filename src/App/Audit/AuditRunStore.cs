using System.Diagnostics;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

using GroupWeaver.Core.Audit;

namespace GroupWeaver.App.Audit;

/// <summary>
/// Persists <see cref="AuditRun"/> artifacts under <c>%APPDATA%\GroupWeaver\runs\</c> (ADR-032 D2 /
/// #190) — the repo-wide user-persistence convention (ADR-008). Mirrors
/// <see cref="Rules.RulesetLocator"/> / <see cref="Settings.UiStateStore"/>: a production ctor
/// resolves <see cref="Environment.SpecialFolder.ApplicationData"/>, an injected-base-directory ctor
/// is the headless test seam.
///
/// <para><see cref="Save"/> is atomic (temp file in the runs directory, then overwrite-move) so a
/// torn write can never destroy a prior run. <see cref="Load"/> / <see cref="List"/> are NEVER-THROW:
/// a corrupt file, an unreadable directory, or a file whose <c>schemaVersion</c> differs from
/// <see cref="AuditRun.CurrentSchemaVersion"/> is SKIPPED with a logged warning, never a crash (the
/// <see cref="Rules.RulesetLocator.LoadEffective"/> degradation contract). De/serialization uses the
/// HARDENED reader (no unmapped members, the strict default encoder — never a relaxed-escaping one),
/// since a new on-disk format is a new deserialization surface (ADR-032 Security note).</para>
///
/// <para><b>Read-only toward AD:</b> the ONLY writes are run JSON files under
/// <c>%APPDATA%\GroupWeaver\runs\</c>. No provider, no LDAP, no AD write.</para>
/// </summary>
public sealed class AuditRunStore
{
    /// <summary>Hardened reader (mirrors <c>RulesetJson.ReadOptions</c>, which is Core-internal):
    /// reject unknown properties so a tampered/older field fails loudly into the never-throw skip,
    /// tolerate property-name case. The default (strict) encoder is used implicitly — never a
    /// relaxed-escaping one (the recurring security-finding class, ADR-032 Security note).</summary>
    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        PropertyNameCaseInsensitive = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>Writer: camelCase, indented, nulls omitted, the STRICT default
    /// <see cref="JavaScriptEncoder"/> (explicit so a future edit cannot silently relax it).</summary>
    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.Default,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    /// <summary>Production store: the repo-wide user-persistence convention is
    /// <c>%APPDATA%\GroupWeaver\</c> (ADR-008).</summary>
    public AuditRunStore()
        : this(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData))
    {
    }

    /// <summary>Test seam: the same <c>GroupWeaver\runs\</c> layout under an injected base directory.</summary>
    public AuditRunStore(string baseDirectory)
    {
        RunsDirectory = Path.Combine(baseDirectory, "GroupWeaver", "runs");
    }

    /// <summary>Full path of the runs directory (which may not yet exist).</summary>
    public string RunsDirectory { get; }

    /// <summary>Persists <paramref name="run"/> atomically to
    /// <c>runs\&lt;timestamp&gt;-&lt;root-slug&gt;.json</c> and returns the written path. The
    /// timestamp segment is the run's INJECTED <see cref="AuditRun.Timestamp"/> in sortable UTC
    /// (<c>yyyyMMddTHHmmssZ</c>), the slug a filesystem-safe rendering of <see cref="AuditRun.RootDn"/>,
    /// so the directory listing sorts chronologically and is hand-inspectable. A torn write can never
    /// destroy a prior run (temp-file + overwrite-move). Throws only on a genuine I/O failure — saving
    /// is a deliberate user act, so the failure is surfaced (unlike the best-effort UI-state save).</summary>
    public string Save(AuditRun run)
    {
        ArgumentNullException.ThrowIfNull(run);
        Directory.CreateDirectory(RunsDirectory);

        var fileName = $"{run.Timestamp.ToUniversalTime():yyyyMMdd'T'HHmmss'Z'}-{Slug(run.RootDn)}.json";
        var path = Path.Combine(RunsDirectory, fileName);

        var tempPath = Path.Combine(RunsDirectory, Path.GetRandomFileName() + ".groupweaver-tmp");
        try
        {
            File.WriteAllText(tempPath, JsonSerializer.Serialize(run, WriteOptions), Utf8NoBom);
            File.Move(tempPath, path, overwrite: true);
        }
        catch
        {
            try
            {
                File.Delete(tempPath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Best-effort cleanup; the original failure is surfaced below.
            }

            throw;
        }

        return path;
    }

    /// <summary>Reads one run file — NEVER throws: a missing / unreadable / corrupt file, or one whose
    /// <c>schemaVersion</c> is not <see cref="AuditRun.CurrentSchemaVersion"/>, yields <c>null</c> with
    /// a logged warning (the never-throw degradation contract).</summary>
    public AuditRun? Load(string path)
    {
        try
        {
            var json = File.ReadAllText(path);
            var run = JsonSerializer.Deserialize<AuditRun>(json, ReadOptions);
            if (run is null || run.SchemaVersion != AuditRun.CurrentSchemaVersion)
            {
                Debug.WriteLine($"AuditRunStore: skipping '{path}' (unsupported or empty run schema).");
                return null;
            }

            return run;
        }
        catch (Exception ex) when (
            ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException
                or ArgumentException)
        {
            Debug.WriteLine($"AuditRunStore: skipping unreadable run '{path}': {ex.Message}");
            return null;
        }
    }

    /// <summary>Lists every readable run, newest <see cref="AuditRun.Timestamp"/> first (ties broken by
    /// file name, descending). NEVER throws: an absent / unreadable runs directory yields an empty list;
    /// individual corrupt or older-schema files are skipped (see <see cref="Load"/>). This is the
    /// enumeration the compare default + the recent-scopes memory (ADR-032 D5) derive from.</summary>
    public IReadOnlyList<AuditRun> List()
    {
        string[] files;
        try
        {
            if (!Directory.Exists(RunsDirectory))
            {
                return Array.Empty<AuditRun>();
            }

            files = Directory.GetFiles(RunsDirectory, "*.json");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Debug.WriteLine($"AuditRunStore: runs directory '{RunsDirectory}' unreadable: {ex.Message}");
            return Array.Empty<AuditRun>();
        }

        var runs = new List<AuditRun>();
        foreach (var file in files)
        {
            if (Load(file) is { } run)
            {
                runs.Add(run);
            }
        }

        return runs
            .OrderByDescending(r => r.Timestamp)
            .ToList();
    }

    /// <summary>The most recent prior saved run for <paramref name="rootDn"/> (the compare default,
    /// ADR-032 D4), or <c>null</c> when none exists. Matched on <see cref="AuditRun.RootDn"/> via the
    /// DN-comparison policy (<see cref="Core.Model.Dn.Comparer"/>), newest first.</summary>
    public AuditRun? MostRecentFor(string rootDn) =>
        List().FirstOrDefault(r => Core.Model.Dn.Comparer.Equals(r.RootDn, rootDn));

    /// <summary>The distinct scope roots seen across all saved runs, most-recently-used first (ADR-032
    /// D5 recent-scopes memory, derived for free from the runs index — first occurrence wins via the
    /// newest-first <see cref="List"/> order, deduped by <see cref="Core.Model.Dn.Comparer"/>).</summary>
    public IReadOnlyList<string> RecentRoots() =>
        List()
            .Select(r => r.RootDn)
            .Distinct(Core.Model.Dn.Comparer)
            .ToList();

    /// <summary>UTF-8 without a BOM — the bytes the rest of the codebase's file writers use.</summary>
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>A filesystem-safe, deterministic slug of a DN for the run file name: every char outside
    /// <c>[A-Za-z0-9-]</c> becomes <c>_</c>, runs of <c>_</c> collapse, and the result is trimmed and
    /// capped at 80 chars (the file name is a convenience label — identity lives INSIDE the JSON's
    /// <see cref="AuditRun.RootDn"/>). An empty/degenerate slug falls back to <c>"root"</c>.</summary>
    private static string Slug(string dn)
    {
        var builder = new StringBuilder(dn.Length);
        var lastWasUnderscore = false;
        foreach (var ch in dn)
        {
            if (char.IsAsciiLetterOrDigit(ch) || ch == '-')
            {
                builder.Append(ch);
                lastWasUnderscore = false;
            }
            else if (!lastWasUnderscore)
            {
                builder.Append('_');
                lastWasUnderscore = true;
            }
        }

        var slug = builder.ToString().Trim('_');
        if (slug.Length > 80)
        {
            slug = slug[..80].Trim('_');
        }

        return slug.Length == 0 ? "root" : slug;
    }
}
