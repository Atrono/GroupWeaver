using Avalonia.Data.Converters;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using GroupWeaver.App.Settings;
using GroupWeaver.Core.Rules;

namespace GroupWeaver.App.Views;

/// <summary>
/// Compiled-bindings-safe converters rendering a nesting-matrix
/// <see cref="CellChoice"/> as a compact colored chip (AP 3.3 / S6, ADR-011 §2):
/// <see cref="CellChoice.Allow"/> → a green ✓, <see cref="CellChoice.Deny"/> → a
/// neutral ✗ (denied at the rule severity, no override), and
/// <see cref="CellChoice.Error"/>/<see cref="CellChoice.Warning"/>/<see cref="CellChoice.Info"/>
/// → the SeverityConverters E/W/i letter in the pinned ADR-010 palette
/// (#D13438 / #F7A30B / #4FA3E3) — so the deny-cell severity override reads in
/// LOCK-STEP with the sidebar glyphs and the matrix-tab cell chips share that one
/// palette. The Allow green (#107C10) is the AdObjectKindConverters group-green, so
/// the matrix legend stays inside the app's existing color vocabulary.
/// </summary>
public static class CellChoiceConverters
{
    /// <summary>Choice → its chip glyph (✓ allow, ✗ deny, E/W/i for a severity override).</summary>
    public static readonly IValueConverter ToGlyph =
        new FuncValueConverter<CellChoice, string>(GlyphFor);

    /// <summary>Choice → its chip color (green allow, neutral deny, ADR-010 palette override).</summary>
    public static readonly IValueConverter ToBrush =
        new FuncValueConverter<CellChoice, IBrush>(BrushFor);

    private static string GlyphFor(CellChoice choice) => choice switch
    {
        CellChoice.Allow => "✓",
        CellChoice.Deny => "✗",
        CellChoice.Error => "E",
        CellChoice.Warning => "W",
        _ => "i",
    };

    private static IBrush BrushFor(CellChoice choice) => choice switch
    {
        CellChoice.Allow => AllowBrush,
        CellChoice.Deny => DenyBrush,
        CellChoice.Error => SeverityBrush(RuleSeverity.Error),
        CellChoice.Warning => SeverityBrush(RuleSeverity.Warning),
        _ => SeverityBrush(RuleSeverity.Info),
    };

    /// <summary>The deny-override colors come from the ONE SeverityConverters palette
    /// (never a second hex) so the matrix chips can never drift off the sidebar glyphs.</summary>
    private static IBrush SeverityBrush(RuleSeverity severity) =>
        (IBrush)SeverityConverters.ToBrush.Convert(
            severity, typeof(IBrush), null, System.Globalization.CultureInfo.InvariantCulture)!;

    private static readonly ImmutableSolidColorBrush AllowBrush = new(Color.Parse("#107C10"));
    private static readonly ImmutableSolidColorBrush DenyBrush = new(Color.Parse("#757575"));
}
