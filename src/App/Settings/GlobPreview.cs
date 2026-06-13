using GroupWeaver.Core.Rules;

namespace GroupWeaver.App.Settings;

/// <summary>
/// Thin settings-editor pass-through over the engine's own glob matcher (AP 3.3):
/// the "does this glob match?" helper next to an ignore/exception entry delegates
/// VERBATIM to <see cref="GlobMatcher.IsMatch(string,string)"/> so the preview can
/// never drift from the rule semantics (full-string anchored, case-insensitive,
/// culture-invariant, linear-time — the same compiled, memoized regex the live
/// rules use; ADR-008/ADR-009). No re-implementation, no parallel matcher.
/// </summary>
public static class GlobPreview
{
    /// <summary>Whether <paramref name="input"/> matches <paramref name="glob"/>,
    /// identical to <see cref="GlobMatcher.IsMatch(string,string)"/>.</summary>
    public static bool IsMatch(string glob, string input) => GlobMatcher.IsMatch(glob, input);
}
