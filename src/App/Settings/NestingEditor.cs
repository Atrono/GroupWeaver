using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GroupWeaver.Core.Model;
using GroupWeaver.Core.Rules;

namespace GroupWeaver.App.Settings;

/// <summary>
/// Editable mirror of the immutable <see cref="NestingRule"/> (AP 3.3 / ADR-011 §2):
/// the rule-level <see cref="Enabled"/>/<see cref="Severity"/>/<see cref="Unlisted"/>
/// plus a fixed 3×6 grid of <see cref="NestingCellEditor"/> — rows (parent) ∈
/// {GlobalGroup, DomainLocalGroup, UniversalGroup}, columns (member) ∈ {User,
/// Computer, GlobalGroup, DomainLocalGroup, UniversalGroup, External}. The grid is
/// always dense for editing addressability (<see cref="Cell"/>), but each cell
/// carries <see cref="NestingCellEditor.Present"/> so <see cref="BuildMatrix"/>
/// emits ONLY the source's present cells in the canonical row/column order — the
/// sparse-matrix byte fixed point (the default's matrix shape is the tripwire).
/// </summary>
public sealed partial class NestingEditor : ObservableObject
{
    /// <summary>Parent (row) kinds in canonical grid/serialization order.</summary>
    public static readonly IReadOnlyList<AdObjectKind> ParentKinds =
    [
        AdObjectKind.GlobalGroup,
        AdObjectKind.DomainLocalGroup,
        AdObjectKind.UniversalGroup,
    ];

    /// <summary>Member (column) kinds in canonical grid/serialization order
    /// (External included, OrganizationalUnit excluded — the loader rejects it).</summary>
    public static readonly IReadOnlyList<AdObjectKind> MemberKinds =
    [
        AdObjectKind.User,
        AdObjectKind.Computer,
        AdObjectKind.GlobalGroup,
        AdObjectKind.DomainLocalGroup,
        AdObjectKind.UniversalGroup,
        AdObjectKind.External,
    ];

    private readonly Dictionary<(AdObjectKind Parent, AdObjectKind Member), NestingCellEditor> _cells = new();

    private NestingEditor()
    {
        foreach (var parent in ParentKinds)
        {
            foreach (var member in MemberKinds)
            {
                var cell = new NestingCellEditor { Parent = parent, Member = member };
                _cells[(parent, member)] = cell;
                Cells.Add(cell);
            }
        }
    }

    /// <summary>Whether the rule produces findings at all.</summary>
    [ObservableProperty]
    private bool _enabled;

    /// <summary>Severity of every deny cell without its own override.</summary>
    [ObservableProperty]
    private RuleSeverity _severity;

    /// <summary>The fallback verdict for any row/column absent from the source
    /// matrix (the rule's <c>unlisted</c> cell; default Deny, fails closed).</summary>
    [ObservableProperty]
    private CellChoice _unlisted;

    /// <summary>The 18 cells in canonical order — the flat handle a 3×6 grid binds to.</summary>
    public ObservableCollection<NestingCellEditor> Cells { get; } = [];

    /// <summary>Per-rule exceptions; the only list whose endpoint is editable.</summary>
    public ObservableCollection<MatchEntryEditor> Exceptions { get; } = [];

    /// <summary>The cell editor for <paramref name="parent"/>←<paramref name="member"/>.</summary>
    public NestingCellEditor Cell(AdObjectKind parent, AdObjectKind member) => _cells[(parent, member)];

    /// <summary>Appends a fresh dn-mode exception. Nesting is the one exception list whose
    /// endpoint is editable (Any/Parent/Member), so the new row is endpoint-editable.</summary>
    [RelayCommand]
    private void AddException() =>
        Exceptions.Add(new MatchEntryEditor { Mode = EntryMode.Dn, EndpointEditable = true });

    /// <summary>Removes <paramref name="entry"/> from the exception list.</summary>
    [RelayCommand]
    private void RemoveException(MatchEntryEditor entry) => Exceptions.Remove(entry);

    /// <summary>Seeds the editor from <paramref name="rule"/>: every grid cell adopts
    /// its effective verdict, but only cells that were genuinely present in the source
    /// matrix are marked <see cref="NestingCellEditor.Present"/> (sparse preservation).</summary>
    public static NestingEditor LoadFrom(NestingRule rule)
    {
        var editor = new NestingEditor
        {
            Enabled = rule.Enabled,
            Severity = rule.Severity,
            Unlisted = CellChoiceMapping.FromCell(rule.Unlisted),
        };

        foreach (var parent in ParentKinds)
        {
            rule.Matrix.TryGetValue(parent, out var row);
            foreach (var member in MemberKinds)
            {
                bool present = row is not null && row.TryGetValue(member, out _);
                editor.Cell(parent, member).Load(rule.Cell(parent, member), present);
            }
        }

        foreach (var exception in rule.Exceptions)
        {
            editor.Exceptions.Add(MatchEntryEditor.LoadFrom(exception, endpointEditable: true));
        }

        return editor;
    }

    /// <summary>Projects the editor back to an immutable <see cref="NestingRule"/>.</summary>
    public NestingRule Build() => new()
    {
        Enabled = Enabled,
        Severity = Severity,
        Unlisted = CellChoiceMapping.ToCell(Unlisted),
        Matrix = BuildMatrix(),
        Exceptions = Exceptions.Select(e => e.Build()).ToList(),
    };

    /// <summary>Rebuilds the sparse matrix in canonical row/column order, emitting
    /// only <see cref="NestingCellEditor.Present"/> cells and only rows that have at
    /// least one present cell — preserving the source's sparse shape and key order
    /// byte-for-byte through the serializer.</summary>
    private IReadOnlyDictionary<AdObjectKind, IReadOnlyDictionary<AdObjectKind, NestingCell>> BuildMatrix()
    {
        var matrix = new Dictionary<AdObjectKind, IReadOnlyDictionary<AdObjectKind, NestingCell>>();
        foreach (var parent in ParentKinds)
        {
            var row = new Dictionary<AdObjectKind, NestingCell>();
            foreach (var member in MemberKinds)
            {
                var cell = Cell(parent, member);
                if (cell.Present)
                {
                    row[member] = CellChoiceMapping.ToCell(cell.Choice);
                }
            }

            if (row.Count > 0)
            {
                matrix[parent] = row;
            }
        }

        return matrix;
    }
}
