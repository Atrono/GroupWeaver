using System.Globalization;

using Avalonia.Data.Converters;
using Avalonia.Media;

using GroupWeaver.App.Settings;
using GroupWeaver.App.Views;
using GroupWeaver.Core.Rules;

using Xunit;

namespace GroupWeaver.App.Tests.Settings;

/// <summary>
/// Pins the AP 3.3 / S4 live-preview CHIP converter (ADR-011 §4): the
/// <see cref="NamingPreviewConverter"/> is the binding seam the Naming-tab preview
/// chip reads — an <see cref="IMultiValueConverter"/> over <c>[Pattern, Sample]</c>
/// (the two TextBoxes), with the OWNING rule's <see cref="RuleSeverity"/> passed as
/// the <c>ConverterParameter</c> (so a Violation reads in the rule's own severity
/// color — exactly the <c>SeverityConverters.CountForSeverity</c> parameter shape).
/// One <c>.Convert(...)</c> call yields a single visual descriptor
/// (<c>NamingPreviewVisual</c>) carrying the verdict <see cref="NamingPreviewKind"/>,
/// the chip <c>Glyph</c>, the chip <c>Brush</c>, and the plain-text <c>Message</c> —
/// so a chip reads as a colored glyph + caption without re-deriving the verdict.
///
/// <para>The four pinned states (spec "Final editor design per section" / Naming):
/// <list type="bullet">
/// <item><b>Ok</b> — the sample matches: a green ✓ ("matches"), a distinct success
///   brush that is NOT any severity color.</item>
/// <item><b>Violation</b> — the sample does not match: the chip reads in the rule's
///   OWN severity glyph + brush, in lock-step PARITY with
///   <see cref="SeverityConverters.ToGlyph"/> / <see cref="SeverityConverters.ToBrush"/>
///   for the <c>ConverterParameter</c> severity (the load-bearing claim).</item>
/// <item><b>PatternInvalid</b> — the pattern will not compile: a neutral/amber chip
///   carrying the regex compiler's plain-text <c>Message</c> VERBATIM (#45 — never a
///   format template/markup surface; control chars survive ordinally).</item>
/// <item><b>Idle</b> — an empty sample: no verdict yet, no ✓/severity glyph, empty
///   message (the chip rests). This is the converter's affordance, NOT a
///   <c>NamingPreview.Evaluate</c> state (Evaluate has no Idle — see
///   <c>NamingPreviewTests</c>).</item>
/// </list></para>
///
/// Style mirrors the AP 3.4 <c>SeverityConvertersTests</c> / AP 2.5
/// <c>DetailPanelViewTests</c> parity oracle and the <c>AssertSeverityChipStrip</c>
/// shape: invoke the converter through its <c>.Convert(...)</c> binding seam exactly
/// as XAML does, and DERIVE every Violation expectation from the one
/// <see cref="SeverityConverters"/> palette (never a hardcoded hex), so this fails the
/// instant the chip drifts off the severity palette.
///
/// Red until <c>src/App/Views/NamingPreviewConverter.cs</c> (and its
/// <c>NamingPreviewVisual</c> descriptor with an <c>Idle</c> kind) exist.
/// </summary>
public sealed class NamingPreviewConverterTests
{
    private const string GgPattern = "^GG_.*$";
    private const string MatchingSample = "GG_x";
    private const string NonMatchingSample = "DL_x";

    // --- Ok: a green ✓, a success brush distinct from every severity color -------------

    [Fact]
    public void Convert_MatchingSample_IsOkVisual_GreenCheck()
    {
        var visual = Convert(GgPattern, MatchingSample, RuleSeverity.Error);

        Assert.Equal(NamingPreviewKind.Ok, visual.Kind);

        // The Ok chip reads as a check mark — the redundant glyph beside the green fill.
        Assert.Equal("✓", visual.Glyph); // ✓

        // A real, green-dominant success brush (G channel leads R and B) — and emphatically
        // NOT recycled from the severity palette (Ok is not a finding).
        var color = Assert.IsAssignableFrom<ISolidColorBrush>(visual.Brush).Color;
        Assert.True(
            color.G > color.R && color.G > color.B,
            $"the Ok chip brush must read green (was R={color.R} G={color.G} B={color.B})");
        foreach (var severity in AllSeverities)
        {
            Assert.NotEqual(SeverityColor(severity), color);
        }

        // Ok carries no diagnostic — Message is reserved for PatternInvalid (#45).
        Assert.True(string.IsNullOrEmpty(visual.Message));
    }

    // --- Violation: parity with the rule's OWN severity (glyph + brush) -----------------

    [Theory]
    [InlineData(RuleSeverity.Error)]
    [InlineData(RuleSeverity.Warning)]
    [InlineData(RuleSeverity.Info)]
    public void Convert_NonMatchingSample_IsViolationVisual_InTheRuleSeverityGlyphAndBrush(
        RuleSeverity severity)
    {
        var visual = Convert(GgPattern, NonMatchingSample, severity);

        Assert.Equal(NamingPreviewKind.Violation, visual.Kind);

        // The load-bearing parity: a Violation chip reads in the OWNING rule's severity,
        // glyph AND brush DERIVED from the one SeverityConverters palette (never hardcoded)
        // — passed in via the ConverterParameter, like CountForSeverity.
        Assert.Equal(SeverityGlyph(severity), visual.Glyph);
        var color = Assert.IsAssignableFrom<ISolidColorBrush>(visual.Brush).Color;
        Assert.Equal(SeverityColor(severity), color);

        // A Violation is not a compile failure — no diagnostic text (#45 surface stays empty).
        Assert.True(string.IsNullOrEmpty(visual.Message));
    }

    /// <summary>The same non-matching sample reads in a DIFFERENT chip color per severity —
    /// proof the ConverterParameter severity actually drives the brush (not a fixed
    /// "violation red"). Guards a converter that ignores the parameter.</summary>
    [Fact]
    public void Convert_Violation_TracksTheConverterParameterSeverity_NotAFixedColor()
    {
        var asError = Convert(GgPattern, NonMatchingSample, RuleSeverity.Error);
        var asWarning = Convert(GgPattern, NonMatchingSample, RuleSeverity.Warning);
        var asInfo = Convert(GgPattern, NonMatchingSample, RuleSeverity.Info);

        var colors = new[] { asError, asWarning, asInfo }
            .Select(v => Assert.IsAssignableFrom<ISolidColorBrush>(v.Brush).Color)
            .ToArray();

        Assert.Equal(3, colors.Distinct().Count());
    }

    // --- PatternInvalid: neutral/amber + the plain-text message, VERBATIM (#45) ----------

    [Fact]
    public void Convert_MalformedPattern_IsPatternInvalidVisual_WithPlainTextMessage()
    {
        // "[" is an unterminated character class — the regex ctor throws ArgumentException;
        // NamingPreview surfaces its plain-text Message, which the chip carries verbatim.
        var visual = Convert("[", "anything", RuleSeverity.Error);

        Assert.Equal(NamingPreviewKind.PatternInvalid, visual.Kind);

        // The message is the loader/compiler diagnostic, surfaced verbatim (#45: plain text,
        // never a format template) — it must be exactly what NamingPreview.Evaluate produced.
        var expected = NamingPreview.Evaluate("[", "anything").Message;
        Assert.False(string.IsNullOrWhiteSpace(expected));
        Assert.Equal(expected, visual.Message);

        // The invalid chip is neutral/amber — it must NOT masquerade as the Ok green ✓.
        Assert.NotEqual("✓", visual.Glyph);
        var color = Assert.IsAssignableFrom<ISolidColorBrush>(visual.Brush).Color;
        Assert.False(
            color.G > color.R && color.G > color.B,
            "the PatternInvalid chip must not read as the green Ok success color");
    }

    /// <summary>#45 hard pin: a compiler diagnostic carrying control chars (an untrusted
    /// community pattern can provoke one) flows to the chip's <c>Message</c> byte-for-byte —
    /// no stripping, no interpolation, no markup. The chip binds this to
    /// <c>TextBlock.Text</c>/<c>SelectableTextBlock</c>; here we pin the value the binding
    /// receives is ORDINAL-equal to what the engine produced.</summary>
    [Fact]
    public void Convert_PatternInvalidMessage_IsSurfacedOrdinallyVerbatim_NotSanitized()
    {
        var visual = Convert("(?<=x)foo", "x", RuleSeverity.Warning); // lookbehind ⇒ NotSupported

        Assert.Equal(NamingPreviewKind.PatternInvalid, visual.Kind);
        var expected = NamingPreview.Evaluate("(?<=x)foo", "x").Message;
        Assert.Equal(expected, visual.Message, StringComparer.Ordinal);
    }

    // --- Idle: an empty sample rests the chip (a converter affordance, not an Evaluate state) -

    [Fact]
    public void Convert_EmptySample_IsIdleVisual_NoVerdictGlyph_NoMessage()
    {
        // Empty sample ⇒ the chip rests: NOT Ok (no green ✓), NOT a Violation (no severity
        // glyph/brush), NOT PatternInvalid. NamingPreview.Evaluate has no Idle state — this
        // is the converter's own affordance, decided from the empty sample BEFORE evaluating.
        var visual = Convert(GgPattern, string.Empty, RuleSeverity.Error);

        Assert.Equal(NamingPreviewKind.Idle, visual.Kind);
        Assert.NotEqual("✓", visual.Glyph); // not the Ok check
        Assert.True(string.IsNullOrEmpty(visual.Message));

        // And it must not borrow the owning severity's glyph (it is not a finding).
        Assert.NotEqual(SeverityGlyph(RuleSeverity.Error), visual.Glyph);
    }

    [Fact]
    public void Convert_WhitespaceOnlySample_IsNotIdle_ASpaceIsARealCandidate()
    {
        // Idle is reserved for an EMPTY sample. A single space is a real (if odd) candidate
        // name — it must produce a true verdict, not rest the chip (guards an over-eager
        // IsNullOrWhiteSpace short-circuit that would mask a genuine Violation).
        var visual = Convert(GgPattern, " ", RuleSeverity.Error);

        Assert.NotEqual(NamingPreviewKind.Idle, visual.Kind);
        Assert.Equal(NamingPreviewKind.Violation, visual.Kind); // " " does not match ^GG_.*$
    }

    // --- the documented worked example reads Ok ("GG_Vertrieb_Lesen" vs the GG pattern) ---

    [Fact]
    public void Convert_TheUiChecklistWorkedExample_ReadsOk()
    {
        // ui-checklist B: "GG_Vertrieb_Lesen" vs the GG pattern reads ✓ matches (green).
        var visual = Convert("^GG_.*_(Lesen|Schreiben)$", "GG_Vertrieb_Lesen", RuleSeverity.Warning);

        Assert.Equal(NamingPreviewKind.Ok, visual.Kind);
        Assert.Equal("✓", visual.Glyph);
    }

    // --- helpers ------------------------------------------------------------------------

    private static readonly RuleSeverity[] AllSeverities =
        [RuleSeverity.Error, RuleSeverity.Warning, RuleSeverity.Info];

    /// <summary>Invoke the chip converter through its binding seam exactly as XAML does:
    /// values <c>[Pattern, Sample]</c>, the owning rule's severity as the parameter.</summary>
    private static NamingPreviewVisual Convert(string pattern, string sample, RuleSeverity severity) =>
        Assert.IsType<NamingPreviewVisual>(NamingPreviewConverter.Instance.Convert(
            new object?[] { pattern, sample },
            typeof(NamingPreviewVisual),
            severity,
            CultureInfo.InvariantCulture));

    /// <summary>The parity oracle: the glyph THE severity palette produces — the chip must
    /// agree with the sidebar/overlay palette for a Violation.</summary>
    private static string SeverityGlyph(RuleSeverity severity) =>
        Assert.IsType<string>(SeverityConverters.ToGlyph.Convert(
            severity, typeof(string), null, CultureInfo.InvariantCulture));

    /// <summary>The parity oracle: the pinned ADR-010 color THE severity palette produces.</summary>
    private static Color SeverityColor(RuleSeverity severity) =>
        Assert.IsAssignableFrom<ISolidColorBrush>(SeverityConverters.ToBrush.Convert(
            severity, typeof(IBrush), null, CultureInfo.InvariantCulture)).Color;
}
