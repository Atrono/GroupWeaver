using System.Threading;
using System.Threading.Tasks;

namespace GroupWeaver.App.Settings;

/// <summary>
/// The headless-testable seam over Avalonia's file pickers (AP 3.3 / ADR-011 §4,
/// open-risk #7). The production adapter reaches the OS picker through
/// <c>TopLevel.GetTopLevel(settingsWindow).StorageProvider</c> — the
/// untestable <c>[I]</c> layer added with the window in S7. All import/export
/// LOGIC stays in <see cref="SettingsViewModel"/> behind this seam so it can be
/// driven by a fake under Avalonia.Headless.
/// </summary>
public interface IRulesetFileDialogs
{
    /// <summary>Prompts for a ruleset file to import and returns its text, or
    /// <c>null</c> when the user cancels.</summary>
    Task<string?> PickOpenTextAsync(CancellationToken ct = default);

    /// <summary>Prompts for an export destination and returns the chosen path,
    /// or <c>null</c> when the user cancels.</summary>
    Task<string?> PickSavePathAsync(CancellationToken ct = default);
}
