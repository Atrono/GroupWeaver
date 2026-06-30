namespace GroupWeaver.App.Settings;

/// <summary>
/// The app-chrome theme choice (ADR-026 extension — adds the System/auto state to the original
/// binary Dark/Light). Persisted by ENUM NAME in <see cref="UiState.Theme"/> (<c>"Dark"</c> /
/// <c>"Light"</c> / <c>"System"</c>): <see cref="Dark"/> and <see cref="Light"/> apply that variant
/// outright; <see cref="System"/> follows the OS light/dark preference (resolved through
/// <see cref="IPlatformThemeProvider"/>, re-applied live on an OS switch).
///
/// <para><see cref="Dark"/> is the dark-first default — an unparseable / legacy / missing persisted
/// name falls back here (the ADR-026 D4 never-throw load contract); the original <c>"Light"</c>
/// still round-trips.</para>
/// </summary>
public enum AppThemeChoice
{
    /// <summary>The dark-first default — applies the Dark variant regardless of the OS.</summary>
    Dark = 0,

    /// <summary>Applies the Light variant regardless of the OS.</summary>
    Light = 1,

    /// <summary>Follows the OS light/dark preference (resolved live via
    /// <see cref="IPlatformThemeProvider"/>).</summary>
    System = 2,
}
