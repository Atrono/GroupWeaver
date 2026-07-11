using Avalonia;
using Avalonia.Headless;
using Avalonia.Media;

using Xunit;

// One headless Avalonia session serves the whole assembly; parallel test collections
// would contend for its single dispatcher, so this assembly runs sequentially.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
[assembly: AvaloniaTestApplication(typeof(GroupWeaver.App.Tests.TestAppBuilder))]

namespace GroupWeaver.App.Tests;

/// <summary>
/// Entry point for the headless test session (ADR-003 D6): boots the real
/// <see cref="App"/> (FluentTheme, default <see cref="App.StartupOptions"/>) on the
/// headless platform with real Skia rendering (<c>UseHeadlessDrawing = false</c>) so
/// later frame captures produce actual pixels, not no-op stubs. The Avalonia.Headless
/// package version must equal the App's Avalonia core pin exactly (ADR-003 D2).
///
/// <para><b>Font determinism (ADR-041 D3):</b> text-measurement asserts
/// (<see cref="TextClippingSweepTests"/>) are only machine-independent if every glyph
/// measured comes from a KNOWN font file, never the ambient system set — this box is
/// disposable (a rebuild changes the installed font set wholesale) and the CI runner
/// image drifts on its own schedule. Two pins, covering the two font channels the app's
/// XAML can reach:
/// (1) <see cref="FontManagerOptions.DefaultFamilyName"/> points the theme's default
/// UI family (FluentTheme's <c>$Default</c>, ambient Segoe UI on Windows) at the
/// embedded OFL Selawik — Microsoft's metric companion to Segoe UI, so measurements
/// stay representative of what the shipped app renders;
/// (2) <see cref="PinEmbeddedFonts"/> overwrites the app-level <c>FontFamilyMono</c>
/// resource (Tokens.axaml: "Cascadia Mono, Consolas, …" — a SYSTEM stack whose first
/// hit varies by machine) with the embedded Cascadia Mono. The override lands in
/// <c>Application.Resources</c>' own dictionary, which resolves BEFORE its merged
/// dictionaries, so every <c>StaticResource FontFamilyMono</c> in the views picks it up.
/// <see cref="TextClippingSweepTests"/> carries canaries pinning both channels against
/// an Avalonia resolution-order change.</para>
/// </summary>
public sealed class TestAppBuilder
{
    /// <summary>The embedded default-UI family (Selawik, OFL) — see the class doc.</summary>
    internal const string EmbeddedDefaultFamily =
        "avares://GroupWeaver.App.Tests/Assets/Fonts#Selawik";

    /// <summary>The embedded mono family (Cascadia Mono, OFL) — the app's own first-choice
    /// mono, embedded so it resolves identically everywhere.</summary>
    internal const string EmbeddedMonoFamily =
        "avares://GroupWeaver.App.Tests/Assets/Fonts#Cascadia Mono";

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UseSkia()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
            .With(new FontManagerOptions
            {
                DefaultFamilyName = EmbeddedDefaultFamily,
                FontFallbacks =
                [
                    new FontFallback { FontFamily = new FontFamily(EmbeddedDefaultFamily) },
                ],
            })
            .AfterSetup(builder => PinEmbeddedFonts((App)builder.Instance!));

    /// <summary>Pin (2): the <c>FontFamilyMono</c> override. The host dictionary's own
    /// entry shadows the Tokens.axaml value merged beneath it (own-entries-before-merged
    /// lookup, canary-pinned in <see cref="TextClippingSweepTests"/>).</summary>
    private static void PinEmbeddedFonts(App app) =>
        app.Resources["FontFamilyMono"] = new FontFamily(EmbeddedMonoFamily);
}
