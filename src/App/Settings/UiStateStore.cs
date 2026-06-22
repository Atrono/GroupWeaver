using System.Text.Json;

namespace GroupWeaver.App.Settings;

/// <summary>
/// Persists the adaptive-rail UI preferences (ADR-022 D4) to
/// <c>%APPDATA%\GroupWeaver\ui-state.json</c> — the repo-wide user-persistence
/// convention (ADR-008/ADR-011). Mirrors <see cref="Rules.RulesetLocator"/>: a
/// production ctor resolves <see cref="Environment.SpecialFolder.ApplicationData"/>;
/// an injected-base-directory ctor is the headless test seam.
///
/// <para><see cref="Load"/> is NEVER-THROW (missing / unreadable / corrupt ⇒
/// <see cref="UiState.Default"/>, the <see cref="Rules.RulesetLocator.LoadEffective"/>
/// degradation contract) and <see cref="Save"/> is atomic temp-file+move (the
/// <c>RulesetSerializer.Save</c> convention). App-preference state only — no untrusted
/// input, no AD; the read-only product is unaffected.</para>
/// </summary>
public sealed class UiStateStore
{
    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };

    /// <summary>Production store: the repo-wide user-persistence convention is
    /// <c>%APPDATA%\GroupWeaver\</c> (ADR-008).</summary>
    public UiStateStore()
        : this(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData))
    {
    }

    /// <summary>Test seam: the same layout under an injected base directory.</summary>
    public UiStateStore(string baseDirectory)
    {
        StatePath = Path.Combine(baseDirectory, "GroupWeaver", "ui-state.json");
    }

    /// <summary>Full path of the UI-state file (which may not exist).</summary>
    public string StatePath { get; }

    /// <summary>Reads the persisted UI state — never throws: a missing, unreadable,
    /// or corrupt file yields <see cref="UiState.Default"/>.</summary>
    public UiState Load()
    {
        try
        {
            var json = File.ReadAllText(StatePath);
            return JsonSerializer.Deserialize<UiState>(json) ?? UiState.Default;
        }
        catch (Exception ex) when (
            ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException
                or ArgumentException)
        {
            return UiState.Default;
        }
    }

    /// <summary>Persists <paramref name="state"/> atomically (temp file in the target
    /// directory, then overwrite-move). Best-effort (ADR-022 D4): a torn write can never
    /// destroy the previous file, and any failure is swallowed — UI preferences are
    /// non-critical.</summary>
    public void Save(UiState state)
    {
        try
        {
            var directory = Path.GetDirectoryName(StatePath)!;
            Directory.CreateDirectory(directory);

            var tempPath = Path.Combine(directory, Path.GetRandomFileName() + ".groupweaver-tmp");
            try
            {
                File.WriteAllText(tempPath, JsonSerializer.Serialize(state, WriteOptions));
                File.Move(tempPath, StatePath, overwrite: true);
            }
            catch
            {
                try
                {
                    File.Delete(tempPath);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Best-effort cleanup; the original failure is swallowed below.
                }

                throw;
            }
        }
        catch (Exception ex) when (
            ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            // Best-effort persistence (ADR-022 D4): UI preferences are non-critical, so a
            // failed save is silently dropped rather than surfaced to the user.
        }
    }
}

/// <summary>The persisted adaptive-rail UI preferences (ADR-022 D4): the rail's pixel
/// width and whether it is collapsed. Two scalars, best-effort.</summary>
public sealed record UiState(double RailWidth, bool RailCollapsed)
{
    /// <summary>The seed values when no state has been persisted yet — the ADR-022 D3
    /// defaults (rail 340 px, expanded).</summary>
    public static UiState Default { get; } = new(340, false);
}
