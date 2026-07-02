using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;

using GroupWeaver.App.Views;

using Xunit;

namespace GroupWeaver.App.Tests.Views;

/// <summary>
/// ADR-036 D2 THEME PARITY (issue #236): the destructive-tier token pair can never drift off the
/// pinned accessible reds. <c>DestructiveTextBrush</c> is BOTH the ink and the 1px hairline of
/// <c>Button.destructive</c> — dark <c>#FF8A8E</c> / light <c>#A4262C</c>, the WP6a
/// validation-error hues re-declared as a SEPARATE role (deliberately not a shared token — a
/// future error-band retone must not silently drag button contrast). <c>DestructiveSoftBrush</c>
/// is the hover/pressed wash — the severity red <c>#D13438</c> at the accent-soft alphas, dark
/// <c>#29D13438</c> / light <c>#1FD13438</c>. These literal values are what the
/// <c>tools/check-contrast.ps1</c> destructive rows re-measure (ADR-036 D2 table: dark ink
/// 7.29:1 page / 5.77:1 card / 5.26:1 hover wash; light 6.25:1 / 5.75:1 / 4.86:1 — every state
/// clears the 4.5:1 text floor, and the border is the same opaque ink so 3:1 non-text follows
/// by construction), so this test pins exactly the hexes those ratios were computed against.
///
/// <para>The idiom is <see cref="FocusRingTokenParityTests"/>' — two levels per variant:</para>
///
/// <list type="number">
/// <item>per-variant resource parity via <c>TryFindResource(key, ThemeVariant, out value)</c> on
///   the live <see cref="Application"/> — reads the requested variant's
///   <c>ThemeDictionaries</c> directly, so NO global <c>RequestedThemeVariant</c> flip is
///   needed (shared app state, flaky under the parallel headless session); each hex is compared
///   against its <c>BrandTokens</c> const, the single declared source of truth
///   (ADR-021);</item>
/// <item>a REALIZED arm in the live default (Dark) variant: an actual
///   <c>Button.destructive</c> shown in a window paints its <c>BorderBrush</c> AND
///   <c>Foreground</c> in exactly the dark destructive ink and keeps the ghost-mirroring 1px
///   hairline (ADR-036 D1 layout-stability geometry) — token and style block cannot drift
///   apart, because the style binds the same <c>DynamicResource</c>.</item>
/// </list>
/// </summary>
public sealed class DestructiveButtonTokenParityTests
{
    /// <summary>(1a) Per-variant: <c>DestructiveTextBrush</c> — the ink AND border role — is the
    /// pinned accessible red in BOTH themes (ADR-036 D2: dark #FF8A8E, light #A4262C).</summary>
    [AvaloniaTheory]
    [InlineData("Dark")]
    [InlineData("Light")]
    public void DestructiveTextBrush_IsPinnedInk_PerVariant(string variantName)
    {
        var variant = variantName == "Light" ? ThemeVariant.Light : ThemeVariant.Dark;
        var expectedInk = Color.Parse(variant == ThemeVariant.Light
            ? BrandTokens.DestructiveTextLightHex
            : BrandTokens.DestructiveTextHex);

        Assert.Equal(expectedInk, Resolve<ISolidColorBrush>("DestructiveTextBrush", variant).Color);
    }

    /// <summary>(1b) Per-variant: <c>DestructiveSoftBrush</c> — the hover/pressed wash — is the
    /// severity red at the pinned accent-soft alphas in BOTH themes (ADR-036 D2: dark
    /// #29D13438 ≙ 16%, light #1FD13438 ≙ 12%, mirroring <c>AccentSoftBrush</c>).</summary>
    [AvaloniaTheory]
    [InlineData("Dark")]
    [InlineData("Light")]
    public void DestructiveSoftBrush_IsPinnedWash_PerVariant(string variantName)
    {
        var variant = variantName == "Light" ? ThemeVariant.Light : ThemeVariant.Dark;
        var expectedWash = Color.Parse(variant == ThemeVariant.Light
            ? BrandTokens.DestructiveSoftLightHex
            : BrandTokens.DestructiveSoftHex);

        Assert.Equal(expectedWash, Resolve<ISolidColorBrush>("DestructiveSoftBrush", variant).Color);
    }

    /// <summary>(2) PARITY at the realized layer: an actual <c>Button.destructive</c> paints its
    /// hairline (<c>BorderBrush</c>) AND its label ink (<c>Foreground</c>) in EXACTLY the
    /// destructive ink — the D2 check-contrast rows measured THIS ink on page/card/wash — and
    /// keeps <c>BorderThickness</c> 1 (the D1 ghost-mirroring geometry, so every
    /// ghost→destructive swap is layout-stable). Asserted in the live default (Dark) variant
    /// (<c>RequestedThemeVariant="Dark"</c>); the per-variant theories above cover the light-side
    /// resource parity the same style re-resolves on a theme flip.</summary>
    [AvaloniaFact]
    public void RealizedDestructiveButton_InksAndStrokesInDestructiveText()
    {
        var darkInk = Color.Parse(BrandTokens.DestructiveTextHex);
        Assert.Equal(darkInk, Resolve<ISolidColorBrush>("DestructiveTextBrush", ThemeVariant.Dark).Color);

        var button = new Button { Content = "Remove", Classes = { "destructive" } };
        var window = new Window
        {
            Content = new StackPanel { Children = { button } },
            Width = 320,
            Height = 200,
        };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();

        var hairline = Assert.IsAssignableFrom<ISolidColorBrush>(button.BorderBrush);
        Assert.Equal(darkInk, hairline.Color);
        var ink = Assert.IsAssignableFrom<ISolidColorBrush>(button.Foreground);
        Assert.Equal(darkInk, ink.Color);
        Assert.Equal(new Thickness(1), button.BorderThickness);

        window.Close();
    }

    /// <summary>Resolves a token by key for a specific <see cref="ThemeVariant"/> off the live
    /// <see cref="Application"/> without touching the global requested variant — the
    /// deterministic, parallel-safe lookup (the <see cref="FocusRingTokenParityTests"/> idiom).</summary>
    private static T Resolve<T>(string key, ThemeVariant variant)
    {
        var app = Application.Current;
        Assert.NotNull(app);
        Assert.True(
            app!.TryFindResource(key, variant, out var value),
            $"resource '{key}' must resolve for the {variant} variant (Tokens.axaml ThemeDictionaries)");
        return Assert.IsAssignableFrom<T>(value);
    }
}
