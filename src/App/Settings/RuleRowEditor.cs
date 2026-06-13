using System;
using CommunityToolkit.Mvvm.ComponentModel;
using GroupWeaver.Core.Rules;

namespace GroupWeaver.App.Settings;

/// <summary>
/// One row of the Rules-tab master grid (AP 3.3 / ADR-011 §2; the
/// <c>Ruleset.EnumerateRules</c> shape): a 2-way <see cref="Enabled"/>/
/// <see cref="Severity"/> handle that reads and writes THROUGH the owning section
/// editor (nesting / a naming rule / circular / empty-group). It owns no state of
/// its own — <c>BuildRuleset</c> projects from the section editors, never from
/// these rows — so the master grid and the per-section tabs can never disagree.
/// </summary>
public sealed partial class RuleRowEditor : ObservableObject
{
    private readonly Func<bool> _getEnabled;
    private readonly Action<bool> _setEnabled;
    private readonly Func<RuleSeverity> _getSeverity;
    private readonly Action<RuleSeverity> _setSeverity;

    private RuleRowEditor(
        string id,
        string displayName,
        Func<bool> getEnabled,
        Action<bool> setEnabled,
        Func<RuleSeverity> getSeverity,
        Action<RuleSeverity> setSeverity)
    {
        Id = id;
        DisplayName = displayName;
        _getEnabled = getEnabled;
        _setEnabled = setEnabled;
        _getSeverity = getSeverity;
        _setSeverity = setSeverity;
    }

    /// <summary>The owning rule's id (<see cref="RuleIds"/> or a naming rule id).</summary>
    public string Id { get; }

    /// <summary>The grid label.</summary>
    public string DisplayName { get; }

    /// <summary>Whether the owning rule produces findings — read/written through.</summary>
    public bool Enabled
    {
        get => _getEnabled();
        set
        {
            if (_getEnabled() != value)
            {
                _setEnabled(value);
                OnPropertyChanged();
            }
        }
    }

    /// <summary>The owning rule's severity — read/written through.</summary>
    public RuleSeverity Severity
    {
        get => _getSeverity();
        set
        {
            if (_getSeverity() != value)
            {
                _setSeverity(value);
                OnPropertyChanged();
            }
        }
    }

    /// <summary>A master-grid row for the nesting rule.</summary>
    public static RuleRowEditor ForNesting(NestingEditor nesting) => new(
        RuleIds.Nesting,
        "Nesting matrix",
        () => nesting.Enabled,
        v => nesting.Enabled = v,
        () => nesting.Severity,
        v => nesting.Severity = v);

    /// <summary>A master-grid row for one naming rule.</summary>
    public static RuleRowEditor ForNaming(NamingRuleEditor naming) => new(
        naming.Id,
        $"Naming: {naming.Id}",
        () => naming.Enabled,
        v => naming.Enabled = v,
        () => naming.Severity,
        v => naming.Severity = v);

    /// <summary>A master-grid row for a simple rule (circular / empty-group).</summary>
    public static RuleRowEditor ForSimple(string id, string displayName, SimpleRuleEditor rule) => new(
        id,
        displayName,
        () => rule.Enabled,
        v => rule.Enabled = v,
        () => rule.Severity,
        v => rule.Severity = v);
}
