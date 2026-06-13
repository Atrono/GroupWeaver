using CommunityToolkit.Mvvm.ComponentModel;
using GroupWeaver.Core.Model;
using GroupWeaver.Core.Rules;

namespace GroupWeaver.App.Settings;

/// <summary>
/// Editable mirror of one nesting-matrix cell (AP 3.3 / ADR-011 §2, the highest
/// round-trip risk). Carries the editable <see cref="Choice"/> plus the
/// sparse-preservation <see cref="Present"/> flag: only cells that were present in
/// the source <c>NestingRule.Matrix</c> load with <see cref="Present"/> = true;
/// absent cells render the effective fallback value (<c>Unlisted</c>) with
/// <see cref="Present"/> = false. Editing the <see cref="Choice"/> sets
/// <see cref="Present"/> = true, and <c>NestingEditor.BuildMatrix</c> emits ONLY
/// present cells — so the default's sparse matrix shape round-trips byte-for-byte
/// and the editor never dense-widens <c>$.nesting.matrix</c>.
/// </summary>
public sealed partial class NestingCellEditor : ObservableObject
{
    private bool _loading;

    /// <summary>The fixed parent (containing group) kind of this cell's row.</summary>
    public required AdObjectKind Parent { get; init; }

    /// <summary>The fixed member kind of this cell's column.</summary>
    public required AdObjectKind Member { get; init; }

    /// <summary>The editor verdict; setting it marks the cell <see cref="Present"/>.</summary>
    [ObservableProperty]
    private CellChoice _choice;

    /// <summary>True when the source matrix carried this exact cell (or the user has
    /// since edited it) — the byte-fixed-point presence flag. Absent cells stay
    /// false and are NOT serialized.</summary>
    [ObservableProperty]
    private bool _present;

    /// <summary>Seeds the cell from the source matrix: a present cell adopts its
    /// <see cref="CellChoice"/> with <see cref="Present"/> = true; an absent cell
    /// shows the effective <paramref name="effective"/> value with
    /// <see cref="Present"/> = false (no edit, no widening).</summary>
    internal void Load(NestingCell effective, bool present)
    {
        _loading = true;
        Choice = CellChoiceMapping.FromCell(effective);
        Present = present;
        _loading = false;
    }

    /// <summary>An edit to the verdict makes the cell present (so it is serialized),
    /// unless we are loading from the source (which sets presence explicitly).</summary>
    partial void OnChoiceChanged(CellChoice value)
    {
        if (!_loading)
        {
            Present = true;
        }
    }
}
