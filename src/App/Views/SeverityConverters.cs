using Avalonia.Data.Converters;
using Avalonia.Media;
using GroupWeaver.App.ViewModels;
using GroupWeaver.Core.Rules;

namespace GroupWeaver.App.Views;

/// <summary>
/// THE App-side severity palette (AP 3.4 S4, ADR-010 §1/§5): the violations sidebar
/// glyphs read these — in lock-step with the graph overlay halos (graph.js
/// <c>node[sev=…]</c> rules) and the verify.mjs <c>SEVERITY</c> tripwire. The hex
/// values are LOAD-BEARING: Error #D13438 / Warning #F7A30B / Info #4FA3E3, the
/// pinned "Final rendering decision" table — they MUST equal the JS map and the
/// stylesheet <c>overlay-color</c> rules (C#↔JS parity is the whole point of
/// pinning them). <see cref="ToGlyph"/> adds the colorblind-redundant letter
/// (E / W / i) the row shows beside the colored square.
///
/// Compiled-bindings-safe; mirrors the AP 2.2 <see cref="AdObjectKindConverters"/>
/// shape (one converter palette reused by the XAML binding seam and the tests).
/// </summary>
public static class SeverityConverters
{
    /// <summary>Severity → its pinned ADR-010 overlay color (the glyph square fill).</summary>
    public static readonly IValueConverter ToBrush =
        new FuncValueConverter<RuleSeverity, IBrush>(BrushFor);

    /// <summary>
    /// Severity → the per-hue ON-BADGE text brush (ADR-021 / #90, the WCAG 1.4.3 re-tone):
    /// Error → white (<see cref="BrandTokens.OnDarkText"/>, 4.93:1 on the red fill ✓), but
    /// Warning → <see cref="BrandTokens.OnLightText"/> (#1b1f27 dark ink, 8.02:1 on amber ✓ —
    /// white was 2.06:1 ✗) and Info → <see cref="BrandTokens.OnLightText"/> (6.04:1 on light
    /// blue ✓ — white was 2.73:1 ✗). The amber/light-blue fills are too light for white text;
    /// only the red keeps white. Mirrors the <see cref="ToBrush"/>/<see cref="ToGlyph"/> shape
    /// so the badge glyph FILL (ToBrush) and its TEXT (this) stay one source of truth.
    /// </summary>
    public static readonly IValueConverter ToTextBrush =
        new FuncValueConverter<RuleSeverity, IBrush>(TextBrushFor);

    /// <summary>Severity → the redundant, colorblind-safe letter (E / W / i).</summary>
    public static readonly IValueConverter ToGlyph =
        new FuncValueConverter<RuleSeverity, string>(GlyphFor);

    /// <summary>WP5c (#154) health-ring fill → its DECORATIVE band-coded <see cref="Color"/> by the
    /// <see cref="AuditSummary.Band"/> string: Excellent/Good → green (<see cref="BrandTokens.NamingOkHex"/>),
    /// Fair → amber (<see cref="BrandTokens.WarningHex"/>), Poor → red (<see cref="BrandTokens.ErrorHex"/>).
    /// COLOR IS NOT THE SOLE CHANNEL (WCAG 1.4.1): the always-present "{Score} / 100" + band text in
    /// the ring carries the meaning; the ring fill is a redundant emphasis cue. Returns a
    /// <see cref="Color"/> (not a brush) — it drives a <c>ConicGradientBrush</c> GradientStop. Reuses
    /// the severity hues so the dashboard reads in the app's existing traffic-light vocabulary.</summary>
    public static readonly IValueConverter BandToRingColor =
        new FuncValueConverter<string, Color>(BandRingColorFor);

    /// <summary>WP4 (#148) audit chip → its FILL: a finding chip uses its class's max-severity
    /// overlay color (in lock-step with the sidebar glyph + graph halo); the green "No findings"
    /// pass chip uses <see cref="BrandTokens.NamingOk"/> — the success green that is deliberately
    /// outside the severity palette (a clean DN is not a finding).</summary>
    public static readonly IValueConverter ChipToBrush =
        new FuncValueConverter<AuditChip, IBrush>(chip =>
            chip is { HasFindings: true } ? BrushFor(chip.Severity) : BrandTokens.NamingOk);

    /// <summary>WP4 (#148) audit chip → its ON-CHIP ink: a finding chip routes through the
    /// per-hue <see cref="ToTextBrush"/> WCAG re-tone (red→white, amber/light-blue→dark ink);
    /// the green pass chip carries DARK ink (<see cref="BrandTokens.OnLightText"/> #1b1f27) —
    /// white on the <see cref="BrandTokens.NamingOk"/> green #2EA043 is only 3.37:1 (FAILS 1.4.3,
    /// the same white-on-light-fill trap ADR-021 §2 fixed for amber/light-blue); dark ink clears
    /// it at 4.89:1.</summary>
    public static readonly IValueConverter ChipToTextBrush =
        new FuncValueConverter<AuditChip, IBrush>(chip =>
            chip is { HasFindings: true } ? TextBrushFor(chip.Severity) : BrandTokens.OnLightText);

    /// <summary>WP4 (#148) audit chip → its label text: a finding chip appends its count
    /// ("Nesting 1"); the pass chip shows just "No findings".</summary>
    public static readonly IValueConverter ChipToLabel =
        new FuncValueConverter<AuditChip, string>(chip =>
            chip is { HasFindings: true } ? $"{chip.Label} {chip.Count}" : chip!.Label);

    private static IBrush BrushFor(RuleSeverity severity) => severity switch
    {
        RuleSeverity.Error => BrandTokens.Error,
        RuleSeverity.Warning => BrandTokens.Warning,
        _ => BrandTokens.Info,
    };

    // The ON-BADGE text ink, per-hue (ADR-021 / #90): red keeps white (4.93:1), amber and
    // light-blue need the dark page-bg ink (white failed 1.4.3 at 2.06 / 2.73).
    private static IBrush TextBrushFor(RuleSeverity severity) => severity switch
    {
        RuleSeverity.Error => BrandTokens.OnDarkText,
        RuleSeverity.Warning => BrandTokens.OnLightText,
        _ => BrandTokens.OnLightText,
    };

    // The health-ring band → its decorative fill hue (WP5c). Excellent/Good are healthy (green),
    // Fair is amber, Poor (and any unknown band string) is red. The ADR-030 (#188) "Action required"
    // band (a live Error, gated regardless of score) reuses the SAME Error/red severity hue — the full
    // red ring at the diluted high fill + the band text + the Critical tile together tell the true
    // story. The band string is the pinned AuditSummary.BandFor output ("Action required"/"Excellent"/
    // "Good"/"Fair"/"Poor"). No new colour token — just a new mapping to the existing severity red.
    private static Color BandRingColorFor(string? band) => band switch
    {
        "Excellent" => BrandTokens.NamingOk.Color,
        "Good" => BrandTokens.NamingOk.Color,
        "Fair" => BrandTokens.Warning.Color,
        "Action required" => BrandTokens.Error.Color,
        _ => BrandTokens.Error.Color,
    };

    private static string GlyphFor(RuleSeverity severity) => severity switch
    {
        RuleSeverity.Error => "E",
        RuleSeverity.Warning => "W",
        _ => "i",
    };
}
