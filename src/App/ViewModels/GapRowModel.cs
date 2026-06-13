using CommunityToolkit.Mvvm.ComponentModel;

using GroupWeaver.Core.Diff;

namespace GroupWeaver.App.ViewModels;

/// <summary>
/// One row of the ADR-015 (#66) Gap sidebar: the projection of a <see cref="GapFinding"/> the
/// GapView binds (the native chrome is slice 9). Mirrors <see cref="ViolationRowModel"/>, but
/// carries the gap <see cref="Kind"/> instead of a rule severity — the Gap view shows the
/// Ist-vs-Plan DIFF, never rule severity. <see cref="Kind"/> drives the row glyph/color, the
/// presentation <see cref="Message"/> and resolved <see cref="SubjectName"/> (falls back to the DN
/// when the union cannot resolve a name) are display-only, and <see cref="PrimaryDn"/> is the jump
/// anchor (<c>Dns[0]</c>) + the selection-sync match key (compared under <c>Dn.Comparer</c>).
///
/// The four projection fields are immutable; only <see cref="IsActive"/> mutates — the
/// selection-sync highlight the VM flips in <c>OnSelectedDnChanged</c> for every row whose
/// <see cref="PrimaryDn"/> matches the current selection (an <see cref="ObservableProperty"/> so
/// the highlight repaints).
/// </summary>
public sealed partial class GapRowModel : ObservableObject
{
    public GapRowModel(GapKind kind, string message, string subjectName, string primaryDn)
    {
        Kind = kind;
        Message = message;
        SubjectName = subjectName;
        PrimaryDn = primaryDn;
    }

    /// <summary>Which gap this row reports (NodeAdded/NodeRemoved/EdgeAdded/EdgeRemoved/
    /// UnverifiableArea) — drives the row glyph/color in the GapView. No severity here.</summary>
    public GapKind Kind { get; }

    /// <summary>The finding's presentation message (deterministic, culture-invariant).</summary>
    public string Message { get; }

    /// <summary>The anchor object's display name, resolved against the render union (Ist-wins);
    /// falls back to the raw DN when the union cannot resolve it.</summary>
    public string SubjectName { get; }

    /// <summary>The jump-to-node anchor (<c>Dns[0]</c>) — the command parameter and the
    /// selection-sync match key (compared under <c>Dn.Comparer</c>).</summary>
    public string PrimaryDn { get; }

    /// <summary>Selection-sync highlight: <c>true</c> while this row's <see cref="PrimaryDn"/>
    /// matches the current graph selection. The VM flips it in <c>OnSelectedDnChanged</c>.</summary>
    [ObservableProperty]
    private bool _isActive;
}
