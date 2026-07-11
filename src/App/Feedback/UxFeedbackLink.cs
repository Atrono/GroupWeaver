using System.Diagnostics;
using Avalonia.Styling;

namespace GroupWeaver.App.Feedback;

/// <summary>
/// Builds the keyboard-help sheet's "Report an issue" link (feedback intake): the repo's
/// <c>ux_feedback.yml</c> issue form, prefilled through GitHub's field-id-keyed query
/// parameters (<c>version</c> / <c>mode</c> / <c>theme</c> — the form's field ids). Every
/// value derives from APP STATE (assembly informational version, the shell's demo flag,
/// the applied theme variant) — never from a user-controlled string — and each is
/// <see cref="Uri.EscapeDataString"/>-encoded, so the dropdown prefills decode to an
/// EXACT option match ("Demo mode (--demo)" / "Live AD"; "Dark" / "Light" /
/// "Both / not sure") — GitHub silently ignores a dropdown prefill that is not
/// byte-identical to one of the form's options.
/// </summary>
internal static class UxFeedbackLink
{
    /// <summary>The repo's new-issue endpoint — a constant; nothing user-typed is ever
    /// concatenated onto it.</summary>
    private const string NewIssueBase = "https://github.com/Atrono/GroupWeaver/issues/new";

    /// <summary>The form with NO prefills — the fallback a bare-constructed
    /// <see cref="Views.KeyboardHelpWindow"/> links to (headless tests construct the window
    /// without a shell; the user simply fills the fields themselves).</summary>
    internal const string TemplateOnlyUrl = NewIssueBase + "?template=ux_feedback.yml";

    /// <summary>The fully prefilled form URL. <paramref name="theme"/> is the applied
    /// <c>Application.RequestedThemeVariant</c>; anything other than a concrete Light/Dark
    /// (no running app in headless callers) prefills the form's own "don't know" option.</summary>
    internal static string BuildUrl(string informationalVersion, bool isDemoMode, ThemeVariant? theme)
    {
        var mode = isDemoMode ? "Demo mode (--demo)" : "Live AD";
        var themeOption =
            theme == ThemeVariant.Light ? "Light"
            : theme == ThemeVariant.Dark ? "Dark"
            : "Both / not sure";

        return TemplateOnlyUrl
            + "&version=" + Uri.EscapeDataString(NormalizeVersion(informationalVersion))
            + "&mode=" + Uri.EscapeDataString(mode)
            + "&theme=" + Uri.EscapeDataString(themeOption);
    }

    /// <summary>Opens <paramref name="url"/> in the default browser (the WebView2-banner
    /// idiom), wrapped never-throw: a failed launch (no default browser, shell error) must
    /// never crash the app — the sheet simply stays open and nothing happens.</summary>
    internal static void OpenInBrowser(string url)
    {
        // Defense-in-depth: launch NOTHING but the constant new-issue base — makes the
        // "constant base, never a user-controlled URL" invariant structural, not review-enforced.
        if (!url.StartsWith(NewIssueBase, StringComparison.Ordinal))
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch
        {
            // Never-throw by contract: opening a browser is a best-effort convenience.
        }
    }

    /// <summary>"0.4.4+&lt;sha&gt;" (the informational version, the <c>--check</c> banner's
    /// source) → "v0.4.4": strip the +metadata, prefix the release-tag "v" — but only onto a
    /// digit-leading core, so the "unknown" fallback never becomes "vunknown".</summary>
    private static string NormalizeVersion(string informationalVersion)
    {
        var core = informationalVersion.Split('+', 2)[0].Trim();
        return core.Length > 0 && char.IsAsciiDigit(core[0]) ? "v" + core : core;
    }
}
