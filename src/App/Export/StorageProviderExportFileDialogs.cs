using System.Threading;
using System.Threading.Tasks;

using Avalonia.Controls;
using Avalonia.Platform.Storage;

namespace GroupWeaver.App.Export;

/// <summary>
/// The production adapter behind <see cref="IExportFileDialogs"/> (AP 4.1 / ADR-013 §5):
/// a THIN layer over Avalonia's <see cref="IStorageProvider"/>, reached through
/// <see cref="TopLevel.GetTopLevel(Avalonia.Visual)"/> on the WORKSPACE window —
/// structurally identical to <c>StorageProviderRulesetFileDialogs.PickSavePathAsync</c>,
/// but mapping each <see cref="ExportKind"/> to its own picker options instead of the
/// jsonc-hardcoded ruleset ones. ALL serialize+write LOGIC stays in
/// <c>WorkspaceViewModel</c> behind the seam — this class only opens the OS picker and
/// returns the chosen path, so it is the untestable <c>[I]</c> layer; the VM is driven by a
/// fake under Avalonia.Headless. Read-only toward AD (it only touches the local file the
/// user picks; never the directory).
/// </summary>
public sealed class StorageProviderExportFileDialogs : IExportFileDialogs
{
    private static readonly FilePickerFileType CsvFileType = new("CSV files")
    {
        Patterns = ["*.csv"],
    };

    private static readonly FilePickerFileType HtmlFileType = new("HTML files")
    {
        Patterns = ["*.html"],
    };

    private static readonly FilePickerFileType PngFileType = new("PNG images")
    {
        Patterns = ["*.png"],
    };

    private static readonly FilePickerFileType Ps1FileType = new("PowerShell scripts")
    {
        Patterns = ["*.ps1"],
    };

    private readonly TopLevel _topLevel;

    /// <summary>Wraps the picker of <paramref name="topLevel"/> — the workspace window's
    /// own <see cref="TopLevel"/> (<c>TopLevel.GetTopLevel(window)</c>).</summary>
    public StorageProviderExportFileDialogs(TopLevel topLevel) => _topLevel = topLevel;

    /// <inheritdoc/>
    public async Task<string?> PickSavePathAsync(ExportKind kind, CancellationToken ct = default)
    {
        var (title, defaultExtension, fileType) = kind switch
        {
            ExportKind.Csv => ("Export violation report (CSV)", "csv", CsvFileType),
            ExportKind.Html => ("Export violation report (HTML)", "html", HtmlFileType),
            ExportKind.Png => ("Export graph image (PNG)", "png", PngFileType),
            ExportKind.DiffCsv => ("Export gap diff (CSV)", "csv", CsvFileType),
            ExportKind.DiffHtml => ("Export gap diff (HTML)", "html", HtmlFileType),
            _ => ("Export plan as a PowerShell script", "ps1", Ps1FileType),
        };

        var file = await _topLevel.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = title,
            DefaultExtension = defaultExtension,
            FileTypeChoices = [fileType],
            ShowOverwritePrompt = true,
        }).ConfigureAwait(true);

        return file?.TryGetLocalPath();
    }
}
