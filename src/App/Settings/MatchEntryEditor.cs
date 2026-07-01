using CommunityToolkit.Mvvm.ComponentModel;
using GroupWeaver.Core.Rules;

namespace GroupWeaver.App.Settings;

/// <summary>Which side of the dn/name XOR a <see cref="MatchEntryEditor"/> edits
/// (AP 3.3): a single <see cref="MatchEntryEditor.Value"/> TextBox feeds either
/// <see cref="MatchEntry.Dn"/> or <see cref="MatchEntry.Name"/>, never both.</summary>
public enum EntryMode
{
    /// <summary>The value is a DN glob (<see cref="MatchEntry.Dn"/>).</summary>
    Dn,

    /// <summary>The value is a name glob (<see cref="MatchEntry.Name"/>).</summary>
    Name,
}

/// <summary>
/// Editable mirror of one immutable <see cref="MatchEntry"/> (AP 3.3 / ADR-011 §2):
/// an ignore entry or a per-rule exception. Holds the dn/name XOR as a
/// <see cref="Mode"/> toggle over a single <see cref="Value"/> (so the UI cannot
/// set both sides), the free-form <see cref="Note"/> (data — round-trips verbatim,
/// rendered as plain text per #45), and the <see cref="Endpoint"/>.
///
/// <para><see cref="EndpointEditable"/> is true ONLY for nesting exceptions — the
/// one place a non-<see cref="MatchEndpoint.Any"/> endpoint is legal. On
/// non-nesting lists it is false and <see cref="Build"/> forces
/// <see cref="MatchEndpoint.Any"/> (which serializes to null, never <c>"any"</c>),
/// so a stray endpoint can never leak into a naming/circular/empty-group exception.</para>
/// </summary>
public sealed partial class MatchEntryEditor : ObservableObject
{
    /// <summary>Whether <see cref="Value"/> is a dn glob or a name glob.</summary>
    [ObservableProperty]
    private EntryMode _mode;

    /// <summary>The single glob value, interpreted per <see cref="Mode"/>.</summary>
    [ObservableProperty]
    private string _value = string.Empty;

    /// <summary>Free-form remark; data, survives the round-trip verbatim (#45).</summary>
    [ObservableProperty]
    private string? _note;

    /// <summary>The edge-endpoint narrowing; meaningful only when
    /// <see cref="EndpointEditable"/> is true (nesting exceptions).</summary>
    [ObservableProperty]
    private MatchEndpoint _endpoint;

    /// <summary>Whether <see cref="Endpoint"/> may be edited — true only for
    /// nesting exceptions; false forces <see cref="MatchEndpoint.Any"/> on build.</summary>
    public bool EndpointEditable { get; init; }

    /// <summary>UI-ONLY (Slice B) test candidate for the live glob-match preview beside the row:
    /// a DN or name the user types to sanity-check <see cref="Value"/>. NEVER serialized —
    /// <see cref="Build"/> does not read it — so it is scratch state, not ruleset data.</summary>
    [ObservableProperty]
    private string _previewCandidate = string.Empty;

    /// <summary>True when the non-empty <see cref="PreviewCandidate"/> matches the current
    /// <see cref="Value"/> glob via the engine's own <see cref="GlobPreview.IsMatch"/> (the same
    /// compiled, memoized matcher the live rules use — never a parallel implementation). Empty
    /// candidate ⇒ false (the chip is hidden). Re-raised whenever <see cref="Value"/> or
    /// <see cref="PreviewCandidate"/> changes.</summary>
    public bool PreviewMatch =>
        !string.IsNullOrEmpty(PreviewCandidate) && GlobPreview.IsMatch(Value, PreviewCandidate);

    partial void OnValueChanged(string value) => OnPropertyChanged(nameof(PreviewMatch));

    partial void OnPreviewCandidateChanged(string value) => OnPropertyChanged(nameof(PreviewMatch));

    /// <summary>Loads an editor from <paramref name="entry"/>;
    /// <paramref name="endpointEditable"/> reflects whether the owning list is the
    /// nesting exception list.</summary>
    public static MatchEntryEditor LoadFrom(MatchEntry entry, bool endpointEditable) => new()
    {
        EndpointEditable = endpointEditable,
        Mode = entry.Dn is not null ? EntryMode.Dn : EntryMode.Name,
        Value = (entry.Dn ?? entry.Name) ?? string.Empty,
        Note = entry.Note,
        Endpoint = entry.Endpoint,
    };

    /// <summary>Projects the editor back to an immutable <see cref="MatchEntry"/>:
    /// exactly one of Dn/Name set per <see cref="Mode"/>; the endpoint forced to
    /// <see cref="MatchEndpoint.Any"/> unless <see cref="EndpointEditable"/>.</summary>
    public MatchEntry Build() => new()
    {
        Dn = Mode == EntryMode.Dn ? Value : null,
        Name = Mode == EntryMode.Name ? Value : null,
        Note = Note,
        Endpoint = EndpointEditable ? Endpoint : MatchEndpoint.Any,
    };
}
