using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

using Avalonia.Controls;
using Avalonia.Platform.Storage;

namespace GroupWeaver.App.Settings;

/// <summary>
/// The production adapter behind <see cref="IRulesetFileDialogs"/> (AP 3.3 / S7,
/// ADR-011 §4, open-risk #7): a THIN layer over Avalonia's
/// <see cref="IStorageProvider"/>, reached through
/// <see cref="TopLevel.GetTopLevel(Avalonia.Visual)"/> on the settings window. ALL
/// import/export LOGIC stays in <see cref="SettingsViewModel"/> behind the seam — this
/// class only opens the OS picker and reads/returns a text or a path, so it is the
/// untestable <c>[I]</c> layer; the VM is driven by a fake under Avalonia.Headless.
/// Read-only toward AD (it only touches local files the user picks; never the directory).
/// </summary>
public sealed class StorageProviderRulesetFileDialogs : IRulesetFileDialogs
{
    private static readonly FilePickerFileType RulesetFileType = new("Ruleset files")
    {
        Patterns = ["*.jsonc", "*.json"],
    };

    private readonly TopLevel _topLevel;

    /// <summary>Wraps the picker of <paramref name="topLevel"/> — the settings window's
    /// own <see cref="TopLevel"/> (<c>TopLevel.GetTopLevel(window)</c>).</summary>
    public StorageProviderRulesetFileDialogs(TopLevel topLevel) => _topLevel = topLevel;

    /// <inheritdoc/>
    public async Task<string?> PickOpenTextAsync(CancellationToken ct = default)
    {
        var files = await _topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Import ruleset",
            AllowMultiple = false,
            FileTypeFilter = [RulesetFileType],
        }).ConfigureAwait(true);

        if (files.Count == 0)
        {
            return null;
        }

        await using var stream = await files[0].OpenReadAsync().ConfigureAwait(true);
        using var reader = new StreamReader(stream);
        return await reader.ReadToEndAsync(ct).ConfigureAwait(true);
    }

    /// <inheritdoc/>
    public async Task<string?> PickSavePathAsync(CancellationToken ct = default)
    {
        var file = await _topLevel.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Export ruleset",
            DefaultExtension = "jsonc",
            FileTypeChoices = [RulesetFileType],
            ShowOverwritePrompt = true,
        }).ConfigureAwait(true);

        return file?.TryGetLocalPath();
    }
}
