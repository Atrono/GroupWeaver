using Avalonia.Data.Converters;
using Avalonia.Media;
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

    /// <summary>
    /// Choice → the per-chip TEXT brush (ADR-021 / #90, the WCAG 1.4.3 re-tone): Allow → white
    /// and Deny → white (both ≥ 4.5:1 on their own fills — green #107C10 4.43→OK and gray #757575
    /// keep white), while the E/W/i severity overrides DELEGATE to
    /// <see cref="SeverityConverters.ToTextBrush"/> so the amber/light-blue cells get the dark
    /// page-bg ink (white failed 1.4.3 there) — exactly like the sidebar glyphs. Mirrors the
    /// <see cref="ToBrush"/>/<see cref="ToGlyph"/> switch so a cell's fill and its ink share one
    /// source.
    /// </summary>
    public static readonly IValueConverter ToTextBrush =
        new FuncValueConverter<CellChoice, IBrush>(TextBrushFor);

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
        CellChoice.Allow => BrandTokens.Allow,
        CellChoice.Deny => BrandTokens.Deny,
        CellChoice.Error => SeverityBrush(RuleSeverity.Error),
        CellChoice.Warning => SeverityBrush(RuleSeverity.Warning),
        _ => SeverityBrush(RuleSeverity.Info),
    };

    // The on-chip TEXT ink (ADR-021 / #90): Allow/Deny keep white (≥ 4.5:1 on their own fills);
    // the E/W/i severity overrides reuse SeverityConverters.ToTextBrush so amber/light-blue cells
    // get the dark page-bg ink — never a second hardcoded color.
    private static IBrush TextBrushFor(CellChoice choice) => choice switch
    {
        CellChoice.Allow => BrandTokens.OnDarkText,
        CellChoice.Deny => BrandTokens.OnDarkText,
        CellChoice.Error => SeverityTextBrush(RuleSeverity.Error),
        CellChoice.Warning => SeverityTextBrush(RuleSeverity.Warning),
        _ => SeverityTextBrush(RuleSeverity.Info),
    };

    /// <summary>The deny-override colors come from the ONE SeverityConverters palette
    /// (never a second hex) so the matrix chips can never drift off the sidebar glyphs.</summary>
    private static IBrush SeverityBrush(RuleSeverity severity) =>
        (IBrush)SeverityConverters.ToBrush.Convert(
            severity, typeof(IBrush), null, System.Globalization.CultureInfo.InvariantCulture)!;

    /// <summary>The deny-override TEXT ink, also from the ONE SeverityConverters source
    /// (<see cref="SeverityConverters.ToTextBrush"/>) so the amber/light-blue cell ink matches
    /// the sidebar badges and can never drift.</summary>
    private static IBrush SeverityTextBrush(RuleSeverity severity) =>
        (IBrush)SeverityConverters.ToTextBrush.Convert(
            severity, typeof(IBrush), null, System.Globalization.CultureInfo.InvariantCulture)!;
}
