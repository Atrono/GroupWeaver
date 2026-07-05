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
/// <param name="StateDir">The hermetic per-scenario state root (ADR-038 D3.1,
/// <c>--state-dir</c>): when non-null, the composition root rebases EVERY user-profile
/// store (<c>UiStateStore</c>/<c>RulesetLocator</c>/<c>AuditRunStore</c>) from
/// <c>%APPDATA%</c> onto this base directory. Already resolved to a full path and
/// demo-gated (exit 64 without <c>--demo</c>) by <see cref="Program"/>; <c>null</c> =
/// the production <c>%APPDATA%</c> layout.</param>
/// <param name="E2e">The <c>--e2e</c> observation-only automation channel (ADR-038 D3.2,
/// WP6, #245): when <c>true</c>, the composition root wires an
/// <c>Automation.E2eChannel</c> over stdio. Demo-gated (exit 64 without <c>--demo</c>) by
/// <see cref="Program"/>, same style as <see cref="StateDir"/>; <c>false</c> = no channel,
/// byte-identical production behavior.</param>
public sealed record StartupOptions(
    bool Demo, IReadOnlyList<string>? Flags = null, string? StateDir = null, bool E2e = false);
