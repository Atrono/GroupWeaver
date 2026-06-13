using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using GroupWeaver.Core.Rules;

namespace GroupWeaver.App.Settings;

/// <summary>
/// Root of the AP 3.3 editable mirror tree (ADR-011 §2): the immutable
/// <see cref="Ruleset"/> records (<c>required</c>/<c>init</c>, un-bindable) are
/// mirrored here into bindable <see cref="ObservableObject"/> editors via
/// <see cref="LoadFrom"/>, edited, then projected back to an immutable
/// <see cref="Ruleset"/> via <see cref="BuildRuleset"/>. The single
/// save/import/apply validation gate is <see cref="RulesetLoader.Load"/> (wired in
/// S3) — this slice is the faithful identity over the model that makes the gate's
/// re-parse safe.
///
/// <para><b>The byte fixed point</b> (the safety contract):
/// <c>Serialize(BuildRuleset(LoadFrom(LoadDefault())))</c> is byte-equal to
/// <c>Serialize(LoadDefault())</c>. The matrix mirror preserves source-cell
/// PRESENCE and canonical key order; the <c>deny</c>(false,null) vs
/// <c>error</c>(false,Error) token distinction survives; dn/name XOR survives;
/// endpoints survive only on nesting exceptions; circular/empty <c>RuleId</c>s are
/// reconstructed from <see cref="RuleIds"/>; <see cref="Ruleset.SchemaVersion"/>
/// stays 1.</para>
/// </summary>
public sealed partial class SettingsViewModel : ObservableObject
{
    private SettingsViewModel(
        MetadataEditor metadata,
        NestingEditor nesting,
        SimpleRuleEditor circular,
        SimpleRuleEditor emptyGroup)
    {
        Metadata = metadata;
        Nesting = nesting;
        Circular = circular;
        EmptyGroup = emptyGroup;
    }

    /// <summary>Name/Description/Author.</summary>
    public MetadataEditor Metadata { get; }

    /// <summary>The nesting matrix editor (rule-level flags + 3×6 cell grid).</summary>
    public NestingEditor Nesting { get; }

    /// <summary>Naming rules in file order (a flat list keyed on id).</summary>
    public ObservableCollection<NamingRuleEditor> Naming { get; } = [];

    /// <summary>The circular-membership rule.</summary>
    public SimpleRuleEditor Circular { get; }

    /// <summary>The empty-group rule.</summary>
    public SimpleRuleEditor EmptyGroup { get; }

    /// <summary>The global ignore list (endpoint hidden).</summary>
    public ObservableCollection<MatchEntryEditor> Ignore { get; } = [];

    /// <summary>The Rules-tab master grid: nesting, each naming rule (file order),
    /// circular, empty-group — each a 2-way handle into the section editors.</summary>
    public ObservableCollection<RuleRowEditor> Rules { get; } = [];

    /// <summary>Mirrors <paramref name="ruleset"/> into a fresh editable tree.</summary>
    public static SettingsViewModel LoadFrom(Ruleset ruleset)
    {
        var vm = new SettingsViewModel(
            MetadataEditor.LoadFrom(ruleset),
            NestingEditor.LoadFrom(ruleset.Nesting),
            SimpleRuleEditor.LoadFrom(ruleset.Circular),
            SimpleRuleEditor.LoadFrom(ruleset.EmptyGroup));

        foreach (var rule in ruleset.Naming)
        {
            vm.Naming.Add(NamingRuleEditor.LoadFrom(rule));
        }

        foreach (var entry in ruleset.Ignore)
        {
            vm.Ignore.Add(MatchEntryEditor.LoadFrom(entry, endpointEditable: false));
        }

        vm.Rules.Add(RuleRowEditor.ForNesting(vm.Nesting));
        foreach (var naming in vm.Naming)
        {
            vm.Rules.Add(RuleRowEditor.ForNaming(naming));
        }

        vm.Rules.Add(RuleRowEditor.ForSimple(RuleIds.Circular, "Circular nesting", vm.Circular));
        vm.Rules.Add(RuleRowEditor.ForSimple(RuleIds.EmptyGroup, "Empty groups", vm.EmptyGroup));

        return vm;
    }

    /// <summary>Projects the mirror tree back to an immutable <see cref="Ruleset"/>.
    /// <see cref="Ruleset.SchemaVersion"/> is pinned to 1; the matrix is emitted
    /// sparse (present cells only); circular/empty <c>RuleId</c>s come from
    /// <see cref="RuleIds"/>. The result is what the save/import/apply gate re-parses.</summary>
    public Ruleset BuildRuleset() => new()
    {
        SchemaVersion = 1,
        Name = Metadata.Name,
        Description = Metadata.Description,
        Author = Metadata.Author,
        Nesting = Nesting.Build(),
        Naming = Naming.Select(r => r.Build()).ToList(),
        Circular = Circular.Build(),
        EmptyGroup = EmptyGroup.Build(),
        Ignore = Ignore.Select(e => e.Build()).ToList(),
    };
}
