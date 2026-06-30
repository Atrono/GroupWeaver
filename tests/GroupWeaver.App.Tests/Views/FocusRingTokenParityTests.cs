using System.Linq;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;

using Xunit;

namespace GroupWeaver.App.Tests.Views;

/// <summary>
/// ADR-033 THEME PARITY (task item 2): the keyboard-focus-visible ring colour can never drift off
/// the accent ink. The ring is now a custom accent <c>FocusAdorner</c> (a 2px <see cref="Border"/>
/// stroked <c>{DynamicResource AccentTextBrush}</c>) that REPLACES the Fluent default — the
/// superseded <c>FocusRingShadow</c> <see cref="BoxShadows"/> token was removed, so
/// <c>AccentTextBrush</c> is the single source of the ring colour now. This test pins parity at TWO
/// levels per theme variant:
///
/// <list type="number">
/// <item>the <c>AccentTextBrush</c> resource resolves to the exact pinned hex (dark <c>#A99BFF</c>,
///   light <c>#4A3CC8</c>) — the literal accent-ink values the WCAG ratios (ADR-021/026) were
///   measured against; and</item>
/// <item>the REALIZED <c>FocusAdorner</c> <see cref="Border"/> (the thing that actually paints) strokes
///   in EXACTLY that <c>AccentTextBrush</c> ink — they cannot drift apart, because the adorner template
///   binds the same <c>DynamicResource</c>.</item>
/// </list>
///
/// <para>The resource lookup is per-variant via <c>TryFindResource(key, ThemeVariant, out value)</c> on
/// the live <see cref="Application"/> — it reads the <c>ThemeDictionaries</c> for the requested variant
/// directly, so NO <c>Application.RequestedThemeVariant</c> flip is needed (that is shared global app
/// state, flaky under the parallel headless session — see the ShellThemeTests note). The realized-Border
/// arm requires the live default variant (the brushes a focused control actually resolves), so it pins
/// the DARK variant (the app default, <c>RequestedThemeVariant="Dark"</c>) — the light arm of (1)/(2)
/// proves the per-variant resource parity that the dark realized Border then anchors.</para>
/// </summary>
public sealed class FocusRingTokenParityTests
{
    private static readonly Color DarkAccentInk = Color.Parse("#A99BFF");
    private static readonly Color LightAccentInk = Color.Parse("#4A3CC8");

    /// <summary>(1)+(2a) Per-variant: the <c>AccentTextBrush</c> resource is the pinned accent hex in
    /// BOTH themes — the single ring-colour source after <c>FocusRingShadow</c> was removed.</summary>
    [AvaloniaTheory]
    [InlineData("Dark")]
    [InlineData("Light")]
    public void AccentTextBrush_IsPinnedAccentInk_PerVariant(string variantName)
    {
        var variant = variantName == "Light" ? ThemeVariant.Light : ThemeVariant.Dark;
        var expectedInk = variant == ThemeVariant.Light ? LightAccentInk : DarkAccentInk;

        Assert.Equal(expectedInk, ResolveAccentTextColour(variant));
    }

    /// <summary>(2b) PARITY at the realized layer: the actual <c>FocusAdorner</c> <see cref="Border"/>
    /// painted under keyboard focus strokes in EXACTLY the <c>AccentTextBrush</c> ink — proving the
    /// ring colour and the token cannot drift apart (the adorner template binds the same DynamicResource).
    /// Asserted in the live default (Dark) variant, where the focused control resolves the dark accent
    /// ink (<c>#A99BFF</c>); the per-variant <see cref="AccentTextBrush_IsPinnedAccentInk_PerVariant"/>
    /// covers the light-side resource parity the same template re-resolves on a theme flip.</summary>
    [AvaloniaFact]
    public void RealizedFocusAdornerBorder_StrokesInAccentTextInk()
    {
        var darkAccent = ResolveAccentTextColour(ThemeVariant.Dark);
        Assert.Equal(DarkAccentInk, darkAccent); // app default is Dark (RequestedThemeVariant="Dark")

        var button = new Button { Content = "Action" };
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

        Assert.True(button.Focus(NavigationMethod.Tab));
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();

        var ring = RealizedFocusAdornerBorder(window);
        Assert.True(
            ring.BorderBrush is ISolidColorBrush,
            "the realized FocusAdorner ring must be a solid-colour border (AccentTextBrush)");
        Assert.Equal(darkAccent, ((ISolidColorBrush)ring.BorderBrush!).Color);

        window.Close();
    }

    /// <summary>The single <see cref="Border"/> realized into the window's <see cref="AdornerLayer"/>
    /// by the keyboard-focus <c>FocusAdorner</c> — the thing that actually paints the ring.</summary>
    private static Border RealizedFocusAdornerBorder(Window window) =>
        Assert.Single(
            window.GetVisualDescendants()
                .OfType<AdornerLayer>()
                .SelectMany(layer => layer.Children.OfType<Border>()));

    private static Color ResolveAccentTextColour(ThemeVariant variant) =>
        Resolve<ISolidColorBrush>("AccentTextBrush", variant).Color;

    /// <summary>Resolves a token by key for a specific <see cref="ThemeVariant"/> off the live
    /// <see cref="Application"/> (an <see cref="IResourceHost"/>) without touching the global
    /// requested variant — the deterministic, parallel-safe lookup.</summary>
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
