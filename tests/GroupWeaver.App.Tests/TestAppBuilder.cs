using Avalonia;
using Avalonia.Headless;

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
/// </summary>
public sealed class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UseSkia()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}
