using System.Globalization;

using Avalonia.Media;

using GroupWeaver.App.ViewModels;
using GroupWeaver.App.Views;
using GroupWeaver.Core.Rules;

using Xunit;

namespace GroupWeaver.App.Tests.Views;

/// <summary>
/// Pins the AP 3.4 S4 severity converters (ADR-010 §1/§5): the ONE App-side palette
/// the violations sidebar glyphs read, in lock-step with the graph overlay halos and
/// the verify.mjs tripwire. <see cref="SeverityConverters.ToBrush"/> maps a
/// <see cref="RuleSeverity"/> to its pinned hex (Error #D13438 / Warning #F7A30B /
/// Info #4FA3E3); <see cref="SeverityConverters.ToGlyph"/> maps to the redundant,
/// colorblind-safe letter (E / W / i) the row shows beside the colored square.
///
/// Style mirrors the AP 2.5 <c>DetailPanelViewTests</c> parity oracle: call the
/// converter's <c>Convert</c> directly (the binding seam), never a private helper, so
/// the test fails the instant the sidebar diverges from the ADR-010 palette. These hex
/// values are LOAD-BEARING — they must equal the JS <c>SEVERITY</c> map and the
/// stylesheet <c>overlay-color</c> rules; the parity tripwire across C#↔JS is the
/// whole point of pinning them here.
/// </summary>
public sealed class SeverityConvertersTests
{
    // The pinned ADR-010 severity palette (the "Final rendering decision" table).
    private const string ErrorHex = "#D13438";
    private const string WarningHex = "#F7A30B";
    private const string InfoHex = "#4FA3E3";

    [Theory]
    [InlineData(RuleSeverity.Error, ErrorHex)]
    [InlineData(RuleSeverity.Warning, WarningHex)]
    [InlineData(RuleSeverity.Info, InfoHex)]
    public void ToBrush_MapsEachSeverity_ToItsPinnedPaletteColor(RuleSeverity severity, string hex)
    {
        var brush = Assert.IsAssignableFrom<ISolidColorBrush>(Brush(severity));
        Assert.Equal(Color.Parse(hex), brush.Color);
    }

    [Theory]
    [InlineData(RuleSeverity.Error, "E")]
    [InlineData(RuleSeverity.Warning, "W")]
    [InlineData(RuleSeverity.Info, "i")]
    public void ToGlyph_MapsEachSeverity_ToItsRedundantLetter(RuleSeverity severity, string glyph)
    {
        Assert.Equal(glyph, Glyph(severity));
    }

    /// <summary>
    /// The per-hue ON-BADGE text contract (ADR-021 / #90, WCAG 1.4.3): Error keeps WHITE
    /// (<see cref="BrandTokens.OnDarkText"/>, 4.93:1 on the red fill ✓), but Warning and
    /// Info switch to the DARK page-bg ink (<see cref="BrandTokens.OnLightText"/> #1b1f27 —
    /// white was 2.06:1 / 2.73:1 ✗ on the amber/light-blue fills). Pinned to the BrandTokens
    /// brushes (the consolidated source of truth) so the badge ink can never drift off them;
    /// the screenshot pins judge the rendered Background, this pins the structured contract.
    /// </summary>
    [Theory]
    [InlineData(RuleSeverity.Error, "#FFFFFF")]
    [InlineData(RuleSeverity.Warning, "#1b1f27")]
    [InlineData(RuleSeverity.Info, "#1b1f27")]
    public void ToTextBrush_MapsEachSeverity_ToItsPerHueInk(RuleSeverity severity, string hex)
    {
        var brush = Assert.IsAssignableFrom<ISolidColorBrush>(TextBrush(severity));
        Assert.Equal(Color.Parse(hex), brush.Color);
    }

    /// <summary>The badge ink brushes ARE the consolidated BrandTokens role tokens
    /// (ADR-021): Error → <see cref="BrandTokens.OnDarkText"/>, Warning/Info →
    /// <see cref="BrandTokens.OnLightText"/>. Compares the resolved Color values (a
    /// projection), never brush identity, so it pins the token wiring without coupling to
    /// the brush instance.</summary>
    [Theory]
    [InlineData(RuleSeverity.Error)]
    [InlineData(RuleSeverity.Warning)]
    [InlineData(RuleSeverity.Info)]
    public void ToTextBrush_IsWiredToBrandTokensRoleInk(RuleSeverity severity)
    {
        var expected = severity == RuleSeverity.Error
            ? BrandTokens.OnDarkText.Color
            : BrandTokens.OnLightText.Color;

        var brush = Assert.IsAssignableFrom<ISolidColorBrush>(TextBrush(severity));
        Assert.Equal(expected, brush.Color);
    }

    /// <summary>The glyph FILL brushes ARE the consolidated BrandTokens severity tokens
    /// (ADR-021) — Error/Warning/Info → <see cref="BrandTokens.Error"/>/<see cref="BrandTokens.Warning"/>/<see cref="BrandTokens.Info"/>.
    /// Compares resolved Color values (a projection), pinning that the token consolidation
    /// left the fill palette transparent.</summary>
    [Theory]
    [InlineData(RuleSeverity.Error)]
    [InlineData(RuleSeverity.Warning)]
    [InlineData(RuleSeverity.Info)]
    public void ToBrush_IsWiredToBrandTokensSeverityFill(RuleSeverity severity)
    {
        var expected = severity switch
        {
            RuleSeverity.Error => BrandTokens.Error.Color,
            RuleSeverity.Warning => BrandTokens.Warning.Color,
            _ => BrandTokens.Info.Color,
        };

        var brush = Assert.IsAssignableFrom<ISolidColorBrush>(Brush(severity));
        Assert.Equal(expected, brush.Color);
    }

    /// <summary>The three severities map to three distinct brushes — no palette
    /// collision (the colorblind-redundant letter exists, but the color channel must
    /// still be unambiguous on its own).</summary>
    [Fact]
    public void ToBrush_IsInjective_AcrossTheThreeSeverities()
    {
        var colors = new[] { RuleSeverity.Error, RuleSeverity.Warning, RuleSeverity.Info }
            .Select(s => Assert.IsAssignableFrom<ISolidColorBrush>(Brush(s)).Color)
            .ToArray();

        Assert.Equal(3, colors.Distinct().Count());
    }

    /// <summary>The three glyph letters are distinct (E / W / i) — the redundant
    /// channel is meaningless if two severities share a letter.</summary>
    [Fact]
    public void ToGlyph_IsInjective_AcrossTheThreeSeverities()
    {
        var glyphs = new[] { RuleSeverity.Error, RuleSeverity.Warning, RuleSeverity.Info }
            .Select(Glyph)
            .ToArray();

        Assert.Equal(3, glyphs.Distinct().Count());
    }

    // --- WP4 (#148) audit-chip converters: ChipToBrush / ChipToTextBrush / ChipToLabel ---

    /// <summary>A FINDING chip routes through the SAME severity palette as the sidebar glyph
    /// (lock-step, ADR-010): its FILL is the class's max-severity overlay color, its INK the
    /// per-hue WCAG re-tone (Error → white, Warning/Info → dark page-bg ink), and its label
    /// appends the count ("Nesting 1"). Pins all three chip converters at once for each
    /// severity, derived from the SAME BrandTokens the severity converters use — a finding
    /// chip must never diverge from the glyph palette.</summary>
    [Theory]
    [InlineData(RuleSeverity.Error, ErrorHex, "#FFFFFF")]
    [InlineData(RuleSeverity.Warning, WarningHex, "#1b1f27")]
    [InlineData(RuleSeverity.Info, InfoHex, "#1b1f27")]
    public void FindingChip_UsesTheSeverityFill_ThePerHueInk_AndAppendsItsCount(
        RuleSeverity severity, string fillHex, string inkHex)
    {
        var chip = new AuditChip("Nesting", severity, Count: 3, HasFindings: true);

        Assert.Equal(Color.Parse(fillHex), ChipBrush(chip).Color);
        Assert.Equal(Color.Parse(inkHex), ChipTextBrush(chip).Color);
        Assert.Equal("Nesting 3", ChipLabel(chip)); // a finding chip shows "Label Count"
    }

    /// <summary>The finding-chip FILL is wired to the consolidated BrandTokens severity tokens —
    /// the SAME source as <see cref="SeverityConverters.ToBrush"/>, so the chip and the glyph
    /// can never drift apart (compares resolved Color values, a projection, never brush
    /// identity).</summary>
    [Theory]
    [InlineData(RuleSeverity.Error)]
    [InlineData(RuleSeverity.Warning)]
    [InlineData(RuleSeverity.Info)]
    public void FindingChipFill_IsWiredToTheSameBrandTokensAsTheGlyph(RuleSeverity severity)
    {
        var chip = new AuditChip("Empty group", severity, Count: 1, HasFindings: true);
        var glyphFill = Assert.IsAssignableFrom<ISolidColorBrush>(Brush(severity)).Color;

        Assert.Equal(glyphFill, ChipBrush(chip).Color);
    }

    /// <summary>The PASS chip ("No findings", <c>HasFindings:false</c>): a deliberately
    /// OFF-palette success — <see cref="BrandTokens.NamingOk"/> green fill with DARK ink
    /// (<see cref="BrandTokens.OnLightText"/> #1b1f27, which clears WCAG 1.4.3 at 4.89:1 on the
    /// NamingOk green #2EA043; white ink FAILED at 3.37:1 — the same white-on-light-fill trap the
    /// ADR-021 §2 on-light-fill pattern fixed for amber/light-blue) — and its label is the bare
    /// text with NO count appended (Count 0 is meaningless on a clean DN).</summary>
    [Fact]
    public void PassChip_IsBrandTokensGreen_WithDarkInk_AndNoCountInItsLabel()
    {
        var chip = new AuditChip("No findings", RuleSeverity.Info, Count: 0, HasFindings: false);

        Assert.Equal(BrandTokens.NamingOk.Color, ChipBrush(chip).Color);       // success green, not a severity hue
        Assert.Equal(BrandTokens.OnLightText.Color, ChipTextBrush(chip).Color); // dark ink #1b1f27 (4.89:1; white failed 3.37:1)
        Assert.Equal("No findings", ChipLabel(chip));                           // no count — bare label
    }

    /// <summary>The pass chip's green FILL is OUTSIDE the severity palette — it must collide with
    /// none of Error/Warning/Info (a clean DN is not a finding; the color must not read as one).</summary>
    [Fact]
    public void PassChipFill_IsDistinctFromEverySeverityColor()
    {
        var passColor = ChipBrush(new AuditChip("No findings", RuleSeverity.Info, 0, HasFindings: false)).Color;
        var severityColors = new[] { RuleSeverity.Error, RuleSeverity.Warning, RuleSeverity.Info }
            .Select(s => Assert.IsAssignableFrom<ISolidColorBrush>(Brush(s)).Color);

        Assert.DoesNotContain(passColor, severityColors);
    }

    /// <summary>Invoke the chip FILL converter through its binding seam exactly as XAML does.</summary>
    private static ISolidColorBrush ChipBrush(AuditChip chip) =>
        Assert.IsAssignableFrom<ISolidColorBrush>(SeverityConverters.ChipToBrush.Convert(
            chip, typeof(IBrush), null, CultureInfo.InvariantCulture));

    /// <summary>Invoke the chip INK converter through its binding seam.</summary>
    private static ISolidColorBrush ChipTextBrush(AuditChip chip) =>
        Assert.IsAssignableFrom<ISolidColorBrush>(SeverityConverters.ChipToTextBrush.Convert(
            chip, typeof(IBrush), null, CultureInfo.InvariantCulture));

    /// <summary>Invoke the chip LABEL converter through its binding seam.</summary>
    private static string ChipLabel(AuditChip chip) =>
        Assert.IsType<string>(SeverityConverters.ChipToLabel.Convert(
            chip, typeof(string), null, CultureInfo.InvariantCulture));

    /// <summary>Invoke the brush converter through its binding seam exactly as XAML does.</summary>
    private static object? Brush(RuleSeverity severity) =>
        SeverityConverters.ToBrush.Convert(
            severity, typeof(IBrush), null, CultureInfo.InvariantCulture);

    /// <summary>Invoke the per-hue text-ink converter through its binding seam.</summary>
    private static object? TextBrush(RuleSeverity severity) =>
        SeverityConverters.ToTextBrush.Convert(
            severity, typeof(IBrush), null, CultureInfo.InvariantCulture);

    /// <summary>Invoke the glyph converter through its binding seam exactly as XAML does.</summary>
    private static string Glyph(RuleSeverity severity) =>
        Assert.IsType<string>(SeverityConverters.ToGlyph.Convert(
            severity, typeof(string), null, CultureInfo.InvariantCulture));
}
