using System.Threading;
using System.Threading.Tasks;

namespace GroupWeaver.App.Export;

/// <summary>The export artifact a save dialog is being opened for (AP 4.1 / ADR-013 §5):
/// each value maps to its own picker title, default extension and file-type filter.</summary>
public enum ExportKind
{
    /// <summary>Violation report as RFC-4180 CSV (<c>*.csv</c>).</summary>
    Csv,

    /// <summary>Violation report as a self-contained HTML file (<c>*.html</c>).</summary>
    Html,

    /// <summary>Rendered graph image as PNG (<c>*.png</c>).</summary>
    Png,

    /// <summary>Plan Mode proposed structure as an inert PowerShell script (<c>*.ps1</c>).</summary>
    Ps1,

    /// <summary>Gap-mode Plan-vs-Ist diff report as RFC-4180 CSV (<c>*.csv</c>).</summary>
    DiffCsv,

    /// <summary>Gap-mode Plan-vs-Ist diff report as a self-contained HTML file (<c>*.html</c>).</summary>
    DiffHtml,
}

/// <summary>
/// The headless-testable seam over Avalonia's save picker for AP 4.1 export (ADR-013 §5).
/// A new seam — deliberately NOT the jsonc-hardcoded <c>IRulesetFileDialogs</c>, which
/// is wired to the settings window. The production adapter reaches the OS picker through
/// <c>TopLevel.GetTopLevel(workspaceWindow).StorageProvider</c> — the untestable
/// <c>[I]</c> layer; all serialize+write LOGIC stays VM-side behind this seam so it can be
/// driven by a fake under Avalonia.Headless. Read-only toward AD: the only write target is
/// the local file the user picks; the directory is never touched.
/// </summary>
public interface IExportFileDialogs
{
    /// <summary>Prompts for an export destination for the given <paramref name="kind"/> and
    /// returns the chosen path, or <c>null</c> when the user cancels.</summary>
    Task<string?> PickSavePathAsync(ExportKind kind, CancellationToken ct = default);
}
