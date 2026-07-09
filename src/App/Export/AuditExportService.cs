using System.Text;

using GroupWeaver.Core.Export;
using GroupWeaver.Core.Rules;

namespace GroupWeaver.App.Export;

/// <summary>
/// The audit-screen report-export mechanics (WP2 / ADR-013 §2/§5/§6), extracted out of
/// <see cref="ViewModels.AuditViewModel"/> so the VM stays focused on VM-owned identity state
/// (<see cref="ViewModels.AuditViewModel.ResolveName"/> / <c>BuildReportHeader</c> stay on the VM — they
/// read <c>_snapshot</c>/<c>RootDn</c>/ruleset/connection-summary state this service never sees).
///
/// <para>Owns the export save-picker seam (<see cref="UseDialogs"/>) and the cancel-on-teardown
/// <see cref="CancellationTokenSource"/> that were previously inline on the VM — same dead-until-armed,
/// re-guard-after-await, cancel-on-Dispose discipline (a save-picker open at <see cref="Dispose"/> can
/// never write after dispose). Read-only toward AD: the only write target is the caller-picked local
/// file; the exporter output itself is the pure-Core <see cref="ViolationReportExporter"/>.</para>
/// </summary>
public sealed class AuditExportService : IDisposable
{
    private readonly CancellationTokenSource _cts = new();

    private IExportFileDialogs? _dialogs;

    /// <summary>True once <see cref="Dispose"/> ran — idempotent teardown observability, mirrors
    /// <see cref="ViewModels.AuditViewModel.IsDisposed"/>.</summary>
    public bool IsDisposed { get; private set; }

    /// <summary>True once <see cref="UseDialogs"/> installed the export save-picker seam — the export
    /// commands' gate (a headless / un-wired audit never opens a picker).</summary>
    public bool IsArmed => _dialogs is not null;

    /// <summary>Installs the export save-picker seam (mirrors the former
    /// <see cref="ViewModels.AuditViewModel.UseExportFileDialogs"/> body). Idempotent — the last writer
    /// wins.</summary>
    public void UseDialogs(IExportFileDialogs dialogs) => _dialogs = dialogs;

    /// <summary>
    /// Exports <paramref name="report"/> as RFC-4180 CSV to a user-picked path. Re-guards after the
    /// await (a stale-armed Execute or a pick resolving after <see cref="Dispose"/> is a no-op), picks
    /// via the seam, and on a non-null pick writes the pure-Core <see cref="ViolationReportExporter.ToCsv"/>
    /// output (UTF-8, no BOM) to ONLY that path.
    /// </summary>
    public async Task ExportCsvAsync(RuleReport report, ViolationReportExporter.ResolveName resolveName)
    {
        if (IsDisposed || _dialogs is null)
        {
            return;
        }

        var path = await _dialogs.PickSavePathAsync(ExportKind.Csv, _cts.Token);
        if (path is null || IsDisposed)
        {
            return;
        }

        var csv = ViolationReportExporter.ToCsv(report, resolveName);
        await WriteUtf8Async(path, csv, _cts.Token);
    }

    /// <summary>
    /// Exports <paramref name="report"/> as a self-contained HTML file to a user-picked path. Same gate,
    /// re-guard, pick and write-once discipline as <see cref="ExportCsvAsync"/>.
    /// </summary>
    public async Task ExportHtmlAsync(RuleReport report, ViolationReportExporter.ResolveName resolveName, ReportHeader header)
    {
        if (IsDisposed || _dialogs is null)
        {
            return;
        }

        var path = await _dialogs.PickSavePathAsync(ExportKind.Html, _cts.Token);
        if (path is null || IsDisposed)
        {
            return;
        }

        var html = ViolationReportExporter.ToHtml(report, resolveName, header);
        await WriteUtf8Async(path, html, _cts.Token);
    }

    /// <summary>Flips <see cref="IsDisposed"/> and cancels+disposes the internal
    /// <see cref="CancellationTokenSource"/> so an export save-picker/write still in flight at teardown
    /// is cancelled and can never write after dispose. Idempotent.</summary>
    public void Dispose()
    {
        if (IsDisposed)
        {
            return;
        }

        IsDisposed = true;
        _cts.Cancel();
        _cts.Dispose();
    }

    /// <summary>Writes <paramref name="content"/> to <paramref name="path"/> as UTF-8 WITHOUT a BOM —
    /// the exact bytes the CSV/HTML exporter pinned tests expect.</summary>
    private static Task WriteUtf8Async(string path, string content, CancellationToken cancellationToken) =>
        File.WriteAllTextAsync(path, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), cancellationToken);
}
