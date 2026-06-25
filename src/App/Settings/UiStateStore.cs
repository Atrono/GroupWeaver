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
/// width and whether it is collapsed, plus the ADR-026 D4 app-chrome theme. Best-effort.</summary>
public sealed record UiState(double RailWidth, bool RailCollapsed)
{
    /// <summary>The app-chrome theme variant (ADR-026 D4): <c>"Dark"</c> | <c>"Light"</c>,
    /// dark-first default. A NON-positional <c>init</c> property (not a ctor param) so the
    /// existing two-arg <c>new UiState(width, collapsed)</c> call sites keep compiling and an
    /// old <c>ui-state.json</c> with no <c>theme</c> field deserializes to the default — the
    /// JSON stays forward/back compatible (ADR-026 D4, the never-throw load contract).</summary>
    public string Theme { get; init; } = "Dark";

    /// <summary>The findings sidebar's share of the rail's findings+detail vertical space
    /// (WP-B / #178): <c>0.5</c> ⇒ a 1:1 split, clamped to <c>[0.2, 0.8]</c> in
    /// <see cref="ViewModels.WorkspaceViewModel.OnRailFindingsFractionChanged"/> so neither
    /// section collapses. A NON-positional <c>init</c> property (like <see cref="Theme"/>) so the
    /// existing two-arg <c>new UiState(width, collapsed)</c> call sites keep compiling and an old
    /// <c>ui-state.json</c> with no <c>railFindingsFraction</c> field deserializes to the default —
    /// the JSON stays forward/back compatible (the never-throw load contract).</summary>
    public double RailFindingsFraction { get; init; } = 0.5;

    /// <summary>The seed values when no state has been persisted yet — the ADR-022 D3
    /// defaults (rail 340 px, expanded), the ADR-026 dark-first default theme, and the WP-B
    /// 1:1 findings/detail split.</summary>
    public static UiState Default { get; } = new(340, false);
}
