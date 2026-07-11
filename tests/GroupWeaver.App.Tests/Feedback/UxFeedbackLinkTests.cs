using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

using Avalonia.Styling;

using GroupWeaver.App.Feedback;

using Xunit;

namespace GroupWeaver.App.Tests.Feedback;

/// <summary>
/// Pins <see cref="UxFeedbackLink.BuildUrl"/> (feat/feedback-intake): the keyboard-help
/// footer's "Report an issue…" prefill URL for the repo's <c>ux_feedback.yml</c> issue form.
///
/// <para>Two contracts matter here. SECURITY: the URL always starts with the constant
/// GitHub new-issue base — every variable part is a query VALUE derived from app state and
/// passed through <see cref="Uri.EscapeDataString"/>, so nothing user-controlled can ever
/// alter the target host/path. CORRECTNESS: GitHub silently ignores a dropdown prefill that
/// is not byte-identical to one of the form's options, so the decoded <c>mode</c>/<c>theme</c>
/// values must match the template's option strings exactly — the drift pin below reads
/// <c>.github/ISSUE_TEMPLATE/ux_feedback.yml</c> itself (the <c>ExampleRulesetTests</c>
/// repo-file drift-pin idiom), so renaming an option in the template without updating
/// <see cref="UxFeedbackLink"/> fails here, not silently in the browser.</para>
/// </summary>
public sealed class UxFeedbackLinkTests
{
    /// <summary>The constant new-issue endpoint — the security pin's expected prefix. Spelled
    /// out as a literal (not read from the SUT) so a change to the target host/path in
    /// <c>UxFeedbackLink</c> can never pass unnoticed.</summary>
    private const string NewIssueBase = "https://github.com/Atrono/GroupWeaver/issues/new";

    // The ux_feedback.yml dropdown option strings BuildUrl must emit byte-identically
    // (mirrored from .github/ISSUE_TEMPLATE/ux_feedback.yml; the drift pin below proves it).
    private const string ModeDemo = "Demo mode (--demo)";
    private const string ModeLiveAd = "Live AD";
    private const string ThemeDark = "Dark";
    private const string ThemeLight = "Light";
    private const string ThemeBothNotSure = "Both / not sure";

    // --- (e) constant base: no user-controlled URL parts -------------------------------

    [Fact]
    public void TemplateOnlyUrl_IsTheConstantBase_PlusTemplateParameterOnly()
    {
        Assert.Equal(NewIssueBase + "?template=ux_feedback.yml", UxFeedbackLink.TemplateOnlyUrl);
    }

    [Theory]
    [InlineData("0.4.4+abc123", true)]
    [InlineData("unknown", false)]
    [InlineData("../../evil?host=attacker.example", true)] // hostile version string stays a VALUE
    public void BuildUrl_AlwaysStartsWithTheConstantNewIssueBase(string version, bool isDemo)
    {
        var url = UxFeedbackLink.BuildUrl(version, isDemo, ThemeVariant.Dark);

        Assert.StartsWith(NewIssueBase + "?template=ux_feedback.yml&", url, StringComparison.Ordinal);

        // No second URL can be smuggled in: everything after the base is exactly the four
        // expected query parameters, in order.
        var query = ParseQuery(url);
        Assert.Equal(["template", "version", "mode", "theme"], query.Select(pair => pair.Key));
    }

    // --- (a) every query value is URL-encoded ------------------------------------------

    [Fact]
    public void BuildUrl_UrlEncodesEveryQueryValue_ParensAndSpacesIncluded()
    {
        var url = UxFeedbackLink.BuildUrl("0.4.4+abc123", isDemoMode: true, theme: null);
        var query = ParseQuery(url);

        // The raw (still-encoded) values: EscapeDataString output, pinned literally for the
        // two values with characters worth worrying about — "(" / ")" / " " in the mode
        // option, " / " in the theme fallback option.
        Assert.Equal("Demo%20mode%20%28--demo%29", RawValue(query, "mode"));
        Assert.Equal("Both%20%2F%20not%20sure", RawValue(query, "theme"));

        // And no encoded value anywhere leaks a raw URL-structural or space character.
        foreach (var (key, rawValue) in query)
        {
            foreach (var forbidden in new[] { ' ', '(', ')', '/', '?', '&', '=', '#' })
            {
                Assert.False(
                    rawValue.Contains(forbidden),
                    $"query value '{key}={rawValue}' contains un-encoded '{forbidden}'");
            }
        }
    }

    // --- (b) decoded dropdown values are byte-identical to the form's options ----------

    [Theory]
    [InlineData(true, ModeDemo)]
    [InlineData(false, ModeLiveAd)]
    public void BuildUrl_ModeDecodesToTheExactTemplateOption(bool isDemoMode, string expected)
    {
        var url = UxFeedbackLink.BuildUrl("0.4.4+abc123", isDemoMode, ThemeVariant.Dark);

        Assert.Equal(expected, Uri.UnescapeDataString(RawValue(ParseQuery(url), "mode")));
    }

    [Fact]
    public void Template_ListsExactlyTheDropdownOptions_BuildUrlEmits()
    {
        // Drift pin against the form itself (the ExampleRulesetTests repo-file idiom): every
        // option literal BuildUrl can emit must appear VERBATIM as a dropdown option line in
        // .github/ISSUE_TEMPLATE/ux_feedback.yml — GitHub ignores prefills that are not
        // byte-identical to an option, so a template rename must fail this test.
        var optionLines = ReadTemplateLines()
            .Where(line => line.StartsWith("- ", StringComparison.Ordinal))
            .Select(line => line["- ".Length..])
            .ToList();

        foreach (var option in new[] { ModeDemo, ModeLiveAd, ThemeDark, ThemeLight, ThemeBothNotSure })
        {
            Assert.Contains(option, optionLines);
        }
    }

    [Fact]
    public void Template_DeclaresEveryFieldId_BuildUrlPrefillsAgainst()
    {
        // The KEY side of the same drift pin: GitHub's issue-form prefill query parameters
        // are keyed by the form's FIELD IDS, and an unknown key is silently ignored — so
        // every query key BuildUrl emits (except the "template" selector itself, which names
        // the form file rather than a field) must appear as an `id:` line in ux_feedback.yml.
        // Renaming a field id in the template must fail this test, not silently drop the
        // prefill in the browser.
        var fieldIds = ReadTemplateLines()
            .Where(line => line.StartsWith("id: ", StringComparison.Ordinal))
            .Select(line => line["id: ".Length..].Trim())
            .ToList();

        var prefillKeys = ParseQuery(
                UxFeedbackLink.BuildUrl("0.4.4+abc123", isDemoMode: true, theme: ThemeVariant.Dark))
            .Select(pair => pair.Key)
            .Where(key => key != "template")
            .ToList();

        Assert.Equal(["version", "mode", "theme"], prefillKeys); // the pinned prefill surface
        foreach (var key in prefillKeys)
        {
            Assert.Contains(key, fieldIds);
        }
    }

    // --- (c) version normalization ------------------------------------------------------

    [Theory]
    [InlineData("0.4.4+abc123", "v0.4.4")] // +metadata stripped, release-tag "v" prefixed
    [InlineData("unknown", "unknown")] // the fallback stays honest — never "vunknown"
    public void BuildUrl_NormalizesTheInformationalVersion(string informationalVersion, string expected)
    {
        var url = UxFeedbackLink.BuildUrl(informationalVersion, isDemoMode: true, theme: ThemeVariant.Dark);

        Assert.Equal(expected, Uri.UnescapeDataString(RawValue(ParseQuery(url), "version")));
    }

    // --- (d) theme mapping: concrete variants map, everything else is "don't know" ------

    [Fact]
    public void BuildUrl_MapsConcreteThemeVariantsToTheirOptions()
    {
        Assert.Equal(ThemeDark, DecodedTheme(ThemeVariant.Dark));
        Assert.Equal(ThemeLight, DecodedTheme(ThemeVariant.Light));
    }

    [Fact]
    public void BuildUrl_NullOrNonConcreteTheme_FallsBackToBothNotSure()
    {
        Assert.Equal(ThemeBothNotSure, DecodedTheme(null)); // headless/no-app caller
        Assert.Equal(ThemeBothNotSure, DecodedTheme(ThemeVariant.Default)); // not a concrete Light/Dark
    }

    // --- helpers -------------------------------------------------------------------------

    private static string DecodedTheme(ThemeVariant? theme)
    {
        var url = UxFeedbackLink.BuildUrl("0.4.4+abc123", isDemoMode: true, theme: theme);
        return Uri.UnescapeDataString(RawValue(ParseQuery(url), "theme"));
    }

    /// <summary>Splits the query into ordered (key, raw-encoded-value) pairs — deliberately NOT
    /// decoding, so encoding assertions see the wire form.</summary>
    private static List<KeyValuePair<string, string>> ParseQuery(string url)
    {
        var questionMark = url.IndexOf('?', StringComparison.Ordinal);
        Assert.True(questionMark >= 0, $"URL has no query: {url}");

        return url[(questionMark + 1)..]
            .Split('&')
            .Select(pair => pair.Split('=', 2))
            .Select(pair => new KeyValuePair<string, string>(
                pair[0],
                pair.Length == 2 ? pair[1] : string.Empty))
            .ToList();
    }

    /// <summary>The single raw value for <paramref name="key"/> — duplicate parameters would be
    /// a bug (GitHub would prefill from an unpredictable one), so Single, not First.</summary>
    private static string RawValue(List<KeyValuePair<string, string>> query, string key)
        => query.Single(pair => string.Equals(pair.Key, key, StringComparison.Ordinal)).Value;

    /// <summary>The issue-form template's lines, trimmed — the shared source for both drift
    /// pins (dropdown option VALUES and field-id KEYS).</summary>
    private static List<string> ReadTemplateLines()
    {
        var path = Path.Combine(FindRepoRoot(), ".github", "ISSUE_TEMPLATE", "ux_feedback.yml");
        Assert.True(File.Exists(path), $"issue-form template missing: {path}");

        return File.ReadAllLines(path).Select(line => line.Trim()).ToList();
    }

    // Same repo-root resolution as ExampleRulesetTests / the screenshot fixtures: ascend from
    // the test output directory until the solution file appears.
    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "GroupWeaver.sln")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException(
            "GroupWeaver.sln not found in any parent of the test output directory.");
    }
}
