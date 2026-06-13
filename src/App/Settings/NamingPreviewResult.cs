namespace GroupWeaver.App.Settings;

/// <summary>
/// The verdicts the live naming preview can surface for a pattern against a
/// candidate sample (ADR-011 §4, AP 3.3). The first three are produced by
/// <see cref="NamingPreview.Evaluate(string,string)"/>: <see cref="Ok"/> (sample
/// matches the pattern), <see cref="Violation"/> (sample does not match — it would
/// be flagged), or <see cref="PatternInvalid"/> (the pattern could not be compiled
/// under the engine's <c>NonBacktracking</c> options — a lookaround/backreference
/// or a malformed pattern). <see cref="Idle"/> is NOT an <c>Evaluate</c> result —
/// it is the preview chip's own resting affordance for an empty sample, decided by
/// <c>NamingPreviewConverter</c> before any evaluation.
/// </summary>
public enum NamingPreviewKind
{
    /// <summary>The sample matches the pattern (live, no rule firing).</summary>
    Ok,

    /// <summary>The sample does not match — the rule would flag it.</summary>
    Violation,

    /// <summary>The pattern itself is not a usable regex; see <see cref="NamingPreviewResult.Message"/>.</summary>
    PatternInvalid,

    /// <summary>The preview chip rests: an empty sample, no verdict yet. A chip-only
    /// affordance (decided before evaluating), never returned by
    /// <see cref="NamingPreview.Evaluate(string,string)"/>.</summary>
    Idle,
}

/// <summary>
/// Immutable result of a single live naming-preview evaluation (AP 3.3 / ADR-011
/// §4). <see cref="Kind"/> carries the verdict; <see cref="Message"/> is meaningful
/// only for <see cref="NamingPreviewKind.PatternInvalid"/>, where it holds the
/// regex compiler's plain-text diagnostic — rendered VERBATIM as plain text
/// (<c>TextBlock.Text</c> / <c>SelectableTextBlock</c>), never as a format
/// template or markup surface (#45: untrusted ruleset patterns may carry control
/// chars). For <see cref="NamingPreviewKind.Ok"/> / <see cref="NamingPreviewKind.Violation"/>
/// it is the empty string.
/// </summary>
public sealed record NamingPreviewResult
{
    private NamingPreviewResult(NamingPreviewKind kind, string message)
    {
        Kind = kind;
        Message = message;
    }

    /// <summary>The preview verdict.</summary>
    public NamingPreviewKind Kind { get; }

    /// <summary>The plain-text compiler diagnostic for
    /// <see cref="NamingPreviewKind.PatternInvalid"/>; otherwise the empty string.</summary>
    public string Message { get; }

    /// <summary>The sample matches the pattern.</summary>
    public static NamingPreviewResult Ok { get; } = new(NamingPreviewKind.Ok, string.Empty);

    /// <summary>The sample does not match the pattern.</summary>
    public static NamingPreviewResult Violation { get; } = new(NamingPreviewKind.Violation, string.Empty);

    /// <summary>The pattern could not be compiled; <paramref name="message"/> is the
    /// regex compiler's plain-text diagnostic (rendered verbatim, #45).</summary>
    public static NamingPreviewResult PatternInvalid(string message) =>
        new(NamingPreviewKind.PatternInvalid, message);
}
