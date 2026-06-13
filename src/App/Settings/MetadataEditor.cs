using CommunityToolkit.Mvvm.ComponentModel;
using GroupWeaver.Core.Rules;

namespace GroupWeaver.App.Settings;

/// <summary>
/// Editable mirror of a ruleset's top-level metadata (AP 3.3 / ADR-011 §2):
/// the required <see cref="Name"/> and the optional <see cref="Description"/> /
/// <see cref="Author"/>. <see cref="Ruleset.SchemaVersion"/> is never edited here
/// (always 1; <c>BuildRuleset</c> pins it), so it has no mirror field.
/// </summary>
public sealed partial class MetadataEditor : ObservableObject
{
    /// <summary>The ruleset name (required; the loader rejects a null name).</summary>
    [ObservableProperty]
    private string _name = string.Empty;

    /// <summary>Optional longer description; null/empty omitted on serialize.</summary>
    [ObservableProperty]
    private string? _description;

    /// <summary>Optional author attribution.</summary>
    [ObservableProperty]
    private string? _author;

    /// <summary>Seeds the editor from <paramref name="ruleset"/>.</summary>
    public static MetadataEditor LoadFrom(Ruleset ruleset) => new()
    {
        Name = ruleset.Name,
        Description = ruleset.Description,
        Author = ruleset.Author,
    };
}
