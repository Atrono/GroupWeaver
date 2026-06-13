using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GroupWeaver.Core.Rules;

namespace GroupWeaver.App.Settings;

/// <summary>
/// Editable mirror of one immutable <see cref="SimpleRule"/> (AP 3.3 / ADR-011 §2):
/// the circular-membership and empty-group rules. Only <see cref="Enabled"/>,
/// <see cref="Severity"/> and the <see cref="Exceptions"/> are user-editable; the
/// fixed <c>RuleId</c> is reconstructed from <see cref="RuleIds"/> on build (never
/// edited, never serialized — the serializer derives it from schema position).
/// Exceptions hide the endpoint (not endpoint-bearing; forced to Any on build).
/// </summary>
public sealed partial class SimpleRuleEditor : ObservableObject
{
    private readonly string _ruleId;

    private SimpleRuleEditor(string ruleId) => _ruleId = ruleId;

    /// <summary>Whether the rule produces findings at all.</summary>
    [ObservableProperty]
    private bool _enabled;

    /// <summary>Severity of every violation.</summary>
    [ObservableProperty]
    private RuleSeverity _severity;

    /// <summary>Per-rule exceptions (endpoint hidden).</summary>
    public ObservableCollection<MatchEntryEditor> Exceptions { get; } = [];

    /// <summary>Appends a fresh dn-mode exception (endpoint hidden — circular/empty-group
    /// exceptions are not endpoint-bearing, forced to Any on build).</summary>
    [RelayCommand]
    private void AddException() => Exceptions.Add(new MatchEntryEditor { Mode = EntryMode.Dn });

    /// <summary>Removes <paramref name="entry"/> from the exception list.</summary>
    [RelayCommand]
    private void RemoveException(MatchEntryEditor entry) => Exceptions.Remove(entry);

    /// <summary>Seeds the editor from <paramref name="rule"/>, keeping its fixed
    /// <see cref="SimpleRule.RuleId"/> so it is reconstructed exactly on build.</summary>
    public static SimpleRuleEditor LoadFrom(SimpleRule rule)
    {
        var editor = new SimpleRuleEditor(rule.RuleId)
        {
            Enabled = rule.Enabled,
            Severity = rule.Severity,
        };

        foreach (var exception in rule.Exceptions)
        {
            editor.Exceptions.Add(MatchEntryEditor.LoadFrom(exception, endpointEditable: false));
        }

        return editor;
    }

    /// <summary>Projects the editor back to an immutable <see cref="SimpleRule"/>,
    /// restoring the fixed <c>RuleId</c>.</summary>
    public SimpleRule Build() => new()
    {
        RuleId = _ruleId,
        Enabled = Enabled,
        Severity = Severity,
        Exceptions = Exceptions.Select(e => e.Build()).ToList(),
    };
}
