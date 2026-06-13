using System.Globalization;

using Avalonia.Data.Converters;
using Avalonia.Media;
using Avalonia.Media.Immutable;

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
/// <see cref="Message"/> is the regex compiler's plain-text diagnostic — meaningful
/// only for <see cref="NamingPreviewKind.PatternInvalid"/>, otherwise empty.
/// <see cref="Message"/> renders STRICTLY as plain text
/// (<c>TextBlock.Text</c> / <c>SelectableTextBlock</c>), never a format template or
/// markup surface (#45: untrusted patterns may carry control chars).
/// </summary>
public sealed record NamingPreviewVisual(
    NamingPreviewKind Kind,
    string Glyph,
    IBrush Brush,
    string Message);

/// <summary>
/// The Naming-tab live-preview chip converter (AP 3.3 / ADR-011 §4): an
/// <see cref="IMultiValueConverter"/> over <c>[Pattern, Sample]</c> (the two
/// TextBoxes) with the owning rule's <see cref="RuleSeverity"/> as the
/// <c>ConverterParameter</c> — exactly the
/// <see cref="SeverityConverters.CountForSeverity"/> parameter shape, so a
/// Violation reads in the rule's OWN severity color.
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
    /// deliberately distinct from every severity color (Ok is not a finding).</summary>
    private static readonly ImmutableSolidColorBrush OkBrush = new(Color.Parse("#2EA043"));

    /// <summary>The PatternInvalid chip's neutral/amber fill — NOT green-dominant, so it
    /// can never masquerade as the Ok success color.</summary>
    private static readonly ImmutableSolidColorBrush PatternInvalidBrush = new(Color.Parse("#B58900"));

    /// <summary>The Ok check mark — the redundant glyph beside the green fill.</summary>
    private const string OkGlyph = "✓";

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
            return new NamingPreviewVisual(NamingPreviewKind.Idle, string.Empty, PatternInvalidBrush, string.Empty);
        }

        var result = NamingPreview.Evaluate(pattern, sample);

        // The owning rule's severity drives a Violation chip's color. The tests pin it via
        // the ConverterParameter (CountForSeverity shape); the XAML MultiBinding can't bind a
        // ConverterParameter, so it passes severity as an OPTIONAL third value — read the
        // parameter first, fall back to values[2]. Both paths yield the same visual.
        var severity = parameter as RuleSeverity?
            ?? (values.Count > 2 ? values[2] as RuleSeverity? : null)
            ?? RuleSeverity.Warning;

        return result.Kind switch
        {
            NamingPreviewKind.Ok =>
                new NamingPreviewVisual(NamingPreviewKind.Ok, OkGlyph, OkBrush, string.Empty),

            NamingPreviewKind.PatternInvalid =>
                new NamingPreviewVisual(
                    NamingPreviewKind.PatternInvalid, PatternInvalidGlyph, PatternInvalidBrush, result.Message),

            // Violation — derive the glyph + brush from the ONE severity palette for the
            // owning rule's severity (the ConverterParameter), never a hardcoded color.
            _ => new NamingPreviewVisual(
                NamingPreviewKind.Violation,
                SeverityGlyph(severity, culture),
                SeverityBrush(severity, culture),
                string.Empty),
        };
    }

    private static string SeverityGlyph(RuleSeverity severity, CultureInfo culture) =>
        (string)SeverityConverters.ToGlyph.Convert(severity, typeof(string), null, culture)!;

    private static IBrush SeverityBrush(RuleSeverity severity, CultureInfo culture) =>
        (IBrush)SeverityConverters.ToBrush.Convert(severity, typeof(IBrush), null, culture)!;
}
