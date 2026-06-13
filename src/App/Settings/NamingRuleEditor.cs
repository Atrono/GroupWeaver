using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using GroupWeaver.Core.Model;
using GroupWeaver.Core.Rules;

namespace GroupWeaver.App.Settings;

/// <summary>
/// Editable mirror of one immutable <see cref="NamingRule"/> (AP 3.3 / ADR-011 §2).
/// Naming is a flat list keyed on <see cref="Id"/> (multiple rules per kind are
/// legal), so the mirror preserves file order and id identity. Exceptions are
/// <see cref="MatchEntryEditor"/>s with the endpoint hidden — naming exceptions are
/// not endpoint-bearing (<c>EndpointEditable=false</c>, forced to Any on build).
/// </summary>
public sealed partial class NamingRuleEditor : ObservableObject
{
    /// <summary>User-chosen id, unique case-insensitively.</summary>
    [ObservableProperty]
    private string _id = string.Empty;

    /// <summary>Whether the rule produces findings at all.</summary>
    [ObservableProperty]
    private bool _enabled;

    /// <summary>Severity of every violation.</summary>
    [ObservableProperty]
    private RuleSeverity _severity;

    /// <summary>The object kind judged (External is rejected by the loader).</summary>
    [ObservableProperty]
    private AdObjectKind _kind;

    /// <summary>The regex a conforming name must match.</summary>
    [ObservableProperty]
    private string _pattern = string.Empty;

    /// <summary>Human-readable grammar of the convention.</summary>
    [ObservableProperty]
    private string? _description;

    /// <summary>Per-rule exceptions (endpoint hidden).</summary>
    public ObservableCollection<MatchEntryEditor> Exceptions { get; } = [];

    /// <summary>Seeds the editor from <paramref name="rule"/>, preserving its
    /// exception order.</summary>
    public static NamingRuleEditor LoadFrom(NamingRule rule)
    {
        var editor = new NamingRuleEditor
        {
            Id = rule.Id,
            Enabled = rule.Enabled,
            Severity = rule.Severity,
            Kind = rule.Kind,
            Pattern = rule.Pattern,
            Description = rule.Description,
        };

        foreach (var exception in rule.Exceptions)
        {
            editor.Exceptions.Add(MatchEntryEditor.LoadFrom(exception, endpointEditable: false));
        }

        return editor;
    }

    /// <summary>Projects the editor back to an immutable <see cref="NamingRule"/>.</summary>
    public NamingRule Build() => new()
    {
        Id = Id,
        Enabled = Enabled,
        Severity = Severity,
        Kind = Kind,
        Pattern = Pattern,
        Description = Description,
        Exceptions = Exceptions.Select(e => e.Build()).ToList(),
    };
}
