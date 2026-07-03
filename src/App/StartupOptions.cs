namespace GroupWeaver.App;

/// <summary>
/// Parsed command-line flags carried from <see cref="Program"/> into <see cref="App"/>;
/// the composition root in <c>App.OnFrameworkInitializationCompleted</c> consumes them
/// (ADR-003 D7).
/// </summary>
/// <param name="Demo">Use the embedded <c>DemoProvider</c> instead of live LDAP.</param>
/// <param name="Flags">The <c>--</c>-prefixed flag NAMES that were passed — banner-only
/// (ADR-037 D6: flag names, NEVER values); <c>null</c> when constructed off <c>Main</c>
/// (headless tests).</param>
public sealed record StartupOptions(bool Demo, IReadOnlyList<string>? Flags = null);
