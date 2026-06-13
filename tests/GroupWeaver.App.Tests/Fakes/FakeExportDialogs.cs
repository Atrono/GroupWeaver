using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using GroupWeaver.App.Export;

namespace GroupWeaver.App.Tests.Fakes;

/// <summary>
/// Headless fake of the AP 4.1 export save-dialog seam (<see cref="IExportFileDialogs"/>,
/// ADR-013 §5) — the same shape as the AP 3.3 <c>FakeDialogs</c> in
/// <c>tests/GroupWeaver.App.Tests/Settings/SettingsValidationTests.cs</c>: the real picker
/// (Avalonia <c>StorageProvider</c> via <c>TopLevel</c>) is the headless-untestable
/// <c>[I]</c> layer, so the VM's serialize+write LOGIC is driven here. A
/// <see cref="SavePath"/> is initialised PER <see cref="ExportKind"/>: each command picks
/// the path registered for the kind it asks for. A <c>null</c> path models a CANCELLED
/// pick (the user dismissed the save dialog) — the command must then be a no-op. Every
/// <see cref="PickSavePathAsync"/> call is recorded so the read-only invariant
/// (written path == dialog-returned path, AD never touched) can be pinned.
/// </summary>
internal sealed class FakeExportDialogs : IExportFileDialogs
{
    private readonly Dictionary<ExportKind, string?> _paths = new();

    /// <summary>Every <see cref="ExportKind"/> the VM asked a save path for, in call order.</summary>
    public List<ExportKind> RequestedKinds { get; } = [];

    /// <summary>Registers the save path the picker returns for <paramref name="kind"/>;
    /// pass <c>null</c> to model a cancelled pick.</summary>
    public FakeExportDialogs SavePathFor(ExportKind kind, string? path)
    {
        _paths[kind] = path;
        return this;
    }

    public Task<string?> PickSavePathAsync(ExportKind kind, CancellationToken ct = default)
    {
        RequestedKinds.Add(kind);
        return Task.FromResult(_paths.TryGetValue(kind, out var path) ? path : null);
    }
}
