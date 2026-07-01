using System.Globalization;

using Avalonia.Data.Converters;
using Avalonia.Media;

using GroupWeaver.App.Settings;
using GroupWeaver.Core.Rules;

namespace GroupWeaver.App.Views;

/// <summary>
/// The Naming-tab live-preview CHIP descriptor (AP 3.3 / ADR-011 §4): a single
/// immutable visual the chip binds to so it reads as a colored glyph + caption
/// without re-deriving the verdict. <see cref="Kind"/> is the
/// <see cref="NamingPreviewKind"/> verdict (including the chip-only
/// <see cref="NamingPreviewKind.Idle"/>); <see cref="Glyph"/> is the redundant chip
/// glyph; <see cref="Brush"/> is its fill (an <see cref="ISolidColorBrush"/>);
/// <see cref="Caption"/> is the human-readable affordance the checklist evidences,
/// rendered beside the <see cref="Glyph"/> so the chip reads "✓ matches" (Ok) /
/// "✗ would be flagged ({severity})" (Violation) — the caption is "matches" (Ok, the
/// <c>✓</c> comes from the green <see cref="Glyph"/>) / "✗ would be flagged ({severity})"
/// (Violation, leading its own <c>✗</c> since the <see cref="Glyph"/> there is the
/// colorblind E/W/i letter) — empty for Idle and PatternInvalid (the latter carries its
/// diagnostic in <see cref="Message"/> instead);
/// <see cref="Message"/> is the regex compiler's plain-text diagnostic — meaningful
/// only for <see cref="NamingPreviewKind.PatternInvalid"/>, otherwise empty.
/// Both <see cref="Caption"/> and <see cref="Message"/> render STRICTLY as plain text
/// (<c>TextBlock.Text</c> / <c>SelectableTextBlock</c>), never a format template or
/// markup surface (#45: untrusted patterns may carry control chars).
/// </summary>
public sealed record NamingPreviewVisual(
    NamingPreviewKind Kind,
    string Glyph,
    IBrush Brush,
    string Caption,
    string Message);

/// <summary>
/// The Naming-tab live-preview chip converter (AP 3.3 / ADR-011 §4): an
/// <see cref="IMultiValueConverter"/> over <c>[Pattern, Sample]</c> (the two
/// TextBoxes) with the owning rule's <see cref="RuleSeverity"/> as the
/// <c>ConverterParameter</c> — the same severity-as-<c>ConverterParameter</c>
/// MultiBinding shape, so a Violation reads in the rule's OWN severity color.
///
/// <para>An EMPTY sample rests the chip (<see cref="NamingPreviewKind.Idle"/>) before
/// any evaluation — a single space is still a real candidate and is evaluated. Every
/// non-idle verdict comes from <see cref="NamingPreview.Evaluate(string,string)"/> (a
/// throwaway, never-interned <c>NonBacktracking | CultureInvariant</c> regex — ADR-009).
/// The Violation glyph and brush are DERIVED from the one
/// <see cref="SeverityConverters"/> palette for the parameter severity (never
/// hardcoded), so the chip can never drift off the sidebar/overlay palette.</para>
/// </summary>
public sealed class NamingPreviewConverter : IMultiValueConverter
{
    /// <summary>The shared instance the XAML binding seam (and the tests) reuse.</summary>
    public static readonly NamingPreviewConverter Instance = new();

    /// <summary>The Ok chip's success fill — green-dominant (G leads R and B) and
    /// deliberately distinct from every severity color (Ok is not a finding). Sourced from
    /// <see cref="BrandTokens.NamingOk"/> (ADR-021, THE palette source of truth).</summary>
    private static readonly IBrush OkBrush = BrandTokens.NamingOk;

    /// <summary>The PatternInvalid chip's neutral/amber fill — NOT green-dominant, so it
    /// can never masquerade as the Ok success color. Sourced from
    /// <see cref="BrandTokens.NamingPatternInvalid"/> (ADR-021).</summary>
    private static readonly IBrush PatternInvalidBrush = BrandTokens.NamingPatternInvalid;

    /// <summary>The Ok check mark — the redundant glyph beside the green fill.</summary>
    private const string OkGlyph = "✓";

    /// <summary>The Ok caption — "matches" (the pattern accepts the sample, no firing). The
    /// chip's <see cref="NamingPreviewVisual.Glyph"/> already paints the leading green <c>✓</c>,
    /// so the caption reads "✓ matches" in the chip without a doubled glyph (checklist row 136).</summary>
    private const string OkCaption = "matches";

    /// <summary>The PatternInvalid warning glyph (non-<c>✓</c>, distinct from the E/W/i
    /// severity letters).</summary>
    private const string PatternInvalidGlyph = "⚠";

    public object Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture)
    {
        var pattern = values.Count > 0 ? values[0] as string ?? string.Empty : string.Empty;
        var sample = values.Count > 1 ? values[1] as string ?? string.Empty : string.Empty;

        // An empty sample rests the chip — no verdict yet. A single space is a real
        // (if odd) candidate and falls through to a true evaluation.
        if (sample.Length == 0)
        {
            return new NamingPreviewVisual(
                NamingPreviewKind.Idle, string.Empty, PatternInvalidBrush, string.Empty, string.Empty);
        }

        var result = NamingPreview.Evaluate(pattern, sample);

        // The owning rule's severity drives a Violation chip's color. The tests pin it via
        // the ConverterParameter (the severity-as-ConverterParameter shape); the XAML
        // MultiBinding can't bind a ConverterParameter, so it passes severity as an OPTIONAL
        // third value — read the parameter first, fall back to values[2]. Both paths yield the
        // same visual.
        var severity = parameter as RuleSeverity?
            ?? (values.Count > 2 ? values[2] as RuleSeverity? : null)
            ?? RuleSeverity.Warning;

        return result.Kind switch
        {
            NamingPreviewKind.Ok =>
                new NamingPreviewVisual(NamingPreviewKind.Ok, OkGlyph, OkBrush, OkCaption, string.Empty),

            NamingPreviewKind.PatternInvalid =>
                new NamingPreviewVisual(
                    NamingPreviewKind.PatternInvalid, PatternInvalidGlyph, PatternInvalidBrush,
                    string.Empty, result.Message),

            // Violation — the glyph + brush DERIVED from the ONE severity palette for the
            // owning rule's severity (the ConverterParameter), never a hardcoded color. The
            // caption leads with a ✗ and spells the severity out, so the chip reads
            // "✗ would be flagged (Warning)" (checklist row 136) without decoding the color.
            _ => new NamingPreviewVisual(
                NamingPreviewKind.Violation,
                SeverityGlyph(severity, culture),
                SeverityBrush(severity, culture),
                ViolationCaption(severity),
                string.Empty),
        };
    }

    /// <summary>The Violation caption — "✗ would be flagged ({severity})" — the
    /// human-readable affordance the checklist evidences (row 136). Leads with the <c>✗</c>
    /// cross and spells the severity out, so the chip reads without decoding the brush color
    /// (the chip's <see cref="NamingPreviewVisual.Glyph"/> still carries the redundant,
    /// colorblind-safe E/W/i severity letter — parity with the sidebar palette).</summary>
    private static string ViolationCaption(RuleSeverity severity) =>
        $"✗ would be flagged ({severity})";

    private static string SeverityGlyph(RuleSeverity severity, CultureInfo culture) =>
        (string)SeverityConverters.ToGlyph.Convert(severity, typeof(string), null, culture)!;

    private static IBrush SeverityBrush(RuleSeverity severity, CultureInfo culture) =>
        (IBrush)SeverityConverters.ToBrush.Convert(severity, typeof(IBrush), null, culture)!;
}
